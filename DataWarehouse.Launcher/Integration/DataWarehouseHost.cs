using DataWarehouse.SDK.Hosting;
using DataWarehouse.Shared.Commands;
using DataWarehouse.Shared.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Launcher.Integration;

/// <summary>
/// DataWarehouse Host - The "base piece" for all DataWarehouse applications.
///
/// This class provides a unified entry point supporting 4 operating modes:
/// 1. Install - Initialize a new DataWarehouse instance
/// 2. Connect - Connect to an existing instance (local or remote)
/// 3. Embedded - Run a lightweight embedded instance
/// 4. Service - Run as Windows service or Linux daemon
/// </summary>
public sealed class DataWarehouseHost : IAsyncDisposable, IServerHost
{
    private readonly ILogger<DataWarehouseHost> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly AdapterRunner _runner;
    private OperatingMode _currentMode = OperatingMode.Embedded;
    private IInstanceConnection? _connection;
    private bool _disposed;
    private CancellationTokenSource? _embeddedCts;
    private Task? _embeddedTask;
    private ServerHostStatus? _status;

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

    #region IServerHost Implementation

    /// <inheritdoc />
    bool IServerHost.IsRunning => _embeddedTask != null && !_embeddedTask.IsCompleted;

    /// <inheritdoc />
    ServerHostStatus? IServerHost.Status => _status;

    /// <inheritdoc />
    async Task IServerHost.StartAsync(EmbeddedConfiguration config, CancellationToken cancellationToken)
    {
        _embeddedCts = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _embeddedCts.Token);

        _status = new ServerHostStatus
        {
            Port = config.HttpPort,
            Mode = "Embedded",
            ProcessId = Environment.ProcessId,
            StartTime = DateTime.UtcNow,
            DataPath = config.DataPath,
            PersistData = config.PersistData
        };

        _embeddedTask = Task.Run(async () =>
        {
            await RunEmbeddedAsync(config, linkedCts.Token);
        }, linkedCts.Token);

        // Wait briefly for startup
        await Task.Delay(500, cancellationToken);
    }

    /// <inheritdoc />
    async Task IServerHost.StopAsync(CancellationToken cancellationToken)
    {
        _embeddedCts?.Cancel();
        if (_embeddedTask != null)
        {
            try
            {
                await _embeddedTask.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Embedded instance did not stop within timeout");
            }
            catch (OperationCanceledException)
            {
                /* Cancellation is expected during shutdown */
            }
        }
        _embeddedTask = null;
        _embeddedCts?.Dispose();
        _embeddedCts = null;
        _status = null;
    }

    /// <inheritdoc />
    async Task<ServerInstallResult> IServerHost.InstallAsync(
        InstallConfiguration config,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var result = await InstallAsync(config, new Progress<InstallProgress>(p =>
            progress?.Report(p.Message)), cancellationToken);

        return new ServerInstallResult
        {
            Success = result.Success,
            InstallPath = result.InstallPath,
            Message = result.Message
        };
    }

    #endregion

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
            _connection = target.Type switch
            {
                ConnectionType.Local => new LocalInstanceConnection(_loggerFactory),
                ConnectionType.Remote => new RemoteInstanceConnection(_loggerFactory),
                ConnectionType.Cluster => new ClusterInstanceConnection(_loggerFactory),
                _ => throw new ArgumentException($"Unknown connection type: {target.Type}")
            };

            await _connection.ConnectAsync(target, cancellationToken);
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

    #region Mode 4: Service

    /// <summary>
    /// Runs as a Windows service or Linux daemon.
    /// </summary>
    public async Task<int> RunServiceAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _currentMode = OperatingMode.Service;
        _logger.LogInformation("Starting as service/daemon");

        var options = new AdapterOptions
        {
            KernelId = $"service-{Environment.MachineName.ToLowerInvariant()}",
            OperatingMode = "Server",
            LoggerFactory = _loggerFactory,
            CustomConfig =
            {
                ["RunAsService"] = true
            }
        };

        return await _runner.RunAsync(options, "DataWarehouse", cancellationToken);
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
            throw new InvalidOperationException("Not connected to an instance.");

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
            throw new InvalidOperationException("Not connected to an instance.");

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
            throw new InvalidOperationException("Not connected to an instance.");

        var config = await _connection.GetConfigurationAsync(cancellationToken);
        var profilePath = GetProfilePath(profileName);
        var json = JsonSerializer.Serialize(new SavedProfile
        {
            Name = profileName,
            InstanceId = _connection.InstanceId,
            Configuration = config,
            SavedAt = DateTime.UtcNow
        }, new JsonSerializerOptions { WriteIndented = true });

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
            return null;

        var json = await File.ReadAllTextAsync(profilePath, cancellationToken);
        return JsonSerializer.Deserialize<SavedProfile>(json);
    }

    /// <summary>
    /// Lists available configuration profiles.
    /// </summary>
    public IEnumerable<string> ListProfiles()
    {
        var profileDir = GetProfileDirectory();
        if (!Directory.Exists(profileDir))
            return Enumerable.Empty<string>();

        return Directory.GetFiles(profileDir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f));
    }

    #endregion

    #region Helper Methods

    private void ValidateInstallConfig(InstallConfiguration config)
    {
        if (string.IsNullOrEmpty(config.InstallPath))
            throw new ArgumentException("InstallPath is required.");

        if (config.CreateDefaultAdmin && string.IsNullOrEmpty(config.AdminPassword))
            throw new ArgumentException("AdminPassword is required when CreateDefaultAdmin is true.");
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

    /// <summary>
    /// Copies actual binaries, DLLs, and configs from the source directory to the install path.
    /// </summary>
    private async Task CopyFilesAsync(InstallConfiguration config, CancellationToken ct)
    {
        var sourceDir = AppContext.BaseDirectory;
        var targetDir = config.InstallPath;

        _logger.LogInformation("Copying files from {Source} to {Target}", sourceDir, targetDir);

        var filesCopied = new int[1]; // Array wrapper to allow mutation in async methods
        var extensions = new[] { ".exe", ".dll", ".json", ".pdb", ".runtimeconfig.json", ".deps.json" };
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "obj", "bin", ".git" };

        await CopyDirectoryAsync(sourceDir, targetDir, extensions, skipDirs, filesCopied, ct);

        // Copy appsettings files to config subdirectory
        var configDir = Path.Combine(targetDir, "config");
        foreach (var file in Directory.GetFiles(sourceDir, "appsettings*.json"))
        {
            var destFile = Path.Combine(configDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
            filesCopied[0]++;
        }

        _logger.LogInformation("Copied {Count} files to {Target}", filesCopied[0], targetDir);
    }

    private async Task CopyDirectoryAsync(
        string sourceDir, string targetDir,
        string[] extensions, HashSet<string> skipDirs,
        int[] filesCopied, CancellationToken ct)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(file);
            if (extensions.Any(e => ext.Equals(e, StringComparison.OrdinalIgnoreCase)))
            {
                var destFile = Path.Combine(targetDir, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
                filesCopied[0]++;
                _logger.LogDebug("Copied: {File}", Path.GetFileName(file));
            }
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(dir);
            if (!skipDirs.Contains(dirName))
            {
                var destDir = Path.Combine(targetDir, dirName);
                await CopyDirectoryAsync(dir, destDir, extensions, skipDirs, filesCopied, ct);
            }
        }
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

        var json = JsonSerializer.Serialize(defaultConfig,
            new JsonSerializerOptions { WriteIndented = true });

        await File.WriteAllTextAsync(configPath, json, ct);
    }

    /// <summary>
    /// Copies plugin DLLs from the source plugins directory to the target plugins directory.
    /// </summary>
    private Task InitializePluginsAsync(InstallConfiguration config, CancellationToken ct)
    {
        var pluginPath = Path.Combine(config.InstallPath, "plugins");
        var sourcePluginPath = Path.Combine(AppContext.BaseDirectory, "plugins");

        var initialized = 0;
        var skipped = 0;

        foreach (var plugin in config.IncludedPlugins)
        {
            ct.ThrowIfCancellationRequested();
            var pluginDir = Path.Combine(pluginPath, plugin);
            Directory.CreateDirectory(pluginDir);

            // Search for plugin DLLs in source
            if (Directory.Exists(sourcePluginPath))
            {
                var sourcePluginDir = Path.Combine(sourcePluginPath, plugin);
                if (Directory.Exists(sourcePluginDir))
                {
                    foreach (var file in Directory.GetFiles(sourcePluginDir, "*.*", SearchOption.AllDirectories))
                    {
                        var relativePath = Path.GetRelativePath(sourcePluginDir, file);
                        var destFile = Path.Combine(pluginDir, relativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                        File.Copy(file, destFile, overwrite: true);
                    }
                    initialized++;
                }
                else
                {
                    _logger.LogWarning("Plugin '{Plugin}' not found in source plugins directory", plugin);
                    skipped++;
                }
            }
            else
            {
                skipped++;
            }
        }

        _logger.LogInformation("Initialized {Initialized} plugins, {Skipped} skipped (not found in source)",
            initialized, skipped);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates a real admin user with hashed password written to security.json.
    /// </summary>
    private async Task CreateAdminUserAsync(InstallConfiguration config, CancellationToken ct)
    {
        var securityPath = Path.Combine(config.InstallPath, "config", "security.json");

        // Validate admin password is configured
        if (string.IsNullOrWhiteSpace(config.AdminPassword))
        {
            throw new ArgumentException("AdminPassword must be configured and cannot be empty.", nameof(config));
        }

        // Generate salt and hash password using PBKDF2
        var salt = new byte[32];
        RandomNumberGenerator.Fill(salt);
        var saltBase64 = Convert.ToBase64String(salt);

        var passwordBytes = Encoding.UTF8.GetBytes(config.AdminPassword);

        // D04 (CVSS 3.7): Use PBKDF2 with SHA256, 600000 iterations per NIST SP 800-63B
        var hashBytes = Rfc2898DeriveBytes.Pbkdf2(
            passwordBytes,
            salt,
            600_000,
            HashAlgorithmName.SHA256,
            32);
        var hashBase64 = Convert.ToBase64String(hashBytes);

        // Wipe password from memory
        CryptographicOperations.ZeroMemory(passwordBytes);
        CryptographicOperations.ZeroMemory(hashBytes);

        var securityConfig = new Dictionary<string, object>
        {
            ["users"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["id"] = Guid.NewGuid().ToString("N"),
                    ["username"] = config.AdminUsername,
                    ["passwordHash"] = hashBase64,
                    ["salt"] = saltBase64,
                    ["role"] = "admin",
                    ["createdAt"] = DateTime.UtcNow.ToString("O")
                }
            }
        };

        var json = JsonSerializer.Serialize(securityConfig,
            new JsonSerializerOptions { WriteIndented = true });

        await File.WriteAllTextAsync(securityPath, json, ct);
        _logger.LogInformation("Created admin user: {Username}", config.AdminUsername);
    }

    /// <summary>
    /// Registers a platform-specific system service by delegating to PlatformServiceManager.
    /// This ensures a single source of truth for service management logic, accessible
    /// to both the Launcher and CLI.
    /// </summary>
    private async Task RegisterServiceAsync(InstallConfiguration config, CancellationToken ct)
    {
        var exePath = OperatingSystem.IsWindows()
            ? Path.Combine(config.InstallPath, "DataWarehouse.Launcher.exe")
            : Path.Combine(config.InstallPath, "DataWarehouse.Launcher");

        _logger.LogInformation("Registering system service via PlatformServiceManager...");

        var registration = new ServiceRegistration
        {
            Name = OperatingSystem.IsMacOS() ? "com.datawarehouse" : "DataWarehouse",
            DisplayName = "DataWarehouse Service",
            ExecutablePath = exePath,
            WorkingDirectory = config.InstallPath,
            AutoStart = config.AutoStart,
            Description = "DataWarehouse data management service"
        };

        await PlatformServiceManager.RegisterServiceAsync(registration, ct);
        _logger.LogInformation("System service registered successfully");
    }

    /// <summary>
    /// Runs a process and waits for it to exit. Throws on non-zero exit code.
    /// </summary>
    private async Task RunProcessAsync(string fileName, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi);
        if (process == null)
        {
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        }

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException(
                $"Process '{fileName} {arguments}' failed with exit code {process.ExitCode}: {stderr}");
        }
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

        _embeddedCts?.Cancel();

        if (_connection != null)
        {
            await _connection.DisposeAsync();
        }

        await _runner.DisposeAsync();
        _embeddedCts?.Dispose();
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
