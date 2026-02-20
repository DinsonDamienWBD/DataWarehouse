using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.SemanticSync;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.SemanticSync.Strategies.Fidelity;

/// <summary>
/// Thread-safe, lock-free bandwidth budget tracker that maintains real-time accounting
/// of bandwidth consumption across fidelity levels. Uses <see cref="Interlocked"/> operations
/// and <see cref="ConcurrentDictionary{TKey,TValue}"/> for production-grade concurrency
/// without any lock statements.
/// </summary>
/// <remarks>
/// <para>
/// The tracker operates on a 60-second sliding window. When the window expires, consumption
/// counters reset automatically on the next measurement update. Budget utilization is computed
/// as the ratio of bytes consumed within the current window to the total bandwidth capacity
/// available during that window (currentBandwidthBps * 60 seconds).
/// </para>
/// <para>
/// All public methods are safe for concurrent access from multiple threads. Pending sync
/// counts use <see cref="Interlocked.Increment(ref int)"/> and <see cref="Interlocked.Decrement(ref int)"/>.
/// Bandwidth and window state use <see cref="Interlocked.Exchange(ref long, long)"/> and
/// <see cref="Interlocked.Read(ref long)"/> for atomic 64-bit reads/writes.
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic conflict resolution")]
internal sealed class BandwidthBudgetTracker
{
    /// <summary>
    /// Duration of the measurement window in seconds.
    /// Budget utilization is computed relative to this window size.
    /// </summary>
    private const long WindowDurationSeconds = 60;

    /// <summary>
    /// Duration of the measurement window in ticks (60 seconds).
    /// Used for atomic comparisons against <see cref="_windowStartTicks"/>.
    /// </summary>
    private static readonly long WindowDurationTicks = TimeSpan.FromSeconds(WindowDurationSeconds).Ticks;

    /// <summary>
    /// Current measured bandwidth in bytes per second. Updated atomically via
    /// <see cref="Interlocked.Exchange(ref long, long)"/>.
    /// </summary>
    private long _currentBandwidthBps;

    /// <summary>
    /// Tracks bytes consumed per fidelity level within the current measurement window.
    /// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
    /// </summary>
    private readonly BoundedDictionary<SyncFidelity, long> _bytesConsumedByFidelity = new BoundedDictionary<SyncFidelity, long>(1000);

    /// <summary>
    /// Number of sync operations currently queued but not yet completed.
    /// Modified atomically via <see cref="Interlocked.Increment(ref int)"/> and
    /// <see cref="Interlocked.Decrement(ref int)"/>.
    /// </summary>
    private int _pendingSyncCount;

    /// <summary>
    /// Tick count when the current measurement window started.
    /// Used for window expiration checks. Accessed atomically via
    /// <see cref="Interlocked.Read(ref long)"/> and <see cref="Interlocked.Exchange(ref long, long)"/>.
    /// </summary>
    private long _windowStartTicks;

    /// <summary>
    /// Total bytes consumed within the current measurement window across all fidelity levels.
    /// Accessed atomically via <see cref="Interlocked.Read(ref long)"/> and
    /// <see cref="Interlocked.Add(ref long, long)"/>.
    /// </summary>
    private long _windowBytesConsumed;

    /// <summary>
    /// Initializes a new instance of the <see cref="BandwidthBudgetTracker"/> class
    /// with the measurement window starting at the current time.
    /// </summary>
    public BandwidthBudgetTracker()
    {
        _windowStartTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>
    /// Updates the current bandwidth measurement and resets the measurement window
    /// if the current window has exceeded 60 seconds.
    /// </summary>
    /// <param name="currentBps">The measured bandwidth in bytes per second.</param>
    public void UpdateBandwidth(long currentBps)
    {
        Interlocked.Exchange(ref _currentBandwidthBps, currentBps);

        long windowStart = Interlocked.Read(ref _windowStartTicks);
        long elapsed = DateTime.UtcNow.Ticks - windowStart;

        if (elapsed > WindowDurationTicks)
        {
            // Window expired -- reset counters atomically.
            // Race conditions here are benign: worst case two threads both reset,
            // which just starts a fresh window slightly earlier.
            Interlocked.Exchange(ref _windowStartTicks, DateTime.UtcNow.Ticks);
            Interlocked.Exchange(ref _windowBytesConsumed, 0);
            _bytesConsumedByFidelity.Clear();
        }
    }

    /// <summary>
    /// Records that <paramref name="bytes"/> were consumed at the given <paramref name="fidelity"/> level.
    /// Increments both the per-fidelity counter and the total window consumption counter.
    /// </summary>
    /// <param name="fidelity">The fidelity level at which the bytes were consumed.</param>
    /// <param name="bytes">The number of bytes consumed.</param>
    public void RecordConsumption(SyncFidelity fidelity, long bytes)
    {
        _bytesConsumedByFidelity.AddOrUpdate(fidelity, bytes, (_, existing) => existing + bytes);
        Interlocked.Add(ref _windowBytesConsumed, bytes);
    }

    /// <summary>
    /// Records that a sync operation has been queued. Thread-safe via <see cref="Interlocked.Increment(ref int)"/>.
    /// </summary>
    public void RecordSyncQueued()
    {
        Interlocked.Increment(ref _pendingSyncCount);
    }

    /// <summary>
    /// Records that a sync operation has completed. Thread-safe via <see cref="Interlocked.Decrement(ref int)"/>.
    /// The pending count is clamped to never go below zero.
    /// </summary>
    public void RecordSyncCompleted()
    {
        // Use CompareExchange loop to prevent going below zero
        int current;
        do
        {
            current = Volatile.Read(ref _pendingSyncCount);
            if (current <= 0) return;
        }
        while (Interlocked.CompareExchange(ref _pendingSyncCount, current - 1, current) != current);
    }

    /// <summary>
    /// Computes and returns the current bandwidth budget state as a <see cref="FidelityBudget"/> snapshot.
    /// </summary>
    /// <returns>
    /// A <see cref="FidelityBudget"/> containing the current bandwidth, pending sync count,
    /// budget utilization percentage, and fidelity distribution.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Budget utilization is calculated as:
    /// <c>(windowBytesConsumed / (currentBandwidthBps * 60)) * 100</c>.
    /// If bandwidth is zero, utilization is reported as 100% (fully constrained).
    /// </para>
    /// <para>
    /// The fidelity distribution maps each <see cref="SyncFidelity"/> level to the number
    /// of bytes consumed at that level, cast to int for the <see cref="FidelityBudget"/> contract.
    /// </para>
    /// </remarks>
    public FidelityBudget GetCurrentBudget()
    {
        long bandwidth = Interlocked.Read(ref _currentBandwidthBps);
        long consumed = Interlocked.Read(ref _windowBytesConsumed);
        int pending = Volatile.Read(ref _pendingSyncCount);

        // Compute utilization as percentage of 60-second window capacity
        double utilizationPercent;
        if (bandwidth <= 0)
        {
            utilizationPercent = consumed > 0 ? 100.0 : 0.0;
        }
        else
        {
            long windowCapacity = bandwidth * WindowDurationSeconds;
            utilizationPercent = Math.Min(100.0, (double)consumed / windowCapacity * 100.0);
        }

        // Build fidelity distribution from per-fidelity byte counts
        var distribution = new Dictionary<SyncFidelity, int>();
        foreach (var kvp in _bytesConsumedByFidelity)
        {
            distribution[kvp.Key] = (int)Math.Min(kvp.Value, int.MaxValue);
        }

        return new FidelityBudget(
            AvailableBandwidthBps: bandwidth,
            PendingSyncCount: Math.Max(0, pending),
            BudgetUtilizationPercent: Math.Round(utilizationPercent, 2),
            FidelityDistribution: distribution);
    }

    /// <summary>
    /// Gets the remaining budget capacity as a percentage (0-100), representing how much
    /// of the 60-second window's bandwidth capacity is still available.
    /// </summary>
    /// <returns>The remaining capacity percentage, clamped between 0.0 and 100.0.</returns>
    public double GetRemainingCapacityPercent()
    {
        var budget = GetCurrentBudget();
        return Math.Clamp(100.0 - budget.BudgetUtilizationPercent, 0.0, 100.0);
    }
}
