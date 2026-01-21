using System.Runtime.CompilerServices;

namespace DataWarehouse.Plugins.Backup;

/// <summary>
/// Interface for backup destinations (local filesystem, cloud storage, network share).
/// </summary>
public interface IBackupDestination : IAsyncDisposable
{
    /// <summary>Gets the destination type.</summary>
    BackupDestinationType Type { get; }

    /// <summary>Gets the destination path.</summary>
    string Path { get; }

    /// <summary>Gets whether the destination is available.</summary>
    bool IsAvailable { get; }

    /// <summary>Initializes the destination.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Writes a file to the destination.</summary>
    Task WriteFileAsync(string relativePath, Stream content, BackupFileMetadata metadata, CancellationToken ct = default);

    /// <summary>Reads a file from the destination.</summary>
    Task<Stream> ReadFileAsync(string relativePath, CancellationToken ct = default);

    /// <summary>Deletes a file from the destination.</summary>
    Task DeleteFileAsync(string relativePath, CancellationToken ct = default);

    /// <summary>Lists files at the destination.</summary>
    IAsyncEnumerable<BackupFileMetadata> ListFilesAsync(string? prefix = null, CancellationToken ct = default);

    /// <summary>Checks if a file exists.</summary>
    Task<bool> FileExistsAsync(string relativePath, CancellationToken ct = default);

    /// <summary>Gets destination statistics.</summary>
    Task<DestinationStatistics> GetStatisticsAsync(CancellationToken ct = default);

    /// <summary>Tests connectivity to the destination.</summary>
    Task<bool> TestConnectivityAsync(CancellationToken ct = default);
}

/// <summary>
/// Specifies the type of backup destination.
/// </summary>
public enum BackupDestinationType
{
    /// <summary>Local filesystem path.</summary>
    LocalFilesystem,
    /// <summary>External USB/connected drive.</summary>
    ExternalDrive,
    /// <summary>Network share (UNC path or SMB).</summary>
    NetworkShare,
    /// <summary>Amazon S3 bucket.</summary>
    AmazonS3,
    /// <summary>Azure Blob Storage.</summary>
    AzureBlob,
    /// <summary>Google Cloud Storage.</summary>
    GoogleCloudStorage,
    /// <summary>Hybrid backup combining local and cloud.</summary>
    Hybrid
}

/// <summary>
/// Metadata for a backup file.
/// </summary>
public sealed record BackupFileMetadata
{
    /// <summary>Gets or sets the relative path within the backup.</summary>
    public string RelativePath { get; init; } = "";

    /// <summary>Gets or sets the original path on the source system.</summary>
    public string? OriginalPath { get; init; }

    /// <summary>Gets or sets the file size in bytes.</summary>
    public long Size { get; init; }

    /// <summary>Gets or sets the last modified time.</summary>
    public DateTime LastModified { get; init; }

    /// <summary>Gets or sets the file checksum.</summary>
    public string? Checksum { get; init; }

    /// <summary>Gets or sets custom metadata.</summary>
    public Dictionary<string, string>? CustomMetadata { get; init; }
}

/// <summary>
/// Statistics for a backup destination.
/// </summary>
public sealed record DestinationStatistics
{
    /// <summary>Gets or sets the destination type.</summary>
    public BackupDestinationType DestinationType { get; init; }

    /// <summary>Gets or sets the available space in bytes.</summary>
    public long AvailableSpaceBytes { get; init; }

    /// <summary>Gets or sets the used space in bytes.</summary>
    public long UsedSpaceBytes { get; init; }

    /// <summary>Gets or sets the total file count.</summary>
    public long TotalFiles { get; init; }

    /// <summary>Gets or sets the last backup time.</summary>
    public DateTime? LastBackupTime { get; init; }
}

/// <summary>
/// Configuration for continuous backup.
/// </summary>
public sealed record ContinuousBackupConfig
{
    /// <summary>Gets or sets whether to enable real-time file monitoring.</summary>
    public bool EnableRealTimeMonitoring { get; init; } = true;

    /// <summary>Gets or sets the debounce interval for file changes.</summary>
    public TimeSpan DebounceInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets the batch size for processing changes.</summary>
    public int BatchSize { get; init; } = 100;

    /// <summary>Gets or sets bandwidth throttling in bytes per second (0 = unlimited).</summary>
    public long BandwidthLimitBytesPerSecond { get; init; }

    /// <summary>Gets or sets file patterns to include.</summary>
    public string[]? IncludePatterns { get; init; }

    /// <summary>Gets or sets file patterns to exclude.</summary>
    public string[]? ExcludePatterns { get; init; }

    /// <summary>Gets or sets the maximum file size to backup (0 = unlimited).</summary>
    public long MaxFileSizeBytes { get; init; }

    /// <summary>Gets or sets the synthetic full backup interval.</summary>
    public TimeSpan? SyntheticFullInterval { get; init; }
}

/// <summary>
/// Configuration for backup destinations.
/// </summary>
public sealed record BackupDestinationConfig
{
    /// <summary>Gets or sets the destination type.</summary>
    public required BackupDestinationType Type { get; init; }

    /// <summary>Gets or sets the destination path.</summary>
    public required string Path { get; init; }

    /// <summary>Gets or sets optional credentials.</summary>
    public BackupCredentials? Credentials { get; init; }

    /// <summary>Gets or sets the region for cloud storage.</summary>
    public string? Region { get; init; }

    /// <summary>Gets or sets the endpoint URL for S3-compatible storage.</summary>
    public string? EndpointUrl { get; init; }

    /// <summary>Gets or sets whether to use SSL/TLS.</summary>
    public bool UseSsl { get; init; } = true;

    /// <summary>Gets or sets the connection timeout.</summary>
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Credentials for backup destinations.
/// </summary>
public sealed record BackupCredentials
{
    /// <summary>Gets or sets the access key.</summary>
    public string? AccessKey { get; init; }

    /// <summary>Gets or sets the secret key.</summary>
    public string? SecretKey { get; init; }

    /// <summary>Gets or sets the account name (for Azure).</summary>
    public string? AccountName { get; init; }

    /// <summary>Gets or sets the SAS token (for Azure).</summary>
    public string? SasToken { get; init; }
}

/// <summary>
/// Backup scheduling configuration.
/// </summary>
public sealed record BackupScheduleConfig
{
    /// <summary>Gets or sets the schedule type.</summary>
    public required BackupSchedule Schedule { get; init; }

    /// <summary>Gets or sets the cron expression for custom schedules.</summary>
    public string? CronExpression { get; init; }

    /// <summary>Gets or sets the preferred time of day.</summary>
    public TimeOnly? PreferredTime { get; init; }
}

/// <summary>
/// Backup schedule frequency.
/// </summary>
public enum BackupSchedule
{
    /// <summary>Real-time continuous backup.</summary>
    RealTime,
    /// <summary>Every hour.</summary>
    Hourly,
    /// <summary>Once per day.</summary>
    Daily,
    /// <summary>Once per week.</summary>
    Weekly,
    /// <summary>Once per month.</summary>
    Monthly,
    /// <summary>Custom cron expression.</summary>
    CustomCron,
    /// <summary>Manual backup only.</summary>
    Manual
}

/// <summary>
/// Backup policy configuration.
/// </summary>
public sealed record BackupPolicy
{
    /// <summary>Gets or sets the policy name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets or sets the backup type.</summary>
    public BackupProviderType BackupType { get; init; }

    /// <summary>Gets or sets the schedule.</summary>
    public BackupScheduleConfig? Schedule { get; init; }

    /// <summary>Gets or sets retention days.</summary>
    public int RetentionDays { get; init; } = 30;

    /// <summary>Gets or sets max versions to keep.</summary>
    public int MaxVersions { get; init; } = 10;

    /// <summary>Gets or sets whether the policy is enabled.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Backup job information.
/// </summary>
public sealed record BackupJob
{
    /// <summary>Gets the job ID.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the job name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the source paths.</summary>
    public List<string> SourcePaths { get; init; } = new();

    /// <summary>Gets the destination name.</summary>
    public required string DestinationName { get; init; }

    /// <summary>Gets the backup options.</summary>
    public BackupOptions? Options { get; init; }

    /// <summary>Gets the schedule.</summary>
    public BackupScheduleConfig? Schedule { get; init; }

    /// <summary>Gets when the job was created.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Gets when the job last ran.</summary>
    public DateTime? LastRunAt { get; set; }

    /// <summary>Gets when the job will next run.</summary>
    public DateTime? NextRunAt { get; set; }

    /// <summary>Gets the job status.</summary>
    public BackupJobStatus Status { get; set; }
}

/// <summary>
/// Backup job status.
/// </summary>
public enum BackupJobStatus
{
    /// <summary>Job is idle.</summary>
    Idle,
    /// <summary>Job is scheduled.</summary>
    Scheduled,
    /// <summary>Job is running.</summary>
    Running,
    /// <summary>Job completed successfully.</summary>
    Completed,
    /// <summary>Job failed.</summary>
    Failed,
    /// <summary>Job was cancelled.</summary>
    Cancelled
}

/// <summary>
/// Backup scheduler for managing scheduled backup jobs.
/// </summary>
public sealed class BackupScheduler : IAsyncDisposable
{
    private readonly IBackupProvider _provider;
    private readonly Dictionary<string, BackupJob> _jobs = new();
    private readonly Dictionary<string, Timer> _timers = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public BackupScheduler(IBackupProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public event EventHandler<BackupJob>? JobStarted;
    public event EventHandler<BackupJob>? JobCompleted;
    public event EventHandler<(BackupJob Job, Exception Error)>? JobFailed;

    public async Task AddJobAsync(BackupJob job, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _jobs[job.Id] = job;

            if (job.Schedule != null && job.Schedule.Schedule != BackupSchedule.Manual)
            {
                ScheduleJob(job);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveJobAsync(string jobId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_timers.TryGetValue(jobId, out var timer))
            {
                await timer.DisposeAsync();
                _timers.Remove(jobId);
            }
            _jobs.Remove(jobId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public IReadOnlyList<BackupJob> GetJobs() => _jobs.Values.ToList();

    public BackupJob? GetJob(string jobId) => _jobs.GetValueOrDefault(jobId);

    private void ScheduleJob(BackupJob job)
    {
        var delay = CalculateNextRunDelay(job.Schedule!);
        job.NextRunAt = DateTime.UtcNow + delay;

        if (_timers.TryGetValue(job.Id, out var existingTimer))
        {
            existingTimer.Dispose();
        }

        _timers[job.Id] = new Timer(
            async _ => await RunJobAsync(job.Id),
            null,
            delay,
            Timeout.InfiniteTimeSpan);
    }

    private async Task RunJobAsync(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return;

        try
        {
            job.Status = BackupJobStatus.Running;
            job.LastRunAt = DateTime.UtcNow;
            JobStarted?.Invoke(this, job);

            await _provider.PerformIncrementalBackupAsync(job.Options);

            job.Status = BackupJobStatus.Completed;
            JobCompleted?.Invoke(this, job);

            // Reschedule
            if (job.Schedule != null && job.Schedule.Schedule != BackupSchedule.Manual)
            {
                ScheduleJob(job);
            }
        }
        catch (Exception ex)
        {
            job.Status = BackupJobStatus.Failed;
            JobFailed?.Invoke(this, (job, ex));
        }
    }

    private static TimeSpan CalculateNextRunDelay(BackupScheduleConfig schedule)
    {
        var now = DateTime.UtcNow;

        return schedule.Schedule switch
        {
            BackupSchedule.Hourly => TimeSpan.FromHours(1),
            BackupSchedule.Daily => TimeSpan.FromDays(1),
            BackupSchedule.Weekly => TimeSpan.FromDays(7),
            BackupSchedule.Monthly => TimeSpan.FromDays(30),
            _ => TimeSpan.FromHours(1)
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var timer in _timers.Values)
        {
            await timer.DisposeAsync();
        }
        _timers.Clear();
        _lock.Dispose();
    }
}
