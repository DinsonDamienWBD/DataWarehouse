using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.Federation.Authorization;

/// <summary>
/// Defines the contract for caching permission check results.
/// </summary>
/// <remarks>
/// <para>
/// IPermissionCache provides a bounded, time-expiring cache for access control decisions.
/// The cache reduces message bus round-trips for repeated permission checks on the same
/// user/resource/operation tuple.
/// </para>
/// <para>
/// <strong>Cache Hit Rate Target:</strong> >95% for typical access patterns (same user
/// repeatedly accessing the same resources).
/// </para>
/// <para>
/// <strong>Cache Invalidation:</strong> Cache entries can be invalidated by user ID,
/// resource key, or both. This supports scenarios like:
/// </para>
/// <list type="bullet">
///   <item><description>User's permissions changed → invalidate by user ID</description></item>
///   <item><description>Resource ACL updated → invalidate by resource key</description></item>
///   <item><description>Global policy change → invalidate all entries</description></item>
/// </list>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Permission cache interface (FOS-03)")]
public interface IPermissionCache
{
    /// <summary>
    /// Attempts to retrieve a cached permission check result.
    /// </summary>
    /// <param name="userId">The user ID to check.</param>
    /// <param name="resourceKey">The resource key (storage address) to check.</param>
    /// <param name="operation">The operation (read, write, etc.) to check.</param>
    /// <param name="result">
    /// When this method returns true, contains the cached permission check result with
    /// <see cref="PermissionCheckResult.FromCache"/> = true and <see cref="PermissionCheckResult.CacheAge"/>
    /// set to the entry age.
    /// </param>
    /// <returns>
    /// True if a non-expired cache entry exists for the specified user/resource/operation tuple;
    /// otherwise false.
    /// </returns>
    /// <remarks>
    /// Expired entries are not returned and should be evicted during the next cleanup cycle.
    /// </remarks>
    bool TryGet(string userId, string resourceKey, string operation, out PermissionCheckResult result);

    /// <summary>
    /// Stores a permission check result in the cache with the specified TTL.
    /// </summary>
    /// <param name="userId">The user ID for this permission.</param>
    /// <param name="resourceKey">The resource key (storage address) for this permission.</param>
    /// <param name="operation">The operation (read, write, etc.) for this permission.</param>
    /// <param name="granted">Whether permission was granted.</param>
    /// <param name="ttl">The time-to-live for this cache entry.</param>
    /// <remarks>
    /// If the cache is at maximum capacity and the entry is new (not an update), the oldest
    /// entry should be evicted to make room.
    /// </remarks>
    void Set(string userId, string resourceKey, string operation, bool granted, TimeSpan ttl);

    /// <summary>
    /// Invalidates cached permissions matching the specified criteria.
    /// </summary>
    /// <param name="userId">
    /// Optional user ID filter. If specified, only entries for this user are invalidated.
    /// If null, all users are affected.
    /// </param>
    /// <param name="resourceKey">
    /// Optional resource key filter. If specified, only entries for this resource are invalidated.
    /// If null, all resources are affected.
    /// </param>
    /// <remarks>
    /// <para>
    /// If both <paramref name="userId"/> and <paramref name="resourceKey"/> are null, all
    /// cache entries are cleared.
    /// </para>
    /// <para>
    /// <strong>Example Scenarios:</strong>
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Invalidate(userId: "alice", resourceKey: null) → clear all of alice's cached permissions</description></item>
    ///   <item><description>Invalidate(userId: null, resourceKey: "bucket/obj") → clear all users' permissions for bucket/obj</description></item>
    ///   <item><description>Invalidate(userId: "alice", resourceKey: "bucket/obj") → clear alice's permission for bucket/obj</description></item>
    ///   <item><description>Invalidate(userId: null, resourceKey: null) → clear entire cache</description></item>
    /// </list>
    /// </remarks>
    void Invalidate(string? userId = null, string? resourceKey = null);

    /// <summary>
    /// Returns cache statistics for observability and performance tuning.
    /// </summary>
    /// <returns>
    /// A <see cref="PermissionCacheStatistics"/> record containing hit/miss counters,
    /// entry count, and computed hit rate.
    /// </returns>
    PermissionCacheStatistics GetStatistics();
}

/// <summary>
/// Represents cache statistics for permission caching.
/// </summary>
/// <remarks>
/// Statistics include total requests, cache hits/misses, current entry count, and computed
/// hit rate. These metrics support observability and performance analysis.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Permission cache statistics (FOS-03)")]
public sealed record PermissionCacheStatistics
{
    /// <summary>
    /// Gets the total number of cache lookup requests.
    /// </summary>
    public long TotalRequests { get; init; }

    /// <summary>
    /// Gets the number of cache hits (non-expired entries found).
    /// </summary>
    public long CacheHits { get; init; }

    /// <summary>
    /// Gets the number of cache misses (no entry or expired entry).
    /// </summary>
    public long CacheMisses { get; init; }

    /// <summary>
    /// Gets the current number of entries in the cache.
    /// </summary>
    public int EntryCount { get; init; }

    /// <summary>
    /// Gets the cache hit rate as a percentage (0.0 to 1.0).
    /// </summary>
    /// <remarks>
    /// Computed as CacheHits / TotalRequests. Returns 0.0 if TotalRequests is 0.
    /// </remarks>
    public double HitRate => TotalRequests > 0 ? (double)CacheHits / TotalRequests : 0;
}
