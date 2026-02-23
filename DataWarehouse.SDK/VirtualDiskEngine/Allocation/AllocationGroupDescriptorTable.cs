using System;
using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Allocation;

/// <summary>
/// Manages all allocation groups for a VDE, providing a unified interface for
/// block allocation across the entire data region. The descriptor table is
/// stored in a dedicated VDE region identified by <see cref="BlockTypeTags.BMAP"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Allocation group descriptor table (VOPT-02)")]
public sealed class AllocationGroupDescriptorTable : IDisposable
{
    /// <summary>
    /// Size in bytes of a single group descriptor on disk.
    /// Layout: GroupId(4) + StartBlock(8) + BlockCount(8) + FreeCount(8) + Policy(1) + Reserved(3) = 32 bytes.
    /// </summary>
    public const int DescriptorSize = 32;

    /// <summary>
    /// Block type tag for the allocation group descriptor region.
    /// </summary>
    public const uint RegionTag = BlockTypeTags.BMAP;

    private readonly AllocationGroup[] _groups;
    private readonly int _blockSize;
    private bool _disposed;

    /// <summary>
    /// Gets the number of allocation groups.
    /// </summary>
    public int GroupCount => _groups.Length;

    /// <summary>
    /// Gets the total number of free blocks across all groups.
    /// </summary>
    public long TotalFreeBlocks
    {
        get
        {
            long total = 0;
            for (int i = 0; i < _groups.Length; i++)
                total += _groups[i].FreeBlockCount;
            return total;
        }
    }

    /// <summary>
    /// Creates a new descriptor table that partitions the data region into allocation groups.
    /// </summary>
    /// <param name="totalDataBlocks">Total number of blocks in the data region.</param>
    /// <param name="blockSize">Size of each block in bytes.</param>
    /// <param name="groupSizeBytes">Target size per allocation group in bytes (default 128 MB).</param>
    public AllocationGroupDescriptorTable(long totalDataBlocks, int blockSize, long groupSizeBytes = AllocationGroup.DefaultGroupSizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalDataBlocks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(groupSizeBytes);

        _blockSize = blockSize;

        long totalDataBytes = totalDataBlocks * blockSize;
        int groupCount = Math.Max(1, (int)Math.Ceiling((double)totalDataBytes / groupSizeBytes));

        long blocksPerGroup = totalDataBlocks / groupCount;
        long remainder = totalDataBlocks % groupCount;

        _groups = new AllocationGroup[groupCount];
        long currentBlock = 0;

        for (int i = 0; i < groupCount; i++)
        {
            // Distribute remainder blocks to the last group
            long groupBlocks = (i == groupCount - 1) ? blocksPerGroup + remainder : blocksPerGroup;
            _groups[i] = new AllocationGroup(i, currentBlock, groupBlocks);
            currentBlock += groupBlocks;
        }
    }

    /// <summary>
    /// Private constructor for deserialization.
    /// </summary>
    private AllocationGroupDescriptorTable(AllocationGroup[] groups, int blockSize)
    {
        _groups = groups;
        _blockSize = blockSize;
    }

    /// <summary>
    /// Gets an allocation group by its identifier.
    /// </summary>
    /// <param name="groupId">The group identifier.</param>
    /// <returns>The requested allocation group.</returns>
    public AllocationGroup GetGroup(int groupId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(groupId);
        if (groupId >= _groups.Length)
            throw new ArgumentOutOfRangeException(nameof(groupId), $"Group {groupId} does not exist (max: {_groups.Length - 1}).");
        return _groups[groupId];
    }

    /// <summary>
    /// Determines which allocation group owns the specified block.
    /// </summary>
    /// <param name="blockNumber">The absolute block number.</param>
    /// <returns>The allocation group that manages the block.</returns>
    public AllocationGroup GetGroupForBlock(long blockNumber)
    {
        for (int i = _groups.Length - 1; i >= 0; i--)
        {
            if (blockNumber >= _groups[i].StartBlock)
                return _groups[i];
        }

        throw new ArgumentOutOfRangeException(nameof(blockNumber), $"Block {blockNumber} is not within any allocation group.");
    }

    /// <summary>
    /// Allocates a single block from the preferred group, or the first non-full group.
    /// </summary>
    /// <param name="preferredGroup">Preferred group index, or -1 for automatic selection.</param>
    /// <returns>The absolute block number of the allocated block.</returns>
    /// <exception cref="InvalidOperationException">All groups are full.</exception>
    public long AllocateBlock(int preferredGroup = -1)
    {
        if (preferredGroup >= 0 && preferredGroup < _groups.Length)
        {
            if (_groups[preferredGroup].FreeBlockCount > 0)
                return _groups[preferredGroup].AllocateBlock();
        }

        for (int i = 0; i < _groups.Length; i++)
        {
            if (_groups[i].FreeBlockCount > 0)
                return _groups[i].AllocateBlock();
        }

        throw new InvalidOperationException("All allocation groups are full.");
    }

    /// <summary>
    /// Allocates a contiguous extent of blocks from the preferred group, or the first group
    /// with sufficient contiguous space.
    /// </summary>
    /// <param name="blockCount">Number of contiguous blocks to allocate.</param>
    /// <param name="preferredGroup">Preferred group index, or -1 for automatic selection.</param>
    /// <returns>Array of absolute block numbers (contiguous sequence).</returns>
    /// <exception cref="InvalidOperationException">No group has a contiguous extent of the requested size.</exception>
    public long[] AllocateExtent(int blockCount, int preferredGroup = -1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockCount);

        if (preferredGroup >= 0 && preferredGroup < _groups.Length)
        {
            if (_groups[preferredGroup].FreeBlockCount >= blockCount)
            {
                try { return _groups[preferredGroup].AllocateExtent(blockCount); }
                catch (InvalidOperationException) { /* Fall through to scan */ }
            }
        }

        for (int i = 0; i < _groups.Length; i++)
        {
            if (_groups[i].FreeBlockCount >= blockCount)
            {
                try { return _groups[i].AllocateExtent(blockCount); }
                catch (InvalidOperationException) { continue; }
            }
        }

        throw new InvalidOperationException($"No allocation group has a contiguous extent of {blockCount} blocks.");
    }

    /// <summary>
    /// Frees a single block, routing to the correct allocation group.
    /// </summary>
    /// <param name="blockNumber">The absolute block number to free.</param>
    public void FreeBlock(long blockNumber)
    {
        GetGroupForBlock(blockNumber).FreeBlock(blockNumber);
    }

    /// <summary>
    /// Frees a contiguous extent of blocks, routing to the correct group(s).
    /// If the extent spans multiple groups, each portion is freed in its respective group.
    /// </summary>
    /// <param name="startBlock">The first block to free.</param>
    /// <param name="blockCount">Number of blocks to free.</param>
    public void FreeExtent(long startBlock, int blockCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockCount);

        long current = startBlock;
        int remaining = blockCount;

        while (remaining > 0)
        {
            var group = GetGroupForBlock(current);
            long groupEnd = group.StartBlock + group.BlockCount;
            int countInGroup = (int)Math.Min(remaining, groupEnd - current);

            group.FreeExtent(current, countInGroup);
            current += countInGroup;
            remaining -= countInGroup;
        }
    }

    /// <summary>
    /// Serializes all group descriptors into the provided buffer.
    /// Each descriptor is <see cref="DescriptorSize"/> (32) bytes.
    /// </summary>
    /// <param name="buffer">Target buffer (must be at least GroupCount * DescriptorSize bytes).</param>
    public void SerializeDescriptors(Span<byte> buffer)
    {
        for (int i = 0; i < _groups.Length; i++)
        {
            var group = _groups[i];
            int offset = i * DescriptorSize;
            var slot = buffer.Slice(offset, DescriptorSize);

            BinaryPrimitives.WriteInt32LittleEndian(slot, group.GroupId);
            BinaryPrimitives.WriteInt64LittleEndian(slot.Slice(4), group.StartBlock);
            BinaryPrimitives.WriteInt64LittleEndian(slot.Slice(12), group.BlockCount);
            BinaryPrimitives.WriteInt64LittleEndian(slot.Slice(20), group.FreeBlockCount);
            slot[28] = (byte)group.Policy;
            // Bytes 29-31 reserved (zero)
            slot[29] = 0;
            slot[30] = 0;
            slot[31] = 0;
        }
    }

    /// <summary>
    /// Deserializes group descriptors from a binary buffer and reconstructs the descriptor table.
    /// Bitmap data must be loaded separately per group via <see cref="AllocationGroup.Deserialize"/>.
    /// </summary>
    /// <param name="buffer">Source buffer containing serialized descriptors.</param>
    /// <param name="groupCount">Number of groups in the buffer.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>A reconstructed <see cref="AllocationGroupDescriptorTable"/>.</returns>
    public static AllocationGroupDescriptorTable DeserializeDescriptors(ReadOnlySpan<byte> buffer, int groupCount, int blockSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(groupCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);

        var groups = new AllocationGroup[groupCount];

        for (int i = 0; i < groupCount; i++)
        {
            int offset = i * DescriptorSize;
            var slot = buffer.Slice(offset, DescriptorSize);

            int groupId = BinaryPrimitives.ReadInt32LittleEndian(slot);
            long startBlock = BinaryPrimitives.ReadInt64LittleEndian(slot.Slice(4));
            long blockCount = BinaryPrimitives.ReadInt64LittleEndian(slot.Slice(12));
            long freeCount = BinaryPrimitives.ReadInt64LittleEndian(slot.Slice(20));
            var policy = (AllocationPolicy)slot[28];

            // Create group with correct range, then its bitmap will be loaded separately
            var group = new AllocationGroup(groupId, startBlock, blockCount, policy);
            groups[i] = group;
        }

        return new AllocationGroupDescriptorTable(groups, blockSize);
    }

    /// <summary>
    /// Gets the total size in bytes needed to serialize all group descriptors.
    /// </summary>
    public int GetDescriptorTableSize() => _groups.Length * DescriptorSize;

    /// <summary>
    /// Releases all allocation groups and their locks.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        for (int i = 0; i < _groups.Length; i++)
            _groups[i].Dispose();
    }
}
