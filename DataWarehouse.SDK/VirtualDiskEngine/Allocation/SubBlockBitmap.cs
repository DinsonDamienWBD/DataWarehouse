using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Allocation;

/// <summary>
/// Secondary bitmap tracking sub-block slot occupancy within shared blocks in an
/// allocation group. Each shared block is divided into fixed-size slots (power-of-2,
/// from 64 to 2048 bytes) and this bitmap tracks which slots are occupied.
/// </summary>
/// <remarks>
/// <para>
/// Sub-block packing eliminates internal fragmentation for small objects (config files,
/// metadata records, tags) that don't fill a full block. The bitmap operates alongside
/// the allocation group's primary block bitmap.
/// </para>
/// <para>
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
/// <b>Thread safety:</b> This class is NOT thread-safe. The caller (e.g., <c>SubBlockPacker</c>)
/// must hold its own lock before calling any method on this instance. Do not share a single
/// <see cref="SubBlockBitmap"/> across threads without external synchronization (finding 766).
=======
=======
>>>>>>> Stashed changes
=======
>>>>>>> Stashed changes
=======
>>>>>>> Stashed changes
/// <b>Thread safety:</b> This class is <em>not</em> thread-safe. The caller
/// (e.g., <c>SubBlockPacker</c>) is responsible for acquiring its own lock before
/// invoking any method on this instance. Do not share a single <see cref="SubBlockBitmap"/>
/// across threads without external synchronization (finding 766).
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
>>>>>>> Stashed changes
=======
>>>>>>> Stashed changes
=======
>>>>>>> Stashed changes
=======
>>>>>>> Stashed changes
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Sub-block slot bitmap for tail-merged shared blocks (VOPT-11)")]
public sealed class SubBlockBitmap
{
    /// <summary>
    /// Supported sub-block slot size classes (power-of-2 from 64 to 2048 bytes).
    /// </summary>
    public static ReadOnlySpan<int> SlotSizeClasses => new[] { 64, 128, 256, 512, 1024, 2048 };

    private readonly Dictionary<long, byte[]> _blockSlotBitmaps = new();
    private readonly int _blockSize;
    private readonly int _minSlotSize;
    private readonly int _slotsPerBlock;

    /// <summary>
    /// Gets the block size in bytes.
    /// </summary>
    public int BlockSize => _blockSize;

    /// <summary>
    /// Gets the minimum (finest-grained) slot size in bytes.
    /// </summary>
    public int MinSlotSize => _minSlotSize;

    /// <summary>
    /// Gets the number of slots per block at the minimum slot size.
    /// </summary>
    public int SlotsPerBlock => _slotsPerBlock;

    /// <summary>
    /// Gets the number of blocks currently tracked by this bitmap.
    /// </summary>
    public int TrackedBlockCount => _blockSlotBitmaps.Count;

    /// <summary>
    /// Creates a new sub-block bitmap for tracking slot occupancy.
    /// </summary>
    /// <param name="blockSize">Block size in bytes (must be power-of-2, typically 4096).</param>
    /// <param name="minSlotSize">Minimum slot size in bytes (must be power-of-2, default 64).</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if sizes are invalid.</exception>
    public SubBlockBitmap(int blockSize, int minSlotSize = 64)
    {
        if (blockSize <= 0 || (blockSize & (blockSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize), "Block size must be a positive power-of-2.");
        if (minSlotSize <= 0 || (minSlotSize & (minSlotSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(minSlotSize), "Minimum slot size must be a positive power-of-2.");
        if (minSlotSize > blockSize)
            throw new ArgumentOutOfRangeException(nameof(minSlotSize), "Minimum slot size cannot exceed block size.");

        _blockSize = blockSize;
        _minSlotSize = minSlotSize;
        _slotsPerBlock = blockSize / minSlotSize;
    }

    /// <summary>
    /// Allocates a slot of the requested size class within the specified block.
    /// </summary>
    /// <param name="blockNumber">The block number to allocate within.</param>
    /// <param name="slotSize">Requested slot size in bytes (will be rounded up to nearest size class).</param>
    /// <param name="slotIndex">When successful, receives the zero-based slot index within the block.</param>
    /// <returns><c>true</c> if a free slot was found and allocated; <c>false</c> if the block is full.</returns>
    public bool AllocateSlot(long blockNumber, int slotSize, out int slotIndex)
    {
        slotIndex = -1;
        int effectiveSlotSize = RoundUpToSlotClass(slotSize);
        int slotsNeeded = effectiveSlotSize / _minSlotSize;

        if (!_blockSlotBitmaps.TryGetValue(blockNumber, out byte[]? bitmap))
        {
            // First allocation in this block: create empty bitmap (all free)
            bitmap = CreateEmptyBitmap();
            _blockSlotBitmaps[blockNumber] = bitmap;
        }

        // Search for a contiguous run of free slots aligned to the size class
        int alignmentSlots = slotsNeeded; // Slots must be aligned to their size class
        for (int i = 0; i <= _slotsPerBlock - slotsNeeded; i += alignmentSlots)
        {
            bool allFree = true;
            for (int j = 0; j < slotsNeeded; j++)
            {
                if (IsBitSet(bitmap, i + j))
                {
                    allFree = false;
                    break;
                }
            }

            if (allFree)
            {
                // Mark all slots in this range as occupied
                for (int j = 0; j < slotsNeeded; j++)
                {
                    SetBit(bitmap, i + j);
                }
                slotIndex = i;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Frees a previously allocated slot within the specified block.
    /// </summary>
    /// <param name="blockNumber">The block number containing the slot.</param>
    /// <param name="slotIndex">The zero-based slot index to free.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the slot index is invalid.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the block is not tracked.</exception>
    public void FreeSlot(long blockNumber, int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= _slotsPerBlock)
            throw new ArgumentOutOfRangeException(nameof(slotIndex), $"Slot index must be in range [0, {_slotsPerBlock}).");

        if (!_blockSlotBitmaps.TryGetValue(blockNumber, out byte[]? bitmap))
            throw new InvalidOperationException($"Block {blockNumber} is not tracked by sub-block bitmap.");

        ClearBit(bitmap, slotIndex);
    }

    /// <summary>
    /// Checks whether all slots in the specified block are free.
    /// </summary>
    /// <param name="blockNumber">The block number to check.</param>
    /// <returns><c>true</c> if all slots are free (block can be returned to primary bitmap).</returns>
    public bool IsBlockFullyFree(long blockNumber)
    {
        if (!_blockSlotBitmaps.TryGetValue(blockNumber, out byte[]? bitmap))
            return true; // Untracked blocks are considered fully free

        int bitmapBytes = (int)(((long)_slotsPerBlock + 7) / 8);
        for (int i = 0; i < bitmapBytes; i++)
        {
            // Check if any bit is set (occupied)
            byte mask = (i == bitmapBytes - 1 && _slotsPerBlock % 8 != 0)
                ? (byte)(0xFF >> (8 - (_slotsPerBlock % 8)))
                : (byte)0xFF;

            if ((bitmap[i] & mask) != 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks whether all slots in the specified block are occupied.
    /// </summary>
    /// <param name="blockNumber">The block number to check.</param>
    /// <returns><c>true</c> if no free slots remain in this block.</returns>
    public bool IsBlockFullyOccupied(long blockNumber)
    {
        if (!_blockSlotBitmaps.TryGetValue(blockNumber, out byte[]? bitmap))
            return false; // Untracked blocks are fully free

        int bitmapBytes = (int)(((long)_slotsPerBlock + 7) / 8);
        for (int i = 0; i < bitmapBytes; i++)
        {
            byte mask = (i == bitmapBytes - 1 && _slotsPerBlock % 8 != 0)
                ? (byte)(0xFF >> (8 - (_slotsPerBlock % 8)))
                : (byte)0xFF;

            if ((bitmap[i] & mask) != mask)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Counts the number of free (unoccupied) slots in the specified block.
    /// </summary>
    /// <param name="blockNumber">The block number to count free slots for.</param>
    /// <returns>The number of available slots at the minimum slot size granularity.</returns>
    public int FreeSlotCount(long blockNumber)
    {
        if (!_blockSlotBitmaps.TryGetValue(blockNumber, out byte[]? bitmap))
            return _slotsPerBlock; // Untracked blocks are fully free

        int freeCount = 0;
        for (int i = 0; i < _slotsPerBlock; i++)
        {
            if (!IsBitSet(bitmap, i))
                freeCount++;
        }

        return freeCount;
    }

    /// <summary>
    /// Removes tracking for a block (called when a fully-free block is returned to
    /// the primary allocation bitmap).
    /// </summary>
    /// <param name="blockNumber">The block number to stop tracking.</param>
    public void RemoveBlock(long blockNumber)
    {
        _blockSlotBitmaps.Remove(blockNumber);
    }

    /// <summary>
    /// Gets the block numbers of all tracked shared blocks that have at least one
    /// free slot of the specified size class.
    /// </summary>
    /// <param name="slotSize">Required slot size in bytes.</param>
    /// <returns>Enumeration of block numbers with available space.</returns>
    public IEnumerable<long> GetBlocksWithFreeSlots(int slotSize)
    {
        int effectiveSlotSize = RoundUpToSlotClass(slotSize);
        int slotsNeeded = effectiveSlotSize / _minSlotSize;
        int alignmentSlots = slotsNeeded;

        foreach (var (blockNumber, bitmap) in _blockSlotBitmaps)
        {
            for (int i = 0; i <= _slotsPerBlock - slotsNeeded; i += alignmentSlots)
            {
                bool allFree = true;
                for (int j = 0; j < slotsNeeded; j++)
                {
                    if (IsBitSet(bitmap, i + j))
                    {
                        allFree = false;
                        break;
                    }
                }

                if (allFree)
                {
                    yield return blockNumber;
                    break; // Only need to find one free run per block
                }
            }
        }
    }

    /// <summary>
    /// Serializes the sub-block bitmap to a binary buffer.
    /// Format: [BlockSize:4][MinSlotSize:4][EntryCount:4][{BlockNumber:8, BitmapBytes:N}...]
    /// </summary>
    /// <param name="buffer">Target buffer (must be at least <see cref="GetSerializedSize"/> bytes).</param>
    public void Serialize(Span<byte> buffer)
    {
        int offset = 0;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset), _blockSize);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset), _minSlotSize);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset), _blockSlotBitmaps.Count);
        offset += 4;

        int bitmapBytes = (int)(((long)_slotsPerBlock + 7) / 8);
        foreach (var (blockNumber, bitmap) in _blockSlotBitmaps)
        {
            BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), blockNumber);
            offset += 8;
            bitmap.AsSpan(0, bitmapBytes).CopyTo(buffer.Slice(offset));
            offset += bitmapBytes;
        }
    }

    /// <summary>
    /// Gets the total serialized size in bytes.
    /// </summary>
    public int GetSerializedSize()
    {
        int bitmapBytes = (int)(((long)_slotsPerBlock + 7) / 8);
        return 4 + 4 + 4 + _blockSlotBitmaps.Count * (8 + bitmapBytes);
    }

    /// <summary>
    /// Deserializes a sub-block bitmap from a binary buffer.
    /// </summary>
    /// <param name="buffer">Source buffer containing serialized bitmap data.</param>
    /// <param name="blockSize">Expected block size (for validation).</param>
    /// <param name="minSlotSize">Expected minimum slot size (for validation).</param>
    /// <returns>A deserialized <see cref="SubBlockBitmap"/>.</returns>
    public static SubBlockBitmap Deserialize(ReadOnlySpan<byte> buffer, int blockSize, int minSlotSize)
    {
        int offset = 0;
        int storedBlockSize = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset));
        offset += 4;
        int storedMinSlotSize = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset));
        offset += 4;
        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset));
        offset += 4;

        if (storedBlockSize != blockSize)
            throw new InvalidOperationException($"Block size mismatch: stored {storedBlockSize}, expected {blockSize}.");
        if (storedMinSlotSize != minSlotSize)
            throw new InvalidOperationException($"Min slot size mismatch: stored {storedMinSlotSize}, expected {minSlotSize}.");

        var result = new SubBlockBitmap(blockSize, minSlotSize);
        int bitmapBytes = (int)(((long)result._slotsPerBlock + 7) / 8);

        for (int i = 0; i < entryCount; i++)
        {
            long blockNumber = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
            offset += 8;
            byte[] bitmap = new byte[bitmapBytes];
            buffer.Slice(offset, bitmapBytes).CopyTo(bitmap);
            offset += bitmapBytes;
            result._blockSlotBitmaps[blockNumber] = bitmap;
        }

        return result;
    }

    // ── Private helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Rounds a requested size up to the nearest supported slot size class.
    /// </summary>
    internal static int RoundUpToSlotClass(int size)
    {
        foreach (int slotSize in SlotSizeClasses)
        {
            if (size <= slotSize)
                return slotSize;
        }

        // If larger than the biggest slot class, return the size rounded up to power-of-2
        int result = 1;
        while (result < size)
            result <<= 1;
        return result;
    }

    private byte[] CreateEmptyBitmap()
    {
        int bitmapBytes = (int)(((long)_slotsPerBlock + 7) / 8);
        return new byte[bitmapBytes];
    }

    private static bool IsBitSet(byte[] bitmap, int slotIndex)
    {
        int byteIndex = slotIndex / 8;
        int bitIndex = slotIndex % 8;
        return (bitmap[byteIndex] & (1 << bitIndex)) != 0;
    }

    private static void SetBit(byte[] bitmap, int slotIndex)
    {
        int byteIndex = slotIndex / 8;
        int bitIndex = slotIndex % 8;
        bitmap[byteIndex] |= (byte)(1 << bitIndex);
    }

    private static void ClearBit(byte[] bitmap, int slotIndex)
    {
        int byteIndex = slotIndex / 8;
        int bitIndex = slotIndex % 8;
        bitmap[byteIndex] &= (byte)~(1 << bitIndex);
    }
}
