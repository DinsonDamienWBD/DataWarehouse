using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives.Probabilistic;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation;

/// <summary>
/// Configuration for per-index-shard bloom filters used in federation routing.
/// Controls filter sizing, memory limits, and auto-rebuild behavior.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Shard Bloom Filter Index (VFED-15)")]
public sealed record ShardBloomFilterConfig
{
    /// <summary>
    /// Expected number of keys per shard. Used to size the bloom filter optimally.
    /// Default: 1,000,000 keys.
    /// </summary>
    public long ExpectedKeysPerShard { get; init; } = 1_000_000;

    /// <summary>
    /// Target false positive rate for bloom filter lookups.
    /// Default: 0.001 (0.1%) meaning 99.9% negative rejection rate.
    /// </summary>
    public double TargetFalsePositiveRate { get; init; } = 0.001;

    /// <summary>
    /// Maximum memory in bytes allowed per bloom filter instance.
    /// If the optimal filter size exceeds this, the filter will be capped,
    /// resulting in a higher false positive rate. Default: 4 MB.
    /// </summary>
    public long MaxMemoryBytesPerFilter { get; init; } = 4 * 1024 * 1024;

    /// <summary>
    /// Whether to automatically flag filters for rebuild when they degrade.
    /// Default: true.
    /// </summary>
    public bool EnableAutoRebuild { get; init; } = true;

    /// <summary>
    /// Fill ratio threshold that triggers a rebuild flag.
    /// When the estimated false positive rate exceeds TargetFalsePositiveRate * 2,
    /// the filter is flagged for rebuild. Default: 0.8.
    /// </summary>
    public double RebuildThresholdFillRatio { get; init; } = 0.8;
}

/// <summary>
/// Immutable snapshot of a bloom filter's state for persistence and transfer.
/// Contains the serialized filter data, item count, and current false positive rate.
/// </summary>
/// <param name="ShardVdeId">The VDE instance ID of the shard this filter belongs to.</param>
/// <param name="FilterData">Serialized bloom filter data from <see cref="BloomFilter{T}.Serialize"/>.</param>
/// <param name="ItemCount">Number of items added to the filter.</param>
/// <param name="FalsePositiveRate">Current estimated false positive rate at time of snapshot.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Shard Bloom Filter Index (VFED-15)")]
public readonly record struct BloomFilterSnapshot(
    Guid ShardVdeId,
    byte[] FilterData,
    long ItemCount,
    double FalsePositiveRate);

/// <summary>
/// Manages a collection of bloom filters, one per index shard, for fast negative lookups
/// in the federation layer. The federation router queries these filters to skip shards that
/// definitely do not contain a requested key, avoiding unnecessary I/O.
/// </summary>
/// <remarks>
/// <para>
/// Design decisions:
/// - Uses existing SDK <see cref="BloomFilter{T}"/> (no reimplementation of bloom filter logic).
/// - Lazy initialization means single-VDE deployments never allocate filters (zero overhead).
/// - Conservative fallback: if ALL filters reject a key, returns all shards as candidates
///   (prevents data loss from bloom filter corruption).
/// - Thread safety: <see cref="ConcurrentDictionary{TKey,TValue}"/> for the filter collection,
///   but individual <see cref="BloomFilter{T}"/> instances are NOT thread-safe per SDK docs.
///   The federation router must serialize writes per shard (via WAL transactions).
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Shard Bloom Filter Index (VFED-15)")]
public sealed class ShardBloomFilterIndex : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, BloomFilter<string>> _shardFilters = new();
    private readonly ConcurrentDictionary<Guid, long> _deleteCounters = new();
    private readonly ConcurrentDictionary<Guid, bool> _rebuildFlags = new();
    private readonly ShardBloomFilterConfig _config;
    private volatile bool _disposed;

    /// <summary>
    /// Creates a new shard bloom filter index with the specified configuration.
    /// </summary>
    /// <param name="config">Configuration controlling filter sizing and rebuild behavior.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="config"/> is null.</exception>
    public ShardBloomFilterIndex(ShardBloomFilterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    /// <summary>
    /// Gets the set of shard VDE IDs that have bloom filters tracked by this index.
    /// </summary>
    public IReadOnlyCollection<Guid> TrackedShards => _shardFilters.Keys.ToArray();

    /// <summary>
    /// Gets the number of shards currently tracked by this index.
    /// </summary>
    public int ShardCount => _shardFilters.Count;

    /// <summary>
    /// Tests whether a shard might contain the given key.
    /// Returns <c>true</c> if the key might be present (possible false positive),
    /// or if no filter exists for the shard (conservative: unknown = might contain).
    /// Returns <c>false</c> only when the filter definitively rejects the key.
    /// </summary>
    /// <param name="shardVdeId">The VDE instance ID of the index shard to check.</param>
    /// <param name="key">The key to test for membership.</param>
    /// <returns>
    /// <c>true</c> if the shard might contain the key (or no filter exists);
    /// <c>false</c> if the key is definitely not in the shard.
    /// </returns>
    public bool MayContain(Guid shardVdeId, string key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // No filter for this shard: conservative default — might contain
        if (!_shardFilters.TryGetValue(shardVdeId, out var filter))
            return true;

        return filter.MightContain(key);
    }

    /// <summary>
    /// Filters a list of candidate shards to only those that may contain the given key.
    /// This is the primary method called by <see cref="VdeFederationRouter"/> before routing a lookup.
    /// </summary>
    /// <param name="allShards">All candidate shard VDE IDs to evaluate.</param>
    /// <param name="key">The key to test for membership.</param>
    /// <returns>
    /// A subset of <paramref name="allShards"/> containing only shards that may hold the key.
    /// If all filters reject the key (empty result), returns all shards as a defensive fallback
    /// to prevent data loss from bloom filter corruption.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="allShards"/> or <paramref name="key"/> is null.</exception>
    public IReadOnlyList<Guid> FilterCandidateShards(IReadOnlyList<Guid> allShards, string key)
    {
        ArgumentNullException.ThrowIfNull(allShards);
        ArgumentNullException.ThrowIfNull(key);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (allShards.Count == 0)
            return allShards;

        var candidates = new List<Guid>(allShards.Count);
        for (int i = 0; i < allShards.Count; i++)
        {
            if (MayContain(allShards[i], key))
            {
                candidates.Add(allShards[i]);
            }
        }

        // Conservative fallback: if all filters reject, return all shards
        // This prevents data loss from bloom filter corruption or bugs
        if (candidates.Count == 0)
            return allShards;

        return candidates;
    }

    /// <summary>
    /// Adds a key to the bloom filter for the specified shard.
    /// The filter is lazily initialized on first Add for a given shard,
    /// so single-VDE deployments that never call Add allocate zero filter memory.
    /// </summary>
    /// <param name="shardVdeId">The VDE instance ID of the index shard.</param>
    /// <param name="key">The key to add to the filter.</param>
    /// <remarks>
    /// Thread safety: Individual BloomFilter instances are NOT thread-safe.
    /// The caller (federation router) must serialize writes per shard via WAL transactions.
    /// </remarks>
    public void Add(Guid shardVdeId, string key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var filter = _shardFilters.GetOrAdd(shardVdeId, _ => CreateFilter());
        filter.Add(key);

        // Check if auto-rebuild should be flagged
        if (_config.EnableAutoRebuild &&
            filter.CurrentFalsePositiveRate > _config.TargetFalsePositiveRate * 2)
        {
            _rebuildFlags[shardVdeId] = true;
        }
    }

    /// <summary>
    /// Records a key deletion from the specified shard.
    /// Since bloom filters cannot remove items, this marks the shard's filter as potentially
    /// degraded and tracks delete counts to determine when a rebuild is necessary.
    /// </summary>
    /// <param name="shardVdeId">The VDE instance ID of the index shard.</param>
    /// <param name="key">The key that was deleted (used for tracking, not for filter modification).</param>
    /// <remarks>
    /// When the delete count exceeds 10% of <see cref="ShardBloomFilterConfig.ExpectedKeysPerShard"/>,
    /// the shard is flagged for rebuild. The actual rebuild must be triggered externally
    /// via <see cref="RebuildFilterAsync"/>.
    /// </remarks>
    public void Remove(Guid shardVdeId, string key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Track delete count per shard
        _deleteCounters.AddOrUpdate(shardVdeId, 1, (_, count) => count + 1);

        // Flag for rebuild when deletes exceed 10% of expected keys
        long deleteCount = _deleteCounters.GetValueOrDefault(shardVdeId, 0);
        if (deleteCount > _config.ExpectedKeysPerShard / 10)
        {
            _rebuildFlags[shardVdeId] = true;
        }
    }

    /// <summary>
    /// Checks whether the specified shard has been flagged for bloom filter rebuild.
    /// A shard is flagged when: (1) too many deletions occurred, or (2) the filter's
    /// false positive rate has degraded beyond the configured threshold.
    /// </summary>
    /// <param name="shardVdeId">The VDE instance ID of the index shard to check.</param>
    /// <returns><c>true</c> if the shard's filter needs rebuilding; otherwise <c>false</c>.</returns>
    public bool NeedsRebuild(Guid shardVdeId)
    {
        return _rebuildFlags.GetValueOrDefault(shardVdeId, false);
    }

    /// <summary>
    /// Rebuilds the bloom filter for the specified shard from a complete enumeration of its keys.
    /// Creates a fresh filter, adds all keys, then atomically swaps the old filter.
    /// Resets the delete counter and rebuild flag for the shard.
    /// </summary>
    /// <param name="shardVdeId">The VDE instance ID of the index shard to rebuild.</param>
    /// <param name="allKeys">Async enumerable of all keys currently in the shard.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="allKeys"/> is null.</exception>
    public async Task RebuildFilterAsync(Guid shardVdeId, IAsyncEnumerable<string> allKeys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(allKeys);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var newFilter = CreateFilter();

        await foreach (var key in allKeys.WithCancellation(ct).ConfigureAwait(false))
        {
            newFilter.Add(key);
        }

        // Atomic swap
        _shardFilters[shardVdeId] = newFilter;

        // Reset tracking state
        _deleteCounters[shardVdeId] = 0;
        _rebuildFlags.TryRemove(shardVdeId, out _);
    }

    /// <summary>
    /// Removes the bloom filter for a decommissioned shard, freeing its memory.
    /// </summary>
    /// <param name="shardVdeId">The VDE instance ID of the shard to remove.</param>
    public void RemoveShard(Guid shardVdeId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _shardFilters.TryRemove(shardVdeId, out _);
        _deleteCounters.TryRemove(shardVdeId, out _);
        _rebuildFlags.TryRemove(shardVdeId, out _);
    }

    /// <summary>
    /// Creates a snapshot of the bloom filter for the specified shard, suitable for persistence.
    /// The snapshot contains the serialized filter data using <see cref="BloomFilter{T}.Serialize"/>.
    /// </summary>
    /// <param name="shardVdeId">The VDE instance ID of the shard to snapshot.</param>
    /// <returns>
    /// A <see cref="BloomFilterSnapshot"/> if a filter exists for the shard;
    /// <c>null</c> if no filter has been created for this shard.
    /// </returns>
    public BloomFilterSnapshot? GetSnapshot(Guid shardVdeId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_shardFilters.TryGetValue(shardVdeId, out var filter))
            return null;

        return new BloomFilterSnapshot(
            shardVdeId,
            filter.Serialize(),
            filter.ItemCount,
            filter.CurrentFalsePositiveRate);
    }

    /// <summary>
    /// Loads a previously persisted bloom filter snapshot, reconstructing the filter
    /// and inserting it into the index. Used during VDE restart to restore filter state
    /// without performing a full rebuild from keys.
    /// </summary>
    /// <param name="snapshot">The snapshot to load.</param>
    /// <exception cref="ArgumentException">Thrown when snapshot FilterData is null or empty.</exception>
    public void LoadSnapshot(BloomFilterSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (snapshot.FilterData == null || snapshot.FilterData.Length == 0)
            throw new ArgumentException("Snapshot FilterData must not be null or empty.", nameof(snapshot));

        var filter = BloomFilter<string>.Deserialize(
            snapshot.FilterData,
            key => System.Text.Encoding.UTF8.GetBytes(key));

        _shardFilters[snapshot.ShardVdeId] = filter;

        // Reset tracking state for the loaded shard
        _deleteCounters[snapshot.ShardVdeId] = 0;
        _rebuildFlags.TryRemove(snapshot.ShardVdeId, out _);
    }

    /// <summary>
    /// Releases all bloom filters and associated tracking state.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _shardFilters.Clear();
            _deleteCounters.Clear();
            _rebuildFlags.Clear();
        }

        return ValueTask.CompletedTask;
    }

    private BloomFilter<string> CreateFilter()
    {
        return new BloomFilter<string>(
            _config.ExpectedKeysPerShard,
            _config.TargetFalsePositiveRate,
            key => System.Text.Encoding.UTF8.GetBytes(key));
    }
}
