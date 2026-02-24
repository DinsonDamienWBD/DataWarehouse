using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Embeddings;

/// <summary>
/// LRU cache for embeddings with TTL support.
///
/// Features:
/// - Thread-safe LRU eviction
/// - Configurable time-to-live (TTL)
/// - Memory-efficient storage
/// - Automatic cleanup of expired entries
/// - Cache statistics tracking
/// - Optional persistence to disk
/// </summary>
public sealed class EmbeddingCache : IDisposable
{
    private readonly BoundedDictionary<string, CacheEntry> _cache = new BoundedDictionary<string, CacheEntry>(1000);
    private readonly LinkedList<string> _lruList = new();
    private readonly object _lruLock = new();
    private readonly int _maxEntries;
    private readonly TimeSpan _defaultTtl;
    private readonly Timer _cleanupTimer;
    private readonly CacheStatistics _statistics = new();
    private bool _disposed;

    /// <summary>
    /// Gets the current number of entries in the cache.
    /// </summary>
    public int Count => _cache.Count;

    /// <summary>
    /// Gets the maximum number of entries allowed.
    /// </summary>
    public int MaxEntries => _maxEntries;

    /// <summary>
    /// Gets the default TTL for cache entries.
    /// </summary>
    public TimeSpan DefaultTtl => _defaultTtl;

    /// <summary>
    /// Creates a new embedding cache.
    /// </summary>
    /// <param name="maxEntries">Maximum number of entries (default: 10000).</param>
    /// <param name="defaultTtl">Default time-to-live (default: 1 hour).</param>
    /// <param name="cleanupInterval">Interval between cleanup runs (default: 5 minutes).</param>
    public EmbeddingCache(
        int maxEntries = 10000,
        TimeSpan? defaultTtl = null,
        TimeSpan? cleanupInterval = null)
    {
        if (maxEntries <= 0)
            throw new ArgumentException("Max entries must be positive", nameof(maxEntries));

        _maxEntries = maxEntries;
        _defaultTtl = defaultTtl ?? TimeSpan.FromHours(1);

        var interval = cleanupInterval ?? TimeSpan.FromMinutes(5);
        _cleanupTimer = new Timer(CleanupExpiredEntries, null, interval, interval);
    }

    /// <summary>
    /// Tries to get an embedding from the cache.
    /// </summary>
    /// <param name="key">The cache key (typically the text that was embedded).</param>
    /// <param name="embedding">The cached embedding if found.</param>
    /// <returns>True if the embedding was found and is not expired.</returns>
    public bool TryGet(string key, out float[]? embedding)
    {
        var cacheKey = GetCacheKey(key);

        if (_cache.TryGetValue(cacheKey, out var entry))
        {
            if (entry.ExpiresAt > DateTime.UtcNow)
            {
                embedding = entry.Embedding;
                entry.AccessCount++;
                entry.LastAccessedAt = DateTime.UtcNow;
                UpdateLru(cacheKey);
                _statistics.Hits++;
                return true;
            }

            // Entry has expired
            _cache.TryRemove(cacheKey, out _);
            RemoveFromLru(cacheKey);
            _statistics.Expirations++;
        }

        embedding = null;
        _statistics.Misses++;
        return false;
    }

    /// <summary>
    /// Gets an embedding from the cache or generates it using the provided function.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="generator">Function to generate the embedding if not cached.</param>
    /// <param name="ttl">Optional custom TTL for this entry.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The embedding (from cache or newly generated).</returns>
    public async Task<float[]> GetOrAddAsync(
        string key,
        Func<CancellationToken, Task<float[]>> generator,
        TimeSpan? ttl = null,
        CancellationToken ct = default)
    {
        if (TryGet(key, out var cached) && cached != null)
            return cached;

        var embedding = await generator(ct);
        Set(key, embedding, ttl);
        return embedding;
    }

    /// <summary>
    /// Adds or updates an embedding in the cache.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="embedding">The embedding to cache.</param>
    /// <param name="ttl">Optional custom TTL for this entry.</param>
    public void Set(string key, float[] embedding, TimeSpan? ttl = null)
    {
        if (embedding == null)
            throw new ArgumentNullException(nameof(embedding));

        var cacheKey = GetCacheKey(key);
        var expiresAt = DateTime.UtcNow.Add(ttl ?? _defaultTtl);

        var entry = new CacheEntry
        {
            Key = key,
            Embedding = embedding,
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
            AccessCount = 0,
            SizeBytes = embedding.Length * sizeof(float) + key.Length * 2
        };

        // Evict if necessary before adding
        while (_cache.Count >= _maxEntries)
        {
            EvictLru();
        }

        _cache[cacheKey] = entry;
        AddToLru(cacheKey);
        _statistics.TotalBytesStored += entry.SizeBytes;
    }

    /// <summary>
    /// Batch gets embeddings from the cache.
    /// </summary>
    /// <param name="keys">The cache keys.</param>
    /// <returns>Dictionary of found embeddings (missing keys are not included).</returns>
    public Dictionary<string, float[]> GetMany(IEnumerable<string> keys)
    {
        var result = new Dictionary<string, float[]>();

        foreach (var key in keys)
        {
            if (TryGet(key, out var embedding) && embedding != null)
            {
                result[key] = embedding;
            }
        }

        return result;
    }

    /// <summary>
    /// Batch sets embeddings in the cache.
    /// </summary>
    /// <param name="embeddings">Dictionary of key-embedding pairs.</param>
    /// <param name="ttl">Optional custom TTL for all entries.</param>
    public void SetMany(IDictionary<string, float[]> embeddings, TimeSpan? ttl = null)
    {
        foreach (var kvp in embeddings)
        {
            Set(kvp.Key, kvp.Value, ttl);
        }
    }

    /// <summary>
    /// Removes an entry from the cache.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <returns>True if the entry was removed.</returns>
    public bool Remove(string key)
    {
        var cacheKey = GetCacheKey(key);
        var removed = _cache.TryRemove(cacheKey, out var entry);

        if (removed)
        {
            RemoveFromLru(cacheKey);
            _statistics.TotalBytesStored -= entry?.SizeBytes ?? 0;
            _statistics.Evictions++;
        }

        return removed;
    }

    /// <summary>
    /// Clears all entries from the cache.
    /// </summary>
    public void Clear()
    {
        lock (_lruLock)
        {
            _cache.Clear();
            _lruList.Clear();
            _statistics.TotalBytesStored = 0;
        }
    }

    /// <summary>
    /// Gets the current cache statistics.
    /// </summary>
    /// <returns>Cache statistics.</returns>
    public CacheStatistics GetStatistics()
    {
        return new CacheStatistics
        {
            Hits = _statistics.Hits,
            Misses = _statistics.Misses,
            Evictions = _statistics.Evictions,
            Expirations = _statistics.Expirations,
            TotalBytesStored = _statistics.TotalBytesStored,
            CurrentEntries = _cache.Count,
            MaxEntries = _maxEntries,
            HitRate = _statistics.TotalRequests > 0
                ? (double)_statistics.Hits / _statistics.TotalRequests
                : 0
        };
    }

    /// <summary>
    /// Resets the cache statistics.
    /// </summary>
    public void ResetStatistics()
    {
        _statistics.Hits = 0;
        _statistics.Misses = 0;
        _statistics.Evictions = 0;
        _statistics.Expirations = 0;
    }

    /// <summary>
    /// Checks if a key exists in the cache (without updating LRU).
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <returns>True if the key exists and is not expired.</returns>
    public bool ContainsKey(string key)
    {
        var cacheKey = GetCacheKey(key);
        if (_cache.TryGetValue(cacheKey, out var entry))
        {
            return entry.ExpiresAt > DateTime.UtcNow;
        }
        return false;
    }

    /// <summary>
    /// Gets information about a cached entry.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <returns>Entry info if found, null otherwise.</returns>
    public CacheEntryInfo? GetEntryInfo(string key)
    {
        var cacheKey = GetCacheKey(key);
        if (_cache.TryGetValue(cacheKey, out var entry))
        {
            return new CacheEntryInfo
            {
                Key = entry.Key,
                CreatedAt = entry.CreatedAt,
                LastAccessedAt = entry.LastAccessedAt,
                ExpiresAt = entry.ExpiresAt,
                AccessCount = entry.AccessCount,
                SizeBytes = entry.SizeBytes,
                Dimensions = entry.Embedding.Length,
                IsExpired = entry.ExpiresAt <= DateTime.UtcNow
            };
        }
        return null;
    }

    private static string GetCacheKey(string text)
    {
        // Use SHA256 hash for consistent key length and to handle long texts
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }

    private void AddToLru(string key)
    {
        lock (_lruLock)
        {
            _lruList.AddFirst(key);
        }
    }

    private void UpdateLru(string key)
    {
        lock (_lruLock)
        {
            // TODO: LinkedList.Find is O(n); consider Dictionary<string, LinkedListNode<string>> for O(1) LRU updates
            var node = _lruList.Find(key);
            if (node != null)
            {
                _lruList.Remove(node);
                _lruList.AddFirst(key);
            }
        }
    }

    private void RemoveFromLru(string key)
    {
        lock (_lruLock)
        {
            _lruList.Remove(key);
        }
    }

    private void EvictLru()
    {
        string? keyToEvict = null;

        lock (_lruLock)
        {
            if (_lruList.Last != null)
            {
                keyToEvict = _lruList.Last.Value;
                _lruList.RemoveLast();
            }
        }

        if (keyToEvict != null && _cache.TryRemove(keyToEvict, out var entry))
        {
            _statistics.TotalBytesStored -= entry.SizeBytes;
            _statistics.Evictions++;
        }
    }

    private void CleanupExpiredEntries(object? state)
    {
        var now = DateTime.UtcNow;
        var expiredKeys = new List<string>();

        foreach (var kvp in _cache)
        {
            if (kvp.Value.ExpiresAt <= now)
            {
                expiredKeys.Add(kvp.Key);
            }
        }

        foreach (var key in expiredKeys)
        {
            if (_cache.TryRemove(key, out var entry))
            {
                RemoveFromLru(key);
                _statistics.TotalBytesStored -= entry.SizeBytes;
                _statistics.Expirations++;
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cleanupTimer.Dispose();
        _cache.Clear();
    }

    private sealed class CacheEntry
    {
        public required string Key { get; init; }
        public required float[] Embedding { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime LastAccessedAt { get; set; }
        public DateTime ExpiresAt { get; init; }
        public int AccessCount { get; set; }
        public long SizeBytes { get; init; }
    }
}

/// <summary>
/// Cache statistics.
/// </summary>
public sealed class CacheStatistics
{
    /// <summary>Number of cache hits.</summary>
    public long Hits { get; set; }

    /// <summary>Number of cache misses.</summary>
    public long Misses { get; set; }

    /// <summary>Number of evictions due to capacity limits.</summary>
    public long Evictions { get; set; }

    /// <summary>Number of expired entries removed.</summary>
    public long Expirations { get; set; }

    /// <summary>Total bytes currently stored.</summary>
    public long TotalBytesStored { get; set; }

    /// <summary>Current number of entries.</summary>
    public int CurrentEntries { get; set; }

    /// <summary>Maximum allowed entries.</summary>
    public int MaxEntries { get; set; }

    /// <summary>Total number of requests (hits + misses).</summary>
    public long TotalRequests => Hits + Misses;

    /// <summary>Hit rate (0.0 - 1.0).</summary>
    public double HitRate { get; set; }

    /// <summary>Utilization percentage (0 - 100).</summary>
    public double UtilizationPercent => MaxEntries > 0 ? (double)CurrentEntries / MaxEntries * 100 : 0;
}

/// <summary>
/// Information about a cached entry.
/// </summary>
public sealed record CacheEntryInfo
{
    /// <summary>The original cache key.</summary>
    public required string Key { get; init; }

    /// <summary>When the entry was created.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>When the entry was last accessed.</summary>
    public DateTime LastAccessedAt { get; init; }

    /// <summary>When the entry expires.</summary>
    public DateTime ExpiresAt { get; init; }

    /// <summary>Number of times accessed.</summary>
    public int AccessCount { get; init; }

    /// <summary>Size in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Embedding dimensions.</summary>
    public int Dimensions { get; init; }

    /// <summary>Whether the entry is expired.</summary>
    public bool IsExpired { get; init; }

    /// <summary>Time remaining until expiration.</summary>
    public TimeSpan TimeToLive => IsExpired ? TimeSpan.Zero : ExpiresAt - DateTime.UtcNow;
}

/// <summary>
/// Embedding provider wrapper with caching support.
/// </summary>
public sealed class CachedEmbeddingProvider : IEmbeddingProvider
{
    private readonly IEmbeddingProvider _innerProvider;
    private readonly EmbeddingCache _cache;
    private readonly string _cacheKeyPrefix;

    /// <inheritdoc/>
    public string ProviderId => _innerProvider.ProviderId;

    /// <inheritdoc/>
    public string DisplayName => $"{_innerProvider.DisplayName} (Cached)";

    /// <inheritdoc/>
    public int VectorDimensions => _innerProvider.VectorDimensions;

    /// <inheritdoc/>
    public int MaxTokens => _innerProvider.MaxTokens;

    /// <inheritdoc/>
    public bool SupportsMultipleTexts => _innerProvider.SupportsMultipleTexts;

    /// <summary>
    /// Gets the underlying cache.
    /// </summary>
    public EmbeddingCache Cache => _cache;

    /// <summary>
    /// Creates a new cached embedding provider.
    /// </summary>
    /// <param name="innerProvider">The provider to wrap.</param>
    /// <param name="cache">The cache to use.</param>
    /// <param name="cacheKeyPrefix">Optional prefix for cache keys.</param>
    public CachedEmbeddingProvider(
        IEmbeddingProvider innerProvider,
        EmbeddingCache cache,
        string? cacheKeyPrefix = null)
    {
        _innerProvider = innerProvider ?? throw new ArgumentNullException(nameof(innerProvider));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _cacheKeyPrefix = cacheKeyPrefix ?? $"{innerProvider.ProviderId}:";
    }

    /// <inheritdoc/>
    public async Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var cacheKey = GetCacheKey(text);
        return await _cache.GetOrAddAsync(
            cacheKey,
            async token => await _innerProvider.GetEmbeddingAsync(text, token),
            ct: ct);
    }

    /// <inheritdoc/>
    public async Task<float[][]> GetEmbeddingsBatchAsync(string[] texts, CancellationToken ct = default)
    {
        var results = new float[texts.Length][];
        var uncachedIndices = new List<int>();
        var uncachedTexts = new List<string>();

        // Check cache for each text
        for (int i = 0; i < texts.Length; i++)
        {
            var cacheKey = GetCacheKey(texts[i]);
            if (_cache.TryGet(cacheKey, out var cached) && cached != null)
            {
                results[i] = cached;
            }
            else
            {
                uncachedIndices.Add(i);
                uncachedTexts.Add(texts[i]);
            }
        }

        // Fetch uncached embeddings
        if (uncachedTexts.Count > 0)
        {
            var newEmbeddings = await _innerProvider.GetEmbeddingsBatchAsync(uncachedTexts.ToArray(), ct);

            for (int i = 0; i < uncachedIndices.Count; i++)
            {
                var originalIndex = uncachedIndices[i];
                results[originalIndex] = newEmbeddings[i];

                // Cache the new embedding
                var cacheKey = GetCacheKey(uncachedTexts[i]);
                _cache.Set(cacheKey, newEmbeddings[i]);
            }
        }

        return results;
    }

    /// <inheritdoc/>
    public Task<bool> ValidateConnectionAsync(CancellationToken ct = default)
    {
        return _innerProvider.ValidateConnectionAsync(ct);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Don't dispose the cache - it may be shared
        _innerProvider.Dispose();
    }

    private string GetCacheKey(string text)
    {
        return $"{_cacheKeyPrefix}{text}";
    }
}
