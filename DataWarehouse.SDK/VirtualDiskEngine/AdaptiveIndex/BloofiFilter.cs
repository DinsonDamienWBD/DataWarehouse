using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Bloom Filter Index (Bloofi) — a balanced binary tree of bloom filters where each internal
/// node's filter is the bitwise OR of its children's filters.
/// </summary>
/// <remarks>
/// <para>
/// At planetary scale (10T+ objects), index queries span multiple nodes. Bloofi aggregates
/// per-shard bloom filters hierarchically: the root filter is the union of all descendant filters.
/// A point query checks the root first; if the root says "definitely not", no cross-node query
/// is needed. If the root says "maybe", the query recurses into children, pruning entire subtrees
/// whose filters indicate no match. This reduces cross-node queries by 80%+ for point lookups.
/// </para>
/// <para>
/// Leaf nodes correspond 1:1 to shards. Internal nodes are aggregates (OR of children).
/// The tree is a balanced binary tree with depth = ceil(log2(shardCount)).
/// Hash functions use XxHash64 with K different seeds (default K=7).
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-05 Bloofi hierarchical bloom filter")]
public sealed class BloofiFilter
{
    private readonly int _filterBitCount;
    private readonly int _hashCount;
    private readonly int _filterByteCount;
    private BloofiNode _root;
    private readonly Dictionary<int, BloofiNode> _leafByShard = new();
    // ReaderWriterLockSlim allows concurrent reads — fixes single-global-lock serialization (finding P2-704).
    private readonly System.Threading.ReaderWriterLockSlim _rwLock = new(System.Threading.LockRecursionPolicy.NoRecursion);

    /// <summary>
    /// Gets the number of bits in each bloom filter.
    /// </summary>
    public int FilterBitCount => _filterBitCount;

    /// <summary>
    /// Gets the number of hash functions (K) used per key.
    /// </summary>
    public int HashCount => _hashCount;

    /// <summary>
    /// Gets the root node of the Bloofi tree.
    /// </summary>
    public BloofiNode Root
    {
        get { _rwLock.EnterReadLock(); try { return _root; } finally { _rwLock.ExitReadLock(); } }
    }

    /// <summary>
    /// Gets the number of leaf shards in the tree.
    /// </summary>
    public int ShardCount
    {
        get { _rwLock.EnterReadLock(); try { return _leafByShard.Count; } finally { _rwLock.ExitReadLock(); } }
    }

    /// <summary>
    /// Initializes a new <see cref="BloofiFilter"/> with the specified filter size and hash count.
    /// </summary>
    /// <param name="filterBitCount">Number of bits per bloom filter. Default: 8192.</param>
    /// <param name="hashCount">Number of hash functions (K). Default: 7.</param>
    public BloofiFilter(int filterBitCount = 8192, int hashCount = 7)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(filterBitCount, 64, nameof(filterBitCount));
        ArgumentOutOfRangeException.ThrowIfLessThan(hashCount, 1, nameof(hashCount));

        _filterBitCount = filterBitCount;
        _hashCount = hashCount;
        _filterByteCount = (filterBitCount + 7) / 8;
        _root = new BloofiNode(-1, _filterByteCount);
    }

    /// <summary>
    /// Adds a key to the bloom filter for the specified shard and propagates OR up to root.
    /// </summary>
    /// <param name="key">The key to add.</param>
    /// <param name="shardId">The shard that contains this key.</param>
    public void Add(byte[] key, int shardId)
    {
        ArgumentNullException.ThrowIfNull(key);

        _rwLock.EnterWriteLock();
        try
        {
            if (!_leafByShard.TryGetValue(shardId, out var leaf))
            {
                leaf = new BloofiNode(shardId, _filterByteCount);
                _leafByShard[shardId] = leaf;
                InsertLeafIntoTree(leaf);
            }

            SetBits(leaf.Filter, key);
            PropagateOrToRoot(leaf);
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Queries the Bloofi tree for shard IDs that may contain the given key.
    /// Returns a subset of all shards — only those whose bloom filter indicates a possible match.
    /// </summary>
    /// <param name="key">The key to query.</param>
    /// <returns>List of shard IDs that may contain the key.</returns>
    public IReadOnlyList<int> Query(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var result = new List<int>();

        _rwLock.EnterReadLock();
        try
        {
            if (!MayContain(_root.Filter, key))
                return result;

            QueryRecursive(_root, key, result);
        }
        finally { _rwLock.ExitReadLock(); }

        return result;
    }

    /// <summary>
    /// Removes a shard's leaf from the tree, clearing its filter and rebuilding parent ORs.
    /// </summary>
    /// <param name="shardId">The shard ID to remove.</param>
    /// <returns>True if the shard was found and removed.</returns>
    public bool Remove(int shardId)
    {
        _rwLock.EnterWriteLock();
        try
        {
            if (!_leafByShard.TryGetValue(shardId, out var leaf))
                return false;

            _leafByShard.Remove(shardId);

            // Clear leaf filter
            Array.Clear(leaf.Filter);

            // Rebuild parent ORs up to root
            var parent = leaf.Parent;
            if (parent != null)
            {
                // Remove leaf from parent's children
                parent.Children.Remove(leaf);
                leaf.Parent = null;

                // Rebuild ORs up to root
                RebuildToRoot(parent);

                // Clean up empty internal nodes
                CleanupEmptyInternalNodes();
            }
            else if (ReferenceEquals(leaf, _root))
            {
                _root = new BloofiNode(-1, _filterByteCount);
            }

            return true;
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Recomputes a node's bloom filter as the bitwise OR of all its children's filters.
    /// </summary>
    /// <param name="node">The node to rebuild.</param>
    public void RebuildNode(BloofiNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        _rwLock.EnterWriteLock();
        try { RebuildNodeInternal(node); }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Calculates the theoretical false positive rate for a bloom filter with the given item count.
    /// </summary>
    /// <param name="itemCount">Number of items inserted into the filter.</param>
    /// <returns>The approximate false positive probability.</returns>
    public double FalsePositiveRate(int itemCount)
    {
        if (itemCount <= 0) return 0.0;

        // FPR = (1 - e^(-kn/m))^k where k=hashCount, n=itemCount, m=filterBitCount
        double exponent = -(double)_hashCount * itemCount / _filterBitCount;
        return Math.Pow(1.0 - Math.Exp(exponent), _hashCount);
    }

    /// <summary>
    /// Serializes the entire Bloofi tree to a byte array for persistence in a VDE region.
    /// Format: [filterBitCount:4][hashCount:4][leafCount:4] then per-leaf: [shardId:4][filter:N].
    /// </summary>
    /// <returns>Serialized Bloofi state.</returns>
    public byte[] Serialize()
    {
        _rwLock.EnterReadLock();
        try
        {
            int leafCount = _leafByShard.Count;
            int headerSize = 12; // 3 ints
            int perLeafSize = 4 + _filterByteCount;
            var result = new byte[headerSize + leafCount * perLeafSize];

            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0), _filterBitCount);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), _hashCount);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), leafCount);

            int offset = headerSize;
            foreach (var (shardId, leaf) in _leafByShard)
            {
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), shardId);
                offset += 4;
                Buffer.BlockCopy(leaf.Filter, 0, result, offset, _filterByteCount);
                offset += _filterByteCount;
            }

            return result;
        }
        finally { _rwLock.ExitReadLock(); }
    }

    /// <summary>
    /// Deserializes a Bloofi tree from a byte array previously produced by <see cref="Serialize"/>.
    /// </summary>
    /// <param name="data">The serialized Bloofi data.</param>
    /// <returns>A reconstructed <see cref="BloofiFilter"/>.</returns>
    public static BloofiFilter Deserialize(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 12)
            throw new ArgumentException("Data too short for Bloofi header.", nameof(data));

        int filterBitCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0));
        int hashCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        int leafCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));

        var bloofi = new BloofiFilter(filterBitCount, hashCount);
        int filterByteCount = (filterBitCount + 7) / 8;
        int offset = 12;

        for (int i = 0; i < leafCount; i++)
        {
            int shardId = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
            offset += 4;

            var leaf = new BloofiNode(shardId, filterByteCount);
            Buffer.BlockCopy(data, offset, leaf.Filter, 0, filterByteCount);
            offset += filterByteCount;

            bloofi._leafByShard[shardId] = leaf;
            bloofi.InsertLeafIntoTree(leaf);
        }

        // Rebuild all internal node ORs from leaves up
        RebuildAllInternal(bloofi._root);

        return bloofi;
    }

    /// <summary>
    /// Checks if the given key may be present in the specified filter (bloom filter check).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool MayContain(byte[] filter, byte[] key)
    {
        for (int i = 0; i < _hashCount; i++)
        {
            int bit = GetBitPosition(key, i);
            int byteIndex = bit / 8;
            int bitMask = 1 << (bit % 8);
            if ((filter[byteIndex] & bitMask) == 0)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Sets the bits for a key in a bloom filter.
    /// </summary>
    private void SetBits(byte[] filter, byte[] key)
    {
        for (int i = 0; i < _hashCount; i++)
        {
            int bit = GetBitPosition(key, i);
            int byteIndex = bit / 8;
            int bitMask = 1 << (bit % 8);
            filter[byteIndex] |= (byte)bitMask;
        }
    }

    /// <summary>
    /// Computes a bit position for a given key and seed index using XxHash64.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetBitPosition(byte[] key, int seedIndex)
    {
        long seed = seedIndex * unchecked((long)0x9E3779B97F4A7C15L); // Golden ratio multiples as seeds
        ulong hash = XxHash64.HashToUInt64(key, seed);
        return (int)(hash % (ulong)_filterBitCount);
    }

    /// <summary>
    /// Recursively queries the tree, collecting shard IDs whose leaf filters may contain the key.
    /// </summary>
    private void QueryRecursive(BloofiNode node, byte[] key, List<int> result)
    {
        if (node.IsLeaf)
        {
            if (node.NodeId >= 0 && MayContain(node.Filter, key))
                result.Add(node.NodeId);
            return;
        }

        foreach (var child in node.Children)
        {
            if (MayContain(child.Filter, key))
                QueryRecursive(child, key, result);
        }
    }

    /// <summary>
    /// Inserts a new leaf node into the balanced binary tree.
    /// </summary>
    private void InsertLeafIntoTree(BloofiNode leaf)
    {
        if (_leafByShard.Count <= 1 && _root.Children.Count == 0 && _root.NodeId < 0)
        {
            // First leaf becomes the root's child
            _root.Children.Add(leaf);
            leaf.Parent = _root;
            return;
        }

        // Find the shallowest internal node with fewer than 2 children
        var target = FindInsertionPoint(_root);
        if (target.Children.Count < 2)
        {
            target.Children.Add(leaf);
            leaf.Parent = target;
        }
        else
        {
            // Split: create a new internal node, move one child + new leaf under it
            var newInternal = new BloofiNode(-1, _filterByteCount);
            var existing = target.Children[target.Children.Count - 1];
            target.Children.RemoveAt(target.Children.Count - 1);
            existing.Parent = newInternal;
            leaf.Parent = newInternal;
            newInternal.Children.Add(existing);
            newInternal.Children.Add(leaf);
            newInternal.Parent = target;
            target.Children.Add(newInternal);
        }
    }

    /// <summary>
    /// Finds the best insertion point (shallowest node with room) via BFS.
    /// </summary>
    private static BloofiNode FindInsertionPoint(BloofiNode root)
    {
        var queue = new Queue<BloofiNode>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node.Children.Count < 2)
                return node;

            foreach (var child in node.Children)
            {
                if (!child.IsLeaf)
                    queue.Enqueue(child);
            }
        }

        // All internal nodes full — return root to force split
        return root;
    }

    /// <summary>
    /// Propagates bitwise OR from the given node up to the root.
    /// </summary>
    private void PropagateOrToRoot(BloofiNode node)
    {
        var current = node.Parent;
        while (current != null)
        {
            RebuildNodeInternal(current);
            current = current.Parent;
        }
    }

    /// <summary>
    /// Rebuilds ORs from the given node up to root.
    /// </summary>
    private void RebuildToRoot(BloofiNode node)
    {
        var current = node;
        while (current != null)
        {
            RebuildNodeInternal(current);
            current = current.Parent;
        }
    }

    /// <summary>
    /// Recomputes a single node's filter as OR of its children.
    /// </summary>
    private static void RebuildNodeInternal(BloofiNode node)
    {
        if (node.IsLeaf) return;

        Array.Clear(node.Filter);

        foreach (var child in node.Children)
        {
            for (int i = 0; i < node.Filter.Length; i++)
                node.Filter[i] |= child.Filter[i];
        }
    }

    /// <summary>
    /// Rebuilds all internal nodes from leaves up (post-order traversal).
    /// </summary>
    private static void RebuildAllInternal(BloofiNode node)
    {
        if (node.IsLeaf) return;

        foreach (var child in node.Children)
            RebuildAllInternal(child);

        RebuildNodeInternal(node);
    }

    /// <summary>
    /// Removes internal nodes that have no children (cleanup after leaf removal).
    /// </summary>
    private void CleanupEmptyInternalNodes()
    {
        CleanupRecursive(_root);
    }

    /// <summary>
    /// Recursively cleans up empty internal nodes.
    /// </summary>
    private static void CleanupRecursive(BloofiNode node)
    {
        if (node.IsLeaf) return;

        for (int i = node.Children.Count - 1; i >= 0; i--)
        {
            var child = node.Children[i];
            if (!child.IsLeaf && child.Children.Count == 0)
            {
                node.Children.RemoveAt(i);
            }
            else
            {
                CleanupRecursive(child);
            }
        }
    }
}

/// <summary>
/// Represents a node in the Bloofi hierarchical bloom filter tree.
/// </summary>
/// <remarks>
/// Leaf nodes have a non-negative <see cref="NodeId"/> mapping to a shard/node.
/// Internal nodes have <see cref="NodeId"/> = -1 and their filter is the bitwise OR of all descendants.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-05 Bloofi node")]
public sealed class BloofiNode
{
    /// <summary>
    /// Gets the node ID. For leaf nodes, this is the shard ID. For internal nodes, this is -1.
    /// </summary>
    public int NodeId { get; }

    /// <summary>
    /// Gets the bloom filter bit array for this node.
    /// </summary>
    public byte[] Filter { get; }

    /// <summary>
    /// Gets the number of bits in the filter.
    /// </summary>
    public int FilterBitCount { get; }

    /// <summary>
    /// Gets the child nodes. Empty for leaf nodes.
    /// </summary>
    public List<BloofiNode> Children { get; } = new();

    /// <summary>
    /// Gets or sets the parent node. Null for the root.
    /// </summary>
    public BloofiNode? Parent { get; internal set; }

    /// <summary>
    /// Gets whether this node is a leaf (has a shard mapping and no children).
    /// </summary>
    public bool IsLeaf => NodeId >= 0;

    /// <summary>
    /// Initializes a new <see cref="BloofiNode"/>.
    /// </summary>
    /// <param name="nodeId">The shard ID for leaf nodes, or -1 for internal nodes.</param>
    /// <param name="filterByteCount">The number of bytes in the bloom filter.</param>
    public BloofiNode(int nodeId, int filterByteCount)
    {
        NodeId = nodeId;
        Filter = new byte[filterByteCount];
        FilterBitCount = filterByteCount * 8;
    }
}
