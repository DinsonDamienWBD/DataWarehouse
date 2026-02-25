using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Storage.Migration;

/// <summary>
/// Engine for zero-downtime background migration of objects between storage nodes.
/// Supports pause/resume/cancel, real-time monitoring, and read-forwarding during migration.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public interface IMigrationEngine
{
    /// <summary>
    /// Starts a migration job based on the provided plan.
    /// </summary>
    /// <param name="plan">The migration plan specifying source, target, and objects to move.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created migration job with initial status.</returns>
    Task<MigrationJob> StartMigrationAsync(MigrationPlan plan, CancellationToken ct = default);

    /// <summary>
    /// Pauses an in-progress migration. Objects currently being transferred will complete,
    /// but no new transfers will be started.
    /// </summary>
    /// <param name="jobId">The ID of the migration job to pause.</param>
    /// <param name="ct">Cancellation token.</param>
    Task PauseMigrationAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    /// Resumes a previously paused migration from its last checkpoint.
    /// </summary>
    /// <param name="jobId">The ID of the migration job to resume.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ResumeMigrationAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    /// Cancels a migration job. Objects already migrated are not rolled back.
    /// Read-forwarding entries (if enabled) remain active until expiry.
    /// </summary>
    /// <param name="jobId">The ID of the migration job to cancel.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CancelMigrationAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    /// Gets the current status of a migration job.
    /// </summary>
    /// <param name="jobId">The ID of the migration job to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current state of the migration job.</returns>
    Task<MigrationJob> GetStatusAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    /// Lists migration jobs, optionally filtered by status.
    /// </summary>
    /// <param name="statusFilter">Optional status filter. Null returns all jobs.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Migration jobs matching the filter criteria.</returns>
    Task<IReadOnlyList<MigrationJob>> ListJobsAsync(
        MigrationStatus? statusFilter = null,
        CancellationToken ct = default);

    /// <summary>
    /// Monitors a migration job in real-time, yielding updated job state as objects are migrated.
    /// </summary>
    /// <param name="jobId">The ID of the migration job to monitor.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async stream of migration job status updates.</returns>
    IAsyncEnumerable<MigrationJob> MonitorAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    /// Gets the read-forwarding entry for a specific object, if one exists.
    /// Used during migration to redirect reads from the old location to the new location.
    /// </summary>
    /// <param name="objectKey">The key of the object to look up.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The forwarding entry, or null if the object is not being forwarded.</returns>
    Task<ReadForwardingEntry?> GetForwardingEntryAsync(string objectKey, CancellationToken ct = default);
}
