using DataWarehouse.SDK.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;

/// <summary>
/// Coordinates bitmap and extent tree for unified block allocation strategy.
/// Delegates single-block allocation to bitmap (O(1)), multi-block allocation to extent tree (best-fit).
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: Virtual Disk Engine (VDE-01/VDE-04)")]
public sealed class FreeSpaceManager : IBlockAllocator
{
    private readonly BitmapAllocator _bitmap;
    private readonly ExtentTree _extents;
    // _syncLock for synchronous callers (FreeBlock, FreeExtent, AllocateBlock) — avoids
    // the SemaphoreSlim.Wait() deadlock risk when called from async continuations (finding P2-760).
    private readonly object _syncLock = new();
    private readonly SemaphoreSlim _asyncLock = new(1, 1);

    /// <summary>
    /// Creates a new free space manager.
    /// </summary>
    /// <param name="bitmap">The bitmap allocator.</param>
    /// <param name="extents">The extent tree (built from bitmap).</param>
    public FreeSpaceManager(BitmapAllocator bitmap, ExtentTree extents)
    {
        _bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
        _extents = extents ?? throw new ArgumentNullException(nameof(extents));
    }

    /// <inheritdoc/>
    public long FreeBlockCount => _bitmap.FreeBlockCount;

    /// <inheritdoc/>
    public long TotalBlockCount => _bitmap.TotalBlockCount;

    /// <inheritdoc/>
    public double FragmentationRatio
    {
        get
        {
            // Simple fragmentation heuristic: ratio of extents to free blocks
            // Low ratio = large contiguous regions, high ratio = many small fragments
            long freeBlocks = FreeBlockCount;
            if (freeBlocks == 0) return 0.0;

            int extentCount = _extents.ExtentCount;
            if (extentCount == 0) return 0.0;

            // Ideal: 1 extent for all free blocks → ratio = 1/freeBlocks ≈ 0
            // Worst: 1 extent per free block → ratio = freeBlocks/freeBlocks = 1
            return Math.Min(1.0, extentCount / (double)freeBlocks);
        }
    }

    /// <inheritdoc/>
    public long AllocateBlock(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncLock)
        {
            long block = _bitmap.AllocateBlock();

            // Update extent tree (remove single-block extent if it exists)
            var extent = new FreeExtent(block, 1);
            _extents.RemoveExtent(extent);

            return block;
        }
    }

    /// <inheritdoc/>
    public long[] AllocateExtent(int blockCount, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(blockCount, 0);
        ct.ThrowIfCancellationRequested();

        lock (_syncLock)
        {
            // Try to find a suitable extent from the extent tree (best-fit)
            var extent = _extents.FindExtent(blockCount);

            if (extent != null)
            {
                var result = _bitmap.AllocateExtent(blockCount);
                _extents.SplitExtent(extent, blockCount);
                return result;
            }
            else
            {
                // Fall back to bitmap scan
                return _bitmap.AllocateExtent(blockCount);
            }
        }
    }

    /// <inheritdoc/>
    public bool IsAllocated(long blockNumber)
    {
        return _bitmap.IsAllocated(blockNumber);
    }

    /// <inheritdoc/>
    public void FreeBlock(long blockNumber)
    {
        // Use regular lock (not SemaphoreSlim.Wait) to avoid deadlock from async continuations (finding P2-760).
        lock (_syncLock)
        {
            _bitmap.FreeBlock(blockNumber);
            _extents.AddFreeExtent(blockNumber, 1);
        }
    }

    /// <inheritdoc/>
    public void FreeExtent(long startBlock, int blockCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startBlock);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(blockCount, 0);

        // Use regular lock (not SemaphoreSlim.Wait) to avoid deadlock from async continuations (finding P2-760).
        lock (_syncLock)
        {
            _bitmap.FreeExtent(startBlock, blockCount);
            _extents.AddFreeExtent(startBlock, blockCount);
        }
    }

    /// <inheritdoc/>
    public async Task PersistAsync(IBlockDevice device, long bitmapStartBlock, CancellationToken ct = default)
    {
        await _asyncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Persist bitmap to disk (extent tree is derived from bitmap on load)
            await _bitmap.PersistAsync(device, bitmapStartBlock, ct);
        }
        finally
        {
            _asyncLock.Release();
        }
    }

    /// <summary>
    /// Loads a free space manager from disk.
    /// Reads the bitmap and rebuilds the extent tree.
    /// </summary>
    public static async Task<IBlockAllocator> LoadAsync(
        IBlockDevice device,
        long bitmapStartBlock,
        long bitmapBlockCount,
        long totalDataBlocks,
        CancellationToken ct = default)
    {
        // Load bitmap
        var bitmap = await BitmapAllocator.LoadAsync(device, bitmapStartBlock, bitmapBlockCount, totalDataBlocks, ct);

        // Build extent tree from bitmap
        var extents = new ExtentTree();
        var bitmapSnapshot = bitmap.GetBitmapSnapshot();
        extents.BuildFromBitmap(bitmapSnapshot, totalDataBlocks);

        return new FreeSpaceManager(bitmap, extents);
    }
}
