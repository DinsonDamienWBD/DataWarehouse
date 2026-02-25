using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;

/// <summary>
/// Bitmap-based free space tracker with O(1) amortized single-block allocation.
/// Uses hardware-accelerated bit operations for fast scanning.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: Virtual Disk Engine (VDE-01/VDE-04)")]
public sealed class BitmapAllocator
{
    private readonly byte[] _bitmap;
    private readonly long _totalBlocks;
    private readonly ReaderWriterLockSlim _lock = new();
    private long _nextFreeHint;
    private long _freeBlockCount;

    /// <summary>
    /// Creates a new bitmap allocator with all blocks initially free.
    /// </summary>
    /// <param name="totalBlocks">Total number of blocks to manage.</param>
    public BitmapAllocator(long totalBlocks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(totalBlocks, 0L);

        _totalBlocks = totalBlocks;
        long bitmapBytes = (totalBlocks + 7) / 8;
        _bitmap = new byte[bitmapBytes];
        _freeBlockCount = totalBlocks;
        _nextFreeHint = 0;
    }

    /// <summary>
    /// Creates a bitmap allocator from an existing bitmap.
    /// </summary>
    /// <param name="bitmap">Existing bitmap array.</param>
    /// <param name="totalBlocks">Total number of blocks tracked.</param>
    private BitmapAllocator(byte[] bitmap, long totalBlocks)
    {
        _bitmap = bitmap;
        _totalBlocks = totalBlocks;
        _nextFreeHint = 0;

        // Count free blocks using SIMD-accelerated PopCount
        _freeBlockCount = SimdBitmapScanner.CountZeroBits(bitmap, totalBlocks);
    }

    /// <summary>
    /// Gets the number of free blocks.
    /// </summary>
    public long FreeBlockCount
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _freeBlockCount;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>
    /// Gets the total number of blocks.
    /// </summary>
    public long TotalBlockCount => _totalBlocks;

    /// <summary>
    /// Allocates a single block using O(1) amortized search with next-free hint.
    /// </summary>
    /// <returns>The allocated block number.</returns>
    /// <exception cref="InvalidOperationException">Thrown if no free blocks are available.</exception>
    public long AllocateBlock()
    {
        _lock.EnterWriteLock();
        try
        {
            if (_freeBlockCount == 0)
            {
                throw new InvalidOperationException("No free blocks available.");
            }

            // Start searching from the hint
            long blockNumber = FindNextFreeBlock(_nextFreeHint);

            if (blockNumber < 0)
            {
                // Wrap around and search from the beginning
                blockNumber = FindNextFreeBlock(0);
            }

            if (blockNumber < 0)
            {
                throw new InvalidOperationException("No free blocks found despite non-zero free count.");
            }

            SetBit(blockNumber, true);
            _freeBlockCount--;
            _nextFreeHint = blockNumber + 1;

            return blockNumber;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Allocates a contiguous extent of blocks.
    /// </summary>
    /// <param name="blockCount">Number of contiguous blocks to allocate.</param>
    /// <returns>Array of allocated block numbers.</returns>
    /// <exception cref="InvalidOperationException">Thrown if no contiguous extent of the requested size is available.</exception>
    public long[] AllocateExtent(int blockCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(blockCount, 0);

        _lock.EnterWriteLock();
        try
        {
            if (_freeBlockCount < blockCount)
            {
                throw new InvalidOperationException($"Insufficient free blocks: requested {blockCount}, available {_freeBlockCount}.");
            }

            // Scan for contiguous free blocks
            long startBlock = FindContiguousFreeBlocks(blockCount);

            if (startBlock < 0)
            {
                throw new InvalidOperationException($"No contiguous extent of {blockCount} blocks available.");
            }

            // Allocate the extent
            for (int i = 0; i < blockCount; i++)
            {
                SetBit(startBlock + i, true);
            }

            _freeBlockCount -= blockCount;
            _nextFreeHint = startBlock + blockCount;

            // Return array of block numbers
            var result = new long[blockCount];
            for (int i = 0; i < blockCount; i++)
            {
                result[i] = startBlock + i;
            }

            return result;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Frees a single block.
    /// </summary>
    /// <param name="blockNumber">The block to free.</param>
    public void FreeBlock(long blockNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(blockNumber, _totalBlocks);

        _lock.EnterWriteLock();
        try
        {
            if (!GetBit(blockNumber))
            {
                throw new InvalidOperationException($"Block {blockNumber} is already free.");
            }

            SetBit(blockNumber, false);
            _freeBlockCount++;
            _nextFreeHint = Math.Min(_nextFreeHint, blockNumber);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Frees a contiguous extent of blocks.
    /// </summary>
    /// <param name="startBlock">The first block in the extent.</param>
    /// <param name="blockCount">Number of blocks in the extent.</param>
    public void FreeExtent(long startBlock, int blockCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startBlock);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(blockCount, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(startBlock + blockCount, _totalBlocks);

        _lock.EnterWriteLock();
        try
        {
            for (int i = 0; i < blockCount; i++)
            {
                long block = startBlock + i;
                if (!GetBit(block))
                {
                    throw new InvalidOperationException($"Block {block} is already free.");
                }
                SetBit(block, false);
            }

            _freeBlockCount += blockCount;
            _nextFreeHint = Math.Min(_nextFreeHint, startBlock);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Persists the bitmap to disk.
    /// </summary>
    public async Task PersistAsync(IBlockDevice device, long bitmapStartBlock, CancellationToken ct = default)
    {
        _lock.EnterReadLock();
        try
        {
            long bitmapBytes = (_totalBlocks + 7) / 8;
            long bitmapBlocks = (bitmapBytes + device.BlockSize - 1) / device.BlockSize;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(device.BlockSize);
            try
            {
                for (long i = 0; i < bitmapBlocks; i++)
                {
                    Array.Clear(buffer, 0, device.BlockSize);

                    long srcOffset = i * device.BlockSize;
                    int copyLength = (int)Math.Min(device.BlockSize, bitmapBytes - srcOffset);

                    if (copyLength > 0)
                    {
                        Array.Copy(_bitmap, srcOffset, buffer, 0, copyLength);
                    }

                    await device.WriteBlockAsync(bitmapStartBlock + i, buffer.AsMemory(0, device.BlockSize), ct);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Loads a bitmap allocator from disk.
    /// </summary>
    public static async Task<BitmapAllocator> LoadAsync(
        IBlockDevice device,
        long bitmapStartBlock,
        long bitmapBlockCount,
        long totalDataBlocks,
        CancellationToken ct = default)
    {
        long bitmapBytes = (totalDataBlocks + 7) / 8;
        byte[] bitmap = new byte[bitmapBytes];

        byte[] buffer = ArrayPool<byte>.Shared.Rent(device.BlockSize);
        try
        {
            for (long i = 0; i < bitmapBlockCount; i++)
            {
                await device.ReadBlockAsync(bitmapStartBlock + i, buffer, ct);

                long destOffset = i * device.BlockSize;
                int copyLength = (int)Math.Min(device.BlockSize, bitmapBytes - destOffset);

                if (copyLength > 0)
                {
                    Array.Copy(buffer, 0, bitmap, destOffset, copyLength);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new BitmapAllocator(bitmap, totalDataBlocks);
    }

    /// <summary>
    /// Provides read-only access to the bitmap for extent tree building.
    /// </summary>
    public byte[] GetBitmapSnapshot()
    {
        _lock.EnterReadLock();
        try
        {
            return (byte[])_bitmap.Clone();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private bool GetBit(long bitIndex)
    {
        long byteIndex = bitIndex / 8;
        int bitOffset = (int)(bitIndex % 8);
        return (_bitmap[byteIndex] & (1 << bitOffset)) != 0;
    }

    private void SetBit(long bitIndex, bool value)
    {
        long byteIndex = bitIndex / 8;
        int bitOffset = (int)(bitIndex % 8);

        if (value)
        {
            _bitmap[byteIndex] |= (byte)(1 << bitOffset);
        }
        else
        {
            _bitmap[byteIndex] &= (byte)~(1 << bitOffset);
        }
    }

    private long FindNextFreeBlock(long startBlock)
    {
        return SimdBitmapScanner.FindFirstZeroBit(_bitmap, _totalBlocks, startBlock);
    }

    private long FindContiguousFreeBlocks(int count)
    {
        return SimdBitmapScanner.FindContiguousZeroBits(_bitmap, _totalBlocks, count);
    }
}
