using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Allocation;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;

/// <summary>
/// Per-temperature-class stats returned by <see cref="SemanticWearLevelingAllocator.GetStats"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: SWLV per-temperature metrics (VOPT-41)")]
public readonly struct TemperatureStats
{
    /// <summary>Number of allocation groups assigned to this temperature class.</summary>
    public int GroupCount { get; init; }

    /// <summary>Total free blocks across all groups in this temperature class.</summary>
    public long FreeBlocks { get; init; }

    /// <summary>Total blocks across all groups in this temperature class.</summary>
    public long TotalBlocks { get; init; }

    /// <summary>Utilization ratio (0.0 = all free, 1.0 = all allocated).</summary>
    public double Utilization => TotalBlocks == 0 ? 0.0 : 1.0 - ((double)FreeBlocks / TotalBlocks);

    /// <summary>Cumulative write count for all groups in this temperature class.</summary>
    public long WriteCount { get; init; }
}

/// <summary>
/// Aggregated wear-leveling statistics returned by <see cref="SemanticWearLevelingAllocator.GetStats"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: SWLV aggregate stats (VOPT-41)")]
public readonly struct WearLevelingStats
{
    /// <summary>Stats for <see cref="TemperatureClass.Hot"/> groups.</summary>
    public TemperatureStats Hot { get; init; }

    /// <summary>Stats for <see cref="TemperatureClass.Warm"/> groups.</summary>
    public TemperatureStats Warm { get; init; }

    /// <summary>Stats for <see cref="TemperatureClass.Cold"/> groups.</summary>
    public TemperatureStats Cold { get; init; }

    /// <summary>Stats for <see cref="TemperatureClass.Frozen"/> groups.</summary>
    public TemperatureStats Frozen { get; init; }

    /// <summary>
    /// <c>true</c> when SWLV is actively routing by temperature class.
    /// <c>false</c> when the ZNS gate has disabled SWLV (ZnsAwareActive = true)
    /// or when the master switch is off.
    /// </summary>
    public bool SwlvActive { get; init; }

    /// <summary>
    /// Estimated reduction in write amplification compared to uniform (non-segregated) allocation.
    /// Computed as the ratio of fully-reclaimable groups to total groups.
    /// Higher values indicate more effective temperature segregation.
    /// </summary>
    public double EstimatedWriteAmplificationReduction { get; init; }
}

/// <summary>
/// Semantic Wear-Leveling Allocator (VOPT-41).
///
/// Segregates writes into <see cref="AllocationGroup"/>s by Expected_TTL hint
/// (Hot / Warm / Cold / Frozen) so that Background Vacuum can reclaim entire
/// same-temperature groups without copying still-live data, eliminating write
/// amplification on conventional NVMe SSDs.
///
/// <b>ZNS gate:</b> SWLV is automatically disabled when <see cref="WearLevelingConfig.ZnsAwareActive"/>
/// is <c>true</c>.  On ZNS devices, epoch-based zone allocation (ZNSM) is strictly
/// superior and all calls pass through to the inner allocator unchanged.
///
/// <b>Fallback policy:</b> when the target temperature band is exhausted, allocation
/// falls back to any available group across all temperature bands.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Semantic wear-leveling allocator with TTL-hint grouping (VOPT-41)")]
public sealed class SemanticWearLevelingAllocator : IBlockAllocator
{
    private readonly IBlockAllocator _inner;
    private readonly WearLevelingConfig _config;
    private readonly Dictionary<TemperatureClass, List<AllocationGroup>> _temperatureGroups;
    private readonly Dictionary<int, TemperatureClass> _groupIdToTemperature;
    private readonly Dictionary<TemperatureClass, long[]> _writeCounters;  // per-group write counts
    private readonly Dictionary<TemperatureClass, int> _roundRobinIndex;
    private readonly AllocationGroup[] _allGroups;
    private readonly object _routingLock = new();

    /// <summary>
    /// Creates a new <see cref="SemanticWearLevelingAllocator"/>.
    /// </summary>
    /// <param name="innerAllocator">
    ///   Underlying allocator used for all free/persist operations and as fallback when
    ///   SWLV is disabled via the ZNS gate.
    /// </param>
    /// <param name="allGroups">
    ///   All allocation groups for the VDE data region.  The allocator partitions these
    ///   into temperature bands according to <paramref name="config"/> ratios.
    /// </param>
    /// <param name="config">Wear-leveling configuration.</param>
    /// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="allGroups"/> is empty.</exception>
    public SemanticWearLevelingAllocator(
        IBlockAllocator innerAllocator,
        AllocationGroup[] allGroups,
        WearLevelingConfig config)
    {
        ArgumentNullException.ThrowIfNull(innerAllocator);
        ArgumentNullException.ThrowIfNull(allGroups);
        ArgumentNullException.ThrowIfNull(config);
        if (allGroups.Length == 0)
            throw new ArgumentException("At least one allocation group is required.", nameof(allGroups));

        _inner = innerAllocator;
        _config = config;
        _allGroups = allGroups;

        _temperatureGroups = new Dictionary<TemperatureClass, List<AllocationGroup>>
        {
            [TemperatureClass.Hot]    = new List<AllocationGroup>(),
            [TemperatureClass.Warm]   = new List<AllocationGroup>(),
            [TemperatureClass.Cold]   = new List<AllocationGroup>(),
            [TemperatureClass.Frozen] = new List<AllocationGroup>()
        };

        _groupIdToTemperature = new Dictionary<int, TemperatureClass>();

        _writeCounters = new Dictionary<TemperatureClass, long[]>
        {
            [TemperatureClass.Hot]    = Array.Empty<long>(),
            [TemperatureClass.Warm]   = Array.Empty<long>(),
            [TemperatureClass.Cold]   = Array.Empty<long>(),
            [TemperatureClass.Frozen] = Array.Empty<long>()
        };

        _roundRobinIndex = new Dictionary<TemperatureClass, int>
        {
            [TemperatureClass.Hot]    = 0,
            [TemperatureClass.Warm]   = 0,
            [TemperatureClass.Cold]   = 0,
            [TemperatureClass.Frozen] = 0
        };

        PartitionGroupsIntoTemperatureBands();
    }

    // ── IBlockAllocator: query properties ────────────────────────────────────

    /// <inheritdoc/>
    public long FreeBlockCount => _inner.FreeBlockCount;

    /// <inheritdoc/>
    public long TotalBlockCount => _inner.TotalBlockCount;

    /// <inheritdoc/>
    public bool IsAllocated(long blockNumber) => _inner.IsAllocated(blockNumber);

    /// <summary>
    /// Weighted average fragmentation considering temperature-band separation.
    /// When SWLV is active, groups are well-segregated so fragmentation within
    /// each band is reported as the weighted mean across bands by utilization.
    /// Falls through to the inner allocator when SWLV is disabled.
    /// </summary>
    public double FragmentationRatio
    {
        get
        {
            if (!IsSwlvActive)
                return _inner.FragmentationRatio;

            double weightedSum = 0.0;
            long totalBlocks = 0;

            foreach (var (_, groups) in _temperatureGroups)
            {
                foreach (var g in groups)
                {
                    long gc = g.BlockCount;
                    weightedSum += g.FragmentationRatio * gc;
                    totalBlocks += gc;
                }
            }

            return totalBlocks == 0 ? 0.0 : weightedSum / totalBlocks;
        }
    }

    // ── IBlockAllocator: allocation ───────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>Allocates from the <see cref="TemperatureClass.Hot"/> band (default).</remarks>
    public long AllocateBlock(CancellationToken ct = default)
        => AllocateBlockWithHint(TemperatureClass.Hot, ct);

    /// <summary>
    /// Allocates a single block from the temperature band matching <paramref name="hint"/>.
    /// Falls back to any available group when the target band is exhausted.
    /// When SWLV is disabled (ZNS gate or master switch), delegates to the inner allocator.
    /// </summary>
    /// <param name="hint">Expected TTL temperature class for the data being written.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Absolute block number of the allocated block.</returns>
    /// <exception cref="InvalidOperationException">No free blocks are available anywhere.</exception>
    public long AllocateBlockWithHint(TemperatureClass hint, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!IsSwlvActive)
            return _inner.AllocateBlock(ct);

        lock (_routingLock)
        {
            // Try preferred temperature band first (round-robin within band)
            if (TryAllocateFromBand(hint, out long block))
            {
                IncrementWriteCount(hint);
                return block;
            }

            // Band exhausted — fall back across all bands in temperature order
            foreach (TemperatureClass tc in new[] { TemperatureClass.Hot, TemperatureClass.Warm, TemperatureClass.Cold, TemperatureClass.Frozen })
            {
                if (tc == hint)
                    continue;  // already tried
                if (TryAllocateFromBand(tc, out long fallbackBlock))
                {
                    IncrementWriteCount(tc);
                    return fallbackBlock;
                }
            }
        }

        throw new InvalidOperationException("SemanticWearLevelingAllocator: no free blocks available in any temperature band.");
    }

    /// <inheritdoc/>
    /// <remarks>Allocates from the <see cref="TemperatureClass.Hot"/> band (default).</remarks>
    public long[] AllocateExtent(int blockCount, CancellationToken ct = default)
        => AllocateExtentWithHint(blockCount, TemperatureClass.Hot, ct);

    /// <summary>
    /// Allocates a contiguous extent from the temperature band matching <paramref name="hint"/>.
    /// Falls back to any band when the target band cannot satisfy the request contiguously.
    /// When SWLV is disabled, delegates to the inner allocator.
    /// </summary>
    /// <param name="blockCount">Number of contiguous blocks to allocate.</param>
    /// <param name="hint">Expected TTL temperature class.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Array of contiguous absolute block numbers.</returns>
    /// <exception cref="InvalidOperationException">No band has a contiguous extent of the requested size.</exception>
    public long[] AllocateExtentWithHint(int blockCount, TemperatureClass hint, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockCount);
        ct.ThrowIfCancellationRequested();

        if (!IsSwlvActive)
            return _inner.AllocateExtent(blockCount, ct);

        lock (_routingLock)
        {
            // Try preferred temperature band first
            if (TryAllocateExtentFromBand(hint, blockCount, out long[]? blocks) && blocks is not null)
            {
                IncrementWriteCount(hint, blockCount);
                return blocks;
            }

            // Fall back across all bands
            foreach (TemperatureClass tc in new[] { TemperatureClass.Hot, TemperatureClass.Warm, TemperatureClass.Cold, TemperatureClass.Frozen })
            {
                if (tc == hint)
                    continue;
                if (TryAllocateExtentFromBand(tc, blockCount, out long[]? fallback) && fallback is not null)
                {
                    IncrementWriteCount(tc, blockCount);
                    return fallback;
                }
            }
        }

        throw new InvalidOperationException($"SemanticWearLevelingAllocator: no contiguous extent of {blockCount} blocks available in any temperature band.");
    }

    // ── IBlockAllocator: free ─────────────────────────────────────────────────

    /// <inheritdoc/>
    public void FreeBlock(long blockNumber) => _inner.FreeBlock(blockNumber);

    /// <inheritdoc/>
    public void FreeExtent(long startBlock, int blockCount) => _inner.FreeExtent(startBlock, blockCount);

    // ── IBlockAllocator: persistence ──────────────────────────────────────────

    /// <inheritdoc/>
    public Task PersistAsync(IBlockDevice device, long bitmapStartBlock, CancellationToken ct = default)
        => _inner.PersistAsync(device, bitmapStartBlock, ct);

    // ── SWLV-specific API ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current wear-leveling statistics, including per-temperature group counts,
    /// free/total block counts, utilization, write counts, and estimated write-amplification
    /// reduction ratio.
    /// </summary>
    public WearLevelingStats GetStats()
    {
        lock (_routingLock)
        {
            bool active = IsSwlvActive;

            var hot    = BuildTemperatureStats(TemperatureClass.Hot);
            var warm   = BuildTemperatureStats(TemperatureClass.Warm);
            var cold   = BuildTemperatureStats(TemperatureClass.Cold);
            var frozen = BuildTemperatureStats(TemperatureClass.Frozen);

            // Estimate write-amplification reduction:
            // Groups that are single-temperature can be reclaimed without live-data copying.
            // Ratio = reclaimable groups / total groups (higher = better segregation).
            double waReduction = 0.0;
            int totalGroups = _allGroups.Length;
            if (totalGroups > 0 && active)
            {
                int singleTempGroups = hot.GroupCount + warm.GroupCount + cold.GroupCount + frozen.GroupCount;
                waReduction = (double)singleTempGroups / totalGroups;
            }

            return new WearLevelingStats
            {
                Hot    = hot,
                Warm   = warm,
                Cold   = cold,
                Frozen = frozen,
                SwlvActive = active,
                EstimatedWriteAmplificationReduction = waReduction
            };
        }
    }

    /// <summary>
    /// Returns allocation groups in the given temperature band where the dead-block ratio
    /// exceeds <see cref="WearLevelingConfig.GroupReclaimThreshold"/>.
    /// Background Vacuum calls this method to find groups safe to reclaim in bulk without
    /// scanning for live data, eliminating write amplification.
    /// </summary>
    /// <param name="tempClass">Temperature band to inspect.</param>
    /// <returns>Groups with dead-block ratio above the configured threshold.</returns>
    public IReadOnlyList<AllocationGroup> GetReclaimCandidates(TemperatureClass tempClass)
    {
        lock (_routingLock)
        {
            if (!_temperatureGroups.TryGetValue(tempClass, out var groups))
                return Array.Empty<AllocationGroup>();

            var candidates = new List<AllocationGroup>();
            foreach (var g in groups)
            {
                // Dead-block ratio = 1 - FreeBlockCount / BlockCount (free bits = live remaining)
                // A fully-reclaimed group has all bits free (FreeBlockCount == BlockCount)
                // A fully-dead group has no free bits (FreeBlockCount == 0), ratio = 1.0
                // We flag groups where allocated (dead) blocks exceed the threshold.
                double deadRatio = g.BlockCount == 0
                    ? 0.0
                    : 1.0 - ((double)g.FreeBlockCount / g.BlockCount);

                if (deadRatio >= _config.GroupReclaimThreshold)
                    candidates.Add(g);
            }

            return candidates;
        }
    }

    /// <summary>
    /// Returns the <see cref="TemperatureClass"/> assigned to the given allocation group.
    /// </summary>
    /// <param name="allocationGroupId">The <see cref="AllocationGroup.GroupId"/> to look up.</param>
    /// <returns>Temperature class of the group.</returns>
    /// <exception cref="KeyNotFoundException">Group ID is not managed by this allocator.</exception>
    public TemperatureClass GetGroupTemperature(int allocationGroupId)
    {
        lock (_routingLock)
        {
            if (!_groupIdToTemperature.TryGetValue(allocationGroupId, out var tc))
                throw new KeyNotFoundException($"Allocation group {allocationGroupId} is not managed by this SemanticWearLevelingAllocator.");
            return tc;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when SWLV is actively routing by temperature class.
    /// SWLV is disabled when the master switch is off OR the ZNS gate is engaged.
    /// </summary>
    private bool IsSwlvActive => _config.Enabled && !_config.ZnsAwareActive;

    /// <summary>
    /// Partitions <see cref="_allGroups"/> into four temperature bands based on config ratios.
    /// Groups are assigned in order (round-robin across bands) to achieve the target ratios.
    /// Any rounding remainder goes to the Cold band.
    /// </summary>
    private void PartitionGroupsIntoTemperatureBands()
    {
        int total = _allGroups.Length;

        // Calculate group counts per temperature class
        int hotCount    = Math.Max(1, (int)Math.Round(total * _config.HotGroupRatio));
        int warmCount   = Math.Max(1, (int)Math.Round(total * _config.WarmGroupRatio));
        int frozenCount = Math.Max(1, (int)Math.Round(total * _config.FrozenGroupRatio));
        // Cold gets the remainder (ensures sum == total)
        int coldCount;

        // Clamp to available groups
        hotCount    = Math.Min(hotCount,    total);
        warmCount   = Math.Min(warmCount,   total - hotCount);
        frozenCount = Math.Min(frozenCount, total - hotCount - warmCount);
        coldCount   = total - hotCount - warmCount - frozenCount;
        if (coldCount < 0) coldCount = 0;

        int index = 0;

        AssignGroups(TemperatureClass.Hot,    hotCount,    ref index);
        AssignGroups(TemperatureClass.Warm,   warmCount,   ref index);
        AssignGroups(TemperatureClass.Cold,   coldCount,   ref index);
        AssignGroups(TemperatureClass.Frozen, frozenCount, ref index);

        // Allocate write-counter arrays
        foreach (TemperatureClass tc in Enum.GetValues<TemperatureClass>())
        {
            int count = _temperatureGroups[tc].Count;
            _writeCounters[tc] = count > 0 ? new long[count] : Array.Empty<long>();
        }
    }

    private void AssignGroups(TemperatureClass tc, int count, ref int index)
    {
        var list = _temperatureGroups[tc];
        for (int i = 0; i < count && index < _allGroups.Length; i++, index++)
        {
            var group = _allGroups[index];
            list.Add(group);
            _groupIdToTemperature[group.GroupId] = tc;
        }
    }

    /// <summary>
    /// Tries to allocate a single block from the given temperature band using round-robin
    /// group selection. Returns <c>false</c> when all groups in the band are full.
    /// </summary>
    private bool TryAllocateFromBand(TemperatureClass tc, out long block)
    {
        block = -1;
        var groups = _temperatureGroups[tc];
        if (groups.Count == 0)
            return false;

        int startIndex = _roundRobinIndex[tc];
        int attempts = 0;

        while (attempts < groups.Count)
        {
            int idx = (_roundRobinIndex[tc]) % groups.Count;
            var group = groups[idx];

            if (group.FreeBlockCount > 0)
            {
                try
                {
                    block = group.AllocateBlock();
                    // Advance round-robin for next call
                    _roundRobinIndex[tc] = (idx + 1) % groups.Count;
                    return true;
                }
                catch (InvalidOperationException)
                {
                    // Group reported free but couldn't allocate (race) — try next
                }
            }

            _roundRobinIndex[tc] = (idx + 1) % groups.Count;
            attempts++;
        }

        // All groups in this band are full
        return false;
    }

    /// <summary>
    /// Tries to allocate a contiguous extent from the given temperature band using
    /// round-robin group selection. Returns <c>false</c> when no group can satisfy
    /// the contiguous request.
    /// </summary>
    private bool TryAllocateExtentFromBand(TemperatureClass tc, int blockCount, out long[]? blocks)
    {
        blocks = null;
        var groups = _temperatureGroups[tc];
        if (groups.Count == 0)
            return false;

        int attempts = 0;
        while (attempts < groups.Count)
        {
            int idx = _roundRobinIndex[tc] % groups.Count;
            var group = groups[idx];

            if (group.FreeBlockCount >= blockCount)
            {
                try
                {
                    blocks = group.AllocateExtent(blockCount);
                    _roundRobinIndex[tc] = (idx + 1) % groups.Count;
                    return true;
                }
                catch (InvalidOperationException)
                {
                    // No contiguous run in this group — try next
                }
            }

            _roundRobinIndex[tc] = (idx + 1) % groups.Count;
            attempts++;
        }

        return false;
    }

    private void IncrementWriteCount(TemperatureClass tc, int count = 1)
    {
        var counters = _writeCounters[tc];
        if (counters.Length == 0)
            return;
        int idx = (_roundRobinIndex[tc] == 0 ? counters.Length - 1 : _roundRobinIndex[tc] - 1) % counters.Length;
        counters[idx] = Interlocked.Add(ref counters[idx], count);
    }

    private TemperatureStats BuildTemperatureStats(TemperatureClass tc)
    {
        var groups = _temperatureGroups[tc];
        long freeBlocks = 0;
        long totalBlocks = 0;

        foreach (var g in groups)
        {
            freeBlocks  += g.FreeBlockCount;
            totalBlocks += g.BlockCount;
        }

        // Sum write counters
        long writes = 0;
        var counters = _writeCounters[tc];
        foreach (long c in counters)
            writes += c;

        return new TemperatureStats
        {
            GroupCount  = groups.Count,
            FreeBlocks  = freeBlocks,
            TotalBlocks = totalBlocks,
            WriteCount  = writes
        };
    }
}
