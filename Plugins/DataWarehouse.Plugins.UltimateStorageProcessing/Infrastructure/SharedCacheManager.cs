using System.Collections.Concurrent;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Infrastructure;

/// <summary>
/// Thread-safe cache manager for storage processing results with TTL-based expiration.
/// Provides key-value caching with automatic background cleanup of expired entries.
/// </summary>
/// <remarks>
/// <para>
/// Uses a <see cref="ConcurrentDictionary{TKey, TValue}"/> for thread-safe access
/// and a <see cref="Timer"/> for periodic eviction of expired entries.
/// Cache entries store creation time and TTL for expiration calculation.
/// </para>
/// </remarks>
internal sealed class SharedCacheManager : IDisposable
{
    private readonly BoundedDictionary<string, CachedEntry> _cache = new BoundedDictionary<string, CachedEntry>(1000);
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _cleanupInterval;
    private long _hits;
    private long _misses;
    private long _evictions;
    private bool _disposed;

    /// <summary>
    /// Gets the number of items currently in the cache.
    /// </summary>
    public int Count => _cache.Count;

    /// <summary>
    /// Initializes a new instance of the <see cref="SharedCacheManager"/> class.
    /// </summary>
    /// <param name="cleanupInterval">
    /// How often to run background eviction of expired entries.
    /// Defaults to 60 seconds.
    /// </param>
    public SharedCacheManager(TimeSpan? cleanupInterval = null)
    {
        _cleanupInterval = cleanupInterval ?? TimeSpan.FromSeconds(60);
        _cleanupTimer = new Timer(EvictExpiredEntries, null, _cleanupInterval, _cleanupInterval);
    }

    /// <summary>
    /// Tries to retrieve a cached result by key.
    /// Returns null if the key is not found or the entry has expired.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <returns>The cached result data, or null if not found or expired.</returns>
    public IReadOnlyDictionary<string, object?>? TryGetCached(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (_cache.TryGetValue(key, out var entry))
        {
            if (entry.IsExpired)
            {
                _cache.TryRemove(key, out _);
                Interlocked.Increment(ref _evictions);
                Interlocked.Increment(ref _misses);
                return null;
            }

            Interlocked.Increment(ref _hits);
            entry.LastAccessed = DateTimeOffset.UtcNow;
            return entry.Data;
        }

        Interlocked.Increment(ref _misses);
        return null;
    }

    /// <summary>
    /// Stores a result in the cache with the specified TTL.
    /// Overwrites any existing entry for the key.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="data">The result data to cache.</param>
    /// <param name="ttl">Time-to-live for the cache entry.</param>
    public void SetCached(string key, IReadOnlyDictionary<string, object?> data, TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(data);

        var entry = new CachedEntry
        {
            Key = key,
            Data = data,
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessed = DateTimeOffset.UtcNow,
            TimeToLive = ttl
        };

        _cache.AddOrUpdate(key, entry, (_, _) => entry);
    }

    /// <summary>
    /// Invalidates (removes) a cached entry by key.
    /// </summary>
    /// <param name="key">The cache key to invalidate.</param>
    /// <returns>True if the entry was found and removed; false otherwise.</returns>
    public bool Invalidate(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _cache.TryRemove(key, out _);
    }

    /// <summary>
    /// Invalidates all entries matching the specified key prefix.
    /// </summary>
    /// <param name="prefix">The key prefix to match.</param>
    /// <returns>The number of entries invalidated.</returns>
    public int InvalidateByPrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        var removed = 0;
        foreach (var key in _cache.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                if (_cache.TryRemove(key, out _))
                    removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// Gets cache statistics including hit rate, miss rate, and eviction count.
    /// </summary>
    /// <returns>A dictionary of cache statistics.</returns>
    public IReadOnlyDictionary<string, object> GetStats()
    {
        var hits = Interlocked.Read(ref _hits);
        var misses = Interlocked.Read(ref _misses);
        var total = hits + misses;
        var hitRate = total > 0 ? (double)hits / total * 100.0 : 0.0;

        return new Dictionary<string, object>
        {
            ["count"] = _cache.Count,
            ["hits"] = hits,
            ["misses"] = misses,
            ["evictions"] = Interlocked.Read(ref _evictions),
            ["hitRate"] = Math.Round(hitRate, 2),
            ["cleanupIntervalMs"] = _cleanupInterval.TotalMilliseconds
        };
    }

    /// <summary>
    /// Clears all entries from the cache.
    /// </summary>
    public void Clear()
    {
        _cache.Clear();
    }

    private void EvictExpiredEntries(object? state)
    {
        foreach (var kvp in _cache)
        {
            if (kvp.Value.IsExpired)
            {
                if (_cache.TryRemove(kvp.Key, out _))
                {
                    Interlocked.Increment(ref _evictions);
                }
            }
        }
    }

    /// <summary>
    /// Disposes cache resources including the cleanup timer.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _cleanupTimer.Dispose();
        _cache.Clear();
        _disposed = true;
    }

    /// <summary>
    /// Represents a cached entry with TTL-based expiration.
    /// </summary>
    private sealed class CachedEntry
    {
        /// <summary>Gets or sets the cache key.</summary>
        public required string Key { get; init; }

        /// <summary>Gets or sets the cached data.</summary>
        public required IReadOnlyDictionary<string, object?> Data { get; init; }

        /// <summary>Gets or sets when the entry was created.</summary>
        public required DateTimeOffset CreatedAt { get; init; }

        /// <summary>Gets or sets when the entry was last accessed.</summary>
        public DateTimeOffset LastAccessed { get; set; }

        /// <summary>Gets or sets the time-to-live for this entry.</summary>
        public required TimeSpan TimeToLive { get; init; }

        /// <summary>Gets a value indicating whether this entry has expired.</summary>
        public bool IsExpired => DateTimeOffset.UtcNow - CreatedAt > TimeToLive;
    }
}
