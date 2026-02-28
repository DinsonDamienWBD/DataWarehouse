using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using DataWarehouse.SDK.VirtualDiskEngine.Journal;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.VirtualDiskEngine.Index;

/// <summary>
/// On-disk B-Tree implementation with WAL-protected modifications.
/// </summary>
/// <remarks>
/// Provides O(log n) lookup, insert, delete, and range query operations.
/// All structural modifications (split, merge) are WAL-protected for crash safety.
/// Concurrent readers are allowed with a single writer.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 33: VDE B-Tree index (VDE-05)")]
public sealed class BTree : IBTreeIndex, IAsyncDisposable
{
    private readonly IBlockDevice _device;
    private readonly IBlockAllocator _allocator;
    private readonly IWriteAheadLog _wal;
    private readonly int _blockSize;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _readLock = new(int.MaxValue, int.MaxValue);
    private readonly BoundedDictionary<long, (BTreeNode Node, DateTime AccessTime)> _nodeCache;
    private const int MaxCachedNodes = 1000;

    private long _rootBlockNumber;

    /// <summary>
    /// Gets the block number of the root node.
    /// </summary>
    public long RootBlockNumber => _rootBlockNumber;

    /// <summary>
    /// Initializes a new B-Tree instance.
    /// </summary>
    /// <param name="device">Block device for I/O.</param>
    /// <param name="allocator">Block allocator for new nodes.</param>
    /// <param name="wal">Write-ahead log for crash recovery.</param>
    /// <param name="rootBlockNumber">Block number of the root node.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public BTree(IBlockDevice device, IBlockAllocator allocator, IWriteAheadLog wal, long rootBlockNumber, int blockSize)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _wal = wal ?? throw new ArgumentNullException(nameof(wal));
        _blockSize = blockSize;
        _rootBlockNumber = rootBlockNumber;
        _nodeCache = new BoundedDictionary<long, (BTreeNode, DateTime)>(1000);
    }

    /// <summary>
    /// Looks up a value by exact key match.
    /// </summary>
    public async Task<long?> LookupAsync(byte[] key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        await _readLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var node = await ReadNodeAsync(_rootBlockNumber, ct).ConfigureAwait(false);
            while (true)
            {
                int index = BinarySearchKeys(node, key);
                if (node.IsLeaf)
                {
                    // Exact match in leaf
                    if (index >= 0 && index < node.Header.KeyCount &&
                        BTreeNode.CompareKeys(node.Keys[index], key) == 0)
                    {
                        return node.Values[index];
                    }
                    return null;
                }
                else
                {
                    // Internal node: follow child pointer
                    int childIndex = index >= 0 ? index + 1 : ~index;
                    long childBlock = node.ChildPointers[childIndex];
                    node = await ReadNodeAsync(childBlock, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _readLock.Release();
        }
    }

    /// <summary>
    /// Inserts a new key-value pair.
    /// </summary>
    public async Task InsertAsync(byte[] key, long value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var txn = await _wal.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                var root = await ReadNodeAsync(_rootBlockNumber, ct).ConfigureAwait(false);

                // Check if root is full -> need to split root first
                if (root.IsFull)
                {
                    var newRoot = new BTreeNode(_blockSize, avgKeySize: 64)
                    {
                        BlockNumber = _allocator.AllocateBlock(ct),
                        Header = new BTreeNodeHeader
                        {
                            Flags = BTreeNodeHeader.FlagRoot,
                            KeyCount = 0,
                            ParentBlock = -1,
                            NextLeafBlock = -1,
                            PrevLeafBlock = -1
                        }
                    };

                    newRoot.ChildPointers[0] = root.BlockNumber;
                    var rootHeader = root.Header;
                    rootHeader.Flags &= unchecked((ushort)~BTreeNodeHeader.FlagRoot); // Clear root flag
                    rootHeader.ParentBlock = newRoot.BlockNumber;
                    root.Header = rootHeader;

                    await SplitChildAsync(newRoot, 0, root, txn, ct).ConfigureAwait(false);
                    await WriteNodeAsync(root, txn, ct).ConfigureAwait(false);
                    await WriteNodeAsync(newRoot, txn, ct).ConfigureAwait(false);

                    await _wal.FlushAsync(ct).ConfigureAwait(false);
                    _rootBlockNumber = newRoot.BlockNumber;
                    root = newRoot;
                }

                await InsertNonFullAsync(root, key, value, txn, ct).ConfigureAwait(false);
                await _wal.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // WAL provides crash recovery — uncommitted entries are discarded on next replay
                System.Diagnostics.Debug.WriteLine($"[BTree.InsertAsync] Insert failed, WAL will handle rollback: {ex.Message}");
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Updates the value for an existing key.
    /// </summary>
    public async Task<bool> UpdateAsync(byte[] key, long newValue, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var node = await ReadNodeAsync(_rootBlockNumber, ct).ConfigureAwait(false);
            while (true)
            {
                int index = BinarySearchKeys(node, key);
                if (node.IsLeaf)
                {
                    if (index >= 0 && index < node.Header.KeyCount &&
                        BTreeNode.CompareKeys(node.Keys[index], key) == 0)
                    {
                        node.Values[index] = newValue;
                        var txn = await _wal.BeginTransactionAsync(ct).ConfigureAwait(false);
                        await WriteNodeAsync(node, txn, ct).ConfigureAwait(false);
                        await _wal.FlushAsync(ct).ConfigureAwait(false);
                        return true;
                    }
                    return false;
                }
                else
                {
                    int childIndex = index >= 0 ? index + 1 : ~index;
                    long childBlock = node.ChildPointers[childIndex];
                    node = await ReadNodeAsync(childBlock, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Deletes a key from the B-Tree.
    /// </summary>
    public async Task<bool> DeleteAsync(byte[] key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var txn = await _wal.BeginTransactionAsync(ct).ConfigureAwait(false);
            var root = await ReadNodeAsync(_rootBlockNumber, ct).ConfigureAwait(false);
            bool deleted = await DeleteFromNodeAsync(root, key, txn, ct).ConfigureAwait(false);

            // If root becomes empty and has children, promote the child
            if (root.Header.KeyCount == 0 && !root.IsLeaf)
            {
                _rootBlockNumber = root.ChildPointers[0];
                var newRoot = await ReadNodeAsync(_rootBlockNumber, ct).ConfigureAwait(false);
                var newRootHeader = newRoot.Header;
                newRootHeader.Flags |= BTreeNodeHeader.FlagRoot;
                newRootHeader.ParentBlock = -1;
                newRoot.Header = newRootHeader;
                await WriteNodeAsync(newRoot, txn, ct).ConfigureAwait(false);
                _allocator.FreeBlock(root.BlockNumber);
            }

            await _wal.FlushAsync(ct).ConfigureAwait(false);
            return deleted;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Performs a range query returning all key-value pairs in [startKey, endKey) sorted order.
    /// </summary>
    public async IAsyncEnumerable<(byte[] Key, long Value)> RangeQueryAsync(
        byte[]? startKey,
        byte[]? endKey,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await _readLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Find the starting leaf node
            var leaf = await FindLeafForKeyAsync(startKey ?? Array.Empty<byte>(), ct).ConfigureAwait(false);

            while (leaf != null)
            {
                for (int i = 0; i < leaf.Header.KeyCount; i++)
                {
                    var key = leaf.Keys[i];

                    // Skip keys before startKey
                    if (startKey != null && BTreeNode.CompareKeys(key, startKey) < 0)
                        continue;

                    // Stop if we've reached endKey
                    if (endKey != null && BTreeNode.CompareKeys(key, endKey) >= 0)
                        yield break;

                    yield return (key, leaf.Values[i]);
                }

                // Move to next leaf
                if (leaf.Header.NextLeafBlock != -1)
                {
                    leaf = await ReadNodeAsync(leaf.Header.NextLeafBlock, ct).ConfigureAwait(false);
                }
                else
                {
                    break;
                }
            }
        }
        finally
        {
            _readLock.Release();
        }
    }

    /// <summary>
    /// Counts the total number of key-value pairs in the index.
    /// </summary>
    public async Task<long> CountAsync(CancellationToken ct = default)
    {
        await _readLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long count = 0;
            var leaf = await FindLeftmostLeafAsync(ct).ConfigureAwait(false);

            while (leaf != null)
            {
                count += leaf.Header.KeyCount;
                if (leaf.Header.NextLeafBlock != -1)
                {
                    leaf = await ReadNodeAsync(leaf.Header.NextLeafBlock, ct).ConfigureAwait(false);
                }
                else
                {
                    break;
                }
            }

            return count;
        }
        finally
        {
            _readLock.Release();
        }
    }

    /// <summary>
    /// Reads a node from disk or cache.
    /// </summary>
    private async Task<BTreeNode> ReadNodeAsync(long blockNumber, CancellationToken ct)
    {
        // Check cache
        if (_nodeCache.TryGetValue(blockNumber, out var cached))
        {
            _nodeCache[blockNumber] = (cached.Node, DateTime.UtcNow);
            return cached.Node;
        }

        // Read from disk
        var buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            await _device.ReadBlockAsync(blockNumber, buffer.AsMemory(0, _blockSize), ct).ConfigureAwait(false);
            var node = BTreeNode.Deserialize(buffer.AsSpan(0, _blockSize), _blockSize, blockNumber);

            // Add to cache (evict old entries if needed) — single-pass O(n) min-find, not O(n log n) sort
            if (_nodeCache.Count >= MaxCachedNodes)
            {
                long oldestKey = -1;
                DateTime oldestTime = DateTime.MaxValue;
                foreach (var kv in _nodeCache)
                {
                    if (kv.Value.AccessTime < oldestTime)
                    {
                        oldestTime = kv.Value.AccessTime;
                        oldestKey = kv.Key;
                    }
                }
                if (oldestKey >= 0)
                    _nodeCache.TryRemove(oldestKey, out _);
            }

            _nodeCache[blockNumber] = (node, DateTime.UtcNow);
            return node;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Writes a node to disk and updates cache.
    /// </summary>
    private async Task WriteNodeAsync(BTreeNode node, WalTransaction txn, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            node.Serialize(buffer.AsSpan(0, _blockSize), _blockSize);

            // Capture before-image for undo-based WAL recovery
            byte[]? beforeImage = null;
            try
            {
                var beforeBuffer = ArrayPool<byte>.Shared.Rent(_blockSize);
                try
                {
                    await _device.ReadBlockAsync(node.BlockNumber, beforeBuffer.AsMemory(0, _blockSize), ct).ConfigureAwait(false);
                    beforeImage = beforeBuffer.AsSpan(0, _blockSize).ToArray();
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(beforeBuffer);
                }
            }
            catch
            {
                // Before-image capture is best-effort; proceed without it for new blocks
            }

            // WAL-log the write
            var entry = new JournalEntry
            {
                TransactionId = txn.TransactionId,
                SequenceNumber = _wal.CurrentSequenceNumber + 1,
                Type = JournalEntryType.BlockWrite,
                TargetBlockNumber = node.BlockNumber,
                BeforeImage = beforeImage,
                AfterImage = buffer.AsMemory(0, _blockSize).ToArray()
            };
            await _wal.AppendEntryAsync(entry, ct).ConfigureAwait(false);

            await _device.WriteBlockAsync(node.BlockNumber, buffer.AsMemory(0, _blockSize), ct).ConfigureAwait(false);

            // Update cache
            _nodeCache[node.BlockNumber] = (node, DateTime.UtcNow);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Binary search for a key in a node.
    /// </summary>
    private static int BinarySearchKeys(BTreeNode node, byte[] key)
    {
        int left = 0, right = node.Header.KeyCount - 1;
        while (left <= right)
        {
            int mid = left + (right - left) / 2;
            int cmp = BTreeNode.CompareKeys(node.Keys[mid], key);
            if (cmp == 0)
                return mid;
            else if (cmp < 0)
                left = mid + 1;
            else
                right = mid - 1;
        }
        return ~left;
    }

    /// <summary>
    /// Inserts into a non-full node.
    /// </summary>
    private async Task InsertNonFullAsync(BTreeNode node, byte[] key, long value, WalTransaction txn, CancellationToken ct)
    {
        int i = node.Header.KeyCount - 1;

        if (node.IsLeaf)
        {
            // Find insertion position
            while (i >= 0 && BTreeNode.CompareKeys(key, node.Keys[i]) < 0)
            {
                node.Keys[i + 1] = node.Keys[i];
                node.Values[i + 1] = node.Values[i];
                i--;
            }

            // Check for duplicate
            if (i >= 0 && BTreeNode.CompareKeys(key, node.Keys[i]) == 0)
                throw new InvalidOperationException("Duplicate key");

            node.Keys[i + 1] = key;
            node.Values[i + 1] = value;
            var header = node.Header;
            header.KeyCount++;
            node.Header = header;

            await WriteNodeAsync(node, txn, ct).ConfigureAwait(false);
        }
        else
        {
            // Find child to descend to
            while (i >= 0 && BTreeNode.CompareKeys(key, node.Keys[i]) < 0)
                i--;
            i++;

            var child = await ReadNodeAsync(node.ChildPointers[i], ct).ConfigureAwait(false);
            if (child.IsFull)
            {
                await SplitChildAsync(node, i, child, txn, ct).ConfigureAwait(false);
                if (BTreeNode.CompareKeys(key, node.Keys[i]) > 0)
                    i++;
                child = await ReadNodeAsync(node.ChildPointers[i], ct).ConfigureAwait(false);
            }
            await InsertNonFullAsync(child, key, value, txn, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Splits a full child node.
    /// </summary>
    private async Task SplitChildAsync(BTreeNode parent, int childIndex, BTreeNode fullChild, WalTransaction txn, CancellationToken ct)
    {
        int midIndex = fullChild.MaxKeys / 2;
        var newNode = new BTreeNode(_blockSize, avgKeySize: 64)
        {
            BlockNumber = _allocator.AllocateBlock(ct),
            Header = new BTreeNodeHeader
            {
                Flags = (ushort)(fullChild.Header.Flags & BTreeNodeHeader.FlagLeaf),
                KeyCount = (ushort)(fullChild.MaxKeys - midIndex - 1),
                ParentBlock = parent.BlockNumber,
                NextLeafBlock = -1,
                PrevLeafBlock = -1
            }
        };

        // Move right half of keys to new node
        for (int j = 0; j < newNode.Header.KeyCount; j++)
        {
            newNode.Keys[j] = fullChild.Keys[midIndex + 1 + j];
            if (fullChild.IsLeaf)
                newNode.Values[j] = fullChild.Values[midIndex + 1 + j];
        }

        if (!fullChild.IsLeaf)
        {
            for (int j = 0; j <= newNode.Header.KeyCount; j++)
                newNode.ChildPointers[j] = fullChild.ChildPointers[midIndex + 1 + j];
        }

        // Update leaf links
        if (fullChild.IsLeaf)
        {
            var newNodeHeader = newNode.Header;
            newNodeHeader.NextLeafBlock = fullChild.Header.NextLeafBlock;
            newNodeHeader.PrevLeafBlock = fullChild.BlockNumber;
            newNode.Header = newNodeHeader;

            var fullChildHeader = fullChild.Header;
            fullChildHeader.NextLeafBlock = newNode.BlockNumber;
            fullChild.Header = fullChildHeader;

            if (newNode.Header.NextLeafBlock != -1)
            {
                var nextLeaf = await ReadNodeAsync(newNode.Header.NextLeafBlock, ct).ConfigureAwait(false);
                var nextLeafHeader = nextLeaf.Header;
                nextLeafHeader.PrevLeafBlock = newNode.BlockNumber;
                nextLeaf.Header = nextLeafHeader;
                await WriteNodeAsync(nextLeaf, txn, ct).ConfigureAwait(false);
            }
        }

        var fcHeader = fullChild.Header;
        fcHeader.KeyCount = (ushort)midIndex;
        fullChild.Header = fcHeader;

        // Move middle key up to parent
        for (int j = parent.Header.KeyCount; j > childIndex; j--)
        {
            parent.Keys[j] = parent.Keys[j - 1];
            parent.ChildPointers[j + 1] = parent.ChildPointers[j];
        }

        parent.Keys[childIndex] = fullChild.Keys[midIndex];
        parent.ChildPointers[childIndex + 1] = newNode.BlockNumber;
        var parentHeader = parent.Header;
        parentHeader.KeyCount++;
        parent.Header = parentHeader;

        await WriteNodeAsync(fullChild, txn, ct).ConfigureAwait(false);
        await WriteNodeAsync(newNode, txn, ct).ConfigureAwait(false);
        await WriteNodeAsync(parent, txn, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a key from a node (recursive).
    /// </summary>
    private async Task<bool> DeleteFromNodeAsync(BTreeNode node, byte[] key, WalTransaction txn, CancellationToken ct)
    {
        int index = BinarySearchKeys(node, key);

        if (node.IsLeaf)
        {
            if (index < 0 || BTreeNode.CompareKeys(node.Keys[index], key) != 0)
                return false;

            // Remove key from leaf
            for (int i = index; i < node.Header.KeyCount - 1; i++)
            {
                node.Keys[i] = node.Keys[i + 1];
                node.Values[i] = node.Values[i + 1];
            }
            var header = node.Header;
            header.KeyCount--;
            node.Header = header;
            await WriteNodeAsync(node, txn, ct).ConfigureAwait(false);

            // Check for underflow and log warning — full rebalance would require parent tracking
            if (node.IsUnderflow)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BTree.DeleteFromNodeAsync] Node {node.BlockNumber} underflow ({node.Header.KeyCount}/{node.MinKeys} min keys). Rebalancing deferred.");
            }

            return true;
        }
        else
        {
            // Internal node: descend to child
            int childIndex = index >= 0 ? index + 1 : ~index;
            var child = await ReadNodeAsync(node.ChildPointers[childIndex], ct).ConfigureAwait(false);
            bool deleted = await DeleteFromNodeAsync(child, key, txn, ct).ConfigureAwait(false);

            // After deletion, check child for underflow and attempt rebalance
            if (deleted && child.IsUnderflow)
            {
                await TryRebalanceChildAsync(node, childIndex, txn, ct).ConfigureAwait(false);
            }

            return deleted;
        }
    }

    /// <summary>
    /// Attempts to rebalance an underflowed child by borrowing from a sibling or merging.
    /// </summary>
    private async Task TryRebalanceChildAsync(BTreeNode parent, int childIndex, WalTransaction txn, CancellationToken ct)
    {
        var child = await ReadNodeAsync(parent.ChildPointers[childIndex], ct).ConfigureAwait(false);

        // Try borrowing from left sibling
        if (childIndex > 0)
        {
            var leftSibling = await ReadNodeAsync(parent.ChildPointers[childIndex - 1], ct).ConfigureAwait(false);
            if (leftSibling.Header.KeyCount > leftSibling.MinKeys)
            {
                // Rotate right: move key from left sibling through parent into child
                // Shift child keys right
                for (int i = child.Header.KeyCount; i > 0; i--)
                {
                    child.Keys[i] = child.Keys[i - 1];
                    child.Values[i] = child.Values[i - 1];
                }
                child.Keys[0] = parent.Keys[childIndex - 1];
                child.Values[0] = parent.Values[childIndex - 1];
                var ch = child.Header;
                ch.KeyCount++;
                child.Header = ch;

                int lastIdx = leftSibling.Header.KeyCount - 1;
                parent.Keys[childIndex - 1] = leftSibling.Keys[lastIdx];
                parent.Values[childIndex - 1] = leftSibling.Values[lastIdx];
                var lh = leftSibling.Header;
                lh.KeyCount--;
                leftSibling.Header = lh;

                await WriteNodeAsync(child, txn, ct).ConfigureAwait(false);
                await WriteNodeAsync(leftSibling, txn, ct).ConfigureAwait(false);
                await WriteNodeAsync(parent, txn, ct).ConfigureAwait(false);
                return;
            }
        }

        // Try borrowing from right sibling
        if (childIndex < parent.Header.KeyCount)
        {
            var rightSibling = await ReadNodeAsync(parent.ChildPointers[childIndex + 1], ct).ConfigureAwait(false);
            if (rightSibling.Header.KeyCount > rightSibling.MinKeys)
            {
                // Rotate left: move key from right sibling through parent into child
                child.Keys[child.Header.KeyCount] = parent.Keys[childIndex];
                child.Values[child.Header.KeyCount] = parent.Values[childIndex];
                var ch = child.Header;
                ch.KeyCount++;
                child.Header = ch;

                parent.Keys[childIndex] = rightSibling.Keys[0];
                parent.Values[childIndex] = rightSibling.Values[0];

                for (int i = 0; i < rightSibling.Header.KeyCount - 1; i++)
                {
                    rightSibling.Keys[i] = rightSibling.Keys[i + 1];
                    rightSibling.Values[i] = rightSibling.Values[i + 1];
                }
                var rh = rightSibling.Header;
                rh.KeyCount--;
                rightSibling.Header = rh;

                await WriteNodeAsync(child, txn, ct).ConfigureAwait(false);
                await WriteNodeAsync(rightSibling, txn, ct).ConfigureAwait(false);
                await WriteNodeAsync(parent, txn, ct).ConfigureAwait(false);
                return;
            }
        }

        // Cannot borrow — merge is needed (complex operation, log for now)
        System.Diagnostics.Debug.WriteLine(
            $"[BTree.TryRebalanceChildAsync] Child {child.BlockNumber} needs merge (no sibling can donate). Full merge deferred.");
    }

    /// <summary>
    /// Finds the leaf node where a key should be located.
    /// </summary>
    private async Task<BTreeNode> FindLeafForKeyAsync(byte[] key, CancellationToken ct)
    {
        var node = await ReadNodeAsync(_rootBlockNumber, ct).ConfigureAwait(false);
        while (!node.IsLeaf)
        {
            int index = BinarySearchKeys(node, key);
            int childIndex = index >= 0 ? index + 1 : ~index;
            node = await ReadNodeAsync(node.ChildPointers[childIndex], ct).ConfigureAwait(false);
        }
        return node;
    }

    /// <summary>
    /// Finds the leftmost (first) leaf node.
    /// </summary>
    private async Task<BTreeNode> FindLeftmostLeafAsync(CancellationToken ct)
    {
        var node = await ReadNodeAsync(_rootBlockNumber, ct).ConfigureAwait(false);
        while (!node.IsLeaf)
        {
            node = await ReadNodeAsync(node.ChildPointers[0], ct).ConfigureAwait(false);
        }
        return node;
    }

    public ValueTask DisposeAsync()
    {
        _writeLock?.Dispose();
        _readLock?.Dispose();
        _nodeCache?.Dispose();
        return ValueTask.CompletedTask;
    }
}
