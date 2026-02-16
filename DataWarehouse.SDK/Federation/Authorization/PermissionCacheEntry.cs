using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.Federation.Authorization;

/// <summary>
/// Represents a cached permission entry with expiration metadata.
/// </summary>
/// <remarks>
/// <para>
/// PermissionCacheEntry is an internal cache storage model. Each entry represents a single
/// permission check result (user X can/cannot perform operation Y on resource Z) along with
/// the timestamp when it was cached and its time-to-live.
/// </para>
/// <para>
/// Entries are keyed by <see cref="CacheKey"/> which combines user ID, resource key, and
/// operation to form a unique identifier.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Internal cached permission entry (FOS-03)")]
internal sealed record PermissionCacheEntry
{
    /// <summary>
    /// Gets the user ID for this permission entry.
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// Gets the resource key (storage address) for this permission entry.
    /// </summary>
    public required string ResourceKey { get; init; }

    /// <summary>
    /// Gets the operation (read, write, delete, etc.) for this permission entry.
    /// </summary>
    public required string Operation { get; init; }

    /// <summary>
    /// Gets whether permission was granted.
    /// </summary>
    public required bool Granted { get; init; }

    /// <summary>
    /// Gets the UTC timestamp when this entry was cached.
    /// </summary>
    public required DateTimeOffset CachedAt { get; init; }

    /// <summary>
    /// Gets the time-to-live for this cached entry.
    /// </summary>
    /// <remarks>
    /// After <see cref="CachedAt"/> + <see cref="TimeToLive"/>, the entry is considered expired
    /// and should be evicted from the cache.
    /// </remarks>
    public required TimeSpan TimeToLive { get; init; }

    /// <summary>
    /// Gets whether this cache entry has expired.
    /// </summary>
    /// <remarks>
    /// Compares current UTC time against <see cref="CachedAt"/> + <see cref="TimeToLive"/>.
    /// </remarks>
    public bool IsExpired => DateTimeOffset.UtcNow > CachedAt + TimeToLive;

    /// <summary>
    /// Gets the composite cache key for this entry.
    /// </summary>
    /// <remarks>
    /// Format: "{UserId}:{ResourceKey}:{Operation}". This key uniquely identifies the
    /// permission tuple.
    /// </remarks>
    public string CacheKey => $"{UserId}:{ResourceKey}:{Operation}";
}
