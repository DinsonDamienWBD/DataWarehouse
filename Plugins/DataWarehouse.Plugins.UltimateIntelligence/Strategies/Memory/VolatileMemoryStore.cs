using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory;

/// <summary>
/// Statistics for a volatile memory store.
/// </summary>
public sealed record VolatileStoreStatistics
{
    public long Hits { get; init; }
    public long Misses { get; init; }
    public long PromotionsIn { get; init; }
    public long EvictionsOut { get; init; }
}

/// <summary>
/// RAM-based volatile storage for tiered memory system.
/// Implements LRU eviction, automatic TTL expiration, and semantic vector indexing.
/// Thread-safe for concurrent access patterns.
/// </summary>
public sealed class VolatileMemoryStore
{
    private readonly BoundedDictionary<string, TieredMemoryEntry> _entries = new BoundedDictionary<string, TieredMemoryEntry>(1000);
    private readonly BoundedDictionary<string, LinkedListNode<string>> _lruNodes = new BoundedDictionary<string, LinkedListNode<string>>(1000);
    private readonly LinkedList<string> _lruOrder = new();
    private readonly object _lruLock = new();
    private readonly TierConfig _config;

    // Statistics
    private long _hits;
    private long _misses;
    private long _promotionsIn;
    private long _evictionsOut;
    private long _bytesUsed;

    // Semantic index for fast similarity search
    private readonly BoundedDictionary<string, List<(string EntryId, float[] Vector)>> _scopeVectorIndex = new BoundedDictionary<string, List<(string EntryId, float[] Vector)>>(1000);

    public VolatileMemoryStore(TierConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Stores an entry in the volatile store.
    /// </summary>
    public Task StoreAsync(TieredMemoryEntry entry, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var entrySize = entry.EstimatedSizeBytes;

        // Check capacity
        if (Interlocked.Read(ref _bytesUsed) + entrySize > _config.MaxCapacityBytes)
        {
            // Evict LRU entries to make room
            EvictLruSync(entrySize);
        }

        _entries[entry.Id] = entry;
        Interlocked.Add(ref _bytesUsed, entrySize);

        // Update LRU
        lock (_lruLock)
        {
            if (_lruNodes.TryGetValue(entry.Id, out var existingNode))
            {
                _lruOrder.Remove(existingNode);
            }
            var node = _lruOrder.AddLast(entry.Id);
            _lruNodes[entry.Id] = node;
        }

        // Update semantic index if embedding present
        if (entry.Embedding != null && entry.Embedding.Length > 0)
        {
            var scopeIndex = _scopeVectorIndex.GetOrAdd(entry.Scope, _ => new List<(string, float[])>());
            lock (scopeIndex)
            {
                scopeIndex.Add((entry.Id, entry.Embedding));
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Retrieves an entry by ID.
    /// </summary>
    public Task<TieredMemoryEntry?> GetAsync(string entryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (_entries.TryGetValue(entryId, out var entry))
        {
            // Check TTL
            if (_config.TTL.HasValue &&
                DateTime.UtcNow - entry.CreatedAt > _config.TTL.Value)
            {
                // Expired - remove and return null
                DeleteSync(entryId);
                Interlocked.Increment(ref _misses);
                return Task.FromResult<TieredMemoryEntry?>(null);
            }

            // Update LRU position and access stats atomically under the same lock.
            // Finding 3197/3198: entry.AccessCount++ is a non-atomic RMW on a shared record
            // field; guard it alongside the LRU update.
            lock (_lruLock)
            {
                if (_lruNodes.TryGetValue(entryId, out var node))
                {
                    _lruOrder.Remove(node);
                    var newNode = _lruOrder.AddLast(entryId);
                    _lruNodes[entryId] = newNode;
                }

                entry.AccessCount++;
                entry.LastAccessedAt = DateTime.UtcNow;
            }

            Interlocked.Increment(ref _hits);
            return Task.FromResult<TieredMemoryEntry?>(entry);
        }

        Interlocked.Increment(ref _misses);
        return Task.FromResult<TieredMemoryEntry?>(null);
    }

    /// <summary>
    /// Deletes an entry by ID.
    /// </summary>
    public Task<bool> DeleteAsync(string entryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(DeleteSync(entryId));
    }

    private bool DeleteSync(string entryId)
    {
        if (_entries.TryRemove(entryId, out var entry))
        {
            var entrySize = entry.EstimatedSizeBytes;
            Interlocked.Add(ref _bytesUsed, -entrySize);

            // Remove from LRU
            lock (_lruLock)
            {
                if (_lruNodes.TryRemove(entryId, out var node))
                {
                    _lruOrder.Remove(node);
                }
            }

            // Remove from semantic index
            if (_scopeVectorIndex.TryGetValue(entry.Scope, out var scopeIndex))
            {
                lock (scopeIndex)
                {
                    scopeIndex.RemoveAll(x => x.EntryId == entryId);
                }
            }

            return true;
        }
        return false;
    }

    /// <summary>
    /// Searches for entries similar to the query vector within a scope.
    /// </summary>
    public Task<IEnumerable<TieredMemoryEntry>> SearchAsync(float[] queryVector, string scope, int topK, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var results = new List<(TieredMemoryEntry Entry, float Score)>();

        // Search in scope-specific index
        if (_scopeVectorIndex.TryGetValue(scope, out var scopeIndex))
        {
            List<(string EntryId, float[] Vector)> snapshot;
            lock (scopeIndex)
            {
                snapshot = scopeIndex.ToList();
            }

            foreach (var (entryId, vector) in snapshot)
            {
                if (_entries.TryGetValue(entryId, out var entry))
                {
                    // Check TTL
                    if (_config.TTL.HasValue &&
                        DateTime.UtcNow - entry.CreatedAt > _config.TTL.Value)
                    {
                        continue; // Skip expired
                    }

                    var similarity = CosineSimilarity(queryVector, vector);
                    results.Add((entry, similarity));
                }
            }
        }

        // Also search global scope if different
        if (scope != "global" && _scopeVectorIndex.TryGetValue("global", out var globalIndex))
        {
            List<(string EntryId, float[] Vector)> snapshot;
            lock (globalIndex)
            {
                snapshot = globalIndex.ToList();
            }

            foreach (var (entryId, vector) in snapshot)
            {
                if (_entries.TryGetValue(entryId, out var entry))
                {
                    if (_config.TTL.HasValue &&
                        DateTime.UtcNow - entry.CreatedAt > _config.TTL.Value)
                    {
                        continue;
                    }

                    var similarity = CosineSimilarity(queryVector, vector);
                    results.Add((entry, similarity));
                }
            }
        }

        var topResults = results
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .Select(r => r.Entry)
            .ToList();

        return Task.FromResult<IEnumerable<TieredMemoryEntry>>(topResults);
    }

    /// <summary>
    /// Expires entries created before the cutoff time.
    /// </summary>
    public Task ExpireBeforeAsync(DateTime cutoff, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var expiredIds = _entries
            .Where(kvp => kvp.Value.CreatedAt < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var id in expiredIds)
        {
            DeleteSync(id);
            Interlocked.Increment(ref _evictionsOut);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Evicts LRU entries until the specified bytes are freed.
    /// </summary>
    public Task EvictLruAsync(long bytesToFree, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EvictLruSync(bytesToFree);
        return Task.CompletedTask;
    }

    private void EvictLruSync(long bytesToFree)
    {
        long freed = 0;

        while (freed < bytesToFree)
        {
            string? lruEntryId = null;

            lock (_lruLock)
            {
                var firstNode = _lruOrder.First;
                if (firstNode == null)
                    break;

                lruEntryId = firstNode.Value;
            }

            if (lruEntryId != null && _entries.TryGetValue(lruEntryId, out var entry))
            {
                freed += entry.EstimatedSizeBytes;
                DeleteSync(lruEntryId);
                Interlocked.Increment(ref _evictionsOut);
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>
    /// Consolidates similar entries by merging duplicates.
    /// </summary>
    public Task ConsolidateSimilarAsync(float similarityThreshold, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var entries = _entries.Values.ToList();
        var toRemove = new HashSet<string>();

        for (int i = 0; i < entries.Count; i++)
        {
            if (toRemove.Contains(entries[i].Id))
                continue;

            for (int j = i + 1; j < entries.Count; j++)
            {
                if (toRemove.Contains(entries[j].Id))
                    continue;

                // Check if same scope and similar vectors
                var embeddingI = entries[i].Embedding;
                var embeddingJ = entries[j].Embedding;
                if (entries[i].Scope == entries[j].Scope &&
                    embeddingI != null &&
                    embeddingJ != null &&
                    embeddingI.Length > 0 &&
                    embeddingJ.Length > 0)
                {
                    var similarity = CosineSimilarity(embeddingI, embeddingJ);
                    if (similarity >= similarityThreshold)
                    {
                        // Keep the one with higher importance/access count
                        if (entries[i].ImportanceScore > entries[j].ImportanceScore ||
                            (entries[i].ImportanceScore == entries[j].ImportanceScore &&
                             entries[i].AccessCount >= entries[j].AccessCount))
                        {
                            // Merge j into i
                            entries[i].AccessCount += entries[j].AccessCount;
                            toRemove.Add(entries[j].Id);
                        }
                        else
                        {
                            // Merge i into j
                            entries[j].AccessCount += entries[i].AccessCount;
                            toRemove.Add(entries[i].Id);
                            break; // entries[i] is now merged, stop comparing it
                        }
                    }
                }
            }
        }

        foreach (var id in toRemove)
        {
            DeleteSync(id);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the total count of entries.
    /// </summary>
    public Task<long> CountAsync(CancellationToken ct = default)
    {
        return Task.FromResult((long)_entries.Count);
    }

    /// <summary>
    /// Gets all entries as an enumerable.
    /// </summary>
    public IEnumerable<TieredMemoryEntry> GetAllEntries()
    {
        return _entries.Values;
    }

    /// <summary>
    /// Gets the total bytes used by stored entries.
    /// </summary>
    public Task<long> GetBytesUsedAsync(CancellationToken ct = default)
    {
        return Task.FromResult(Interlocked.Read(ref _bytesUsed));
    }

    /// <summary>
    /// Gets store statistics.
    /// </summary>
    public Task<VolatileStoreStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new VolatileStoreStatistics
        {
            Hits = Interlocked.Read(ref _hits),
            Misses = Interlocked.Read(ref _misses),
            PromotionsIn = Interlocked.Read(ref _promotionsIn),
            EvictionsOut = Interlocked.Read(ref _evictionsOut)
        });
    }

    /// <summary>
    /// Records a promotion into this store.
    /// </summary>
    public void RecordPromotionIn()
    {
        Interlocked.Increment(ref _promotionsIn);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 0f;

        float dot = 0, magA = 0, magB = 0;

        // SIMD-friendly loop
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        var magnitude = MathF.Sqrt(magA) * MathF.Sqrt(magB);
        return magnitude > 0 ? dot / magnitude : 0f;
    }
}

/// <summary>
/// Thread-safe LRU cache implementation for memory entries.
/// Optimized for high-concurrency scenarios with minimal lock contention.
/// </summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly BoundedDictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _cache = new BoundedDictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>>(1000);
    private readonly LinkedList<(TKey Key, TValue Value)> _order = new();
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    public LruCache(int capacity)
    {
        _capacity = capacity;
    }

    public bool TryGet(TKey key, out TValue? value)
    {
        if (_cache.TryGetValue(key, out var node))
        {
            _lock.EnterWriteLock();
            try
            {
                _order.Remove(node);
                _order.AddLast(node);
            }
            finally
            {
                _lock.ExitWriteLock();
            }

            value = node.Value.Value;
            return true;
        }

        value = default;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_cache.TryGetValue(key, out var existingNode))
            {
                _order.Remove(existingNode);
                _cache.TryRemove(key, out _);
            }
            else if (_cache.Count >= _capacity)
            {
                // Evict LRU
                var lru = _order.First;
                if (lru != null)
                {
                    _order.RemoveFirst();
                    _cache.TryRemove(lru.Value.Key, out _);
                }
            }

            var newNode = new LinkedListNode<(TKey Key, TValue Value)>((key, value));
            _order.AddLast(newNode);
            _cache[key] = newNode;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public bool Remove(TKey key)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_cache.TryRemove(key, out var node))
            {
                _order.Remove(node);
                return true;
            }
            return false;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _cache.Count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    public void Clear()
    {
        _lock.EnterWriteLock();
        try
        {
            _cache.Clear();
            _order.Clear();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
}

/// <summary>
/// High-performance vector index for semantic similarity search.
/// Uses bucketed spatial hashing for approximate nearest neighbor queries.
/// </summary>
public sealed class SemanticVectorIndex
{
    private readonly BoundedDictionary<string, ConcurrentBag<(string Id, float[] Vector)>> _buckets = new BoundedDictionary<string, ConcurrentBag<(string Id, float[] Vector)>>(1000);
    private readonly int _numBuckets;
    private readonly float[] _randomProjection;

    public SemanticVectorIndex(int dimensions, int numBuckets = 256)
    {
        _numBuckets = numBuckets;
        _randomProjection = GenerateRandomProjection(dimensions);
    }

    public void Add(string id, float[] vector)
    {
        var bucket = GetBucket(vector);
        var bag = _buckets.GetOrAdd(bucket, _ => new ConcurrentBag<(string, float[])>());
        bag.Add((id, vector));
    }

    public IEnumerable<(string Id, float Similarity)> Search(float[] query, int topK)
    {
        var bucket = GetBucket(query);
        var results = new List<(string Id, float Similarity)>();

        // Search primary bucket
        if (_buckets.TryGetValue(bucket, out var primaryBag))
        {
            foreach (var (id, vector) in primaryBag)
            {
                var similarity = CosineSimilarity(query, vector);
                results.Add((id, similarity));
            }
        }

        // Search adjacent buckets for better recall
        var adjacentBuckets = GetAdjacentBuckets(bucket);
        foreach (var adjBucket in adjacentBuckets)
        {
            if (_buckets.TryGetValue(adjBucket, out var adjBag))
            {
                foreach (var (id, vector) in adjBag)
                {
                    var similarity = CosineSimilarity(query, vector);
                    results.Add((id, similarity));
                }
            }
        }

        return results
            .OrderByDescending(r => r.Similarity)
            .Take(topK);
    }

    public bool Remove(string id)
    {
        // Note: ConcurrentBag doesn't support efficient removal
        // In production, would use a different data structure
        foreach (var kvp in _buckets)
        {
            var items = kvp.Value.ToList();
            var newItems = items.Where(x => x.Id != id).ToList();
            if (items.Count != newItems.Count)
            {
                _buckets[kvp.Key] = new ConcurrentBag<(string, float[])>(newItems);
                return true;
            }
        }
        return false;
    }

    private string GetBucket(float[] vector)
    {
        // Simple LSH-style bucketing using random projection
        float projection = 0;
        var len = Math.Min(vector.Length, _randomProjection.Length);
        for (int i = 0; i < len; i++)
        {
            projection += vector[i] * _randomProjection[i];
        }

        var bucketIndex = (int)((projection + 1000) / 2000 * _numBuckets) % _numBuckets;
        return bucketIndex.ToString();
    }

    private IEnumerable<string> GetAdjacentBuckets(string bucket)
    {
        if (int.TryParse(bucket, out var index))
        {
            yield return ((index - 1 + _numBuckets) % _numBuckets).ToString();
            yield return ((index + 1) % _numBuckets).ToString();
        }
    }

    private static float[] GenerateRandomProjection(int dimensions)
    {
        var rng = new Random(42); // Fixed seed for reproducibility
        var projection = new float[dimensions];
        for (int i = 0; i < dimensions; i++)
        {
            projection[i] = (float)(rng.NextDouble() * 2 - 1);
        }
        return projection;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 0f;

        float dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        var magnitude = MathF.Sqrt(magA) * MathF.Sqrt(magB);
        return magnitude > 0 ? dot / magnitude : 0f;
    }
}

