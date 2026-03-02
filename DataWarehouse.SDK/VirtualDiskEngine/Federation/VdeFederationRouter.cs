using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation;

/// <summary>
/// Interface for resolving a key through the shard catalog hierarchy.
/// Implemented by ShardCatalog (plan 92-02/03) to walk the federation catalog tree.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Federation Router (VFED-05)")]
public interface IShardCatalogResolver
{
    /// <summary>
    /// Resolves a key through the shard catalog hierarchy using pre-computed path slots.
    /// </summary>
    /// <param name="key">The full key/path to resolve.</param>
    /// <param name="pathSlots">Pre-computed hash slots for each path segment.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resolved shard address, or <see cref="ShardAddress.None"/> if not found.</returns>
    ValueTask<ShardAddress> ResolveAsync(string key, ReadOnlyMemory<int> pathSlots, CancellationToken ct = default);
}

/// <summary>
/// Statistics snapshot from the federation router.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Federation Router (VFED-05)")]
public readonly record struct FederationRouterStats(
    long CacheHits,
    long CacheMisses,
    long ColdPathResolutions,
    double AverageHops,
    int CacheEntryCount);

/// <summary>
/// Core namespace resolution engine for VDE 2.0B federation.
/// Maps any key/path to a specific (shardVdeId, inodeNumber) address through:
/// 1. Zero-overhead passthrough for single-VDE deployments (no hashing, no table lookup).
/// 2. O(1) warm cache for repeated lookups within TTL.
/// 3. Cold-path resolution: hash path segments to catalog slots, walk hierarchy (max 5 hops).
/// </summary>
/// <remarks>
/// Thread-safe: the warm cache uses <see cref="BoundedDictionary{TKey, TValue}"/> (LRU, thread-safe),
/// the routing table uses <see cref="ReaderWriterLockSlim"/>, and statistics use Interlocked operations.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Federation Router (VFED-05)")]
public sealed class VdeFederationRouter : IAsyncDisposable
{
    private readonly PathHashRouter _pathHashRouter;
    private readonly RoutingTable? _routingTable;
    private readonly BoundedDictionary<string, (ShardAddress Address, long ExpiryTicks)>? _warmCache;
    private readonly IShardCatalogResolver? _catalogResolver;
    private readonly Guid? _singleVdeId;

    // Statistics (updated via Interlocked for thread safety)
    private long _cacheHits;
    private long _cacheMisses;
    private long _coldPathResolutions;
    private long _totalHops;
    private bool _disposed;

    /// <summary>
    /// Creates a federation router with full routing infrastructure.
    /// </summary>
    /// <param name="routingTable">The slot-to-VDE routing table.</param>
    /// <param name="pathHashRouter">The path segment hasher.</param>
    /// <param name="catalogResolver">
    /// Optional catalog resolver for cold-path hierarchy traversal.
    /// When null, routing resolves only to the domain VDE level (no deeper hierarchy walks).
    /// </param>
    public VdeFederationRouter(
        RoutingTable routingTable,
        PathHashRouter pathHashRouter,
        IShardCatalogResolver? catalogResolver = null)
    {
        ArgumentNullException.ThrowIfNull(routingTable);
        ArgumentNullException.ThrowIfNull(pathHashRouter);

        _routingTable = routingTable;
        _pathHashRouter = pathHashRouter;
        _catalogResolver = catalogResolver;
        _warmCache = new BoundedDictionary<string, (ShardAddress, long)>(FederationConstants.MaxWarmCacheEntries);
    }

    /// <summary>
    /// Private constructor for single-VDE passthrough mode.
    /// </summary>
    private VdeFederationRouter(Guid singleVdeId)
    {
        _singleVdeId = singleVdeId;
        _pathHashRouter = new PathHashRouter(); // Not used, but avoids null checks
    }

    /// <summary>
    /// Creates a zero-overhead passthrough router for single-VDE deployments.
    /// All keys resolve to the same VDE ID with no hashing, no table lookup, no caching.
    /// </summary>
    /// <param name="vdeId">The single VDE instance identifier.</param>
    /// <returns>A passthrough federation router.</returns>
    /// <exception cref="ArgumentException">Thrown when vdeId is empty.</exception>
    public static VdeFederationRouter CreateSingleVde(Guid vdeId)
    {
        if (vdeId == Guid.Empty)
            throw new ArgumentException("VDE ID must not be empty.", nameof(vdeId));

        return new VdeFederationRouter(vdeId);
    }

    /// <summary>
    /// Resolves a key to a shard address within the federation.
    /// </summary>
    /// <param name="key">The key/path to resolve (e.g., "domain/collection/document").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resolved shard address.</returns>
    /// <exception cref="ArgumentNullException">Thrown when key is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when cold-path resolution fails after max hops.</exception>
    public async ValueTask<ShardAddress> ResolveAsync(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Fast path: single-VDE passthrough (zero overhead)
        if (_singleVdeId.HasValue)
        {
            return new ShardAddress(_singleVdeId.Value, 0, ShardLevel.Data);
        }

        // Warm cache lookup
        if (_warmCache!.TryGetValue(key, out var cached))
        {
            if (Stopwatch.GetTimestamp() < cached.ExpiryTicks)
            {
                Interlocked.Increment(ref _cacheHits);
                return cached.Address;
            }

            // Expired -- remove and fall through to cold path
            _warmCache.TryRemove(key, out _);
        }

        Interlocked.Increment(ref _cacheMisses);

        // Cold path resolution
        ShardAddress address = await ResolveColdPathAsync(key, ct).ConfigureAwait(false);

        // Cache the result
        if (address.IsValid)
        {
            long expiryTicks = Stopwatch.GetTimestamp()
                + (long)(FederationConstants.WarmCacheTtlSeconds * Stopwatch.Frequency);
            _warmCache.TryAdd(key, (address, expiryTicks));
        }

        return address;
    }

    /// <summary>
    /// Removes a specific key from the warm cache.
    /// Used when a key is known to have been migrated to a different shard.
    /// </summary>
    /// <param name="key">The key to invalidate.</param>
    public void InvalidateCache(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _warmCache?.TryRemove(key, out _);
    }

    /// <summary>
    /// Bulk cache invalidation for all keys matching a prefix.
    /// Used during shard migrations to invalidate an entire namespace partition.
    /// </summary>
    /// <param name="prefix">The key prefix to match.</param>
    public void InvalidateCacheByPrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        if (_warmCache is null)
            return;

        // Collect keys to remove (can't modify during enumeration)
        var keysToRemove = new System.Collections.Generic.List<string>();
        foreach (var kvp in _warmCache)
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
                keysToRemove.Add(kvp.Key);
        }

        foreach (string keyToRemove in keysToRemove)
        {
            _warmCache.TryRemove(keyToRemove, out _);
        }
    }

    /// <summary>
    /// Clears the entire warm cache. Use after major topology changes.
    /// </summary>
    public void ClearCache()
    {
        _warmCache?.Clear();
    }

    /// <summary>
    /// Returns a snapshot of router performance statistics.
    /// </summary>
    /// <returns>Current statistics snapshot.</returns>
    public FederationRouterStats GetStats()
    {
        long hits = Interlocked.Read(ref _cacheHits);
        long misses = Interlocked.Read(ref _cacheMisses);
        long coldResolutions = Interlocked.Read(ref _coldPathResolutions);
        long totalHops = Interlocked.Read(ref _totalHops);
        double avgHops = coldResolutions > 0 ? (double)totalHops / coldResolutions : 0.0;
        int cacheCount = _warmCache?.Count ?? 0;

        return new FederationRouterStats(hits, misses, coldResolutions, avgHops, cacheCount);
    }

    /// <summary>
    /// Disposes the router, clearing the cache and releasing resources.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        _warmCache?.Clear();
        _warmCache?.Dispose();
        _routingTable?.Dispose();

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Cold-path resolution: hash path segments, look up domain VDE in routing table,
    /// then delegate to the catalog resolver for deeper hierarchy traversal.
    /// </summary>
    private async ValueTask<ShardAddress> ResolveColdPathAsync(string key, CancellationToken ct)
    {
        int[] slots = _pathHashRouter.ComputeSlots(key);
        if (slots.Length == 0)
            return ShardAddress.None;

        // First slot determines the domain VDE
        int domainSlot = slots[0];
        Guid domainVdeId = _routingTable!.Resolve(domainSlot);

        if (domainVdeId == Guid.Empty)
            return ShardAddress.None; // No VDE assigned to this domain slot

        Interlocked.Increment(ref _coldPathResolutions);

        // If no catalog resolver, return domain-level address (1 hop)
        if (_catalogResolver is null)
        {
            Interlocked.Add(ref _totalHops, 1);
            return new ShardAddress(domainVdeId, 0, ShardLevel.Domain);
        }

        // Delegate to catalog resolver for deeper resolution (up to MaxColdPathHops)
        ShardAddress resolved = await _catalogResolver.ResolveAsync(key, slots, ct).ConfigureAwait(false);

        // Track hops (minimum 2: domain lookup + catalog resolve)
        int estimatedHops = Math.Min(slots.Length + 1, FederationConstants.MaxColdPathHops);
        Interlocked.Add(ref _totalHops, estimatedHops);

        return resolved;
    }
}
