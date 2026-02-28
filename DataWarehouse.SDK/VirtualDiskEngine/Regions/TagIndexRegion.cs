using System.Buffers.Binary;
using System.IO.Hashing;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Regions;

/// <summary>
/// A single entry in the tag index, associating a variable-length key/value pair
/// with an inode number and direct block address.
/// </summary>
/// <remarks>
/// Serialized layout: [KeyLen:1][Key:variable][ValueLen:1][Value:variable][InodeNumber:8 LE][BlockAddress:8 LE]
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 72: VDE regions -- Tag Index (VREG-04)")]
public readonly struct TagEntry
{
    /// <summary>Tag key, variable length (UTF-8 encoded string, max 255 bytes).</summary>
    public byte[] Key { get; }

    /// <summary>Tag value, variable length (max 255 bytes).</summary>
    public byte[] Value { get; }

    /// <summary>The inode this tag belongs to.</summary>
    public long InodeNumber { get; }

    /// <summary>Direct block reference for fast access.</summary>
    public long BlockAddress { get; }

    /// <summary>Creates a new tag entry.</summary>
    /// <param name="key">Tag key bytes (max 255 bytes).</param>
    /// <param name="value">Tag value bytes (max 255 bytes).</param>
    /// <param name="inodeNumber">Inode number.</param>
    /// <param name="blockAddress">Block address for fast access.</param>
    public TagEntry(byte[] key, byte[] value, long inodeNumber, long blockAddress)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        if (value is null) throw new ArgumentNullException(nameof(value));
        if (key.Length > 255) throw new ArgumentException("Key length must not exceed 255 bytes.", nameof(key));
        if (value.Length > 255) throw new ArgumentException("Value length must not exceed 255 bytes.", nameof(value));

        Key = key;
        Value = value;
        InodeNumber = inodeNumber;
        BlockAddress = blockAddress;
    }

    /// <summary>Serialized size of this entry in bytes.</summary>
    public int SerializedSize => 1 + Key.Length + 1 + Value.Length + 8 + 8;

    /// <summary>Writes this entry to the buffer, returning bytes written.</summary>
    public int WriteTo(Span<byte> buffer)
    {
        int offset = 0;
        buffer[offset++] = (byte)Key.Length;
        Key.AsSpan().CopyTo(buffer.Slice(offset));
        offset += Key.Length;
        buffer[offset++] = (byte)Value.Length;
        Value.AsSpan().CopyTo(buffer.Slice(offset));
        offset += Value.Length;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), InodeNumber);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), BlockAddress);
        offset += 8;
        return offset;
    }

    /// <summary>Reads an entry from the buffer, returning bytes consumed.</summary>
    public static TagEntry ReadFrom(ReadOnlySpan<byte> buffer, out int bytesRead)
    {
        int offset = 0;
        int keyLen = buffer[offset++];
        var key = buffer.Slice(offset, keyLen).ToArray();
        offset += keyLen;
        int valueLen = buffer[offset++];
        var value = buffer.Slice(offset, valueLen).ToArray();
        offset += valueLen;
        long inodeNumber = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;
        long blockAddress = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;
        bytesRead = offset;
        return new TagEntry(key, value, inodeNumber, blockAddress);
    }
}

/// <summary>
/// Bloom filter for probabilistic negative lookups in the tag index.
/// Uses XxHash64 with multiple seeds for hash functions.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 72: VDE regions -- Tag Index (VREG-04)")]
internal sealed class TagIndexBloomFilter
{
    private readonly byte[] _bits;

    /// <summary>Number of bits in the filter.</summary>
    public int BitCount { get; }

    /// <summary>Number of hash functions.</summary>
    public int HashFunctionCount { get; }

    /// <summary>
    /// Creates a new bloom filter with the specified parameters.
    /// </summary>
    /// <param name="bitCount">Number of bits (default: 8192 = 1 KiB for ~1% FPR at ~570 entries).</param>
    /// <param name="hashFunctionCount">Number of hash functions (default: 5).</param>
    public TagIndexBloomFilter(int bitCount = 8192, int hashFunctionCount = 5)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bitCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hashFunctionCount);

        BitCount = bitCount;
        HashFunctionCount = hashFunctionCount;
        _bits = new byte[(bitCount + 7) / 8];
    }

    /// <summary>Adds a key to the bloom filter.</summary>
    public void Add(ReadOnlySpan<byte> key)
    {
        for (int i = 0; i < HashFunctionCount; i++)
        {
            int bitIndex = GetBitIndex(key, (ulong)i);
            _bits[bitIndex / 8] |= (byte)(1 << (bitIndex % 8));
        }
    }

    /// <summary>
    /// Returns true if the key is possibly present, false if definitely absent.
    /// </summary>
    public bool MayContain(ReadOnlySpan<byte> key)
    {
        for (int i = 0; i < HashFunctionCount; i++)
        {
            int bitIndex = GetBitIndex(key, (ulong)i);
            if ((_bits[bitIndex / 8] & (1 << (bitIndex % 8))) == 0)
                return false;
        }

        return true;
    }

    /// <summary>Resets all bits to zero.</summary>
    public void Clear()
    {
        Array.Clear(_bits, 0, _bits.Length);
    }

    /// <summary>Serializes the bloom filter bits to the buffer.</summary>
    public void Serialize(Span<byte> buffer)
    {
        _bits.AsSpan().CopyTo(buffer);
    }

    /// <summary>Deserializes a bloom filter from the buffer.</summary>
    public static TagIndexBloomFilter Deserialize(ReadOnlySpan<byte> buffer, int bitCount, int hashCount)
    {
        var filter = new TagIndexBloomFilter(bitCount, hashCount);
        int byteCount = (bitCount + 7) / 8;
        buffer.Slice(0, byteCount).CopyTo(filter._bits);
        return filter;
    }

    /// <summary>Size in bytes of the serialized bit array.</summary>
    public int SerializedSize => (BitCount + 7) / 8;

    private int GetBitIndex(ReadOnlySpan<byte> key, ulong seed)
    {
        ulong hash = XxHash64.HashToUInt64(key, (long)seed);
        return (int)(hash % (ulong)BitCount);
    }
}

/// <summary>
/// Internal B+-tree node for the tag index. Supports both leaf and internal nodes.
/// </summary>
internal sealed class BPlusTreeNode
{
    /// <summary>Whether this is a leaf node (contains entries) or internal node (contains child pointers).</summary>
    public bool IsLeaf { get; }

    /// <summary>Sorted keys (byte[] compared lexicographically).</summary>
    public List<byte[]> Keys { get; } = new();

    /// <summary>Tag entries parallel to Keys (leaf nodes only).</summary>
    public List<TagEntry> Entries { get; } = new();

    /// <summary>Child node pointers (internal nodes only). Keys.Count+1 children.</summary>
    public List<BPlusTreeNode> Children { get; } = new();

    /// <summary>Linked list pointer for leaf-level iteration.</summary>
    public BPlusTreeNode? NextLeaf { get; set; }

    /// <summary>Maximum keys per node.</summary>
    public int Order { get; }

    /// <summary>Creates a new B+-tree node.</summary>
    /// <param name="order">Maximum number of keys per node.</param>
    /// <param name="isLeaf">True for leaf nodes, false for internal nodes.</param>
    public BPlusTreeNode(int order, bool isLeaf)
    {
        if (order < 3) throw new ArgumentOutOfRangeException(nameof(order), "Order must be at least 3.");
        Order = order;
        IsLeaf = isLeaf;
    }

    /// <summary>Whether this node is full (at maximum capacity).</summary>
    public bool IsFull => Keys.Count >= Order;
}

/// <summary>
/// Tag Index Region: a B+-tree with bloom filter for efficient tag-based lookups
/// within the VDE. Tags are the primary metadata mechanism for objects in the VDE.
/// The B+-tree provides ordered access and range queries, while the bloom filter
/// enables O(1) "definitely not here" checks.
/// </summary>
/// <remarks>
/// Compound keys: For compound lookups, the key is formed by concatenating key parts
/// with a null separator byte (0x00). E.g., "category\0subcategory" allows prefix
/// matching on "category" alone via <see cref="PrefixSearch"/>.
///
/// Serialization layout:
///   - Block 0 (header + bloom filter):
///     [EntryCount:4 LE][TreeDepth:2 LE][BloomBitCount:4 LE][BloomHashCount:1][Reserved:5][BloomFilter bits...][UniversalBlockTrailer]
///   - Block 1+ (tree nodes): each block = one B+-tree node
///     Leaf: [IsLeaf=1][KeyCount:2 LE][Entry0..EntryN][NextLeafBlockIndex:4 LE][UniversalBlockTrailer]
///     Internal: [IsLeaf=0][KeyCount:2 LE][Key0..KeyN][ChildBlockIndex0..ChildBlockIndexN+1:4 LE each][UniversalBlockTrailer]
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 72: VDE regions -- Tag Index (VREG-04)")]
public sealed class TagIndexRegion
{
    /// <summary>Header fields size: EntryCount(4) + TreeDepth(2) + BloomBitCount(4) + BloomHashCount(1) + Reserved(5) = 16.</summary>
    private const int HeaderFieldsSize = 16;

    /// <summary>Default B+-tree order when not computed from block size.</summary>
    private const int DefaultOrder = 64;

    /// <summary>Default bloom filter bit count (8192 = 1 KiB, ~1% FPR at ~570 entries).</summary>
    private const int DefaultBloomBitCount = 8192;

    /// <summary>Default number of bloom filter hash functions.</summary>
    private const int DefaultBloomHashCount = 5;

    /// <summary>Null separator byte for compound keys.</summary>
    public const byte CompoundKeySeparator = 0x00;

    private BPlusTreeNode _root;
    private readonly TagIndexBloomFilter _bloomFilter;
    private int _entryCount;

    /// <summary>Monotonic generation number for torn-write detection.</summary>
    public uint Generation { get; set; }

    /// <summary>Number of entries in the tag index.</summary>
    public int EntryCount => _entryCount;

    /// <summary>
    /// Creates a new empty tag index region.
    /// </summary>
    /// <param name="order">B+-tree order (max keys per node). Default: 64.</param>
    /// <param name="bloomBitCount">Bloom filter bit count. Default: 8192.</param>
    /// <param name="bloomHashCount">Bloom filter hash function count. Default: 5.</param>
    public TagIndexRegion(int order = DefaultOrder, int bloomBitCount = DefaultBloomBitCount, int bloomHashCount = DefaultBloomHashCount)
    {
        if (order < 3) throw new ArgumentOutOfRangeException(nameof(order), "Order must be at least 3.");

        _bloomFilter = new TagIndexBloomFilter(bloomBitCount, bloomHashCount);
        _root = new BPlusTreeNode(order, isLeaf: true);
        _entryCount = 0;
    }

    /// <summary>
    /// Inserts a tag entry into the B+-tree and adds the key to the bloom filter.
    /// </summary>
    /// <param name="entry">The tag entry to insert.</param>
    public void Insert(TagEntry entry)
    {
        _bloomFilter.Add(entry.Key);

        if (_root.IsFull)
        {
            var oldRoot = _root;
            var newRoot = new BPlusTreeNode(_root.Order, isLeaf: false);
            newRoot.Children.Add(oldRoot);
            SplitChild(newRoot, 0);
            _root = newRoot;
        }

        InsertNonFull(_root, entry);
        _entryCount++;
    }

    /// <summary>
    /// Removes the entry matching the given key and inode number.
    /// The bloom filter is NOT updated on remove; it is rebuilt on next serialize.
    /// </summary>
    /// <param name="key">The tag key to remove.</param>
    /// <param name="inodeNumber">The inode number to match.</param>
    /// <returns>True if an entry was removed, false if not found.</returns>
    public bool Remove(byte[] key, long inodeNumber)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));

        var node = FindLeafNode(_root, key);
        if (node is null) return false;

        for (int i = 0; i < node.Keys.Count; i++)
        {
            if (CompareKeys(node.Keys[i], key) == 0 && node.Entries[i].InodeNumber == inodeNumber)
            {
                node.Keys.RemoveAt(i);
                node.Entries.RemoveAt(i);
                _entryCount--;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Exact key lookup. Checks bloom filter first for fast negative response.
    /// Returns the first matching entry or null if not found.
    /// </summary>
    /// <param name="key">The tag key to look up.</param>
    /// <returns>The matching entry, or null if not found.</returns>
    public TagEntry? Lookup(byte[] key)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));

        if (!_bloomFilter.MayContain(key))
            return null;

        var node = FindLeafNode(_root, key);
        if (node is null) return null;

        for (int i = 0; i < node.Keys.Count; i++)
        {
            int cmp = CompareKeys(node.Keys[i], key);
            if (cmp == 0)
                return node.Entries[i];
            if (cmp > 0)
                break;
        }

        return null;
    }

    /// <summary>
    /// Returns all entries with the exact key (multiple inodes can share a tag key).
    /// Checks bloom filter first for fast negative response.
    /// </summary>
    /// <param name="key">The tag key to look up.</param>
    /// <returns>All matching entries.</returns>
    public IReadOnlyList<TagEntry> LookupAll(byte[] key)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));

        if (!_bloomFilter.MayContain(key))
            return Array.Empty<TagEntry>();

        var results = new List<TagEntry>();
        var node = FindLeafNode(_root, key);

        while (node is not null)
        {
            for (int i = 0; i < node.Keys.Count; i++)
            {
                int cmp = CompareKeys(node.Keys[i], key);
                if (cmp == 0)
                    results.Add(node.Entries[i]);
                else if (cmp > 0)
                    return results;
            }

            node = node.NextLeaf;
        }

        return results;
    }

    /// <summary>
    /// Returns all entries where the key starts with the given prefix.
    /// Enables compound key partial matching (e.g., prefix "category\0" matches
    /// all entries in that category).
    /// </summary>
    /// <param name="keyPrefix">The key prefix to match.</param>
    /// <returns>All entries with keys starting with the prefix.</returns>
    public IReadOnlyList<TagEntry> PrefixSearch(byte[] keyPrefix)
    {
        if (keyPrefix is null) throw new ArgumentNullException(nameof(keyPrefix));

        var results = new List<TagEntry>();
        var node = FindLeafNode(_root, keyPrefix);

        while (node is not null)
        {
            bool foundAny = false;
            for (int i = 0; i < node.Keys.Count; i++)
            {
                if (StartsWith(node.Keys[i], keyPrefix))
                {
                    results.Add(node.Entries[i]);
                    foundAny = true;
                }
                else if (foundAny || CompareKeys(node.Keys[i], keyPrefix) > 0)
                {
                    // Past the prefix range
                    if (!StartsWith(node.Keys[i], keyPrefix))
                        return results;
                }
            }

            node = node.NextLeaf;
        }

        return results;
    }

    /// <summary>
    /// Traverses all leaf nodes via NextLeaf links in key order, yielding all entries.
    /// </summary>
    public IEnumerable<TagEntry> Iterate()
    {
        var leaf = FindLeftmostLeaf(_root);
        while (leaf is not null)
        {
            for (int i = 0; i < leaf.Entries.Count; i++)
                yield return leaf.Entries[i];
            leaf = leaf.NextLeaf;
        }
    }

    /// <summary>
    /// Range iteration: yields all entries with keys in [startKey, endKey] inclusive.
    /// </summary>
    /// <param name="startKey">Start of range (inclusive).</param>
    /// <param name="endKey">End of range (inclusive).</param>
    public IEnumerable<TagEntry> IterateRange(byte[] startKey, byte[] endKey)
    {
        if (startKey is null) throw new ArgumentNullException(nameof(startKey));
        if (endKey is null) throw new ArgumentNullException(nameof(endKey));

        var node = FindLeafNode(_root, startKey);
        while (node is not null)
        {
            for (int i = 0; i < node.Keys.Count; i++)
            {
                int cmpStart = CompareKeys(node.Keys[i], startKey);
                int cmpEnd = CompareKeys(node.Keys[i], endKey);

                if (cmpStart >= 0 && cmpEnd <= 0)
                    yield return node.Entries[i];
                else if (cmpEnd > 0)
                    yield break;
            }

            node = node.NextLeaf;
        }
    }

    /// <summary>
    /// Computes the number of blocks required to serialize this region.
    /// </summary>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>Number of blocks: 1 header + N tree node blocks.</returns>
    public int RequiredBlocks(int blockSize)
    {
        int nodeCount = CountNodes(_root);
        return 1 + nodeCount; // 1 header block + tree node blocks
    }

    /// <summary>
    /// Serializes the tag index into blocks. BFS serialization of tree into blocks,
    /// rebuilds bloom filter from all entries, writes header + bloom + tree blocks,
    /// each block with UniversalBlockTrailer using BlockTypeTags.TAGI.
    /// </summary>
    /// <param name="buffer">Output buffer (must be at least RequiredBlocks * blockSize).</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public void Serialize(Span<byte> buffer, int blockSize)
    {
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);

        // Rebuild bloom filter from all entries
        _bloomFilter.Clear();
        foreach (var entry in Iterate())
            _bloomFilter.Add(entry.Key);

        // ── Block 0: Header + Bloom Filter ──────────────────────────────
        var headerBlock = buffer.Slice(0, blockSize);
        headerBlock.Clear();

        int treeDepth = ComputeDepth(_root);

        BinaryPrimitives.WriteInt32LittleEndian(headerBlock, _entryCount);
        BinaryPrimitives.WriteInt16LittleEndian(headerBlock.Slice(4), (short)treeDepth);
        BinaryPrimitives.WriteInt32LittleEndian(headerBlock.Slice(6), _bloomFilter.BitCount);
        headerBlock[10] = (byte)_bloomFilter.HashFunctionCount;
        // Bytes 11-15: reserved (zero)

        _bloomFilter.Serialize(headerBlock.Slice(HeaderFieldsSize));

        UniversalBlockTrailer.Write(headerBlock, blockSize, BlockTypeTags.TAGI, Generation);

        // ── Block 1+: BFS tree node serialization ───────────────────────
        // First, assign block indices via BFS
        var nodeQueue = new Queue<BPlusTreeNode>();
        var nodeBlockMap = new Dictionary<BPlusTreeNode, int>();
        var leafList = new List<BPlusTreeNode>();

        nodeQueue.Enqueue(_root);
        int blockIndex = 1; // block 0 is header

        while (nodeQueue.Count > 0)
        {
            var node = nodeQueue.Dequeue();
            nodeBlockMap[node] = blockIndex++;

            if (node.IsLeaf)
                leafList.Add(node);

            if (!node.IsLeaf)
            {
                foreach (var child in node.Children)
                    nodeQueue.Enqueue(child);
            }
        }

        // Set NextLeaf pointers for serialization (they should already be set, but ensure BFS order)
        // Note: NextLeaf links are maintained during insert operations.

        // Serialize each node into its block
        foreach (var (node, blkIdx) in nodeBlockMap)
        {
            var nodeBlock = buffer.Slice(blkIdx * blockSize, blockSize);
            nodeBlock.Clear();

            int offset = 0;
            nodeBlock[offset++] = node.IsLeaf ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt16LittleEndian(nodeBlock.Slice(offset), (ushort)node.Keys.Count);
            offset += 2;

            if (node.IsLeaf)
            {
                // Leaf node: entries followed by NextLeafBlockIndex
                for (int i = 0; i < node.Entries.Count; i++)
                {
                    offset += node.Entries[i].WriteTo(nodeBlock.Slice(offset));
                }

                // NextLeaf block index (0 = no next leaf)
                int nextLeafIdx = node.NextLeaf is not null && nodeBlockMap.TryGetValue(node.NextLeaf, out int nlIdx)
                    ? nlIdx : 0;
                BinaryPrimitives.WriteInt32LittleEndian(nodeBlock.Slice(payloadSize - 4), nextLeafIdx);
            }
            else
            {
                // Internal node: keys followed by child block indices
                for (int i = 0; i < node.Keys.Count; i++)
                {
                    byte keyLen = (byte)node.Keys[i].Length;
                    nodeBlock[offset++] = keyLen;
                    node.Keys[i].AsSpan().CopyTo(nodeBlock.Slice(offset));
                    offset += keyLen;
                }

                // Child block indices (Keys.Count + 1 children)
                for (int i = 0; i < node.Children.Count; i++)
                {
                    int childIdx = nodeBlockMap.TryGetValue(node.Children[i], out int ci) ? ci : 0;
                    BinaryPrimitives.WriteInt32LittleEndian(nodeBlock.Slice(offset), childIdx);
                    offset += 4;
                }
            }

            UniversalBlockTrailer.Write(nodeBlock, blockSize, BlockTypeTags.TAGI, Generation);
        }
    }

    /// <summary>
    /// Deserializes a tag index region from blocks.
    /// </summary>
    /// <param name="buffer">Input buffer containing all blocks.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="blockCount">Number of blocks.</param>
    /// <returns>The deserialized tag index region.</returns>
    public static TagIndexRegion Deserialize(ReadOnlySpan<byte> buffer, int blockSize, int blockCount)
    {
        if (blockCount < 1)
            throw new ArgumentOutOfRangeException(nameof(blockCount), "At least one block is required.");

        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);

        // ── Block 0: Header + Bloom Filter ──────────────────────────────
        var headerBlock = buffer.Slice(0, blockSize);

        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(headerBlock);
        // short treeDepth = BinaryPrimitives.ReadInt16LittleEndian(headerBlock.Slice(4)); // informational
        int bloomBitCount = BinaryPrimitives.ReadInt32LittleEndian(headerBlock.Slice(6));
        int bloomHashCount = headerBlock[10];

        var bloomFilter = TagIndexBloomFilter.Deserialize(
            headerBlock.Slice(HeaderFieldsSize), bloomBitCount, bloomHashCount);

        // Read trailer for generation
        var headerTrailer = UniversalBlockTrailer.Read(headerBlock, blockSize);

        if (blockCount < 2)
        {
            // Empty tree
            var emptyRegion = new TagIndexRegion(DefaultOrder, bloomBitCount, bloomHashCount);
            emptyRegion.Generation = headerTrailer.GenerationNumber;
            return emptyRegion;
        }

        // ── Block 1+: Reconstruct tree nodes ────────────────────────────
        // First pass: read all nodes
        var nodes = new BPlusTreeNode[blockCount]; // index 0 unused (header block)

        for (int blkIdx = 1; blkIdx < blockCount; blkIdx++)
        {
            var nodeBlock = buffer.Slice(blkIdx * blockSize, blockSize);
            int offset = 0;

            bool isLeaf = nodeBlock[offset++] == 1;
            int keyCount = BinaryPrimitives.ReadUInt16LittleEndian(nodeBlock.Slice(offset));
            offset += 2;
            int maxKeysPerBlock = (blockSize - 16) / 8; // Conservative estimate of max keys that fit in a block
            if (keyCount < 0 || keyCount > maxKeysPerBlock)
                throw new InvalidDataException($"TagIndex B+-tree node keyCount {keyCount} exceeds block capacity {maxKeysPerBlock}.");

            var node = new BPlusTreeNode(Math.Max(keyCount + 1, DefaultOrder), isLeaf);

            if (isLeaf)
            {
                for (int i = 0; i < keyCount; i++)
                {
                    var entry = TagEntry.ReadFrom(nodeBlock.Slice(offset), out int bytesRead);
                    node.Keys.Add(entry.Key);
                    node.Entries.Add(entry);
                    offset += bytesRead;
                }

                // NextLeaf index stored at end of payload area
                int nextLeafIdx = BinaryPrimitives.ReadInt32LittleEndian(nodeBlock.Slice(payloadSize - 4));
                // Store temporarily; will resolve after all nodes read
                node.NextLeaf = null; // placeholder
                nodes[blkIdx] = node;

                // Store the next-leaf index for second pass
                // We use a trick: temporarily store blockIndex in a way we can retrieve
                // Using a separate array for next-leaf resolution
            }
            else
            {
                // Internal node: keys followed by child block indices
                for (int i = 0; i < keyCount; i++)
                {
                    int keyLen = nodeBlock[offset++];
                    var key = nodeBlock.Slice(offset, keyLen).ToArray();
                    node.Keys.Add(key);
                    offset += keyLen;
                }

                // Child block indices will be resolved in second pass
                nodes[blkIdx] = node;
            }
        }

        // Second pass: resolve child pointers and next-leaf links
        for (int blkIdx = 1; blkIdx < blockCount; blkIdx++)
        {
            var nodeBlock = buffer.Slice(blkIdx * blockSize, blockSize);
            var node = nodes[blkIdx];
            if (node is null) continue;

            if (node.IsLeaf)
            {
                int nextLeafIdx = BinaryPrimitives.ReadInt32LittleEndian(nodeBlock.Slice(payloadSize - 4));
                if (nextLeafIdx > 0 && nextLeafIdx < blockCount && nodes[nextLeafIdx] is not null)
                    node.NextLeaf = nodes[nextLeafIdx];
            }
            else
            {
                // Re-read to find child pointer offsets
                int offset = 3; // skip IsLeaf(1) + KeyCount(2)
                int keyCount = node.Keys.Count;

                for (int i = 0; i < keyCount; i++)
                {
                    int keyLen = nodeBlock[offset++];
                    offset += keyLen;
                }

                // Read child block indices
                for (int i = 0; i <= keyCount; i++)
                {
                    int childIdx = BinaryPrimitives.ReadInt32LittleEndian(nodeBlock.Slice(offset));
                    offset += 4;
                    if (childIdx > 0 && childIdx < blockCount && nodes[childIdx] is not null)
                        node.Children.Add(nodes[childIdx]);
                }
            }
        }

        // Build region from deserialized data
        var root = nodes[1] ?? new BPlusTreeNode(DefaultOrder, isLeaf: true);

        var region = new TagIndexRegion(root, bloomFilter, entryCount);
        region.Generation = headerTrailer.GenerationNumber;
        return region;
    }

    /// <summary>
    /// Creates a compound key by concatenating parts with null separator bytes (0x00).
    /// </summary>
    /// <param name="parts">Key parts to concatenate.</param>
    /// <returns>Compound key with null-separated parts.</returns>
    public static byte[] MakeCompoundKey(params byte[][] parts)
    {
        if (parts is null || parts.Length == 0)
            throw new ArgumentException("At least one key part is required.", nameof(parts));

        int totalLen = parts.Sum(p => p.Length) + parts.Length - 1;
        var result = new byte[totalLen];
        int offset = 0;

        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0)
                result[offset++] = CompoundKeySeparator;
            parts[i].AsSpan().CopyTo(result.AsSpan(offset));
            offset += parts[i].Length;
        }

        return result;
    }

    // ── Private Constructor for Deserialization ─────────────────────────

    private TagIndexRegion(BPlusTreeNode root, TagIndexBloomFilter bloomFilter, int entryCount)
    {
        _root = root;
        _bloomFilter = bloomFilter;
        _entryCount = entryCount;
    }

    // ── B+-tree Operations ──────────────────────────────────────────────

    private void InsertNonFull(BPlusTreeNode node, TagEntry entry)
    {
        if (node.IsLeaf)
        {
            // Find insertion position
            int pos = 0;
            while (pos < node.Keys.Count && CompareKeys(node.Keys[pos], entry.Key) < 0)
                pos++;

            node.Keys.Insert(pos, entry.Key);
            node.Entries.Insert(pos, entry);
        }
        else
        {
            // Find child to descend into
            int pos = node.Keys.Count - 1;
            while (pos >= 0 && CompareKeys(entry.Key, node.Keys[pos]) < 0)
                pos--;
            pos++;

            if (node.Children[pos].IsFull)
            {
                SplitChild(node, pos);
                if (CompareKeys(entry.Key, node.Keys[pos]) > 0)
                    pos++;
            }

            InsertNonFull(node.Children[pos], entry);
        }
    }

    private void SplitChild(BPlusTreeNode parent, int childIndex)
    {
        var fullChild = parent.Children[childIndex];
        int mid = fullChild.Keys.Count / 2;

        var newNode = new BPlusTreeNode(fullChild.Order, fullChild.IsLeaf);

        if (fullChild.IsLeaf)
        {
            // Copy right half of keys/entries to new node
            for (int i = mid; i < fullChild.Keys.Count; i++)
            {
                newNode.Keys.Add(fullChild.Keys[i]);
                newNode.Entries.Add(fullChild.Entries[i]);
            }

            // Remove right half from old node
            fullChild.Keys.RemoveRange(mid, fullChild.Keys.Count - mid);
            fullChild.Entries.RemoveRange(mid, fullChild.Entries.Count - mid);

            // Maintain leaf-level linked list
            newNode.NextLeaf = fullChild.NextLeaf;
            fullChild.NextLeaf = newNode;

            // Push up the first key of the new node (copy up for leaf splits)
            parent.Keys.Insert(childIndex, newNode.Keys[0]);
            parent.Children.Insert(childIndex + 1, newNode);
        }
        else
        {
            // Internal node split: push up the middle key
            var middleKey = fullChild.Keys[mid];

            // Copy right half (after middle) to new node
            for (int i = mid + 1; i < fullChild.Keys.Count; i++)
                newNode.Keys.Add(fullChild.Keys[i]);

            for (int i = mid + 1; i < fullChild.Children.Count; i++)
                newNode.Children.Add(fullChild.Children[i]);

            // Remove right half + middle from old node
            int removeCount = fullChild.Keys.Count - mid;
            fullChild.Keys.RemoveRange(mid, removeCount);
            fullChild.Children.RemoveRange(mid + 1, fullChild.Children.Count - mid - 1);

            parent.Keys.Insert(childIndex, middleKey);
            parent.Children.Insert(childIndex + 1, newNode);
        }
    }

    private BPlusTreeNode? FindLeafNode(BPlusTreeNode node, byte[] key)
    {
        if (node.IsLeaf)
            return node;

        for (int i = 0; i < node.Keys.Count; i++)
        {
            if (CompareKeys(key, node.Keys[i]) < 0)
                return FindLeafNode(node.Children[i], key);
        }

        return FindLeafNode(node.Children[node.Keys.Count], key);
    }

    private static BPlusTreeNode FindLeftmostLeaf(BPlusTreeNode node)
    {
        while (!node.IsLeaf)
            node = node.Children[0];
        return node;
    }

    // ── Utility ─────────────────────────────────────────────────────────

    private static int CompareKeys(byte[] a, byte[] b)
    {
        return a.AsSpan().SequenceCompareTo(b.AsSpan());
    }

    private static bool StartsWith(byte[] data, byte[] prefix)
    {
        if (data.Length < prefix.Length) return false;
        return data.AsSpan(0, prefix.Length).SequenceEqual(prefix);
    }

    private static int CountNodes(BPlusTreeNode node)
    {
        if (node.IsLeaf)
            return 1;

        int count = 1;
        foreach (var child in node.Children)
            count += CountNodes(child);
        return count;
    }

    private static int ComputeDepth(BPlusTreeNode node)
    {
        int depth = 0;
        var current = node;
        while (!current.IsLeaf)
        {
            depth++;
            current = current.Children[0];
        }

        return depth;
    }
}
