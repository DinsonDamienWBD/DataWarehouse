using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Flags for an individual extent entry describing on-disk block properties.
/// Multiple flags may be combined (e.g., Compressed | Encrypted).
/// </summary>
[Flags]
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 inode extent flags (VDE2-05)")]
public enum ExtentFlags : uint
{
    /// <summary>No special flags.</summary>
    None = 0,

    /// <summary>Extent data is compressed.</summary>
    Compressed = 1 << 0,

    /// <summary>Extent data is already compressed (e.g., JPEG, MP4) and should not be re-compressed.</summary>
    Precompressed = 1 << 1,

    /// <summary>Extent data is encrypted.</summary>
    Encrypted = 1 << 2,

    /// <summary>Sparse hole: no data stored on disk for this extent range.</summary>
    Hole = 1 << 3,

    /// <summary>Shared via copy-on-write snapshot; must be copied before modification.</summary>
    SharedCow = 1 << 4,
}

/// <summary>
/// A 24-byte extent descriptor that maps a contiguous range of physical blocks to a
/// logical byte offset within a file. Up to <see cref="FormatConstants.MaxExtentsPerInode"/>
/// extents are stored inline in the inode; overflow goes to indirect extent blocks.
/// </summary>
/// <remarks>
/// Wire format (24 bytes, little-endian):
/// <code>
///   [0..8)   StartBlock     (long)   - physical block number
///   [8..12)  BlockCount     (int)    - number of contiguous blocks
///   [12..16) Flags          (uint)   - ExtentFlags
///   [16..24) LogicalOffset  (long)   - logical byte offset within file
/// </code>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 extent descriptor (VDE2-05)")]
public readonly struct InodeExtent : IEquatable<InodeExtent>
{
    /// <summary>Serialized size of an extent entry in bytes.</summary>
    public const int SerializedSize = 24;

    /// <summary>Physical block number where this extent starts.</summary>
    public long StartBlock { get; }

    /// <summary>Number of contiguous blocks in this extent.</summary>
    public int BlockCount { get; }

    /// <summary>Extent property flags (compression, encryption, sparse, CoW).</summary>
    public ExtentFlags Flags { get; }

    /// <summary>Logical byte offset within the file that this extent covers.</summary>
    public long LogicalOffset { get; }

    /// <summary>Creates a new extent descriptor.</summary>
    public InodeExtent(long startBlock, int blockCount, ExtentFlags flags, long logicalOffset)
    {
        StartBlock = startBlock;
        BlockCount = blockCount;
        Flags = flags;
        LogicalOffset = logicalOffset;
    }

    /// <summary>
    /// Computes the physical size in bytes that this extent occupies on disk.
    /// </summary>
    /// <param name="blockSize">The block size in bytes (e.g., 4096).</param>
    /// <returns>Total bytes = BlockCount * blockSize.</returns>
    public long PhysicalSize(int blockSize) => (long)BlockCount * blockSize;

    /// <summary>True if this extent is a sparse hole (no data on disk).</summary>
    public bool IsSparse => Flags.HasFlag(ExtentFlags.Hole);

    /// <summary>True if this extent is shared via copy-on-write and must be copied before modification.</summary>
    public bool IsShared => Flags.HasFlag(ExtentFlags.SharedCow);

    /// <summary>True if this extent represents no data (zero start block and zero block count).</summary>
    public bool IsEmpty => StartBlock == 0 && BlockCount == 0;

    /// <summary>
    /// Serializes this extent to exactly 24 bytes in little-endian format.
    /// </summary>
    /// <param name="ext">The extent to serialize.</param>
    /// <param name="buffer">Target buffer (must be at least 24 bytes).</param>
    public static void Serialize(in InodeExtent ext, Span<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException($"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        BinaryPrimitives.WriteInt64LittleEndian(buffer[..8], ext.StartBlock);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[8..12], ext.BlockCount);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[12..16], (uint)ext.Flags);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[16..24], ext.LogicalOffset);
    }

    /// <summary>
    /// Deserializes an extent from exactly 24 bytes in little-endian format.
    /// </summary>
    /// <param name="buffer">Source buffer (must be at least 24 bytes).</param>
    /// <returns>The deserialized extent.</returns>
    public static InodeExtent Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException($"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        long startBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer[..8]);
        int blockCount = BinaryPrimitives.ReadInt32LittleEndian(buffer[8..12]);
        var flags = (ExtentFlags)BinaryPrimitives.ReadUInt32LittleEndian(buffer[12..16]);
        long logicalOffset = BinaryPrimitives.ReadInt64LittleEndian(buffer[16..24]);

        return new InodeExtent(startBlock, blockCount, flags, logicalOffset);
    }

    /// <inheritdoc />
    public bool Equals(InodeExtent other)
        => StartBlock == other.StartBlock
        && BlockCount == other.BlockCount
        && Flags == other.Flags
        && LogicalOffset == other.LogicalOffset;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is InodeExtent other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(StartBlock, BlockCount, Flags, LogicalOffset);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(InodeExtent left, InodeExtent right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(InodeExtent left, InodeExtent right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString()
        => $"Extent(Block={StartBlock}, Count={BlockCount}, Offset={LogicalOffset}, Flags={Flags})";
}
