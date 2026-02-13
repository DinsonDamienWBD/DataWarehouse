using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Storage.Services;

/// <summary>
/// Default in-memory cache manager implementation (AD-03).
/// Tracks cache entry metadata with TTL, expiration cleanup, and pattern-based invalidation.
/// Extracted from CacheableStoragePluginBase logic.
/// </summary>
public sealed class DefaultCacheManager : ICacheManager, IDisposable
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();
    private readonly Timer? _cleanupTimer;
    private bool _disposed;

    /// <summary>
    /// Creates a new DefaultCacheManager with an optional automatic cleanup interval.
    /// </summary>
    /// <param name="cleanupInterval">Interval for automatic expired entry cleanup. Pass TimeSpan.Zero to disable automatic cleanup.</param>
    public DefaultCacheManager(TimeSpan? cleanupInterval = null)
    {
        var interval = cleanupInterval ?? TimeSpan.FromMinutes(5);
        if (interval > TimeSpan.Zero)
        {
            _cleanupTimer = new Timer(
                _ => _ = CleanupExpiredAsync(),
                null,
                interval,
                interval);
        }
    }

    /// <inheritdoc/>
    public Task StoreWithTtlAsync(string key, Stream data, TimeSpan ttl, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(data);

        var size = data.CanSeek ? data.Length : 0;
        var now = DateTime.UtcNow;

        _entries[key] = new CacheEntry
        {
            Key = key,
            CreatedAt = now,
            ExpiresAt = now.Add(ttl),
            LastAccessedAt = now,
            Size = size,
            HitCount = 0
        };

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<TimeSpan?> GetTtlAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_entries.TryGetValue(key, out var entry) && entry.ExpiresAt.HasValue)
        {
            var remaining = entry.ExpiresAt.Value - DateTime.UtcNow;
            return Task.FromResult<TimeSpan?>(remaining > TimeSpan.Zero ? remaining : null);
        }

        return Task.FromResult<TimeSpan?>(null);
    }

    /// <inheritdoc/>
    public Task<bool> SetTtlAsync(string key, TimeSpan ttl, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_entries.TryGetValue(key, out var entry))
        {
            _entries[key] = entry with { ExpiresAt = DateTime.UtcNow.Add(ttl) };
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public Task<int> InvalidatePatternAsync(string pattern, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        // Convert glob pattern to regex with timeout (VALID-04)
        var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        var regex = new Regex(regexPattern, RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

        var keysToRemove = _entries.Keys
            .Where(k =>
            {
                try { return regex.IsMatch(k); }
                catch (RegexMatchTimeoutException) { return false; }
            })
            .ToList();

        var count = 0;
        foreach (var key in keysToRemove)
        {
            if (ct.IsCancellationRequested) break;
            if (_entries.TryRemove(key, out _))
                count++;
        }

        return Task.FromResult(count);
    }

    /// <inheritdoc/>
    public Task<CacheStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var now = DateTime.UtcNow;
        var entries = _entries.Values.ToList();

        return Task.FromResult(new CacheStatistics
        {
            TotalEntries = entries.Count,
            TotalSizeBytes = entries.Sum(e => e.Size),
            ExpiredEntries = entries.Count(e => e.ExpiresAt.HasValue && e.ExpiresAt < now),
            HitCount = entries.Sum(e => e.HitCount),
            MissCount = 0,
            OldestEntry = entries.MinBy(e => e.CreatedAt)?.CreatedAt,
            NewestEntry = entries.MaxBy(e => e.CreatedAt)?.CreatedAt
        });
    }

    /// <inheritdoc/>
    public Task<int> CleanupExpiredAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var expiredKeys = _entries
            .Where(kv => kv.Value.ExpiresAt.HasValue && kv.Value.ExpiresAt < now)
            .Select(kv => kv.Key)
            .ToList();

        var count = 0;
        foreach (var key in expiredKeys)
        {
            if (ct.IsCancellationRequested) break;
            if (_entries.TryRemove(key, out _))
                count++;
        }

        return Task.FromResult(count);
    }

    /// <summary>
    /// Records a cache hit for tracking statistics.
    /// </summary>
    /// <param name="key">The cache key that was hit.</param>
    public void RecordHit(string key)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            _entries[key] = entry with
            {
                LastAccessedAt = DateTime.UtcNow,
                HitCount = entry.HitCount + 1
            };
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _cleanupTimer?.Dispose();
        _disposed = true;
    }

    private record CacheEntry
    {
        public string Key { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public DateTime LastAccessedAt { get; init; }
        public long Size { get; init; }
        public long HitCount { get; init; }
    }
}
