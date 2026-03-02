using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;

namespace DataWarehouse.SDK.VirtualDiskEngine.Allocation;

/// <summary>
/// Tracks access heat for a single extent identified by its start block.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Heat-driven tiering (VOPT-51)")]
public readonly struct HeatRecord
{
    /// <summary>Total number of times this extent has been accessed since tracking began.</summary>
    public long AccessCount { get; }

    /// <summary>Timestamp of the most recent access to this extent.</summary>
    public DateTimeOffset LastAccessed { get; }

    /// <summary>The heat tier most recently assigned to this extent.</summary>
    public HeatTier CurrentTier { get; }

    /// <summary>
    /// Initializes a new <see cref="HeatRecord"/>.
    /// </summary>
    public HeatRecord(long accessCount, DateTimeOffset lastAccessed, HeatTier currentTier)
    {
        AccessCount = accessCount;
        LastAccessed = lastAccessed;
        CurrentTier = currentTier;
    }

    /// <summary>Returns a new record with incremented access count and updated timestamp.</summary>
    public HeatRecord WithAccess(DateTimeOffset now) =>
        new(AccessCount + 1, now, CurrentTier);

    /// <summary>Returns a new record with the tier updated.</summary>
    public HeatRecord WithTier(HeatTier tier) =>
        new(AccessCount, LastAccessed, tier);
}

/// <summary>
/// Result of a single background migration cycle.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Heat-driven tiering (VOPT-51)")]
public readonly struct MigrationResult
{
    /// <summary>Total number of extents moved between tiers during this cycle.</summary>
    public int ExtentsMigrated { get; }

    /// <summary>Total number of blocks moved across all migrated extents.</summary>
    public long BlocksMoved { get; }

    /// <summary>Total bytes of data moved (BlocksMoved * BlockSize).</summary>
    public long BytesMoved { get; }

    /// <summary>Number of hot extents moved from a cold shard to a hot shard.</summary>
    public int HotToCorrect { get; }

    /// <summary>Number of cold extents moved from a hot shard to a cold/frozen shard.</summary>
    public int ColdToCorrect { get; }

    /// <summary>Wall-clock time taken by this migration cycle.</summary>
    public TimeSpan Duration { get; }

    /// <summary>True when the I/O budget was exhausted before all candidates were processed.</summary>
    public bool BudgetExhausted { get; }

    /// <summary>
    /// Initializes a new <see cref="MigrationResult"/>.
    /// </summary>
    public MigrationResult(
        int extentsMigrated,
        long blocksMoved,
        long bytesMoved,
        int hotToCorrect,
        int coldToCorrect,
        TimeSpan duration,
        bool budgetExhausted)
    {
        ExtentsMigrated = extentsMigrated;
        BlocksMoved = blocksMoved;
        BytesMoved = bytesMoved;
        HotToCorrect = hotToCorrect;
        ColdToCorrect = coldToCorrect;
        Duration = duration;
        BudgetExhausted = budgetExhausted;
    }
}

/// <summary>
/// Describes a candidate extent for tier migration.
/// </summary>
internal sealed class MigrationCandidate
{
    public long StartBlock { get; }
    public int BlockCount { get; }
    public HeatTier TargetTier { get; }
    public ushort TargetShardId { get; }

    /// <summary>Priority score — higher = migrate first.</summary>
    public double Priority { get; }

    public MigrationCandidate(long startBlock, int blockCount, HeatTier targetTier, ushort targetShardId, double priority)
    {
        StartBlock = startBlock;
        BlockCount = blockCount;
        TargetTier = targetTier;
        TargetShardId = targetShardId;
        Priority = priority;
    }
}

/// <summary>
/// Classifies extents as hot, warm, cold, or frozen based on access frequency and recency,
/// then migrates misplaced extents to their correct tier shards in a background loop.
/// </summary>
/// <remarks>
/// <para>
/// The allocator maintains a <see cref="HeatRecord"/> per tracked extent (keyed by start block).
/// When <see cref="RecordAccess"/> is called, the access count and timestamp are updated.
/// <see cref="ClassifyExtent"/> applies the configured thresholds from <see cref="TieringConfig"/>
/// to assign a <see cref="HeatTier"/>.
/// </para>
/// <para>
/// <see cref="RunMigrationCycleAsync"/> scans the heat map, identifies extents whose current shard
/// does not match the target shard for their tier, and moves them within the configured I/O budget.
/// <see cref="RunContinuousAsync"/> drives periodic cycles until cancellation.
/// </para>
/// <para>
/// <see cref="AllocateWithHeatHint"/> provides hint-aware allocation: new extents are placed in
/// the allocation group that corresponds to the shard for the requested <see cref="HeatTier"/>.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Heat-driven tiering with background migration (VOPT-51)")]
public sealed class HeatDrivenTieringAllocator
{
    private readonly IBlockDevice _device;
    private readonly IBlockAllocator _allocator;
    private readonly TieringConfig _config;
    private readonly int _blockSize;

    // Heat tracking — keyed by extent start block.
    private readonly ConcurrentDictionary<long, HeatRecord> _heatMap = new();

    // Shard → allocation group mapping, populated by the caller.
    private readonly Dictionary<ushort, AllocationGroup> _shardGroups = new();

    /// <summary>
    /// Initializes a new <see cref="HeatDrivenTieringAllocator"/>.
    /// </summary>
    /// <param name="device">The underlying block device used for data migration reads and writes.</param>
    /// <param name="allocator">The primary block allocator for new allocations.</param>
    /// <param name="config">Tiering configuration (thresholds, budgets, shard mappings).</param>
    /// <param name="blockSize">Block size in bytes; must match <paramref name="device"/>.</param>
    public HeatDrivenTieringAllocator(
        IBlockDevice device,
        IBlockAllocator allocator,
        TieringConfig config,
        int blockSize)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);

        _device = device;
        _allocator = allocator;
        _config = config;
        _blockSize = blockSize;
    }

    // ── Shard Group Registration ─────────────────────────────────────────

    /// <summary>
    /// Registers an <see cref="AllocationGroup"/> for a specific shard.
    /// Migration targets are resolved through registered shard groups.
    /// </summary>
    /// <param name="shardId">ShardId that identifies the physical/logical storage tier.</param>
    /// <param name="group">The allocation group backing that shard.</param>
    public void RegisterShardGroup(ushort shardId, AllocationGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        _shardGroups[shardId] = group;
    }

    // ── Heat Tracking ────────────────────────────────────────────────────

    /// <summary>
    /// Records an access to the extent beginning at <paramref name="startBlock"/>.
    /// Creates a new heat record if one does not already exist.
    /// </summary>
    /// <param name="startBlock">Absolute block number where the extent starts.</param>
    public void RecordAccess(long startBlock)
    {
        var now = DateTimeOffset.UtcNow;
        _heatMap.AddOrUpdate(
            startBlock,
            _ => new HeatRecord(1, now, HeatTier.Warm),
            (_, existing) => existing.WithAccess(now));
    }

    /// <summary>
    /// Classifies the heat tier of the extent at <paramref name="startBlock"/> based on
    /// access frequency and the time elapsed since the last access.
    /// </summary>
    /// <param name="startBlock">Absolute block number where the extent starts.</param>
    /// <returns>
    /// <see cref="HeatTier.Warm"/> if no heat record exists (unknown extents default to warm).
    /// Otherwise the tier determined by the configured thresholds.
    /// </returns>
    public HeatTier ClassifyExtent(long startBlock)
    {
        if (!_heatMap.TryGetValue(startBlock, out var record))
            return HeatTier.Warm;

        var now = DateTimeOffset.UtcNow;
        double hoursSinceAccess = (now - record.LastAccessed).TotalHours;

        // Frozen: no access for FrozenThresholdDaysWithoutAccess days.
        if (hoursSinceAccess >= _config.FrozenThresholdDaysWithoutAccess * 24.0)
            return HeatTier.Frozen;

        // Cold: no access for ColdThresholdHoursWithoutAccess hours.
        if (hoursSinceAccess >= _config.ColdThresholdHoursWithoutAccess)
            return HeatTier.Cold;

        // Hot: access rate meets or exceeds HotThresholdAccessesPerHour.
        // Approximate rate: total accesses / elapsed hours since first seen (floor 1h).
        double elapsedHours = Math.Max(1.0, hoursSinceAccess);
        double accessesPerHour = record.AccessCount / elapsedHours;

        if (accessesPerHour >= _config.HotThresholdAccessesPerHour)
            return HeatTier.Hot;

        return HeatTier.Warm;
    }

    // ── Allocation ───────────────────────────────────────────────────────

    /// <summary>
    /// Allocates <paramref name="blockCount"/> contiguous blocks in the allocation group
    /// corresponding to the shard for <paramref name="hint"/>.
    /// Falls back to the primary allocator if no shard group is registered for the hint tier.
    /// </summary>
    /// <param name="blockCount">Number of contiguous blocks to allocate.</param>
    /// <param name="hint">Desired heat tier; used to select the target shard group.</param>
    /// <returns>The start block of the allocated extent.</returns>
    public long AllocateWithHeatHint(int blockCount, HeatTier hint)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockCount);

        if (_config.TierToShardId.TryGetValue(hint, out ushort shardId)
            && _shardGroups.TryGetValue(shardId, out AllocationGroup? group)
            && group.FreeBlockCount >= blockCount)
        {
            long[] blocks = group.AllocateExtent(blockCount);
            return blocks[0];
        }

        // Fallback: use the primary allocator.
        long[] fallback = _allocator.AllocateExtent(blockCount);
        return fallback[0];
    }

    // ── Background Migration ─────────────────────────────────────────────

    /// <summary>
    /// Executes a single background migration cycle.
    /// </summary>
    /// <remarks>
    /// The cycle proceeds as follows:
    /// <list type="number">
    ///   <item>Scan the heat map to classify each tracked extent and identify those placed
    ///         in a shard that does not match the tier's target shard.</item>
    ///   <item>Sort candidates by priority: hot extents in wrong (cold) shards first,
    ///         then cold/frozen extents in wrong (hot) shards.</item>
    ///   <item>For each candidate (within <see cref="TieringConfig.MaxMigrationsPerCycle"/>
    ///         and the I/O budget): read each source block, write to the target shard's
    ///         allocation group, and free the source blocks.</item>
    /// </list>
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="MigrationResult"/> summarising what was moved.</returns>
    public async Task<MigrationResult> RunMigrationCycleAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        int extentsMigrated = 0;
        long blocksMoved = 0L;
        long bytesMoved = 0L;
        int hotToCorrect = 0;
        int coldToCorrect = 0;
        bool budgetExhausted = false;

        // Budget: bytes remaining this cycle.
        long budgetRemaining = (long)(_config.MigrationBudgetBytesPerSecond *
                                      _config.HeatScanInterval.TotalSeconds);

        // Step 1: Classify all tracked extents and build candidate list.
        var candidates = BuildCandidates();

        // Step 2: Sort — hot-in-wrong-shard first (highest priority), then cold-in-wrong-shard.
        candidates.Sort((a, b) => b.Priority.CompareTo(a.Priority));

        // Step 3: Migrate within budget and per-cycle limit.
        int cycleLimit = Math.Min(candidates.Count, _config.MaxMigrationsPerCycle);

        for (int i = 0; i < cycleLimit; i++)
        {
            ct.ThrowIfCancellationRequested();

            var candidate = candidates[i];
            long extentBytes = (long)candidate.BlockCount * _blockSize;

            if (budgetRemaining < extentBytes)
            {
                budgetExhausted = true;
                break;
            }

            bool isHotCorrection = candidate.TargetTier == HeatTier.Hot;

            bool migrated = await MigrateExtentAsync(candidate, ct).ConfigureAwait(false);
            if (migrated)
            {
                extentsMigrated++;
                blocksMoved += candidate.BlockCount;
                bytesMoved += extentBytes;
                budgetRemaining -= extentBytes;

                if (isHotCorrection) hotToCorrect++;
                else coldToCorrect++;
            }
        }

        if (!budgetExhausted && candidates.Count > cycleLimit)
            budgetExhausted = true;

        sw.Stop();
        return new MigrationResult(extentsMigrated, blocksMoved, bytesMoved,
                                   hotToCorrect, coldToCorrect, sw.Elapsed, budgetExhausted);
    }

    /// <summary>
    /// Runs continuous background heat-scan and migration cycles until <paramref name="ct"/> is cancelled.
    /// Each cycle waits <see cref="TieringConfig.HeatScanInterval"/> before the next execution.
    /// </summary>
    /// <param name="ct">Cancellation token to stop the loop.</param>
    public async Task RunContinuousAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunMigrationCycleAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031
            catch (Exception)
            {
                // Log-and-continue: tiering is best-effort; individual cycle failures
                // must not crash the background loop.
            }
#pragma warning restore CA1031

            try
            {
                await Task.Delay(_config.HeatScanInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Builds the list of migration candidates from the current heat map snapshot.
    /// An extent is a candidate when the tier's target shard differs from the shard
    /// inferred from the extent's current allocation group.
    /// </summary>
    private List<MigrationCandidate> BuildCandidates()
    {
        var candidates = new List<MigrationCandidate>();

        foreach (var (startBlock, record) in _heatMap)
        {
            HeatTier tier = ClassifyExtent(startBlock);

            // Update the tier in the heat record if it changed.
            if (tier != record.CurrentTier)
            {
                _heatMap.TryUpdate(startBlock, record.WithTier(tier), record);
            }

            if (!_config.TierToShardId.TryGetValue(tier, out ushort targetShardId))
                continue;

            // Determine the current shard by finding which registered group owns startBlock.
            ushort? currentShardId = FindCurrentShard(startBlock);

            // If no shard group is registered for this block, we cannot migrate it.
            if (currentShardId is null)
                continue;

            // Already in the correct shard — no migration needed.
            if (currentShardId.Value == targetShardId)
                continue;

            // Estimate block count from the heat record — use 1 as the minimum unit.
            // Callers may register extent sizes via RecordExtentSize; default to 1.
            int blockCount = GetTrackedBlockCount(startBlock);

            double priority = ComputePriority(tier, record);
            candidates.Add(new MigrationCandidate(startBlock, blockCount, tier, targetShardId, priority));
        }

        return candidates;
    }

    /// <summary>
    /// Migrates a single extent to its target shard allocation group.
    /// Reads source blocks, writes to newly allocated target blocks, frees source blocks.
    /// </summary>
    private async Task<bool> MigrateExtentAsync(MigrationCandidate candidate, CancellationToken ct)
    {
        if (!_config.TierToShardId.TryGetValue(candidate.TargetTier, out ushort targetShardId))
            return false;

        if (!_shardGroups.TryGetValue(targetShardId, out AllocationGroup? targetGroup))
            return false;

        if (targetGroup.FreeBlockCount < candidate.BlockCount)
            return false;

        // a. Allocate blocks in target shard's allocation group.
        long[] targetBlocks;
        try
        {
            targetBlocks = targetGroup.AllocateExtent(candidate.BlockCount);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        // b. Copy extent data block by block from source to target.
        var buffer = new byte[_blockSize];
        var mem = buffer.AsMemory();

        try
        {
            for (int i = 0; i < candidate.BlockCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                long srcBlock = candidate.StartBlock + i;
                long dstBlock = targetBlocks[i];

                await _device.ReadBlockAsync(srcBlock, mem, ct).ConfigureAwait(false);
                await _device.WriteBlockAsync(dstBlock, mem, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // On partial failure, free the target blocks we already allocated.
            // Source blocks remain intact — data is not lost.
            try { targetGroup.FreeExtent(targetBlocks[0], candidate.BlockCount); } catch { /* best-effort */ }
            return false;
        }

        // c. Free source blocks from the owning allocation group.
        ushort? srcShardId = FindCurrentShard(candidate.StartBlock);
        if (srcShardId.HasValue && _shardGroups.TryGetValue(srcShardId.Value, out AllocationGroup? srcGroup))
        {
            try { srcGroup.FreeExtent(candidate.StartBlock, candidate.BlockCount); } catch { /* best-effort */ }
        }
        else
        {
            // Fall back to the primary allocator to free the source extent.
            try { _allocator.FreeExtent(candidate.StartBlock, candidate.BlockCount); } catch { /* best-effort */ }
        }

        // d. Update the heat map to reflect the new start block location.
        if (_heatMap.TryRemove(candidate.StartBlock, out var oldRecord))
        {
            _heatMap[targetBlocks[0]] = oldRecord.WithTier(candidate.TargetTier);
        }

        return true;
    }

    /// <summary>
    /// Finds the ShardId of the allocation group that contains <paramref name="startBlock"/>.
    /// Returns null if no registered shard group owns the block.
    /// </summary>
    private ushort? FindCurrentShard(long startBlock)
    {
        foreach (var (shardId, group) in _shardGroups)
        {
            if (startBlock >= group.StartBlock && startBlock < group.StartBlock + group.BlockCount)
                return shardId;
        }
        return null;
    }

    // Tracked extent sizes — populated via RecordExtentSize; defaults to 1 block.
    private readonly ConcurrentDictionary<long, int> _extentSizes = new();

    /// <summary>
    /// Records the block count for an extent, enabling accurate migration cost accounting.
    /// Must be called whenever an extent is allocated or its size changes.
    /// </summary>
    /// <param name="startBlock">Absolute block number where the extent starts.</param>
    /// <param name="blockCount">Number of blocks in the extent.</param>
    public void RecordExtentSize(long startBlock, int blockCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockCount);
        _extentSizes[startBlock] = blockCount;
    }

    private int GetTrackedBlockCount(long startBlock) =>
        _extentSizes.TryGetValue(startBlock, out int count) ? count : 1;

    /// <summary>
    /// Computes a migration priority score for a candidate extent.
    /// Higher scores are migrated first within a cycle.
    /// Hot extents in wrong (cold/frozen) shards are urgent; cold extents in hot shards less so.
    /// </summary>
    private static double ComputePriority(HeatTier targetTier, HeatRecord record)
    {
        // Base priority by urgency: hot misplacements are most urgent.
        double tierScore = targetTier switch
        {
            HeatTier.Hot    => 100.0,
            HeatTier.Warm   => 50.0,
            HeatTier.Cold   => 25.0,
            HeatTier.Frozen => 10.0,
            _ => 0.0
        };

        // Boost by access frequency to promote the most active misplaced hot extents first.
        double freqBoost = targetTier == HeatTier.Hot ? Math.Min(record.AccessCount, 1000) * 0.1 : 0.0;

        // Boost cold/frozen demotion by staleness to move the oldest data earliest.
        double staleBoost = targetTier is HeatTier.Cold or HeatTier.Frozen
            ? (DateTimeOffset.UtcNow - record.LastAccessed).TotalHours * 0.01
            : 0.0;

        return tierScore + freqBoost + staleBoost;
    }
}
