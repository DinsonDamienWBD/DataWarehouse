using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Storage.Placement;

/// <summary>
/// Background rebalancing service that executes rebalance plans by moving objects
/// between nodes. Supports pause/resume/cancel and real-time progress monitoring.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public interface IRebalancer
{
    /// <summary>
    /// Starts executing a rebalance plan, moving objects between nodes as specified.
    /// </summary>
    /// <param name="plan">The rebalance plan to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created rebalance job with initial status.</returns>
    Task<RebalanceJob> StartRebalanceAsync(RebalancePlan plan, CancellationToken ct = default);

    /// <summary>
    /// Pauses an in-progress rebalance job. Moves already in flight will complete,
    /// but no new moves will be started.
    /// </summary>
    /// <param name="jobId">The ID of the job to pause.</param>
    /// <param name="ct">Cancellation token.</param>
    Task PauseRebalanceAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    /// Resumes a previously paused rebalance job from where it left off.
    /// </summary>
    /// <param name="jobId">The ID of the job to resume.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ResumeRebalanceAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    /// Cancels a rebalance job. Moves already completed are not rolled back.
    /// </summary>
    /// <param name="jobId">The ID of the job to cancel.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CancelRebalanceAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    /// Gets the current status of a rebalance job.
    /// </summary>
    /// <param name="jobId">The ID of the job to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current state of the rebalance job.</returns>
    Task<RebalanceJob> GetStatusAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    /// Lists all rebalance jobs (active and completed).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All known rebalance jobs.</returns>
    Task<IReadOnlyList<RebalanceJob>> ListJobsAsync(CancellationToken ct = default);

    /// <summary>
    /// Monitors a rebalance job in real-time, yielding updated job state as moves complete.
    /// </summary>
    /// <param name="jobId">The ID of the job to monitor.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async stream of job status updates.</returns>
    IAsyncEnumerable<RebalanceJob> MonitorAsync(string jobId, CancellationToken ct = default);
}
