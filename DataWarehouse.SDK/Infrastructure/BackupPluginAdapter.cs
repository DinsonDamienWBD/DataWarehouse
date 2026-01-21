using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Infrastructure;

/// <summary>
/// Adapter that provides backward compatibility for SDK backup operations
/// while delegating to the new DataWarehouse.Plugins.Backup plugin.
///
/// MIGRATION NOTICE:
/// Backup implementations have been moved to the DataWarehouse.Plugins.Backup plugin.
/// This adapter provides compatibility for existing code. New implementations should
/// reference the plugin directly:
/// - ContinuousBackupProvider
/// - IncrementalBackupProvider
/// - DeltaBackupProvider
/// - SyntheticFullBackupProvider
///
/// The following types are now available in the plugin:
/// - IBackupProvider (unified backup interface)
/// - IContinuousBackupProvider (real-time monitoring)
/// - IDeltaBackupProvider (block-level backups)
/// - ISyntheticFullBackupProvider (synthetic full creation)
/// - BackupPlugin (main plugin class for Kernel registration)
/// </summary>
public sealed class BackupPluginAdapter : IAsyncDisposable
{
    private readonly BackupDestinationManager _destinationManager;
    private readonly object? _backupPlugin; // Reference to DataWarehouse.Plugins.Backup.BackupPlugin when loaded
    private bool _pluginLoaded;
    private bool _disposed;

    /// <summary>
    /// Event raised when a backup job starts.
    /// </summary>
    public event EventHandler<BackupJobEventArgs>? BackupStarted;

    /// <summary>
    /// Event raised when a backup job completes.
    /// </summary>
    public event EventHandler<BackupJobEventArgs>? BackupCompleted;

    /// <summary>
    /// Event raised when a backup job fails.
    /// </summary>
    public event EventHandler<BackupJobEventArgs>? BackupFailed;

    /// <summary>
    /// Event raised to report backup progress.
    /// </summary>
    public event EventHandler<BackupProgressEventArgs>? BackupProgress;

    /// <summary>
    /// Creates a new backup plugin adapter.
    /// </summary>
    public BackupPluginAdapter()
    {
        _destinationManager = new BackupDestinationManager();
        _destinationManager.BackupStarted += (s, e) => BackupStarted?.Invoke(this, e);
        _destinationManager.BackupCompleted += (s, e) => BackupCompleted?.Invoke(this, e);
        _destinationManager.BackupFailed += (s, e) => BackupFailed?.Invoke(this, e);
        _destinationManager.BackupProgress += (s, e) => BackupProgress?.Invoke(this, e);
    }

    /// <summary>
    /// Attempts to load and use the DataWarehouse.Plugins.Backup plugin.
    /// Falls back to built-in implementations if plugin is not available.
    /// </summary>
    public async Task<bool> TryLoadPluginAsync(CancellationToken ct = default)
    {
        try
        {
            // Attempt to load plugin assembly dynamically
            var pluginPath = FindPluginAssembly();
            if (pluginPath != null)
            {
                var assembly = System.Reflection.Assembly.LoadFrom(pluginPath);
                var pluginType = assembly.GetType("DataWarehouse.Plugins.Backup.BackupPlugin");
                if (pluginType != null)
                {
                    _backupPlugin = Activator.CreateInstance(pluginType);
                    _pluginLoaded = true;
                    return true;
                }
            }
        }
        catch
        {
            // Fall back to built-in implementations
        }

        return false;
    }

    private static string? FindPluginAssembly()
    {
        var searchPaths = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins", "DataWarehouse.Plugins.Backup.dll"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DataWarehouse.Plugins.Backup.dll"),
            Path.Combine(Environment.CurrentDirectory, "plugins", "DataWarehouse.Plugins.Backup.dll")
        };

        foreach (var path in searchPaths)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    /// <summary>
    /// Gets whether the backup plugin is loaded.
    /// </summary>
    public bool IsPluginLoaded => _pluginLoaded;

    /// <summary>
    /// Gets the underlying destination manager for legacy compatibility.
    /// </summary>
    [Obsolete("Use the DataWarehouse.Plugins.Backup plugin directly for new implementations")]
    public BackupDestinationManager DestinationManager => _destinationManager;

    /// <summary>
    /// Registers a backup destination.
    /// </summary>
    public async Task<IBackupDestination> RegisterDestinationAsync(
        string name,
        BackupDestinationConfig config,
        CancellationToken ct = default)
    {
        return await _destinationManager.RegisterDestinationAsync(name, config, ct);
    }

    /// <summary>
    /// Starts a backup job.
    /// </summary>
    public async Task<BackupJob> StartBackupAsync(
        string destinationName,
        IEnumerable<string> sourcePaths,
        BackupScheduleConfig schedule,
        BackupOptions? options = null,
        CancellationToken ct = default)
    {
        return await _destinationManager.StartBackupAsync(destinationName, sourcePaths, schedule, options, ct);
    }

    /// <summary>
    /// Runs a backup immediately.
    /// </summary>
    public async Task<BackupJobResult> RunBackupNowAsync(
        string destinationName,
        IEnumerable<string> sourcePaths,
        BackupOptions? options = null,
        CancellationToken ct = default)
    {
        return await _destinationManager.RunBackupNowAsync(destinationName, sourcePaths, options, ct);
    }

    /// <summary>
    /// Restores from a backup.
    /// </summary>
    public async Task<RestoreJobResult> RestoreAsync(
        string destinationName,
        string targetPath,
        DateTime? pointInTime = null,
        CancellationToken ct = default)
    {
        return await _destinationManager.RestoreAsync(destinationName, targetPath, pointInTime, ct);
    }

    /// <summary>
    /// Gets backup statistics.
    /// </summary>
    public BackupManagerStatistics GetStatistics()
    {
        return _destinationManager.GetStatistics();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _destinationManager.DisposeAsync();

        if (_backupPlugin is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (_backupPlugin is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

/// <summary>
/// Plugin provider for discovering and loading backup providers from the plugin.
/// </summary>
public static class BackupPluginProvider
{
    private static readonly Lazy<Type?> _pluginType = new(LoadPluginType);

    private static Type? LoadPluginType()
    {
        try
        {
            var pluginPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "plugins", "DataWarehouse.Plugins.Backup.dll");

            if (File.Exists(pluginPath))
            {
                var assembly = System.Reflection.Assembly.LoadFrom(pluginPath);
                return assembly.GetType("DataWarehouse.Plugins.Backup.BackupPlugin");
            }
        }
        catch
        {
            // Plugin not available
        }
        return null;
    }

    /// <summary>
    /// Gets whether the backup plugin is available.
    /// </summary>
    public static bool IsPluginAvailable => _pluginType.Value != null;

    /// <summary>
    /// Creates a new instance of the backup plugin if available.
    /// </summary>
    public static object? CreatePlugin()
    {
        if (_pluginType.Value == null)
            return null;

        return Activator.CreateInstance(_pluginType.Value);
    }

    /// <summary>
    /// Gets the backup provider types supported by the plugin.
    /// </summary>
    public static string[] GetSupportedProviderTypes()
    {
        return new[]
        {
            "ContinuousBackupProvider",
            "IncrementalBackupProvider",
            "DeltaBackupProvider",
            "SyntheticFullBackupProvider"
        };
    }

    /// <summary>
    /// Gets the plugin assembly qualified name for Kernel registration.
    /// </summary>
    public static string GetPluginAssemblyName()
    {
        return "DataWarehouse.Plugins.Backup, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    }
}
