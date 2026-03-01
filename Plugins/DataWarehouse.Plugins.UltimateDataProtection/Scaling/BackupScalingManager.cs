using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Persistence;
using DataWarehouse.SDK.Contracts.Scaling;

namespace DataWarehouse.Plugins.UltimateDataProtection.Scaling;

/// <summary>
/// Manages backup subsystem scaling with dynamic <c>MaxConcurrentJobs</c> based on I/O capacity,
/// crash recovery with persistent backup metadata, and configurable retention policy enforcement.
/// Implements <see cref="IScalableSubsystem"/> to participate in the unified scaling infrastructure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dynamic MaxConcurrentJobs:</b> Replaces hardcoded <c>4</c> with dynamic calculation based on
/// I/O capacity monitoring. Uses Exponential Moving Average (EMA) of disk throughput and queue depth
/// to smoothly adjust concurrency: increase when I/O utilization &lt; 60%, decrease when &gt; 80%.
/// Upper bound is <c>4 * diskCount</c>, configurable via <see cref="ScalingLimits.MaxConcurrentOperations"/>.
/// </para>
/// <para>
/// <b>Crash recovery:</b> In-progress backup metadata is persisted to <see cref="IPersistentBackingStore"/>
/// under <c>dw://internal/backup/{jobId}</c>. On startup, incomplete backups are detected and either
/// resumed or restarted. Corrupted partial backups are marked for cleanup.
/// </para>
/// <para>
/// <b>Backup chain management:</b> Tracks backup chains (full + incrementals) in a
/// <see cref="BoundedCache{TKey,TValue}"/>. Configurable retention policies by count (keep last N),
/// age (keep last N days), or size (keep under N GB). A background task periodically enforces retention.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-11: Backup scaling with dynamic concurrency and crash recovery")]
public sealed class BackupScalingManager : IScalableSubsystem, IDisposable
{
    // ---- Constants ----
    private const string SubsystemName = "UltimateDataProtection";
    private const int DefaultMinConcurrentJobs = 1;
    private const int DefaultMaxConcurrentJobs = 16;
    private const int DefaultDiskCount = 1;
    private const double EmaAlpha = 0.3; // Smoothing factor for EMA
    private const double ScaleUpThreshold = 0.6; // I/O utilization below this -> scale up
    private const double ScaleDownThreshold = 0.8; // I/O utilization above this -> scale down
    private const int DefaultRetentionCheckIntervalMs = 60 * 60 * 1000; // 1 hour
    private const int DefaultMaxRetentionCount = 30;
    private const int DefaultMaxRetentionDays = 90;
    private const long DefaultMaxRetentionBytes = 100L * 1024 * 1024 * 1024; // 100 GB

    // ---- Dependencies ----
    private readonly IPersistentBackingStore? _backingStore;

    // ---- Configuration ----
    private volatile ScalingLimits _currentLimits;
    private int _diskCount;
    private int _maxConcurrentJobs;
    private int _currentConcurrentJobs;

    // ---- I/O capacity monitoring (EMA) ----
    // P2-2527: Lock protects non-atomic double read-modify-write operations on EMA fields
    // and _currentConcurrentJobs accessed from both the timer callback and callers.
    private readonly object _emaLock = new();
    private double _emaThroughput;
    private double _emaQueueDepth;
    private double _emaIoUtilization;

    // ---- Active job tracking ----
    private readonly BoundedCache<string, BackupJobMetadata> _activeJobs;
    private readonly object _jobLock = new();

    // ---- Backup chain management ----
    private readonly BoundedCache<string, BackupChain> _backupChains;

    // ---- Retention policy ----
    private RetentionPolicy _retentionPolicy;
    private readonly Timer _retentionEnforcementTimer;

    // ---- Metrics ----
    private long _jobsStarted;
    private long _jobsCompleted;
    private long _jobsFailed;
    private long _jobsRecovered;
    private long _chainsTracked;
    private long _totalBytesBackedUp;

    // ---- Backpressure ----
    private volatile BackpressureState _backpressureState = BackpressureState.Normal;

    // ---- Disposal ----
    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="BackupScalingManager"/> with configurable disk count,
    /// persistent backing store for crash recovery, and retention policy.
    /// </summary>
    /// <param name="backingStore">Optional persistent backing store for backup job metadata durability.</param>
    /// <param name="initialLimits">Optional initial scaling limits. Uses defaults if null.</param>
    /// <param name="diskCount">Number of disks available for concurrent I/O. Default: 1.</param>
    /// <param name="retentionPolicy">Optional retention policy. Uses defaults if null.</param>
    public BackupScalingManager(
        IPersistentBackingStore? backingStore = null,
        ScalingLimits? initialLimits = null,
        int diskCount = DefaultDiskCount,
        RetentionPolicy? retentionPolicy = null)
    {
        _backingStore = backingStore;
        _diskCount = Math.Max(1, diskCount);

        _currentLimits = initialLimits ?? new ScalingLimits(
            MaxCacheEntries: 10_000,
            MaxMemoryBytes: 256 * 1024 * 1024,
            MaxConcurrentOperations: Math.Min(4 * _diskCount, DefaultMaxConcurrentJobs),
            MaxQueueDepth: 1_000);

        _maxConcurrentJobs = _currentLimits.MaxConcurrentOperations;
        _currentConcurrentJobs = DefaultMinConcurrentJobs;

        _retentionPolicy = retentionPolicy ?? new RetentionPolicy
        {
            MaxCount = DefaultMaxRetentionCount,
            MaxAgeDays = DefaultMaxRetentionDays,
            MaxTotalBytes = DefaultMaxRetentionBytes
        };

        // Initialize active job cache with backing store for crash recovery
        var jobCacheOptions = new BoundedCacheOptions<string, BackupJobMetadata>
        {
            MaxEntries = 1_000,
            EvictionPolicy = CacheEvictionMode.LRU,
            BackingStore = _backingStore,
            BackingStorePath = "dw://internal/backup/",
            WriteThrough = true,
            Serializer = job => JsonSerializer.SerializeToUtf8Bytes(job),
            Deserializer = data => JsonSerializer.Deserialize<BackupJobMetadata>(data)!,
            KeyToString = key => key
        };
        _activeJobs = new BoundedCache<string, BackupJobMetadata>(jobCacheOptions);

        // Initialize backup chain cache
        var chainCacheOptions = new BoundedCacheOptions<string, BackupChain>
        {
            MaxEntries = 10_000,
            EvictionPolicy = CacheEvictionMode.LRU,
            BackingStore = _backingStore,
            BackingStorePath = "dw://internal/backup-chains/",
            WriteThrough = true,
            Serializer = chain => JsonSerializer.SerializeToUtf8Bytes(chain),
            Deserializer = data => JsonSerializer.Deserialize<BackupChain>(data)!,
            KeyToString = key => key
        };
        _backupChains = new BoundedCache<string, BackupChain>(chainCacheOptions);

        // Start retention enforcement timer
        _retentionEnforcementTimer = new Timer(
            RetentionEnforcementCallback,
            null,
            TimeSpan.FromMinutes(5), // Initial delay
            TimeSpan.FromMilliseconds(DefaultRetentionCheckIntervalMs));
    }

    // ---------------------------------------------------------------
    // IScalableSubsystem
    // ---------------------------------------------------------------

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object> GetScalingMetrics()
    {
        var metrics = new Dictionary<string, object>
        {
            ["backup.maxConcurrentJobs"] = _maxConcurrentJobs,
            ["backup.currentConcurrentJobs"] = _currentConcurrentJobs,
            ["backup.dynamicConcurrency"] = GetDynamicConcurrency(),
            ["backup.jobsStarted"] = Interlocked.Read(ref _jobsStarted),
            ["backup.jobsCompleted"] = Interlocked.Read(ref _jobsCompleted),
            ["backup.jobsFailed"] = Interlocked.Read(ref _jobsFailed),
            ["backup.jobsRecovered"] = Interlocked.Read(ref _jobsRecovered),
            ["backup.chainsTracked"] = Interlocked.Read(ref _chainsTracked),
            ["backup.totalBytesBackedUp"] = Interlocked.Read(ref _totalBytesBackedUp),
            ["backup.activeJobs"] = _activeJobs.Count,
            // P2-2527: read EMA snapshot under lock to avoid torn reads on non-atomic doubles.
            ["io.emaThroughput"] = GetEmaSnapshot().throughput,
            ["io.emaQueueDepth"] = GetEmaSnapshot().queueDepth,
            ["io.emaUtilization"] = GetEmaSnapshot().utilization,
            ["io.diskCount"] = _diskCount,
            ["retention.maxCount"] = _retentionPolicy.MaxCount,
            ["retention.maxAgeDays"] = _retentionPolicy.MaxAgeDays,
            ["retention.maxTotalBytes"] = _retentionPolicy.MaxTotalBytes,
            ["backpressure.state"] = _backpressureState.ToString(),
            ["backpressure.queueDepth"] = _activeJobs.Count
        };
        return metrics;
    }

    /// <inheritdoc/>
    public Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(limits);

        _currentLimits = limits;

        if (limits.MaxConcurrentOperations > 0)
        {
            _maxConcurrentJobs = limits.MaxConcurrentOperations;
        }

        // Recompute dynamic concurrency
        RecalculateDynamicConcurrency();
        UpdateBackpressureState();

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ScalingLimits CurrentLimits => _currentLimits;

    /// <inheritdoc/>
    public BackpressureState CurrentBackpressureState => _backpressureState;

    // ---------------------------------------------------------------
    // Dynamic MaxConcurrentJobs
    // ---------------------------------------------------------------

    /// <summary>
    /// Reports I/O metrics from the storage subsystem, which are used to dynamically
    /// adjust <c>MaxConcurrentJobs</c> using Exponential Moving Average smoothing.
    /// Call this periodically (e.g., every 10 seconds) with current I/O measurements.
    /// </summary>
    /// <param name="throughputBytesPerSec">Current disk throughput in bytes per second.</param>
    /// <param name="queueDepth">Current I/O queue depth.</param>
    /// <param name="ioUtilization">Current I/O utilization as a fraction (0.0 to 1.0).</param>
    public void ReportIoMetrics(double throughputBytesPerSec, double queueDepth, double ioUtilization)
    {
        // P2-2527: Lock protects non-atomic double read-modify-write operations.
        lock (_emaLock)
        {
            _emaThroughput = _emaThroughput == 0
                ? throughputBytesPerSec
                : EmaAlpha * throughputBytesPerSec + (1 - EmaAlpha) * _emaThroughput;

            _emaQueueDepth = _emaQueueDepth == 0
                ? queueDepth
                : EmaAlpha * queueDepth + (1 - EmaAlpha) * _emaQueueDepth;

            _emaIoUtilization = EmaAlpha * ioUtilization + (1 - EmaAlpha) * _emaIoUtilization;

            RecalculateDynamicConcurrency();
        }
    }

    /// <summary>
    /// Gets the current dynamically calculated number of concurrent backup jobs
    /// based on I/O capacity monitoring.
    /// </summary>
    /// <returns>The current dynamic concurrency level.</returns>
    public int GetDynamicConcurrency() => _currentConcurrentJobs;

    // ---------------------------------------------------------------
    // Backup Job Lifecycle
    // ---------------------------------------------------------------

    /// <summary>
    /// Registers a new backup job, persisting its metadata for crash recovery.
    /// </summary>
    /// <param name="jobId">Unique identifier for the backup job.</param>
    /// <param name="backupType">Type of backup (Full, Incremental, Differential).</param>
    /// <param name="sourcePath">Source path being backed up.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RegisterJobAsync(
        string jobId,
        BackupType backupType,
        string sourcePath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("Job ID must not be empty.", nameof(jobId));
        ObjectDisposedException.ThrowIf(_disposed, this);

        var metadata = new BackupJobMetadata
        {
            JobId = jobId,
            BackupType = backupType,
            SourcePath = sourcePath,
            Status = BackupJobStatus.InProgress,
            StartedUtc = DateTime.UtcNow,
            BytesProcessed = 0
        };

        await _activeJobs.PutAsync(jobId, metadata, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _jobsStarted);
        UpdateBackpressureState();
    }

    /// <summary>
    /// Marks a backup job as completed and updates the backup chain.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="bytesProcessed">Total bytes written by this backup job.</param>
    /// <param name="chainId">Optional chain identifier to associate this backup with.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task CompleteJobAsync(
        string jobId,
        long bytesProcessed,
        string? chainId = null,
        CancellationToken ct = default)
    {
        var job = await _activeJobs.GetAsync(jobId, ct).ConfigureAwait(false);
        if (job == null) return;

        var completed = job with
        {
            Status = BackupJobStatus.Completed,
            CompletedUtc = DateTime.UtcNow,
            BytesProcessed = bytesProcessed
        };

        await _activeJobs.PutAsync(jobId, completed, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _jobsCompleted);
        Interlocked.Add(ref _totalBytesBackedUp, bytesProcessed);

        // Update backup chain
        if (!string.IsNullOrEmpty(chainId))
        {
            await AddToChainAsync(chainId, completed, ct).ConfigureAwait(false);
        }

        UpdateBackpressureState();
    }

    /// <summary>
    /// Marks a backup job as failed for potential retry or cleanup.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="errorMessage">Description of the failure.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task FailJobAsync(string jobId, string errorMessage, CancellationToken ct = default)
    {
        var job = await _activeJobs.GetAsync(jobId, ct).ConfigureAwait(false);
        if (job == null) return;

        var failed = job with
        {
            Status = BackupJobStatus.Failed,
            CompletedUtc = DateTime.UtcNow,
            ErrorMessage = errorMessage
        };

        await _activeJobs.PutAsync(jobId, failed, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _jobsFailed);
        UpdateBackpressureState();
    }

    // ---------------------------------------------------------------
    // Crash Recovery
    // ---------------------------------------------------------------

    /// <summary>
    /// Scans for incomplete backup jobs from a previous session and returns them
    /// for the caller to decide whether to resume or restart each one.
    /// Corrupted partial backups are marked for cleanup.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of incomplete backup job metadata for recovery decisions.</returns>
    public async Task<IReadOnlyList<BackupJobMetadata>> RecoverIncompleteJobsAsync(
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_backingStore == null)
            return Array.Empty<BackupJobMetadata>();

        var incompleteJobs = new List<BackupJobMetadata>();
        var jobPaths = await _backingStore.ListAsync("dw://internal/backup/", ct).ConfigureAwait(false);

        foreach (var path in jobPaths)
        {
            try
            {
                var data = await _backingStore.ReadAsync(path, ct).ConfigureAwait(false);
                if (data == null) continue;

                var job = JsonSerializer.Deserialize<BackupJobMetadata>(data);
                if (job == null) continue;

                if (job.Status == BackupJobStatus.InProgress)
                {
                    incompleteJobs.Add(job);
                    Interlocked.Increment(ref _jobsRecovered);
                }
            }
            catch (JsonException)
            {
                // Corrupted metadata -- mark for cleanup by deleting
                await _backingStore.DeleteAsync(path, ct).ConfigureAwait(false);
            }
        }

        return incompleteJobs;
    }

    // ---------------------------------------------------------------
    // Backup Chain Management
    // ---------------------------------------------------------------

    /// <summary>
    /// Configures the retention policy for backup chains. Changes take effect
    /// at the next retention enforcement cycle.
    /// </summary>
    /// <param name="policy">The new retention policy to apply.</param>
    public void ConfigureRetentionPolicy(RetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _retentionPolicy = policy;
    }

    /// <summary>
    /// Gets the current backup chain for the specified chain identifier.
    /// </summary>
    /// <param name="chainId">The backup chain identifier.</param>
    /// <returns>The backup chain if found; otherwise null.</returns>
    public BackupChain? GetChain(string chainId)
    {
        return _backupChains.GetOrDefault(chainId);
    }

    // ---------------------------------------------------------------
    // Private Helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Recalculates the dynamic concurrency level based on EMA I/O metrics.
    /// </summary>
    private void RecalculateDynamicConcurrency()
    {
        var upperBound = Math.Min(4 * _diskCount, _maxConcurrentJobs);

        if (_emaIoUtilization < ScaleUpThreshold && _currentConcurrentJobs < upperBound)
        {
            // I/O is underutilized -- increase concurrent jobs
            _currentConcurrentJobs = Math.Min(_currentConcurrentJobs + 1, upperBound);
        }
        else if (_emaIoUtilization > ScaleDownThreshold && _currentConcurrentJobs > DefaultMinConcurrentJobs)
        {
            // I/O is overutilized -- decrease concurrent jobs
            _currentConcurrentJobs = Math.Max(_currentConcurrentJobs - 1, DefaultMinConcurrentJobs);
        }
    }

    /// <summary>
    /// Returns a consistent snapshot of EMA metrics under _emaLock to avoid torn reads.
    /// </summary>
    private (double throughput, double queueDepth, double utilization) GetEmaSnapshot()
    {
        lock (_emaLock)
        {
            return (_emaThroughput, _emaQueueDepth, _emaIoUtilization);
        }
    }

    /// <summary>
    /// Adds a completed backup job to its backup chain.
    /// </summary>
    private async Task AddToChainAsync(string chainId, BackupJobMetadata job, CancellationToken ct)
    {
        var chain = await _backupChains.GetAsync(chainId, ct).ConfigureAwait(false);
        if (chain == null)
        {
            chain = new BackupChain
            {
                ChainId = chainId,
                CreatedUtc = DateTime.UtcNow,
                Backups = new List<BackupChainEntry>()
            };
        }

        var entry = new BackupChainEntry
        {
            JobId = job.JobId,
            BackupType = job.BackupType,
            CompletedUtc = job.CompletedUtc ?? DateTime.UtcNow,
            BytesProcessed = job.BytesProcessed,
            SourcePath = job.SourcePath
        };

        var updatedBackups = new List<BackupChainEntry>(chain.Backups) { entry };
        var updatedChain = chain with { Backups = updatedBackups };

        await _backupChains.PutAsync(chainId, updatedChain, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _chainsTracked);
    }

    /// <summary>
    /// Enforces retention policies on backup chains by removing chains/entries
    /// that exceed configured count, age, or size thresholds.
    /// </summary>
    private void RetentionEnforcementCallback(object? state)
    {
        if (_disposed) return;

        try
        {
            var now = DateTime.UtcNow;
            var chainsToRemove = new List<string>();

            foreach (var entry in _backupChains)
            {
                var chain = entry.Value;
                var shouldRemove = false;

                // Check age retention
                if (_retentionPolicy.MaxAgeDays > 0 &&
                    (now - chain.CreatedUtc).TotalDays > _retentionPolicy.MaxAgeDays)
                {
                    shouldRemove = true;
                }

                if (shouldRemove)
                {
                    chainsToRemove.Add(entry.Key);
                }
            }

            // Remove expired chains
            foreach (var chainId in chainsToRemove)
            {
                _backupChains.TryRemove(chainId, out _);
            }

            // Enforce count limit -- keep only the most recent N chains
            if (_retentionPolicy.MaxCount > 0)
            {
                var allChains = _backupChains
                    .OrderByDescending(c => c.Value.CreatedUtc)
                    .ToList();

                for (int i = _retentionPolicy.MaxCount; i < allChains.Count; i++)
                {
                    _backupChains.TryRemove(allChains[i].Key, out _);
                }
            }

            // Enforce size limit
            if (_retentionPolicy.MaxTotalBytes > 0)
            {
                var allChains = _backupChains
                    .OrderByDescending(c => c.Value.CreatedUtc)
                    .ToList();

                long totalBytes = 0;
                foreach (var chainEntry in allChains)
                {
                    var chainBytes = chainEntry.Value.Backups.Sum(b => b.BytesProcessed);
                    totalBytes += chainBytes;

                    if (totalBytes > _retentionPolicy.MaxTotalBytes)
                    {
                        _backupChains.TryRemove(chainEntry.Key, out _);
                    }
                }
            }
        }
        catch (Exception ex)
        {

            // Best-effort retention enforcement
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates the backpressure state based on active jobs vs max concurrency.
    /// </summary>
    private void UpdateBackpressureState()
    {
        var activeJobs = _activeJobs.Count;
        var maxJobs = _maxConcurrentJobs;

        if (maxJobs <= 0)
        {
            _backpressureState = BackpressureState.Normal;
            return;
        }

        var ratio = (double)activeJobs / maxJobs;
        _backpressureState = ratio switch
        {
            >= 1.0 => BackpressureState.Shedding,
            >= 0.8 => BackpressureState.Critical,
            >= 0.6 => BackpressureState.Warning,
            _ => BackpressureState.Normal
        };
    }

    // ---------------------------------------------------------------
    // IDisposable
    // ---------------------------------------------------------------

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _retentionEnforcementTimer.Dispose();
        _activeJobs.Dispose();
        _backupChains.Dispose();
    }
}

// ---------------------------------------------------------------
// Supporting Types
// ---------------------------------------------------------------

/// <summary>
/// Metadata for a backup job, persisted to <see cref="IPersistentBackingStore"/>
/// for crash recovery. Tracks job lifecycle from start to completion or failure.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-11: Backup job metadata for crash recovery")]
public sealed record BackupJobMetadata
{
    /// <summary>Gets the unique identifier for this backup job.</summary>
    public required string JobId { get; init; }

    /// <summary>Gets the type of backup (Full, Incremental, Differential).</summary>
    public required BackupType BackupType { get; init; }

    /// <summary>Gets the source path being backed up.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Gets the current status of the backup job.</summary>
    public required BackupJobStatus Status { get; init; }

    /// <summary>Gets the UTC timestamp when the backup job started.</summary>
    public required DateTime StartedUtc { get; init; }

    /// <summary>Gets the UTC timestamp when the backup job completed. Null if still in progress.</summary>
    public DateTime? CompletedUtc { get; init; }

    /// <summary>Gets the total bytes processed by this backup job.</summary>
    public long BytesProcessed { get; init; }

    /// <summary>Gets the error message if the job failed. Null if not failed.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Type of backup operation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-11: Backup type enumeration")]
public enum BackupType
{
    /// <summary>Full backup of all data.</summary>
    Full,

    /// <summary>Incremental backup of changes since the last backup.</summary>
    Incremental,

    /// <summary>Differential backup of changes since the last full backup.</summary>
    Differential
}

/// <summary>
/// Status of a backup job in its lifecycle.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-11: Backup job status enumeration")]
public enum BackupJobStatus
{
    /// <summary>Job is currently running.</summary>
    InProgress,

    /// <summary>Job completed successfully.</summary>
    Completed,

    /// <summary>Job failed with an error.</summary>
    Failed,

    /// <summary>Job was cancelled by the user or system.</summary>
    Cancelled,

    /// <summary>Job is marked for recovery after a crash.</summary>
    PendingRecovery
}

/// <summary>
/// Represents a backup chain consisting of a full backup and its associated incrementals.
/// Chains are tracked for retention management and dependency resolution.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-11: Backup chain for retention management")]
public sealed record BackupChain
{
    /// <summary>Gets the unique identifier for this backup chain.</summary>
    public required string ChainId { get; init; }

    /// <summary>Gets the UTC timestamp when this chain was created.</summary>
    public required DateTime CreatedUtc { get; init; }

    /// <summary>Gets the ordered list of backups in this chain.</summary>
    public required List<BackupChainEntry> Backups { get; init; }
}

/// <summary>
/// An entry in a backup chain, representing a single completed backup.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-11: Backup chain entry")]
public sealed record BackupChainEntry
{
    /// <summary>Gets the backup job identifier.</summary>
    public required string JobId { get; init; }

    /// <summary>Gets the type of backup.</summary>
    public required BackupType BackupType { get; init; }

    /// <summary>Gets the UTC timestamp when this backup completed.</summary>
    public required DateTime CompletedUtc { get; init; }

    /// <summary>Gets the total bytes in this backup.</summary>
    public required long BytesProcessed { get; init; }

    /// <summary>Gets the source path that was backed up.</summary>
    public required string SourcePath { get; init; }
}

/// <summary>
/// Configures retention policies for backup chains. Supports retention by count,
/// age, and total size. Multiple policies can be active simultaneously.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-11: Backup retention policy configuration")]
public sealed record RetentionPolicy
{
    /// <summary>
    /// Gets the maximum number of backup chains to retain. 0 means unlimited.
    /// Default: 30.
    /// </summary>
    public int MaxCount { get; init; } = 30;

    /// <summary>
    /// Gets the maximum age in days for backup chains. Chains older than this are removed.
    /// 0 means unlimited. Default: 90.
    /// </summary>
    public int MaxAgeDays { get; init; } = 90;

    /// <summary>
    /// Gets the maximum total bytes across all retained backup chains.
    /// Oldest chains are removed when this limit is exceeded. 0 means unlimited.
    /// Default: 100 GB.
    /// </summary>
    public long MaxTotalBytes { get; init; } = 100L * 1024 * 1024 * 1024;
}
