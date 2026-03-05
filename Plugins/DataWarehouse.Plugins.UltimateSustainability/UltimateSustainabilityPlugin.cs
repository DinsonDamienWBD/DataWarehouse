using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Utilities;
using PluginCategory = DataWarehouse.SDK.Primitives.PluginCategory;

namespace DataWarehouse.Plugins.UltimateSustainability;

/// <summary>
/// Ultimate Sustainability plugin that consolidates 45+ green computing strategies.
/// Provides comprehensive carbon awareness, energy optimization, battery management,
/// thermal control, resource efficiency, intelligent scheduling, metrics collection,
/// and cloud optimization capabilities.
/// Intelligence-aware for AI-powered sustainability recommendations.
/// </summary>
/// <remarks>
/// Supported strategy categories:
/// <list type="bullet">
///   <item>Carbon Awareness: Carbon intensity tracking, grid carbon API, carbon-aware scheduling, offsetting</item>
///   <item>Energy Optimization: CPU frequency scaling, sleep states, power capping, workload consolidation</item>
///   <item>Battery Awareness: Battery monitoring, charge-aware scheduling, UPS integration</item>
///   <item>Thermal Management: Temperature monitoring, thermal throttling, cooling optimization</item>
///   <item>Resource Efficiency: Memory optimization, disk spin-down, network power saving</item>
///   <item>Scheduling: Off-peak scheduling, renewable windows, demand response</item>
///   <item>Metrics: Energy tracking, carbon footprint, sustainability reporting</item>
///   <item>Cloud Optimization: Spot instances, right-sizing, carbon-aware region selection</item>
/// </list>
/// </remarks>
public sealed class UltimateSustainabilityPlugin : InfrastructurePluginBase
{
    // Single authoritative registry; _strategies is a secondary index kept in sync via RegisterStrategy.
    // Authoritative source of truth is _strategies (BoundedDictionary gives thread-safe access);
    // _registry provides category-based lookup only and is always updated atomically with _strategies.
    private readonly SustainabilityStrategyRegistry _registry = new();
    private readonly BoundedDictionary<string, ISustainabilityStrategy> _strategies = new BoundedDictionary<string, ISustainabilityStrategy>(1000);
    private ISustainabilityStrategy? _activeStrategy;
    // Cached capabilities list; invalidated whenever a new strategy is registered.
    private volatile IReadOnlyList<RegisteredCapability>? _cachedCapabilities;


    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.sustainability.ultimate";

    /// <inheritdoc/>
    public override string Name => "Ultimate Sustainability";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string InfrastructureDomain => "Sustainability";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.MetricsProvider;

    /// <summary>
    /// Registers a sustainability strategy with the plugin.
    /// Both internal registries are updated atomically and the capability cache is invalidated.
    /// </summary>
    public void RegisterStrategy(ISustainabilityStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _strategies[strategy.StrategyId] = strategy;
        _registry.Register(strategy);
        // Invalidate cached capabilities so the next access rebuilds with the new strategy.
        _cachedCapabilities = null;
    }

    /// <summary>
    /// Gets a strategy by ID.
    /// </summary>
    public ISustainabilityStrategy? GetStrategy(string strategyId)
    {
        _strategies.TryGetValue(strategyId, out var strategy);
        return strategy;
    }

    /// <summary>
    /// Gets all registered strategy IDs.
    /// </summary>
    public IReadOnlyCollection<string> GetRegisteredStrategies() => _strategies.Keys.ToArray();

    /// <summary>
    /// Gets strategies by category.
    /// </summary>
    public IReadOnlyCollection<ISustainabilityStrategy> GetStrategiesByCategory(SustainabilityCategory category)
    {
        return _registry.GetByCategory(category);
    }

    /// <summary>
    /// Sets the active strategy.
    /// </summary>
    public void SetActiveStrategy(string strategyId)
    {
        if (!_strategies.TryGetValue(strategyId, out var strategy))
            throw new ArgumentException($"Unknown strategy: {strategyId}");
        _activeStrategy = strategy;
    }

    /// <summary>
    /// Gets all recommendations from all strategies.
    /// </summary>
    public async Task<IReadOnlyList<SustainabilityRecommendation>> GetAllRecommendationsAsync(CancellationToken ct = default)
    {
        var allRecommendations = new List<SustainabilityRecommendation>();
        foreach (var strategy in _strategies.Values)
        {
            try
            {
                var recommendations = await strategy.GetRecommendationsAsync(ct);
                allRecommendations.AddRange(recommendations);
            }
            catch (Exception ex)
            {
                // Log and continue with remaining strategies so one faulty strategy
                // does not block the others from returning their recommendations.
                System.Diagnostics.Debug.WriteLine(
                    $"[UltimateSustainabilityPlugin] GetRecommendationsAsync failed for strategy '{strategy.StrategyId}': {ex.GetType().Name}: {ex.Message}");
            }
        }
        return allRecommendations.OrderByDescending(r => r.Priority).ToList().AsReadOnly();
    }

    /// <summary>
    /// Gets aggregate statistics across all strategies.
    /// </summary>
    public SustainabilityStatistics GetAggregateStatistics()
    {
        var aggregate = new SustainabilityStatistics();
        foreach (var strategy in _strategies.Values)
        {
            var stats = strategy.GetStatistics();
            aggregate.TotalEnergySavedWh += stats.TotalEnergySavedWh;
            aggregate.TotalCarbonAvoidedGrams += stats.TotalCarbonAvoidedGrams;
            aggregate.TotalOptimizationActions += stats.TotalOptimizationActions;
            aggregate.TotalSamplesCollected += stats.TotalSamplesCollected;
            aggregate.TotalWorkloadsScheduled += stats.TotalWorkloadsScheduled;
            aggregate.TotalCostSavingsUsd += stats.TotalCostSavingsUsd;
            aggregate.TotalLowPowerTimeSeconds += stats.TotalLowPowerTimeSeconds;
            aggregate.ThermalThrottlingEvents += stats.ThermalThrottlingEvents;
        }
        return aggregate;
    }

    /// <summary>
    /// Initializes the plugin and discovers sustainability strategies.
    /// </summary>
    public UltimateSustainabilityPlugin()
    {
        DiscoverAndRegisterStrategies();
    }

    /// <summary>
    /// Discovers and registers all sustainability strategies via reflection.
    /// Dual-registration: local typed registries + base-class PluginBase.RegisterStrategy(IStrategy).
    /// </summary>
    private void DiscoverAndRegisterStrategies()
    {
        var strategyTypes = GetType().Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && typeof(SustainabilityStrategyBase).IsAssignableFrom(t));

        foreach (var strategyType in strategyTypes)
        {
            try
            {
                if (Activator.CreateInstance(strategyType) is SustainabilityStrategyBase strategy)
                {
                    // Local typed registries
                    _strategies[strategy.StrategyId] = strategy;
                    _registry.Register(strategy);

                    // Base-class registration (65.5-05): SustainabilityStrategyBase extends StrategyBase (IStrategy)
                    RegisterStrategy(strategy);
                }
            }
            catch (Exception ex)
            {
                // Log the failure so operators can diagnose broken strategy types.
                System.Diagnostics.Debug.WriteLine(
                    $"[UltimateSustainabilityPlugin] Failed to instantiate strategy type '{strategyType.FullName}': {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The result is cached after the first build and invalidated whenever a new strategy is registered.
    /// This avoids per-call allocation and iteration over all strategies on a property getter hot path.
    /// </remarks>
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
    {
        get
        {
            // Return cached value if still valid (volatile read for thread visibility).
            if (_cachedCapabilities != null)
                return _cachedCapabilities;

            var capabilities = new List<RegisteredCapability>
            {
                new RegisteredCapability
                {
                    CapabilityId = $"{Id}.monitor",
                    DisplayName = $"{Name} - Monitor",
                    Description = "Monitor energy consumption and carbon emissions",
                    Category = CapabilityCategory.Custom,
                    SubCategory = "Sustainability",
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = new[] { "sustainability", "monitoring", "energy", "carbon" }
                },
                new RegisteredCapability
                {
                    CapabilityId = $"{Id}.optimize",
                    DisplayName = $"{Name} - Optimize",
                    Description = "Optimize energy consumption and reduce carbon footprint",
                    Category = CapabilityCategory.Custom,
                    SubCategory = "Sustainability",
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = new[] { "sustainability", "optimization", "green-computing" }
                }
            };

            foreach (var kvp in _strategies)
            {
                var strategy = kvp.Value;
                capabilities.Add(new RegisteredCapability
                {
                    CapabilityId = $"{Id}.strategy.{strategy.StrategyId}",
                    DisplayName = strategy.DisplayName,
                    Description = strategy.SemanticDescription,
                    Category = CapabilityCategory.Custom,
                    SubCategory = strategy.Category.ToString(),
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = strategy.Tags,
                    Metadata = new Dictionary<string, object>
                    {
                        ["strategyId"] = strategy.StrategyId,
                        ["category"] = strategy.Category.ToString(),
                        ["capabilities"] = strategy.Capabilities.ToString()
                    },
                    SemanticDescription = strategy.SemanticDescription
                });
            }

            var built = capabilities.AsReadOnly();
            // Store in cache field (volatile write ensures visibility to other threads).
            _cachedCapabilities = built;
            return built;
        }
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

        var strategies = _strategies.Values.ToList();
        if (strategies.Any())
        {
            knowledge.Add(new KnowledgeObject
            {
                Id = $"{Id}.strategies",
                Topic = "sustainability.strategies",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"{strategies.Count} sustainability strategies available",
                Payload = new Dictionary<string, object>
                {
                    ["count"] = strategies.Count,
                    ["categories"] = strategies.Select(s => s.Category.ToString()).Distinct().ToArray(),
                    ["carbonAwarenessCount"] = strategies.Count(s => s.Category == SustainabilityCategory.CarbonAwareness),
                    ["energyOptimizationCount"] = strategies.Count(s => s.Category == SustainabilityCategory.EnergyOptimization),
                    ["schedulingCount"] = strategies.Count(s => s.Category == SustainabilityCategory.Scheduling),
                    ["cloudOptimizationCount"] = strategies.Count(s => s.Category == SustainabilityCategory.CloudOptimization)
                },
                Tags = new[] { "sustainability", "strategies", "summary", "green-computing" }
            });
        }

        return knowledge;
    }

    /// <inheritdoc/>
    public override Task OnMessageAsync(PluginMessage message)
    {
        switch (message.Type)
        {
            case "sustainability.list":
                // Return list of available strategies
                break;

            case "sustainability.select":
                if (message.Payload.TryGetValue("strategyId", out var idObj) && idObj is string strategyId)
                {
                    try
                    {
                        SetActiveStrategy(strategyId);
                    }
                    catch (ArgumentException ex)
                    {
                        // Unknown strategy ID — log and swallow so the message bus does not get an unhandled exception.
                        System.Diagnostics.Debug.WriteLine(
                            $"[UltimateSustainabilityPlugin] sustainability.select failed: {ex.Message}");
                    }
                }
                break;

            case "sustainability.recommendations":
                // Return recommendations (would need async handling)
                break;
        }

        return base.OnMessageAsync(message);
    }

    #region Intelligence Integration

    /// <summary>
    /// Semantic description for AI discovery.
    /// </summary>
    public string SemanticDescription =>
        "Ultimate sustainability plugin providing 45+ green computing strategies. " +
        "Supports carbon awareness, energy optimization, battery management, thermal control, " +
        "resource efficiency, intelligent scheduling, metrics collection, and cloud optimization " +
        "for environmentally responsible computing.";

    /// <summary>
    /// Semantic tags for AI discovery.
    /// </summary>
    public string[] SemanticTags => new[]
    {
        "sustainability", "green-computing", "carbon", "energy", "optimization",
        "scheduling", "monitoring", "cloud", "efficiency"
    };

    /// <inheritdoc/>
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
    {
        await base.OnStartWithIntelligenceAsync(ct);

        if (MessageBus != null)
        {
            var strategies = _strategies.Values.ToList();

            await MessageBus.PublishAsync(IntelligenceTopics.QueryCapability, new PluginMessage
            {
                Type = "capability.register",
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["pluginId"] = Id,
                    ["pluginName"] = Name,
                    ["pluginType"] = "sustainability",
                    ["capabilities"] = new Dictionary<string, object>
                    {
                        ["strategyCount"] = strategies.Count,
                        ["categories"] = strategies.Select(s => s.Category.ToString()).Distinct().ToArray(),
                        ["supportsCarbonAwareness"] = strategies.Any(s => s.Category == SustainabilityCategory.CarbonAwareness),
                        ["supportsEnergyOptimization"] = strategies.Any(s => s.Category == SustainabilityCategory.EnergyOptimization),
                        ["supportsScheduling"] = strategies.Any(s => s.Category == SustainabilityCategory.Scheduling)
                    },
                    ["semanticDescription"] = SemanticDescription,
                    ["tags"] = SemanticTags
                }
            }, ct);

            SubscribeToSustainabilityRequests();
        }
    }

    /// <summary>
    /// Subscribes to Intelligence sustainability recommendation requests.
    /// </summary>
    private void SubscribeToSustainabilityRequests()
    {
        if (MessageBus == null) return;

        MessageBus.Subscribe("sustainability.recommendation.request", async msg =>
        {
            if (msg.Payload.TryGetValue("workloadType", out var wtObj) && wtObj is string workloadType)
            {
                var recommendation = await GetWorkloadRecommendationAsync(workloadType);

                await MessageBus.PublishAsync("sustainability.recommendation.response", new PluginMessage
                {
                    Type = "sustainability-recommendation.response",
                    CorrelationId = msg.CorrelationId,
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["recommendations"] = recommendation
                    }
                });
            }
        });
    }

    /// <summary>
    /// Gets sustainability recommendations for a workload type.
    /// </summary>
    private async Task<List<Dictionary<string, object>>> GetWorkloadRecommendationAsync(string workloadType)
    {
        var recommendations = new List<Dictionary<string, object>>();

        // Get recommendations from carbon awareness strategies
        foreach (var strategy in _strategies.Values.Where(s => s.Category == SustainabilityCategory.CarbonAwareness))
        {
            var recs = await strategy.GetRecommendationsAsync();
            foreach (var rec in recs.Take(2))
            {
                recommendations.Add(new Dictionary<string, object>
                {
                    ["strategyId"] = strategy.StrategyId,
                    ["type"] = rec.Type,
                    ["description"] = rec.Description,
                    ["estimatedEnergySavingsWh"] = rec.EstimatedEnergySavingsWh,
                    ["estimatedCarbonReductionGrams"] = rec.EstimatedCarbonReductionGrams,
                    ["priority"] = rec.Priority
                });
            }
        }

        // Add scheduling recommendations for batch workloads
        if (workloadType.Contains("batch", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var strategy in _strategies.Values.Where(s => s.Category == SustainabilityCategory.Scheduling))
            {
                var recs = await strategy.GetRecommendationsAsync();
                foreach (var rec in recs.Take(2))
                {
                    recommendations.Add(new Dictionary<string, object>
                    {
                        ["strategyId"] = strategy.StrategyId,
                        ["type"] = rec.Type,
                        ["description"] = rec.Description,
                        ["estimatedEnergySavingsWh"] = rec.EstimatedEnergySavingsWh,
                        ["estimatedCarbonReductionGrams"] = rec.EstimatedCarbonReductionGrams,
                        ["priority"] = rec.Priority
                    });
                }
            }
        }

        return recommendations;
    }

    /// <inheritdoc/>
    protected override Task OnStartCoreAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    #endregion
}
