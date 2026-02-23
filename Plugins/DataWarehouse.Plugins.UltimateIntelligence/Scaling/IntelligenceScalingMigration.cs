// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Scaling;

namespace DataWarehouse.Plugins.UltimateIntelligence.Scaling;

/// <summary>
/// Migrates the UltimateIntelligence plugin's entity stores from unbounded
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
/// to <see cref="BoundedCache{TKey,TValue}"/> using <see cref="PluginScalingMigrationHelper"/>.
/// </summary>
/// <remarks>
/// <para>
/// The Intelligence plugin manages several high-cardinality entity stores:
/// <list type="bullet">
/// <item><description><b>Knowledge stores</b>: facts, rules, ontology nodes (100K entries)</description></item>
/// <item><description><b>Model caches</b>: trained model metadata, inference results (50K entries)</description></item>
/// <item><description><b>Provider caches</b>: external AI provider responses, embeddings (50K entries)</description></item>
/// <item><description><b>Vector operation caches</b>: similarity search results, nearest-neighbor lookups (50K entries)</description></item>
/// </list>
/// </para>
/// <para>
/// All caches use LRU eviction with write-through persistence. The knowledge store uses
/// <see cref="PluginCategory.DataIntelligence"/> sizing (100K default); other caches use
/// 50K entries to balance memory pressure with cache effectiveness.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-13: Intelligence plugin BoundedCache migration")]
public sealed class IntelligenceScalingMigration : IDisposable
{
    private const string PluginId = "UltimateIntelligence";

    private readonly BoundedCache<string, byte[]> _knowledgeStore;
    private readonly BoundedCache<string, byte[]> _modelCache;
    private readonly BoundedCache<string, byte[]> _providerCache;
    private readonly BoundedCache<string, double[]> _vectorCache;
    private bool _disposed;

    /// <summary>
    /// Initializes the Intelligence scaling migration with pre-configured bounded caches.
    /// </summary>
    /// <param name="limits">Optional scaling limits override. When <c>null</c>, defaults are used.</param>
    public IntelligenceScalingMigration(ScalingLimits? limits = null)
    {
        int knowledgeMax = limits?.MaxCacheEntries ?? PluginScalingMigrationHelper.GetDefaultMaxEntries(PluginCategory.DataIntelligence);
        int modelMax = limits?.MaxCacheEntries ?? 50_000;
        int providerMax = limits?.MaxCacheEntries ?? 50_000;
        int vectorMax = limits?.MaxCacheEntries ?? 50_000;

        _knowledgeStore = PluginScalingMigrationHelper.CreateBoundedEntityStore<string, byte[]>(
            PluginId, "knowledge", knowledgeMax);

        _modelCache = PluginScalingMigrationHelper.CreateBoundedEntityStore<string, byte[]>(
            PluginId, "models", modelMax);

        _providerCache = PluginScalingMigrationHelper.CreateBoundedEntityStore<string, byte[]>(
            PluginId, "providers", providerMax);

        _vectorCache = PluginScalingMigrationHelper.CreateBoundedEntityStore<string, double[]>(
            PluginId, "vectors", vectorMax);
    }

    /// <summary>Gets the bounded knowledge entity store (facts, rules, ontology).</summary>
    public BoundedCache<string, byte[]> KnowledgeStore => _knowledgeStore;

    /// <summary>Gets the bounded model metadata cache.</summary>
    public BoundedCache<string, byte[]> ModelCache => _modelCache;

    /// <summary>Gets the bounded AI provider response cache.</summary>
    public BoundedCache<string, byte[]> ProviderCache => _providerCache;

    /// <summary>Gets the bounded vector operation cache.</summary>
    public BoundedCache<string, double[]> VectorCache => _vectorCache;

    /// <summary>
    /// Returns scaling metrics for all Intelligence entity stores.
    /// </summary>
    /// <returns>A dictionary of metric names to current values.</returns>
    public IReadOnlyDictionary<string, object> GetScalingMetrics()
    {
        return new Dictionary<string, object>
        {
            ["knowledge.count"] = _knowledgeStore.Count,
            ["knowledge.memoryBytes"] = _knowledgeStore.EstimatedMemoryBytes,
            ["models.count"] = _modelCache.Count,
            ["models.memoryBytes"] = _modelCache.EstimatedMemoryBytes,
            ["providers.count"] = _providerCache.Count,
            ["providers.memoryBytes"] = _providerCache.EstimatedMemoryBytes,
            ["vectors.count"] = _vectorCache.Count,
            ["vectors.memoryBytes"] = _vectorCache.EstimatedMemoryBytes
        };
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _knowledgeStore.Dispose();
        _modelCache.Dispose();
        _providerCache.Dispose();
        _vectorCache.Dispose();
    }
}
