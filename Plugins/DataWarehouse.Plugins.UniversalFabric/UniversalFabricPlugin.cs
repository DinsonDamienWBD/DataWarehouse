using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Storage;
using DataWarehouse.SDK.Storage.Fabric;
using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using IStorageStrategy = DataWarehouse.SDK.Contracts.Storage.IStorageStrategy;
using HealthStatus = DataWarehouse.SDK.Contracts.Storage.HealthStatus;
using StorageTier = DataWarehouse.SDK.Contracts.Storage.StorageTier;

namespace DataWarehouse.Plugins.UniversalFabric;

/// <summary>
/// Core Universal Storage Fabric plugin that makes all storage backends accessible
/// through dw:// addressing. Implements <see cref="IStorageFabric"/> to provide
/// a unified facade for Store/Retrieve/Delete/Copy/Move operations across any
/// registered backend via address routing.
/// </summary>
/// <remarks>
/// <para>
/// On initialization, the plugin discovers all registered <see cref="IStorageStrategy"/>
/// instances via message bus and registers them in its <see cref="BackendRegistryImpl"/>.
/// Subsequent operations are routed through <see cref="AddressRouter"/> which resolves
/// <see cref="StorageAddress"/> instances to the correct backend strategy.
/// </para>
/// <para>
/// Cross-backend operations (copy, move) are streamed without full buffering, enabling
/// efficient migration of large objects between heterogeneous backends.
/// </para>
/// </remarks>
public sealed class UniversalFabricPlugin : StoragePluginBase, IStorageFabric, IDisposable
{
    private readonly BackendRegistryImpl _registry = new();
    private readonly AddressRouter _router = new();
    private readonly BoundedDictionary<string, HealthStatus> _healthCache = new BoundedDictionary<string, HealthStatus>(1000);
    private readonly List<IDisposable> _subscriptions = new();
    private volatile bool _initialized;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.storage.universal-fabric";

    /// <inheritdoc/>
    public override string Name => "Universal Storage Fabric";

    /// <inheritdoc/>
    public override string Version => "5.0.0";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.StorageProvider;

    /// <summary>
    /// Gets the backend registry for discovering, registering, and managing storage backends.
    /// </summary>
    public IBackendRegistry Registry => _registry;

    #region Initialization

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken ct = default)
    {
        await base.InitializeAsync(ct).ConfigureAwait(false);

        // Subscribe to dynamic backend registration events
        if (MessageBus is not null)
        {
            _subscriptions.Add(MessageBus.Subscribe("storage.backend.registered", OnBackendRegistered));
            _subscriptions.Add(MessageBus.Subscribe("storage.backend.health", OnBackendHealthUpdate));

            // Discover existing storage strategies via message bus
            await DiscoverBackendsAsync(ct).ConfigureAwait(false);

            // Announce fabric readiness
            await MessageBus.PublishAsync("storage.fabric.ready", new PluginMessage
            {
                Type = "storage.fabric.ready",
                SourcePluginId = Id,
                Payload = new Dictionary<string, object>
                {
                    ["backendCount"] = _registry.Count,
                    ["fabricId"] = Id
                }
            }, ct).ConfigureAwait(false);
        }

        _initialized = true;
    }

    /// <summary>
    /// Discovers existing storage strategies by publishing a discovery request on the message bus.
    /// Strategies that respond are automatically registered in the backend registry.
    /// </summary>
    private async Task DiscoverBackendsAsync(CancellationToken ct)
    {
        if (MessageBus is null) return;

        try
        {
            var response = await MessageBus.SendAsync("storage.fabric.discover", new PluginMessage
            {
                Type = "storage.fabric.discover",
                SourcePluginId = Id,
                Payload = new Dictionary<string, object>
                {
                    ["requestType"] = "enumerate-strategies"
                }
            }, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);

            if (response.Success && response.Payload is IEnumerable<object> strategies)
            {
                foreach (var strategyObj in strategies)
                {
                    if (strategyObj is IStorageStrategy strategy)
                    {
                        RegisterStrategyAsBackend(strategy);
                    }
                }
            }
        }
        catch (TimeoutException)
        {
            // No strategies responded to discovery -- this is normal during initial startup
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Discovery failed gracefully -- backends can be registered dynamically later
        }
    }

    /// <summary>
    /// Creates a <see cref="BackendDescriptor"/> from an <see cref="IStorageStrategy"/>
    /// and registers it in the backend registry.
    /// </summary>
    private void RegisterStrategyAsBackend(IStorageStrategy strategy)
    {
        var descriptor = new BackendDescriptor
        {
            BackendId = strategy.StrategyId,
            Name = strategy.Name,
            StrategyId = strategy.StrategyId,
            Tier = strategy.Tier,
            Capabilities = strategy.Capabilities,
            Tags = InferTags(strategy)
        };

        _registry.Register(descriptor, strategy);

        // Set the first registered backend as default if none set
        if (_router.DefaultBackendId is null)
        {
            _router.SetDefaultBackend(strategy.StrategyId);
        }
    }

    /// <summary>
    /// Infers tags from a strategy's metadata for categorization.
    /// </summary>
    private static IReadOnlySet<string> InferTags(IStorageStrategy strategy)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var name = strategy.Name.ToLowerInvariant();
        var id = strategy.StrategyId.ToLowerInvariant();

        if (name.Contains("local") || id.Contains("local") || name.Contains("file"))
            tags.Add("local");
        if (name.Contains("s3") || id.Contains("s3") || name.Contains("aws"))
            tags.Add("cloud");
        if (name.Contains("azure") || id.Contains("azure"))
            tags.Add("cloud");
        if (name.Contains("gcs") || id.Contains("gcs") || name.Contains("google"))
            tags.Add("cloud");
        if (strategy.Capabilities.SupportsEncryption)
            tags.Add("encrypted");
        if (name.Contains("archive") || strategy.Tier == StorageTier.Archive)
            tags.Add("archive");

        return tags;
    }

    #endregion

    #region Message Bus Handlers

    private Task OnBackendRegistered(PluginMessage message)
    {
        if (message.Payload.TryGetValue("strategy", out var strategyObj) &&
            strategyObj is IStorageStrategy strategy)
        {
            RegisterStrategyAsBackend(strategy);
        }
        return Task.CompletedTask;
    }

    private Task OnBackendHealthUpdate(PluginMessage message)
    {
        if (message.Payload.TryGetValue("backendId", out var idObj) &&
            idObj is string backendId &&
            message.Payload.TryGetValue("status", out var statusObj) &&
            statusObj is HealthStatus status)
        {
            _healthCache[backendId] = status;
        }
        return Task.CompletedTask;
    }

    #endregion

    #region IStorageFabric Implementation

    /// <inheritdoc/>
    public async Task<StorageObjectMetadata> StoreAsync(
        StorageAddress address,
        Stream data,
        StoragePlacementHints? hints,
        IDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        var strategy = ResolveWithHints(address, hints);
        var (_, objectKey) = _router.ExtractRoutingKey(address, _registry);

        try
        {
            return await strategy.StoreAsync(objectKey, data, metadata, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw WrapBackendException(ex, strategy.StrategyId, address, "Store");
        }
    }

    /// <inheritdoc/>
    Task<Stream> IStorageFabric.RetrieveAsync(StorageAddress address, CancellationToken ct)
    {
        return RetrieveFromFabricAsync(address, ct);
    }

    /// <summary>
    /// Retrieves data from the fabric with full address routing.
    /// </summary>
    private async Task<Stream> RetrieveFromFabricAsync(StorageAddress address, CancellationToken ct)
    {
        var strategy = ResolveOrThrow(address);
        var (_, objectKey) = _router.ExtractRoutingKey(address, _registry);

        try
        {
            return await strategy.RetrieveAsync(objectKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw WrapBackendException(ex, strategy.StrategyId, address, "Retrieve");
        }
    }

    /// <inheritdoc/>
    public async Task<StorageObjectMetadata> CopyAsync(
        StorageAddress source,
        StorageAddress destination,
        CancellationToken ct = default)
    {
        var sourceStrategy = ResolveOrThrow(source);
        var destStrategy = ResolveOrThrow(destination);
        var (_, sourceKey) = _router.ExtractRoutingKey(source, _registry);
        var (_, destKey) = _router.ExtractRoutingKey(destination, _registry);

        try
        {
            // Stream data from source to destination without full buffering
            await using var stream = await sourceStrategy.RetrieveAsync(sourceKey, ct).ConfigureAwait(false);

            // Retrieve source metadata to carry over
            StorageObjectMetadata? sourceMeta = null;
            try
            {
                sourceMeta = await sourceStrategy.GetMetadataAsync(sourceKey, ct).ConfigureAwait(false);
            }
            catch
            {
                // Metadata retrieval is best-effort for copy operations
            }

            var copyMetadata = sourceMeta?.CustomMetadata is not null
                ? new Dictionary<string, string>(sourceMeta.CustomMetadata)
                : null;

            return await destStrategy.StoreAsync(destKey, stream, copyMetadata, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not BackendNotFoundException)
        {
            throw new MigrationFailedException(
                $"Copy failed from {source} to {destination}: {ex.Message}",
                source, destination, ex);
        }
    }

    /// <inheritdoc/>
    public async Task<StorageObjectMetadata> MoveAsync(
        StorageAddress source,
        StorageAddress destination,
        CancellationToken ct = default)
    {
        // Copy first, then delete source on success
        var result = await CopyAsync(source, destination, ct).ConfigureAwait(false);

        try
        {
            var sourceStrategy = ResolveOrThrow(source);
            var (_, sourceKey) = _router.ExtractRoutingKey(source, _registry);
            await sourceStrategy.DeleteAsync(sourceKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Copy succeeded but delete failed -- data exists in both locations
            // This is a partial success; we return the copy result but could log the issue
            throw new MigrationFailedException(
                $"Move partially succeeded: copy to {destination} completed but delete from {source} failed: {ex.Message}",
                source, destination, ex);
        }

        return result;
    }

    /// <inheritdoc/>
    public IStorageStrategy? ResolveBackend(StorageAddress address)
    {
        return _router.Resolve(address, _registry);
    }

    /// <inheritdoc/>
    public async Task<FabricHealthReport> GetFabricHealthAsync(CancellationToken ct = default)
    {
        var backendHealth = new Dictionary<string, StorageHealthInfo>();
        var healthyCount = 0;
        var degradedCount = 0;
        var unhealthyCount = 0;

        foreach (var descriptor in _registry.All)
        {
            ct.ThrowIfCancellationRequested();

            var strategy = _registry.GetStrategy(descriptor.BackendId);
            if (strategy is null) continue;

            try
            {
                var health = await strategy.GetHealthAsync(ct).ConfigureAwait(false);
                backendHealth[descriptor.BackendId] = health;

                switch (health.Status)
                {
                    case HealthStatus.Healthy:
                        healthyCount++;
                        break;
                    case HealthStatus.Degraded:
                        degradedCount++;
                        break;
                    default:
                        unhealthyCount++;
                        break;
                }

                _healthCache[descriptor.BackendId] = health.Status;
            }
            catch
            {
                unhealthyCount++;
                backendHealth[descriptor.BackendId] = new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy
                };
                _healthCache[descriptor.BackendId] = HealthStatus.Unhealthy;
            }
        }

        return new FabricHealthReport
        {
            TotalBackends = _registry.Count,
            HealthyBackends = healthyCount,
            DegradedBackends = degradedCount,
            UnhealthyBackends = unhealthyCount,
            BackendHealth = backendHealth,
            CheckedAt = DateTime.UtcNow
        };
    }

    #endregion

    #region IObjectStorageCore Implementation (string-key methods)

    /// <inheritdoc/>
    public override async Task<StorageObjectMetadata> StoreAsync(string key, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        StorageAddress address = key;
        return await StoreAsync(address, data, hints: null, metadata, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task<Stream> RetrieveAsync(string key, CancellationToken ct = default)
    {
        StorageAddress address = key;
        return await RetrieveFromFabricAsync(address, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        StorageAddress address = key;
        var strategy = ResolveOrThrow(address);
        var (_, objectKey) = _router.ExtractRoutingKey(address, _registry);

        try
        {
            await strategy.DeleteAsync(objectKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw WrapBackendException(ex, strategy.StrategyId, address, "Delete");
        }
    }

    /// <inheritdoc/>
    public override async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        StorageAddress address = key;
        var strategy = ResolveOrThrow(address);
        var (_, objectKey) = _router.ExtractRoutingKey(address, _registry);

        try
        {
            return await strategy.ExistsAsync(objectKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw WrapBackendException(ex, strategy.StrategyId, address, "Exists");
        }
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<StorageObjectMetadata> ListAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (prefix is not null)
        {
            // Route to specific backend if prefix can be resolved
            StorageAddress address = prefix;
            var strategy = _router.Resolve(address, _registry);
            if (strategy is not null)
            {
                var (_, objectKey) = _router.ExtractRoutingKey(address, _registry);
                await foreach (var item in strategy.ListAsync(objectKey, ct).ConfigureAwait(false))
                {
                    yield return item;
                }
                yield break;
            }
        }

        // List across all backends when no specific backend resolves
        foreach (var descriptor in _registry.All)
        {
            ct.ThrowIfCancellationRequested();
            var strategy = _registry.GetStrategy(descriptor.BackendId);
            if (strategy is null) continue;

            IAsyncEnumerable<StorageObjectMetadata> items;
            try
            {
                items = strategy.ListAsync(prefix, ct);
            }
            catch
            {
                continue; // Skip unhealthy backends
            }

            await foreach (var item in items.ConfigureAwait(false))
            {
                yield return item;
            }
        }
    }

    /// <inheritdoc/>
    public override async Task<StorageObjectMetadata> GetMetadataAsync(string key, CancellationToken ct = default)
    {
        StorageAddress address = key;
        var strategy = ResolveOrThrow(address);
        var (_, objectKey) = _router.ExtractRoutingKey(address, _registry);

        try
        {
            return await strategy.GetMetadataAsync(objectKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw WrapBackendException(ex, strategy.StrategyId, address, "GetMetadata");
        }
    }

    /// <inheritdoc/>
    public override async Task<StorageHealthInfo> GetHealthAsync(CancellationToken ct = default)
    {
        // Default backend health check
        if (_router.DefaultBackendId is not null)
        {
            var strategy = _registry.GetStrategy(_router.DefaultBackendId);
            if (strategy is not null)
            {
                return await strategy.GetHealthAsync(ct).ConfigureAwait(false);
            }
        }

        // If no default, report based on overall fabric health
        var report = await GetFabricHealthAsync(ct).ConfigureAwait(false);
        return new StorageHealthInfo
        {
            Status = report.OverallStatus,
            AvailableCapacity = report.BackendHealth.Values.Sum(h => h.AvailableCapacity ?? 0)
        };
    }

    /// <summary>
    /// Gets available capacity summed across all registered backends.
    /// </summary>
    public async Task<long?> GetAvailableCapacityAsync(CancellationToken ct = default)
    {
        long total = 0;
        var hasCapacity = false;

        foreach (var descriptor in _registry.All)
        {
            ct.ThrowIfCancellationRequested();
            var strategy = _registry.GetStrategy(descriptor.BackendId);
            if (strategy is null) continue;

            try
            {
                var capacity = await strategy.GetAvailableCapacityAsync(ct).ConfigureAwait(false);
                if (capacity.HasValue)
                {
                    total += capacity.Value;
                    hasCapacity = true;
                }
            }
            catch
            {
                // Skip backends that fail capacity check
            }
        }

        return hasCapacity ? total : null;
    }

    #endregion

    #region Resolution Helpers

    /// <summary>
    /// Resolves a storage address with optional placement hints. When hints specify a
    /// preferred backend, routes directly to that backend. Otherwise uses standard routing.
    /// </summary>
    private IStorageStrategy ResolveWithHints(StorageAddress address, StoragePlacementHints? hints)
    {
        // Direct backend override from hints
        if (hints?.PreferredBackendId is not null)
        {
            var strategy = _registry.GetStrategy(hints.PreferredBackendId);
            if (strategy is not null) return strategy;
            throw new PlacementFailedException(
                $"Preferred backend '{hints.PreferredBackendId}' is not registered.",
                hints);
        }

        // Hint-based backend selection (tier, tags, encryption, versioning)
        if (hints is not null && HasPlacementCriteria(hints))
        {
            var candidates = _registry.All.ToList();

            if (hints.PreferredTier.HasValue)
                candidates = candidates.Where(b => b.Tier == hints.PreferredTier.Value).ToList();

            if (hints.RequiredTags is not null)
                candidates = candidates.Where(b => hints.RequiredTags.All(t => b.Tags.Contains(t))).ToList();

            if (hints.PreferredRegion is not null)
                candidates = candidates.Where(b => string.Equals(b.Region, hints.PreferredRegion, StringComparison.OrdinalIgnoreCase)).ToList();

            if (hints.RequireEncryption)
                candidates = candidates.Where(b => b.Capabilities.SupportsEncryption).ToList();

            if (hints.RequireVersioning)
                candidates = candidates.Where(b => b.Capabilities.SupportsVersioning).ToList();

            if (candidates.Count > 0)
            {
                // Pick highest priority (lowest value) among candidates
                var best = candidates.OrderBy(c => c.Priority).First();
                var strategy = _registry.GetStrategy(best.BackendId);
                if (strategy is not null) return strategy;
            }

            // No candidate matches hints -- fall through to standard routing
        }

        return ResolveOrThrow(address);
    }

    /// <summary>
    /// Resolves a storage address to a backend strategy, throwing if no backend matches.
    /// If the primary backend is unhealthy, attempts to find a fallback in the same tier.
    /// </summary>
    private IStorageStrategy ResolveOrThrow(StorageAddress address)
    {
        var strategy = _router.Resolve(address, _registry);
        if (strategy is not null)
        {
            // Check health cache for failover
            if (_healthCache.TryGetValue(strategy.StrategyId, out var status) &&
                status == HealthStatus.Unhealthy)
            {
                // Try fallback: next priority backend in same tier
                var fallback = TryFindFallback(strategy);
                if (fallback is not null) return fallback;
            }
            return strategy;
        }

        throw new BackendNotFoundException(
            $"No backend found for address: {address}",
            backendId: null,
            address: address);
    }

    /// <summary>
    /// Attempts to find a healthy fallback backend in the same tier as the given strategy.
    /// </summary>
    private IStorageStrategy? TryFindFallback(IStorageStrategy unhealthy)
    {
        var descriptor = _registry.GetById(unhealthy.StrategyId);
        if (descriptor is null) return null;

        var sameTier = _registry.FindByTier(descriptor.Tier)
            .Where(b => b.BackendId != unhealthy.StrategyId)
            .OrderBy(b => b.Priority);

        foreach (var candidate in sameTier)
        {
            // Skip candidates marked unhealthy in cache
            if (_healthCache.TryGetValue(candidate.BackendId, out var status) &&
                status == HealthStatus.Unhealthy)
            {
                continue;
            }

            var fallbackStrategy = _registry.GetStrategy(candidate.BackendId);
            if (fallbackStrategy is not null) return fallbackStrategy;
        }

        return null;
    }

    /// <summary>
    /// Checks whether placement hints contain any actual criteria beyond defaults.
    /// </summary>
    private static bool HasPlacementCriteria(StoragePlacementHints hints)
    {
        return hints.PreferredTier.HasValue
            || hints.RequiredTags is not null
            || hints.PreferredRegion is not null
            || hints.RequireEncryption
            || hints.RequireVersioning;
    }

    /// <summary>
    /// Wraps a backend exception with fabric context for better diagnostics.
    /// </summary>
    private static BackendUnavailableException WrapBackendException(
        Exception inner, string backendId, StorageAddress address, string operation)
    {
        return new BackendUnavailableException(
            $"Backend '{backendId}' failed during {operation} for address '{address}': {inner.Message}",
            backendId, inner);
    }

    #endregion

    #region Metadata

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FabricPlugin"] = true;
        metadata["RegisteredBackends"] = _registry.Count;
        metadata["TotalRouteMappings"] = _router.TotalMappings;
        metadata["DefaultBackend"] = _router.DefaultBackendId ?? "none";
        return metadata;
    }

    #endregion

    #region IDisposable

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var sub in _subscriptions)
            {
                sub.Dispose();
            }
            _subscriptions.Clear();
        }
        base.Dispose(disposing);
    }

    #endregion
}
