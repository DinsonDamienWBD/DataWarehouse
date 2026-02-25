using DataWarehouse.SDK.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Infrastructure.Distributed
{
    /// <summary>
    /// Defines the contract for a distributed locking service providing lease-based mutual exclusion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default in-process implementation (<see cref="DistributedLockService"/>) provides mutual
    /// exclusion within a single process. For true cross-node distributed locking, integrate with the
    /// <c>RaftConsensusEngine</c> to achieve consensus on lock state across cluster nodes.
    /// </para>
    /// <para>
    /// All locks use a lease-based model: if the owner crashes or loses connectivity before calling
    /// <see cref="ReleaseAsync"/>, the lock will automatically expire after the lease period,
    /// preventing permanent resource starvation.
    /// </para>
    /// </remarks>
    [SdkCompatibility("5.0.0", Notes = "Phase 65.2: Distributed locking contract")]
    public interface IDistributedLockService
    {
        /// <summary>
        /// Attempts to acquire a lock for the specified resource.
        /// </summary>
        /// <param name="lockId">The unique identifier for the resource to lock.</param>
        /// <param name="owner">The identifier of the caller requesting the lock (e.g., node ID, session ID).</param>
        /// <param name="leaseTime">The duration of the lock lease. The lock auto-expires after this period.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>
        /// A <see cref="DistributedLock"/> if the lock was successfully acquired; null if the lock is
        /// currently held by another owner and has not expired.
        /// </returns>
        Task<DistributedLock?> TryAcquireAsync(string lockId, string owner, TimeSpan leaseTime, CancellationToken ct = default);

        /// <summary>
        /// Attempts to renew the lease on an existing lock.
        /// Only the current owner may renew the lock.
        /// </summary>
        /// <param name="lockId">The unique identifier for the resource.</param>
        /// <param name="owner">The identifier of the owner renewing the lock. Must match the original acquirer.</param>
        /// <param name="newLeaseTime">The new lease duration from the time of renewal.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if the renewal succeeded; false if the lock is not held or is held by a different owner.</returns>
        Task<bool> TryRenewAsync(string lockId, string owner, TimeSpan newLeaseTime, CancellationToken ct = default);

        /// <summary>
        /// Releases a lock held by the specified owner.
        /// Only the current owner may release the lock.
        /// </summary>
        /// <param name="lockId">The unique identifier for the resource.</param>
        /// <param name="owner">The identifier of the owner releasing the lock. Must match the original acquirer.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if the lock was released; false if the lock is not held or is held by a different owner.</returns>
        Task<bool> ReleaseAsync(string lockId, string owner, CancellationToken ct = default);

        /// <summary>
        /// Gets information about a currently held lock.
        /// </summary>
        /// <param name="lockId">The unique identifier for the resource.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The lock record if a non-expired lock exists; null if the lock is not held or has expired.</returns>
        Task<DistributedLock?> GetLockInfoAsync(string lockId, CancellationToken ct = default);

        /// <summary>
        /// Removes all expired lock entries from storage.
        /// This is a maintenance operation and should be called periodically (e.g., via a background job).
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        Task CleanupExpiredLocksAsync(CancellationToken ct = default);
    }
}
