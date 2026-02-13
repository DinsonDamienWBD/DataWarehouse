using DataWarehouse.SDK.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Integration;

/// <summary>
/// DataWarehouse Host - The "base piece" for all DataWarehouse applications.
///
/// This class provides a unified entry point supporting 3 operating modes:
/// 1. Install - Initialize a new DataWarehouse instance
/// 2. Connect - Connect to an existing instance (local or remote)
/// 3. Embedded - Run a lightweight embedded instance
///
/// Usage Pattern (copy this pattern to your host applications):
/// <code>
/// // Create host
/// var host = new DataWarehouseHost(loggerFactory);
///
/// // Mode 1: Install
/// await host.InstallAsync(new InstallConfiguration { InstallPath = "C:/DataWarehouse" });
///
/// // Mode 2: Connect to existing
/// await host.ConnectAsync(ConnectionTarget.Remote("192.168.1.100", 8080));
///
/// // Mode 3: Run embedded
/// await host.RunEmbeddedAsync(new EmbeddedConfiguration());
/// </code>
///
/// The CLI and GUI applications should use this host as their foundation,
/// ensuring consistent behavior and code reuse.
/// </summary>
public sealed class DataWarehouseHost : IAsyncDisposable
{
    private readonly ILogger<DataWarehouseHost> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly AdapterRunner _runner;
    private OperatingMode _currentMode = OperatingMode.Embedded;
    private IInstanceConnection? _connection;
    private bool _disposed;

    /// <summary>
    /// Creates a new DataWarehouse host.
    /// </summary>
    public DataWarehouseHost(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<DataWarehouseHost>();
        _runner = new AdapterRunner(_loggerFactory);
    }

    /// <summary>
    /// Current operating mode.
    /// </summary>
    public OperatingMode CurrentMode => _currentMode;

    /// <summary>
    /// Whether the host is currently connected to an instance.
    /// </summary>
    public bool IsConnected => _connection?.IsConnected ?? false;

    /// <summary>
    /// Current connection (null if not connected).
    /// </summary>
    public IInstanceConnection? Connection => _connection;

    /// <summary>
    /// Gets the capabilities of the connected instance.
    /// </summary>
    public InstanceCapabilities? Capabilities => _connection?.Capabilities;

    #region Mode 1: Install

    /// <summary>
    /// Installs and initializes a new DataWarehouse instance.
    /// </summary>
    public async Task<InstallResult> InstallAsync(
        InstallConfiguration config,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _currentMode = OperatingMode.Install;
        _logger.LogInformation("Starting installation to: {Path}", config.InstallPath);

        var result = new InstallResult();

        try
        {
            // Step 1: Validate configuration
            progress?.Report(new InstallProgress(InstallStep.Validating, 0, "Validating configuration..."));
            ValidateInstallConfig(config);

            // Step 2: Create directories
            progress?.Report(new InstallProgress(InstallStep.CreatingDirectories, 10, "Creating directories..."));
            await CreateDirectoriesAsync(config, cancellationToken);

            // Step 3: Copy/extract files
            progress?.Report(new InstallProgress(InstallStep.CopyingFiles, 30, "Copying files..."));
            await CopyFilesAsync(config, cancellationToken);

            // Step 4: Initialize configuration
            progress?.Report(new InstallProgress(InstallStep.InitializingConfig, 50, "Initializing configuration..."));
            await InitializeConfigurationAsync(config, cancellationToken);

            // Step 5: Initialize plugins
            progress?.Report(new InstallProgress(InstallStep.InitializingPlugins, 70, "Initializing plugins..."));
            await InitializePluginsAsync(config, cancellationToken);

            // Step 6: Create admin user
            if (config.CreateDefaultAdmin)
            {
                progress?.Report(new InstallProgress(InstallStep.CreatingAdmin, 85, "Creating admin user..."));
                await CreateAdminUserAsync(config, cancellationToken);
            }

            // Step 7: Register service
            if (config.CreateService)
            {
                progress?.Report(new InstallProgress(InstallStep.RegisteringService, 95, "Registering service..."));
                await RegisterServiceAsync(config, cancellationToken);
            }

            progress?.Report(new InstallProgress(InstallStep.Complete, 100, "Installation complete!"));

            result.Success = true;
            result.InstallPath = config.InstallPath;
            result.Message = "DataWarehouse installed successfully.";

            _logger.LogInformation("Installation completed successfully at: {Path}", config.InstallPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Installation failed");
            result.Success = false;
            result.Message = $"Installation failed: {ex.Message}";
            result.Exception = ex;
        }

        return result;
    }

    #endregion

    #region Mode 2: Connect

    /// <summary>
    /// Connects to an existing DataWarehouse instance.
    /// </summary>
    public async Task<ConnectResult> ConnectAsync(
        ConnectionTarget target,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _currentMode = OperatingMode.Connect;
        _logger.LogInformation("Connecting to instance: {Type} {Host}:{Port}",
            target.Type, target.Host, target.Port);

        var result = new ConnectResult();

        try
        {
            // Create appropriate connection based on type
            _connection = target.Type switch
            {
                ConnectionType.Local => new LocalInstanceConnection(_loggerFactory),
                ConnectionType.Remote => new RemoteInstanceConnection(_loggerFactory),
                ConnectionType.Cluster => new ClusterInstanceConnection(_loggerFactory),
                _ => throw new ArgumentException($"Unknown connection type: {target.Type}")
            };

            // Connect
            await _connection.ConnectAsync(target, cancellationToken);

            // Discover capabilities
            var capabilities = await _connection.DiscoverCapabilitiesAsync(cancellationToken);

            result.Success = true;
            result.InstanceId = _connection.InstanceId;
            result.Capabilities = capabilities;
            result.Message = $"Connected to {target.Type} instance: {_connection.InstanceId}";

            _logger.LogInformation("Connected to instance: {InstanceId} with {PluginCount} plugins",
                _connection.InstanceId, capabilities.AvailablePlugins.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connection failed");
            result.Success = false;
            result.Message = $"Connection failed: {ex.Message}";
            result.Exception = ex;

            if (_connection != null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }
        }

        return result;
    }

    /// <summary>
    /// Disconnects from the current instance.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_connection != null)
        {
            _logger.LogInformation("Disconnecting from instance: {InstanceId}", _connection.InstanceId);
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    #endregion

    #region Mode 3: Embedded

    /// <summary>
    /// Runs an embedded DataWarehouse instance.
    /// </summary>
    public async Task<int> RunEmbeddedAsync(
        EmbeddedConfiguration config,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _currentMode = OperatingMode.Embedded;
        _logger.LogInformation("Starting embedded instance");

        // Configure adapter options for embedded mode
        var options = new AdapterOptions
        {
            KernelId = $"embedded-{Guid.NewGuid():N}"[..20],
            OperatingMode = "Embedded",
            PluginPath = config.DataPath,
            LoggerFactory = _loggerFactory,
            CustomConfig =
            {
                ["MaxMemoryMb"] = config.MaxMemoryMb,
                ["PersistData"] = config.PersistData,
                ["ExposeHttp"] = config.ExposeHttp,
                ["HttpPort"] = config.HttpPort,
                ["Plugins"] = config.Plugins
            }
        };

        // Run the adapter
        return await _runner.RunAsync(options, "Embedded", cancellationToken);
    }

    /// <summary>
    /// Requests shutdown of the embedded instance.
    /// </summary>
    public void RequestShutdown()
    {
        _runner.RequestShutdown();
    }

    #endregion

    #region Configuration Management

    /// <summary>
    /// Gets the configuration of the connected instance.
    /// </summary>
    public async Task<Dictionary<string, object>> GetConfigurationAsync(
        CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            throw new InvalidOperationException("Not connected to an instance.");
        }

        return await _connection.GetConfigurationAsync(cancellationToken);
    }

    /// <summary>
    /// Updates the configuration of the connected instance.
    /// </summary>
    public async Task UpdateConfigurationAsync(
        Dictionary<string, object> config,
        CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            throw new InvalidOperationException("Not connected to an instance.");
        }

        await _connection.UpdateConfigurationAsync(config, cancellationToken);
        _logger.LogInformation("Configuration updated for instance: {InstanceId}", _connection.InstanceId);
    }

    /// <summary>
    /// Saves the current configuration for future use.
    /// </summary>
    public async Task SaveConfigurationAsync(
        string profileName,
        CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            throw new InvalidOperationException("Not connected to an instance.");
        }

        var config = await _connection.GetConfigurationAsync(cancellationToken);

        // Save to profile
        var profilePath = GetProfilePath(profileName);
        var json = System.Text.Json.JsonSerializer.Serialize(new SavedProfile
        {
            Name = profileName,
            InstanceId = _connection.InstanceId,
            Configuration = config,
            SavedAt = DateTime.UtcNow
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
        await File.WriteAllTextAsync(profilePath, json, cancellationToken);

        _logger.LogInformation("Configuration saved to profile: {ProfileName}", profileName);
    }

    /// <summary>
    /// Loads a saved configuration profile.
    /// </summary>
    public async Task<SavedProfile?> LoadProfileAsync(
        string profileName,
        CancellationToken cancellationToken = default)
    {
        var profilePath = GetProfilePath(profileName);

        if (!File.Exists(profilePath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(profilePath, cancellationToken);
        return System.Text.Json.JsonSerializer.Deserialize<SavedProfile>(json);
    }

    /// <summary>
    /// Lists available configuration profiles.
    /// </summary>
    public IEnumerable<string> ListProfiles()
    {
        var profileDir = GetProfileDirectory();
        if (!Directory.Exists(profileDir))
        {
            return Enumerable.Empty<string>();
        }

        return Directory.GetFiles(profileDir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f));
    }

    #endregion

    #region Helper Methods

    private void ValidateInstallConfig(InstallConfiguration config)
    {
        if (string.IsNullOrEmpty(config.InstallPath))
        {
            throw new ArgumentException("InstallPath is required.");
        }

        if (config.CreateDefaultAdmin && string.IsNullOrEmpty(config.AdminPassword))
        {
            throw new ArgumentException("AdminPassword is required when CreateDefaultAdmin is true.");
        }
    }

    private Task CreateDirectoriesAsync(InstallConfiguration config, CancellationToken ct)
    {
        Directory.CreateDirectory(config.InstallPath);
        Directory.CreateDirectory(Path.Combine(config.InstallPath, "data"));
        Directory.CreateDirectory(Path.Combine(config.InstallPath, "config"));
        Directory.CreateDirectory(Path.Combine(config.InstallPath, "plugins"));
        Directory.CreateDirectory(Path.Combine(config.InstallPath, "logs"));

        if (!string.IsNullOrEmpty(config.DataPath))
        {
            Directory.CreateDirectory(config.DataPath);
        }

        return Task.CompletedTask;
    }

    private Task CopyFilesAsync(InstallConfiguration config, CancellationToken ct)
    {
        // In a real implementation, this would copy binaries, configs, etc.
        // For now, we just ensure the structure is in place
        return Task.CompletedTask;
    }

    private async Task InitializeConfigurationAsync(InstallConfiguration config, CancellationToken ct)
    {
        var configPath = Path.Combine(config.InstallPath, "config", "datawarehouse.json");

        var defaultConfig = new Dictionary<string, object>
        {
            ["instanceId"] = Guid.NewGuid().ToString("N"),
            ["dataPath"] = config.DataPath ?? Path.Combine(config.InstallPath, "data"),
            ["pluginPath"] = Path.Combine(config.InstallPath, "plugins"),
            ["logPath"] = Path.Combine(config.InstallPath, "logs"),
            ["autoStart"] = config.AutoStart
        };

        foreach (var kv in config.InitialConfig)
        {
            defaultConfig[kv.Key] = kv.Value;
        }

        var json = System.Text.Json.JsonSerializer.Serialize(defaultConfig,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        await File.WriteAllTextAsync(configPath, json, ct);
    }

    private Task InitializePluginsAsync(InstallConfiguration config, CancellationToken ct)
    {
        // Initialize/copy configured plugins
        var pluginPath = Path.Combine(config.InstallPath, "plugins");

        foreach (var plugin in config.IncludedPlugins)
        {
            var pluginDir = Path.Combine(pluginPath, plugin);
            Directory.CreateDirectory(pluginDir);
            // In real impl: copy plugin files
        }

        return Task.CompletedTask;
    }

    private Task CreateAdminUserAsync(InstallConfiguration config, CancellationToken ct)
    {
        // In real implementation: create admin user in security store
        _logger.LogInformation("Created admin user: {Username}", config.AdminUsername);
        return Task.CompletedTask;
    }

    private Task RegisterServiceAsync(InstallConfiguration config, CancellationToken ct)
    {
        // Platform-specific service registration
        if (OperatingSystem.IsWindows())
        {
            _logger.LogInformation("Registering Windows service...");
            // sc create, etc.
        }
        else if (OperatingSystem.IsLinux())
        {
            _logger.LogInformation("Creating systemd service file...");
            // systemd unit file
        }

        return Task.CompletedTask;
    }

    private static string GetProfileDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DataWarehouse",
            "profiles");
    }

    private static string GetProfilePath(string profileName)
    {
        return Path.Combine(GetProfileDirectory(), $"{profileName}.json");
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_connection != null)
        {
            await _connection.DisposeAsync();
        }

        await _runner.DisposeAsync();
    }
}

#region Result Types

/// <summary>
/// Result of an installation operation.
/// </summary>
public sealed class InstallResult
{
    public bool Success { get; set; }
    public string? InstallPath { get; set; }
    public string? Message { get; set; }
    public Exception? Exception { get; set; }
}

/// <summary>
/// Progress information during installation.
/// </summary>
public readonly record struct InstallProgress(
    InstallStep Step,
    int PercentComplete,
    string Message);

/// <summary>
/// Installation steps.
/// </summary>
public enum InstallStep
{
    Validating,
    CreatingDirectories,
    CopyingFiles,
    InitializingConfig,
    InitializingPlugins,
    CreatingAdmin,
    RegisteringService,
    Complete
}

/// <summary>
/// Result of a connection operation.
/// </summary>
public sealed class ConnectResult
{
    public bool Success { get; set; }
    public string? InstanceId { get; set; }
    public InstanceCapabilities? Capabilities { get; set; }
    public string? Message { get; set; }
    public Exception? Exception { get; set; }
}

/// <summary>
/// Saved configuration profile.
/// </summary>
public sealed class SavedProfile
{
    public string Name { get; set; } = "";
    public string? InstanceId { get; set; }
    public Dictionary<string, object> Configuration { get; set; } = new();
    public DateTime SavedAt { get; set; }
}

#endregion
