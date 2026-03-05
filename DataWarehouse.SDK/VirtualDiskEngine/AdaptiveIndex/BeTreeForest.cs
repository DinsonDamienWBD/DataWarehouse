using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using DataWarehouse.SDK.VirtualDiskEngine.Journal;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Level 5: Sharded Be-tree forest with Hilbert curve partitioning, learned shard routing,
/// auto-split/merge, and per-shard independent morph level tracking.
/// </summary>
/// <remarks>
/// <para>
/// At very large scale (100M+ objects), a single Be-tree becomes bottlenecked. The forest
/// shards the key space across multiple independent Be-trees (or any <see cref="IAdaptiveIndex"/>
/// implementation), each capable of morphing independently based on its own object count.
/// </para>
/// <para>
/// Hilbert partitioning provides better spatial locality than Z-order for multi-dimensional keys.
/// The <see cref="LearnedShardRouter"/> provides O(1) amortized shard selection via a CDF model.
/// </para>
/// <para>
/// Thread safety: per-shard <see cref="SemaphoreSlim"/>(1,1) for mutations; reads are concurrent.
/// Forest-level operations (split/merge) acquire a global <see cref="ReaderWriterLockSlim"/>.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-04 Be-tree Forest")]
public sealed class BeTreeForest : IAdaptiveIndex, IAsyncDisposable
{
    private readonly IBlockDevice _device;
    private readonly IBlockAllocator _allocator;
    private readonly IWriteAheadLog _wal;
    private readonly int _blockSize;
    private readonly long _splitThreshold;
    private readonly long _mergeThreshold;
    private readonly ReaderWriterLockSlim _forestLock = new(LockRecursionPolicy.NoRecursion);

    private readonly List<ForestShard> _shards;
    private LearnedShardRouter _router;
    private long _totalObjectCount;

    /// <inheritdoc />
    public MorphLevel CurrentLevel => MorphLevel.BeTreeForest;

    /// <inheritdoc />
    public long ObjectCount => Interlocked.Read(ref _totalObjectCount);

    /// <inheritdoc />
    // Cat 2 (finding 714): acquire read lock before accessing _shards.Count — concurrent shard creation races
    public long RootBlockNumber
    {
        get
        {
            _forestLock.EnterReadLock();
            try { return _shards.Count > 0 ? _shards[0].Index.RootBlockNumber : -1; }
            finally { _forestLock.ExitReadLock(); }
        }
    }

    /// <inheritdoc />
#pragma warning disable CS0067 // Event is declared by IAdaptiveIndex; raised by AdaptiveIndexEngine orchestrator
    public event Action<MorphLevel, MorphLevel>? LevelChanged;
#pragma warning restore CS0067

    /// <summary>
    /// Morph-up threshold: when total object count exceeds this, recommend DistributedRouting.
    /// </summary>
    private const long MorphUpThreshold = 1_000_000_000;

    /// <summary>
    /// Morph-down threshold: when total object count drops below this, recommend LearnedIndex.
    /// </summary>
    private const long MorphDownThreshold = 10_000_000;

    /// <summary>
    /// Initializes a new <see cref="BeTreeForest"/>.
    /// </summary>
    /// <param name="device">Block device for I/O.</param>
    /// <param name="allocator">Block allocator for new nodes.</param>
    /// <param name="wal">Write-ahead log for crash recovery.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="initialShardCount">Number of initial shards (default 4).</param>
    /// <param name="splitThreshold">Per-shard object count that triggers auto-split (default 10M).</param>
    public BeTreeForest(
        IBlockDevice device,
        IBlockAllocator allocator,
        IWriteAheadLog wal,
        int blockSize,
        int initialShardCount = 4,
        long splitThreshold = 10_000_000)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _wal = wal ?? throw new ArgumentNullException(nameof(wal));
        _blockSize = blockSize;
        _splitThreshold = splitThreshold;
        _mergeThreshold = splitThreshold / 4;

        if (initialShardCount < 1)
            throw new ArgumentOutOfRangeException(nameof(initialShardCount), initialShardCount, "Must have at least 1 shard.");

        _shards = new List<ForestShard>(initialShardCount);
        _router = new LearnedShardRouter(initialShardCount);

        // Initialize shards with Hilbert-partitioned key ranges
        var partitions = HilbertPartitioner.PartitionHilbertSpace(initialShardCount, 16);

        for (int i = 0; i < initialShardCount; i++)
        {
            long rootBlock = _allocator.AllocateBlock(default);
            var index = CreateShardIndex(rootBlock);

            var minKey = HilbertPartitioner.HilbertValueToKey(partitions[i].Start, 2, 16);
            var maxKey = HilbertPartitioner.HilbertValueToKey(partitions[i].End, 2, 16);

            _shards.Add(new ForestShard(
                index: index,
                keyRangeMin: minKey,
                keyRangeMax: maxKey,
                level: MorphLevel.DirectPointer,
                objectCount: 0,
                shardLock: new SemaphoreSlim(1, 1)));
        }
    }

    /// <inheritdoc />
    public async Task<long?> LookupAsync(byte[] key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        _forestLock.EnterReadLock();
        try
        {
            int shardId = ResolveShardId(key);
            var shard = _shards[shardId];
            return await shard.Index.LookupAsync(key, ct).ConfigureAwait(false);
        }
        finally
        {
            _forestLock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public async Task InsertAsync(byte[] key, long value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        _forestLock.EnterReadLock();
        try
        {
            int shardId = ResolveShardId(key);
            var shard = _shards[shardId];

            await shard.ShardLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await shard.Index.InsertAsync(key, value, ct).ConfigureAwait(false);
                shard.IncrementCount();
                Interlocked.Increment(ref _totalObjectCount);
            }
            finally
            {
                shard.ShardLock.Release();
            }
        }
        finally
        {
            _forestLock.ExitReadLock();
        }

        // Check if auto-split is needed (outside read lock to avoid deadlock)
        int checkShardId = ResolveShardIdSafe(key);
        if (checkShardId >= 0 && checkShardId < _shards.Count)
        {
            var checkShard = _shards[checkShardId];
            if (Interlocked.Read(ref checkShard._objectCount) > _splitThreshold)
            {
                await TryAutoSplitAsync(checkShardId, ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(byte[] key, long newValue, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        _forestLock.EnterReadLock();
        try
        {
            int shardId = ResolveShardId(key);
            var shard = _shards[shardId];

            await shard.ShardLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await shard.Index.UpdateAsync(key, newValue, ct).ConfigureAwait(false);
            }
            finally
            {
                shard.ShardLock.Release();
            }
        }
        finally
        {
            _forestLock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(byte[] key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        bool deleted;
        int shardId;

        _forestLock.EnterReadLock();
        try
        {
            shardId = ResolveShardId(key);
            var shard = _shards[shardId];

            await shard.ShardLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                deleted = await shard.Index.DeleteAsync(key, ct).ConfigureAwait(false);
                if (deleted)
                {
                    shard.DecrementCount();
                    Interlocked.Decrement(ref _totalObjectCount);
                    _router.DecrementShardCount(shardId);
                }
            }
            finally
            {
                shard.ShardLock.Release();
            }
        }
        finally
        {
            _forestLock.ExitReadLock();
        }

        // Check if auto-merge is needed
        if (deleted && shardId >= 0 && shardId < _shards.Count && _shards.Count > 1)
        {
            var checkShard = _shards[shardId];
            if (Interlocked.Read(ref checkShard._objectCount) < _mergeThreshold)
            {
                await TryAutoMergeAsync(shardId, ct).ConfigureAwait(false);
            }
        }

        return deleted;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<(byte[] Key, long Value)> RangeQueryAsync(
        byte[]? startKey,
        byte[]? endKey,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Identify all shards overlapping [startKey, endKey)
        List<ForestShard> overlappingShards;

        _forestLock.EnterReadLock();
        try
        {
            overlappingShards = GetOverlappingShards(startKey, endKey);
        }
        finally
        {
            _forestLock.ExitReadLock();
        }

        // Query each overlapping shard and merge-sort results
        // Collect all results first, then sort and yield
        var allResults = new SortedList<byte[], long>(Comparer<byte[]>.Create(BeTreeMessage.CompareKeys));

        foreach (var shard in overlappingShards)
        {
            await foreach (var entry in shard.Index.RangeQueryAsync(startKey, endKey, ct).ConfigureAwait(false))
            {
                allResults[entry.Key] = entry.Value;
            }
        }

        foreach (var entry in allResults)
        {
            yield return (entry.Key, entry.Value);
        }
    }

    /// <inheritdoc />
    public Task<long> CountAsync(CancellationToken ct = default)
    {
        return Task.FromResult(Interlocked.Read(ref _totalObjectCount));
    }

    /// <inheritdoc />
    public Task MorphToAsync(MorphLevel targetLevel, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            $"BeTreeForest (Level 5) does not directly morph to {targetLevel}. Use AdaptiveIndexEngine for level transitions.");
    }

    /// <inheritdoc />
    public Task<MorphLevel> RecommendLevelAsync(CancellationToken ct = default)
    {
        long count = ObjectCount;
        if (count < MorphDownThreshold)
            return Task.FromResult(MorphLevel.LearnedIndex);
        if (count > MorphUpThreshold)
            return Task.FromResult(MorphLevel.DistributedRouting);
        return Task.FromResult(MorphLevel.BeTreeForest);
    }

    /// <summary>
    /// Gets the current number of shards in the forest.
    /// </summary>
    public int ShardCount
    {
        get
        {
            _forestLock.EnterReadLock();
            try { return _shards.Count; }
            finally { _forestLock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Gets the morph level of a specific shard.
    /// </summary>
    /// <param name="shardIndex">The shard index.</param>
    /// <returns>The morph level of the shard.</returns>
    public MorphLevel GetShardLevel(int shardIndex)
    {
        _forestLock.EnterReadLock();
        try
        {
            if (shardIndex < 0 || shardIndex >= _shards.Count)
                throw new ArgumentOutOfRangeException(nameof(shardIndex));
            return _shards[shardIndex].Level;
        }
        finally
        {
            _forestLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets the object count of a specific shard.
    /// </summary>
    /// <param name="shardIndex">The shard index.</param>
    /// <returns>The object count of the shard.</returns>
    public long GetShardObjectCount(int shardIndex)
    {
        _forestLock.EnterReadLock();
        try
        {
            if (shardIndex < 0 || shardIndex >= _shards.Count)
                throw new ArgumentOutOfRangeException(nameof(shardIndex));
            return Interlocked.Read(ref _shards[shardIndex]._objectCount);
        }
        finally
        {
            _forestLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Resolves the shard ID for a key using the learned router.
    /// Must be called under forest read lock.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ResolveShardId(byte[] key)
    {
        int shardId = _router.RouteReadOnly(key);
        return Math.Clamp(shardId, 0, _shards.Count - 1);
    }

    /// <summary>
    /// Resolves shard ID safely (acquires read lock). Returns -1 if lock cannot be acquired.
    /// </summary>
    private int ResolveShardIdSafe(byte[] key)
    {
        _forestLock.EnterReadLock();
        try
        {
            return ResolveShardId(key);
        }
        finally
        {
            _forestLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Returns all shards whose key range overlaps with [startKey, endKey).
    /// Must be called under forest read lock.
    /// </summary>
    private List<ForestShard> GetOverlappingShards(byte[]? startKey, byte[]? endKey)
    {
        if (startKey == null && endKey == null)
            return new List<ForestShard>(_shards);

        var result = new List<ForestShard>();
        foreach (var shard in _shards)
        {
            // Check overlap: shard range [min, max] overlaps [startKey, endKey)
            if (endKey != null && BeTreeMessage.CompareKeys(shard.KeyRangeMin, endKey) >= 0)
                continue;
            if (startKey != null && BeTreeMessage.CompareKeys(shard.KeyRangeMax, startKey) < 0)
                continue;
            result.Add(shard);
        }

        return result.Count > 0 ? result : new List<ForestShard>(_shards);
    }

    /// <summary>
    /// Attempts to auto-split an oversized shard. Acquires forest write lock.
    /// WAL-protects the split operation.
    /// </summary>
    private async Task TryAutoSplitAsync(int shardId, CancellationToken ct)
    {
        _forestLock.EnterWriteLock();
        try
        {
            if (shardId >= _shards.Count)
                return;

            var shard = _shards[shardId];
            long shardCount = Interlocked.Read(ref shard._objectCount);
            if (shardCount <= _splitThreshold)
                return; // Already below threshold (concurrent operation resolved it)

            // WAL: split-start marker
            var splitStartEntry = new JournalEntry
            {
                TransactionId = -1,
                Type = JournalEntryType.Checkpoint,
                TargetBlockNumber = shard.Index.RootBlockNumber,
                BeforeImage = BitConverter.GetBytes(shardId),
                AfterImage = BitConverter.GetBytes(_shards.Count)
            };
            await _wal.AppendEntryAsync(splitStartEntry, ct).ConfigureAwait(false);
            await _wal.FlushAsync(ct).ConfigureAwait(false);

            // Find approximate median via sampling
            var sampleKeys = new List<byte[]>();
            int sampleCount = 0;
            const int maxSamples = 1000;

            await foreach (var entry in shard.Index.RangeQueryAsync(null, null, ct).ConfigureAwait(false))
            {
                if (sampleCount % Math.Max(1, (int)(shardCount / maxSamples)) == 0)
                    sampleKeys.Add(entry.Key);
                sampleCount++;
                if (sampleKeys.Count >= maxSamples)
                    break;
            }

            if (sampleKeys.Count < 2)
                return; // Not enough data to split meaningfully

            sampleKeys.Sort((a, b) => BeTreeMessage.CompareKeys(a, b));
            var medianKey = sampleKeys[sampleKeys.Count / 2];

            // Create new shard
            long newRootBlock = _allocator.AllocateBlock(ct);
            var newIndex = CreateShardIndex(newRootBlock);

            var newShard = new ForestShard(
                index: newIndex,
                keyRangeMin: medianKey,
                keyRangeMax: shard.KeyRangeMax,
                level: MorphLevel.DirectPointer,
                objectCount: 0,
                shardLock: new SemaphoreSlim(1, 1));

            // Transfer entries > median to new shard
            long transferred = 0;
            var keysToDelete = new List<byte[]>();

            await foreach (var entry in shard.Index.RangeQueryAsync(medianKey, null, ct).ConfigureAwait(false))
            {
                await newIndex.InsertAsync(entry.Key, entry.Value, ct).ConfigureAwait(false);
                keysToDelete.Add(entry.Key);
                transferred++;
            }

            // Remove transferred keys from original shard
            foreach (var key in keysToDelete)
            {
                await shard.Index.DeleteAsync(key, ct).ConfigureAwait(false);
            }

            // Update shard metadata
            shard.KeyRangeMax = medianKey;
            Interlocked.Add(ref shard._objectCount, -transferred);
            Interlocked.Exchange(ref newShard._objectCount, transferred);

            // Insert new shard after current
            _shards.Insert(shardId + 1, newShard);

            // Rebuild router with new shard boundaries
            RebuildRouter();

            // WAL: split-complete marker
            var splitCompleteEntry = new JournalEntry
            {
                TransactionId = -1,
                Type = JournalEntryType.Checkpoint,
                TargetBlockNumber = newRootBlock,
                BeforeImage = BitConverter.GetBytes(shardId),
                AfterImage = new byte[] { 0xFF } // Complete marker
            };
            await _wal.AppendEntryAsync(splitCompleteEntry, ct).ConfigureAwait(false);
            await _wal.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _forestLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Attempts to auto-merge an undersized shard with an adjacent shard. Acquires forest write lock.
    /// </summary>
    private async Task TryAutoMergeAsync(int shardId, CancellationToken ct)
    {
        _forestLock.EnterWriteLock();
        try
        {
            if (_shards.Count <= 1)
                return; // Cannot merge below 1 shard

            if (shardId >= _shards.Count)
                return;

            var shard = _shards[shardId];
            long shardCount = Interlocked.Read(ref shard._objectCount);
            if (shardCount >= _mergeThreshold)
                return;

            // Find adjacent shard to merge with (prefer the smaller neighbor)
            int mergeTarget;
            if (shardId == 0)
                mergeTarget = 1;
            else if (shardId == _shards.Count - 1)
                mergeTarget = shardId - 1;
            else
            {
                long leftCount = Interlocked.Read(ref _shards[shardId - 1]._objectCount);
                long rightCount = Interlocked.Read(ref _shards[shardId + 1]._objectCount);
                mergeTarget = leftCount <= rightCount ? shardId - 1 : shardId + 1;
            }

            var targetShard = _shards[mergeTarget];

            // WAL: merge-start marker
            var mergeStartEntry = new JournalEntry
            {
                TransactionId = -1,
                Type = JournalEntryType.Checkpoint,
                TargetBlockNumber = shard.Index.RootBlockNumber,
                BeforeImage = BitConverter.GetBytes(shardId),
                AfterImage = BitConverter.GetBytes(mergeTarget)
            };
            await _wal.AppendEntryAsync(mergeStartEntry, ct).ConfigureAwait(false);
            await _wal.FlushAsync(ct).ConfigureAwait(false);

            // Transfer all entries from source shard to target shard
            long transferred = 0;
            await foreach (var entry in shard.Index.RangeQueryAsync(null, null, ct).ConfigureAwait(false))
            {
                await targetShard.Index.InsertAsync(entry.Key, entry.Value, ct).ConfigureAwait(false);
                transferred++;
            }

            // Update target shard metadata
            Interlocked.Add(ref targetShard._objectCount, transferred);

            // Expand target key range to cover merged shard
            if (BeTreeMessage.CompareKeys(shard.KeyRangeMin, targetShard.KeyRangeMin) < 0)
                targetShard.KeyRangeMin = shard.KeyRangeMin;
            if (BeTreeMessage.CompareKeys(shard.KeyRangeMax, targetShard.KeyRangeMax) > 0)
                targetShard.KeyRangeMax = shard.KeyRangeMax;

            // Remove empty shard
            shard.ShardLock.Dispose();
            if (shard.Index is IAsyncDisposable disposable)
                await disposable.DisposeAsync().ConfigureAwait(false);
            else if (shard.Index is IDisposable syncDisposable)
                syncDisposable.Dispose();

            _shards.RemoveAt(shardId);

            // Rebuild router
            RebuildRouter();

            // WAL: merge-complete marker
            var mergeCompleteEntry = new JournalEntry
            {
                TransactionId = -1,
                Type = JournalEntryType.Checkpoint,
                TargetBlockNumber = targetShard.Index.RootBlockNumber,
                BeforeImage = BitConverter.GetBytes(mergeTarget),
                AfterImage = new byte[] { 0xFF }
            };
            await _wal.AppendEntryAsync(mergeCompleteEntry, ct).ConfigureAwait(false);
            await _wal.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _forestLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Rebuilds the learned shard router from current shard boundaries.
    /// Must be called under forest write lock.
    /// </summary>
    private void RebuildRouter()
    {
        var newRouter = new LearnedShardRouter(_shards.Count);

        // Build sample keys from shard boundaries
        var sampleKeys = new List<byte[]>();
        foreach (var shard in _shards)
        {
            sampleKeys.Add(shard.KeyRangeMin);
            sampleKeys.Add(shard.KeyRangeMax);
        }

        if (sampleKeys.Count > 0)
            newRouter.TrainFromSample(sampleKeys);

        _router = newRouter;
    }

    /// <summary>
    /// Creates a new shard index (starts as DirectPointer, will morph as objects accumulate).
    /// Each shard has its own <see cref="AdaptiveIndexEngine"/> internally for independent morphing.
    /// </summary>
    private IAdaptiveIndex CreateShardIndex(long rootBlock)
    {
        return new AdaptiveIndexEngine(
            _device,
            _allocator,
            _wal,
            rootBlock,
            _blockSize);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var shard in _shards)
        {
            shard.ShardLock.Dispose();
            if (shard.Index is IAsyncDisposable disposable)
                await disposable.DisposeAsync().ConfigureAwait(false);
            else if (shard.Index is IDisposable syncDisposable)
                syncDisposable.Dispose();
        }
        _shards.Clear();
        _forestLock.Dispose();
    }

    /// <summary>
    /// Represents a single shard in the Be-tree forest with its own index, key range,
    /// morph level, and object count.
    /// </summary>
    internal sealed class ForestShard
    {
        /// <summary>
        /// The adaptive index for this shard (independently morphing).
        /// </summary>
        public IAdaptiveIndex Index { get; }

        /// <summary>
        /// Minimum key in this shard's range (inclusive).
        /// </summary>
        public byte[] KeyRangeMin { get; internal set; }

        /// <summary>
        /// Maximum key in this shard's range (inclusive).
        /// </summary>
        public byte[] KeyRangeMax { get; internal set; }

        /// <summary>
        /// Current morph level of this shard's index.
        /// </summary>
        public MorphLevel Level => Index.CurrentLevel;

        /// <summary>
        /// Object count for this shard. Use Interlocked for thread-safe access.
        /// </summary>
        internal long _objectCount;

        /// <summary>
        /// Per-shard mutation lock.
        /// </summary>
        public SemaphoreSlim ShardLock { get; }

        public ForestShard(
            IAdaptiveIndex index,
            byte[] keyRangeMin,
            byte[] keyRangeMax,
            MorphLevel level,
            long objectCount,
            SemaphoreSlim shardLock)
        {
            Index = index ?? throw new ArgumentNullException(nameof(index));
            KeyRangeMin = keyRangeMin ?? throw new ArgumentNullException(nameof(keyRangeMin));
            KeyRangeMax = keyRangeMax ?? throw new ArgumentNullException(nameof(keyRangeMax));
            _objectCount = objectCount;
            ShardLock = shardLock ?? throw new ArgumentNullException(nameof(shardLock));
        }

        /// <summary>
        /// Thread-safe increment of the object count.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void IncrementCount() => Interlocked.Increment(ref _objectCount);

        /// <summary>
        /// Thread-safe decrement of the object count.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DecrementCount() => Interlocked.Decrement(ref _objectCount);
    }
}
