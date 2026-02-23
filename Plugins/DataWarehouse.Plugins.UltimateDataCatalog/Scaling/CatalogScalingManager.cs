using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Persistence;
using DataWarehouse.SDK.Contracts.Scaling;

namespace DataWarehouse.Plugins.UltimateDataCatalog.Scaling;

/// <summary>
/// Paginated result set returned by listing operations.
/// </summary>
/// <typeparam name="T">The type of items in the result set.</typeparam>
[SdkCompatibility("6.0.0", Notes = "Phase 88-08: Paginated result for catalog and lineage queries")]
public sealed class PagedResult<T>
{
    /// <summary>Gets the items in this page.</summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>Gets the total number of items across all pages.</summary>
    public int TotalCount { get; }

    /// <summary>Gets whether more items exist beyond this page.</summary>
    public bool HasMore { get; }

    /// <summary>
    /// Initializes a new <see cref="PagedResult{T}"/>.
    /// </summary>
    /// <param name="items">Items in this page.</param>
    /// <param name="totalCount">Total count across all pages.</param>
    /// <param name="hasMore">Whether additional pages exist.</param>
    public PagedResult(IReadOnlyList<T> items, int totalCount, bool hasMore)
    {
        Items = items;
        TotalCount = totalCount;
        HasMore = hasMore;
    }
}

/// <summary>
/// Manages scaling for the DataCatalog plugin with persistent asset/relationship/glossary stores,
/// LRU-eviction bounded caches, auto-sharding by namespace prefix, and paginated listing operations.
/// Implements <see cref="IScalableSubsystem"/> for centralized scaling metrics and runtime reconfiguration.
/// </summary>
/// <remarks>
/// <para>
/// Addresses DSCL-13: DataCatalog previously stored all assets, relationships, and glossary terms
/// in unbounded in-memory dictionaries. On restart, all state was lost. Under load, memory grew without bound.
/// This manager provides:
/// <list type="bullet">
///   <item><description>Write-through <see cref="BoundedCache{TKey,TValue}"/> with LRU eviction for assets (100K), relationships (500K), and glossary (50K)</description></item>
///   <item><description>Persistent backing via <see cref="IPersistentBackingStore"/> so state survives restarts</description></item>
///   <item><description>Auto-sharding by namespace prefix: 1 shard per 100K assets, max 256 shards</description></item>
///   <item><description>Paginated <c>ListAssets</c>, <c>ListRelationships</c>, and <c>SearchGlossary</c> returning <see cref="PagedResult{T}"/></description></item>
/// </list>
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-08: Catalog scaling with persistence, LRU cache, sharding, pagination")]
public sealed class CatalogScalingManager : IScalableSubsystem, IDisposable
{
    /// <summary>Default maximum number of assets per shard.</summary>
    public const int DefaultMaxAssetsPerShard = 100_000;

    /// <summary>Default maximum number of relationships.</summary>
    public const int DefaultMaxRelationships = 500_000;

    /// <summary>Default maximum number of glossary terms.</summary>
    public const int DefaultMaxGlossaryTerms = 50_000;

    /// <summary>Maximum number of shards for auto-scaling.</summary>
    public const int MaxShardCount = 256;

    /// <summary>Number of assets per shard before a new shard is created.</summary>
    public const int AssetsPerShard = 100_000;

    private const string BackingStorePrefix = "dw://internal/catalog";

    // ---- Sharded asset caches ----
    private readonly ConcurrentDictionary<int, BoundedCache<string, byte[]>> _assetShards = new();
    private readonly ConcurrentDictionary<string, int> _namespaceShardMap = new();
    private volatile int _shardCount = 1;
    private readonly object _shardLock = new();
    private long _totalAssetCount;

    // ---- Relationship and glossary caches ----
    private readonly BoundedCache<string, byte[]> _relationships;
    private readonly BoundedCache<string, byte[]> _glossary;

    // ---- Backing store ----
    private readonly IPersistentBackingStore? _backingStore;

    // ---- Scaling ----
    private readonly object _configLock = new();
    private ScalingLimits _currentLimits;

    // ---- Metrics ----
    private long _backingStoreReads;
    private long _backingStoreWrites;

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogScalingManager"/> class.
    /// </summary>
    /// <param name="backingStore">
    /// Optional persistent backing store for write-through persistence.
    /// When <c>null</c>, operates in-memory only (state lost on restart).
    /// </param>
    /// <param name="initialLimits">Initial scaling limits. Uses defaults if <c>null</c>.</param>
    public CatalogScalingManager(
        IPersistentBackingStore? backingStore = null,
        ScalingLimits? initialLimits = null)
    {
        _backingStore = backingStore;
        _currentLimits = initialLimits ?? new ScalingLimits(MaxCacheEntries: DefaultMaxAssetsPerShard);

        // Initialize first asset shard
        _assetShards[0] = CreateAssetCache(0);

        // Initialize relationship cache (LRU, write-through)
        _relationships = new BoundedCache<string, byte[]>(new BoundedCacheOptions<string, byte[]>
        {
            MaxEntries = DefaultMaxRelationships,
            EvictionPolicy = CacheEvictionMode.LRU,
            BackingStore = _backingStore,
            BackingStorePath = $"{BackingStorePrefix}/relationships",
            Serializer = static v => v,
            Deserializer = static v => v,
            KeyToString = static k => k,
            WriteThrough = _backingStore != null
        });

        // Initialize glossary cache (LRU, write-through)
        _glossary = new BoundedCache<string, byte[]>(new BoundedCacheOptions<string, byte[]>
        {
            MaxEntries = DefaultMaxGlossaryTerms,
            EvictionPolicy = CacheEvictionMode.LRU,
            BackingStore = _backingStore,
            BackingStorePath = $"{BackingStorePrefix}/glossary",
            Serializer = static v => v,
            Deserializer = static v => v,
            KeyToString = static k => k,
            WriteThrough = _backingStore != null
        });
    }

    // -------------------------------------------------------------------
    // Asset operations (sharded)
    // -------------------------------------------------------------------

    /// <summary>
    /// Stores an asset with write-through to the backing store. Automatically selects
    /// the shard based on the asset's namespace prefix.
    /// </summary>
    /// <param name="assetId">Unique asset identifier.</param>
    /// <param name="namespacePrefix">Namespace prefix for shard routing.</param>
    /// <param name="data">Serialized asset data.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PutAssetAsync(string assetId, string namespacePrefix, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assetId);
        ArgumentNullException.ThrowIfNull(data);

        int shardId = ResolveShardId(namespacePrefix ?? string.Empty);
        var shard = GetOrCreateShard(shardId);

        await shard.PutAsync(assetId, data, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _totalAssetCount);
        Interlocked.Increment(ref _backingStoreWrites);

        // Check if we need to auto-scale shards
        AutoScaleShards();
    }

    /// <summary>
    /// Retrieves an asset by ID, falling back to the backing store on cache miss.
    /// </summary>
    /// <param name="assetId">Unique asset identifier.</param>
    /// <param name="namespacePrefix">Namespace prefix for shard routing.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Serialized asset data, or <c>null</c> if not found.</returns>
    public async Task<byte[]?> GetAssetAsync(string assetId, string namespacePrefix, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assetId);

        int shardId = ResolveShardId(namespacePrefix ?? string.Empty);
        var shard = GetOrCreateShard(shardId);

        var result = await shard.GetAsync(assetId, ct).ConfigureAwait(false);
        if (result != null)
            Interlocked.Increment(ref _backingStoreReads);

        return result;
    }

    /// <summary>
    /// Lists assets with pagination support.
    /// </summary>
    /// <param name="offset">Number of items to skip.</param>
    /// <param name="limit">Maximum number of items to return.</param>
    /// <returns>A <see cref="PagedResult{T}"/> containing the requested page of asset key-value pairs.</returns>
    public PagedResult<KeyValuePair<string, byte[]>> ListAssets(int offset, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var allItems = new List<KeyValuePair<string, byte[]>>();
        foreach (var shard in _assetShards.Values)
        {
            foreach (var kvp in shard)
            {
                allItems.Add(kvp);
            }
        }

        int totalCount = allItems.Count;
        var page = allItems.Skip(offset).Take(limit).ToList();
        bool hasMore = offset + limit < totalCount;

        return new PagedResult<KeyValuePair<string, byte[]>>(page, totalCount, hasMore);
    }

    // -------------------------------------------------------------------
    // Relationship operations
    // -------------------------------------------------------------------

    /// <summary>
    /// Stores a relationship with write-through to the backing store.
    /// </summary>
    /// <param name="relationshipId">Unique relationship identifier.</param>
    /// <param name="data">Serialized relationship data.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PutRelationshipAsync(string relationshipId, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(relationshipId);
        ArgumentNullException.ThrowIfNull(data);

        await _relationships.PutAsync(relationshipId, data, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _backingStoreWrites);
    }

    /// <summary>
    /// Retrieves a relationship by ID, falling back to the backing store on cache miss.
    /// </summary>
    /// <param name="relationshipId">Unique relationship identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Serialized relationship data, or <c>null</c> if not found.</returns>
    public async Task<byte[]?> GetRelationshipAsync(string relationshipId, CancellationToken ct = default)
    {
        var result = await _relationships.GetAsync(relationshipId, ct).ConfigureAwait(false);
        if (result != null)
            Interlocked.Increment(ref _backingStoreReads);
        return result;
    }

    /// <summary>
    /// Lists relationships with pagination support.
    /// </summary>
    /// <param name="offset">Number of items to skip.</param>
    /// <param name="limit">Maximum number of items to return.</param>
    /// <returns>A <see cref="PagedResult{T}"/> containing the requested page of relationship key-value pairs.</returns>
    public PagedResult<KeyValuePair<string, byte[]>> ListRelationships(int offset, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var allItems = _relationships.ToList();
        int totalCount = allItems.Count;
        var page = allItems.Skip(offset).Take(limit).ToList();
        bool hasMore = offset + limit < totalCount;

        return new PagedResult<KeyValuePair<string, byte[]>>(page, totalCount, hasMore);
    }

    // -------------------------------------------------------------------
    // Glossary operations
    // -------------------------------------------------------------------

    /// <summary>
    /// Stores a glossary term with write-through to the backing store.
    /// </summary>
    /// <param name="termId">Unique glossary term identifier.</param>
    /// <param name="data">Serialized glossary term data.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PutGlossaryTermAsync(string termId, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(termId);
        ArgumentNullException.ThrowIfNull(data);

        await _glossary.PutAsync(termId, data, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _backingStoreWrites);
    }

    /// <summary>
    /// Retrieves a glossary term by ID, falling back to the backing store on cache miss.
    /// </summary>
    /// <param name="termId">Unique glossary term identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Serialized glossary term data, or <c>null</c> if not found.</returns>
    public async Task<byte[]?> GetGlossaryTermAsync(string termId, CancellationToken ct = default)
    {
        var result = await _glossary.GetAsync(termId, ct).ConfigureAwait(false);
        if (result != null)
            Interlocked.Increment(ref _backingStoreReads);
        return result;
    }

    /// <summary>
    /// Searches glossary terms with pagination support. Matches terms whose key contains
    /// the specified query string (case-insensitive).
    /// </summary>
    /// <param name="query">Search query to match against term keys.</param>
    /// <param name="offset">Number of matching items to skip.</param>
    /// <param name="limit">Maximum number of items to return.</param>
    /// <returns>A <see cref="PagedResult{T}"/> containing the requested page of matching glossary entries.</returns>
    public PagedResult<KeyValuePair<string, byte[]>> SearchGlossary(string query, int offset, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var allItems = _glossary
            .Where(kvp => string.IsNullOrEmpty(query) || kvp.Key.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        int totalCount = allItems.Count;
        var page = allItems.Skip(offset).Take(limit).ToList();
        bool hasMore = offset + limit < totalCount;

        return new PagedResult<KeyValuePair<string, byte[]>>(page, totalCount, hasMore);
    }

    // -------------------------------------------------------------------
    // Sharding
    // -------------------------------------------------------------------

    /// <summary>
    /// Gets the current number of active shards.
    /// </summary>
    public int ShardCount => _shardCount;

    /// <summary>
    /// Gets the total number of assets across all shards.
    /// </summary>
    public long TotalAssetCount => Interlocked.Read(ref _totalAssetCount);

    private int ResolveShardId(string namespacePrefix)
    {
        if (_namespaceShardMap.TryGetValue(namespacePrefix, out int shardId))
            return shardId;

        // Assign namespace to shard via consistent distribution
        int hash = namespacePrefix.GetHashCode(StringComparison.Ordinal);
        shardId = Math.Abs(hash % _shardCount);
        _namespaceShardMap.TryAdd(namespacePrefix, shardId);
        return shardId;
    }

    private BoundedCache<string, byte[]> GetOrCreateShard(int shardId)
    {
        return _assetShards.GetOrAdd(shardId, id => CreateAssetCache(id));
    }

    private BoundedCache<string, byte[]> CreateAssetCache(int shardId)
    {
        return new BoundedCache<string, byte[]>(new BoundedCacheOptions<string, byte[]>
        {
            MaxEntries = DefaultMaxAssetsPerShard,
            EvictionPolicy = CacheEvictionMode.LRU,
            BackingStore = _backingStore,
            BackingStorePath = $"{BackingStorePrefix}/assets/shard-{shardId}",
            Serializer = static v => v,
            Deserializer = static v => v,
            KeyToString = static k => k,
            WriteThrough = _backingStore != null
        });
    }

    private void AutoScaleShards()
    {
        long totalAssets = Interlocked.Read(ref _totalAssetCount);
        int desiredShards = Math.Clamp((int)(totalAssets / AssetsPerShard) + 1, 1, MaxShardCount);

        if (desiredShards > _shardCount)
        {
            lock (_shardLock)
            {
                while (_shardCount < desiredShards && _shardCount < MaxShardCount)
                {
                    int newShardId = _shardCount;
                    _assetShards.TryAdd(newShardId, CreateAssetCache(newShardId));
                    _shardCount = newShardId + 1;
                }
            }
        }
    }

    // -------------------------------------------------------------------
    // IScalableSubsystem
    // -------------------------------------------------------------------

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> GetScalingMetrics()
    {
        var relationshipStats = _relationships.GetStatistics();
        var glossaryStats = _glossary.GetStatistics();

        long totalAssetCacheSize = 0;
        long totalAssetHits = 0;
        long totalAssetMisses = 0;
        foreach (var shard in _assetShards.Values)
        {
            var stats = shard.GetStatistics();
            totalAssetCacheSize += stats.ItemCount;
            totalAssetHits += stats.HitCount;
            totalAssetMisses += stats.MissCount;
        }

        long assetTotal = totalAssetHits + totalAssetMisses;

        return new Dictionary<string, object>
        {
            ["catalog.assetCount"] = Interlocked.Read(ref _totalAssetCount),
            ["catalog.assetCacheSize"] = totalAssetCacheSize,
            ["catalog.assetCacheHitRate"] = assetTotal > 0 ? (double)totalAssetHits / assetTotal : 0.0,
            ["catalog.shardCount"] = _shardCount,
            ["catalog.relationshipCacheSize"] = relationshipStats.ItemCount,
            ["catalog.relationshipCacheHitRate"] = relationshipStats.HitRatio,
            ["catalog.glossaryCacheSize"] = glossaryStats.ItemCount,
            ["catalog.glossaryCacheHitRate"] = glossaryStats.HitRatio,
            ["catalog.backingStoreReads"] = Interlocked.Read(ref _backingStoreReads),
            ["catalog.backingStoreWrites"] = Interlocked.Read(ref _backingStoreWrites)
        };
    }

    /// <inheritdoc />
    public Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_configLock)
        {
            _currentLimits = limits;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ScalingLimits CurrentLimits
    {
        get
        {
            lock (_configLock)
            {
                return _currentLimits;
            }
        }
    }

    /// <inheritdoc />
    public BackpressureState CurrentBackpressureState
    {
        get
        {
            long totalAssets = Interlocked.Read(ref _totalAssetCount);
            long maxCapacity = (long)_shardCount * DefaultMaxAssetsPerShard;

            if (maxCapacity == 0) return BackpressureState.Normal;

            double utilization = (double)totalAssets / maxCapacity;
            return utilization switch
            {
                >= 0.85 => BackpressureState.Critical,
                >= 0.50 => BackpressureState.Warning,
                _ => BackpressureState.Normal
            };
        }
    }

    // -------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var shard in _assetShards.Values)
            shard.Dispose();
        _assetShards.Clear();

        _relationships.Dispose();
        _glossary.Dispose();
    }
}
