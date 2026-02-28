using System.Collections.Concurrent;
using System.Diagnostics;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Tags;

/// <summary>
/// In-memory inverted tag index with sharding for efficient tag-to-object lookups at scale.
/// Designed for up to 1B objects with bounded per-shard memory via configurable shard count.
/// </summary>
/// <remarks>
/// <para>
/// The index maintains an inverted mapping from <see cref="TagKey"/> to sets of object keys,
/// distributed across shards determined by the object key's hash code. This provides:
/// </para>
/// <list type="bullet">
/// <item><description>O(1) amortized index and remove operations</description></item>
/// <item><description>Concurrent read access via <see cref="ConcurrentDictionary{TKey,TValue}"/></description></item>
/// <item><description>Write safety via per-shard <see cref="ReaderWriterLockSlim"/></description></item>
/// <item><description>Bounded per-shard memory for predictable resource usage</description></item>
/// </list>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Inverted tag index")]
public sealed class InvertedTagIndex : ITagIndex, IDisposable
{
    private readonly int _shardCount;

    /// <summary>
    /// Per-shard inverted index: TagKey -> set of object keys belonging to that shard.
    /// </summary>
    private readonly BoundedDictionary<TagKey, HashSet<string>>[] _shards;

    /// <summary>
    /// Per-shard tag values: (TagKey, ObjectKey) -> TagValue for value-based filtering.
    /// </summary>
    private readonly BoundedDictionary<(TagKey TagKey, string ObjectKey), TagValue>[] _valueShards;

    /// <summary>
    /// Per-shard reader-writer locks for write operations on the HashSet (which is not thread-safe).
    /// </summary>
    private readonly ReaderWriterLockSlim[] _locks;

    private int _disposed; // 0=not disposed, 1=disposed (atomic via Interlocked)

    /// <summary>
    /// Initializes a new <see cref="InvertedTagIndex"/> with the specified shard count.
    /// </summary>
    /// <param name="shardCount">
    /// Number of shards for distributing object keys. Higher values reduce contention
    /// but increase memory overhead. Default is 256, suitable for up to 1B objects.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="shardCount"/> is less than 1.</exception>
    public InvertedTagIndex(int shardCount = 256)
    {
        if (shardCount < 1)
            throw new ArgumentOutOfRangeException(nameof(shardCount), shardCount, "Shard count must be at least 1.");

        _shardCount = shardCount;
        _shards = new BoundedDictionary<TagKey, HashSet<string>>[shardCount];
        _valueShards = new BoundedDictionary<(TagKey, string), TagValue>[shardCount];
        _locks = new ReaderWriterLockSlim[shardCount];

        for (int i = 0; i < shardCount; i++)
        {
            _shards[i] = new BoundedDictionary<TagKey, HashSet<string>>(1000);
            _valueShards[i] = new BoundedDictionary<(TagKey, string), TagValue>(1000);
            _locks[i] = new ReaderWriterLockSlim();
        }
    }

    /// <summary>
    /// Gets the total number of shards in this index.
    /// </summary>
    public int ShardCount => _shardCount;

    /// <inheritdoc />
    public Task IndexAsync(string objectKey, Tag tag, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentNullException.ThrowIfNull(tag);
        ct.ThrowIfCancellationRequested();

        var shard = GetShard(objectKey);
        var shardDict = _shards[shard];
        var valueDict = _valueShards[shard];
        var rwLock = _locks[shard];

        rwLock.EnterWriteLock();
        try
        {
            if (!shardDict.TryGetValue(tag.Key, out var objectKeys))
            {
                objectKeys = new HashSet<string>(StringComparer.Ordinal);
                shardDict[tag.Key] = objectKeys;
            }

            objectKeys.Add(objectKey);
            valueDict[(tag.Key, objectKey)] = tag.Value;
        }
        finally
        {
            rwLock.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string objectKey, TagKey tagKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentNullException.ThrowIfNull(tagKey);
        ct.ThrowIfCancellationRequested();

        var shard = GetShard(objectKey);
        var shardDict = _shards[shard];
        var valueDict = _valueShards[shard];
        var rwLock = _locks[shard];

        rwLock.EnterWriteLock();
        try
        {
            if (shardDict.TryGetValue(tagKey, out var objectKeys))
            {
                objectKeys.Remove(objectKey);

                // Clean up empty entries to prevent memory leaks
                if (objectKeys.Count == 0)
                {
                    shardDict.TryRemove(tagKey, out _);
                }
            }

            valueDict.TryRemove((tagKey, objectKey), out _);
        }
        finally
        {
            rwLock.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<TagIndexResult> QueryAsync(TagIndexQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Skip < 0)
            throw new ArgumentOutOfRangeException(nameof(query), "Skip must be non-negative.");
        // Guard against pathological Skip values that would silently return empty results
        // while TotalCount reports a large number (misleading pagination).
        const int MaxAllowedSkip = 10_000_000; // 10M rows is the practical ceiling
        if (query.Skip > MaxAllowedSkip)
            throw new ArgumentOutOfRangeException(nameof(query), $"Skip ({query.Skip}) exceeds the maximum allowed value ({MaxAllowedSkip}).");

        var sw = Stopwatch.StartNew();

        if (query.Filters.Count == 0)
        {
            sw.Stop();
            return Task.FromResult(new TagIndexResult
            {
                ObjectKeys = Array.Empty<string>(),
                TotalCount = 0,
                QueryDuration = sw.Elapsed
            });
        }

        // Process first filter to get initial candidate set
        HashSet<string>? candidates = null;

        foreach (var filter in query.Filters)
        {
            ct.ThrowIfCancellationRequested();
            var filterResults = EvaluateFilter(filter);

            if (candidates is null)
            {
                candidates = filterResults;
            }
            else
            {
                // AND semantics: intersect with previous results
                candidates.IntersectWith(filterResults);
            }

            // Short-circuit if no candidates remain
            if (candidates.Count == 0)
                break;
        }

        candidates ??= new HashSet<string>(StringComparer.Ordinal);

        // Apply ObjectKeyPrefix filter
        if (!string.IsNullOrEmpty(query.ObjectKeyPrefix))
        {
            candidates.RemoveWhere(key =>
                !key.StartsWith(query.ObjectKeyPrefix, StringComparison.Ordinal));
        }

        var totalCount = candidates.Count;
        var effectiveTake = query.EffectiveTake;

        IReadOnlyList<string> resultKeys;
        if (query.CountOnly)
        {
            resultKeys = Array.Empty<string>();
        }
        else
        {
            resultKeys = candidates
                .OrderBy(k => k, StringComparer.Ordinal)
                .Skip(query.Skip)
                .Take(effectiveTake)
                .ToList();
        }

        sw.Stop();
        return Task.FromResult(new TagIndexResult
        {
            ObjectKeys = resultKeys,
            TotalCount = totalCount,
            QueryDuration = sw.Elapsed
        });
    }

    /// <inheritdoc />
    public Task<long> CountByTagAsync(TagKey tagKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tagKey);
        ct.ThrowIfCancellationRequested();

        long count = 0;
        for (int i = 0; i < _shardCount; i++)
        {
            var rwLock = _locks[i];
            rwLock.EnterReadLock();
            try
            {
                if (_shards[i].TryGetValue(tagKey, out var objectKeys))
                {
                    count += objectKeys.Count;
                }
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }

        return Task.FromResult(count);
    }

    /// <inheritdoc />
    public async Task RebuildAsync(
        IAsyncEnumerable<(string ObjectKey, TagCollection Tags)> source,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Clear all shards
        for (int i = 0; i < _shardCount; i++)
        {
            var rwLock = _locks[i];
            rwLock.EnterWriteLock();
            try
            {
                _shards[i].Clear();
                _valueShards[i].Clear();
            }
            finally
            {
                rwLock.ExitWriteLock();
            }
        }

        // Re-index from source
        await foreach (var (objectKey, tags) in source.WithCancellation(ct))
        {
            foreach (var tag in tags)
            {
                await IndexAsync(objectKey, tag, ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<TagKey, long>> GetTagDistributionAsync(
        int topN = 50,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var distribution = new Dictionary<TagKey, long>();

        for (int i = 0; i < _shardCount; i++)
        {
            var rwLock = _locks[i];
            rwLock.EnterReadLock();
            try
            {
                foreach (var kvp in _shards[i])
                {
                    if (!distribution.TryGetValue(kvp.Key, out var count))
                        count = 0;
                    distribution[kvp.Key] = count + kvp.Value.Count;
                }
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }

        var topEntries = distribution
            .OrderByDescending(kvp => kvp.Value)
            .Take(topN)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        return Task.FromResult<IReadOnlyDictionary<TagKey, long>>(topEntries);
    }

    /// <summary>
    /// Returns the approximate number of entries across all shards.
    /// Useful for monitoring memory usage.
    /// </summary>
    public long GetApproximateEntryCount()
    {
        long count = 0;
        for (int i = 0; i < _shardCount; i++)
        {
            var rwLock = _locks[i];
            rwLock.EnterReadLock();
            try
            {
                foreach (var kvp in _shards[i])
                {
                    count += kvp.Value.Count;
                }
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }
        return count;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;

        for (int i = 0; i < _shardCount; i++)
        {
            _locks[i].Dispose();
        }
    }

    // ── Private helpers ───────────────────────────────────────────────

    /// <summary>
    /// Computes the shard index for a given object key using a positive hash modulo.
    /// </summary>
    private int GetShard(string objectKey)
        => (objectKey.GetHashCode() & 0x7FFFFFFF) % _shardCount;

    /// <summary>
    /// Evaluates a single filter across all shards and returns the set of matching object keys.
    /// </summary>
    private HashSet<string> EvaluateFilter(TagFilter filter)
    {
        var results = new HashSet<string>(StringComparer.Ordinal);

        switch (filter.Operator)
        {
            case TagFilterOperator.Exists:
                CollectObjectKeysForTag(filter.TagKey, results);
                break;

            case TagFilterOperator.NotExists:
                CollectAllObjectKeys(results);
                RemoveObjectKeysForTag(filter.TagKey, results);
                break;

            case TagFilterOperator.Equals:
                CollectObjectKeysMatchingValue(filter.TagKey, v => ValuesEqual(v, filter.Value), results);
                break;

            case TagFilterOperator.NotEquals:
                CollectObjectKeysMatchingValue(filter.TagKey, v => !ValuesEqual(v, filter.Value), results);
                break;

            case TagFilterOperator.Contains:
                CollectObjectKeysMatchingValue(filter.TagKey, v =>
                    v is StringTagValue sv && filter.Value is StringTagValue fv &&
                    sv.Value.Contains(fv.Value, StringComparison.Ordinal), results);
                break;

            case TagFilterOperator.StartsWith:
                CollectObjectKeysMatchingValue(filter.TagKey, v =>
                    v is StringTagValue sv && filter.Value is StringTagValue fv &&
                    sv.Value.StartsWith(fv.Value, StringComparison.Ordinal), results);
                break;

            case TagFilterOperator.In:
                if (filter.Values is not null)
                {
                    CollectObjectKeysMatchingValue(filter.TagKey, v =>
                        filter.Values.Contains(v), results);
                }
                break;

            case TagFilterOperator.Between:
                CollectObjectKeysMatchingValue(filter.TagKey, v =>
                    CompareValues(v, filter.Value) >= 0 &&
                    CompareValues(v, filter.UpperBound) <= 0, results);
                break;

            case TagFilterOperator.GreaterThan:
                CollectObjectKeysMatchingValue(filter.TagKey, v =>
                    CompareValues(v, filter.Value) > 0, results);
                break;

            case TagFilterOperator.LessThan:
                CollectObjectKeysMatchingValue(filter.TagKey, v =>
                    CompareValues(v, filter.Value) < 0, results);
                break;

            case TagFilterOperator.GreaterOrEqual:
                CollectObjectKeysMatchingValue(filter.TagKey, v =>
                    CompareValues(v, filter.Value) >= 0, results);
                break;

            case TagFilterOperator.LessOrEqual:
                CollectObjectKeysMatchingValue(filter.TagKey, v =>
                    CompareValues(v, filter.Value) <= 0, results);
                break;
        }

        return results;
    }

    /// <summary>
    /// Collects all object keys that have the specified tag key across all shards.
    /// </summary>
    private void CollectObjectKeysForTag(TagKey tagKey, HashSet<string> results)
    {
        for (int i = 0; i < _shardCount; i++)
        {
            var rwLock = _locks[i];
            rwLock.EnterReadLock();
            try
            {
                if (_shards[i].TryGetValue(tagKey, out var objectKeys))
                {
                    foreach (var key in objectKeys)
                        results.Add(key);
                }
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }
    }

    /// <summary>
    /// Collects all object keys across all shards (used for NotExists).
    /// </summary>
    private void CollectAllObjectKeys(HashSet<string> results)
    {
        for (int i = 0; i < _shardCount; i++)
        {
            var rwLock = _locks[i];
            rwLock.EnterReadLock();
            try
            {
                foreach (var kvp in _shards[i])
                {
                    foreach (var key in kvp.Value)
                        results.Add(key);
                }
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }
    }

    /// <summary>
    /// Removes all object keys that have the specified tag key from the result set.
    /// </summary>
    private void RemoveObjectKeysForTag(TagKey tagKey, HashSet<string> results)
    {
        for (int i = 0; i < _shardCount; i++)
        {
            var rwLock = _locks[i];
            rwLock.EnterReadLock();
            try
            {
                if (_shards[i].TryGetValue(tagKey, out var objectKeys))
                {
                    foreach (var key in objectKeys)
                        results.Remove(key);
                }
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }
    }

    /// <summary>
    /// Collects object keys for a tag key where the stored value matches the predicate.
    /// </summary>
    private void CollectObjectKeysMatchingValue(
        TagKey tagKey,
        Func<TagValue, bool> predicate,
        HashSet<string> results)
    {
        for (int i = 0; i < _shardCount; i++)
        {
            var rwLock = _locks[i];
            rwLock.EnterReadLock();
            try
            {
                if (_shards[i].TryGetValue(tagKey, out var objectKeys))
                {
                    foreach (var objectKey in objectKeys)
                    {
                        if (_valueShards[i].TryGetValue((tagKey, objectKey), out var value) &&
                            predicate(value))
                        {
                            results.Add(objectKey);
                        }
                    }
                }
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }
    }

    /// <summary>
    /// Compares two tag values for equality using value semantics.
    /// </summary>
    private static bool ValuesEqual(TagValue? a, TagValue? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.Equals(b);
    }

    /// <summary>
    /// Compares two tag values for ordering. Supports numeric and string comparisons.
    /// Returns negative if a &lt; b, zero if equal, positive if a &gt; b.
    /// Returns 0 for incomparable types.
    /// </summary>
    private static int CompareValues(TagValue? a, TagValue? b)
    {
        if (a is null || b is null) return 0;

        return (a, b) switch
        {
            (NumberTagValue na, NumberTagValue nb) => na.Value.CompareTo(nb.Value),
            (StringTagValue sa, StringTagValue sb) => string.Compare(sa.Value, sb.Value, StringComparison.Ordinal),
            (BoolTagValue ba, BoolTagValue bb) => ba.Value.CompareTo(bb.Value),
            _ => 0
        };
    }
}
