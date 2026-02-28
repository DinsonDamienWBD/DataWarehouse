using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Federation.Authorization;

/// <summary>
/// In-memory implementation of <see cref="IPermissionCache"/> with bounded size and automatic expiration.
/// </summary>
/// <remarks>
/// <para>
/// InMemoryPermissionCache stores permission check results in a bounded in-memory cache using
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> for thread-safe access. The cache is bounded
/// to prevent unbounded memory growth (default: 10,000 entries).
/// </para>
/// <para>
/// <strong>Eviction Policy:</strong> When the cache reaches maximum capacity, the oldest entry
/// (by <see cref="PermissionCacheEntry.CachedAt"/> timestamp) is evicted to make room for new entries.
/// </para>
/// <para>
/// <strong>Expiration:</strong> A background cleanup task runs periodically (every 1 minute) to
/// remove expired entries. Expired entries are also excluded during TryGet lookups.
/// </para>
/// <para>
/// <strong>Thread Safety:</strong> All public methods are thread-safe. Statistics counters use
/// <see cref="Interlocked"/> for atomic updates.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: In-memory permission cache with bounded size (FOS-03)")]
internal sealed class InMemoryPermissionCache : IPermissionCache, IDisposable
{
    private readonly BoundedDictionary<string, PermissionCacheEntry> _cache;
    private readonly PermissionCacheConfiguration _config;
    private readonly SemaphoreSlim _cleanupLock;
    private readonly PeriodicTimer _cleanupTimer;
    private readonly CancellationTokenSource _cleanupCts;

    private long _totalRequests;
    private long _cacheHits;
    private long _cacheMisses;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryPermissionCache"/> class.
    /// </summary>
    /// <param name="config">
    /// Optional configuration. If null, uses <see cref="PermissionCacheConfiguration"/> defaults
    /// (10,000 max entries, 5-minute TTL).
    /// </param>
    public InMemoryPermissionCache(PermissionCacheConfiguration? config = null)
    {
        _config = config ?? new PermissionCacheConfiguration();
        _cache = new BoundedDictionary<string, PermissionCacheEntry>(_config.MaxEntries);
        _cleanupLock = new SemaphoreSlim(1, 1);
        _cleanupTimer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        _cleanupCts = new CancellationTokenSource();

        // Start background cleanup loop
        _ = RunCleanupLoopAsync(_cleanupCts.Token);
    }

    /// <inheritdoc/>
    public bool TryGet(string userId, string resourceKey, string operation, out PermissionCheckResult result)
    {
        Interlocked.Increment(ref _totalRequests);

        var cacheKey = $"{userId}:{resourceKey}:{operation}";

        if (_cache.TryGetValue(cacheKey, out var entry) && !entry.IsExpired)
        {
            Interlocked.Increment(ref _cacheHits);

            var age = DateTimeOffset.UtcNow - entry.CachedAt;
            result = new PermissionCheckResult
            {
                Granted = entry.Granted,
                Reason = entry.Granted ? "Cached: Access granted" : "Cached: Access denied",
                FromCache = true,
                CheckedAt = entry.CachedAt,
                CacheAge = age
            };
            return true;
        }

        Interlocked.Increment(ref _cacheMisses);
        result = PermissionCheckResult.Denied("Not in cache");
        return false;
    }

    /// <inheritdoc/>
    public void Set(string userId, string resourceKey, string operation, bool granted, TimeSpan ttl)
    {
        var entry = new PermissionCacheEntry
        {
            UserId = userId,
            ResourceKey = resourceKey,
            Operation = operation,
            Granted = granted,
            CachedAt = DateTimeOffset.UtcNow,
            TimeToLive = ttl
        };

        var cacheKey = entry.CacheKey;

        // Enforce max cache size (bounded per SDK rules)
        if (_cache.Count >= _config.MaxEntries && !_cache.ContainsKey(cacheKey))
        {
            // Evict oldest entry
            var oldest = _cache.Values.MinBy(e => e.CachedAt);
            if (oldest != null)
            {
                _cache.TryRemove(oldest.CacheKey, out _);
            }
        }

        _cache[cacheKey] = entry;
    }

    /// <inheritdoc/>
    public void Invalidate(string? userId = null, string? resourceKey = null)
    {
        if (userId == null && resourceKey == null)
        {
            _cache.Clear();
            return;
        }

        var keysToRemove = _cache.Values
            .Where(e => (userId == null || e.UserId == userId) &&
                       (resourceKey == null || e.ResourceKey == resourceKey))
            .Select(e => e.CacheKey)
            .ToList();

        foreach (var key in keysToRemove)
        {
            _cache.TryRemove(key, out _);
        }
    }

    /// <inheritdoc/>
    public PermissionCacheStatistics GetStatistics()
    {
        return new PermissionCacheStatistics
        {
            TotalRequests = Interlocked.Read(ref _totalRequests),
            CacheHits = Interlocked.Read(ref _cacheHits),
            CacheMisses = Interlocked.Read(ref _cacheMisses),
            EntryCount = _cache.Count
        };
    }

    private async Task RunCleanupLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _cleanupTimer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await CleanupExpiredEntriesAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on disposal
        }
    }

    private async Task CleanupExpiredEntriesAsync(CancellationToken ct)
    {
        await _cleanupLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var expiredKeys = _cache.Values
                .Where(e => e.IsExpired)
                .Select(e => e.CacheKey)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _cache.TryRemove(key, out _);
            }
        }
        finally
        {
            _cleanupLock.Release();
        }
    }

    /// <summary>
    /// Disposes the cache and stops the background cleanup task.
    /// </summary>
    public void Dispose()
    {
        _cleanupCts.Cancel();
        _cleanupCts.Dispose();
        _cleanupTimer.Dispose();
        _cleanupLock.Dispose();
    }
}

/// <summary>
/// Configuration for <see cref="InMemoryPermissionCache"/>.
/// </summary>
/// <remarks>
/// Defines cache size limits and default TTL for cached permission entries.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Permission cache configuration (FOS-03)")]
internal sealed record PermissionCacheConfiguration
{
    /// <summary>
    /// Gets the maximum number of entries the cache can hold.
    /// </summary>
    /// <remarks>
    /// Default: 10,000 entries. When this limit is reached, the oldest entry is evicted to
    /// make room for new entries. This bound prevents unbounded memory growth.
    /// </remarks>
    public int MaxEntries { get; init; } = 10_000;

    /// <summary>
    /// Gets the default time-to-live for cached entries.
    /// </summary>
    /// <remarks>
    /// Default: 5 minutes. Individual cache Set operations can override this with a custom TTL.
    /// </remarks>
    public TimeSpan DefaultTtl { get; init; } = TimeSpan.FromMinutes(5);
}
