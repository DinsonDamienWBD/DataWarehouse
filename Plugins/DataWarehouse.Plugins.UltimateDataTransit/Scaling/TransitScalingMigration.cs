// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Scaling;

namespace DataWarehouse.Plugins.UltimateDataTransit.Scaling;

/// <summary>
/// Migrates the UltimateDataTransit plugin's entity stores from unbounded
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
/// to <see cref="BoundedCache{TKey,TValue}"/> using <see cref="PluginScalingMigrationHelper"/>.
/// </summary>
/// <remarks>
/// <para>
/// The DataTransit plugin manages several entity stores for data transfer operations:
/// <list type="bullet">
/// <item><description><b>Transfer state</b>: active and completed transfer metadata (50K entries)</description></item>
/// <item><description><b>Route caches</b>: resolved routing paths and endpoint metadata (50K entries)</description></item>
/// <item><description><b>QoS state</b>: quality-of-service metrics per transfer channel (10K entries)</description></item>
/// <item><description><b>Cost routing tables</b>: cached cost calculations for multi-path routing (10K entries)</description></item>
/// </list>
/// </para>
/// <para>
/// All caches use LRU eviction with write-through persistence via
/// <see cref="DataWarehouse.SDK.Contracts.Persistence.IPersistentBackingStore"/>.
/// Transfer state uses <see cref="PluginCategory.General"/> sizing (50K);
/// QoS and cost caches use smaller allocations (10K) due to their update-heavy nature.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-13: DataTransit plugin BoundedCache migration")]
public sealed class TransitScalingMigration : IDisposable
{
    private const string PluginId = "UltimateDataTransit";

    private readonly BoundedCache<string, byte[]> _transferState;
    private readonly BoundedCache<string, byte[]> _routeCache;
    private readonly BoundedCache<string, byte[]> _qosState;
    private readonly BoundedCache<string, byte[]> _costRoutingTable;
    private bool _disposed;

    /// <summary>
    /// Initializes the DataTransit scaling migration with pre-configured bounded caches.
    /// </summary>
    /// <param name="limits">Optional scaling limits override. When <c>null</c>, defaults are used.</param>
    public TransitScalingMigration(ScalingLimits? limits = null)
    {
        int transferMax = limits?.MaxCacheEntries ?? PluginScalingMigrationHelper.GetDefaultMaxEntries(PluginCategory.General);
        int routeMax = limits?.MaxCacheEntries ?? 50_000;
        int qosMax = limits?.MaxCacheEntries ?? 10_000;
        int costMax = limits?.MaxCacheEntries ?? 10_000;

        _transferState = PluginScalingMigrationHelper.CreateBoundedEntityStore<string, byte[]>(
            PluginId, "transfers", transferMax);

        _routeCache = PluginScalingMigrationHelper.CreateBoundedEntityStore<string, byte[]>(
            PluginId, "routes", routeMax);

        _qosState = PluginScalingMigrationHelper.CreateBoundedEntityStore<string, byte[]>(
            PluginId, "qos", qosMax);

        _costRoutingTable = PluginScalingMigrationHelper.CreateBoundedEntityStore<string, byte[]>(
            PluginId, "costRouting", costMax);
    }

    /// <summary>Gets the bounded transfer state store.</summary>
    public BoundedCache<string, byte[]> TransferState => _transferState;

    /// <summary>Gets the bounded route cache.</summary>
    public BoundedCache<string, byte[]> RouteCache => _routeCache;

    /// <summary>Gets the bounded QoS state store.</summary>
    public BoundedCache<string, byte[]> QosState => _qosState;

    /// <summary>Gets the bounded cost routing table.</summary>
    public BoundedCache<string, byte[]> CostRoutingTable => _costRoutingTable;

    /// <summary>
    /// Returns scaling metrics for all DataTransit entity stores.
    /// </summary>
    /// <returns>A dictionary of metric names to current values.</returns>
    public IReadOnlyDictionary<string, object> GetScalingMetrics()
    {
        return new Dictionary<string, object>
        {
            ["transfers.count"] = _transferState.Count,
            ["transfers.memoryBytes"] = _transferState.EstimatedMemoryBytes,
            ["routes.count"] = _routeCache.Count,
            ["routes.memoryBytes"] = _routeCache.EstimatedMemoryBytes,
            ["qos.count"] = _qosState.Count,
            ["qos.memoryBytes"] = _qosState.EstimatedMemoryBytes,
            ["costRouting.count"] = _costRoutingTable.Count,
            ["costRouting.memoryBytes"] = _costRoutingTable.EstimatedMemoryBytes
        };
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transferState.Dispose();
        _routeCache.Dispose();
        _qosState.Dispose();
        _costRoutingTable.Dispose();
    }
}
