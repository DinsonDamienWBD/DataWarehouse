using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Fixed-size bucket holding inode entries for the extendible hash table.
/// Each bucket has a local depth indicating how many hash bits it uses for
/// entry distribution. Buckets split independently when full, incrementing
/// their local depth and redistributing entries based on the new bit.
/// </summary>
/// <remarks>
/// On-disk format: [LocalDepth:4][Count:4][Entries: InodeId:8 + BlockNumber:8 each][Zero-padding].
/// Bucket capacity is derived from block size: (blockSize - HeaderSize) / EntrySize.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-11 Extendible hashing")]
public sealed class ExtendibleHashBucket
{
    /// <summary>
    /// Header size in bytes: [LocalDepth:4][Count:4].
    /// </summary>
    public const int HeaderSize = 8;

    /// <summary>
    /// Size of each entry in bytes: [InodeId:8][BlockNumber:8].
    /// </summary>
    public const int EntrySize = 16;

    /// <summary>
    /// Gets or sets the number of hash bits this bucket uses for entry distribution.
    /// Starts at 0 and grows on split. When LocalDepth equals the table's GlobalDepth,
    /// a split triggers directory doubling.
    /// </summary>
    public int LocalDepth { get; set; }

    /// <summary>
    /// Gets the maximum number of entries this bucket can hold, derived from block size.
    /// </summary>
    public int Capacity { get; }

    /// <summary>
    /// Gets the entries stored in this bucket: (InodeId, BlockNumber) pairs.
    /// </summary>
    public List<(ulong InodeId, long BlockNumber)> Entries { get; } = new();

    /// <summary>
    /// Gets or sets the VDE block number storing this bucket on disk.
    /// </summary>
    public long BlockNumber { get; set; }

    /// <summary>
    /// Gets whether this bucket has reached its entry capacity.
    /// </summary>
    public bool IsFull => Entries.Count >= Capacity;

    /// <summary>
    /// Initializes a new <see cref="ExtendibleHashBucket"/> with the given block size.
    /// </summary>
    /// <param name="blockSize">VDE block size in bytes.</param>
    /// <param name="localDepth">Initial local depth (default 0).</param>
    public ExtendibleHashBucket(int blockSize, int localDepth = 0)
    {
        if (blockSize < HeaderSize + EntrySize)
            throw new ArgumentOutOfRangeException(nameof(blockSize), "Block size too small for even one entry.");

        Capacity = (blockSize - HeaderSize) / EntrySize;
        LocalDepth = localDepth;
    }

    /// <summary>
    /// Splits this bucket into two new buckets by redistributing entries based on
    /// the (LocalDepth+1)th bit of the hash. Both new buckets have LocalDepth = old + 1.
    /// </summary>
    /// <param name="hashFunc">Hash function matching the table's hash function.</param>
    /// <param name="blockSize">Block size for the new buckets.</param>
    /// <returns>
    /// A tuple of (lowBucket, highBucket) where lowBucket receives entries whose
    /// (LocalDepth)th bit is 0, and highBucket receives entries whose bit is 1.
    /// </returns>
    public (ExtendibleHashBucket Low, ExtendibleHashBucket High) Split(
        Func<ulong, ulong> hashFunc, int blockSize)
    {
        int newDepth = LocalDepth + 1;
        var low = new ExtendibleHashBucket(blockSize, newDepth);
        var high = new ExtendibleHashBucket(blockSize, newDepth);

        ulong bitMask = 1UL << LocalDepth;

        foreach (var entry in Entries)
        {
            ulong hash = hashFunc(entry.InodeId);
            if ((hash & bitMask) == 0)
                low.Entries.Add(entry);
            else
                high.Entries.Add(entry);
        }

        return (low, high);
    }

    /// <summary>
    /// Serializes this bucket to a byte array suitable for writing to a VDE block.
    /// Format: [LocalDepth:4 LE][Count:4 LE][Entries: InodeId:8 LE + BlockNumber:8 LE each][Zero-pad].
    /// </summary>
    /// <param name="blockSize">Target block size; output is zero-padded to this length.</param>
    /// <returns>Serialized bucket data of exactly <paramref name="blockSize"/> bytes.</returns>
    public byte[] Serialize(int blockSize)
    {
        var data = new byte[blockSize];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0, 4), LocalDepth);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), Entries.Count);

        int offset = HeaderSize;
        foreach (var (inodeId, block) in Entries)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset, 8), inodeId);
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset + 8, 8), block);
            offset += EntrySize;
        }

        return data;
    }

    /// <summary>
    /// Deserializes a bucket from raw block data.
    /// </summary>
    /// <param name="data">Raw block data (at least <see cref="HeaderSize"/> bytes).</param>
    /// <param name="blockSize">Block size used to compute capacity.</param>
    /// <returns>A populated <see cref="ExtendibleHashBucket"/>.</returns>
    public static ExtendibleHashBucket Deserialize(ReadOnlySpan<byte> data, int blockSize)
    {
        int localDepth = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(0, 4));
        int count = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4, 4));

        var bucket = new ExtendibleHashBucket(blockSize, localDepth);

        int offset = HeaderSize;
        for (int i = 0; i < count; i++)
        {
            ulong inodeId = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
            long block = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset + 8, 8));
            bucket.Entries.Add((inodeId, block));
            offset += EntrySize;
        }

        return bucket;
    }
}
