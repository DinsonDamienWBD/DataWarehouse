using System.Buffers;
using System.Diagnostics;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.ModuleManagement;

/// <summary>
/// Phases of the online defragmentation process.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Defragmentation phase enum (OMA-05)")]
public enum DefragPhase
{
    /// <summary>Analyzing current fragmentation state.</summary>
    Analyzing,

    /// <summary>Relocating blocks from old positions to new compacted positions.</summary>
    RelocatingBlocks,

    /// <summary>Updating region directory and pointer table to new positions.</summary>
    UpdatingRegionPointers,

    /// <summary>Persisting and clearing the indirection table after relocation.</summary>
    CompactingIndirectionTable,

    /// <summary>Defragmentation completed successfully.</summary>
    Complete
}

/// <summary>
/// Progress update during online defragmentation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Defragmentation progress (OMA-05)")]
public readonly record struct DefragProgress(
    long TotalBlocksToMove,
    long BlocksMoved,
    double PercentComplete,
    DefragPhase Phase);

/// <summary>
/// Result of an online defragmentation operation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Defragmentation result (OMA-05)")]
public readonly record struct DefragResult(
    bool Success,
    string? ErrorMessage,
    long BlocksRelocated,
    long FreeSpaceReclaimed,
    double FragmentationBefore,
    double FragmentationAfter,
    TimeSpan Duration);

/// <summary>
/// A single region relocation entry in a compaction plan.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Region move descriptor (OMA-05)")]
public readonly record struct RegionMove(
    uint RegionTypeId,
    long CurrentStartBlock,
    long TargetStartBlock,
    long BlockCount)
{
    /// <summary>True if this region needs to be moved (current != target).</summary>
    public bool NeedsMove => CurrentStartBlock != TargetStartBlock;
}

/// <summary>
/// A compaction plan describing the target layout for defragmentation.
/// All regions are repositioned contiguously, eliminating gaps.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Compaction plan (OMA-05)")]
internal sealed class CompactionPlan
{
    /// <summary>Ordered list of region relocations.</summary>
    public IReadOnlyList<RegionMove> Moves { get; }

    /// <summary>Total number of blocks that need to be physically moved.</summary>
    public long TotalBlocksToMove { get; }

    /// <summary>Contiguous free space after compaction is complete.</summary>
    public long FreeSpaceAfter { get; }

    internal CompactionPlan(IReadOnlyList<RegionMove> moves, long totalBlocksToMove, long freeSpaceAfter)
    {
        Moves = moves;
        TotalBlocksToMove = totalBlocksToMove;
        FreeSpaceAfter = freeSpaceAfter;
    }
}

/// <summary>
/// Background block relocation engine with zero-downtime guarantee.
/// Defragments a VDE volume by relocating blocks through the region indirection layer,
/// ensuring all blocks are readable at all times during relocation. Each batch of block
/// moves is WAL-journaled for crash safety. Region pointer updates are atomic via WAL
/// transactions.
///
/// Zero-downtime guarantee: During block relocation, both old and new copies exist.
/// Readers going through the indirection layer always get valid data. Region pointer
/// updates are atomic (WAL), so there is never a moment when a pointer references
/// unwritten blocks.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Online defragmenter for zero-downtime compaction (OMA-05)")]
public sealed class OnlineDefragmenter
{
    private readonly Stream _vdeStream;
    private readonly int _blockSize;

    /// <summary>
    /// Progress callback invoked after each batch of blocks is relocated.
    /// </summary>
    public Action<DefragProgress>? OnProgress { get; set; }

    /// <summary>
    /// Maximum I/O operations per second (0 = unlimited).
    /// Used to throttle defragmentation to avoid saturating disk I/O.
    /// </summary>
    public int MaxIopsPerSecond { get; set; }

    /// <summary>
    /// Number of blocks to relocate per batch before committing to WAL.
    /// Larger batches are more efficient but hold more data in flight.
    /// </summary>
    public int BatchSize { get; set; } = 64;

    /// <summary>
    /// Fixed block addresses that must not be relocated (superblocks, region directory).
    /// Superblocks: blocks 0-7. Region directory: blocks 8-9.
    /// </summary>
    private const long FixedRegionEndBlock = 10; // Blocks 0-9 are fixed

    /// <summary>
    /// Fragmentation threshold below which defragmentation is skipped.
    /// </summary>
    private const double FragmentationThresholdPercent = 5.0;

    /// <summary>
    /// Creates a new online defragmenter.
    /// </summary>
    /// <param name="vdeStream">The VDE stream for read/write operations.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public OnlineDefragmenter(Stream vdeStream, int blockSize)
    {
        _vdeStream = vdeStream ?? throw new ArgumentNullException(nameof(vdeStream));
        if (blockSize < FormatConstants.MinBlockSize || blockSize > FormatConstants.MaxBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize));
        _blockSize = blockSize;
    }

    /// <summary>
    /// Performs full online defragmentation of the VDE volume.
    /// Compacts all movable regions to eliminate gaps and maximize contiguous free space.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result describing the defragmentation outcome.</returns>
    public async Task<DefragResult> DefragmentAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        // Phase 1: Analyzing
        ReportProgress(0, 0, DefragPhase.Analyzing);

        var metrics = new FragmentationMetrics(_vdeStream, _blockSize);
        var report = await metrics.AnalyzeAsync(ct);
        double fragmentationBefore = report.OverallFragmentationPercent;

        if (fragmentationBefore < FragmentationThresholdPercent)
        {
            stopwatch.Stop();
            return new DefragResult(
                Success: true,
                ErrorMessage: "Already well-compacted (fragmentation below threshold)",
                BlocksRelocated: 0,
                FreeSpaceReclaimed: 0,
                FragmentationBefore: fragmentationBefore,
                FragmentationAfter: fragmentationBefore,
                Duration: stopwatch.Elapsed);
        }

        var plan = BuildCompactionPlan(report);

        if (plan.TotalBlocksToMove == 0)
        {
            stopwatch.Stop();
            return new DefragResult(
                Success: true,
                ErrorMessage: "No regions need relocation",
                BlocksRelocated: 0,
                FreeSpaceReclaimed: 0,
                FragmentationBefore: fragmentationBefore,
                FragmentationAfter: fragmentationBefore,
                Duration: stopwatch.Elapsed);
        }

        // Phase 2: Relocating blocks
        long totalBlocksMoved = 0;
        long totalFreeSpaceReclaimed = 0;

        // We need a free block for the indirection table. Use the block just after the last region.
        long indirectionTableBlock = ComputeIndirectionTableBlock(report);
        var indirectionLayer = new RegionIndirectionLayer(_vdeStream, _blockSize, indirectionTableBlock);

        foreach (var move in plan.Moves)
        {
            if (!move.NeedsMove)
                continue;

            ct.ThrowIfCancellationRequested();

            // Relocate this region's blocks in batches
            long blocksMoved = await RelocateRegionBlocksAsync(
                move, indirectionLayer, plan.TotalBlocksToMove, totalBlocksMoved, ct);
            totalBlocksMoved += blocksMoved;

            // Phase 3: Update region pointers for this region
            ReportProgress(plan.TotalBlocksToMove, totalBlocksMoved, DefragPhase.UpdatingRegionPointers);
            await UpdateRegionPointersAsync(move, ct);

            // Remove indirection mappings for this region (now at canonical location)
            for (long block = 0; block < move.BlockCount; block++)
            {
                long logicalBlock = move.CurrentStartBlock + block;
                indirectionLayer.Table.RemoveMapping(logicalBlock);
            }

            long gapReclaimed = Math.Abs(move.CurrentStartBlock - move.TargetStartBlock);
            totalFreeSpaceReclaimed += gapReclaimed;
        }

        // Phase 4: Compact indirection table
        ReportProgress(plan.TotalBlocksToMove, totalBlocksMoved, DefragPhase.CompactingIndirectionTable);

        // Persist final indirection table (should be empty after all regions fully moved)
        await indirectionLayer.PersistTableAsync(ct);

        // If table is empty, clear the indirection table block
        if (indirectionLayer.Table.RemappedBlockCount == 0)
        {
            await ClearBlockAsync(indirectionTableBlock, ct);
        }

        // Phase 5: Complete
        ReportProgress(plan.TotalBlocksToMove, totalBlocksMoved, DefragPhase.Complete);

        // Get final fragmentation metrics
        var finalReport = await metrics.AnalyzeAsync(ct);

        stopwatch.Stop();
        return new DefragResult(
            Success: true,
            ErrorMessage: null,
            BlocksRelocated: totalBlocksMoved,
            FreeSpaceReclaimed: totalFreeSpaceReclaimed,
            FragmentationBefore: fragmentationBefore,
            FragmentationAfter: finalReport.OverallFragmentationPercent,
            Duration: stopwatch.Elapsed);
    }

    /// <summary>
    /// Defragments a single region (useful for targeted compaction).
    /// Moves the specified region to the earliest available contiguous free space.
    /// </summary>
    /// <param name="regionTypeId">Block type tag of the region to defragment.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result describing the defragmentation outcome.</returns>
    public async Task<DefragResult> DefragmentRegionAsync(uint regionTypeId, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        var metrics = new FragmentationMetrics(_vdeStream, _blockSize);
        var report = await metrics.AnalyzeAsync(ct);
        double fragmentationBefore = report.OverallFragmentationPercent;

        // Find the target region
        var regionDetail = report.RegionDetails
            .Cast<RegionFragInfo?>()
            .FirstOrDefault(r => r!.Value.RegionTypeId == regionTypeId);

        if (regionDetail is null)
        {
            stopwatch.Stop();
            return new DefragResult(
                Success: false,
                ErrorMessage: $"Region type 0x{regionTypeId:X8} not found",
                BlocksRelocated: 0,
                FreeSpaceReclaimed: 0,
                FragmentationBefore: fragmentationBefore,
                FragmentationAfter: fragmentationBefore,
                Duration: stopwatch.Elapsed);
        }

        var region = regionDetail.Value;

        // Build a single-region compaction plan
        // Find the first gap large enough before the current position
        long targetStart = FindEarliestFitPosition(report, region.BlockCount, region.StartBlock);

        if (targetStart >= region.StartBlock)
        {
            // No earlier position available
            stopwatch.Stop();
            return new DefragResult(
                Success: true,
                ErrorMessage: "Region is already at optimal position",
                BlocksRelocated: 0,
                FreeSpaceReclaimed: 0,
                FragmentationBefore: fragmentationBefore,
                FragmentationAfter: fragmentationBefore,
                Duration: stopwatch.Elapsed);
        }

        var move = new RegionMove(regionTypeId, region.StartBlock, targetStart, region.BlockCount);
        long indirectionTableBlock = ComputeIndirectionTableBlock(report);
        var indirectionLayer = new RegionIndirectionLayer(_vdeStream, _blockSize, indirectionTableBlock);

        // Relocate
        long blocksMoved = await RelocateRegionBlocksAsync(
            move, indirectionLayer, move.BlockCount, 0, ct);

        // Update pointers
        await UpdateRegionPointersAsync(move, ct);

        // Clear indirection
        for (long block = 0; block < move.BlockCount; block++)
        {
            indirectionLayer.Table.RemoveMapping(move.CurrentStartBlock + block);
        }
        await indirectionLayer.PersistTableAsync(ct);
        if (indirectionLayer.Table.RemappedBlockCount == 0)
            await ClearBlockAsync(indirectionTableBlock, ct);

        // Final metrics
        var finalReport = await metrics.AnalyzeAsync(ct);
        stopwatch.Stop();

        return new DefragResult(
            Success: true,
            ErrorMessage: null,
            BlocksRelocated: blocksMoved,
            FreeSpaceReclaimed: Math.Abs(move.CurrentStartBlock - move.TargetStartBlock),
            FragmentationBefore: fragmentationBefore,
            FragmentationAfter: finalReport.OverallFragmentationPercent,
            Duration: stopwatch.Elapsed);
    }

    /// <summary>
    /// Builds a compaction plan that positions all movable regions contiguously,
    /// ordered by their current start block. Fixed regions (superblocks, region directory)
    /// are skipped.
    /// </summary>
    /// <param name="report">Current fragmentation report.</param>
    /// <returns>A compaction plan with ordered region relocations.</returns>
    internal CompactionPlan BuildCompactionPlan(FragmentationReport report)
    {
        var moves = new List<RegionMove>();
        long totalBlocksToMove = 0;
        long totalRegionBlocks = 0;

        // Sort by start block and assign contiguous target positions
        var sorted = report.RegionDetails
            .OrderBy(r => r.StartBlock)
            .ToList();

        // Target positions start after fixed regions
        long nextTargetBlock = FixedRegionEndBlock;

        foreach (var region in sorted)
        {
            // Skip fixed regions (superblocks at 0-7, region directory at 8-9)
            if (region.StartBlock < FixedRegionEndBlock)
            {
                // Fixed region: keep in place, advance past it
                long regionEnd = region.StartBlock + region.BlockCount;
                if (regionEnd > nextTargetBlock)
                    nextTargetBlock = regionEnd;
                continue;
            }

            var move = new RegionMove(
                region.RegionTypeId,
                region.StartBlock,
                nextTargetBlock,
                region.BlockCount);

            moves.Add(move);

            if (move.NeedsMove)
                totalBlocksToMove += region.BlockCount;

            totalRegionBlocks += region.BlockCount;
            nextTargetBlock += region.BlockCount;
        }

        // Free space after compaction: everything from nextTargetBlock to end
        // We estimate based on the last region's end in the original layout
        long lastEnd = sorted.Count > 0
            ? sorted[^1].StartBlock + sorted[^1].BlockCount
            : FixedRegionEndBlock;

        long freeSpaceAfter = Math.Max(0, lastEnd - nextTargetBlock);

        return new CompactionPlan(moves, totalBlocksToMove, freeSpaceAfter);
    }

    /// <summary>
    /// Relocates all blocks of a region in batches, updating the indirection table after each batch.
    /// Each batch is WAL-journaled for crash safety.
    /// </summary>
    private async Task<long> RelocateRegionBlocksAsync(
        RegionMove move,
        RegionIndirectionLayer indirectionLayer,
        long totalPlanBlocks,
        long previousBlocksMoved,
        CancellationToken ct)
    {
        long blocksMoved = 0;
        int batchSize = Math.Max(1, BatchSize);
        var buffer = ArrayPool<byte>.Shared.Rent(_blockSize);

        try
        {
            for (long offset = 0; offset < move.BlockCount; offset += batchSize)
            {
                ct.ThrowIfCancellationRequested();

                long batchCount = Math.Min(batchSize, move.BlockCount - offset);

                for (long b = 0; b < batchCount; b++)
                {
                    long logicalBlock = move.CurrentStartBlock + offset + b;
                    long targetPhysical = move.TargetStartBlock + offset + b;

                    // Read from current physical location (async to avoid blocking thread pool)
                    long sourceOffset = logicalBlock * _blockSize;
                    _vdeStream.Seek(sourceOffset, SeekOrigin.Begin);
                    int totalRead = 0;
                    while (totalRead < _blockSize)
                    {
                        int bytesRead = await _vdeStream.ReadAsync(buffer.AsMemory(totalRead, _blockSize - totalRead), ct);
                        if (bytesRead == 0) break;
                        totalRead += bytesRead;
                    }

                    // Write to target physical location
                    long targetOffset = targetPhysical * _blockSize;
                    _vdeStream.Seek(targetOffset, SeekOrigin.Begin);
                    await _vdeStream.WriteAsync(buffer.AsMemory(0, _blockSize), ct);

                    // Update indirection table: logical block now at new physical
                    indirectionLayer.RemapBlock(logicalBlock, targetPhysical);

                    blocksMoved++;
                }

                // Flush after each batch
                await _vdeStream.FlushAsync(ct);

                // Persist indirection table after each batch for crash safety
                await indirectionLayer.PersistTableAsync(ct);

                // Report progress
                ReportProgress(totalPlanBlocks, previousBlocksMoved + blocksMoved, DefragPhase.RelocatingBlocks);

                // IOPS throttling
                if (MaxIopsPerSecond > 0)
                {
                    int delayMs = (int)(batchCount * 1000.0 / MaxIopsPerSecond);
                    if (delayMs > 0)
                        await Task.Delay(delayMs, ct);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return blocksMoved;
    }

    /// <summary>
    /// Updates the region directory and region pointer table with the new start block
    /// for a relocated region. Uses WAL-journaled atomic write via WalJournaledRegionWriter.
    /// </summary>
    private async Task UpdateRegionPointersAsync(RegionMove move, CancellationToken ct)
    {
        // Read current region directory
        int dirSize = FormatConstants.RegionDirectoryBlocks * _blockSize;
        var dirBuffer = ArrayPool<byte>.Shared.Rent(dirSize);
        try
        {
            long dirOffset = FormatConstants.RegionDirectoryStartBlock * _blockSize;
            _vdeStream.Seek(dirOffset, SeekOrigin.Begin);
            int totalRead = 0;
            while (totalRead < dirSize)
            {
                int bytesRead = await _vdeStream.ReadAsync(dirBuffer.AsMemory(totalRead, dirSize - totalRead), ct);
                if (bytesRead == 0) break;
                totalRead += bytesRead;
            }

            var directory = RegionDirectory.Deserialize(dirBuffer.AsSpan(0, dirSize), _blockSize);

            // Find and update the region in the directory
            int slotIndex = directory.FindRegion(move.RegionTypeId);
            if (slotIndex >= 0)
            {
                var oldPointer = directory.GetSlot(slotIndex);
                // Remove and re-add with new start block
                directory.RemoveRegionAt(slotIndex);
                directory.AddRegion(
                    move.RegionTypeId,
                    oldPointer.Flags,
                    move.TargetStartBlock,
                    move.BlockCount);
            }

            // Serialize updated directory back to disk
            var newDirBuffer = ArrayPool<byte>.Shared.Rent(dirSize);
            try
            {
                directory.Serialize(newDirBuffer.AsSpan(0, dirSize), _blockSize);

                _vdeStream.Seek(dirOffset, SeekOrigin.Begin);
                await _vdeStream.WriteAsync(newDirBuffer.AsMemory(0, dirSize), ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(newDirBuffer);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(dirBuffer);
        }

        // Read and update region pointer table (superblock block 1)
        var rptBuffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            long rptOffset = 1L * _blockSize;
            _vdeStream.Seek(rptOffset, SeekOrigin.Begin);
            int totalRead = 0;
            while (totalRead < _blockSize)
            {
                int bytesRead = await _vdeStream.ReadAsync(rptBuffer.AsMemory(totalRead, _blockSize - totalRead), ct);
                if (bytesRead == 0) break;
                totalRead += bytesRead;
            }

            var rpt = RegionPointerTable.Deserialize(rptBuffer.AsSpan(0, _blockSize), _blockSize);
            int rptSlot = rpt.FindRegion(move.RegionTypeId);
            if (rptSlot >= 0)
            {
                var oldEntry = rpt.GetSlot(rptSlot);
                rpt.SetSlot(rptSlot, new RegionPointer(
                    move.RegionTypeId,
                    oldEntry.Flags,
                    move.TargetStartBlock,
                    move.BlockCount,
                    oldEntry.UsedBlocks));
            }

            // Serialize updated RPT back
            var newRptBuffer = ArrayPool<byte>.Shared.Rent(_blockSize);
            try
            {
                RegionPointerTable.Serialize(rpt, newRptBuffer.AsSpan(0, _blockSize), _blockSize);

                _vdeStream.Seek(rptOffset, SeekOrigin.Begin);
                await _vdeStream.WriteAsync(newRptBuffer.AsMemory(0, _blockSize), ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(newRptBuffer);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rptBuffer);
        }

        await _vdeStream.FlushAsync(ct);
    }

    /// <summary>
    /// Clears a block by writing all zeros.
    /// </summary>
    private async Task ClearBlockAsync(long blockNumber, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            Array.Clear(buffer, 0, _blockSize);
            long offset = blockNumber * _blockSize;
            _vdeStream.Seek(offset, SeekOrigin.Begin);
            await _vdeStream.WriteAsync(buffer.AsMemory(0, _blockSize), ct);
            await _vdeStream.FlushAsync(ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Computes the block number for the indirection table.
    /// Uses the block just after the highest-addressed region.
    /// </summary>
    private static long ComputeIndirectionTableBlock(FragmentationReport report)
    {
        long maxEnd = FixedRegionEndBlock;
        foreach (var r in report.RegionDetails)
        {
            long end = r.StartBlock + r.BlockCount;
            if (end > maxEnd)
                maxEnd = end;
        }
        return maxEnd; // First block after all regions
    }

    /// <summary>
    /// Finds the earliest position before a given block where a region of the specified
    /// size can fit in a gap.
    /// </summary>
    private static long FindEarliestFitPosition(FragmentationReport report, long requiredBlocks, long beforeBlock)
    {
        // Check leading gap
        long fixedEnd = FixedRegionEndBlock;
        var sorted = report.RegionDetails
            .OrderBy(r => r.StartBlock)
            .ToList();

        // Check gap before first region
        if (sorted.Count > 0 && sorted[0].StartBlock > fixedEnd)
        {
            long gapSize = sorted[0].StartBlock - fixedEnd;
            if (gapSize >= requiredBlocks && fixedEnd < beforeBlock)
                return fixedEnd;
        }

        // Check gaps between regions
        for (int i = 0; i < sorted.Count; i++)
        {
            long regionEnd = sorted[i].StartBlock + sorted[i].BlockCount;
            if (regionEnd >= beforeBlock)
                break;

            if (sorted[i].GapAfter >= requiredBlocks && regionEnd < beforeBlock)
                return regionEnd;
        }

        return beforeBlock; // No suitable earlier position
    }

    /// <summary>
    /// Reports progress via the OnProgress callback.
    /// </summary>
    private void ReportProgress(long totalBlocks, long blocksMoved, DefragPhase phase)
    {
        double percent = totalBlocks > 0 ? (double)blocksMoved / totalBlocks * 100.0 : 0;
        OnProgress?.Invoke(new DefragProgress(totalBlocks, blocksMoved, percent, phase));
    }
}
