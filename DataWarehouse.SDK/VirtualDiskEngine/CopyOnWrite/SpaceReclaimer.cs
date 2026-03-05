using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using DataWarehouse.SDK.VirtualDiskEngine.Metadata;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.CopyOnWrite;

/// <summary>
/// Result of a space reclamation operation.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: VDE CoW engine (VDE-06)")]
public sealed record ReclaimResult
{
    /// <summary>
    /// Number of blocks processed for reclamation.
    /// </summary>
    public long BlocksProcessed { get; init; }

    /// <summary>
    /// Number of blocks actually freed (refCount reached 0).
    /// </summary>
    public long BlocksFreed { get; init; }

    /// <summary>
    /// Number of blocks still shared with other snapshots (refCount > 0).
    /// </summary>
    public long BlocksStillShared { get; init; }
}

/// <summary>
/// Manages reference-counted space reclamation for snapshot deletion.
/// </summary>
/// <remarks>
/// When a snapshot is deleted, all blocks referenced by that snapshot have their
/// reference counts decremented. Blocks with refCount reaching 0 are freed.
/// This class provides utilities for collecting block numbers and estimating
/// reclaimable space.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 33: VDE CoW engine (VDE-06)")]
public sealed class SpaceReclaimer
{
    private readonly ICowEngine _cowEngine;
    private readonly IBlockAllocator _allocator;

    /// <summary>
    /// Initializes a new instance of the <see cref="SpaceReclaimer"/> class.
    /// </summary>
    /// <param name="cowEngine">Copy-on-write engine for reference counting.</param>
    /// <param name="allocator">Block allocator for tracking free space.</param>
    public SpaceReclaimer(ICowEngine cowEngine, IBlockAllocator allocator)
    {
        _cowEngine = cowEngine ?? throw new ArgumentNullException(nameof(cowEngine));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
    }

    /// <summary>
    /// Reclaims blocks by decrementing their reference counts and freeing blocks at zero.
    /// </summary>
    /// <param name="blockNumbers">Block numbers to reclaim.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Reclamation result with counts of processed, freed, and shared blocks.</returns>
    public async Task<ReclaimResult> ReclaimBlocksAsync(IEnumerable<long> blockNumbers, CancellationToken ct = default)
    {
        if (blockNumbers == null)
        {
            throw new ArgumentNullException(nameof(blockNumbers));
        }

        var blockList = blockNumbers.ToList();
        long blocksProcessed = blockList.Count;
        long blocksFreed = 0;
        long blocksStillShared = 0;

        // Batch decrement all reference counts
        // The CowEngine will handle freeing blocks that reach refCount == 0
        long freeBlocksBefore = _allocator.FreeBlockCount;

        await _cowEngine.DecrementRefBatchAsync(blockList, ct);

        long freeBlocksAfter = _allocator.FreeBlockCount;
        blocksFreed = freeBlocksAfter - freeBlocksBefore;
        blocksStillShared = blocksProcessed - blocksFreed;

        return new ReclaimResult
        {
            BlocksProcessed = blocksProcessed,
            BlocksFreed = blocksFreed,
            BlocksStillShared = blocksStillShared
        };
    }

    /// <summary>
    /// Collects all block numbers referenced by an inode tree using bounded stack-based traversal.
    /// </summary>
    /// <param name="rootInodeNumber">Root inode number to start from.</param>
    /// <param name="inodeTable">Inode table for accessing file system metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="blockDevice">
    /// Optional block device used to traverse indirect and double-indirect pointer chains (finding 775).
    /// When null the pointer blocks are collected but their referenced data blocks are not.
    /// </param>
    /// <returns>List of all referenced block numbers.</returns>
    /// <remarks>
    /// Uses an iterative (non-recursive) approach to avoid stack overflow on deep directory trees.
    /// </remarks>
    public async Task<IReadOnlyList<long>> CollectBlockNumbersAsync(
        long rootInodeNumber,
        IInodeTable inodeTable,
        CancellationToken ct = default,
        IBlockDevice? blockDevice = null)
    {
        if (inodeTable == null)
        {
            throw new ArgumentNullException(nameof(inodeTable));
        }

        var blockNumbers = new HashSet<long>();
        var inodeStack = new Stack<long>();
        inodeStack.Push(rootInodeNumber);

        while (inodeStack.Count > 0)
        {
            long currentInodeNumber = inodeStack.Pop();
            Inode? inode = await inodeTable.GetInodeAsync(currentInodeNumber, ct);

            if (inode == null)
            {
                continue;
            }

            // Collect direct block pointers
            foreach (long blockNumber in inode.DirectBlockPointers)
            {
                if (blockNumber > 0)
                {
                    blockNumbers.Add(blockNumber);
                }
            }

            // Collect indirect block pointer and, if a block device is available,
            // all data blocks it references (finding 775).
            if (inode.IndirectBlockPointer > 0)
            {
                blockNumbers.Add(inode.IndirectBlockPointer);
                if (blockDevice != null)
                    await CollectIndirectBlockReferencesAsync(inode.IndirectBlockPointer, blockNumbers, blockDevice, ct).ConfigureAwait(false);
            }

            // Collect double indirect block pointer and recursively collect data blocks.
            if (inode.DoubleIndirectPointer > 0)
            {
                blockNumbers.Add(inode.DoubleIndirectPointer);
                if (blockDevice != null)
                    await CollectDoubleIndirectBlockReferencesAsync(inode.DoubleIndirectPointer, blockNumbers, blockDevice, ct).ConfigureAwait(false);
            }

            // Collect extended attributes block
            if (inode.ExtendedAttributesBlock > 0)
            {
                blockNumbers.Add(inode.ExtendedAttributesBlock);
            }

            // If this is a directory, recurse into child inodes
            if (inode.Type == InodeType.Directory)
            {
                var entries = await inodeTable.ReadDirectoryAsync(currentInodeNumber, ct);
                foreach (var entry in entries)
                {
                    // Skip self and parent references
                    if (entry.Name != "." && entry.Name != "..")
                    {
                        inodeStack.Push(entry.InodeNumber);
                    }
                }
            }
        }

        return blockNumbers.ToList();
    }

    /// <summary>
    /// Collects data block numbers stored in a single-level indirect block.
    /// </summary>
    private static async Task CollectIndirectBlockReferencesAsync(
        long indirectBlockPointer, HashSet<long> blockNumbers, IBlockDevice blockDevice, CancellationToken ct)
    {
        int blockSize = blockDevice.BlockSize;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(blockSize);
        try
        {
            await blockDevice.ReadBlockAsync(indirectBlockPointer, buffer.AsMemory(0, blockSize), ct).ConfigureAwait(false);

            int pointerCount = blockSize / 8;
            for (int i = 0; i < pointerCount; i++)
            {
                long dataBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(i * 8, 8));
                if (dataBlock > 0)
                    blockNumbers.Add(dataBlock);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Collects data block numbers stored across a double-indirect block chain.
    /// </summary>
    private static async Task CollectDoubleIndirectBlockReferencesAsync(
        long doubleIndirectPointer, HashSet<long> blockNumbers, IBlockDevice blockDevice, CancellationToken ct)
    {
        int blockSize = blockDevice.BlockSize;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(blockSize);
        try
        {
            await blockDevice.ReadBlockAsync(doubleIndirectPointer, buffer.AsMemory(0, blockSize), ct).ConfigureAwait(false);

            int pointerCount = blockSize / 8;
            for (int i = 0; i < pointerCount; i++)
            {
                long indirectBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(i * 8, 8));
                if (indirectBlock > 0)
                {
                    blockNumbers.Add(indirectBlock);
                    await CollectIndirectBlockReferencesAsync(indirectBlock, blockNumbers, blockDevice, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Estimates the amount of space that would be reclaimed if a snapshot were deleted.
    /// </summary>
    /// <param name="snapshotName">Name of the snapshot to estimate.</param>
    /// <param name="snapshots">Snapshot manager for accessing snapshot metadata.</param>
    /// <param name="inodeTable">Inode table for accessing file system metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="blockDevice">Optional block device for traversing indirect block chains (finding 775).</param>
    /// <returns>Estimated number of bytes that would be reclaimed.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the snapshot does not exist.</exception>
    public async Task<long> EstimateReclaimableSpaceAsync(
        string snapshotName,
        SnapshotManager snapshots,
        IInodeTable inodeTable,
        CancellationToken ct = default,
        IBlockDevice? blockDevice = null)
    {
        if (snapshots == null)
        {
            throw new ArgumentNullException(nameof(snapshots));
        }

        if (inodeTable == null)
        {
            throw new ArgumentNullException(nameof(inodeTable));
        }

        var snapshot = await snapshots.GetSnapshotAsync(snapshotName, ct);
        if (snapshot == null)
        {
            throw new InvalidOperationException($"Snapshot '{snapshotName}' not found.");
        }

        // Collect all block numbers for this snapshot
        var blockNumbers = await CollectBlockNumbersAsync(snapshot.RootInodeNumber, inodeTable, ct, blockDevice);

        long reclaimableBlocks = 0;

        // Check reference counts: blocks with refCount == 1 will be freed
        foreach (long blockNumber in blockNumbers)
        {
            int refCount = await _cowEngine.GetRefCountAsync(blockNumber, ct);
            if (refCount == 1)
            {
                reclaimableBlocks++;
            }
        }

        // Use actual block size from device if available (finding 789: hardcoded 4096 was wrong).
        int effectiveBlockSize = blockDevice?.BlockSize ?? DataWarehouse.SDK.VirtualDiskEngine.VdeConstants.DefaultBlockSize;
        return reclaimableBlocks * effectiveBlockSize;
    }

    /// <summary>
    /// Performs a mark-sweep garbage collection pass to identify and free unreferenced blocks.
    /// </summary>
    /// <param name="snapshots">Snapshot manager for accessing all snapshots.</param>
    /// <param name="inodeTable">Inode table for accessing file system metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Reclamation result.</returns>
    /// <remarks>
    /// This is a comprehensive GC operation that should be run periodically in the background.
    /// It walks all live snapshots and the current file system state to mark referenced blocks,
    /// then sweeps unreferenced blocks.
    /// </remarks>
    public async Task<ReclaimResult> MarkSweepGarbageCollectAsync(
        SnapshotManager snapshots,
        IInodeTable inodeTable,
        CancellationToken ct = default,
        IBlockDevice? blockDevice = null)
    {
        if (snapshots == null)
        {
            throw new ArgumentNullException(nameof(snapshots));
        }

        if (inodeTable == null)
        {
            throw new ArgumentNullException(nameof(inodeTable));
        }

        var referencedBlocks = new HashSet<long>();

        // Mark phase: collect all blocks referenced by all snapshots
        var allSnapshots = await snapshots.ListSnapshotsAsync(ct);
        foreach (var snapshot in allSnapshots)
        {
            var blockNumbers = await CollectBlockNumbersAsync(snapshot.RootInodeNumber, inodeTable, ct, blockDevice);
            foreach (long blockNumber in blockNumbers)
            {
                referencedBlocks.Add(blockNumber);
            }
        }

        // Also mark blocks referenced by the current root inode (live file system)
        var liveBlocks = await CollectBlockNumbersAsync(inodeTable.RootInode.InodeNumber, inodeTable, ct, blockDevice);
        foreach (long blockNumber in liveBlocks)
        {
            referencedBlocks.Add(blockNumber);
        }

        // Sweep phase: iterate all block addresses and free any that are allocated but not referenced.
        // We scan block 0 through TotalBlockCount-1, skipping block 0 (reserved superblock).
        long totalAllocatedBlocks = _allocator.TotalBlockCount - _allocator.FreeBlockCount;
        long blocksFreed = 0;
        long totalBlocks = _allocator.TotalBlockCount;

        for (long blockNum = 1; blockNum < totalBlocks; blockNum++)
        {
            ct.ThrowIfCancellationRequested();

            // Skip referenced blocks
            if (referencedBlocks.Contains(blockNum))
                continue;

            // Free any allocated block that is not referenced by any live snapshot or the current FS
            if (_allocator.IsAllocated(blockNum))
            {
                _allocator.FreeBlock(blockNum);
                blocksFreed++;
            }
        }

        System.Diagnostics.Debug.WriteLine(
            $"[SpaceReclaimer.MarkSweep] Scanned {totalBlocks} blocks, {referencedBlocks.Count} referenced, {blocksFreed} freed");

        return new ReclaimResult
        {
            BlocksProcessed = totalAllocatedBlocks,
            BlocksFreed = blocksFreed,
            BlocksStillShared = referencedBlocks.Count
        };
    }
}
