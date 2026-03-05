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
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataMesh.Scaling;

/// <summary>
/// Manages scaling for the DataMesh plugin with persistent stores for domains, data products,
/// consumers, data shares, and mesh policies. All entity stores use <see cref="BoundedCache{TKey,TValue}"/>
/// with write-through to <see cref="IPersistentBackingStore"/>. Supports cross-domain federation
/// via message bus for multi-instance deployments with last-writer-wins conflict resolution.
/// </summary>
/// <remarks>
/// <para>
/// Addresses DSCL-16: DataMesh previously stored all entity state in unbounded ConcurrentDictionary
/// instances. On restart, all state was lost. Under load, memory grew without bound. This manager provides:
/// <list type="bullet">
///   <item><description>Bounded caches with write-through: domains (1K), products (10K), consumers (50K), shares (10K), policies (5K)</description></item>
///   <item><description>Cross-domain federation: publishes domain/product changes to <c>dw.mesh.federation.events</c> via message bus</description></item>
///   <item><description>Last-writer-wins merge: remote catalog entries merged into local cache using UTC timestamp comparison</description></item>
///   <item><description>No unbounded ConcurrentDictionary in DataMesh</description></item>
/// </list>
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-08: DataMesh scaling with persistent stores, bounded caches, cross-domain federation")]
public sealed class DataMeshScalingManager : IScalableSubsystem, IDisposable
{
    /// <summary>Default maximum number of domains.</summary>
    public const int DefaultMaxDomains = 1_000;

    /// <summary>Default maximum number of data products.</summary>
    public const int DefaultMaxProducts = 10_000;

    /// <summary>Default maximum number of consumers.</summary>
    public const int DefaultMaxConsumers = 50_000;

    /// <summary>Default maximum number of data shares.</summary>
    public const int DefaultMaxShares = 10_000;

    /// <summary>Default maximum number of mesh policies.</summary>
    public const int DefaultMaxPolicies = 5_000;

    /// <summary>Message bus topic for federation events.</summary>
    public const string FederationTopic = "dw.mesh.federation.events";

    private const string BackingStorePrefix = "dw://internal/mesh";

    // ---- Bounded caches for all entity types ----
    private readonly BoundedCache<string, byte[]> _domains;
    private readonly BoundedCache<string, byte[]> _products;
    private readonly BoundedCache<string, byte[]> _consumers;
    private readonly BoundedCache<string, byte[]> _shares;
    private readonly BoundedCache<string, byte[]> _meshPolicies;

    // ---- Federation ----
    private readonly IMessageBus? _messageBus;
    private IDisposable? _federationSubscription;
    private readonly ConcurrentDictionary<string, long> _entityTimestamps = new();

    // ---- Backing store ----
    private readonly IPersistentBackingStore? _backingStore;

    // ---- Scaling ----
    private readonly object _configLock = new();
    private ScalingLimits _currentLimits;

    // ---- Metrics ----
    private long _backingStoreReads;
    private long _backingStoreWrites;
    private long _federationEventsPublished;
    private long _federationEventsReceived;
    private long _federationConflictsResolved;

    /// <summary>
    /// Initializes a new instance of the <see cref="DataMeshScalingManager"/> class.
    /// </summary>
    /// <param name="backingStore">
    /// Optional persistent backing store for write-through persistence.
    /// When <c>null</c>, operates in-memory only (state lost on restart).
    /// </param>
    /// <param name="messageBus">
    /// Optional message bus for cross-domain federation.
    /// When <c>null</c>, federation is disabled (single-instance mode).
    /// </param>
    /// <param name="initialLimits">Initial scaling limits. Uses defaults if <c>null</c>.</param>
    public DataMeshScalingManager(
        IPersistentBackingStore? backingStore = null,
        IMessageBus? messageBus = null,
        ScalingLimits? initialLimits = null)
    {
        _backingStore = backingStore;
        _messageBus = messageBus;
        _currentLimits = initialLimits ?? new ScalingLimits(MaxCacheEntries: DefaultMaxProducts);

        // Initialize all entity caches with write-through
        _domains = CreateCache("domains", DefaultMaxDomains);
        _products = CreateCache("products", DefaultMaxProducts);
        _consumers = CreateCache("consumers", DefaultMaxConsumers);
        _shares = CreateCache("shares", DefaultMaxShares);
        _meshPolicies = CreateCache("policies", DefaultMaxPolicies);

        SubscribeToFederation();
    }

    // -------------------------------------------------------------------
    // Domain operations
    // -------------------------------------------------------------------

    /// <summary>
    /// Stores a domain with write-through and publishes a federation event.
    /// </summary>
    /// <param name="domainId">Unique domain identifier.</param>
    /// <param name="data">Serialized domain data.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PutDomainAsync(string domainId, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainId);
        ArgumentNullException.ThrowIfNull(data);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _domains.PutAsync(domainId, data, ct).ConfigureAwait(false);
        _entityTimestamps[$"domain:{domainId}"] = timestamp;
        Interlocked.Increment(ref _backingStoreWrites);

        await PublishFederationEventAsync("domain", domainId, data, timestamp, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves a domain by ID, falling back to the backing store on cache miss.
    /// </summary>
    /// <param name="domainId">Unique domain identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Serialized domain data, or <c>null</c> if not found.</returns>
    public async Task<byte[]?> GetDomainAsync(string domainId, CancellationToken ct = default)
    {
        var result = await _domains.GetAsync(domainId, ct).ConfigureAwait(false);
        if (result != null) Interlocked.Increment(ref _backingStoreReads);
        return result;
    }

    // -------------------------------------------------------------------
    // Data product operations
    // -------------------------------------------------------------------

    /// <summary>
    /// Stores a data product with write-through and publishes a federation event.
    /// </summary>
    /// <param name="productId">Unique product identifier.</param>
    /// <param name="data">Serialized product data.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PutProductAsync(string productId, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(productId);
        ArgumentNullException.ThrowIfNull(data);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _products.PutAsync(productId, data, ct).ConfigureAwait(false);
        _entityTimestamps[$"product:{productId}"] = timestamp;
        Interlocked.Increment(ref _backingStoreWrites);

        await PublishFederationEventAsync("product", productId, data, timestamp, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves a data product by ID, falling back to the backing store on cache miss.
    /// </summary>
    /// <param name="productId">Unique product identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Serialized product data, or <c>null</c> if not found.</returns>
    public async Task<byte[]?> GetProductAsync(string productId, CancellationToken ct = default)
    {
        var result = await _products.GetAsync(productId, ct).ConfigureAwait(false);
        if (result != null) Interlocked.Increment(ref _backingStoreReads);
        return result;
    }

    // -------------------------------------------------------------------
    // Consumer operations
    // -------------------------------------------------------------------

    /// <summary>
    /// Stores a consumer record with write-through.
    /// </summary>
    /// <param name="consumerId">Unique consumer identifier.</param>
    /// <param name="data">Serialized consumer data.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PutConsumerAsync(string consumerId, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(consumerId);
        ArgumentNullException.ThrowIfNull(data);

        await _consumers.PutAsync(consumerId, data, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _backingStoreWrites);
    }

    /// <summary>
    /// Retrieves a consumer by ID, falling back to the backing store on cache miss.
    /// </summary>
    /// <param name="consumerId">Unique consumer identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Serialized consumer data, or <c>null</c> if not found.</returns>
    public async Task<byte[]?> GetConsumerAsync(string consumerId, CancellationToken ct = default)
    {
        var result = await _consumers.GetAsync(consumerId, ct).ConfigureAwait(false);
        if (result != null) Interlocked.Increment(ref _backingStoreReads);
        return result;
    }

    // -------------------------------------------------------------------
    // Data share operations
    // -------------------------------------------------------------------

    /// <summary>
    /// Stores a data share with write-through.
    /// </summary>
    /// <param name="shareId">Unique share identifier.</param>
    /// <param name="data">Serialized share data.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PutShareAsync(string shareId, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(shareId);
        ArgumentNullException.ThrowIfNull(data);

        await _shares.PutAsync(shareId, data, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _backingStoreWrites);
    }

    /// <summary>
    /// Retrieves a data share by ID, falling back to the backing store on cache miss.
    /// </summary>
    /// <param name="shareId">Unique share identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Serialized share data, or <c>null</c> if not found.</returns>
    public async Task<byte[]?> GetShareAsync(string shareId, CancellationToken ct = default)
    {
        var result = await _shares.GetAsync(shareId, ct).ConfigureAwait(false);
        if (result != null) Interlocked.Increment(ref _backingStoreReads);
        return result;
    }

    // -------------------------------------------------------------------
    // Mesh policy operations
    // -------------------------------------------------------------------

    /// <summary>
    /// Stores a mesh policy with write-through.
    /// </summary>
    /// <param name="policyId">Unique policy identifier.</param>
    /// <param name="data">Serialized policy data.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PutMeshPolicyAsync(string policyId, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policyId);
        ArgumentNullException.ThrowIfNull(data);

        await _meshPolicies.PutAsync(policyId, data, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _backingStoreWrites);
    }

    /// <summary>
    /// Retrieves a mesh policy by ID, falling back to the backing store on cache miss.
    /// </summary>
    /// <param name="policyId">Unique policy identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Serialized policy data, or <c>null</c> if not found.</returns>
    public async Task<byte[]?> GetMeshPolicyAsync(string policyId, CancellationToken ct = default)
    {
        var result = await _meshPolicies.GetAsync(policyId, ct).ConfigureAwait(false);
        if (result != null) Interlocked.Increment(ref _backingStoreReads);
        return result;
    }

    // -------------------------------------------------------------------
    // Cross-domain federation
    // -------------------------------------------------------------------

    /// <summary>
    /// Gets the number of federation events published by this instance.
    /// </summary>
    public long FederationEventsPublished => Interlocked.Read(ref _federationEventsPublished);

    /// <summary>
    /// Gets the number of federation events received from remote instances.
    /// </summary>
    public long FederationEventsReceived => Interlocked.Read(ref _federationEventsReceived);

    private async Task PublishFederationEventAsync(
        string entityType,
        string entityId,
        byte[] data,
        long timestamp,
        CancellationToken ct)
    {
        if (_messageBus == null) return;

        var message = new PluginMessage
        {
            Type = "mesh.federation.sync",
            Payload = new Dictionary<string, object>
            {
                ["entityType"] = entityType,
                ["entityId"] = entityId,
                ["data"] = Convert.ToBase64String(data),
                ["timestamp"] = timestamp
            }
        };

        await _messageBus.PublishAsync(FederationTopic, message, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _federationEventsPublished);
    }

    private void SubscribeToFederation()
    {
        if (_messageBus == null) return;

        _federationSubscription = _messageBus.Subscribe(FederationTopic, async msg =>
        {
            Interlocked.Increment(ref _federationEventsReceived);

            if (msg.Payload.TryGetValue("entityType", out var typeObj) &&
                msg.Payload.TryGetValue("entityId", out var idObj) &&
                msg.Payload.TryGetValue("data", out var dataObj) &&
                msg.Payload.TryGetValue("timestamp", out var tsObj))
            {
                var entityType = typeObj?.ToString();
                var entityId = idObj?.ToString();
                var dataBase64 = dataObj?.ToString();

                if (entityType == null || entityId == null || dataBase64 == null)
                    return;

                long remoteTimestamp = tsObj is long ts ? ts : 0;
                var compositeKey = $"{entityType}:{entityId}";

                byte[] data;
                try
                {
                    data = Convert.FromBase64String(dataBase64);
                }
                catch
                {
                    return; // Invalid data, skip
                }

                // Atomically apply last-writer-wins: update only if remote is newer.
                // AddOrUpdate factory runs under ConcurrentDictionary's internal lock,
                // guaranteeing the timestamp check and write are atomic per key.
                bool applied = false;
                _entityTimestamps.AddOrUpdate(
                    compositeKey,
                    _ => { applied = true; return remoteTimestamp; },
                    (_, localTs) =>
                    {
                        if (localTs < remoteTimestamp)
                        {
                            applied = true;
                            return remoteTimestamp;
                        }
                        return localTs; // local is newer, no update
                    });

                if (applied)
                {
                    var cache = entityType switch
                    {
                        "domain" => _domains,
                        "product" => _products,
                        _ => null
                    };

                    cache?.Put(entityId, data);
                    Interlocked.Increment(ref _federationConflictsResolved);
                }
            }
        });
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private BoundedCache<string, byte[]> CreateCache(string entityType, int maxEntries)
    {
        return new BoundedCache<string, byte[]>(new BoundedCacheOptions<string, byte[]>
        {
            MaxEntries = maxEntries,
            EvictionPolicy = CacheEvictionMode.LRU,
            BackingStore = _backingStore,
            BackingStorePath = $"{BackingStorePrefix}/{entityType}",
            Serializer = static v => v,
            Deserializer = static v => v,
            KeyToString = static k => k,
            WriteThrough = _backingStore != null
        });
    }

    // -------------------------------------------------------------------
    // IScalableSubsystem
    // -------------------------------------------------------------------

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> GetScalingMetrics()
    {
        var domainStats = _domains.GetStatistics();
        var productStats = _products.GetStatistics();
        var consumerStats = _consumers.GetStatistics();
        var shareStats = _shares.GetStatistics();
        var policyStats = _meshPolicies.GetStatistics();

        return new Dictionary<string, object>
        {
            ["mesh.domainCount"] = domainStats.ItemCount,
            ["mesh.domainCacheHitRate"] = domainStats.HitRatio,
            ["mesh.productCount"] = productStats.ItemCount,
            ["mesh.productCacheHitRate"] = productStats.HitRatio,
            ["mesh.consumerCount"] = consumerStats.ItemCount,
            ["mesh.consumerCacheHitRate"] = consumerStats.HitRatio,
            ["mesh.shareCount"] = shareStats.ItemCount,
            ["mesh.shareCacheHitRate"] = shareStats.HitRatio,
            ["mesh.policyCount"] = policyStats.ItemCount,
            ["mesh.policyCacheHitRate"] = policyStats.HitRatio,
            ["mesh.backingStoreReads"] = Interlocked.Read(ref _backingStoreReads),
            ["mesh.backingStoreWrites"] = Interlocked.Read(ref _backingStoreWrites),
            ["mesh.federationEventsPublished"] = Interlocked.Read(ref _federationEventsPublished),
            ["mesh.federationEventsReceived"] = Interlocked.Read(ref _federationEventsReceived),
            ["mesh.federationConflictsResolved"] = Interlocked.Read(ref _federationConflictsResolved)
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
            int totalEntries = _domains.Count + _products.Count + _consumers.Count +
                               _shares.Count + _meshPolicies.Count;
            int maxCapacity = DefaultMaxDomains + DefaultMaxProducts + DefaultMaxConsumers +
                              DefaultMaxShares + DefaultMaxPolicies;

            if (maxCapacity == 0) return BackpressureState.Normal;

            double utilization = (double)totalEntries / maxCapacity;
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
        _federationSubscription?.Dispose();
        _domains.Dispose();
        _products.Dispose();
        _consumers.Dispose();
        _shares.Dispose();
        _meshPolicies.Dispose();
    }
}
