using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.Infrastructure.Distributed
{
    /// <summary>
    /// Represents an acquired distributed lock with lease-based expiry semantics.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <see cref="DistributedLock"/> is immutable. To extend the lock's lifetime, call
    /// <see cref="IDistributedLockService.TryRenewAsync"/> which returns a new lock record with
    /// the updated expiry. The old record becomes stale and must be discarded.
    /// </para>
    /// <para>
    /// Callers should check <see cref="IsExpired"/> before using a cached lock reference,
    /// especially after long-running operations, to detect lease expiry under network partitions.
    /// </para>
    /// </remarks>
    [SdkCompatibility("5.0.0", Notes = "Phase 65.2")]
    public sealed class DistributedLock
    {
        /// <summary>
        /// Gets the unique identifier for this lock (the resource being locked).
        /// </summary>
        public string LockId { get; }

        /// <summary>
        /// Gets the identifier of the owner that acquired this lock (e.g., node ID, process ID, session ID).
        /// </summary>
        public string Owner { get; }

        /// <summary>
        /// Gets the UTC timestamp when this lock was acquired.
        /// </summary>
        public DateTimeOffset AcquiredAt { get; }

        /// <summary>
        /// Gets the lease duration for this lock.
        /// </summary>
        public TimeSpan LeaseTime { get; }

        /// <summary>
        /// Gets the UTC timestamp when this lock expires.
        /// </summary>
        public DateTimeOffset ExpiresAt => AcquiredAt + LeaseTime;

        /// <summary>
        /// Gets a value indicating whether this lock lease has expired.
        /// </summary>
        public bool IsExpired => DateTimeOffset.UtcNow > ExpiresAt;

        /// <summary>
        /// Initializes a new instance of <see cref="DistributedLock"/>.
        /// </summary>
        /// <param name="lockId">The unique identifier for the resource being locked.</param>
        /// <param name="owner">The identifier of the owner acquiring the lock.</param>
        /// <param name="acquiredAt">The UTC timestamp when the lock was acquired.</param>
        /// <param name="leaseTime">The duration of the lock lease.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="lockId"/> or <paramref name="owner"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="lockId"/> or <paramref name="owner"/> is empty or whitespace, or when <paramref name="leaseTime"/> is not positive.</exception>
        public DistributedLock(string lockId, string owner, DateTimeOffset acquiredAt, TimeSpan leaseTime)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(lockId);
            ArgumentException.ThrowIfNullOrWhiteSpace(owner);

            if (leaseTime <= TimeSpan.Zero)
                throw new ArgumentException("Lease time must be a positive duration.", nameof(leaseTime));

            LockId = lockId;
            Owner = owner;
            AcquiredAt = acquiredAt;
            LeaseTime = leaseTime;
        }

        /// <summary>
        /// Returns a string representation for diagnostics.
        /// </summary>
        public override string ToString() =>
            $"DistributedLock[{LockId}] owner={Owner} expires={ExpiresAt:u} expired={IsExpired}";
    }
}
