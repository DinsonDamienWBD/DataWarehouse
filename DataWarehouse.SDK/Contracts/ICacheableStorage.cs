using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.SDK.Contracts;

/// <summary>
/// Interface for storage providers that support caching with TTL (Time-To-Live).
/// Extends IStorageProvider with expiration, invalidation, and cache statistics.
/// </summary>
public interface ICacheableStorage : IStorageProvider
{
    /// <summary>
    /// Save data with a time-to-live expiration.
    /// After TTL expires, the data may be automatically removed.
    /// </summary>
    /// <param name="uri">Resource URI.</param>
    /// <param name="data">Data stream to save.</param>
    /// <param name="ttl">Time-to-live before expiration.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveWithTtlAsync(Uri uri, Stream data, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>
    /// Get the remaining time-to-live for a cached item.
    /// Returns null if item doesn't exist or has no TTL set.
    /// </summary>
    /// <param name="uri">Resource URI.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Remaining TTL or null if not applicable.</returns>
    Task<TimeSpan?> GetTtlAsync(Uri uri, CancellationToken ct = default);

    /// <summary>
    /// Update the time-to-live for an existing cached item.
    /// </summary>
    /// <param name="uri">Resource URI.</param>
    /// <param name="ttl">New time-to-live.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if TTL was updated, false if item not found.</returns>
    Task<bool> SetTtlAsync(Uri uri, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>
    /// Remove TTL from an item, making it permanent.
    /// </summary>
    /// <param name="uri">Resource URI.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if TTL was removed, false if item not found.</returns>
    Task<bool> RemoveTtlAsync(Uri uri, CancellationToken ct = default);

    /// <summary>
    /// Invalidate all cached items matching a pattern.
    /// Pattern format depends on storage implementation (glob, regex, prefix).
    /// </summary>
    /// <param name="pattern">Pattern to match (e.g., "user:*" or "/cache/**").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of items invalidated.</returns>
    Task<int> InvalidatePatternAsync(string pattern, CancellationToken ct = default);

    /// <summary>
    /// Invalidate all cached items with a specific tag.
    /// Useful for cache invalidation by category.
    /// </summary>
    /// <param name="tag">Tag to invalidate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of items invalidated.</returns>
    Task<int> InvalidateByTagAsync(string tag, CancellationToken ct = default);

    /// <summary>
    /// Get cache statistics including hit/miss ratios.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current cache statistics.</returns>
    Task<CacheStatistics> GetCacheStatisticsAsync(CancellationToken ct = default);

    /// <summary>
    /// Clear all expired items from the cache.
    /// This is typically called automatically by a background timer.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of items removed.</returns>
    Task<int> CleanupExpiredAsync(CancellationToken ct = default);

    /// <summary>
    /// Touch an item to refresh its TTL without loading it.
    /// Useful for extending cache lifetime on access patterns.
    /// </summary>
    /// <param name="uri">Resource URI.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if item was touched, false if not found.</returns>
    Task<bool> TouchAsync(Uri uri, CancellationToken ct = default);
}

/// <summary>
/// Cache statistics for monitoring and diagnostics.
/// </summary>
public class CacheStatistics
{
    /// <summary>Total number of items currently in cache.</summary>
    public long ItemCount { get; set; }

    /// <summary>Total size of cached data in bytes.</summary>
    public long TotalSizeBytes { get; set; }

    /// <summary>Number of cache hits (successful retrievals).</summary>
    public long Hits { get; set; }

    /// <summary>Number of cache misses (item not found).</summary>
    public long Misses { get; set; }

    /// <summary>Number of items evicted due to expiration.</summary>
    public long Evictions { get; set; }

    /// <summary>Number of items with TTL set.</summary>
    public long ItemsWithTtl { get; set; }

    /// <summary>Average TTL remaining across all items with TTL.</summary>
    public TimeSpan? AverageTtlRemaining { get; set; }

    /// <summary>Time when statistics were captured.</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Cache hit ratio (0.0 to 1.0).
    /// </summary>
    public double HitRatio => Hits + Misses > 0 ? (double)Hits / (Hits + Misses) : 0;

    /// <summary>
    /// Create statistics snapshot.
    /// </summary>
    public static CacheStatistics Create(long items, long size, long hits, long misses, long evictions)
        => new()
        {
            ItemCount = items,
            TotalSizeBytes = size,
            Hits = hits,
            Misses = misses,
            Evictions = evictions,
            Timestamp = DateTime.UtcNow
        };
}

/// <summary>
/// Options for cache behavior.
/// </summary>
public class CacheOptions
{
    /// <summary>Default TTL for items without explicit TTL.</summary>
    public TimeSpan? DefaultTtl { get; set; }

    /// <summary>Maximum number of items in cache (0 = unlimited).</summary>
    public long MaxItems { get; set; }

    /// <summary>Maximum total size in bytes (0 = unlimited).</summary>
    public long MaxSizeBytes { get; set; }

    /// <summary>Eviction policy when cache is full.</summary>
    public CacheEvictionPolicy EvictionPolicy { get; set; } = CacheEvictionPolicy.LRU;

    /// <summary>Interval for background cleanup of expired items.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Whether to extend TTL on read (touch-on-access).</summary>
    public bool ExtendTtlOnAccess { get; set; }

    /// <summary>Amount to extend TTL by on access.</summary>
    public TimeSpan TtlExtensionAmount { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Creates default cache options suitable for most use cases.
    /// </summary>
    public static CacheOptions Default => new()
    {
        DefaultTtl = TimeSpan.FromHours(1),
        MaxItems = 10000,
        MaxSizeBytes = 100 * 1024 * 1024, // 100 MB
        EvictionPolicy = CacheEvictionPolicy.LRU,
        CleanupInterval = TimeSpan.FromMinutes(5)
    };

    /// <summary>
    /// Creates options for a high-performance cache with short TTL.
    /// </summary>
    public static CacheOptions HighPerformance => new()
    {
        DefaultTtl = TimeSpan.FromMinutes(5),
        MaxItems = 50000,
        MaxSizeBytes = 500 * 1024 * 1024, // 500 MB
        EvictionPolicy = CacheEvictionPolicy.LRU,
        CleanupInterval = TimeSpan.FromMinutes(1),
        ExtendTtlOnAccess = true,
        TtlExtensionAmount = TimeSpan.FromMinutes(5)
    };

    /// <summary>
    /// Creates options for a persistent cache with long TTL.
    /// </summary>
    public static CacheOptions LongTerm => new()
    {
        DefaultTtl = TimeSpan.FromDays(7),
        MaxItems = 100000,
        MaxSizeBytes = 1024 * 1024 * 1024, // 1 GB
        EvictionPolicy = CacheEvictionPolicy.LFU,
        CleanupInterval = TimeSpan.FromHours(1)
    };
}

/// <summary>
/// Cache eviction policy when capacity is reached.
/// </summary>
public enum CacheEvictionPolicy
{
    /// <summary>Least Recently Used - evict items not accessed recently.</summary>
    LRU,

    /// <summary>Least Frequently Used - evict items accessed least often.</summary>
    LFU,

    /// <summary>First In First Out - evict oldest items first.</summary>
    FIFO,

    /// <summary>Random - evict random items.</summary>
    Random,

    /// <summary>Time-based - evict items closest to expiration.</summary>
    TTL,

    /// <summary>Size-based - evict largest items first.</summary>
    LargestFirst,

    /// <summary>Size-based - evict smallest items first (more items freed).</summary>
    SmallestFirst
}

/// <summary>
/// Metadata for a cached item including TTL information.
/// </summary>
public class CacheEntryMetadata
{
    /// <summary>URI of the cached item.</summary>
    public Uri Uri { get; set; } = null!;

    /// <summary>Size of the cached data in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>When the item was created/cached.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the item was last accessed.</summary>
    public DateTime LastAccessedAt { get; set; }

    /// <summary>Number of times the item has been accessed.</summary>
    public long AccessCount { get; set; }

    /// <summary>When the item expires (null = never).</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Tags associated with this cache entry.</summary>
    public string[] Tags { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Remaining time until expiration.
    /// </summary>
    public TimeSpan? RemainingTtl => ExpiresAt.HasValue
        ? ExpiresAt.Value > DateTime.UtcNow
            ? ExpiresAt.Value - DateTime.UtcNow
            : TimeSpan.Zero
        : null;

    /// <summary>
    /// Whether the item has expired.
    /// </summary>
    public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value <= DateTime.UtcNow;
}
