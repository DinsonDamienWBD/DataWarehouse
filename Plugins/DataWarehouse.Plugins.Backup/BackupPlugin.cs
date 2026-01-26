using DataWarehouse.Plugins.Backup.Providers;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.Backup;

/// <summary>
/// Main backup plugin that provides comprehensive backup functionality including
/// continuous, incremental, differential, delta, and synthetic full backups.
/// Supports multiple backup destinations and scheduling.
/// </summary>
public sealed class BackupPlugin : FeaturePluginBase, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IBackupProvider> _providers = new();
    private readonly ConcurrentDictionary<string, IBackupDestination> _destinations = new();
    private readonly ConcurrentDictionary<string, BackupJob> _jobs = new();
    private readonly ConcurrentDictionary<string, BackupPolicy> _policies = new();
    private readonly BackupPluginConfig _config;
    private readonly string _statePath;
    private readonly ILogger<BackupPlugin>? _logger;
    private CancellationTokenSource? _cts;
    private volatile bool _disposed;

    public override string Id => "com.datawarehouse.plugins.backup";
    public override string Name => "DataWarehouse Backup Plugin";
    public override PluginCategory Category => PluginCategory.FeatureProvider;
    public override string Version => "1.0.0";

    /// <summary>
    /// Event raised when a backup starts.
    /// </summary>
    public event EventHandler<BackupJob>? BackupStarted;

    /// <summary>
    /// Event raised when a backup completes.
    /// </summary>
    public event EventHandler<BackupCompletedEventArgs>? BackupCompleted;

    /// <summary>
    /// Event raised when backup progress changes.
    /// </summary>
    public event EventHandler<BackupProgressEventArgs>? ProgressChanged;

    /// <summary>
    /// Creates a new backup plugin instance.
    /// </summary>
    public BackupPlugin(BackupPluginConfig? config = null, ILogger<BackupPlugin>? logger = null)
    {
        _config = config ?? new BackupPluginConfig();
        _logger = logger;
        _statePath = _config.StatePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DataWarehouse", "Backup");

        Directory.CreateDirectory(_statePath);
    }

    public override async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await LoadStateAsync();
    }

    public override async Task StopAsync()
    {
        _cts?.Cancel();

        foreach (var provider in _providers.Values)
        {
            await provider.DisposeAsync();
        }
        _providers.Clear();

        foreach (var destination in _destinations.Values)
        {
            await destination.DisposeAsync();
        }
        _destinations.Clear();

        await SaveStateAsync();
    }

    /// <summary>
    /// Registers a backup destination.
    /// </summary>
    public async Task<IBackupDestination> RegisterDestinationAsync(
        string name,
        IBackupDestination destination,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(destination);

        await destination.InitializeAsync(ct);
        _destinations[name] = destination;

        return destination;
    }

    /// <summary>
    /// Unregisters a backup destination.
    /// </summary>
    public async Task UnregisterDestinationAsync(string name)
    {
        if (_destinations.TryRemove(name, out var destination))
        {
            await destination.DisposeAsync();
        }
    }

    /// <summary>
    /// Creates and registers a backup provider.
    /// </summary>
    public async Task<IBackupProvider> CreateProviderAsync(
        string name,
        BackupProviderType type,
        string destinationName,
        CancellationToken ct = default)
    {
        if (!_destinations.TryGetValue(destinationName, out var destination))
        {
            throw new InvalidOperationException($"Destination '{destinationName}' not found");
        }

        var providerStatePath = Path.Combine(_statePath, "providers", name);
        Directory.CreateDirectory(providerStatePath);

        IBackupProvider provider = type switch
        {
            BackupProviderType.Continuous => new ContinuousBackupProvider(
                destination, providerStatePath, _config.ContinuousConfig),
            BackupProviderType.Incremental => new IncrementalBackupProvider(
                destination, providerStatePath, _config.IncrementalConfig),
            BackupProviderType.Delta => new DeltaBackupProvider(
                destination, providerStatePath, _config.DeltaConfig),
            BackupProviderType.SyntheticFull => new SyntheticFullBackupProvider(
                destination, providerStatePath, _config.SyntheticConfig),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported backup type")
        };

        await provider.InitializeAsync(ct);

        // Wire up events
        provider.ProgressChanged += (s, e) => ProgressChanged?.Invoke(this, e);
        provider.BackupCompleted += (s, e) => BackupCompleted?.Invoke(this, e);

        _providers[name] = provider;
        return provider;
    }

    /// <summary>
    /// Gets a registered provider by name.
    /// </summary>
    public IBackupProvider? GetProvider(string name)
    {
        return _providers.GetValueOrDefault(name);
    }

    /// <summary>
    /// Gets all registered providers.
    /// </summary>
    public IReadOnlyDictionary<string, IBackupProvider> GetProviders()
    {
        return _providers;
    }

    /// <summary>
    /// Gets a registered destination by name.
    /// </summary>
    public IBackupDestination? GetDestination(string name)
    {
        return _destinations.GetValueOrDefault(name);
    }

    /// <summary>
    /// Gets all registered destinations.
    /// </summary>
    public IReadOnlyDictionary<string, IBackupDestination> GetDestinations()
    {
        return _destinations;
    }

    /// <summary>
    /// Creates and schedules a backup job.
    /// </summary>
    public async Task<BackupJob> CreateJobAsync(
        string name,
        string providerName,
        IEnumerable<string> sourcePaths,
        BackupScheduleConfig? schedule = null,
        BackupOptions? options = null,
        CancellationToken ct = default)
    {
        if (!_providers.ContainsKey(providerName))
        {
            throw new InvalidOperationException($"Provider '{providerName}' not found");
        }

        var job = new BackupJob
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            SourcePaths = sourcePaths.ToList(),
            DestinationName = providerName,
            Schedule = schedule,
            Options = options
        };

        _jobs[job.Id] = job;
        await SaveStateAsync();

        return job;
    }

    /// <summary>
    /// Runs a backup job immediately.
    /// </summary>
    public async Task<BackupResult> RunJobAsync(string jobId, CancellationToken ct = default)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            throw new InvalidOperationException($"Job '{jobId}' not found");
        }

        if (!_providers.TryGetValue(job.DestinationName, out var provider))
        {
            throw new InvalidOperationException($"Provider '{job.DestinationName}' not found");
        }

        // Validate source paths
        if (job.SourcePaths == null || job.SourcePaths.Count == 0)
        {
            throw new InvalidOperationException($"Job '{job.Name}' has no source paths configured");
        }

        // Validate that at least one source path exists
        var existingPaths = job.SourcePaths.Where(p => Directory.Exists(p) || File.Exists(p)).ToList();
        if (existingPaths.Count == 0)
        {
            throw new InvalidOperationException($"Job '{job.Name}': None of the configured source paths exist");
        }

        // Log which paths are being backed up
        _logger?.LogInformation("Starting backup job '{JobName}' for {PathCount} source path(s)",
            job.Name, existingPaths.Count);

        foreach (var path in job.SourcePaths)
        {
            if (Directory.Exists(path) || File.Exists(path))
            {
                _logger?.LogInformation("  Backing up: {Path}", path);
            }
            else
            {
                _logger?.LogWarning("  Source path does not exist (skipping): {Path}", path);
            }
        }

        job.Status = BackupJobStatus.Running;
        job.LastRunAt = DateTime.UtcNow;
        BackupStarted?.Invoke(this, job);

        try
        {
            BackupResult result;

            // Check if this is the first backup for this provider (needs full backup to establish baseline)
            var stats = provider.GetStatistics();
            if (stats.LastFullBackupTime == null || stats.TrackedFiles == 0)
            {
                _logger?.LogInformation("Performing initial full backup for job '{JobName}'", job.Name);
                result = await provider.PerformFullBackupAsync(existingPaths, job.Options, ct);
            }
            else
            {
                _logger?.LogInformation("Performing incremental backup for job '{JobName}'", job.Name);
                result = await provider.PerformIncrementalBackupAsync(job.Options, ct);
            }

            job.Status = BackupJobStatus.Completed;
            _logger?.LogInformation("Backup job '{JobName}' completed: {FileCount} files, {ByteCount} bytes",
                job.Name, result.TotalFiles, result.TotalBytes);

            return result;
        }
        catch (Exception ex)
        {
            job.Status = BackupJobStatus.Failed;
            _logger?.LogError(ex, "Backup job '{JobName}' failed", job.Name);
            throw;
        }
    }

    /// <summary>
    /// Performs a full backup using the specified provider.
    /// </summary>
    public async Task<BackupResult> PerformFullBackupAsync(
        string providerName,
        IEnumerable<string> paths,
        BackupOptions? options = null,
        CancellationToken ct = default)
    {
        if (!_providers.TryGetValue(providerName, out var provider))
        {
            throw new InvalidOperationException($"Provider '{providerName}' not found");
        }

        return await provider.PerformFullBackupAsync(paths, options, ct);
    }

    /// <summary>
    /// Performs an incremental backup using the specified provider.
    /// </summary>
    public async Task<BackupResult> PerformIncrementalBackupAsync(
        string providerName,
        BackupOptions? options = null,
        CancellationToken ct = default)
    {
        if (!_providers.TryGetValue(providerName, out var provider))
        {
            throw new InvalidOperationException($"Provider '{providerName}' not found");
        }

        return await provider.PerformIncrementalBackupAsync(options, ct);
    }

    /// <summary>
    /// Restores from a backup.
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(
        string providerName,
        string backupId,
        string? targetPath = null,
        DateTime? pointInTime = null,
        CancellationToken ct = default)
    {
        if (!_providers.TryGetValue(providerName, out var provider))
        {
            throw new InvalidOperationException($"Provider '{providerName}' not found");
        }

        return await provider.RestoreAsync(backupId, targetPath, pointInTime, ct);
    }

    /// <summary>
    /// Creates a synthetic full backup from incrementals.
    /// </summary>
    public async Task<SyntheticFullBackupResult> CreateSyntheticFullAsync(
        string providerName,
        CancellationToken ct = default)
    {
        if (!_providers.TryGetValue(providerName, out var provider))
        {
            throw new InvalidOperationException($"Provider '{providerName}' not found");
        }

        if (provider is not ISyntheticFullBackupProvider syntheticProvider)
        {
            throw new InvalidOperationException($"Provider '{providerName}' does not support synthetic full backups");
        }

        return await syntheticProvider.CreateSyntheticFullBackupAsync(ct);
    }

    /// <summary>
    /// Starts continuous monitoring on a provider.
    /// </summary>
    public async Task StartContinuousBackupAsync(
        string providerName,
        IEnumerable<string> paths,
        CancellationToken ct = default)
    {
        if (!_providers.TryGetValue(providerName, out var provider))
        {
            throw new InvalidOperationException($"Provider '{providerName}' not found");
        }

        if (provider is not IContinuousBackupProvider continuousProvider)
        {
            throw new InvalidOperationException($"Provider '{providerName}' does not support continuous backup");
        }

        await continuousProvider.StartMonitoringAsync(paths, ct);
    }

    /// <summary>
    /// Stops continuous monitoring on a provider.
    /// </summary>
    public async Task StopContinuousBackupAsync(string providerName)
    {
        if (!_providers.TryGetValue(providerName, out var provider))
        {
            return;
        }

        if (provider is IContinuousBackupProvider continuousProvider)
        {
            await continuousProvider.StopMonitoringAsync();
        }
    }

    /// <summary>
    /// Adds or updates a backup policy.
    /// </summary>
    public void SetPolicy(BackupPolicy policy)
    {
        _policies[policy.Name] = policy;
    }

    /// <summary>
    /// Gets a backup policy by name.
    /// </summary>
    public BackupPolicy? GetPolicy(string name)
    {
        return _policies.GetValueOrDefault(name);
    }

    /// <summary>
    /// Gets all backup policies.
    /// </summary>
    public IReadOnlyDictionary<string, BackupPolicy> GetPolicies()
    {
        return _policies;
    }

    /// <summary>
    /// Gets backup statistics for a provider.
    /// </summary>
    public BackupStatistics? GetStatistics(string providerName)
    {
        if (!_providers.TryGetValue(providerName, out var provider))
        {
            return null;
        }

        return provider.GetStatistics();
    }

    /// <summary>
    /// Lists all backups from a provider.
    /// </summary>
    public IAsyncEnumerable<BackupMetadata> ListBackupsAsync(
        string providerName,
        CancellationToken ct = default)
    {
        if (!_providers.TryGetValue(providerName, out var provider))
        {
            return AsyncEnumerable.Empty<BackupMetadata>();
        }

        return provider.ListBackupsAsync(ct);
    }

    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new()
            {
                Name = "FullBackup",
                Description = "Performs a full backup of specified paths",
                Parameters = new Dictionary<string, object>
                {
                    ["paths"] = new PluginParameterDescriptor { Name = "paths", Type = "string[]", Required = true, Description = "Paths to backup" },
                    ["provider"] = new PluginParameterDescriptor { Name = "provider", Type = "string", Required = true, Description = "Provider name" }
                }
            },
            new()
            {
                Name = "IncrementalBackup",
                Description = "Performs an incremental backup",
                Parameters = new Dictionary<string, object>
                {
                    ["provider"] = new PluginParameterDescriptor { Name = "provider", Type = "string", Required = true, Description = "Provider name" }
                }
            },
            new()
            {
                Name = "Restore",
                Description = "Restores from a backup",
                Parameters = new Dictionary<string, object>
                {
                    ["provider"] = new PluginParameterDescriptor { Name = "provider", Type = "string", Required = true, Description = "Provider name" },
                    ["backupId"] = new PluginParameterDescriptor { Name = "backupId", Type = "string", Required = true, Description = "Backup ID to restore" },
                    ["targetPath"] = new PluginParameterDescriptor { Name = "targetPath", Type = "string", Required = false, Description = "Target restore path" }
                }
            },
            new()
            {
                Name = "SyntheticFull",
                Description = "Creates a synthetic full backup from incrementals",
                Parameters = new Dictionary<string, object>
                {
                    ["provider"] = new PluginParameterDescriptor { Name = "provider", Type = "string", Required = true, Description = "Provider name" }
                }
            },
            new()
            {
                Name = "StartContinuous",
                Description = "Starts continuous backup monitoring",
                Parameters = new Dictionary<string, object>
                {
                    ["provider"] = new PluginParameterDescriptor { Name = "provider", Type = "string", Required = true, Description = "Provider name" },
                    ["paths"] = new PluginParameterDescriptor { Name = "paths", Type = "string[]", Required = true, Description = "Paths to monitor" }
                }
            }
        };
    }

    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["Description"] = "Comprehensive backup plugin supporting continuous, incremental, differential, delta, and synthetic full backups";
        metadata["SupportsCloudDestinations"] = true;
        metadata["SupportsDeduplication"] = true;
        metadata["SupportsBlockLevelBackup"] = true;
        metadata["SupportsSyntheticFull"] = true;
        metadata["RegisteredProviders"] = _providers.Count;
        metadata["RegisteredDestinations"] = _destinations.Count;
        metadata["ActiveJobs"] = _jobs.Count(j => j.Value.Status == BackupJobStatus.Running);
        return metadata;
    }

    private async Task LoadStateAsync()
    {
        var stateFile = Path.Combine(_statePath, "plugin_state.json");
        if (!File.Exists(stateFile))
        {
            _logger?.LogInformation("No existing state file at {StatePath}, starting fresh", stateFile);
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(stateFile);
            var state = System.Text.Json.JsonSerializer.Deserialize<PluginState>(json);

            if (state == null)
            {
                _logger?.LogWarning("State file at {StatePath} was empty or invalid, starting fresh", stateFile);
                return;
            }

            // Restore policies
            foreach (var policy in state.Policies)
                _policies[policy.Name] = policy;

            // Restore jobs
            foreach (var job in state.Jobs)
                _jobs[job.Id] = job;

            _logger?.LogInformation("Loaded backup state with {PolicyCount} policies and {JobCount} jobs from {StatePath}",
                state.Policies?.Count ?? 0, state.Jobs?.Count ?? 0, stateFile);
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger?.LogError(ex, "State file at {StatePath} is corrupted, starting fresh", stateFile);

            // Back up corrupted file for analysis
            var backupPath = stateFile + ".corrupted." + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            try
            {
                File.Move(stateFile, backupPath);
                _logger?.LogInformation("Moved corrupted state file to {BackupPath}", backupPath);
            }
            catch (Exception moveEx)
            {
                _logger?.LogWarning(moveEx, "Failed to backup corrupted state file");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load backup state from {StatePath}", stateFile);
            throw; // Critical failure - don't silently continue
        }
    }

    private async Task SaveStateAsync()
    {
        var state = new PluginState
        {
            Policies = _policies.Values.ToList(),
            Jobs = _jobs.Values.ToList(),
            SavedAt = DateTime.UtcNow
        };

        var json = System.Text.Json.JsonSerializer.Serialize(state,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var stateFile = Path.Combine(_statePath, "plugin_state.json");
        await File.WriteAllTextAsync(stateFile, json);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync();
        _cts?.Dispose();
    }
}

internal sealed class PluginState
{
    public List<BackupPolicy> Policies { get; init; } = new();
    public List<BackupJob> Jobs { get; init; } = new();
    public DateTime SavedAt { get; init; }
}

/// <summary>
/// Configuration for the backup plugin.
/// </summary>
public sealed record BackupPluginConfig
{
    /// <summary>Gets or sets the state storage path.</summary>
    public string? StatePath { get; init; }

    /// <summary>Gets or sets continuous backup config.</summary>
    public ContinuousBackupConfig? ContinuousConfig { get; init; }

    /// <summary>Gets or sets incremental backup config.</summary>
    public IncrementalBackupConfig? IncrementalConfig { get; init; }

    /// <summary>Gets or sets delta backup config.</summary>
    public DeltaBackupConfig? DeltaConfig { get; init; }

    /// <summary>Gets or sets synthetic full backup config.</summary>
    public SyntheticFullBackupConfig? SyntheticConfig { get; init; }
}
