using DataWarehouse.SDK.Contracts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Infrastructure.Distributed
{
    /// <summary>
    /// In-process lease-based implementation of <see cref="IDistributedLockService"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This implementation provides mutual exclusion within a single process.
    /// For cross-node distributed locking, integrate with the <c>RaftConsensusEngine</c>:
    /// use Raft log replication to achieve cluster-wide consensus on lock acquisition and release,
    /// then delegate to this service for the in-process lock table.
    /// </para>
    /// <para>
    /// Locks use a lease model: each lock has an expiry time. If the owner crashes or becomes
    /// unavailable, the lock expires automatically after the lease period without requiring
    /// an explicit release. This prevents permanent resource deadlock under failure conditions.
    /// </para>
    /// <para>
    /// Thread safety: all operations are serialized via a <see cref="SemaphoreSlim"/>.
    /// </para>
    /// </remarks>
    [SdkCompatibility("5.0.0", Notes = "Phase 65.2: Distributed locking service migrated from obsolete Raft plugin")]
    public sealed class DistributedLockService : IDistributedLockService
    {
        private readonly Dictionary<string, DistributedLock> _locks = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private readonly ILogger<DistributedLockService>? _logger;

        /// <summary>
        /// Initializes a new instance of <see cref="DistributedLockService"/>.
        /// </summary>
        /// <param name="logger">Optional logger for observability. If null, diagnostic events are silently dropped.</param>
        public DistributedLockService(ILogger<DistributedLockService>? logger = null)
        {
            _logger = logger;
        }

        /// <inheritdoc/>
        public async Task<DistributedLock?> TryAcquireAsync(
            string lockId,
            string owner,
            TimeSpan leaseTime,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(lockId);
            ArgumentException.ThrowIfNullOrWhiteSpace(owner);

            if (leaseTime <= TimeSpan.Zero)
                throw new ArgumentException("Lease time must be a positive duration.", nameof(leaseTime));

            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // If a non-expired lock exists for a different owner, deny acquisition
                if (_locks.TryGetValue(lockId, out var existing) && !existing.IsExpired)
                {
                    _logger?.LogDebug("[DistributedLock] Acquisition denied: {LockId} held by {Existing} until {Expiry}",
                        lockId, existing.Owner, existing.ExpiresAt);
                    return null;
                }

                // Create and store the new lock (overwriting any expired lock for this key)
                var acquired = new DistributedLock(lockId, owner, DateTimeOffset.UtcNow, leaseTime);
                _locks[lockId] = acquired;

                _logger?.LogInformation("[DistributedLock] Acquired: {LockId} by {Owner} for {LeaseTime}",
                    lockId, owner, leaseTime);

                return acquired;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<bool> TryRenewAsync(
            string lockId,
            string owner,
            TimeSpan newLeaseTime,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(lockId);
            ArgumentException.ThrowIfNullOrWhiteSpace(owner);

            if (newLeaseTime <= TimeSpan.Zero)
                throw new ArgumentException("New lease time must be a positive duration.", nameof(newLeaseTime));

            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_locks.TryGetValue(lockId, out var existing) || existing.IsExpired)
                {
                    _logger?.LogDebug("[DistributedLock] Renewal failed (not held or expired): {LockId}", lockId);
                    return false;
                }

                if (!string.Equals(existing.Owner, owner, StringComparison.Ordinal))
                {
                    _logger?.LogWarning("[DistributedLock] Renewal denied: {LockId} owned by {Actual}, requested by {Requested}",
                        lockId, existing.Owner, owner);
                    return false;
                }

                // Replace the lock record with a fresh lease from now
                _locks[lockId] = new DistributedLock(lockId, owner, DateTimeOffset.UtcNow, newLeaseTime);

                _logger?.LogDebug("[DistributedLock] Renewed: {LockId} by {Owner} for {LeaseTime}",
                    lockId, owner, newLeaseTime);

                return true;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<bool> ReleaseAsync(
            string lockId,
            string owner,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(lockId);
            ArgumentException.ThrowIfNullOrWhiteSpace(owner);

            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_locks.TryGetValue(lockId, out var existing))
                {
                    _logger?.LogDebug("[DistributedLock] Release: {LockId} not found (already released or expired)", lockId);
                    return false;
                }

                if (!string.Equals(existing.Owner, owner, StringComparison.Ordinal))
                {
                    _logger?.LogWarning("[DistributedLock] Release denied: {LockId} owned by {Actual}, release attempted by {Requested}",
                        lockId, existing.Owner, owner);
                    return false;
                }

                _locks.Remove(lockId);

                _logger?.LogInformation("[DistributedLock] Released: {LockId} by {Owner}", lockId, owner);

                return true;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<DistributedLock?> GetLockInfoAsync(
            string lockId,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(lockId);

            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_locks.TryGetValue(lockId, out var existing) || existing.IsExpired)
                    return null;

                return existing;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <inheritdoc/>
        public async Task CleanupExpiredLocksAsync(CancellationToken ct = default)
        {
            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var toRemove = new List<string>();
                foreach (var (lockId, lck) in _locks)
                {
                    if (lck.IsExpired)
                        toRemove.Add(lockId);
                }

                foreach (var lockId in toRemove)
                {
                    _locks.Remove(lockId);
                }

                if (toRemove.Count > 0)
                {
                    _logger?.LogInformation("[DistributedLock] Cleanup: removed {Count} expired lock(s)", toRemove.Count);
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }
}
