using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Storage.Services;

/// <summary>
/// Composable cache management service (AD-03).
/// Extracted from CacheableStoragePluginBase to enable composition without inheritance.
/// Any storage plugin can use cache management by accepting an ICacheManager dependency.
/// </summary>
public interface ICacheManager
{
    /// <summary>Stores data with a time-to-live.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="data">The data to cache.</param>
    /// <param name="ttl">Time-to-live for the cached entry.</param>
    /// <param name="ct">Cancellation token.</param>
    Task StoreWithTtlAsync(string key, Stream data, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>Gets the remaining TTL for a cached entry.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The remaining TTL, or null if the entry does not exist or has no TTL.</returns>
    Task<TimeSpan?> GetTtlAsync(string key, CancellationToken ct = default);

    /// <summary>Sets or updates the TTL for an existing entry.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="ttl">The new TTL to set.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the entry was found and updated, false otherwise.</returns>
    Task<bool> SetTtlAsync(string key, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>Invalidates all entries matching a glob pattern.</summary>
    /// <param name="pattern">Glob pattern (e.g., "cache/images/*").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of entries invalidated.</returns>
    Task<int> InvalidatePatternAsync(string pattern, CancellationToken ct = default);

    /// <summary>Gets cache statistics.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current cache statistics.</returns>
    Task<CacheStatistics> GetStatisticsAsync(CancellationToken ct = default);

    /// <summary>Removes all expired entries.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of entries removed.</returns>
    Task<int> CleanupExpiredAsync(CancellationToken ct = default);
}
