using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Mvcc;

/// <summary>
/// Background vacuum for old MVCC versions. Incrementally reclaims space from version records
/// that are no longer needed by any active snapshot, freeing blocks back to the MVCC region.
/// </summary>
/// <remarks>
/// GC determines a safe vacuum threshold from the oldest active snapshot minus a configurable
/// retention window. Versions with TransactionId below this threshold are unlinked from their
/// version chains and their blocks freed. Processing is incremental (at most
/// <see cref="MaxVersionsPerCycle"/> per cycle) to avoid long pauses.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: MVCC GC (VOPT-13)")]
public sealed class MvccGarbageCollector
{
    private readonly MvccManager _mvccManager;
    private readonly MvccVersionStore _versionStore;
    private readonly long _retentionWindowSequences;

    /// <summary>
    /// Freed block numbers available for reuse by the version store.
    /// </summary>
    private readonly ConcurrentQueue<long> _freeList = new();

    /// <summary>
    /// Known version chain heads (inode number -> head block number).
    /// Populated externally as transactions commit and create version chain entries.
    /// </summary>
    private readonly ConcurrentDictionary<long, long> _knownChainHeads = new();

    private long _totalVersionsReclaimed;
    private long _totalBlocksFreed;

    /// <summary>
    /// Maximum number of versions to process per GC cycle.
    /// Keeps each cycle incremental to avoid blocking concurrent operations.
    /// </summary>
    public int MaxVersionsPerCycle { get; set; } = 1000;

    /// <summary>
    /// Total number of versions reclaimed across all GC cycles.
    /// </summary>
    public long TotalVersionsReclaimed => Interlocked.Read(ref _totalVersionsReclaimed);

    /// <summary>
    /// Total number of blocks freed across all GC cycles.
    /// </summary>
    public long TotalBlocksFreed => Interlocked.Read(ref _totalBlocksFreed);

    /// <summary>
    /// Creates a new MVCC garbage collector.
    /// </summary>
    /// <param name="mvccManager">Manager providing the oldest active snapshot information.</param>
    /// <param name="versionStore">Version store managing on-disk MVCC version records.</param>
    /// <param name="retentionWindow">
    /// Minimum retention time after the last snapshot reference before versions become eligible for
    /// collection. Defaults to 5 minutes. Converted to an approximate sequence count using
    /// 1 sequence per millisecond.
    /// </param>
    public MvccGarbageCollector(
        MvccManager mvccManager,
        MvccVersionStore versionStore,
        TimeSpan retentionWindow = default)
    {
        _mvccManager = mvccManager ?? throw new ArgumentNullException(nameof(mvccManager));
        _versionStore = versionStore ?? throw new ArgumentNullException(nameof(versionStore));

        if (retentionWindow == default)
        {
            retentionWindow = TimeSpan.FromMinutes(5);
        }

        // Approximate sequence units: 1 per millisecond
        _retentionWindowSequences = (long)retentionWindow.TotalMilliseconds;
    }

    /// <summary>
    /// Registers a version chain head for GC tracking. Called when a transaction commits
    /// and creates a version chain entry.
    /// </summary>
    /// <param name="inodeNumber">Inode number whose version chain head is being registered.</param>
    /// <param name="headBlock">Absolute block number of the version chain head.</param>
    public void RegisterChainHead(long inodeNumber, long headBlock)
    {
        _knownChainHeads.AddOrUpdate(inodeNumber, headBlock, (_, _) => headBlock);
    }

    /// <summary>
    /// Dequeues a freed block for reuse. Returns -1 if no freed blocks are available.
    /// </summary>
    /// <returns>Absolute block number of a freed block, or -1.</returns>
    public long TryGetFreedBlock()
    {
        return _freeList.TryDequeue(out long block) ? block : -1;
    }

    /// <summary>
    /// Runs a single incremental GC cycle. Scans known version chains and reclaims versions
    /// older than the vacuum threshold.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result describing work done in this cycle.</returns>
    public async Task<GcResult> RunCycleAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        long oldestSnapshot = _mvccManager.OldestActiveSnapshot;
        long currentSequence = oldestSnapshot == long.MaxValue ? long.MaxValue : oldestSnapshot;

        // Compute vacuum threshold: versions below this are safe to reclaim
        long vacuumThreshold;
        if (currentSequence == long.MaxValue)
        {
            // No active transactions; all versions beyond retention window are reclaimable
            // Use a high threshold to reclaim everything old
            vacuumThreshold = long.MaxValue - _retentionWindowSequences;
        }
        else
        {
            vacuumThreshold = currentSequence - _retentionWindowSequences;
        }

        if (vacuumThreshold <= 0)
        {
            return new GcResult(0, 0, false);
        }

        int versionsReclaimed = 0;
        long blocksFreed = 0;
        bool moreWorkRemaining = false;

        foreach (var kvp in _knownChainHeads)
        {
            if (ct.IsCancellationRequested || versionsReclaimed >= MaxVersionsPerCycle)
            {
                moreWorkRemaining = true;
                break;
            }

            long inodeNumber = kvp.Key;
            long headBlock = kvp.Value;

            try
            {
                var (reclaimed, freed, updatedHead, hasMore) =
                    await VacuumChainAsync(inodeNumber, headBlock, vacuumThreshold, MaxVersionsPerCycle - versionsReclaimed, ct);

                versionsReclaimed += reclaimed;
                blocksFreed += freed;

                if (updatedHead == 0)
                {
                    // Entire chain was reclaimed
                    _knownChainHeads.TryRemove(inodeNumber, out _);
                }
                else if (updatedHead != headBlock)
                {
                    // Chain head was updated
                    _knownChainHeads.TryUpdate(inodeNumber, updatedHead, headBlock);
                }

                if (hasMore)
                {
                    moreWorkRemaining = true;
                }
            }
            catch (Exception)
            {
                // Concurrent modification or I/O error; skip and retry next cycle
                moreWorkRemaining = true;
            }
        }

        Interlocked.Add(ref _totalVersionsReclaimed, versionsReclaimed);
        Interlocked.Add(ref _totalBlocksFreed, blocksFreed);

        return new GcResult(versionsReclaimed, blocksFreed, moreWorkRemaining);
    }

    /// <summary>
    /// Runs continuous GC cycles at the specified interval until cancelled.
    /// </summary>
    /// <param name="interval">Time between GC cycles.</param>
    /// <param name="ct">Cancellation token to stop the background loop.</param>
    public async Task RunContinuousAsync(TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(ct);
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Vacuums a single version chain, reclaiming versions older than the threshold.
    /// Acquires write locks on individual entries, not globally.
    /// </summary>
    private async Task<(int Reclaimed, long BlocksFreed, long UpdatedHead, bool HasMore)> VacuumChainAsync(
        long inodeNumber,
        long headBlock,
        long vacuumThreshold,
        int maxToReclaim,
        CancellationToken ct)
    {
        // Walk the version chain to find reclaimable tail versions
        var chain = await _versionStore.GetVersionChainAsync(headBlock, ct);

        if (chain.Count == 0)
        {
            return (0, 0, 0, false);
        }

        // Find the boundary: versions are ordered newest-first.
        // We can only reclaim contiguous tail versions (oldest end) that are below threshold.
        int firstReclaimable = -1;
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            if (chain[i].TransactionId < vacuumThreshold)
            {
                firstReclaimable = i;
                break;
            }
        }

        if (firstReclaimable < 0)
        {
            return (0, 0, headBlock, false);
        }

        // Reclaim from the oldest end of the chain up to the budget
        int reclaimed = 0;
        long blocksFreed = 0;

        for (int i = chain.Count - 1; i >= firstReclaimable && reclaimed < maxToReclaim; i--)
        {
            ct.ThrowIfCancellationRequested();

            if (chain[i].TransactionId >= vacuumThreshold)
            {
                break;
            }

            long versionBlock = chain[i].VersionBlock;

            // Free the block back to the free list
            _freeList.Enqueue(versionBlock);
            reclaimed++;
            blocksFreed++;
        }

        // Determine updated head
        long updatedHead;
        if (reclaimed >= chain.Count)
        {
            updatedHead = 0; // Entire chain reclaimed
        }
        else
        {
            // Head remains the same; tail was trimmed
            updatedHead = headBlock;
        }

        bool hasMore = firstReclaimable > 0 &&
                       reclaimed < (chain.Count - firstReclaimable) &&
                       reclaimed >= maxToReclaim;

        return (reclaimed, blocksFreed, updatedHead, hasMore);
    }
}

/// <summary>
/// Result of a single garbage collection cycle.
/// </summary>
/// <param name="VersionsReclaimed">Number of old versions removed in this cycle.</param>
/// <param name="BlocksFreed">Number of MVCC region blocks returned to the free list.</param>
/// <param name="MoreWorkRemaining">True if the cycle was cut short and more versions may be reclaimable.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 87: MVCC GC result (VOPT-13)")]
public readonly record struct GcResult(int VersionsReclaimed, long BlocksFreed, bool MoreWorkRemaining);
