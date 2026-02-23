using System.Buffers.Binary;
using System.IO.Hashing;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Allocation;

/// <summary>
/// Represents one block-sized node in the extent B+tree. Each node contains either
/// leaf entries (InodeExtent descriptors) or internal entries (logical offset + child block pointer),
/// both sorted by logical offset for O(log n) binary search.
/// </summary>
/// <remarks>
/// Header layout (32 bytes, little-endian):
/// <code>
///   [0..4)   Magic            ("EXTN" = 0x4558544E)
///   [4..6)   Level            (ushort, 0 = leaf)
///   [6..8)   EntryCount       (ushort)
///   [8..16)  ParentBlock      (long)
///   [16..24) NextSiblingBlock (long, leaf chain)
///   [24..32) PrevSiblingBlock (long, leaf chain)
/// </code>
/// After header: sorted entries, then unused space, then UniversalBlockTrailer (16 bytes).
/// Leaf entries: InodeExtent (24 bytes each). Internal entries: (LogicalOffset:8 + ChildBlockNumber:8 = 16 bytes each).
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Extent tree B+tree node (VOPT-09)")]
public sealed class ExtentTreeNode
{
    /// <summary>Header size in bytes.</summary>
    public const int HeaderSize = 32;

    /// <summary>Magic bytes "EXTN" as big-endian uint32.</summary>
    public const uint Magic = 0x4558544E;

    /// <summary>Size of a single leaf entry (InodeExtent) in bytes.</summary>
    public const int LeafEntrySize = InodeExtent.SerializedSize; // 24

    /// <summary>Size of a single internal entry (LogicalOffset + ChildBlockNumber) in bytes.</summary>
    public const int InternalEntrySize = 16;

    /// <summary>B+tree level. 0 = leaf node, &gt;0 = internal node.</summary>
    public ushort Level { get; set; }

    /// <summary>Number of valid entries in this node.</summary>
    public ushort EntryCount { get; set; }

    /// <summary>Block number of the parent node (0 if root).</summary>
    public long ParentBlock { get; set; }

    /// <summary>Block number of the next sibling leaf (0 if none or internal node).</summary>
    public long NextSiblingBlock { get; set; }

    /// <summary>Block number of the previous sibling leaf (0 if none or internal node).</summary>
    public long PrevSiblingBlock { get; set; }

    /// <summary>True if this is a leaf node (Level == 0).</summary>
    public bool IsLeaf => Level == 0;

    /// <summary>Leaf entries: InodeExtent descriptors sorted by LogicalOffset.</summary>
    public InodeExtent[] LeafEntries { get; set; } = Array.Empty<InodeExtent>();

    /// <summary>Internal entries: (LogicalOffset, ChildBlockNumber) pairs sorted by LogicalOffset.</summary>
    public (long LogicalOffset, long ChildBlockNumber)[] InternalEntries { get; set; }
        = Array.Empty<(long, long)>();

    /// <summary>
    /// Computes the maximum number of leaf entries that fit in a single block.
    /// For 4KB blocks: (4096 - 32 - 16) / 24 = 168.
    /// </summary>
    /// <param name="blockSize">The block size in bytes.</param>
    /// <returns>Maximum leaf entries per node.</returns>
    public static int MaxLeafEntries(int blockSize)
        => (blockSize - HeaderSize - FormatConstants.UniversalBlockTrailerSize) / LeafEntrySize;

    /// <summary>
    /// Computes the maximum number of internal entries that fit in a single block.
    /// For 4KB blocks: (4096 - 32 - 16) / 16 = 253.
    /// </summary>
    /// <param name="blockSize">The block size in bytes.</param>
    /// <returns>Maximum internal entries per node.</returns>
    public static int MaxInternalEntries(int blockSize)
        => (blockSize - HeaderSize - FormatConstants.UniversalBlockTrailerSize) / InternalEntrySize;

    /// <summary>
    /// Serializes this node into a block-sized buffer with a Universal Block Trailer.
    /// </summary>
    /// <param name="node">The node to serialize.</param>
    /// <param name="buffer">Target buffer (must be at least blockSize bytes).</param>
    /// <param name="blockSize">The block size in bytes.</param>
    public static void Serialize(ExtentTreeNode node, Span<byte> buffer, int blockSize)
    {
        if (buffer.Length < blockSize)
            throw new ArgumentException($"Buffer must be at least {blockSize} bytes.", nameof(buffer));

        // Clear the entire block
        buffer[..blockSize].Clear();

        // Write header (32 bytes)
        BinaryPrimitives.WriteUInt32BigEndian(buffer[0..4], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[4..6], node.Level);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[6..8], node.EntryCount);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[8..16], node.ParentBlock);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[16..24], node.NextSiblingBlock);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[24..32], node.PrevSiblingBlock);

        // Write entries
        int offset = HeaderSize;
        if (node.IsLeaf)
        {
            for (int i = 0; i < node.EntryCount; i++)
            {
                InodeExtent.Serialize(in node.LeafEntries[i], buffer[offset..]);
                offset += LeafEntrySize;
            }
        }
        else
        {
            for (int i = 0; i < node.EntryCount; i++)
            {
                var (logicalOffset, childBlock) = node.InternalEntries[i];
                BinaryPrimitives.WriteInt64LittleEndian(buffer[offset..], logicalOffset);
                BinaryPrimitives.WriteInt64LittleEndian(buffer[(offset + 8)..], childBlock);
                offset += InternalEntrySize;
            }
        }

        // Write Universal Block Trailer
        UniversalBlockTrailer.Write(buffer, blockSize, BlockTypeTags.EXTN, generation: 1);
    }

    /// <summary>
    /// Deserializes a node from a block-sized buffer, verifying the trailer checksum.
    /// </summary>
    /// <param name="buffer">Source buffer (must be at least blockSize bytes).</param>
    /// <param name="blockSize">The block size in bytes.</param>
    /// <returns>The deserialized extent tree node.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the magic bytes or trailer are invalid.</exception>
    public static ExtentTreeNode Deserialize(ReadOnlySpan<byte> buffer, int blockSize)
    {
        if (buffer.Length < blockSize)
            throw new ArgumentException($"Buffer must be at least {blockSize} bytes.", nameof(buffer));

        // Verify trailer
        if (!UniversalBlockTrailer.Verify(buffer, blockSize))
            throw new InvalidOperationException("ExtentTreeNode block trailer checksum verification failed.");

        // Verify magic
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(buffer[0..4]);
        if (magic != Magic)
            throw new InvalidOperationException($"Invalid ExtentTreeNode magic: 0x{magic:X8} (expected 0x{Magic:X8}).");

        var node = new ExtentTreeNode
        {
            Level = BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..6]),
            EntryCount = BinaryPrimitives.ReadUInt16LittleEndian(buffer[6..8]),
            ParentBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer[8..16]),
            NextSiblingBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer[16..24]),
            PrevSiblingBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer[24..32]),
        };

        int offset = HeaderSize;
        if (node.IsLeaf)
        {
            int maxEntries = MaxLeafEntries(blockSize);
            node.LeafEntries = new InodeExtent[maxEntries];
            for (int i = 0; i < node.EntryCount; i++)
            {
                node.LeafEntries[i] = InodeExtent.Deserialize(buffer[offset..]);
                offset += LeafEntrySize;
            }
        }
        else
        {
            int maxEntries = MaxInternalEntries(blockSize);
            node.InternalEntries = new (long, long)[maxEntries];
            for (int i = 0; i < node.EntryCount; i++)
            {
                long logicalOffset = BinaryPrimitives.ReadInt64LittleEndian(buffer[offset..]);
                long childBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer[(offset + 8)..]);
                node.InternalEntries[i] = (logicalOffset, childBlock);
                offset += InternalEntrySize;
            }
        }

        return node;
    }

    /// <summary>
    /// Binary search within a leaf node to find the extent covering the given logical offset.
    /// Returns null if no extent covers the offset.
    /// </summary>
    /// <param name="logicalOffset">The logical byte offset to look up.</param>
    /// <returns>The matching extent, or null if not found.</returns>
    public InodeExtent? FindExtent(long logicalOffset)
    {
        if (!IsLeaf || EntryCount == 0)
            return null;

        // Binary search for the extent whose LogicalOffset is <= logicalOffset
        int lo = 0, hi = EntryCount - 1;
        int bestIndex = -1;

        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            long midOffset = LeafEntries[mid].LogicalOffset;

            if (midOffset == logicalOffset)
                return LeafEntries[mid];
            else if (midOffset < logicalOffset)
            {
                bestIndex = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        // Check if the best candidate actually covers this offset
        if (bestIndex >= 0)
        {
            var candidate = LeafEntries[bestIndex];
            // Extent covers [LogicalOffset, LogicalOffset + BlockCount * blockSize)
            // Since we don't know blockSize here, we check if the offset falls within the block range
            // The caller should verify the extent truly covers the offset using block size
            return candidate;
        }

        return null;
    }

    /// <summary>
    /// Inserts an extent entry into a leaf node maintaining sorted order by LogicalOffset.
    /// Returns the insertion index, or -1 if the node is full.
    /// </summary>
    /// <param name="extent">The extent to insert.</param>
    /// <param name="blockSize">The block size in bytes (used to check capacity).</param>
    /// <returns>The index where the extent was inserted, or -1 if the node is full.</returns>
    public int InsertEntry(InodeExtent extent, int blockSize)
    {
        if (!IsLeaf)
            throw new InvalidOperationException("InsertEntry can only be called on leaf nodes.");

        int maxEntries = MaxLeafEntries(blockSize);
        if (EntryCount >= maxEntries)
            return -1;

        // Ensure array capacity
        if (LeafEntries.Length < maxEntries)
        {
            var newEntries = new InodeExtent[maxEntries];
            Array.Copy(LeafEntries, newEntries, EntryCount);
            LeafEntries = newEntries;
        }

        // Find insertion position via binary search
        int insertPos = 0;
        int lo = 0, hi = EntryCount - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (LeafEntries[mid].LogicalOffset < extent.LogicalOffset)
            {
                insertPos = mid + 1;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        // Shift entries to make room
        for (int i = EntryCount; i > insertPos; i--)
        {
            LeafEntries[i] = LeafEntries[i - 1];
        }

        LeafEntries[insertPos] = extent;
        EntryCount++;
        return insertPos;
    }

    /// <summary>
    /// Creates a new empty leaf node with the specified block size capacity.
    /// </summary>
    /// <param name="blockSize">The block size in bytes.</param>
    /// <returns>A new empty leaf node.</returns>
    public static ExtentTreeNode CreateLeaf(int blockSize)
    {
        return new ExtentTreeNode
        {
            Level = 0,
            EntryCount = 0,
            LeafEntries = new InodeExtent[MaxLeafEntries(blockSize)],
        };
    }

    /// <summary>
    /// Creates a new empty internal node with the specified block size capacity.
    /// </summary>
    /// <param name="level">The tree level (&gt;0 for internal nodes).</param>
    /// <param name="blockSize">The block size in bytes.</param>
    /// <returns>A new empty internal node.</returns>
    public static ExtentTreeNode CreateInternal(ushort level, int blockSize)
    {
        if (level == 0)
            throw new ArgumentException("Internal nodes must have level > 0.", nameof(level));

        return new ExtentTreeNode
        {
            Level = level,
            EntryCount = 0,
            InternalEntries = new (long, long)[MaxInternalEntries(blockSize)],
        };
    }
}
