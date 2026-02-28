using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DataWarehouse.Plugins.UniversalFabric.Migration;

/// <summary>
/// Represents a live migration job that moves data between storage backends.
/// Tracks progress, supports pause/resume, and handles partial failures with
/// thread-safe counters for concurrent object transfers.
/// </summary>
public class MigrationJob
{
    /// <summary>
    /// Unique identifier for this migration job.
    /// </summary>
    public string JobId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// The backend ID to migrate data from.
    /// </summary>
    public required string SourceBackendId { get; init; }

    /// <summary>
    /// The backend ID to migrate data to.
    /// </summary>
    public required string DestinationBackendId { get; init; }

    /// <summary>
    /// Optional prefix filter: only objects matching this prefix are migrated.
    /// </summary>
    public string? SourcePrefix { get; init; }

    /// <summary>
    /// Whether to copy or move (copy + delete source) objects.
    /// </summary>
    public MigrationMode Mode { get; init; } = MigrationMode.Copy;

    // Status and timestamp fields use volatile int/long backing to provide memory barriers
    // for cross-thread visibility (finding 4532).
    private volatile int _statusValue = (int)MigrationJobStatus.Pending;
    private long _startedAtTicks = 0;
    private long _completedAtTicks = 0;

    /// <summary>
    /// Current status of this migration job.
    /// </summary>
    public MigrationJobStatus Status
    {
        get => (MigrationJobStatus)_statusValue;
        private set => _statusValue = (int)value;
    }

    /// <summary>
    /// When this job was created.
    /// </summary>
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    /// <summary>
    /// When this job started executing.
    /// </summary>
    public DateTime? StartedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _startedAtTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
        private set => Interlocked.Exchange(ref _startedAtTicks, value?.Ticks ?? 0);
    }

    /// <summary>
    /// When this job completed (success, failure, or cancellation).
    /// </summary>
    public DateTime? CompletedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _completedAtTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
        private set => Interlocked.Exchange(ref _completedAtTicks, value?.Ticks ?? 0);
    }

    /// <summary>
    /// Error message if the job failed.
    /// </summary>
    public string? ErrorMessage { get; private set; }

    // --- Progress (exposed via properties reading from Interlocked-backed fields) ---

    /// <summary>
    /// Total number of objects to migrate.
    /// </summary>
    public long TotalObjects => Interlocked.Read(ref _totalObjects);

    /// <summary>
    /// Number of objects successfully migrated.
    /// </summary>
    public long MigratedObjects => Interlocked.Read(ref _migratedObjects);

    /// <summary>
    /// Number of objects that failed to migrate.
    /// </summary>
    public long FailedObjects => Interlocked.Read(ref _failedObjects);

    /// <summary>
    /// Number of objects skipped (already exist at destination).
    /// </summary>
    public long SkippedObjects => Interlocked.Read(ref _skippedObjects);

    /// <summary>
    /// Total bytes across all objects to migrate.
    /// </summary>
    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    /// <summary>
    /// Total bytes successfully migrated.
    /// </summary>
    public long MigratedBytes => Interlocked.Read(ref _migratedBytes);

    // --- Configuration ---

    /// <summary>
    /// Maximum number of parallel object transfers.
    /// </summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>
    /// Maximum number of retries per object before recording failure.
    /// </summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Whether to verify integrity (size check) after copying each object.
    /// </summary>
    public bool VerifyAfterCopy { get; init; } = true;

    /// <summary>
    /// Whether to delete the source object after verified copy (only effective in Move mode).
    /// </summary>
    public bool DeleteSourceAfterVerify { get; init; }

    /// <summary>
    /// Whether to skip objects that already exist at the destination.
    /// </summary>
    public bool SkipExisting { get; init; } = true;

    // --- Failure tracking ---

    private readonly ConcurrentBag<MigrationFailure> _failures = new();

    /// <summary>
    /// List of objects that failed to migrate with their error details.
    /// </summary>
    public IReadOnlyList<MigrationFailure> Failures => _failures.ToList();

    // --- State transitions ---

    /// <summary>
    /// Transitions the job to Running state.
    /// </summary>
    public void Start()
    {
        Status = MigrationJobStatus.Running;
        StartedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Transitions the job to Paused state.
    /// </summary>
    public void Pause()
    {
        if (Status == MigrationJobStatus.Running)
            Status = MigrationJobStatus.Paused;
    }

    /// <summary>
    /// Resumes a paused job back to Running state.
    /// </summary>
    public void Resume()
    {
        if (Status == MigrationJobStatus.Paused)
            Status = MigrationJobStatus.Running;
    }

    /// <summary>
    /// Marks the job as successfully completed.
    /// </summary>
    public void Complete()
    {
        Status = MigrationJobStatus.Completed;
        CompletedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Marks the job as failed with an error message.
    /// </summary>
    public void Fail(string error)
    {
        Status = MigrationJobStatus.Failed;
        ErrorMessage = error;
        CompletedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Marks the job as cancelled.
    /// </summary>
    public void Cancel()
    {
        Status = MigrationJobStatus.Cancelled;
        CompletedAt = DateTime.UtcNow;
    }

    // --- Thread-safe progress updates ---

    /// <summary>
    /// Records a successfully migrated object and its byte count.
    /// </summary>
    public void RecordMigrated(long bytes)
    {
        Interlocked.Increment(ref _migratedObjects);
        Interlocked.Add(ref _migratedBytes, bytes);
    }

    /// <summary>
    /// Records a failed object migration with the key and error message.
    /// </summary>
    public void RecordFailed(string key, string error)
    {
        Interlocked.Increment(ref _failedObjects);
        _failures.Add(new MigrationFailure(key, error));
    }

    /// <summary>
    /// Records a skipped object (already exists at destination).
    /// </summary>
    public void RecordSkipped()
    {
        Interlocked.Increment(ref _skippedObjects);
    }

    /// <summary>
    /// Sets the total object count and byte count for this migration.
    /// Called once after enumeration of source objects.
    /// </summary>
    public void SetTotal(long objects, long bytes)
    {
        Interlocked.Exchange(ref _totalObjects, objects);
        Interlocked.Exchange(ref _totalBytes, bytes);
    }

    // --- Private backing fields for Interlocked ---
    private long _migratedObjects;
    private long _failedObjects;
    private long _skippedObjects;
    private long _totalObjects;
    private long _totalBytes;
    private long _migratedBytes;
}

/// <summary>
/// Status of a migration job.
/// </summary>
public enum MigrationJobStatus
{
    /// <summary>Job created but not yet started.</summary>
    Pending,
    /// <summary>Job is actively migrating objects.</summary>
    Running,
    /// <summary>Job is paused and waiting to be resumed.</summary>
    Paused,
    /// <summary>Job completed successfully.</summary>
    Completed,
    /// <summary>Job failed with an error.</summary>
    Failed,
    /// <summary>Job was cancelled by the user.</summary>
    Cancelled
}

/// <summary>
/// Whether to copy or move objects during migration.
/// </summary>
public enum MigrationMode
{
    /// <summary>Copy objects to destination, leaving source intact.</summary>
    Copy,
    /// <summary>Move objects to destination, deleting source after verified copy.</summary>
    Move
}

/// <summary>
/// Records a single object migration failure with the key and error details.
/// </summary>
public record MigrationFailure(string Key, string Error);
