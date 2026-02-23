using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Masstree: a trie of B+-trees for fast namespace path lookups via 8-byte key slicing.
/// </summary>
/// <remarks>
/// <para>
/// Masstree provides O(k/8) lookup for namespace paths (e.g., /data/users/profiles)
/// by slicing keys into 8-byte segments and traversing a trie of B+-trees.
/// Each trie level is a <see cref="MasstreeLayer"/> containing a B+-tree over
/// 8-byte key slices (represented as <see cref="ulong"/>).
/// </para>
/// <para>
/// Concurrency model:
/// <list type="bullet">
/// <item><description>Optimistic readers: read version, read data, re-read version; retry if changed</description></item>
/// <item><description>Writers: acquire SpinLock, increment version, modify, unlock, increment version</description></item>
/// <item><description>Version counters use <see cref="Interlocked"/> for thread-safe access</description></item>
/// </list>
/// </para>
/// <para>
/// Key encoding uses big-endian byte order so that lexicographic byte order matches
/// numeric <see cref="ulong"/> comparison order.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-09 Masstree")]
public sealed class Masstree
{
    private readonly MasstreeLayer _root;
    private readonly int _nodeCapacity;

    /// <summary>
    /// Gets the root layer of the Masstree trie.
    /// </summary>
    public MasstreeLayer Root => _root;

    /// <summary>
    /// Initializes a new Masstree instance.
    /// </summary>
    /// <param name="nodeCapacity">Maximum keys per B+-tree node. Defaults to 15.</param>
    public Masstree(int nodeCapacity = 15)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nodeCapacity);
        _nodeCapacity = nodeCapacity;
        _root = new MasstreeLayer(_nodeCapacity);
    }

    /// <summary>
    /// Looks up the value associated with the specified key.
    /// </summary>
    /// <param name="key">The key bytes to look up.</param>
    /// <param name="value">When found, contains the associated value.</param>
    /// <returns>True if the key was found, false otherwise.</returns>
    public bool Lookup(byte[] key, out long value)
    {
        ArgumentNullException.ThrowIfNull(key);
        var slices = SliceKey(key);
        return LookupInternal(_root, slices, 0, out value);
    }

    /// <summary>
    /// Inserts a key-value pair into the Masstree.
    /// </summary>
    /// <param name="key">The key bytes to insert.</param>
    /// <param name="value">The value to associate with the key.</param>
    /// <returns>True if inserted, false if the key already exists.</returns>
    public bool Insert(byte[] key, long value)
    {
        ArgumentNullException.ThrowIfNull(key);
        var slices = SliceKey(key);
        return InsertInternal(_root, slices, 0, value);
    }

    /// <summary>
    /// Deletes a key from the Masstree.
    /// </summary>
    /// <param name="key">The key bytes to delete.</param>
    /// <returns>True if the key was found and deleted, false otherwise.</returns>
    public bool Delete(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var slices = SliceKey(key);
        return DeleteInternal(_root, slices, 0);
    }

    /// <summary>
    /// Enumerates all key-value pairs whose keys start with the specified prefix.
    /// </summary>
    /// <param name="prefix">The prefix bytes to match.</param>
    /// <returns>An enumerable of matching key-value pairs.</returns>
    public IEnumerable<(byte[] Key, long Value)> PrefixScan(byte[] prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var slices = SliceKey(prefix);
        var results = new List<(byte[] Key, long Value)>();
        PrefixScanInternal(_root, slices, 0, prefix, results);
        return results;
    }

    /// <summary>
    /// Internal recursive lookup traversing layers via 8-byte key slices.
    /// </summary>
    private static bool LookupInternal(MasstreeLayer layer, ulong[] slices, int depth, out long value)
    {
        value = 0;
        if (depth >= slices.Length)
            return false;

        ulong slice = slices[depth];
        var leaf = layer.FindLeaf(slice);
        if (leaf == null)
            return false;

        // Optimistic read with version check
        while (true)
        {
            long v1 = leaf.ReadVersion();
            if (MasstreeNode.IsLocked(v1))
            {
                Thread.SpinWait(1);
                continue;
            }

            int idx = leaf.FindKeyIndex(slice);
            if (idx < 0)
            {
                long v2 = leaf.ReadVersion();
                if (v1 != v2) continue; // Retry on version change
                return false;
            }

            if (depth == slices.Length - 1)
            {
                // Terminal slice - check for value
                if (leaf.HasValue(idx))
                {
                    value = leaf.GetValue(idx);
                    long v2 = leaf.ReadVersion();
                    if (v1 != v2) continue;
                    return true;
                }
            }

            // Check for next-layer pointer
            var nextLayer = leaf.GetNextLayer(idx);
            if (nextLayer != null)
            {
                long v2 = leaf.ReadVersion();
                if (v1 != v2) continue;
                return LookupInternal(nextLayer, slices, depth + 1, out value);
            }

            long v3 = leaf.ReadVersion();
            if (v1 != v3) continue;
            return false;
        }
    }

    /// <summary>
    /// Internal recursive insert traversing and creating layers as needed.
    /// </summary>
    private bool InsertInternal(MasstreeLayer layer, ulong[] slices, int depth, long value)
    {
        if (depth >= slices.Length)
            return false;

        ulong slice = slices[depth];
        var leaf = layer.FindOrCreateLeaf(slice, _nodeCapacity);

        leaf.AcquireLock();
        try
        {
            int idx = leaf.FindKeyIndex(slice);

            if (depth == slices.Length - 1)
            {
                // Terminal slice - insert value
                if (idx >= 0 && leaf.HasValue(idx))
                    return false; // Key already exists

                if (idx < 0)
                {
                    leaf.InsertKey(slice, value);
                }
                else
                {
                    leaf.SetValue(idx, value);
                }
                return true;
            }

            // Non-terminal slice - need next layer
            if (idx < 0)
            {
                // Key slice not in this node - insert it without value, then create next layer
                idx = leaf.InsertKeyNoValue(slice);
            }

            var nextLayer = leaf.GetNextLayer(idx);
            if (nextLayer == null)
            {
                nextLayer = new MasstreeLayer(_nodeCapacity);
                leaf.SetNextLayer(idx, nextLayer);
            }

            // Release lock before descending (fine-grained locking)
            leaf.ReleaseLock();
            return InsertInternal(nextLayer, slices, depth + 1, value);
        }
        catch
        {
            leaf.ReleaseLock();
            throw;
        }
    }

    /// <summary>
    /// Internal recursive delete traversing layers.
    /// </summary>
    private static bool DeleteInternal(MasstreeLayer layer, ulong[] slices, int depth)
    {
        if (depth >= slices.Length)
            return false;

        ulong slice = slices[depth];
        var leaf = layer.FindLeaf(slice);
        if (leaf == null)
            return false;

        leaf.AcquireLock();
        try
        {
            int idx = leaf.FindKeyIndex(slice);
            if (idx < 0)
                return false;

            if (depth == slices.Length - 1)
            {
                // Terminal slice - remove value
                if (!leaf.HasValue(idx))
                    return false;

                leaf.RemoveValue(idx);

                // If no value and no next-layer, remove the key entirely
                if (leaf.GetNextLayer(idx) == null)
                {
                    leaf.RemoveKey(idx);
                }
                return true;
            }

            // Non-terminal - descend
            var nextLayer = leaf.GetNextLayer(idx);
            if (nextLayer == null)
                return false;

            leaf.ReleaseLock();
            bool deleted = DeleteInternal(nextLayer, slices, depth + 1);

            // Check if layer became empty and clean up
            if (deleted && nextLayer.IsEmpty)
            {
                leaf.AcquireLock();
                try
                {
                    leaf.SetNextLayer(idx, null);
                    if (!leaf.HasValue(idx))
                    {
                        leaf.RemoveKey(idx);
                    }
                }
                finally
                {
                    leaf.ReleaseLock();
                }
            }

            return deleted;
        }
        catch
        {
            leaf.ReleaseLock();
            throw;
        }
    }

    /// <summary>
    /// Internal recursive prefix scan collecting all descendants.
    /// </summary>
    private static void PrefixScanInternal(
        MasstreeLayer layer,
        ulong[] prefixSlices,
        int depth,
        byte[] originalPrefix,
        List<(byte[] Key, long Value)> results)
    {
        if (depth >= prefixSlices.Length)
        {
            // We've matched all prefix slices - enumerate everything below
            CollectAll(layer, originalPrefix, results);
            return;
        }

        ulong slice = prefixSlices[depth];
        var leaf = layer.FindLeaf(slice);
        if (leaf == null)
            return;

        while (true)
        {
            long v1 = leaf.ReadVersion();
            if (MasstreeNode.IsLocked(v1))
            {
                Thread.SpinWait(1);
                continue;
            }

            int idx = leaf.FindKeyIndex(slice);
            if (idx < 0)
            {
                long v2 = leaf.ReadVersion();
                if (v1 != v2) continue;
                return;
            }

            if (depth == prefixSlices.Length - 1)
            {
                // Last prefix slice - collect this entry and all below
                if (leaf.HasValue(idx))
                {
                    results.Add((originalPrefix, leaf.GetValue(idx)));
                }

                var nextLayer = leaf.GetNextLayer(idx);
                if (nextLayer != null)
                {
                    CollectAll(nextLayer, originalPrefix, results);
                }
            }
            else
            {
                var nextLayer = leaf.GetNextLayer(idx);
                if (nextLayer != null)
                {
                    long v2 = leaf.ReadVersion();
                    if (v1 != v2) continue;
                    PrefixScanInternal(nextLayer, prefixSlices, depth + 1, originalPrefix, results);
                }
            }

            long v3 = leaf.ReadVersion();
            if (v1 != v3) continue;
            return;
        }
    }

    /// <summary>
    /// Collects all key-value pairs from a layer and its descendants.
    /// </summary>
    private static void CollectAll(MasstreeLayer layer, byte[] keyPrefix, List<(byte[] Key, long Value)> results)
    {
        var node = layer.FirstLeaf;
        while (node != null)
        {
            while (true)
            {
                long v1 = node.ReadVersion();
                if (MasstreeNode.IsLocked(v1))
                {
                    Thread.SpinWait(1);
                    continue;
                }

                int count = node.KeyCount;
                for (int i = 0; i < count; i++)
                {
                    if (node.HasValue(i))
                    {
                        results.Add((keyPrefix, node.GetValue(i)));
                    }

                    var nextLayer = node.GetNextLayer(i);
                    if (nextLayer != null)
                    {
                        CollectAll(nextLayer, keyPrefix, results);
                    }
                }

                long v2 = node.ReadVersion();
                if (v1 != v2) continue;
                break;
            }

            node = node.RightSibling;
        }
    }

    /// <summary>
    /// Slices a key byte array into 8-byte segments encoded as big-endian ulongs.
    /// </summary>
    /// <remarks>
    /// Keys are zero-padded to the next 8-byte boundary so that the final slice
    /// is a full 8-byte value. Big-endian encoding ensures that lexicographic byte
    /// order matches numeric ulong comparison order.
    /// </remarks>
    /// <param name="key">The raw key bytes.</param>
    /// <returns>An array of 8-byte slices encoded as big-endian ulongs.</returns>
    internal static ulong[] SliceKey(byte[] key)
    {
        if (key.Length == 0)
            return new ulong[] { 0UL };

        int sliceCount = (key.Length + 7) / 8;
        var slices = new ulong[sliceCount];
        // Heap-allocated to satisfy CA2014 (stackalloc hoisted but analyzer flags it)
        byte[] paddedBuf = new byte[8];
        Span<byte> padded = paddedBuf;

        for (int i = 0; i < sliceCount; i++)
        {
            int offset = i * 8;
            int remaining = key.Length - offset;

            if (remaining >= 8)
            {
                // Full 8-byte slice - read as big-endian ulong
                slices[i] = BinaryPrimitives.ReadUInt64BigEndian(key.AsSpan(offset, 8));
            }
            else
            {
                // Partial slice - zero-pad to 8 bytes
                padded.Clear();
                key.AsSpan(offset, remaining).CopyTo(padded);
                slices[i] = BinaryPrimitives.ReadUInt64BigEndian(padded);
            }
        }

        return slices;
    }
}

/// <summary>
/// A B+-tree layer within the Masstree trie, operating on 8-byte key slices.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-09 Masstree layer")]
public sealed class MasstreeLayer
{
    private MasstreeNode _root;
    private readonly int _capacity;

    /// <summary>
    /// Gets the first (leftmost) leaf node for sequential scan.
    /// </summary>
    public MasstreeNode? FirstLeaf
    {
        get
        {
            var node = _root;
            while (node != null && !node.IsLeaf)
            {
                node = node.Children[0];
            }
            return node;
        }
    }

    /// <summary>
    /// Gets whether this layer contains no keys.
    /// </summary>
    public bool IsEmpty => _root.KeyCount == 0;

    /// <summary>
    /// Initializes a new B+-tree layer.
    /// </summary>
    /// <param name="capacity">Maximum keys per node.</param>
    public MasstreeLayer(int capacity)
    {
        _capacity = capacity;
        _root = new MasstreeNode(capacity, isLeaf: true);
    }

    /// <summary>
    /// Finds the leaf node that should contain the given 8-byte key slice.
    /// </summary>
    /// <param name="slice">The 8-byte key slice to search for.</param>
    /// <returns>The leaf node, or null if the tree is empty.</returns>
    public MasstreeNode? FindLeaf(ulong slice)
    {
        var node = _root;
        while (node != null && !node.IsLeaf)
        {
            int idx = node.FindChildIndex(slice);
            node = node.Children[idx];
        }
        return node;
    }

    /// <summary>
    /// Finds or creates a leaf node for the given key slice, splitting as needed.
    /// </summary>
    /// <param name="slice">The 8-byte key slice.</param>
    /// <param name="capacity">Node capacity for new nodes.</param>
    /// <returns>The leaf node where the slice should be stored.</returns>
    public MasstreeNode FindOrCreateLeaf(ulong slice, int capacity)
    {
        var node = _root;

        // If root is full, split it first
        if (node.KeyCount >= _capacity)
        {
            var newRoot = new MasstreeNode(capacity, isLeaf: false);
            newRoot.Children[0] = node;
            SplitChild(newRoot, 0, node);
            _root = newRoot;
            node = newRoot;
        }

        while (!node.IsLeaf)
        {
            int idx = node.FindChildIndex(slice);
            var child = node.Children[idx];
            if (child != null && child.KeyCount >= _capacity)
            {
                SplitChild(node, idx, child);
                // After split, re-determine which child to follow
                if (slice > node.Keys[idx])
                    idx++;
                child = node.Children[idx];
            }
            node = child!;
        }

        return node;
    }

    /// <summary>
    /// Splits a full child node, promoting the median key to the parent.
    /// </summary>
    private static void SplitChild(MasstreeNode parent, int childIndex, MasstreeNode child)
    {
        int mid = child.KeyCount / 2;
        var newNode = new MasstreeNode(child.Capacity, child.IsLeaf);

        child.AcquireLock();
        try
        {
            // Move right half to new node
            int moveCount = child.KeyCount - mid - 1;
            for (int i = 0; i < moveCount; i++)
            {
                newNode.Keys[i] = child.Keys[mid + 1 + i];
                newNode.Values[i] = child.Values[mid + 1 + i];
                newNode.HasValues[i] = child.HasValues[mid + 1 + i];
                newNode.NextLayers[i] = child.NextLayers[mid + 1 + i];

                if (!child.IsLeaf)
                {
                    newNode.Children[i] = child.Children[mid + 1 + i];
                }
            }

            if (!child.IsLeaf)
            {
                newNode.Children[moveCount] = child.Children[child.KeyCount];
            }

            newNode.KeyCount = moveCount;

            ulong medianKey = child.Keys[mid];
            long medianValue = child.Values[mid];
            bool medianHasValue = child.HasValues[mid];
            var medianNextLayer = child.NextLayers[mid];
            child.KeyCount = mid;

            // Set up leaf links
            if (child.IsLeaf)
            {
                newNode.RightSibling = child.RightSibling;
                child.RightSibling = newNode;
            }

            // Make room in parent
            parent.AcquireLock();
            try
            {
                for (int i = parent.KeyCount; i > childIndex; i--)
                {
                    parent.Keys[i] = parent.Keys[i - 1];
                    parent.Values[i] = parent.Values[i - 1];
                    parent.HasValues[i] = parent.HasValues[i - 1];
                    parent.NextLayers[i] = parent.NextLayers[i - 1];
                    parent.Children[i + 1] = parent.Children[i];
                }

                parent.Keys[childIndex] = medianKey;
                parent.Values[childIndex] = medianValue;
                parent.HasValues[childIndex] = medianHasValue;
                parent.NextLayers[childIndex] = medianNextLayer;
                parent.Children[childIndex + 1] = newNode;
                parent.KeyCount++;
            }
            finally
            {
                parent.ReleaseLock();
            }
        }
        finally
        {
            child.ReleaseLock();
        }
    }
}

/// <summary>
/// A node in the Masstree B+-tree layer, storing 8-byte key slices as ulongs.
/// </summary>
/// <remarks>
/// <para>
/// Uses optimistic concurrency for readers and SpinLock for writers.
/// The version counter is incremented twice per write (once on lock, once on unlock)
/// so that readers can detect concurrent modifications by checking for odd versions
/// (locked) or version changes (concurrent write completed).
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-09 Masstree node")]
public sealed class MasstreeNode
{
    private long _version;
    private SpinLock _spinLock;

    /// <summary>The 8-byte key slices stored in this node, sorted.</summary>
    internal readonly ulong[] Keys;

    /// <summary>Values for leaf entries (indexed parallel to Keys).</summary>
    internal readonly long[] Values;

    /// <summary>Flags indicating which entries have values (vs. being trie-internal only).</summary>
    internal readonly bool[] HasValues;

    /// <summary>Pointers to next-level Masstree layers for keys that continue beyond this slice.</summary>
    internal readonly MasstreeLayer?[] NextLayers;

    /// <summary>Child node pointers for internal (non-leaf) nodes. Length = Capacity + 1.</summary>
    internal readonly MasstreeNode?[] Children;

    /// <summary>Number of keys currently stored in this node.</summary>
    internal int KeyCount;

    /// <summary>Right sibling pointer for leaf-level linked list traversal.</summary>
    internal MasstreeNode? RightSibling;

    /// <summary>Whether this is a leaf node.</summary>
    public bool IsLeaf { get; }

    /// <summary>Maximum number of keys this node can hold.</summary>
    public int Capacity { get; }

    /// <summary>
    /// Initializes a new Masstree node.
    /// </summary>
    /// <param name="capacity">Maximum number of keys.</param>
    /// <param name="isLeaf">Whether this is a leaf node.</param>
    public MasstreeNode(int capacity, bool isLeaf)
    {
        Capacity = capacity;
        IsLeaf = isLeaf;
        Keys = new ulong[capacity];
        Values = new long[capacity];
        HasValues = new bool[capacity];
        NextLayers = new MasstreeLayer?[capacity];
        Children = new MasstreeNode?[capacity + 1];
        KeyCount = 0;
        RightSibling = null;
        _version = 0;
        _spinLock = new SpinLock(enableThreadOwnerTracking: false);
    }

    /// <summary>
    /// Reads the current version counter for optimistic concurrency.
    /// </summary>
    /// <returns>The current version value.</returns>
    public long ReadVersion()
    {
        return Interlocked.Read(ref _version);
    }

    /// <summary>
    /// Checks whether a version value indicates the node is currently locked.
    /// </summary>
    /// <param name="version">The version value to check.</param>
    /// <returns>True if the version indicates a lock is held (odd value).</returns>
    public static bool IsLocked(long version)
    {
        return (version & 1) != 0;
    }

    /// <summary>
    /// Acquires the writer lock, incrementing the version to an odd value.
    /// </summary>
    public void AcquireLock()
    {
        bool lockTaken = false;
        _spinLock.Enter(ref lockTaken);
        Interlocked.Increment(ref _version);
    }

    /// <summary>
    /// Releases the writer lock, incrementing the version to an even value.
    /// </summary>
    public void ReleaseLock()
    {
        Interlocked.Increment(ref _version);
        _spinLock.Exit(useMemoryBarrier: true);
    }

    /// <summary>
    /// Finds the index of a key slice in this node using binary search.
    /// </summary>
    /// <param name="slice">The 8-byte key slice to find.</param>
    /// <returns>The index if found, or -1 if not found.</returns>
    public int FindKeyIndex(ulong slice)
    {
        int lo = 0, hi = KeyCount - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (Keys[mid] == slice)
                return mid;
            if (Keys[mid] < slice)
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        return -1;
    }

    /// <summary>
    /// Finds the child index to follow for a given key slice (internal nodes only).
    /// </summary>
    /// <param name="slice">The 8-byte key slice.</param>
    /// <returns>The child index to follow.</returns>
    public int FindChildIndex(ulong slice)
    {
        int lo = 0, hi = KeyCount - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (slice <= Keys[mid])
                hi = mid - 1;
            else
                lo = mid + 1;
        }
        return lo;
    }

    /// <summary>
    /// Checks whether the entry at the given index has a terminal value.
    /// </summary>
    public bool HasValue(int index) => HasValues[index];

    /// <summary>
    /// Gets the value at the specified index.
    /// </summary>
    public long GetValue(int index) => Values[index];

    /// <summary>
    /// Sets the value at the specified index.
    /// </summary>
    public void SetValue(int index, long value)
    {
        Values[index] = value;
        HasValues[index] = true;
    }

    /// <summary>
    /// Removes the value at the specified index (keeps the key for trie pointers).
    /// </summary>
    public void RemoveValue(int index)
    {
        Values[index] = 0;
        HasValues[index] = false;
    }

    /// <summary>
    /// Gets the next-layer pointer at the specified index.
    /// </summary>
    public MasstreeLayer? GetNextLayer(int index) => NextLayers[index];

    /// <summary>
    /// Sets the next-layer pointer at the specified index.
    /// </summary>
    public void SetNextLayer(int index, MasstreeLayer? layer)
    {
        NextLayers[index] = layer;
    }

    /// <summary>
    /// Inserts a key-value pair into this leaf node in sorted order.
    /// </summary>
    /// <param name="slice">The 8-byte key slice.</param>
    /// <param name="value">The value to store.</param>
    public void InsertKey(ulong slice, long value)
    {
        int pos = FindInsertPosition(slice);

        // Shift right
        for (int i = KeyCount; i > pos; i--)
        {
            Keys[i] = Keys[i - 1];
            Values[i] = Values[i - 1];
            HasValues[i] = HasValues[i - 1];
            NextLayers[i] = NextLayers[i - 1];
        }

        Keys[pos] = slice;
        Values[pos] = value;
        HasValues[pos] = true;
        NextLayers[pos] = null;
        KeyCount++;
    }

    /// <summary>
    /// Inserts a key without a value (trie-internal node for continued descent).
    /// </summary>
    /// <param name="slice">The 8-byte key slice.</param>
    /// <returns>The index where the key was inserted.</returns>
    public int InsertKeyNoValue(ulong slice)
    {
        int pos = FindInsertPosition(slice);

        // Shift right
        for (int i = KeyCount; i > pos; i--)
        {
            Keys[i] = Keys[i - 1];
            Values[i] = Values[i - 1];
            HasValues[i] = HasValues[i - 1];
            NextLayers[i] = NextLayers[i - 1];
        }

        Keys[pos] = slice;
        Values[pos] = 0;
        HasValues[pos] = false;
        NextLayers[pos] = null;
        KeyCount++;
        return pos;
    }

    /// <summary>
    /// Removes the key at the specified index, shifting remaining keys left.
    /// </summary>
    /// <param name="index">The index of the key to remove.</param>
    public void RemoveKey(int index)
    {
        for (int i = index; i < KeyCount - 1; i++)
        {
            Keys[i] = Keys[i + 1];
            Values[i] = Values[i + 1];
            HasValues[i] = HasValues[i + 1];
            NextLayers[i] = NextLayers[i + 1];
        }

        KeyCount--;
        // Clear the last slot
        Keys[KeyCount] = 0;
        Values[KeyCount] = 0;
        HasValues[KeyCount] = false;
        NextLayers[KeyCount] = null;
    }

    /// <summary>
    /// Finds the sorted insertion position for a key slice.
    /// </summary>
    private int FindInsertPosition(ulong slice)
    {
        int lo = 0, hi = KeyCount - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (Keys[mid] < slice)
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        return lo;
    }
}
