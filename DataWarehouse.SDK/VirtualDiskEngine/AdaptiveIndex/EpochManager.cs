using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Epoch-based garbage collection for safe memory reclamation in lock-free data structures.
/// </summary>
/// <remarks>
/// <para>
/// The epoch manager ensures that delta records and other objects are not reclaimed
/// while any thread might still hold a reference to them. Threads register their
/// activity by entering the current epoch and signal completion by exiting.
/// </para>
/// <para>
/// Garbage is collected when no active thread is in an epoch equal to or older than
/// the garbage's epoch. The global epoch is bumped periodically (e.g., every 256 operations)
/// to advance the reclamation frontier.
/// </para>
/// <para>
/// Thread tracking uses <see cref="ThreadLocal{T}"/> for per-thread epoch entries,
/// with a concurrent collection for cross-thread visibility during garbage collection.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-09 Epoch-based GC")]
public sealed class EpochManager : IDisposable
{
    private long _globalEpoch;
    private readonly ConcurrentQueue<(object Item, long Epoch)> _garbage;
    private readonly ConcurrentDictionary<int, EpochEntry> _threadEntries;
    private readonly ThreadLocal<EpochEntry> _localEntry;
    private long _operationCounter;
    private bool _disposed;

    /// <summary>
    /// Gets the number of operations between automatic epoch bumps.
    /// </summary>
    public int BumpInterval { get; }

    /// <summary>
    /// Gets the current global epoch value.
    /// </summary>
    public long CurrentEpoch => Interlocked.Read(ref _globalEpoch);

    /// <summary>
    /// Gets the number of garbage items awaiting collection.
    /// </summary>
    public int PendingGarbageCount => _garbage.Count;

    /// <summary>
    /// Initializes a new epoch manager.
    /// </summary>
    /// <param name="bumpInterval">Number of Enter/Exit cycles before automatic epoch bump. Defaults to 256.</param>
    public EpochManager(int bumpInterval = 256)
    {
        if (bumpInterval <= 0)
            throw new ArgumentOutOfRangeException(nameof(bumpInterval), "Bump interval must be positive.");

        BumpInterval = bumpInterval;
        _globalEpoch = 0;
        _garbage = new ConcurrentQueue<(object, long)>();
        _threadEntries = new ConcurrentDictionary<int, EpochEntry>();
        _localEntry = new ThreadLocal<EpochEntry>(
            () =>
            {
                var entry = new EpochEntry();
                _threadEntries[Environment.CurrentManagedThreadId] = entry;
                return entry;
            },
            trackAllValues: true);
        _operationCounter = 0;
    }

    /// <summary>
    /// Enters the current epoch, protecting the calling thread from garbage collection
    /// of objects in the current and newer epochs.
    /// </summary>
    /// <remarks>
    /// Must be paired with a call to <see cref="Exit"/>. Typically used as:
    /// <code>
    /// _epochManager.Enter();
    /// try { /* lock-free operations */ }
    /// finally { _epochManager.Exit(); }
    /// </code>
    /// </remarks>
    public void Enter()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var entry = _localEntry.Value!;
        long epoch = Interlocked.Read(ref _globalEpoch);
        entry.SetEpoch(epoch);
    }

    /// <summary>
    /// Exits the current epoch, allowing garbage collection to proceed for epochs
    /// no longer protected by any active thread.
    /// </summary>
    /// <remarks>
    /// Also triggers periodic epoch bumps and garbage collection attempts.
    /// </remarks>
    public void Exit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var entry = _localEntry.Value!;
        entry.Clear();

        // Periodically bump epoch and attempt collection
        long count = Interlocked.Increment(ref _operationCounter);
        if (count % BumpInterval == 0)
        {
            BumpEpoch();
            TryCollect();
        }
    }

    /// <summary>
    /// Registers an object for garbage collection at the current epoch.
    /// </summary>
    /// <param name="garbage">The object to reclaim when safe.</param>
    /// <param name="epoch">The epoch at which the object became unreachable.</param>
    public void AddGarbage(object garbage, long epoch)
    {
        ArgumentNullException.ThrowIfNull(garbage);
        _garbage.Enqueue((garbage, epoch));
    }

    /// <summary>
    /// Registers an object for garbage collection at the current global epoch.
    /// </summary>
    /// <param name="garbage">The object to reclaim when safe.</param>
    public void AddGarbage(object garbage)
    {
        ArgumentNullException.ThrowIfNull(garbage);
        _garbage.Enqueue((garbage, Interlocked.Read(ref _globalEpoch)));
    }

    /// <summary>
    /// Increments the global epoch counter.
    /// </summary>
    /// <returns>The new epoch value.</returns>
    public long BumpEpoch()
    {
        return Interlocked.Increment(ref _globalEpoch);
    }

    /// <summary>
    /// Attempts to collect garbage from epochs that are no longer active.
    /// </summary>
    /// <returns>The number of items collected.</returns>
    /// <remarks>
    /// Finds the minimum epoch across all active threads. Any garbage registered
    /// at an epoch strictly less than this minimum is safe to collect.
    /// <para>
    /// <b>Performance note (Cat 13, finding 738):</b> <see cref="FindMinActiveEpoch"/> iterates
    /// all registered thread entries — O(n) in the number of concurrent threads. This is
    /// acceptable for moderate thread counts (≤256). For larger pools, replace
    /// <see cref="_threadEntries"/> with a lock-free sorted structure.
    /// </para>
    /// </remarks>
    public int TryCollect()
    {
        long minActiveEpoch = FindMinActiveEpoch();
        int collected = 0;

        // Drain garbage items that are safe to collect
        // We peek and dequeue, stopping when we find items we can't collect yet
        var retained = new List<(object Item, long Epoch)>();

        while (_garbage.TryDequeue(out var item))
        {
            if (item.Epoch < minActiveEpoch)
            {
                // Safe to collect - item's epoch is older than all active threads
                collected++;
                // Let GC handle the actual reclamation
                if (item.Item is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            else
            {
                // Not yet safe - retain for future collection
                retained.Add(item);
            }
        }

        // Re-enqueue retained items
        foreach (var item in retained)
        {
            _garbage.Enqueue(item);
        }

        return collected;
    }

    /// <summary>
    /// Finds the minimum epoch across all active (entered but not exited) threads.
    /// </summary>
    /// <returns>
    /// The minimum active epoch, or <see cref="long.MaxValue"/> if no threads are active.
    /// </returns>
    private long FindMinActiveEpoch()
    {
        long min = long.MaxValue;

        foreach (var kvp in _threadEntries)
        {
            long threadEpoch = kvp.Value.GetEpoch();
            if (threadEpoch != EpochEntry.InactiveEpoch && threadEpoch < min)
            {
                min = threadEpoch;
            }
        }

        return min;
    }

    /// <summary>
    /// Releases all resources and collects remaining garbage.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Collect all remaining garbage
        while (_garbage.TryDequeue(out var item))
        {
            if (item.Item is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        _localEntry.Dispose();
    }

    /// <summary>
    /// Per-thread epoch entry tracking the epoch at which the thread entered.
    /// </summary>
    internal sealed class EpochEntry
    {
        /// <summary>
        /// Sentinel value indicating the thread is not currently in any epoch.
        /// </summary>
        internal const long InactiveEpoch = long.MaxValue;

        private long _epoch;

        /// <summary>
        /// Initializes a new epoch entry in the inactive state.
        /// </summary>
        public EpochEntry()
        {
            _epoch = InactiveEpoch;
        }

        /// <summary>
        /// Sets the thread's active epoch.
        /// </summary>
        /// <param name="epoch">The epoch to record.</param>
        public void SetEpoch(long epoch)
        {
            Interlocked.Exchange(ref _epoch, epoch);
        }

        /// <summary>
        /// Clears the thread's active epoch (marks as inactive).
        /// </summary>
        public void Clear()
        {
            Interlocked.Exchange(ref _epoch, InactiveEpoch);
        }

        /// <summary>
        /// Gets the thread's current epoch value.
        /// </summary>
        /// <returns>The current epoch, or <see cref="InactiveEpoch"/> if not active.</returns>
        public long GetEpoch()
        {
            return Interlocked.Read(ref _epoch);
        }
    }
}
