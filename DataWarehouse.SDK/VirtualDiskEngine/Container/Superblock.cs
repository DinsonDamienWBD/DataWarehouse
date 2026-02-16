using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers.Binary;
using System.IO.Hashing;

namespace DataWarehouse.SDK.VirtualDiskEngine.Container;

/// <summary>
/// On-disk superblock structure for DWVD container files.
/// Contains metadata about the container format, block layout, and integrity checksum.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: Virtual Disk Engine (VDE-01/VDE-04)")]
public readonly struct Superblock
{
    /// <summary>Magic bytes: 0x44575644 ("DWVD").</summary>
    public required uint Magic { get; init; }

    /// <summary>Container format version.</summary>
    public required ushort FormatVersion { get; init; }

    /// <summary>Flags (reserved for future use).</summary>
    public required ushort Flags { get; init; }

    /// <summary>Block size in bytes.</summary>
    public required int BlockSize { get; init; }

    /// <summary>Total number of blocks in the container.</summary>
    public required long TotalBlocks { get; init; }

    /// <summary>Number of free blocks available for allocation.</summary>
    public required long FreeBlocks { get; init; }

    /// <summary>Block number where the inode table starts.</summary>
    public required long InodeTableBlock { get; init; }

    /// <summary>Block number where the free space bitmap starts.</summary>
    public required long BitmapStartBlock { get; init; }

    /// <summary>Number of blocks occupied by the bitmap.</summary>
    public required long BitmapBlockCount { get; init; }

    /// <summary>Block number of the B-Tree root node.</summary>
    public required long BTreeRootBlock { get; init; }

    /// <summary>Block number where the Write-Ahead Log starts.</summary>
    public required long WalStartBlock { get; init; }

    /// <summary>Number of blocks reserved for the WAL.</summary>
    public required long WalBlockCount { get; init; }

    /// <summary>Block number of the checksum table.</summary>
    public required long ChecksumTableBlock { get; init; }

    /// <summary>Timestamp when the container was created (Unix ticks, UTC).</summary>
    public required long CreatedTimestampUtc { get; init; }

    /// <summary>Timestamp of the last modification (Unix ticks, UTC).</summary>
    public required long ModifiedTimestampUtc { get; init; }

    /// <summary>Checkpoint sequence number (incremented on each checkpoint).</summary>
    public required long CheckpointSequence { get; init; }

    /// <summary>XxHash64 checksum of all preceding fields.</summary>
    public required ulong Checksum { get; init; }

    /// <summary>
    /// Validates the superblock: checks magic bytes and checksum.
    /// </summary>
    /// <returns>True if valid, false otherwise.</returns>
    public bool IsValid()
    {
        if (Magic != VdeConstants.MagicBytes)
        {
            return false;
        }

        // Serialize and recompute checksum to verify integrity
        Span<byte> buffer = stackalloc byte[VdeConstants.SuperblockSize];
        Serialize(this, buffer);
        ulong computedChecksum = ComputeChecksum(buffer);

        return Checksum == computedChecksum;
    }

    /// <summary>
    /// Serializes the superblock to a byte buffer using little-endian encoding.
    /// </summary>
    /// <param name="sb">The superblock to serialize.</param>
    /// <param name="buffer">Buffer to write to. Must be at least <see cref="VdeConstants.SuperblockSize"/> bytes.</param>
    public static void Serialize(Superblock sb, Span<byte> buffer)
    {
        if (buffer.Length < VdeConstants.SuperblockSize)
        {
            throw new ArgumentException($"Buffer must be at least {VdeConstants.SuperblockSize} bytes.", nameof(buffer));
        }

        buffer.Clear();

        int offset = 0;

        BinaryPrimitives.WriteUInt32LittleEndian(buffer[offset..], sb.Magic);
        offset += 4;

        BinaryPrimitives.WriteUInt16LittleEndian(buffer[offset..], sb.FormatVersion);
        offset += 2;

        BinaryPrimitives.WriteUInt16LittleEndian(buffer[offset..], sb.Flags);
        offset += 2;

        BinaryPrimitives.WriteInt32LittleEndian(buffer[offset..], sb.BlockSize);
        offset += 4;

        BinaryPrimitives.WriteInt64LittleEndian(buffer[offset..], sb.TotalBlocks);
        offset += 8;

        BinaryPrimitives.WriteInt64LittleEndian(buffer[offset..], sb.FreeBlocks);
        offset += 8;

        BinaryPrimitives.WriteInt64LittleEndian(buffer[offset..], sb.InodeTableBlock);
        offset += 8;

        BinaryPrimitives.WriteInt64LittleEndian(buffer[offset..], sb.BitmapStartBlock);
        offset += 8;

        BinaryPrimitives.WriteInt64LittleEndian(buffer[offset..], sb.BitmapBlockCount);
        offset += 8;

        BinaryPrimitives.WriteInt64LittleEndian(buffer[offset..], sb.BTreeRootBlock);
        offset += 8;

        BinaryPrimitives.WriteInt64LittleEndian(buffer[offset..], sb.WalStartBlock);
        offset += 8;

        BinaryPrimitives.WriteInt64LittleEndian(buffer[offset..], sb.WalBlockCount);
        offset += 8;

        BinaryPrimitives.WriteInt64LittleEndian(buffer[offset..], sb.ChecksumTableBlock);
        offset += 8;

        BinaryPrimitives.WriteInt64LittleEndian(buffer[offset..], sb.CreatedTimestampUtc);
        offset += 8;

        BinaryPrimitives.WriteInt64LittleEndian(buffer[offset..], sb.ModifiedTimestampUtc);
        offset += 8;

        BinaryPrimitives.WriteInt64LittleEndian(buffer[offset..], sb.CheckpointSequence);
        offset += 8;

        // Compute checksum over all fields except the checksum itself
        ulong checksum = ComputeChecksum(buffer[..offset]);

        BinaryPrimitives.WriteUInt64LittleEndian(buffer[offset..], checksum);
    }

    /// <summary>
    /// Deserializes a superblock from a byte buffer.
    /// </summary>
    /// <param name="buffer">Buffer containing the serialized superblock.</param>
    /// <param name="sb">The deserialized superblock.</param>
    /// <returns>True if deserialization succeeded and magic/checksum are valid, false otherwise.</returns>
    public static bool Deserialize(ReadOnlySpan<byte> buffer, out Superblock sb)
    {
        sb = default;

        if (buffer.Length < VdeConstants.SuperblockSize)
        {
            return false;
        }

        int offset = 0;

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]);
        offset += 4;

        if (magic != VdeConstants.MagicBytes)
        {
            return false;
        }

        ushort formatVersion = BinaryPrimitives.ReadUInt16LittleEndian(buffer[offset..]);
        offset += 2;

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(buffer[offset..]);
        offset += 2;

        int blockSize = BinaryPrimitives.ReadInt32LittleEndian(buffer[offset..]);
        offset += 4;

        long totalBlocks = BinaryPrimitives.ReadInt64LittleEndian(buffer[offset..]);
        offset += 8;

        long freeBlocks = BinaryPrimitives.ReadInt64LittleEndian(buffer[offset..]);
        offset += 8;

        long inodeTableBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer[offset..]);
        offset += 8;

        long bitmapStartBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer[offset..]);
        offset += 8;

        long bitmapBlockCount = BinaryPrimitives.ReadInt64LittleEndian(buffer[offset..]);
        offset += 8;

        long bTreeRootBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer[offset..]);
        offset += 8;

        long walStartBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer[offset..]);
        offset += 8;

        long walBlockCount = BinaryPrimitives.ReadInt64LittleEndian(buffer[offset..]);
        offset += 8;

        long checksumTableBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer[offset..]);
        offset += 8;

        long createdTimestampUtc = BinaryPrimitives.ReadInt64LittleEndian(buffer[offset..]);
        offset += 8;

        long modifiedTimestampUtc = BinaryPrimitives.ReadInt64LittleEndian(buffer[offset..]);
        offset += 8;

        long checkpointSequence = BinaryPrimitives.ReadInt64LittleEndian(buffer[offset..]);
        offset += 8;

        ulong checksum = BinaryPrimitives.ReadUInt64LittleEndian(buffer[offset..]);

        // Verify checksum (hash all bytes before the checksum field)
        ulong computedChecksum = ComputeChecksum(buffer[..(offset)]);

        if (checksum != computedChecksum)
        {
            return false;
        }

        sb = new Superblock
        {
            Magic = magic,
            FormatVersion = formatVersion,
            Flags = flags,
            BlockSize = blockSize,
            TotalBlocks = totalBlocks,
            FreeBlocks = freeBlocks,
            InodeTableBlock = inodeTableBlock,
            BitmapStartBlock = bitmapStartBlock,
            BitmapBlockCount = bitmapBlockCount,
            BTreeRootBlock = bTreeRootBlock,
            WalStartBlock = walStartBlock,
            WalBlockCount = walBlockCount,
            ChecksumTableBlock = checksumTableBlock,
            CreatedTimestampUtc = createdTimestampUtc,
            ModifiedTimestampUtc = modifiedTimestampUtc,
            CheckpointSequence = checkpointSequence,
            Checksum = checksum
        };

        return true;
    }

    /// <summary>
    /// Computes XxHash64 checksum over the provided buffer.
    /// </summary>
    /// <param name="buffer">Data to hash.</param>
    /// <returns>64-bit hash value.</returns>
    public static ulong ComputeChecksum(ReadOnlySpan<byte> buffer)
    {
        return XxHash64.HashToUInt64(buffer);
    }
}
