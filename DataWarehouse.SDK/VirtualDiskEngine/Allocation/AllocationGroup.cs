using System;
using System.Buffers.Binary;
using System.Threading;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Allocation;

/// <summary>
/// A single allocation group within the VDE data region. Each group manages
/// an independent bitmap with per-group locking, eliminating cross-group
/// contention during parallel writes.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Allocation group with per-group bitmap and lock (VOPT-01)")]
public sealed class AllocationGroup : IDisposable
{
    /// <summary>
    /// Default allocation group size in bytes (128 MB).
    /// For small VDEs (&lt;128MB), a single group covers the entire data region.
    /// </summary>
    public const long DefaultGroupSizeBytes = 128L * 1024 * 1024;

    private readonly byte[] _bitmap;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private long _freeBlockCount;
    private bool _disposed;

    /// <summary>
    /// Gets the unique identifier of this allocation group.
    /// </summary>
    public int GroupId { get; }

    /// <summary>
    /// Gets the absolute block number where this group starts in the data region.
    /// </summary>
    public long StartBlock { get; }

    /// <summary>
    /// Gets the total number of blocks managed by this group.
    /// </summary>
    public long BlockCount { get; }

    /// <summary>
    /// Gets the allocation policy for this group.
    /// </summary>
    public AllocationPolicy Policy { get; }

    /// <summary>
    /// Gets the number of free (unallocated) blocks in this group.
    /// </summary>
    public long FreeBlockCount
    {
        get
        {
            _lock.EnterReadLock();
            try { return _freeBlockCount; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Computes the fragmentation ratio for this group.
    /// 0.0 means all free space is contiguous; approaches 1.0 as free space
    /// becomes scattered across many fragments.
    /// </summary>
    public double FragmentationRatio
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                if (_freeBlockCount == 0 || _freeBlockCount == BlockCount)
                    return 0.0;

                long fragmentCount = 0;
                bool inFreeRun = false;

                for (long i = 0; i < BlockCount; i++)
                {
                    bool isFree = IsBitSet(i);
                    if (isFree && !inFreeRun)
                    {
                        fragmentCount++;
                        inFreeRun = true;
                    }
                    else if (!isFree)
                    {
                        inFreeRun = false;
                    }
                }

                if (fragmentCount <= 1)
                    return 0.0;

                // Ratio of fragment count to free block count; capped at 1.0
                return Math.Min(1.0, (double)(fragmentCount - 1) / _freeBlockCount);
            }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Creates a new allocation group with all blocks initially free.
    /// </summary>
    /// <param name="groupId">Unique group identifier.</param>
    /// <param name="startBlock">Absolute block number where this group starts.</param>
    /// <param name="blockCount">Number of blocks in this group.</param>
    /// <param name="policy">Allocation policy (FirstFit or BestFit).</param>
    public AllocationGroup(int groupId, long startBlock, long blockCount, AllocationPolicy policy = AllocationPolicy.FirstFit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(groupId);
        ArgumentOutOfRangeException.ThrowIfNegative(startBlock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockCount);

        GroupId = groupId;
        StartBlock = startBlock;
        BlockCount = blockCount;
        Policy = policy;

        // One bit per block, rounded up to full bytes
        int bitmapBytes = (int)((blockCount + 7) / 8);
        _bitmap = new byte[bitmapBytes];

        // Set all bits to 1 (free)
        Array.Fill(_bitmap, (byte)0xFF);

        // Clear any trailing bits beyond BlockCount
        int trailingBits = (int)(blockCount % 8);
        if (trailingBits > 0)
        {
            _bitmap[bitmapBytes - 1] = (byte)(0xFF >> (8 - trailingBits));
        }

        _freeBlockCount = blockCount;
    }

    /// <summary>
    /// Allocates a single block using the group's allocation policy.
    /// </summary>
    /// <returns>The absolute block number of the allocated block.</returns>
    /// <exception cref="InvalidOperationException">No free blocks available.</exception>
    public long AllocateBlock()
    {
        _lock.EnterWriteLock();
        try
        {
            if (_freeBlockCount == 0)
                throw new InvalidOperationException($"Allocation group {GroupId} is full.");

            long localBlock = Policy switch
            {
                AllocationPolicy.FirstFit => FindFirstFree(),
                AllocationPolicy.BestFit => FindBestFitSingle(),
                _ => FindFirstFree()
            };

            ClearBit(localBlock);
            _freeBlockCount--;
            return StartBlock + localBlock;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Allocates a contiguous extent of blocks.
    /// </summary>
    /// <param name="count">Number of contiguous blocks to allocate.</param>
    /// <returns>Array of absolute block numbers (contiguous sequence).</returns>
    /// <exception cref="InvalidOperationException">No contiguous run of the requested size.</exception>
    public long[] AllocateExtent(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        _lock.EnterWriteLock();
        try
        {
            if (_freeBlockCount < count)
                throw new InvalidOperationException($"Allocation group {GroupId} has insufficient free blocks ({_freeBlockCount} < {count}).");

            long start = Policy switch
            {
                AllocationPolicy.FirstFit => FindFirstFitExtent(count),
                AllocationPolicy.BestFit => FindBestFitExtent(count),
                _ => FindFirstFitExtent(count)
            };

            if (start < 0)
                throw new InvalidOperationException($"Allocation group {GroupId} has no contiguous extent of size {count}.");

            var result = new long[count];
            for (int i = 0; i < count; i++)
            {
                ClearBit(start + i);
                result[i] = StartBlock + start + i;
            }
            _freeBlockCount -= count;
            return result;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Frees a single block.
    /// </summary>
    /// <param name="blockNumber">Absolute block number to free.</param>
    public void FreeBlock(long blockNumber)
    {
        _lock.EnterWriteLock();
        try
        {
            long local = ValidateAndGetLocal(blockNumber);
            if (IsBitSet(local))
                throw new InvalidOperationException($"Block {blockNumber} is already free.");

            SetBit(local);
            _freeBlockCount++;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Frees a contiguous range of blocks.
    /// </summary>
    /// <param name="startBlock">Absolute block number of the first block to free.</param>
    /// <param name="blockCount">Number of blocks to free.</param>
    public void FreeExtent(long startBlock, int blockCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockCount);

        _lock.EnterWriteLock();
        try
        {
            for (int i = 0; i < blockCount; i++)
            {
                long local = ValidateAndGetLocal(startBlock + i);
                if (IsBitSet(local))
                    throw new InvalidOperationException($"Block {startBlock + i} is already free.");

                SetBit(local);
                _freeBlockCount++;
            }
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Serializes the group's bitmap and metadata into the provided buffer.
    /// Format: [FreeCount:8][Policy:1][BitmapLength:4][Bitmap:N]
    /// </summary>
    /// <param name="buffer">Target buffer (must be at least <see cref="GetSerializedSize"/> bytes).</param>
    public void Serialize(Span<byte> buffer)
    {
        _lock.EnterReadLock();
        try
        {
            int offset = 0;
            BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), _freeBlockCount);
            offset += 8;
            buffer[offset] = (byte)Policy;
            offset += 1;
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset), _bitmap.Length);
            offset += 4;
            _bitmap.AsSpan().CopyTo(buffer.Slice(offset));
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Deserializes an allocation group from a binary buffer.
    /// </summary>
    /// <param name="buffer">Source buffer containing serialized group data.</param>
    /// <param name="groupId">Group identifier to assign.</param>
    /// <param name="startBlock">Absolute start block for this group.</param>
    /// <param name="blockCount">Number of blocks in this group.</param>
    /// <returns>A deserialized <see cref="AllocationGroup"/>.</returns>
    public static AllocationGroup Deserialize(ReadOnlySpan<byte> buffer, int groupId, long startBlock, long blockCount)
    {
        int offset = 0;
        long freeCount = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;
        var policy = (AllocationPolicy)buffer[offset];
        offset += 1;
        int bitmapLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset));
        offset += 4;

        var group = new AllocationGroup(groupId, startBlock, blockCount, policy);
        buffer.Slice(offset, bitmapLength).CopyTo(group._bitmap);
        group._freeBlockCount = freeCount;

        return group;
    }

    /// <summary>
    /// Gets the total serialized size in bytes for this group.
    /// </summary>
    public int GetSerializedSize() => 8 + 1 + 4 + _bitmap.Length;

    /// <summary>
    /// Releases the reader-writer lock.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lock.Dispose();
    }

    // ── Private helpers ─────────────────────────────────────────────────

    private long ValidateAndGetLocal(long absoluteBlock)
    {
        long local = absoluteBlock - StartBlock;
        if (local < 0 || local >= BlockCount)
            throw new ArgumentOutOfRangeException(nameof(absoluteBlock),
                $"Block {absoluteBlock} is outside group {GroupId} range [{StartBlock}..{StartBlock + BlockCount - 1}].");
        return local;
    }

    private bool IsBitSet(long localBlock)
    {
        int byteIndex = (int)(localBlock / 8);
        int bitIndex = (int)(localBlock % 8);
        return (_bitmap[byteIndex] & (1 << bitIndex)) != 0;
    }

    private void SetBit(long localBlock)
    {
        int byteIndex = (int)(localBlock / 8);
        int bitIndex = (int)(localBlock % 8);
        _bitmap[byteIndex] |= (byte)(1 << bitIndex);
    }

    private void ClearBit(long localBlock)
    {
        int byteIndex = (int)(localBlock / 8);
        int bitIndex = (int)(localBlock % 8);
        _bitmap[byteIndex] &= (byte)~(1 << bitIndex);
    }

    private long FindFirstFree()
    {
        for (long i = 0; i < BlockCount; i++)
        {
            if (IsBitSet(i))
                return i;
        }
        throw new InvalidOperationException($"Allocation group {GroupId} bitmap inconsistency: free count > 0 but no free bit found.");
    }

    private long FindBestFitSingle()
    {
        // For a single block, best-fit is equivalent to first-fit
        return FindFirstFree();
    }

    private long FindFirstFitExtent(int count)
    {
        long runStart = -1;
        int runLength = 0;

        for (long i = 0; i < BlockCount; i++)
        {
            if (IsBitSet(i))
            {
                if (runStart < 0)
                    runStart = i;
                runLength++;
                if (runLength >= count)
                    return runStart;
            }
            else
            {
                runStart = -1;
                runLength = 0;
            }
        }

        return -1;
    }

    private long FindBestFitExtent(int count)
    {
        long bestStart = -1;
        long bestLength = long.MaxValue;
        long runStart = -1;
        int runLength = 0;

        for (long i = 0; i <= BlockCount; i++)
        {
            bool isFree = i < BlockCount && IsBitSet(i);
            if (isFree)
            {
                if (runStart < 0)
                    runStart = i;
                runLength++;
            }
            else
            {
                if (runLength >= count && runLength < bestLength)
                {
                    bestStart = runStart;
                    bestLength = runLength;
                }
                runStart = -1;
                runLength = 0;
            }
        }

        return bestStart;
    }
}
