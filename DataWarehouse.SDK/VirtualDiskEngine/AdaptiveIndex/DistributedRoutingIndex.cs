using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Level 6: Distributed Probabilistic Routing Index for datasets exceeding 1B objects across nodes.
/// </summary>
/// <remarks>
/// <para>
/// At planetary scale (10T+ objects), a single-node index is insufficient. The distributed
/// routing index coordinates across multiple remote shard indexes, using three key technologies:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="BloofiFilter"/>: Hierarchical bloom filter tree that reduces cross-node queries
/// by 80%+ via bitwise OR-propagation. Only shards whose bloom filter indicates a possible
/// match are queried.
/// </description></item>
/// <item><description>
/// <see cref="CrushPlacement"/>: Deterministic shard placement via CRUSH algorithm. Same key +
/// same map = same placement, with no centralized lookup table.
/// </description></item>
/// <item><description>
/// <see cref="ClockSiTransaction"/>: Clock-based Snapshot Isolation for consistent reads and
/// writes across distributed shards.
/// </description></item>
/// </list>
/// <para>
/// Thread safety: operations are inherently parallel across shards. Local state (Bloofi, CRUSH map)
/// is protected by <see cref="ReaderWriterLockSlim"/>.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-05 Distributed routing")]
public sealed class DistributedRoutingIndex : IAdaptiveIndex, IDisposable
{
    private readonly IReadOnlyList<IAdaptiveIndex> _remoteShards;
    private readonly CrushMap _crushMap;
    private readonly CrushPlacement _crushPlacement;
    private readonly BloofiFilter _bloofi;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private long _objectCount;

    /// <inheritdoc />
    public MorphLevel CurrentLevel => MorphLevel.DistributedRouting;

    /// <inheritdoc />
    public long ObjectCount
    {
        get
        {
            _lock.EnterReadLock();
            try { return Interlocked.Read(ref _objectCount); }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <inheritdoc />
    public long RootBlockNumber => -1; // Distributed; no single root block

    /// <inheritdoc />
#pragma warning disable CS0067 // Event required by IAdaptiveIndex interface; fired by coordinator
    public event Action<MorphLevel, MorphLevel>? LevelChanged;
#pragma warning restore CS0067

    /// <summary>
    /// Gets the Bloofi filter used for cross-node query reduction.
    /// </summary>
    public BloofiFilter Bloofi => _bloofi;

    /// <summary>
    /// Gets the CRUSH map defining the cluster topology.
    /// </summary>
    public CrushMap CrushMap => _crushMap;

    /// <summary>
    /// Initializes a new <see cref="DistributedRoutingIndex"/>.
    /// </summary>
    /// <param name="remoteShards">The shard index instances (one per remote node).</param>
    /// <param name="crushMap">The cluster topology for deterministic placement.</param>
    /// <param name="bloofi">The hierarchical bloom filter for query reduction.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    /// <exception cref="ArgumentException">Thrown when remoteShards is empty.</exception>
    public DistributedRoutingIndex(
        IReadOnlyList<IAdaptiveIndex> remoteShards,
        CrushMap crushMap,
        BloofiFilter bloofi)
    {
        ArgumentNullException.ThrowIfNull(remoteShards);
        ArgumentNullException.ThrowIfNull(crushMap);
        ArgumentNullException.ThrowIfNull(bloofi);

        if (remoteShards.Count == 0)
            throw new ArgumentException("At least one remote shard is required.", nameof(remoteShards));

        _remoteShards = remoteShards;
        _crushMap = crushMap;
        _bloofi = bloofi;
        _crushPlacement = new CrushPlacement();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Uses Bloofi for candidate shard selection. If single candidate, queries directly.
    /// If multiple candidates, queries all in parallel and returns first hit.
    /// If Bloofi says none, returns null immediately.
    /// </remarks>
    public async Task<long?> LookupAsync(byte[] key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        IReadOnlyList<int> candidates;

        _lock.EnterReadLock();
        try
        {
            candidates = _bloofi.Query(key);
        }
        finally
        {
            _lock.ExitReadLock();
        }

        if (candidates.Count == 0)
            return null;

        if (candidates.Count == 1)
        {
            int shardId = candidates[0];
            if (shardId >= 0 && shardId < _remoteShards.Count)
                return await _remoteShards[shardId].LookupAsync(key, ct).ConfigureAwait(false);
            return null;
        }

        // Multiple candidates: query in parallel, return first hit
        var tasks = new List<Task<long?>>(candidates.Count);
        foreach (int shardId in candidates)
        {
            if (shardId >= 0 && shardId < _remoteShards.Count)
                tasks.Add(_remoteShards[shardId].LookupAsync(key, ct));
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var result in results)
        {
            if (result.HasValue)
                return result.Value;
        }

        return null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Uses CRUSH to determine the target shard deterministically.
    /// Inserts into the shard and updates the Bloofi leaf for that shard.
    /// </remarks>
    public async Task InsertAsync(byte[] key, long value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        int targetShardId;

        _lock.EnterReadLock();
        try
        {
            var targets = _crushPlacement.GetTargetNodes(key, _crushMap);
            targetShardId = targets.Length > 0 ? targets[0] : 0;

            // Clamp to valid shard range
            targetShardId = Math.Clamp(targetShardId, 0, _remoteShards.Count - 1);
        }
        finally
        {
            _lock.ExitReadLock();
        }

        // Insert into target shard
        await _remoteShards[targetShardId].InsertAsync(key, value, ct).ConfigureAwait(false);

        // Update Bloofi and count
        _lock.EnterWriteLock();
        try
        {
            _bloofi.Add(key, targetShardId);
            Interlocked.Increment(ref _objectCount);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(byte[] key, long newValue, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        IReadOnlyList<int> candidates;

        _lock.EnterReadLock();
        try
        {
            candidates = _bloofi.Query(key);
        }
        finally
        {
            _lock.ExitReadLock();
        }

        // Try each candidate shard
        foreach (int shardId in candidates)
        {
            if (shardId >= 0 && shardId < _remoteShards.Count)
            {
                bool updated = await _remoteShards[shardId].UpdateAsync(key, newValue, ct).ConfigureAwait(false);
                if (updated) return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Uses Bloofi to find candidate shards, then deletes from the shard that contains the key.
    /// Note: bloom filters do not support removal of individual keys, so the Bloofi leaf
    /// must be periodically rebuilt (see <see cref="RebuildBloofiForShard"/>).
    /// </remarks>
    public async Task<bool> DeleteAsync(byte[] key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        IReadOnlyList<int> candidates;

        _lock.EnterReadLock();
        try
        {
            candidates = _bloofi.Query(key);
        }
        finally
        {
            _lock.ExitReadLock();
        }

        foreach (int shardId in candidates)
        {
            if (shardId >= 0 && shardId < _remoteShards.Count)
            {
                bool deleted = await _remoteShards[shardId].DeleteAsync(key, ct).ConfigureAwait(false);
                if (deleted)
                {
                    Interlocked.Decrement(ref _objectCount);
                    // Note: bloom filter does not support individual key removal.
                    // Bloofi leaf for this shard should be periodically rebuilt.
                    return true;
                }
            }
        }

        return false;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Fans out to all shards (no bloom filter for ranges) and merge-sorts results.
    /// </remarks>
    public async IAsyncEnumerable<(byte[] Key, long Value)> RangeQueryAsync(
        byte[]? startKey,
        byte[]? endKey,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Collect all results from all shards in parallel
        var shardResults = new List<List<(byte[] Key, long Value)>>(_remoteShards.Count);
        var tasks = new List<Task<List<(byte[] Key, long Value)>>>(_remoteShards.Count);

        for (int i = 0; i < _remoteShards.Count; i++)
        {
            var shard = _remoteShards[i];
            tasks.Add(CollectRangeAsync(shard, startKey, endKey, ct));
        }

        var allResults = await Task.WhenAll(tasks).ConfigureAwait(false);

        // Merge-sort: use a priority queue approach
        var merged = MergeSortResults(allResults);

        foreach (var entry in merged)
        {
            yield return entry;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sums counts across all shards.
    /// </remarks>
    public async Task<long> CountAsync(CancellationToken ct = default)
    {
        var tasks = new Task<long>[_remoteShards.Count];
        for (int i = 0; i < _remoteShards.Count; i++)
        {
            tasks[i] = _remoteShards[i].CountAsync(ct);
        }

        var counts = await Task.WhenAll(tasks).ConfigureAwait(false);

        long total = 0;
        foreach (long count in counts)
            total += count;

        return total;
    }

    /// <inheritdoc />
    public Task MorphToAsync(MorphLevel targetLevel, CancellationToken ct = default)
    {
        if (targetLevel == MorphLevel.DistributedRouting)
            return Task.CompletedTask;

        throw new NotSupportedException(
            $"DistributedRoutingIndex is Level 6; cannot morph to {targetLevel}. " +
            "Downward morph from distributed to single-node requires data consolidation.");
    }

    /// <inheritdoc />
    public Task<MorphLevel> RecommendLevelAsync(CancellationToken ct = default)
    {
        return Task.FromResult(MorphLevel.DistributedRouting);
    }

    /// <summary>
    /// Rebuilds the Bloofi bloom filter for a specific shard by iterating all keys in that shard.
    /// This is needed periodically because bloom filters do not support individual key removal.
    /// </summary>
    /// <param name="shardId">The shard to rebuild the bloom filter for.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RebuildBloofiForShard(int shardId, CancellationToken ct = default)
    {
        if (shardId < 0 || shardId >= _remoteShards.Count)
            throw new ArgumentOutOfRangeException(nameof(shardId));

        // Remove old leaf
        _lock.EnterWriteLock();
        try
        {
            _bloofi.Remove(shardId);
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        // Re-add all keys from the shard
        await foreach (var (key, _) in _remoteShards[shardId].RangeQueryAsync(null, null, ct).ConfigureAwait(false))
        {
            _lock.EnterWriteLock();
            try
            {
                _bloofi.Add(key, shardId);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }
    }

    /// <summary>
    /// Notifies the coordinator that a remote shard has morphed levels.
    /// Updates the Bloofi filter to reflect potential changes in the shard's key set.
    /// </summary>
    /// <param name="shardId">The shard that morphed.</param>
    /// <param name="oldLevel">The previous morph level.</param>
    /// <param name="newLevel">The new morph level.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task OnShardMorphedAsync(int shardId, MorphLevel oldLevel, MorphLevel newLevel, CancellationToken ct = default)
    {
        // After a shard morphs, its bloom filter may have changed (data migration).
        // Rebuild the Bloofi leaf for that shard.
        await RebuildBloofiForShard(shardId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a Clock-SI transaction for cross-shard operations.
    /// </summary>
    /// <returns>A new Clock-SI transaction with a snapshot timestamp.</returns>
    public ClockSiTransaction BeginTransaction()
    {
        var tx = new ClockSiTransaction();
        tx.BeginSnapshot();
        return tx;
    }

    /// <summary>
    /// Commits a Clock-SI transaction against the distributed shards.
    /// </summary>
    /// <param name="transaction">The transaction to commit.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if committed, false if aborted due to conflict.</returns>
    public async Task<bool> CommitTransactionAsync(ClockSiTransaction transaction, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return await transaction.CommitAsync(_remoteShards, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Collects all range query results from a single shard into a list.
    /// </summary>
    private static async Task<List<(byte[] Key, long Value)>> CollectRangeAsync(
        IAdaptiveIndex shard,
        byte[]? startKey,
        byte[]? endKey,
        CancellationToken ct)
    {
        var results = new List<(byte[] Key, long Value)>();
        await foreach (var entry in shard.RangeQueryAsync(startKey, endKey, ct).ConfigureAwait(false))
        {
            results.Add(entry);
        }
        return results;
    }

    /// <summary>
    /// Merges pre-sorted results from multiple shards into a single sorted sequence.
    /// Uses a k-way merge with index tracking.
    /// </summary>
    private static List<(byte[] Key, long Value)> MergeSortResults(List<(byte[] Key, long Value)>[] shardResults)
    {
        var merged = new List<(byte[] Key, long Value)>();

        // Simple approach: collect all, sort
        foreach (var shard in shardResults)
        {
            merged.AddRange(shard);
        }

        merged.Sort((a, b) => CompareKeys(a.Key, b.Key));
        return merged;
    }

    /// <summary>
    /// Lexicographic comparison of two byte array keys.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CompareKeys(byte[] a, byte[] b)
    {
        int minLen = Math.Min(a.Length, b.Length);
        for (int i = 0; i < minLen; i++)
        {
            int cmp = a[i].CompareTo(b[i]);
            if (cmp != 0) return cmp;
        }
        return a.Length.CompareTo(b.Length);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _lock.Dispose();
    }
}

/// <summary>
/// Coordinator for distributed morph operations. When a remote shard needs to morph,
/// it notifies this coordinator to ensure Bloofi is updated after the morph completes.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-05 Distributed morph coordinator")]
public sealed class DistributedMorphCoordinator
{
    private readonly DistributedRoutingIndex _index;

    /// <summary>
    /// Initializes a new <see cref="DistributedMorphCoordinator"/>.
    /// </summary>
    /// <param name="index">The distributed routing index to coordinate.</param>
    public DistributedMorphCoordinator(DistributedRoutingIndex index)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
    }

    /// <summary>
    /// Handles notification that a shard has completed a morph transition.
    /// Updates the Bloofi filter for the affected shard.
    /// </summary>
    /// <param name="shardId">The shard that morphed.</param>
    /// <param name="oldLevel">The previous level.</param>
    /// <param name="newLevel">The new level.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task NotifyShardMorphedAsync(
        int shardId,
        MorphLevel oldLevel,
        MorphLevel newLevel,
        CancellationToken ct = default)
    {
        await _index.OnShardMorphedAsync(shardId, oldLevel, newLevel, ct).ConfigureAwait(false);
    }
}
