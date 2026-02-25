using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DataWarehouse.SDK.VirtualDiskEngine.Index;

/// <summary>
/// B-Tree node header structure (28 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[SdkCompatibility("3.0.0", Notes = "Phase 33: VDE B-Tree index (VDE-05)")]
public struct BTreeNodeHeader
{
    /// <summary>
    /// Number of keys currently in this node.
    /// </summary>
    public ushort KeyCount;

    /// <summary>
    /// Node flags: Leaf=0x01, Root=0x02, Internal=0x00.
    /// </summary>
    public ushort Flags;

    /// <summary>
    /// Block number of parent node (-1 for root).
    /// </summary>
    public long ParentBlock;

    /// <summary>
    /// Block number of next leaf node (-1 if not leaf or last leaf).
    /// </summary>
    public long NextLeafBlock;

    /// <summary>
    /// Block number of previous leaf node (-1 if not leaf or first leaf).
    /// </summary>
    public long PrevLeafBlock;

    /// <summary>
    /// Total header size in bytes.
    /// </summary>
    public const int Size = 2 + 2 + 8 + 8 + 8; // 28 bytes

    /// <summary>
    /// Leaf node flag (bit 0).
    /// </summary>
    public const ushort FlagLeaf = 0x01;

    /// <summary>
    /// Root node flag (bit 1).
    /// </summary>
    public const ushort FlagRoot = 0x02;
}

/// <summary>
/// B-Tree node structure fitting in a single disk block.
/// </summary>
/// <remarks>
/// Node layout in block: [Header:28][KeyLengths:2*N][Keys:var][Values/ChildPtrs:8*(N+1)][Padding]
/// Leaf nodes store key-value pairs. Internal nodes store keys and child block pointers.
/// Leaf nodes are linked via NextLeafBlock/PrevLeafBlock for efficient range queries.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 33: VDE B-Tree index (VDE-05)")]
public sealed class BTreeNode
{

    /// <summary>
    /// Maximum key size in bytes.
    /// </summary>
    public const int MaxKeySize = 256;

    /// <summary>
    /// Leaf node flag (bit 0) - reference to BTreeNodeHeader.FlagLeaf for convenience.
    /// </summary>
    private const ushort FlagLeaf = BTreeNodeHeader.FlagLeaf;

    /// <summary>
    /// Root node flag (bit 1) - reference to BTreeNodeHeader.FlagRoot for convenience.
    /// </summary>
    private const ushort FlagRoot = BTreeNodeHeader.FlagRoot;

    /// <summary>
    /// Block number where this node resides.
    /// </summary>
    public long BlockNumber { get; set; }

    /// <summary>
    /// Node header with metadata.
    /// </summary>
    public BTreeNodeHeader Header { get; set; }

    /// <summary>
    /// Variable-length keys (max 256 bytes each).
    /// </summary>
    public byte[][] Keys { get; set; }

    /// <summary>
    /// For leaf nodes: mapped values (e.g., block numbers).
    /// For internal nodes: not used (child pointers are stored separately).
    /// </summary>
    public long[] Values { get; set; }

    /// <summary>
    /// For internal nodes: N+1 child block pointers for N keys.
    /// For leaf nodes: empty.
    /// </summary>
    public long[] ChildPointers { get; set; }

    /// <summary>
    /// Maximum number of keys per node (computed based on block size).
    /// </summary>
    public int MaxKeys { get; }

    /// <summary>
    /// Minimum number of keys per node (MaxKeys / 2).
    /// </summary>
    public int MinKeys => MaxKeys / 2;

    /// <summary>
    /// Gets whether this node is a leaf.
    /// </summary>
    public bool IsLeaf => (Header.Flags & FlagLeaf) != 0;

    /// <summary>
    /// Gets whether this node is the root.
    /// </summary>
    public bool IsRoot => (Header.Flags & FlagRoot) != 0;

    /// <summary>
    /// Gets whether this node is full.
    /// </summary>
    public bool IsFull => Header.KeyCount >= MaxKeys;

    /// <summary>
    /// Gets whether this node has underflowed (fewer than minimum keys).
    /// </summary>
    public bool IsUnderflow => Header.KeyCount < MinKeys && !IsRoot;

    /// <summary>
    /// Initializes a new B-Tree node.
    /// </summary>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="avgKeySize">Average key size for computing MaxKeys.</param>
    public BTreeNode(int blockSize, int avgKeySize = 64)
    {
        MaxKeys = ComputeMaxKeys(blockSize, avgKeySize);
        Keys = new byte[MaxKeys][];
        Values = new long[MaxKeys];
        ChildPointers = new long[MaxKeys + 1];
        Header = new BTreeNodeHeader
        {
            KeyCount = 0,
            Flags = 0,
            ParentBlock = -1,
            NextLeafBlock = -1,
            PrevLeafBlock = -1
        };
        BlockNumber = -1;
    }

    /// <summary>
    /// Computes the maximum number of keys per node based on block size and average key size.
    /// </summary>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="avgKeySize">Average key size in bytes.</param>
    /// <returns>Maximum keys per node.</returns>
    public static int ComputeMaxKeys(int blockSize, int avgKeySize)
    {
        // Layout: [Header:28][KeyLengths:2*N][Keys:avgKeySize*N][Values/ChildPtrs:8*(N+1)]
        // blockSize >= 28 + 2*N + avgKeySize*N + 8*(N+1)
        // blockSize >= 28 + 2*N + avgKeySize*N + 8*N + 8
        // blockSize >= 36 + (10 + avgKeySize)*N
        // N <= (blockSize - 36) / (10 + avgKeySize)
        int maxKeys = (blockSize - 36) / (10 + avgKeySize);
        return Math.Max(3, Math.Min(maxKeys, 255)); // Minimum 3 keys, max 255 (for ushort KeyCount)
    }

    /// <summary>
    /// Compares two keys lexicographically.
    /// </summary>
    /// <param name="a">First key.</param>
    /// <param name="b">Second key.</param>
    /// <returns>Negative if a &lt; b, zero if equal, positive if a &gt; b.</returns>
    public static int CompareKeys(byte[] a, byte[] b)
    {
        if (a == null && b == null) return 0;
        if (a == null) return -1;
        if (b == null) return 1;

        int minLen = Math.Min(a.Length, b.Length);
        for (int i = 0; i < minLen; i++)
        {
            int cmp = a[i].CompareTo(b[i]);
            if (cmp != 0) return cmp;
        }
        return a.Length.CompareTo(b.Length);
    }

    /// <summary>
    /// Serializes this node to a byte buffer.
    /// </summary>
    /// <param name="buffer">Destination buffer (must be at least blockSize bytes).</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public void Serialize(Span<byte> buffer, int blockSize)
    {
        if (buffer.Length < blockSize)
            throw new ArgumentException($"Buffer too small: {buffer.Length} < {blockSize}", nameof(buffer));

        buffer.Clear();
        int offset = 0;

        // Write header (28 bytes)
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset, 2), Header.KeyCount);
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset, 2), Header.Flags);
        offset += 2;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), Header.ParentBlock);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), Header.NextLeafBlock);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), Header.PrevLeafBlock);
        offset += 8;

        // Write key lengths (2 * KeyCount)
        for (int i = 0; i < Header.KeyCount; i++)
        {
            ushort keyLen = (ushort)(Keys[i]?.Length ?? 0);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset, 2), keyLen);
            offset += 2;
        }

        // Write keys
        for (int i = 0; i < Header.KeyCount; i++)
        {
            if (Keys[i] != null && Keys[i].Length > 0)
            {
                Keys[i].CopyTo(buffer.Slice(offset, Keys[i].Length));
                offset += Keys[i].Length;
            }
        }

        // Write values (for leaf) or child pointers (for internal)
        int ptrCount = IsLeaf ? Header.KeyCount : Header.KeyCount + 1;
        for (int i = 0; i < ptrCount; i++)
        {
            long val = IsLeaf ? Values[i] : ChildPointers[i];
            BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), val);
            offset += 8;
        }
    }

    /// <summary>
    /// Deserializes a B-Tree node from a byte buffer.
    /// </summary>
    /// <param name="buffer">Source buffer.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="blockNumber">Block number where this node resides.</param>
    /// <returns>Deserialized B-Tree node.</returns>
    public static BTreeNode Deserialize(ReadOnlySpan<byte> buffer, int blockSize, long blockNumber)
    {
        if (buffer.Length < blockSize)
            throw new ArgumentException($"Buffer too small: {buffer.Length} < {blockSize}", nameof(buffer));

        int offset = 0;

        // Read header
        var header = new BTreeNodeHeader
        {
            KeyCount = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset, 2)),
            Flags = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset + 2, 2)),
            ParentBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 4, 8)),
            NextLeafBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 12, 8)),
            PrevLeafBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 20, 8))
        };
        offset += BTreeNodeHeader.Size;

        var node = new BTreeNode(blockSize, avgKeySize: 64)
        {
            Header = header,
            BlockNumber = blockNumber
        };

        // Read key lengths
        var keyLengths = new ushort[header.KeyCount];
        for (int i = 0; i < header.KeyCount; i++)
        {
            keyLengths[i] = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset, 2));
            offset += 2;
        }

        // Read keys
        for (int i = 0; i < header.KeyCount; i++)
        {
            if (keyLengths[i] > 0)
            {
                node.Keys[i] = buffer.Slice(offset, keyLengths[i]).ToArray();
                offset += keyLengths[i];
            }
        }

        // Read values or child pointers
        bool isLeaf = (header.Flags & FlagLeaf) != 0;
        int ptrCount = isLeaf ? header.KeyCount : header.KeyCount + 1;
        for (int i = 0; i < ptrCount; i++)
        {
            long val = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, 8));
            if (isLeaf)
                node.Values[i] = val;
            else
                node.ChildPointers[i] = val;
            offset += 8;
        }

        return node;
    }
}
