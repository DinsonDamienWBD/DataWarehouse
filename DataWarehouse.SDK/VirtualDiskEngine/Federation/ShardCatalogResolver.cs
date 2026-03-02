using System;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation;

/// <summary>
/// Resolution statistics from the shard catalog hierarchy traversal.
/// </summary>
/// <param name="RootHits">Successful root catalog lookups.</param>
/// <param name="RootMisses">Failed root catalog lookups (domain slot not found).</param>
/// <param name="DomainCacheHits">Domain catalog cache hits (avoided I/O).</param>
/// <param name="DomainCacheMisses">Domain catalog cache misses (triggered domain loader).</param>
/// <param name="IndexShardHits">Successful index shard lookups (bloom filter passed, data shard found).</param>
/// <param name="IndexBloomRejections">Keys rejected by index shard bloom filter (definitely not present).</param>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Catalog Resolver (VFED-13)")]
public readonly record struct CatalogResolutionStats(
    long RootHits,
    long RootMisses,
    long DomainCacheHits,
    long DomainCacheMisses,
    long IndexShardHits,
    long IndexBloomRejections);

/// <summary>
/// Concrete <see cref="IShardCatalogResolver"/> that wires the 4-level catalog hierarchy together:
/// Root (Level 0) -> Domain (Level 1, cached) -> Index (Level 2, bloom filter) -> Data (Level 3).
/// </summary>
/// <remarks>
/// <para>
/// Resolution path (3 typical hops, 5 max):
/// <list type="number">
/// <item><description>Hop 1: Root catalog lookup for domain slot -> domain VDE ID</description></item>
/// <item><description>Hop 2: Domain catalog lookup (cached) for index slot -> index shard VDE ID</description></item>
/// <item><description>Hop 3: Index shard bloom filter + binary search -> data shard VDE ID</description></item>
/// </list>
/// </para>
/// <para>
/// Statistics are tracked via <see cref="Interlocked"/> for lock-free hot path counters.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Catalog Resolver (VFED-13)")]
public sealed class ShardCatalogResolver : IShardCatalogResolver
{
    private readonly RootCatalog _rootCatalog;
    private readonly DomainCatalogCache _domainCache;
    private readonly Func<Guid, CancellationToken, ValueTask<DomainCatalog?>> _domainLoader;
    private readonly Func<Guid, CancellationToken, ValueTask<IndexShardCatalog?>> _indexShardLoader;

    // Lock-free statistics counters
    private long _rootHits;
    private long _rootMisses;
    private long _domainCacheHits;
    private long _domainCacheMisses;
    private long _indexShardHits;
    private long _indexBloomRejections;

    /// <summary>
    /// Creates a new shard catalog resolver wiring the 4-level hierarchy.
    /// </summary>
    /// <param name="rootCatalog">The Level 0 root catalog for domain lookups.</param>
    /// <param name="domainCatalogCache">LRU cache for Level 1 domain catalogs.</param>
    /// <param name="domainLoader">
    /// Async factory that loads a <see cref="DomainCatalog"/> from its VDE on cache miss.
    /// </param>
    /// <param name="indexShardLoader">
    /// Async factory that loads an <see cref="IndexShardCatalog"/> from its VDE.
    /// Index shards are not cached -- they are lightweight and use bloom filter fast rejection.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    public ShardCatalogResolver(
        RootCatalog rootCatalog,
        DomainCatalogCache domainCatalogCache,
        Func<Guid, CancellationToken, ValueTask<DomainCatalog?>> domainLoader,
        Func<Guid, CancellationToken, ValueTask<IndexShardCatalog?>> indexShardLoader)
    {
        ArgumentNullException.ThrowIfNull(rootCatalog);
        ArgumentNullException.ThrowIfNull(domainCatalogCache);
        ArgumentNullException.ThrowIfNull(domainLoader);
        ArgumentNullException.ThrowIfNull(indexShardLoader);

        _rootCatalog = rootCatalog;
        _domainCache = domainCatalogCache;
        _domainLoader = domainLoader;
        _indexShardLoader = indexShardLoader;
    }

    /// <summary>
    /// Resolves a key through the 4-level catalog hierarchy using pre-computed path slots.
    /// Walks Root -> Domain (cached) -> Index (bloom filter) -> Data in at most
    /// <see cref="FederationConstants.MaxColdPathHops"/> iterations.
    /// </summary>
    /// <param name="key">The full key/path to resolve.</param>
    /// <param name="pathSlots">Pre-computed hash slots for each path segment.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The resolved <see cref="ShardAddress"/> pointing to the data VDE, or
    /// <see cref="ShardAddress.None"/> if the key could not be resolved at any level.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when resolution exceeds <see cref="FederationConstants.MaxColdPathHops"/> hops.
    /// </exception>
    public async ValueTask<ShardAddress> ResolveAsync(
        string key,
        ReadOnlyMemory<int> pathSlots,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (pathSlots.Length == 0)
            return ShardAddress.None;

        int hopCount = 0;

        // Hop 1: Root catalog -> domain entry
        hopCount++;
        if (hopCount > FederationConstants.MaxColdPathHops)
            ThrowMaxHopsExceeded();

        int domainSlot = pathSlots.Span[0];
        CatalogEntry? rootEntry = _rootCatalog.LookupDomain(domainSlot);

        if (rootEntry is null)
        {
            Interlocked.Increment(ref _rootMisses);
            return ShardAddress.None;
        }

        Interlocked.Increment(ref _rootHits);

        // Hop 2: Domain catalog (cached) -> index shard entry
        hopCount++;
        if (hopCount > FederationConstants.MaxColdPathHops)
            ThrowMaxHopsExceeded();

        DomainCatalog? domainCatalog = _domainCache.Get(rootEntry.Value.ShardVdeId);

        if (domainCatalog is null)
        {
            Interlocked.Increment(ref _domainCacheMisses);
            domainCatalog = await _domainLoader(rootEntry.Value.ShardVdeId, ct).ConfigureAwait(false);

            if (domainCatalog is null)
                return ShardAddress.None;

            _domainCache.Put(rootEntry.Value.ShardVdeId, domainCatalog);
        }
        else
        {
            Interlocked.Increment(ref _domainCacheHits);
        }

        int indexSlot = pathSlots.Length > 1 ? pathSlots.Span[1] : pathSlots.Span[0];
        CatalogEntry? indexEntry = domainCatalog.LookupIndexShard(indexSlot);

        if (indexEntry is null)
            return ShardAddress.None;

        // Hop 3: Index shard (bloom filter + binary search) -> data shard
        hopCount++;
        if (hopCount > FederationConstants.MaxColdPathHops)
            ThrowMaxHopsExceeded();

        IndexShardCatalog? indexShard = await _indexShardLoader(indexEntry.Value.ShardVdeId, ct)
            .ConfigureAwait(false);

        if (indexShard is null)
            return ShardAddress.None;

        DataShardDescriptor? dataShard = indexShard.LookupDataShard(key);

        if (dataShard is null)
        {
            // Bloom filter rejected or no matching slot range
            Interlocked.Increment(ref _indexBloomRejections);
            return ShardAddress.None;
        }

        Interlocked.Increment(ref _indexShardHits);

        // Return data shard address. InodeNumber is 0 at this stage --
        // the actual inode is resolved by the data VDE itself when the operation reaches it.
        return new ShardAddress(dataShard.Value.DataVdeId, 0, ShardLevel.Data);
    }

    /// <summary>
    /// Returns a snapshot of catalog resolution performance statistics.
    /// </summary>
    /// <returns>Current statistics snapshot with per-level hit/miss counts.</returns>
    public CatalogResolutionStats GetStats()
    {
        return new CatalogResolutionStats(
            Interlocked.Read(ref _rootHits),
            Interlocked.Read(ref _rootMisses),
            Interlocked.Read(ref _domainCacheHits),
            Interlocked.Read(ref _domainCacheMisses),
            Interlocked.Read(ref _indexShardHits),
            Interlocked.Read(ref _indexBloomRejections));
    }

    private static void ThrowMaxHopsExceeded()
    {
        throw new InvalidOperationException(
            $"Federation resolution exceeded {FederationConstants.MaxColdPathHops} hops.");
    }
}
