using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using DataWarehouse.SDK.VirtualDiskEngine.Journal;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Level 3: Buffered epsilon tree (Be-tree) with epsilon=0.5 message buffers.
/// </summary>
/// <remarks>
/// <para>
/// The Be-tree is a write-optimized B-tree variant that batches writes in internal node
/// message buffers, flushing them downward only when buffers overflow. This achieves
/// O(log_B(N)/epsilon) amortized I/Os per write vs O(log_B(N)) for standard B-trees.
/// </para>
/// <para>
/// With epsilon = 0.5, the buffer capacity at each internal node is sqrt(B) where B is
/// the node fanout. Messages include Insert, Update, Delete (tombstone), and Upsert operations.
/// On lookup, pending messages are collected along the root-to-leaf path and resolved with
/// the leaf entry to determine the effective value.
/// </para>
/// <para>
/// All structural modifications (flushes, splits) are WAL-protected for crash safety.
/// Concurrent readers are allowed with a single writer via ReaderWriterLockSlim.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-02 Be-tree")]
public sealed class BeTree : IAdaptiveIndex, IAsyncDisposable
{
    private readonly IBlockDevice _device;
    private readonly IBlockAllocator _allocator;
    private readonly IWriteAheadLog _wal;
    private readonly int _blockSize;
    private readonly double _epsilon;
    private readonly ReaderWriterLockSlim _lock;
    private readonly BoundedDictionary<long, BeTreeNode> _nodeCache;
    private const int MaxCachedNodes = 2000;

    private long _rootBlockNumber;
    private long _count;
    private long _nextTimestamp;

    /// <summary>
    /// Morph-up threshold: when object count exceeds this, recommend morphing to next level.
    /// </summary>
    private const long MorphUpThreshold = 10_000_000;

    /// <summary>
    /// Morph-down threshold: when object count drops below this, recommend morphing to previous level.
    /// </summary>
    private const long MorphDownThreshold = 100_000;

    /// <inheritdoc />
    public MorphLevel CurrentLevel => MorphLevel.BeTree;

    /// <inheritdoc />
    public long ObjectCount => Interlocked.Read(ref _count);

    /// <inheritdoc />
    public long RootBlockNumber => _rootBlockNumber;

    /// <inheritdoc />
#pragma warning disable CS0067 // Event is declared by IAdaptiveIndex; raised by AdaptiveIndexEngine orchestrator
    public event Action<MorphLevel, MorphLevel>? LevelChanged;
#pragma warning restore CS0067

    /// <summary>
    /// Initializes a new Be-tree instance.
    /// </summary>
    /// <param name="device">Block device for I/O.</param>
    /// <param name="allocator">Block allocator for new nodes.</param>
    /// <param name="wal">Write-ahead log for crash recovery.</param>
    /// <param name="rootBlockNumber">Block number of the root node.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="epsilon">Epsilon parameter (default 0.5). Controls buffer size vs fanout tradeoff.</param>
    public BeTree(
        IBlockDevice device,
        IBlockAllocator allocator,
        IWriteAheadLog wal,
        long rootBlockNumber,
        int blockSize,
        double epsilon = 0.5)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _wal = wal ?? throw new ArgumentNullException(nameof(wal));
        _rootBlockNumber = rootBlockNumber;
        _blockSize = blockSize;
        _epsilon = epsilon;
        _lock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        _nodeCache = new BoundedDictionary<long, BeTreeNode>(MaxCachedNodes);
    }

    /// <inheritdoc />
    public async Task InsertAsync(byte[] key, long value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        _lock.EnterWriteLock();
        try
        {
            var txn = await _wal.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                var root = await ReadNodeAsync(_rootBlockNumber, ct).ConfigureAwait(false);
                var timestamp = Interlocked.Increment(ref _nextTimestamp);
                var message = new BeTreeMessage(BeTreeMessageType.Insert, key, value, timestamp);

                if (root.IsLeaf)
                {
                    // For leaf root, apply directly
                    ApplyMessageToLeaf(root, message);
                    await WriteNodeAsync(root, txn, ct).ConfigureAwait(false);
                }
                else
                {
                    // Buffer message in root
                    root.Messages.Add(message);
                    root.Messages.Sort();

                    if (root.IsBufferFull)
                    {
                        await FlushBufferAsync(root, txn, ct).ConfigureAwait(false);
                    }

                    await WriteNodeAsync(root, txn, ct).ConfigureAwait(false);
                }

                Interlocked.Increment(ref _count);
                await _wal.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BeTree.InsertAsync] WAL transaction error, rolling back: {ex.Message}");
                // WAL will discard uncommitted entries on next recovery
                throw;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(byte[] key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        _lock.EnterWriteLock();
        try
        {
            // Verify key exists before issuing tombstone
            var existing = await LookupInternalAsync(key, ct).ConfigureAwait(false);
            if (existing == null)
                return false;

            var txn = await _wal.BeginTransactionAsync(ct).ConfigureAwait(false);
            var root = await ReadNodeAsync(_rootBlockNumber, ct).ConfigureAwait(false);
            var timestamp = Interlocked.Increment(ref _nextTimestamp);
            var message = new BeTreeMessage(BeTreeMessageType.Delete, key, 0, timestamp);

            if (root.IsLeaf)
            {
                ApplyMessageToLeaf(root, message);
                await WriteNodeAsync(root, txn, ct).ConfigureAwait(false);
            }
            else
            {
                root.Messages.Add(message);
                root.Messages.Sort();

                if (root.IsBufferFull)
                {
                    await FlushBufferAsync(root, txn, ct).ConfigureAwait(false);
                }

                await WriteNodeAsync(root, txn, ct).ConfigureAwait(false);
            }

            Interlocked.Decrement(ref _count);
            await _wal.FlushAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public async Task<long?> LookupAsync(byte[] key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        _lock.EnterReadLock();
        try
        {
            return await LookupInternalAsync(key, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(byte[] key, long newValue, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        _lock.EnterWriteLock();
        try
        {
            // Check if key exists before issuing update
            var existing = await LookupInternalAsync(key, ct).ConfigureAwait(false);
            if (existing == null)
                return false;

            var txn = await _wal.BeginTransactionAsync(ct).ConfigureAwait(false);
            var root = await ReadNodeAsync(_rootBlockNumber, ct).ConfigureAwait(false);
            var timestamp = Interlocked.Increment(ref _nextTimestamp);
            var message = new BeTreeMessage(BeTreeMessageType.Upsert, key, newValue, timestamp);

            if (root.IsLeaf)
            {
                ApplyMessageToLeaf(root, message);
                await WriteNodeAsync(root, txn, ct).ConfigureAwait(false);
            }
            else
            {
                root.Messages.Add(message);
                root.Messages.Sort();

                if (root.IsBufferFull)
                {
                    await FlushBufferAsync(root, txn, ct).ConfigureAwait(false);
                }

                await WriteNodeAsync(root, txn, ct).ConfigureAwait(false);
            }

            await _wal.FlushAsync(ct).ConfigureAwait(false);
            return true;
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
        _lock.EnterReadLock();
        try
        {
            // Collect all resolved entries from the tree via in-order traversal
            var results = new SortedList<byte[], long>(Comparer<byte[]>.Create(BeTreeMessage.CompareKeys));
            await CollectRangeAsync(_rootBlockNumber, startKey, endKey, results, new List<BeTreeMessage>(), ct).ConfigureAwait(false);

            foreach (var entry in results)
            {
                yield return (entry.Key, entry.Value);
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(CancellationToken ct = default)
    {
        _lock.EnterReadLock();
        try
        {
            // Full tree traversal counting resolved live entries
            return await CountResolvedAsync(_rootBlockNumber, new List<BeTreeMessage>(), ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public Task MorphToAsync(MorphLevel targetLevel, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            $"BeTree (Level 3) does not directly morph to {targetLevel}. Use AdaptiveIndexEngine for level transitions.");
    }

    /// <inheritdoc />
    public Task<MorphLevel> RecommendLevelAsync(CancellationToken ct = default)
    {
        long count = ObjectCount;
        if (count < MorphDownThreshold)
            return Task.FromResult(MorphLevel.AdaptiveRadixTree);
        if (count > MorphUpThreshold)
            return Task.FromResult(MorphLevel.LearnedIndex);
        return Task.FromResult(MorphLevel.BeTree);
    }

    /// <summary>
    /// Internal lookup without acquiring the read lock (called from within write lock too).
    /// </summary>
    private async Task<long?> LookupInternalAsync(byte[] key, CancellationToken ct)
    {
        var pendingMessages = new List<BeTreeMessage>();
        var node = await ReadNodeAsync(_rootBlockNumber, ct).ConfigureAwait(false);

        while (!node.IsLeaf)
        {
            // Collect pending messages for this key from the buffer
            foreach (var msg in node.Messages)
            {
                if (BeTreeMessage.CompareKeys(msg.Key, key) == 0)
                {
                    pendingMessages.Add(msg);
                }
            }

            // Descend to correct child
            int childIndex = FindChildIndex(node, key);
            long childBlock = node.ChildPointers[childIndex];
            node = await ReadNodeAsync(childBlock, ct).ConfigureAwait(false);
        }

        // At leaf: find entry
        long? leafValue = null;
        foreach (var (entryKey, entryValue) in node.Entries)
        {
            if (BeTreeMessage.CompareKeys(entryKey, key) == 0)
            {
                leafValue = entryValue;
                break;
            }
        }

        // Apply message resolution
        if (pendingMessages.Count > 0)
        {
            pendingMessages.Sort();
            var resolved = BeTreeMessage.Resolve(pendingMessages);
            // If there are pending messages, they override the leaf value
            return resolved;
        }

        return leafValue;
    }

    /// <summary>
    /// Flushes an internal node's buffer by partitioning messages by child range
    /// and pushing each partition to the corresponding child's buffer.
    /// </summary>
    private async Task FlushBufferAsync(BeTreeNode node, WalTransaction txn, CancellationToken ct)
    {
        if (node.IsLeaf || node.Messages.Count == 0)
            return;

        // Partition messages by child range
        var partitions = new List<BeTreeMessage>[node.ChildPointers.Count];
        for (int i = 0; i < partitions.Length; i++)
            partitions[i] = new List<BeTreeMessage>();

        foreach (var msg in node.Messages)
        {
            int childIndex = FindChildIndex(node, msg.Key);
            partitions[childIndex].Add(msg);
        }

        // Clear the node's buffer
        node.Messages.Clear();

        // Push partitions to children
        for (int i = 0; i < partitions.Length; i++)
        {
            if (partitions[i].Count == 0)
                continue;

            var child = await ReadNodeAsync(node.ChildPointers[i], ct).ConfigureAwait(false);

            if (child.IsLeaf)
            {
                // Apply messages directly to leaf
                foreach (var msg in partitions[i])
                {
                    ApplyMessageToLeaf(child, msg);
                }

                // Split leaf if over capacity
                if (child.IsLeafFull)
                {
                    await SplitLeafAsync(node, i, child, txn, ct).ConfigureAwait(false);
                }
                else
                {
                    await WriteNodeAsync(child, txn, ct).ConfigureAwait(false);
                }
            }
            else
            {
                // Push to child's buffer
                child.Messages.AddRange(partitions[i]);
                child.Messages.Sort();

                // Recursively flush if child buffer overflows
                if (child.IsBufferFull)
                {
                    await FlushBufferAsync(child, txn, ct).ConfigureAwait(false);
                }

                await WriteNodeAsync(child, txn, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Applies a single message to a leaf node.
    /// </summary>
    private static void ApplyMessageToLeaf(BeTreeNode leaf, BeTreeMessage message)
    {
        int index = -1;
        for (int i = 0; i < leaf.Entries.Count; i++)
        {
            int cmp = BeTreeMessage.CompareKeys(leaf.Entries[i].Key, message.Key);
            if (cmp == 0)
            {
                index = i;
                break;
            }
            if (cmp > 0)
            {
                // Insert position found
                index = ~i;
                break;
            }
        }
        if (index == -1 && leaf.Entries.Count > 0)
            index = ~leaf.Entries.Count; // Append at end

        switch (message.Type)
        {
            case BeTreeMessageType.Insert:
                if (index >= 0)
                {
                    // Key exists; update value (upsert behavior for idempotency)
                    leaf.Entries[index] = (message.Key, message.Value);
                }
                else
                {
                    int insertAt = ~index;
                    leaf.Entries.Insert(insertAt, (message.Key, message.Value));
                }
                break;

            case BeTreeMessageType.Update:
            case BeTreeMessageType.Upsert:
                if (index >= 0)
                {
                    leaf.Entries[index] = (message.Key, message.Value);
                }
                else
                {
                    int insertAt = ~index;
                    leaf.Entries.Insert(insertAt, (message.Key, message.Value));
                }
                break;

            case BeTreeMessageType.Delete:
                if (index >= 0)
                {
                    leaf.Entries.RemoveAt(index);
                }
                break;
        }
    }

    /// <summary>
    /// Splits a full leaf node, promoting the median key to the parent.
    /// </summary>
    private async Task SplitLeafAsync(BeTreeNode parent, int childIndex, BeTreeNode fullLeaf, WalTransaction txn, CancellationToken ct)
    {
        int mid = fullLeaf.Entries.Count / 2;

        var newLeaf = new BeTreeNode(_blockSize, _epsilon)
        {
            IsLeaf = true,
            BlockNumber = _allocator.AllocateBlock(ct)
        };

        // Move right half to new leaf
        for (int i = mid; i < fullLeaf.Entries.Count; i++)
        {
            newLeaf.Entries.Add(fullLeaf.Entries[i]);
        }

        var medianKey = fullLeaf.Entries[mid].Key;

        // Trim left leaf
        fullLeaf.Entries.RemoveRange(mid, fullLeaf.Entries.Count - mid);

        // Insert pivot and child pointer into parent
        parent.PivotKeys.Insert(childIndex, medianKey);
        parent.ChildPointers.Insert(childIndex + 1, newLeaf.BlockNumber);

        // Check if parent needs splitting too
        if (parent.IsInternalFull)
        {
            // For simplicity, write both leaves first, then handle parent split
            await WriteNodeAsync(fullLeaf, txn, ct).ConfigureAwait(false);
            await WriteNodeAsync(newLeaf, txn, ct).ConfigureAwait(false);
            await SplitInternalRootIfNeeded(parent, txn, ct).ConfigureAwait(false);
        }
        else
        {
            await WriteNodeAsync(fullLeaf, txn, ct).ConfigureAwait(false);
            await WriteNodeAsync(newLeaf, txn, ct).ConfigureAwait(false);
            await WriteNodeAsync(parent, txn, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Splits the root internal node if it becomes too full.
    /// Creates a new root with two children.
    /// </summary>
    private async Task SplitInternalRootIfNeeded(BeTreeNode node, WalTransaction txn, CancellationToken ct)
    {
        if (!node.IsInternalFull)
        {
            await WriteNodeAsync(node, txn, ct).ConfigureAwait(false);
            return;
        }

        int mid = node.PivotKeys.Count / 2;
        var promotedKey = node.PivotKeys[mid];

        var newRight = new BeTreeNode(_blockSize, _epsilon)
        {
            IsLeaf = false,
            BlockNumber = _allocator.AllocateBlock(ct)
        };

        // Move right half of pivots and children to new node
        for (int i = mid + 1; i < node.PivotKeys.Count; i++)
            newRight.PivotKeys.Add(node.PivotKeys[i]);
        for (int i = mid + 1; i < node.ChildPointers.Count; i++)
            newRight.ChildPointers.Add(node.ChildPointers[i]);

        // Partition messages between left and right based on promoted key
        var leftMessages = new List<BeTreeMessage>();
        var rightMessages = new List<BeTreeMessage>();
        foreach (var msg in node.Messages)
        {
            if (BeTreeMessage.CompareKeys(msg.Key, promotedKey) < 0)
                leftMessages.Add(msg);
            else
                rightMessages.Add(msg);
        }
        newRight.Messages.AddRange(rightMessages);

        // Trim left node
        node.PivotKeys.RemoveRange(mid, node.PivotKeys.Count - mid);
        node.ChildPointers.RemoveRange(mid + 1, node.ChildPointers.Count - mid - 1);
        node.Messages.Clear();
        node.Messages.AddRange(leftMessages);

        // If this node is the root, create a new root
        if (node.BlockNumber == _rootBlockNumber)
        {
            var newRoot = new BeTreeNode(_blockSize, _epsilon)
            {
                IsLeaf = false,
                BlockNumber = _allocator.AllocateBlock(ct)
            };
            newRoot.PivotKeys.Add(promotedKey);
            newRoot.ChildPointers.Add(node.BlockNumber);
            newRoot.ChildPointers.Add(newRight.BlockNumber);

            _rootBlockNumber = newRoot.BlockNumber;

            await WriteNodeAsync(node, txn, ct).ConfigureAwait(false);
            await WriteNodeAsync(newRight, txn, ct).ConfigureAwait(false);
            await WriteNodeAsync(newRoot, txn, ct).ConfigureAwait(false);
        }
        else
        {
            await WriteNodeAsync(node, txn, ct).ConfigureAwait(false);
            await WriteNodeAsync(newRight, txn, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Finds the child index for a given key in an internal node.
    /// Returns the index into ChildPointers for the subtree containing the key.
    /// </summary>
    private static int FindChildIndex(BeTreeNode node, byte[] key)
    {
        for (int i = 0; i < node.PivotKeys.Count; i++)
        {
            if (BeTreeMessage.CompareKeys(key, node.PivotKeys[i]) < 0)
                return i;
        }
        return node.PivotKeys.Count;
    }

    /// <summary>
    /// Recursively collects range query results, merging buffer messages with child results.
    /// </summary>
    private async Task CollectRangeAsync(
        long blockNumber,
        byte[]? startKey,
        byte[]? endKey,
        SortedList<byte[], long> results,
        List<BeTreeMessage> pendingMessages,
        CancellationToken ct)
    {
        var node = await ReadNodeAsync(blockNumber, ct).ConfigureAwait(false);

        if (node.IsLeaf)
        {
            // Merge leaf entries with pending messages
            foreach (var (key, value) in node.Entries)
            {
                if (startKey != null && BeTreeMessage.CompareKeys(key, startKey) < 0)
                    continue;
                if (endKey != null && BeTreeMessage.CompareKeys(key, endKey) >= 0)
                    continue;

                // Check if any pending message overrides this entry
                var keyMessages = pendingMessages.Where(m => BeTreeMessage.CompareKeys(m.Key, key) == 0).ToList();
                if (keyMessages.Count > 0)
                {
                    keyMessages.Sort();
                    var resolved = BeTreeMessage.Resolve(keyMessages);
                    if (resolved.HasValue)
                    {
                        results[key] = resolved.Value;
                    }
                    // If resolved to null (deleted), skip this entry
                }
                else
                {
                    results[key] = value;
                }
            }

            // Also check for insert/upsert messages for keys not in leaf
            foreach (var msg in pendingMessages)
            {
                if (msg.Type == BeTreeMessageType.Delete)
                    continue;
                if (startKey != null && BeTreeMessage.CompareKeys(msg.Key, startKey) < 0)
                    continue;
                if (endKey != null && BeTreeMessage.CompareKeys(msg.Key, endKey) >= 0)
                    continue;

                if (!results.ContainsKey(msg.Key))
                {
                    // Check if this key was already handled from leaf
                    bool inLeaf = node.Entries.Any(e => BeTreeMessage.CompareKeys(e.Key, msg.Key) == 0);
                    if (!inLeaf)
                    {
                        results[msg.Key] = msg.Value;
                    }
                }
            }
        }
        else
        {
            // Accumulate buffer messages
            var combined = new List<BeTreeMessage>(pendingMessages);
            combined.AddRange(node.Messages);

            // Visit all relevant children
            for (int i = 0; i < node.ChildPointers.Count; i++)
            {
                // Determine child key range
                byte[]? childStart = i > 0 ? node.PivotKeys[i - 1] : null;
                byte[]? childEnd = i < node.PivotKeys.Count ? node.PivotKeys[i] : null;

                // Check if child range overlaps with query range
                if (endKey != null && childStart != null && BeTreeMessage.CompareKeys(childStart, endKey) >= 0)
                    continue;
                if (startKey != null && childEnd != null && BeTreeMessage.CompareKeys(childEnd, startKey) <= 0)
                    continue;

                await CollectRangeAsync(node.ChildPointers[i], startKey, endKey, results, combined, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Recursively counts resolved live entries in the tree.
    /// </summary>
    private async Task<long> CountResolvedAsync(long blockNumber, List<BeTreeMessage> pendingMessages, CancellationToken ct)
    {
        var node = await ReadNodeAsync(blockNumber, ct).ConfigureAwait(false);

        if (node.IsLeaf)
        {
            long count = 0;
            foreach (var (key, _) in node.Entries)
            {
                var keyMessages = pendingMessages.Where(m => BeTreeMessage.CompareKeys(m.Key, key) == 0).ToList();
                if (keyMessages.Count > 0)
                {
                    keyMessages.Sort();
                    var resolved = BeTreeMessage.Resolve(keyMessages);
                    if (resolved.HasValue)
                        count++;
                }
                else
                {
                    count++;
                }
            }

            // Count inserts for keys not in leaf
            var insertKeys = pendingMessages
                .Where(m => m.Type != BeTreeMessageType.Delete)
                .Select(m => m.Key)
                .Distinct(new ByteArrayEqualityComparer());

            foreach (var key in insertKeys)
            {
                bool inLeaf = node.Entries.Any(e => BeTreeMessage.CompareKeys(e.Key, key) == 0);
                if (!inLeaf)
                {
                    var keyMessages = pendingMessages.Where(m => BeTreeMessage.CompareKeys(m.Key, key) == 0).ToList();
                    keyMessages.Sort();
                    var resolved = BeTreeMessage.Resolve(keyMessages);
                    if (resolved.HasValue)
                        count++;
                }
            }

            return count;
        }
        else
        {
            var combined = new List<BeTreeMessage>(pendingMessages);
            combined.AddRange(node.Messages);

            long total = 0;
            for (int i = 0; i < node.ChildPointers.Count; i++)
            {
                total += await CountResolvedAsync(node.ChildPointers[i], combined, ct).ConfigureAwait(false);
            }
            return total;
        }
    }

    /// <summary>
    /// Reads a node from disk or cache.
    /// </summary>
    private async Task<BeTreeNode> ReadNodeAsync(long blockNumber, CancellationToken ct)
    {
        if (_nodeCache.TryGetValue(blockNumber, out var cached))
        {
            // Return a clone to prevent concurrent read/write mutation visibility
            return cached.Clone();
        }

        var buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            await _device.ReadBlockAsync(blockNumber, buffer.AsMemory(0, _blockSize), ct).ConfigureAwait(false);
            var node = BeTreeNode.Deserialize(buffer, _blockSize);
            node.BlockNumber = blockNumber;
            _nodeCache[blockNumber] = node;
            return node;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Writes a node to disk and updates cache. WAL-protects the write.
    /// </summary>
    private async Task WriteNodeAsync(BeTreeNode node, WalTransaction txn, CancellationToken ct)
    {
        var data = node.Serialize(_blockSize);

        var entry = new JournalEntry
        {
            TransactionId = txn.TransactionId,
            SequenceNumber = _wal.CurrentSequenceNumber + 1,
            Type = JournalEntryType.BlockWrite,
            TargetBlockNumber = node.BlockNumber,
            BeforeImage = null,
            AfterImage = data
        };
        await _wal.AppendEntryAsync(entry, ct).ConfigureAwait(false);
        await _device.WriteBlockAsync(node.BlockNumber, data.AsMemory(0, _blockSize), ct).ConfigureAwait(false);

        _nodeCache[node.BlockNumber] = node;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _lock?.Dispose();
        _nodeCache?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Byte array equality comparer for LINQ Distinct operations.
    /// </summary>
    private sealed class ByteArrayEqualityComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y)
        {
            if (x is null && y is null) return true;
            if (x is null || y is null) return false;
            return x.AsSpan().SequenceEqual(y);
        }

        public int GetHashCode(byte[] obj)
        {
            if (obj.Length == 0) return 0;
            // FNV-1a hash
            unchecked
            {
                int hash = (int)2166136261;
                foreach (byte b in obj)
                    hash = (hash ^ b) * 16777619;
                return hash;
            }
        }
    }
}
