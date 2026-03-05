using System;

namespace DataWarehouse.Plugins.UniversalFabric.Migration;

/// <summary>
/// Immutable snapshot of migration job progress, including completion percentage,
/// throughput, and estimated time remaining.
/// </summary>
public record MigrationProgress
{
    /// <summary>
    /// The job ID this progress report belongs to.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Current status of the migration job.
    /// </summary>
    public required MigrationJobStatus Status { get; init; }

    /// <summary>
    /// Total number of objects to migrate.
    /// </summary>
    public required long TotalObjects { get; init; }

    /// <summary>
    /// Number of objects successfully migrated so far.
    /// </summary>
    public required long MigratedObjects { get; init; }

    /// <summary>
    /// Number of objects that failed to migrate.
    /// </summary>
    public required long FailedObjects { get; init; }

    /// <summary>
    /// Number of objects skipped (already exist at destination).
    /// </summary>
    public required long SkippedObjects { get; init; }

    /// <summary>
    /// Total bytes across all objects to migrate.
    /// </summary>
    public required long TotalBytes { get; init; }

    /// <summary>
    /// Total bytes successfully migrated so far.
    /// </summary>
    public required long MigratedBytes { get; init; }

    /// <summary>
    /// Percentage of migration completed (0-100), based on object count.
    /// </summary>
    public double PercentComplete => TotalObjects > 0
        ? (MigratedObjects + SkippedObjects) * 100.0 / TotalObjects
        : 0;

    /// <summary>
    /// Time elapsed since the migration started.
    /// </summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Average throughput in bytes per second.
    /// </summary>
    public double BytesPerSecond => Elapsed.TotalSeconds > 0
        ? MigratedBytes / Elapsed.TotalSeconds
        : 0;

    /// <summary>
    /// Estimated time remaining based on current throughput.
    /// Null if throughput cannot be calculated yet.
    /// </summary>
    public TimeSpan? EstimatedRemaining
    {
        get
        {
            // Finding 4551: guard against MigratedBytes > TotalBytes (e.g., due to torn read)
            // and BytesPerSecond == 0 (division by zero / infinite time).
            if (BytesPerSecond <= 0) return null;
            var remaining = TotalBytes - MigratedBytes;
            if (remaining <= 0) return TimeSpan.Zero;
            return TimeSpan.FromSeconds(remaining / BytesPerSecond);
        }
    }
}
