using DataWarehouse.SDK.Contracts;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;

/// <summary>
/// Interface for block allocation and free space management.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: Virtual Disk Engine (VDE-01/VDE-04)")]
public interface IBlockAllocator
{
    /// <summary>
    /// Allocates a single block.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The allocated block number.</returns>
    /// <exception cref="System.InvalidOperationException">Thrown if no free blocks are available.</exception>
    long AllocateBlock(CancellationToken ct = default);

    /// <summary>
    /// Allocates a contiguous extent of blocks.
    /// </summary>
    /// <param name="blockCount">Number of contiguous blocks to allocate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Array of allocated block numbers (contiguous sequence).</returns>
    /// <exception cref="System.InvalidOperationException">Thrown if no contiguous extent of the requested size is available.</exception>
    long[] AllocateExtent(int blockCount, CancellationToken ct = default);

    /// <summary>
    /// Frees a single block.
    /// </summary>
    /// <param name="blockNumber">The block number to free.</param>
    void FreeBlock(long blockNumber);

    /// <summary>
    /// Frees a contiguous extent of blocks.
    /// </summary>
    /// <param name="startBlock">The first block in the extent.</param>
    /// <param name="blockCount">Number of blocks in the extent.</param>
    void FreeExtent(long startBlock, int blockCount);

    /// <summary>
    /// Gets the number of free blocks available.
    /// </summary>
    long FreeBlockCount { get; }

    /// <summary>
    /// Gets the total number of blocks managed by this allocator.
    /// </summary>
    long TotalBlockCount { get; }

    /// <summary>
    /// Gets the fragmentation ratio (0.0 = no fragmentation, 1.0 = maximum fragmentation).
    /// Hint for defragmentation decisions.
    /// </summary>
    double FragmentationRatio { get; }

    /// <summary>
    /// Returns true if the given block is currently allocated (not free).
    /// </summary>
    /// <param name="blockNumber">Block number to query.</param>
    /// <returns><c>true</c> if allocated; <c>false</c> if free.</returns>
    bool IsAllocated(long blockNumber);

    /// <summary>
    /// Persists the allocation state to disk.
    /// </summary>
    /// <param name="device">Block device to write to.</param>
    /// <param name="bitmapStartBlock">Block number where the bitmap starts.</param>
    /// <param name="ct">Cancellation token.</param>
    Task PersistAsync(IBlockDevice device, long bitmapStartBlock, CancellationToken ct = default);
}
