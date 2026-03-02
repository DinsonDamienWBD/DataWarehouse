using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace DataWarehouse.SDK.VirtualDiskEngine.Mvcc;

/// <summary>
/// Thrown when a reader's epoch lease has expired because it exceeded the SLA timeout.
/// The reader must either re-acquire a fresh lease or handle the loss of snapshot consistency.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-19: Epoch-based vacuum (VOPT-32)")]
public sealed class EpochExpiredException : InvalidOperationException
{
    /// <summary>The epoch that was held when the lease expired.</summary>
    public long ExpiredEpoch { get; }

    /// <summary>How long the lease was actually held before expiry.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>The configured SLA lease limit that was exceeded.</summary>
    public TimeSpan Limit { get; }

    /// <summary>
    /// Creates a new <see cref="EpochExpiredException"/>.
    /// </summary>
    /// <param name="epoch">The epoch that was held when the lease expired.</param>
    /// <param name="elapsed">How long the lease was held.</param>
    /// <param name="limit">The SLA limit that was exceeded.</param>
    public EpochExpiredException(long epoch, TimeSpan elapsed, TimeSpan limit)
        : base($"Epoch lease {epoch} expired: held {elapsed.TotalSeconds:F1}s, limit {limit.TotalSeconds:F0}s.")
    {
        ExpiredEpoch = epoch;
        Elapsed = elapsed;
        Limit = limit;
    }
}

/// <summary>
/// Represents a reader's active epoch lease, capturing when it was acquired and whether
/// the snapshot data has been materialized to allow continued reading past SLA timeout.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-19: Epoch-based vacuum (VOPT-32)")]
public readonly struct ReaderLease
{
    /// <summary>Unique identifier of the reader holding this lease.</summary>
    public long ReaderId { get; init; }

    /// <summary>Global epoch captured when the lease was acquired.</summary>
    public long Epoch { get; init; }

    /// <summary>UTC timestamp when the lease was first acquired.</summary>
    public DateTimeOffset AcquiredAt { get; init; }

    /// <summary>
    /// True if the underlying snapshot data has been copied to a temporary VDE location,
    /// allowing this reader to continue past the SLA timeout without re-acquiring.
    /// </summary>
    public bool IsMaterialized { get; init; }
}

/// <summary>
/// Tracks reader epoch registrations and enforces SLA lease timeouts for the epoch-based MVCC vacuum.
/// Each reader registers at the current global epoch when it begins a long-running snapshot read.
/// The vacuum process consults <see cref="OldestActiveEpoch"/> to determine which old versions
/// are safe to reclaim — no version at or after the oldest active epoch may be collected.
/// </summary>
/// <remarks>
/// The global epoch is monotonically advanced on every transaction commit. This decouples the
/// "safe vacuum floor" from wall-clock time: the vacuum never collects a version that any
/// registered reader might still need.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87-19: Epoch-based vacuum (VOPT-32)")]
public sealed class EpochTracker
{
    private long _currentGlobalEpoch;
    private readonly ConcurrentDictionary<long, ReaderLease> _activeLeases = new();

    /// <summary>
    /// Gets the current global epoch. Monotonically increasing; advanced by
    /// <see cref="AdvanceEpoch"/> on every transaction commit.
    /// </summary>
    public long CurrentGlobalEpoch => Interlocked.Read(ref _currentGlobalEpoch);

    /// <summary>
    /// Atomically increments the global epoch and returns the new value.
    /// Should be called by <see cref="MvccManager"/> immediately after each commit.
    /// </summary>
    /// <returns>The new global epoch after incrementing.</returns>
    public long AdvanceEpoch()
    {
        return Interlocked.Increment(ref _currentGlobalEpoch);
    }

    /// <summary>
    /// Registers a reader at the current global epoch, starting an SLA-bounded lease.
    /// If a lease already exists for <paramref name="readerId"/> it is overwritten.
    /// </summary>
    /// <param name="readerId">Unique reader identifier (e.g. transaction ID).</param>
    /// <returns>The newly created <see cref="ReaderLease"/>.</returns>
    public ReaderLease AcquireReaderLease(long readerId)
    {
        var lease = new ReaderLease
        {
            ReaderId = readerId,
            Epoch = CurrentGlobalEpoch,
            AcquiredAt = DateTimeOffset.UtcNow,
            IsMaterialized = false
        };

        _activeLeases[readerId] = lease;
        return lease;
    }

    /// <summary>
    /// Releases a reader's epoch lease, allowing the vacuum to potentially advance
    /// the safe vacuum floor past the epoch this reader held.
    /// </summary>
    /// <param name="readerId">Unique reader identifier to release.</param>
    public void ReleaseReaderLease(long readerId)
    {
        _activeLeases.TryRemove(readerId, out _);
    }

    /// <summary>
    /// Gets the minimum epoch across all currently active reader leases.
    /// The vacuum must not collect any version at or after this epoch.
    /// Returns <see cref="CurrentGlobalEpoch"/> when no readers are active.
    /// </summary>
    public long OldestActiveEpoch
    {
        get
        {
            long oldest = long.MaxValue;
            foreach (var kvp in _activeLeases)
            {
                if (kvp.Value.Epoch < oldest)
                {
                    oldest = kvp.Value.Epoch;
                }
            }

            return oldest == long.MaxValue ? CurrentGlobalEpoch : oldest;
        }
    }

    /// <summary>
    /// Returns all leases that have been held longer than <paramref name="timeout"/>.
    /// These are candidates for materialization or forced expiry.
    /// </summary>
    /// <param name="timeout">Maximum allowed lease duration.</param>
    /// <returns>Snapshot list of expired leases at the time of the call.</returns>
    public IReadOnlyList<ReaderLease> GetExpiredLeases(TimeSpan timeout)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = new List<ReaderLease>();

        foreach (var kvp in _activeLeases)
        {
            var lease = kvp.Value;
            if (now - lease.AcquiredAt > timeout)
            {
                expired.Add(lease);
            }
        }

        return expired;
    }

    /// <summary>
    /// Marks a lease as materialized (snapshot data was copied to a temp VDE location).
    /// The reader may continue reading beyond the SLA timeout without incurring
    /// <see cref="EpochExpiredException"/>.
    /// </summary>
    /// <param name="readerId">Reader whose lease should be marked as materialized.</param>
    public void MarkMaterialized(long readerId)
    {
        if (_activeLeases.TryGetValue(readerId, out var existing))
        {
            _activeLeases[readerId] = existing with { IsMaterialized = true };
        }
    }

    /// <summary>
    /// Forcibly removes an expired lease. After this call the reader will receive
    /// <see cref="EpochExpiredException"/> on its next read attempt unless it has been
    /// materialized and re-registered.
    /// </summary>
    /// <param name="readerId">Unique reader identifier to expire.</param>
    public void ExpireLease(long readerId)
    {
        _activeLeases.TryRemove(readerId, out _);
    }

    /// <summary>
    /// Gets the number of currently active reader leases.
    /// </summary>
    public int ActiveReaderCount => _activeLeases.Count;
}
