using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for storage pipeline plugins (AD-04 key-based model).
/// Provides object/key-based CRUD, metadata, health, AI-driven placement,
/// and opt-in caching and indexing capabilities (ported from legacy storage chain).
/// </summary>
public abstract class StoragePluginBase : DataPipelinePluginBase
{
    /// <inheritdoc/>
    public override bool MutatesData => false;

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.StorageProvider;

    #region Core Storage Operations

    /// <summary>Store data with the specified key.</summary>
    public abstract Task<StorageObjectMetadata> StoreAsync(string key, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default);

    /// <summary>Retrieve data for the specified key.</summary>
    public abstract Task<Stream> RetrieveAsync(string key, CancellationToken ct = default);

    /// <summary>Delete the object with the specified key.</summary>
    public abstract Task DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>Check if an object exists.</summary>
    public abstract Task<bool> ExistsAsync(string key, CancellationToken ct = default);

    /// <summary>List objects matching prefix.</summary>
    public abstract IAsyncEnumerable<StorageObjectMetadata> ListAsync(string? prefix, CancellationToken ct = default);

    /// <summary>Get object metadata without retrieving data.</summary>
    public abstract Task<StorageObjectMetadata> GetMetadataAsync(string key, CancellationToken ct = default);

    /// <summary>Get storage health status.</summary>
    public abstract Task<StorageHealthInfo> GetHealthAsync(CancellationToken ct = default);

    #endregion

    #region StorageAddress Overloads (HAL-05)

    /// <summary>Store data using a StorageAddress. Override for native StorageAddress support.</summary>
    public virtual Task<StorageObjectMetadata> StoreAsync(StorageAddress address, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default)
        => StoreAsync(address.ToKey(), data, metadata, ct);

    /// <summary>Retrieve data using a StorageAddress. Override for native StorageAddress support.</summary>
    public virtual Task<Stream> RetrieveAsync(StorageAddress address, CancellationToken ct = default)
        => RetrieveAsync(address.ToKey(), ct);

    /// <summary>Delete an object using a StorageAddress. Override for native StorageAddress support.</summary>
    public virtual Task DeleteAsync(StorageAddress address, CancellationToken ct = default)
        => DeleteAsync(address.ToKey(), ct);

    /// <summary>Check existence using a StorageAddress. Override for native StorageAddress support.</summary>
    public virtual Task<bool> ExistsAsync(StorageAddress address, CancellationToken ct = default)
        => ExistsAsync(address.ToKey(), ct);

    /// <summary>List objects using a StorageAddress prefix. Override for native StorageAddress support.</summary>
    public virtual IAsyncEnumerable<StorageObjectMetadata> ListAsync(StorageAddress? prefix, CancellationToken ct = default)
        => ListAsync(prefix?.ToKey(), ct);

    /// <summary>Get metadata using a StorageAddress. Override for native StorageAddress support.</summary>
    public virtual Task<StorageObjectMetadata> GetMetadataAsync(StorageAddress address, CancellationToken ct = default)
        => GetMetadataAsync(address.ToKey(), ct);

    #endregion

    #region Enumeration (from ListableStoragePluginBase)

    /// <summary>
    /// Enumerates objects matching a filter in batches.
    /// Override for provider-specific optimized enumeration.
    /// </summary>
    /// <param name="prefix">Key prefix filter (null for all).</param>
    /// <param name="batchSize">Hint for batch size (provider may ignore).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of storage object metadata.</returns>
    protected virtual async IAsyncEnumerable<StorageObjectMetadata> EnumerateAsync(
        string? prefix,
        int batchSize = 100,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Default delegates to ListAsync. Override for provider-specific batching.
        await foreach (var item in ListAsync(prefix, ct))
        {
            yield return item;
        }
    }

    #endregion

    #region Opt-in Caching (from CacheableStoragePluginBase)

    private BoundedDictionary<string, CacheEntryState>? _cacheEntries;
    private Timer? _cacheCleanupTimer;
    private CacheConfiguration? _cacheConfig;

    /// <summary>
    /// Whether caching is enabled for this storage plugin.
    /// </summary>
    protected bool IsCachingEnabled => _cacheConfig != null;

    /// <summary>
    /// Enables caching for this storage plugin. Call in constructor or InitializeAsync.
    /// Caching is opt-in: plugins must explicitly call this method.
    /// </summary>
    /// <param name="config">Cache configuration (TTL, max entries, eviction policy).</param>
    protected void EnableCaching(CacheConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _cacheConfig = config;
        _cacheEntries = new BoundedDictionary<string, CacheEntryState>(1000);

        if (config.CleanupInterval > TimeSpan.Zero)
        {
            _cacheCleanupTimer = new Timer(
                async _ => await CleanupExpiredCacheAsync().ConfigureAwait(false),
                null,
                config.CleanupInterval,
                config.CleanupInterval);
        }
    }

    /// <summary>
    /// Invalidates a single cached entry by key.
    /// </summary>
    /// <param name="key">The cache key to invalidate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the entry was found and removed.</returns>
    protected virtual Task<bool> InvalidateCacheAsync(string key, CancellationToken ct = default)
    {
        if (_cacheEntries == null) return Task.FromResult(false);
        return Task.FromResult(_cacheEntries.TryRemove(key, out _));
    }

    /// <summary>
    /// Invalidates all cached entries matching a key prefix/pattern.
    /// </summary>
    /// <param name="pattern">Glob pattern (supports * wildcard) for keys to invalidate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of entries invalidated.</returns>
    protected virtual Task<int> InvalidateCacheByPatternAsync(string pattern, CancellationToken ct = default)
    {
        if (_cacheEntries == null) return Task.FromResult(0);

        var regex = new Regex(
            "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$",
            RegexOptions.None,
            TimeSpan.FromMilliseconds(100));

        var keysToRemove = _cacheEntries.Keys.Where(k => regex.IsMatch(k)).ToList();
        var count = 0;

        foreach (var key in keysToRemove)
        {
            if (ct.IsCancellationRequested) break;
            if (_cacheEntries.TryRemove(key, out _))
                count++;
        }

        return Task.FromResult(count);
    }

    /// <summary>
    /// Invalidates all cached entries with a specific tag.
    /// </summary>
    /// <param name="tag">The tag to match.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of entries invalidated.</returns>
    protected virtual Task<int> InvalidateCacheByTagAsync(string tag, CancellationToken ct = default)
    {
        if (_cacheEntries == null) return Task.FromResult(0);

        var keysWithTag = _cacheEntries
            .Where(kv => kv.Value.Tags?.Contains(tag) == true)
            .Select(kv => kv.Key)
            .ToList();

        var count = 0;
        foreach (var key in keysWithTag)
        {
            if (ct.IsCancellationRequested) break;
            if (_cacheEntries.TryRemove(key, out _))
                count++;
        }

        return Task.FromResult(count);
    }

    /// <summary>
    /// Gets cache statistics including total entries, hit/miss counts, and size.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current cache statistics.</returns>
    protected virtual Task<StorageCacheStatistics> GetCacheStatisticsAsync(CancellationToken ct = default)
    {
        if (_cacheEntries == null)
            return Task.FromResult(new StorageCacheStatistics());

        var now = DateTime.UtcNow;
        var entries = _cacheEntries.Values.ToList();

        return Task.FromResult(new StorageCacheStatistics
        {
            TotalEntries = entries.Count,
            TotalSizeBytes = entries.Sum(e => e.SizeBytes),
            ExpiredEntries = entries.Count(e => e.ExpiresAt.HasValue && e.ExpiresAt < now),
            HitCount = entries.Sum(e => e.HitCount),
            OldestEntry = entries.MinBy(e => e.CreatedAt)?.CreatedAt,
            NewestEntry = entries.MaxBy(e => e.CreatedAt)?.CreatedAt
        });
    }

    /// <summary>
    /// Removes all expired entries from the cache.
    /// Called automatically if CleanupInterval is configured.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of expired entries removed.</returns>
    protected virtual Task<int> CleanupExpiredCacheAsync(CancellationToken ct = default)
    {
        if (_cacheEntries == null) return Task.FromResult(0);

        var now = DateTime.UtcNow;
        var expiredKeys = _cacheEntries
            .Where(kv => kv.Value.ExpiresAt.HasValue && kv.Value.ExpiresAt < now)
            .Select(kv => kv.Key)
            .ToList();

        var count = 0;
        foreach (var key in expiredKeys)
        {
            if (ct.IsCancellationRequested) break;
            if (_cacheEntries.TryRemove(key, out _))
                count++;
        }

        return Task.FromResult(count);
    }

    /// <summary>
    /// Records a cache entry. Call after storing data to track cache state.
    /// </summary>
    protected void RecordCacheEntry(string key, long sizeBytes, TimeSpan? ttl = null, string[]? tags = null)
    {
        if (_cacheEntries == null || _cacheConfig == null) return;

        // Enforce max entries with oldest-first eviction
        if (_cacheConfig.MaxEntries > 0 && !_cacheEntries.ContainsKey(key) && _cacheEntries.Count >= _cacheConfig.MaxEntries)
        {
            var oldest = _cacheEntries.OrderBy(kv => kv.Value.CreatedAt).FirstOrDefault();
            if (oldest.Key != null)
            {
                _cacheEntries.TryRemove(oldest.Key, out _);
            }
        }

        var effectiveTtl = ttl ?? _cacheConfig.DefaultTtl;
        _cacheEntries[key] = new CacheEntryState
        {
            Key = key,
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow,
            ExpiresAt = effectiveTtl > TimeSpan.Zero ? DateTime.UtcNow.Add(effectiveTtl) : null,
            SizeBytes = sizeBytes,
            Tags = tags
        };
    }

    /// <summary>
    /// Tracks a cache hit for statistics and sliding expiration.
    /// </summary>
    protected void RecordCacheHit(string key)
    {
        if (_cacheEntries == null) return;
        if (_cacheEntries.TryGetValue(key, out var entry))
        {
            entry.LastAccessedAt = DateTime.UtcNow;
            entry.HitCount++;
        }
    }

    #endregion

    #region Opt-in Indexing (from IndexableStoragePluginBase)

    private BoundedDictionary<string, Dictionary<string, object>>? _indexStore;
    private IndexConfiguration? _indexConfig;
    private long _indexedCount;

    /// <summary>
    /// Whether indexing is enabled for this storage plugin.
    /// </summary>
    protected bool IsIndexingEnabled => _indexConfig != null;

    /// <summary>
    /// Enables indexing for this storage plugin. Call in constructor or InitializeAsync.
    /// Indexing is opt-in: plugins must explicitly call this method.
    /// </summary>
    /// <param name="config">Index configuration (max entries, etc.).</param>
    protected void EnableIndexing(IndexConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _indexConfig = config;
        _indexStore = new BoundedDictionary<string, Dictionary<string, object>>(1000);
    }

    /// <summary>
    /// Adds a document to the index with metadata.
    /// Enforces bounded index size with oldest-first eviction.
    /// </summary>
    /// <param name="id">Document identifier.</param>
    /// <param name="metadata">Metadata key-value pairs to index.</param>
    /// <param name="ct">Cancellation token.</param>
    protected virtual Task IndexDocumentAsync(string id, Dictionary<string, object> metadata, CancellationToken ct = default)
    {
        if (_indexStore == null || _indexConfig == null) return Task.CompletedTask;

        // Enforce bounded index
        if (_indexConfig.MaxEntries > 0 && !_indexStore.ContainsKey(id) && _indexStore.Count >= _indexConfig.MaxEntries)
        {
            var firstKey = _indexStore.Keys.FirstOrDefault();
            if (firstKey != null)
            {
                _indexStore.TryRemove(firstKey, out _);
            }
        }

        metadata["_indexed_at"] = DateTime.UtcNow;
        _indexStore[id] = metadata;
        Interlocked.Increment(ref _indexedCount);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes a document from the index.
    /// </summary>
    /// <param name="id">Document identifier to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the document was found and removed.</returns>
    protected virtual Task<bool> RemoveFromIndexAsync(string id, CancellationToken ct = default)
    {
        if (_indexStore == null) return Task.FromResult(false);
        var removed = _indexStore.TryRemove(id, out _);
        if (removed) Interlocked.Decrement(ref _indexedCount);
        return Task.FromResult(removed);
    }

    /// <summary>
    /// Searches the index with a text query (case-insensitive contains match).
    /// </summary>
    /// <param name="query">Search query text.</param>
    /// <param name="limit">Maximum results to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Array of matching document IDs.</returns>
    protected virtual Task<string[]> SearchIndexAsync(string query, int limit = 100, CancellationToken ct = default)
    {
        if (_indexStore == null) return Task.FromResult(Array.Empty<string>());

        var queryLower = query.ToLowerInvariant();
        var results = _indexStore
            .Where(kv => kv.Value.Values.Any(v =>
                v?.ToString()?.Contains(queryLower, StringComparison.OrdinalIgnoreCase) == true))
            .Take(limit)
            .Select(kv => kv.Key)
            .ToArray();

        return Task.FromResult(results);
    }

    /// <summary>
    /// Queries the index by specific metadata criteria (exact match).
    /// </summary>
    /// <param name="criteria">Metadata key-value pairs that must match.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Array of matching document IDs.</returns>
    protected virtual Task<string[]> QueryByMetadataAsync(Dictionary<string, object> criteria, CancellationToken ct = default)
    {
        if (_indexStore == null) return Task.FromResult(Array.Empty<string>());

        var results = _indexStore
            .Where(kv => criteria.All(c =>
                kv.Value.TryGetValue(c.Key, out var v) &&
                Equals(v?.ToString(), c.Value?.ToString())))
            .Select(kv => kv.Key)
            .ToArray();

        return Task.FromResult(results);
    }

    /// <summary>
    /// Gets index statistics including document count, term count, and size.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current index statistics.</returns>
    protected virtual Task<StorageIndexStatistics> GetIndexStatisticsAsync(CancellationToken ct = default)
    {
        if (_indexStore == null)
            return Task.FromResult(new StorageIndexStatistics());

        return Task.FromResult(new StorageIndexStatistics
        {
            DocumentCount = _indexStore.Count,
            TermCount = _indexStore.Values
                .SelectMany(v => v.Keys)
                .Distinct()
                .Count(),
            IndexSizeBytes = _indexStore.Sum(kv =>
                kv.Key.Length + kv.Value.Sum(v => (v.Key?.Length ?? 0) + (v.Value?.ToString()?.Length ?? 0))),
            IndexType = "InMemory"
        });
    }

    /// <summary>
    /// Rebuilds the entire index by re-indexing all objects from storage.
    /// Clears the existing index and enumerates all objects.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of documents indexed.</returns>
    protected virtual async Task<int> RebuildIndexAsync(CancellationToken ct = default)
    {
        if (_indexStore == null) return 0;

        _indexStore.Clear();
        _indexedCount = 0;
        var count = 0;

        await foreach (var item in ListAsync(null, ct))
        {
            if (ct.IsCancellationRequested) break;

            var id = item.Key ?? item.ETag ?? count.ToString();
            await IndexDocumentAsync(id, new Dictionary<string, object>
            {
                ["key"] = id,
                ["size"] = item.Size,
                ["contentType"] = item.ContentType ?? "",
                ["created"] = item.Created
            }, ct).ConfigureAwait(false);
            count++;
        }

        return count;
    }

    #endregion

    #region AI Hooks

    /// <summary>AI hook: Optimize storage placement.</summary>
    protected virtual Task<Dictionary<string, object>> OptimizeStoragePlacementAsync(string key, Dictionary<string, object> context, CancellationToken ct = default)
        => Task.FromResult(new Dictionary<string, object>());

    /// <summary>AI hook: Predict access pattern for tiering.</summary>
    protected virtual Task<string> PredictAccessPatternAsync(string key, CancellationToken ct = default)
        => Task.FromResult("unknown");

    /// <summary>
    /// AI hook: Select optimal storage strategy based on data characteristics and context.
    /// Override in derived classes to provide storage-specific selection logic.
    /// Default returns null (no AI selection — falls back to registered default).
    /// </summary>
    /// <param name="context">Context information for strategy selection (key, data size, tier hints).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The selected storage strategy identifier, or null to use the registry default.</returns>
    protected virtual Task<string?> SelectOptimalStorageStrategyAsync(Dictionary<string, object> context, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    #endregion

    #region Typed Storage Strategy Registry

    /// <summary>
    /// Lazy-initialized typed registry for <see cref="DataWarehouse.SDK.Contracts.Storage.IStorageStrategy"/> instances.
    /// Uses its own dedicated registry to support typed dispatch beyond the PluginBase IStrategy registry.
    /// </summary>
    private StrategyRegistry<DataWarehouse.SDK.Contracts.Storage.IStorageStrategy>? _storageStrategyRegistry;

    /// <summary>Lock protecting lazy initialization of <see cref="_storageStrategyRegistry"/>.</summary>
    private readonly object _storageRegistryLock = new();

    /// <summary>
    /// Gets the typed storage strategy registry. Lazily initialized on first access.
    /// </summary>
    protected StrategyRegistry<DataWarehouse.SDK.Contracts.Storage.IStorageStrategy> StorageStrategyRegistry
    {
        get
        {
            if (_storageStrategyRegistry is not null) return _storageStrategyRegistry;
            lock (_storageRegistryLock)
            {
                _storageStrategyRegistry ??= new StrategyRegistry<DataWarehouse.SDK.Contracts.Storage.IStorageStrategy>(s => s.StrategyId);
            }
            return _storageStrategyRegistry;
        }
    }

    /// <summary>
    /// Registers a storage strategy with the typed registry.
    /// </summary>
    /// <param name="strategy">The storage strategy to register.</param>
    protected void RegisterStorageStrategy(DataWarehouse.SDK.Contracts.Storage.IStorageStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        StorageStrategyRegistry.Register(strategy);
    }

    /// <summary>
    /// Dispatches an operation to the optimal storage strategy, using AI-driven strategy
    /// selection when no explicit strategy is specified. Routes through the typed
    /// <see cref="StorageStrategyRegistry"/> with CommandIdentity ACL enforcement.
    /// </summary>
    /// <typeparam name="TResult">The operation result type.</typeparam>
    /// <param name="explicitStrategyId">Explicit strategy ID (null = use AI selection via SelectOptimalStorageStrategyAsync).</param>
    /// <param name="identity">Optional CommandIdentity for ACL checks. When null, ACL is skipped.</param>
    /// <param name="dataContext">Context about the data for AI strategy selection.</param>
    /// <param name="operation">The operation to execute on the resolved storage strategy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The operation result.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no strategy is found for the given ID.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the identity is denied by the ACL provider.</exception>
    protected async Task<TResult> DispatchStorageStrategyAsync<TResult>(
        string? explicitStrategyId,
        CommandIdentity? identity,
        Dictionary<string, object>? dataContext,
        Func<DataWarehouse.SDK.Contracts.Storage.IStorageStrategy, Task<TResult>> operation,
        CancellationToken ct = default)
    {
        string? strategyId;
        if (!string.IsNullOrEmpty(explicitStrategyId))
        {
            strategyId = explicitStrategyId;
        }
        else
        {
            // Delegate to AI hook -- SelectOptimalStorageStrategyAsync
            strategyId = await SelectOptimalStorageStrategyAsync(
                dataContext ?? new Dictionary<string, object>(), ct).ConfigureAwait(false);
        }

        // Fall back to registry default if still null
        if (string.IsNullOrEmpty(strategyId))
            strategyId = StorageStrategyRegistry.DefaultStrategyId;

        if (string.IsNullOrEmpty(strategyId))
            throw new InvalidOperationException(
                $"No storage strategy ID provided and no default strategy is configured for plugin '{Id}'.");

        // ACL check if provider is configured
        if (identity != null && StrategyAclProvider != null)
        {
            if (!StrategyAclProvider.IsStrategyAllowed(strategyId, identity))
                throw new UnauthorizedAccessException(
                    $"Principal '{identity.EffectivePrincipalId}' is not authorized to use storage strategy '{strategyId}'.");
        }

        var strategy = StorageStrategyRegistry.Get(strategyId)
            ?? throw new InvalidOperationException(
                $"Storage strategy '{strategyId}' is not registered. " +
                $"Call RegisterStorageStrategy before dispatching.");

        return await operation(strategy).ConfigureAwait(false);
    }

    #endregion

    #region Domain Operations (Strategy-Dispatched)

    /// <summary>
    /// Stores data using the specified or default storage strategy.
    /// Resolves the strategy from the typed <see cref="StorageStrategyRegistry"/> with
    /// optional CommandIdentity ACL, falls back to <see cref="SelectOptimalStorageStrategyAsync"/>
    /// for AI-driven strategy selection when no strategy is specified.
    /// </summary>
    /// <param name="key">The unique key/path for the object.</param>
    /// <param name="data">The data stream to store.</param>
    /// <param name="strategyId">Optional strategy ID. When null, AI selection applies.</param>
    /// <param name="identity">Optional identity for ACL enforcement.</param>
    /// <param name="metadata">Optional metadata to associate with the stored object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Metadata of the stored object.</returns>
    protected virtual Task<StorageObjectMetadata> StoreWithStrategyAsync(
        string key,
        Stream data,
        string? strategyId = null,
        CommandIdentity? identity = null,
        IDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(data);

        return DispatchStorageStrategyAsync<StorageObjectMetadata>(
            strategyId,
            identity,
            new Dictionary<string, object>
            {
                ["operation"] = "store",
                ["key"] = key,
                ["hasMetadata"] = metadata != null
            },
            (DataWarehouse.SDK.Contracts.Storage.IStorageStrategy s) => s.StoreAsync(key, data, metadata, ct),
            ct);
    }

    /// <summary>
    /// Retrieves data using the specified or default storage strategy.
    /// Resolves the strategy from the typed <see cref="StorageStrategyRegistry"/> with
    /// optional CommandIdentity ACL.
    /// </summary>
    /// <param name="key">The unique key/path of the object.</param>
    /// <param name="strategyId">Optional strategy ID. When null, AI selection applies.</param>
    /// <param name="identity">Optional identity for ACL enforcement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The data stream.</returns>
    protected virtual Task<Stream> RetrieveWithStrategyAsync(
        string key,
        string? strategyId = null,
        CommandIdentity? identity = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        return DispatchStorageStrategyAsync<Stream>(
            strategyId,
            identity,
            new Dictionary<string, object>
            {
                ["operation"] = "retrieve",
                ["key"] = key
            },
            (DataWarehouse.SDK.Contracts.Storage.IStorageStrategy s) => s.RetrieveAsync(key, ct),
            ct);
    }

    /// <inheritdoc/>
    /// <remarks>Returns null — storage plugins set their own default strategy.</remarks>
    protected override string? GetDefaultStrategyId() => null;

    #endregion

    #region Metadata

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["StorageModel"] = "ObjectKeyBased";
        metadata["CachingEnabled"] = IsCachingEnabled;
        metadata["IndexingEnabled"] = IsIndexingEnabled;
        return metadata;
    }

    #endregion

    #region IDisposable

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cacheCleanupTimer?.Dispose();
            _cacheCleanupTimer = null;
        }
        base.Dispose(disposing);
    }

    #endregion

    #region Supporting Types

    /// <summary>
    /// Configuration for opt-in caching.
    /// </summary>
    protected class CacheConfiguration
    {
        /// <summary>Default time-to-live for cached entries.</summary>
        public TimeSpan DefaultTtl { get; init; } = TimeSpan.FromHours(1);

        /// <summary>Maximum number of cache entries (0 = unlimited, not recommended).</summary>
        public int MaxEntries { get; init; } = 10_000;

        /// <summary>Interval for automatic cleanup of expired entries.</summary>
        public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Configuration for opt-in indexing.
    /// </summary>
    protected class IndexConfiguration
    {
        /// <summary>Maximum number of indexed documents (0 = unlimited, not recommended).</summary>
        public int MaxEntries { get; init; } = 100_000;
    }

    /// <summary>
    /// Internal cache entry state tracking.
    /// </summary>
    private sealed class CacheEntryState
    {
        public string Key { get; init; } = "";
        public DateTime CreatedAt { get; init; }
        public DateTime LastAccessedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public long SizeBytes { get; init; }
        public long HitCount { get; set; }
        public string[]? Tags { get; init; }
    }

    /// <summary>
    /// Cache statistics for monitoring.
    /// </summary>
    protected record StorageCacheStatistics
    {
        /// <summary>Total entries in cache.</summary>
        public int TotalEntries { get; init; }
        /// <summary>Total size of all cached entries in bytes.</summary>
        public long TotalSizeBytes { get; init; }
        /// <summary>Number of expired entries pending cleanup.</summary>
        public int ExpiredEntries { get; init; }
        /// <summary>Total cache hits across all entries.</summary>
        public long HitCount { get; init; }
        /// <summary>Timestamp of the oldest cache entry.</summary>
        public DateTime? OldestEntry { get; init; }
        /// <summary>Timestamp of the newest cache entry.</summary>
        public DateTime? NewestEntry { get; init; }
    }

    /// <summary>
    /// Index statistics for monitoring.
    /// </summary>
    protected record StorageIndexStatistics
    {
        /// <summary>Number of indexed documents.</summary>
        public int DocumentCount { get; init; }
        /// <summary>Number of distinct metadata terms.</summary>
        public int TermCount { get; init; }
        /// <summary>Approximate index size in bytes.</summary>
        public long IndexSizeBytes { get; init; }
        /// <summary>Type of index (e.g., "InMemory").</summary>
        public string IndexType { get; init; } = "InMemory";
    }

    #endregion
}
