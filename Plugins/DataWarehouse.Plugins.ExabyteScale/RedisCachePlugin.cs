using System.Collections.Concurrent;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Scale;
using ScaleCacheOptions = DataWarehouse.SDK.Scale.CacheOptions;

namespace DataWarehouse.Plugins.ExabyteScale;

/// <summary>
/// Distributed cache plugin with Redis-compatible interface.
/// Provides high-throughput caching with TTL and tag-based invalidation.
/// </summary>
public class RedisCachePlugin : FeaturePluginBase, IDistributedCache
{
    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.scale.rediscache";

    /// <inheritdoc/>
    public override string Name => "Redis-Compatible Distributed Cache";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _tagIndex = new();
    private readonly Timer _evictionTimer;
    private long _hitCount = 0;
    private long _missCount = 0;
    private long _evictionCount = 0;

    /// <summary>
    /// Initializes the cache and starts background eviction timer.
    /// </summary>
    public RedisCachePlugin()
    {
        // Run eviction every 60 seconds
        _evictionTimer = new Timer(EvictExpiredEntries, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// Gets a cached value by key with automatic deserialization.
    /// Returns default value if key not found or expired.
    /// </summary>
    /// <typeparam name="T">Type of cached value.</typeparam>
    /// <param name="key">Cache key to retrieve.</param>
    /// <returns>Cached value or default if not found.</returns>
    public Task<T?> GetAsync<T>(string key)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            // Check if expired
            if (entry.IsExpired())
            {
                _cache.TryRemove(key, out _);
                Interlocked.Increment(ref _missCount);
                Interlocked.Increment(ref _evictionCount);
                return Task.FromResult<T?>(default);
            }

            // Update sliding expiration
            if (entry.Sliding && entry.Ttl.HasValue)
            {
                entry.ExpiresAt = DateTimeOffset.UtcNow.Add(entry.Ttl.Value);
            }

            Interlocked.Increment(ref _hitCount);
            return Task.FromResult(JsonSerializer.Deserialize<T>(entry.Data));
        }

        Interlocked.Increment(ref _missCount);
        return Task.FromResult<T?>(default);
    }

    /// <summary>
    /// Sets a cached value with optional TTL and tags.
    /// Automatically serializes value to JSON for storage.
    /// </summary>
    /// <typeparam name="T">Type of value to cache.</typeparam>
    /// <param name="key">Cache key to store value under.</param>
    /// <param name="value">Value to cache.</param>
    /// <param name="options">Optional caching options including TTL and tags.</param>
    /// <returns>A task representing the asynchronous cache set operation.</returns>
    public Task SetAsync<T>(string key, T value, ScaleCacheOptions? options = null)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(value);
        var entry = new CacheEntry
        {
            Data = data,
            Ttl = options?.Ttl,
            Sliding = options?.Sliding ?? false,
            ExpiresAt = options?.Ttl.HasValue == true
                ? DateTimeOffset.UtcNow.Add(options.Ttl.Value)
                : null,
            Tags = options?.Tags ?? Array.Empty<string>(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        _cache[key] = entry;

        // Index tags
        if (entry.Tags.Length > 0)
        {
            foreach (var tag in entry.Tags)
            {
                _tagIndex.AddOrUpdate(tag,
                    _ => new HashSet<string> { key },
                    (_, set) =>
                    {
                        lock (set)
                        {
                            set.Add(key);
                        }
                        return set;
                    });
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Deletes a cached value by key.
    /// </summary>
    /// <param name="key">Cache key to delete.</param>
    /// <returns>True if key existed and was deleted, false otherwise.</returns>
    public Task<bool> DeleteAsync(string key)
    {
        if (_cache.TryRemove(key, out var entry))
        {
            // Remove from tag index
            foreach (var tag in entry.Tags)
            {
                if (_tagIndex.TryGetValue(tag, out var set))
                {
                    lock (set)
                    {
                        set.Remove(key);
                    }
                }
            }

            Interlocked.Increment(ref _evictionCount);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    /// <summary>
    /// Invalidates all cached entries matching a pattern.
    /// Supports glob-style wildcards for bulk invalidation.
    /// </summary>
    /// <param name="pattern">Pattern to match keys (e.g., "user:*:profile").</param>
    /// <returns>A task representing the asynchronous invalidation operation.</returns>
    public Task InvalidatePatternAsync(string pattern)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";

        var keysToRemove = _cache.Keys
            .Where(key => System.Text.RegularExpressions.Regex.IsMatch(key, regex))
            .ToList();

        foreach (var key in keysToRemove)
        {
            _cache.TryRemove(key, out _);
            Interlocked.Increment(ref _evictionCount);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Invalidates all cached entries with a specific tag.
    /// Enables bulk invalidation of related cache entries.
    /// </summary>
    /// <param name="tag">Tag to invalidate.</param>
    /// <returns>A task representing the asynchronous invalidation operation.</returns>
    public Task InvalidateTagAsync(string tag)
    {
        if (_tagIndex.TryGetValue(tag, out var keys))
        {
            List<string> keysCopy;
            lock (keys)
            {
                keysCopy = keys.ToList();
            }

            foreach (var key in keysCopy)
            {
                _cache.TryRemove(key, out _);
                Interlocked.Increment(ref _evictionCount);
            }

            _tagIndex.TryRemove(tag, out _);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets cache statistics including hits, misses, and hit ratio.
    /// </summary>
    /// <returns>Statistics object with current cache metrics.</returns>
    public Task<DistributedCacheStatistics> GetStatisticsAsync()
    {
        var hitCount = Interlocked.Read(ref _hitCount);
        var missCount = Interlocked.Read(ref _missCount);
        var total = hitCount + missCount;
        var hitRatio = total > 0 ? (double)hitCount / total : 0.0;
        var evictionCount = Interlocked.Read(ref _evictionCount);

        return Task.FromResult(new DistributedCacheStatistics(
            hitCount,
            missCount,
            hitRatio,
            evictionCount
        ));
    }

    /// <summary>
    /// Evicts expired cache entries (called by background timer).
    /// </summary>
    private void EvictExpiredEntries(object? state)
    {
        var now = DateTimeOffset.UtcNow;
        var expiredKeys = _cache
            .Where(kv => kv.Value.IsExpired())
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            if (_cache.TryRemove(key, out var entry))
            {
                // Remove from tag index
                foreach (var tag in entry.Tags)
                {
                    if (_tagIndex.TryGetValue(tag, out var set))
                    {
                        lock (set)
                        {
                            set.Remove(key);
                        }
                    }
                }

                Interlocked.Increment(ref _evictionCount);
            }
        }
    }

    /// <inheritdoc/>
    public override Task StartAsync(CancellationToken ct)
    {
        // Cache is ready immediately
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task StopAsync()
    {
        // Stop eviction timer and cleanup
        _evictionTimer?.Dispose();
        _cache.Clear();
        _tagIndex.Clear();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Represents a cached entry with TTL and metadata.
/// </summary>
internal class CacheEntry
{
    public required byte[] Data { get; init; }
    public required TimeSpan? Ttl { get; init; }
    public required bool Sliding { get; init; }
    public required DateTimeOffset? ExpiresAt { get; set; }
    public required string[] Tags { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Checks if the cache entry has expired.
    /// </summary>
    public bool IsExpired()
    {
        return ExpiresAt.HasValue && DateTimeOffset.UtcNow >= ExpiresAt.Value;
    }
}
