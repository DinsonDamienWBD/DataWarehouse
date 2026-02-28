using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Level 2 adaptive index: Adaptive Radix Tree (ART) providing O(k) lookup where k is key length.
/// Supports up to ~1M objects with path compression and adaptive node sizing.
/// </summary>
/// <remarks>
/// <para>
/// The ART uses four node sizes (4, 16, 48, 256) that grow and shrink dynamically.
/// Path compression collapses single-child paths into prefix bytes stored on each node.
/// Node16 uses SIMD (SSE2) for child lookup when available, with scalar fallback.
/// </para>
/// <para>
/// Thread-safe via <see cref="ReaderWriterLockSlim"/>. Range queries perform in-order DFS
/// traversal with start/end bounds.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-01 Level 2 ART Index")]
public sealed class ArtIndex : IAdaptiveIndex, IDisposable
{
    private ArtNode? _root;
    private long _count;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    /// <inheritdoc />
    public MorphLevel CurrentLevel => MorphLevel.AdaptiveRadixTree;

    /// <inheritdoc />
    public long ObjectCount
    {
        get
        {
            _lock.EnterReadLock();
            try { return _count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <inheritdoc />
    public long RootBlockNumber => -1;

    /// <inheritdoc />
#pragma warning disable CS0067 // Event is required by IAdaptiveIndex but only raised by AdaptiveIndexEngine
    public event Action<MorphLevel, MorphLevel>? LevelChanged;
#pragma warning restore CS0067

    /// <inheritdoc />
    public Task<long?> LookupAsync(byte[] key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        _lock.EnterReadLock();
        try
        {
            var leaf = FindLeaf(key);
            return Task.FromResult(leaf != null ? (long?)leaf.Value : null);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public Task InsertAsync(byte[] key, long value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        _lock.EnterWriteLock();
        try
        {
            _root = InsertRecursive(_root, key, value, 0);
            Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public Task<bool> UpdateAsync(byte[] key, long newValue, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        _lock.EnterWriteLock();
        try
        {
            var leaf = FindLeaf(key);
            if (leaf != null)
            {
                leaf.Value = newValue;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(byte[] key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        _lock.EnterWriteLock();
        try
        {
            bool deleted = false;
            _root = DeleteRecursive(_root, key, 0, ref deleted);
            if (deleted) _count--;
            return Task.FromResult(deleted);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<(byte[] Key, long Value)> RangeQueryAsync(
        byte[]? startKey,
        byte[]? endKey,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Snapshot results under lock, then yield outside lock
        var results = new List<(byte[] Key, long Value)>();
        _lock.EnterReadLock();
        try
        {
            CollectRange(_root, startKey, endKey, results);
        }
        finally
        {
            _lock.ExitReadLock();
        }

        foreach (var entry in results)
        {
            yield return entry;
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<long> CountAsync(CancellationToken ct = default)
    {
        _lock.EnterReadLock();
        try { return Task.FromResult(_count); }
        finally { _lock.ExitReadLock(); }
    }

    /// <inheritdoc />
    public Task MorphToAsync(MorphLevel targetLevel, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "ArtIndex does not support self-morphing. Use AdaptiveIndexEngine for level transitions.");
    }

    /// <inheritdoc />
    public Task<MorphLevel> RecommendLevelAsync(CancellationToken ct = default)
    {
        _lock.EnterReadLock();
        try
        {
            if (_count <= 1)
                return Task.FromResult(MorphLevel.DirectPointer);
            if (_count <= 10_000)
                return Task.FromResult(MorphLevel.SortedArray);
            return Task.FromResult(MorphLevel.AdaptiveRadixTree);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Finds the leaf node matching the given key.
    /// </summary>
    private ArtNode.Leaf? FindLeaf(byte[] key)
    {
        var node = _root;
        int depth = 0;

        while (node != null)
        {
            if (node is ArtNode.Leaf leaf)
            {
                return leaf.Matches(key) ? leaf : null;
            }

            // Check prefix
            if (node.PrefixLength > 0)
            {
                int matched = node.CheckPrefix(key, depth);
                if (matched != node.PrefixLength)
                    return null;
                depth += node.PrefixLength;
            }

            if (depth >= key.Length)
                return null;

            node = node.FindChild(key[depth]);
            depth++;
        }

        return null;
    }

    /// <summary>
    /// Recursively inserts a key-value pair into the ART.
    /// </summary>
    private ArtNode InsertRecursive(ArtNode? node, byte[] key, long value, int depth)
    {
        if (node == null)
        {
            // Lazy expansion: create leaf with full key
            return new ArtNode.Leaf(key, value);
        }

        if (node is ArtNode.Leaf existingLeaf)
        {
            if (existingLeaf.Matches(key))
            {
                throw new InvalidOperationException("Duplicate key.");
            }

            // Replace leaf with inner node, push both leaves down
            var newNode = new ArtNode.Node4();

            // Find longest common prefix between existing and new key from depth
            int commonLen = 0;
            int maxCommon = Math.Min(existingLeaf.Key.Length, key.Length) - depth;
            while (commonLen < maxCommon &&
                   existingLeaf.Key[depth + commonLen] == key[depth + commonLen])
            {
                commonLen++;
            }

            if (commonLen > 0)
            {
                newNode.Prefix = new byte[commonLen];
                Array.Copy(key, depth, newNode.Prefix, 0, commonLen);
                newNode.PrefixLength = commonLen;
            }

            int newDepth = depth + commonLen;

            if (newDepth < existingLeaf.Key.Length && newDepth < key.Length)
            {
                newNode.AddChild(existingLeaf.Key[newDepth], existingLeaf);
                newNode.AddChild(key[newDepth], new ArtNode.Leaf(key, value));
            }
            else if (newDepth >= existingLeaf.Key.Length)
            {
                // Existing key is prefix of new key - store existing as child at sentinel
                newNode.AddChild(key[newDepth], new ArtNode.Leaf(key, value));
                newNode.AddChild(0, existingLeaf); // sentinel byte for shorter key
            }
            else
            {
                // New key is prefix of existing key
                newNode.AddChild(existingLeaf.Key[newDepth], existingLeaf);
                newNode.AddChild(0, new ArtNode.Leaf(key, value)); // sentinel byte
            }

            return newNode;
        }

        // Inner node: check prefix match
        if (node.PrefixLength > 0)
        {
            int matched = node.CheckPrefix(key, depth);
            if (matched < node.PrefixLength)
            {
                // Prefix mismatch: create new node splitting at mismatch point
                var splitNode = new ArtNode.Node4();
                splitNode.Prefix = new byte[matched];
                if (matched > 0)
                {
                    Array.Copy(node.Prefix, 0, splitNode.Prefix, 0, matched);
                }
                splitNode.PrefixLength = matched;

                // Old node becomes child with remaining prefix
                byte oldByte = node.Prefix[matched];
                byte[] remainingPrefix = new byte[node.PrefixLength - matched - 1];
                if (remainingPrefix.Length > 0)
                {
                    Array.Copy(node.Prefix, matched + 1, remainingPrefix, 0, remainingPrefix.Length);
                }
                node.Prefix = remainingPrefix;
                node.PrefixLength = remainingPrefix.Length;

                splitNode.AddChild(oldByte, node);

                // New leaf
                if (depth + matched < key.Length)
                {
                    splitNode.AddChild(key[depth + matched], new ArtNode.Leaf(key, value));
                }
                else
                {
                    splitNode.AddChild(0, new ArtNode.Leaf(key, value));
                }

                return splitNode;
            }
            depth += node.PrefixLength;
        }

        if (depth >= key.Length)
        {
            // Key exhausted at inner node - use sentinel
            var existing = node.FindChild(0);
            if (existing != null)
            {
                throw new InvalidOperationException("Duplicate key.");
            }
            return node.AddChild(0, new ArtNode.Leaf(key, value));
        }

        // Navigate to child
        byte keyByte = key[depth];
        var child = node.FindChild(keyByte);
        if (child != null)
        {
            var newChild = InsertRecursive(child, key, value, depth + 1);
            if (!ReferenceEquals(newChild, child))
            {
                // Child was replaced (grew), update reference
                node = node.RemoveChild(keyByte);
                node = node.AddChild(keyByte, newChild);
            }
            return node;
        }
        else
        {
            return node.AddChild(keyByte, new ArtNode.Leaf(key, value));
        }
    }

    /// <summary>
    /// Recursively deletes a key from the ART.
    /// </summary>
    private ArtNode? DeleteRecursive(ArtNode? node, byte[] key, int depth, ref bool deleted)
    {
        if (node == null)
        {
            deleted = false;
            return null;
        }

        if (node is ArtNode.Leaf leaf)
        {
            if (leaf.Matches(key))
            {
                deleted = true;
                return null;
            }
            deleted = false;
            return node;
        }

        // Inner node: check prefix
        if (node.PrefixLength > 0)
        {
            int matched = node.CheckPrefix(key, depth);
            if (matched != node.PrefixLength)
            {
                deleted = false;
                return node;
            }
            depth += node.PrefixLength;
        }

        if (depth >= key.Length)
        {
            // Try sentinel child
            var sentinel = node.FindChild(0);
            if (sentinel is ArtNode.Leaf sentinelLeaf && sentinelLeaf.Matches(key))
            {
                deleted = true;
                var result = node.RemoveChild(0);
                if (result.ChildCount == 1)
                {
                    return CollapseIfSingleChild(result);
                }
                return result;
            }
            deleted = false;
            return node;
        }

        byte keyByte = key[depth];
        var child = node.FindChild(keyByte);
        if (child == null)
        {
            deleted = false;
            return node;
        }

        var newChild = DeleteRecursive(child, key, depth + 1, ref deleted);
        if (!deleted)
            return node;

        if (newChild == null)
        {
            var result = node.RemoveChild(keyByte);
            if (result.ChildCount == 1)
            {
                return CollapseIfSingleChild(result);
            }
            if (result.ChildCount == 0)
            {
                return null;
            }
            return result;
        }
        else if (!ReferenceEquals(newChild, child))
        {
            node = node.RemoveChild(keyByte);
            node = node.AddChild(keyByte, newChild);
        }

        return node;
    }

    /// <summary>
    /// Collapses a single-child inner node by merging its prefix with the child.
    /// </summary>
    private static ArtNode CollapseIfSingleChild(ArtNode node)
    {
        ArtNode? singleChild = null;
        byte singleKey = 0;
        int childCount = 0;

        node.ForEachChild((k, c) =>
        {
            singleKey = k;
            singleChild = c;
            childCount++;
        });

        if (childCount != 1 || singleChild == null)
            return node;

        // Merge prefix: node.Prefix + singleKey + child.Prefix
        if (singleChild is ArtNode.Leaf)
        {
            return singleChild;
        }

        int newPrefixLen = node.PrefixLength + 1 + singleChild.PrefixLength;
        var newPrefix = new byte[newPrefixLen];
        if (node.PrefixLength > 0)
        {
            Array.Copy(node.Prefix, 0, newPrefix, 0, node.PrefixLength);
        }
        newPrefix[node.PrefixLength] = singleKey;
        if (singleChild.PrefixLength > 0)
        {
            Array.Copy(singleChild.Prefix, 0, newPrefix, node.PrefixLength + 1, singleChild.PrefixLength);
        }
        singleChild.Prefix = newPrefix;
        singleChild.PrefixLength = newPrefixLen;

        return singleChild;
    }

    /// <summary>
    /// Collects all leaf entries within the [startKey, endKey) range via in-order DFS.
    /// </summary>
    private static void CollectRange(ArtNode? node, byte[]? startKey, byte[]? endKey, List<(byte[] Key, long Value)> results)
    {
        if (node == null)
            return;

        if (node is ArtNode.Leaf leaf)
        {
            bool afterStart = startKey == null || ByteArrayComparer.Instance.Compare(leaf.Key, startKey) >= 0;
            bool beforeEnd = endKey == null || ByteArrayComparer.Instance.Compare(leaf.Key, endKey) < 0;
            if (afterStart && beforeEnd)
            {
                results.Add((leaf.Key, leaf.Value));
            }
            return;
        }

        // In-order traversal of children (already sorted by key byte in Node4/Node16,
        // and iterated 0..255 in Node48/Node256)
        node.ForEachChild((_, child) => CollectRange(child, startKey, endKey, results));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _lock.Dispose();
    }
}
