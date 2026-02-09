using System.Collections.Concurrent;
using System.Text.Json;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory;

namespace DataWarehouse.Plugins.UltimateIntelligence;

/// <summary>
/// Ultimate Intelligence plugin (T90) that consolidates AI providers, vector stores,
/// knowledge graphs, and intelligent features as strategies.
/// Provides unified access to multiple AI services, vector databases, and graph databases
/// with automatic selection, fallback, and real-time performance tracking.
/// </summary>
/// <remarks>
/// Supported strategy categories:
/// <list type="bullet">
///   <item>AI Providers: OpenAI, Claude, Ollama, Azure OpenAI, AWS Bedrock, HuggingFace</item>
///   <item>Vector Stores: Pinecone, Weaviate, Milvus, Qdrant, Chroma, PgVector</item>
///   <item>Knowledge Graphs: Neo4j, ArangoDB, Neptune, TigerGraph</item>
///   <item>Features: Semantic Search, Content Classification, Anomaly Detection, Access Prediction, Failure Prediction, Psychometric Indexing</item>
/// </list>
/// </remarks>
public sealed class UltimateIntelligencePlugin : PipelinePluginBase
{
    private readonly ConcurrentDictionary<string, IIntelligenceStrategy> _allStrategies = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<IntelligenceStrategyCategory, ConcurrentDictionary<string, IIntelligenceStrategy>> _strategiesByCategory = new();

    // Active strategies by category
    private IIntelligenceStrategy? _activeAIProvider;
    private IIntelligenceStrategy? _activeVectorStore;
    private IIntelligenceStrategy? _activeKnowledgeGraph;
    private IIntelligenceStrategy? _activeFeature;
    private IIntelligenceStrategy? _activeLongTermMemory;

    // Tiered memory system
    private TieredMemoryStrategy? _tieredMemoryStrategy;


    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.intelligence.ultimate";

    /// <inheritdoc/>
    public override string Name => "Ultimate Intelligence";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string SubCategory => "AI";

    /// <inheritdoc/>
    public override int DefaultOrder => 30;

    /// <inheritdoc/>
    public override bool AllowBypass => true;

    /// <inheritdoc/>
    public override int QualityLevel => 95;

    /// <summary>
    /// Initializes the plugin and discovers intelligence strategies.
    /// </summary>
    public UltimateIntelligencePlugin()
    {
        // Initialize category dictionaries
        foreach (IntelligenceStrategyCategory category in Enum.GetValues<IntelligenceStrategyCategory>())
        {
            _strategiesByCategory[category] = new ConcurrentDictionary<string, IIntelligenceStrategy>(StringComparer.OrdinalIgnoreCase);
        }

        DiscoverAndRegisterStrategies();

        // Initialize tiered memory strategy
        _tieredMemoryStrategy = GetStrategy<TieredMemoryStrategy>("ltm-tiered-hierarchical");
    }

    /// <summary>
    /// Sets the active long-term memory strategy.
    /// </summary>
    /// <param name="strategyId">The strategy ID to activate.</param>
    public void SetActiveLongTermMemory(string strategyId)
    {
        var strategy = GetStrategy(strategyId);
        if (strategy?.Category != IntelligenceStrategyCategory.LongTermMemory)
            throw new ArgumentException($"Strategy '{strategyId}' is not a long-term memory strategy");
        _activeLongTermMemory = strategy;
    }

    /// <summary>
    /// Gets the active long-term memory strategy.
    /// </summary>
    public LongTermMemoryStrategyBase? GetActiveLongTermMemory() => _activeLongTermMemory as LongTermMemoryStrategyBase;

    /// <summary>
    /// Gets the tiered memory strategy.
    /// </summary>
    public TieredMemoryStrategy? GetTieredMemoryStrategy() => _tieredMemoryStrategy;

    /// <summary>
    /// Registers an intelligence strategy with the plugin.
    /// </summary>
    /// <param name="strategy">The strategy to register.</param>
    public void RegisterStrategy(IIntelligenceStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _allStrategies[strategy.StrategyId] = strategy;
        _strategiesByCategory[strategy.Category][strategy.StrategyId] = strategy;
    }

    /// <summary>
    /// Gets a strategy by ID.
    /// </summary>
    /// <param name="strategyId">The strategy ID.</param>
    /// <returns>The matching strategy, or null if not found.</returns>
    public IIntelligenceStrategy? GetStrategy(string strategyId)
    {
        _allStrategies.TryGetValue(strategyId, out var strategy);
        return strategy;
    }

    /// <summary>
    /// Gets a typed strategy by ID.
    /// </summary>
    /// <typeparam name="T">The expected strategy type.</typeparam>
    /// <param name="strategyId">The strategy ID.</param>
    /// <returns>The matching strategy, or null if not found or wrong type.</returns>
    public T? GetStrategy<T>(string strategyId) where T : class, IIntelligenceStrategy
    {
        return GetStrategy(strategyId) as T;
    }

    /// <summary>
    /// Gets all strategies in a category.
    /// </summary>
    /// <param name="category">The strategy category.</param>
    /// <returns>All strategies in the category.</returns>
    public IReadOnlyCollection<IIntelligenceStrategy> GetStrategiesByCategory(IntelligenceStrategyCategory category)
    {
        return _strategiesByCategory.TryGetValue(category, out var strategies)
            ? strategies.Values.ToArray()
            : Array.Empty<IIntelligenceStrategy>();
    }

    /// <summary>
    /// Gets all registered strategy IDs.
    /// </summary>
    public IReadOnlyCollection<string> GetRegisteredStrategyIds() => _allStrategies.Keys.ToArray();

    /// <summary>
    /// Gets strategies that support specific capabilities.
    /// </summary>
    /// <param name="capabilities">Required capabilities.</param>
    /// <returns>Strategies that support all specified capabilities.</returns>
    public IEnumerable<IIntelligenceStrategy> GetStrategiesByCapabilities(IntelligenceCapabilities capabilities)
    {
        return _allStrategies.Values.Where(s => (s.Info.Capabilities & capabilities) == capabilities);
    }

    /// <summary>
    /// Sets the active AI provider strategy.
    /// </summary>
    /// <param name="strategyId">The strategy ID to activate.</param>
    public void SetActiveAIProvider(string strategyId)
    {
        var strategy = GetStrategy(strategyId);
        if (strategy?.Category != IntelligenceStrategyCategory.AIProvider)
            throw new ArgumentException($"Strategy '{strategyId}' is not an AI provider");
        _activeAIProvider = strategy;
    }

    /// <summary>
    /// Sets the active vector store strategy.
    /// </summary>
    /// <param name="strategyId">The strategy ID to activate.</param>
    public void SetActiveVectorStore(string strategyId)
    {
        var strategy = GetStrategy(strategyId);
        if (strategy?.Category != IntelligenceStrategyCategory.VectorStore)
            throw new ArgumentException($"Strategy '{strategyId}' is not a vector store");
        _activeVectorStore = strategy;
    }

    /// <summary>
    /// Sets the active knowledge graph strategy.
    /// </summary>
    /// <param name="strategyId">The strategy ID to activate.</param>
    public void SetActiveKnowledgeGraph(string strategyId)
    {
        var strategy = GetStrategy(strategyId);
        if (strategy?.Category != IntelligenceStrategyCategory.KnowledgeGraph)
            throw new ArgumentException($"Strategy '{strategyId}' is not a knowledge graph");
        _activeKnowledgeGraph = strategy;
    }

    /// <summary>
    /// Sets the active feature strategy.
    /// </summary>
    /// <param name="strategyId">The strategy ID to activate.</param>
    public void SetActiveFeature(string strategyId)
    {
        var strategy = GetStrategy(strategyId);
        if (strategy?.Category != IntelligenceStrategyCategory.Feature)
            throw new ArgumentException($"Strategy '{strategyId}' is not a feature");
        _activeFeature = strategy;
    }

    /// <summary>
    /// Gets the active AI provider.
    /// </summary>
    public IAIProvider? GetActiveAIProvider() => _activeAIProvider as IAIProvider;

    /// <summary>
    /// Gets the active vector store.
    /// </summary>
    public IVectorStore? GetActiveVectorStore() => _activeVectorStore as IVectorStore;

    /// <summary>
    /// Gets the active knowledge graph.
    /// </summary>
    public IKnowledgeGraph? GetActiveKnowledgeGraph() => _activeKnowledgeGraph as IKnowledgeGraph;

    /// <summary>
    /// Gets the active feature strategy.
    /// </summary>
    public FeatureStrategyBase? GetActiveFeature() => _activeFeature as FeatureStrategyBase;

    /// <summary>
    /// Selects the best AI provider based on requirements.
    /// </summary>
    /// <param name="capabilities">Required capabilities.</param>
    /// <param name="preferLowCost">Prefer lower cost providers.</param>
    /// <param name="preferLowLatency">Prefer lower latency providers.</param>
    /// <returns>The recommended strategy.</returns>
    public IIntelligenceStrategy? SelectBestAIProvider(
        IntelligenceCapabilities capabilities = IntelligenceCapabilities.AllAIProvider,
        bool preferLowCost = false,
        bool preferLowLatency = false)
    {
        var providers = GetStrategiesByCategory(IntelligenceStrategyCategory.AIProvider)
            .Where(s => s.IsAvailable && (s.Info.Capabilities & capabilities) == capabilities);

        if (preferLowCost)
            providers = providers.OrderBy(s => s.Info.CostTier);
        else if (preferLowLatency)
            providers = providers.OrderBy(s => s.Info.LatencyTier);
        else
            providers = providers.OrderByDescending(s => s.Info.Capabilities.HasFlag(IntelligenceCapabilities.FunctionCalling) ? 1 : 0);

        return providers.FirstOrDefault();
    }

    /// <summary>
    /// Selects the best vector store based on requirements.
    /// </summary>
    /// <param name="preferLowCost">Prefer lower cost stores.</param>
    /// <param name="requireLocal">Require local/offline support.</param>
    /// <returns>The recommended strategy.</returns>
    public IIntelligenceStrategy? SelectBestVectorStore(bool preferLowCost = false, bool requireLocal = false)
    {
        var stores = GetStrategiesByCategory(IntelligenceStrategyCategory.VectorStore)
            .Where(s => s.IsAvailable)
            .Where(s => !requireLocal || s.Info.SupportsOfflineMode);

        if (preferLowCost)
            stores = stores.OrderBy(s => s.Info.CostTier);
        else
            stores = stores.OrderByDescending(s => s.Info.Capabilities.HasFlag(IntelligenceCapabilities.MetadataFiltering) ? 1 : 0);

        return stores.FirstOrDefault();
    }

    /// <summary>
    /// Configures a feature strategy with the active AI provider and vector store.
    /// </summary>
    /// <param name="feature">The feature to configure.</param>
    public void ConfigureFeature(FeatureStrategyBase feature)
    {
        ArgumentNullException.ThrowIfNull(feature);

        if (_activeAIProvider is IAIProvider aiProvider)
            feature.SetAIProvider(aiProvider);

        if (_activeVectorStore is IVectorStore vectorStore)
            feature.SetVectorStore(vectorStore);

        if (_activeKnowledgeGraph is IKnowledgeGraph graph)
            feature.SetKnowledgeGraph(graph);
    }

    /// <inheritdoc/>
    public override Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
    {
        // Intelligence plugin typically does not transform data directly
        // It provides AI capabilities to other plugins
        return input;
    }

    /// <inheritdoc/>
    public override Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
    {
        // Intelligence plugin typically does not transform data directly
        return stored;
    }

    /// <summary>
    /// Discovers and registers all intelligence strategies via reflection.
    /// </summary>
    private void DiscoverAndRegisterStrategies()
    {
        var strategyTypes = GetType().Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && typeof(IntelligenceStrategyBase).IsAssignableFrom(t));

        foreach (var strategyType in strategyTypes)
        {
            try
            {
                if (Activator.CreateInstance(strategyType) is IntelligenceStrategyBase strategy)
                {
                    RegisterStrategy(strategy);
                }
            }
            catch
            {
                // Strategy failed to instantiate, skip
            }
        }
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
    {
        get
        {
            var capabilities = new List<RegisteredCapability>();

            // Main plugin capabilities
            capabilities.Add(new RegisteredCapability
            {
                CapabilityId = $"{Id}.ai",
                DisplayName = $"{Name} - AI Services",
                Description = "Unified AI provider access with multiple backends",
                Category = SDK.Contracts.CapabilityCategory.AI,
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "ai", "intelligence", "ml", "llm" }
            });

            capabilities.Add(new RegisteredCapability
            {
                CapabilityId = $"{Id}.vector",
                DisplayName = $"{Name} - Vector Operations",
                Description = "Vector storage and similarity search",
                Category = SDK.Contracts.CapabilityCategory.AI,
                SubCategory = "Vector",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "vector", "embeddings", "similarity", "search" }
            });

            capabilities.Add(new RegisteredCapability
            {
                CapabilityId = $"{Id}.graph",
                DisplayName = $"{Name} - Knowledge Graph",
                Description = "Knowledge graph operations and traversal",
                Category = SDK.Contracts.CapabilityCategory.AI,
                SubCategory = "Graph",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "graph", "knowledge", "relationships", "traversal" }
            });

            // Add capabilities for each strategy
            foreach (var strategy in _allStrategies.Values)
            {
                var info = strategy.Info;
                var tags = new List<string> { "intelligence", "strategy", strategy.Category.ToString().ToLowerInvariant() };
                tags.AddRange(info.Tags);

                capabilities.Add(new RegisteredCapability
                {
                    CapabilityId = $"{Id}.strategy.{strategy.StrategyId}",
                    DisplayName = $"{strategy.StrategyName}",
                    Description = info.Description,
                    Category = SDK.Contracts.CapabilityCategory.AI,
                    SubCategory = strategy.Category.ToString(),
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = tags.ToArray(),
                    Priority = info.CostTier <= 2 ? 70 : 50,
                    Metadata = new Dictionary<string, object>
                    {
                        ["strategyId"] = strategy.StrategyId,
                        ["category"] = strategy.Category.ToString(),
                        ["provider"] = info.ProviderName,
                        ["capabilities"] = info.Capabilities.ToString(),
                        ["costTier"] = info.CostTier,
                        ["latencyTier"] = info.LatencyTier,
                        ["requiresNetwork"] = info.RequiresNetworkAccess,
                        ["supportsOffline"] = info.SupportsOfflineMode
                    },
                    SemanticDescription = $"Use {info.ProviderName} for {info.Description}"
                });
            }

            return capabilities;
        }
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

        // Add strategy summary knowledge
        var providers = GetStrategiesByCategory(IntelligenceStrategyCategory.AIProvider).ToList();
        var vectorStores = GetStrategiesByCategory(IntelligenceStrategyCategory.VectorStore).ToList();
        var graphs = GetStrategiesByCategory(IntelligenceStrategyCategory.KnowledgeGraph).ToList();
        var features = GetStrategiesByCategory(IntelligenceStrategyCategory.Feature).ToList();

        knowledge.Add(new KnowledgeObject
        {
            Id = $"{Id}.strategies.summary",
            Topic = "intelligence.strategies",
            SourcePluginId = Id,
            SourcePluginName = Name,
            KnowledgeType = "capability",
            Description = $"{_allStrategies.Count} intelligence strategies available across 4 categories",
            Payload = new Dictionary<string, object>
            {
                ["totalCount"] = _allStrategies.Count,
                ["aiProviderCount"] = providers.Count,
                ["vectorStoreCount"] = vectorStores.Count,
                ["knowledgeGraphCount"] = graphs.Count,
                ["featureCount"] = features.Count,
                ["aiProviders"] = providers.Select(s => s.Info.ProviderName).ToArray(),
                ["vectorStores"] = vectorStores.Select(s => s.Info.ProviderName).ToArray(),
                ["knowledgeGraphs"] = graphs.Select(s => s.Info.ProviderName).ToArray(),
                ["features"] = features.Select(s => s.StrategyName).ToArray()
            },
            Tags = new[] { "intelligence", "strategies", "summary", "ai", "vector", "graph" }
        });

        // Add individual strategy knowledge using GetStrategyKnowledge()
        foreach (var strategy in _allStrategies.Values)
        {
            if (strategy is IntelligenceStrategyBase strategyBase)
            {
                var strategyKnowledge = strategyBase.GetStrategyKnowledge();
                // Enrich with additional runtime information
                strategyKnowledge.Payload["isAvailable"] = strategy.IsAvailable;
                strategyKnowledge.Payload["configurationRequirements"] = strategy.Info.ConfigurationRequirements.Select(r => new
                {
                    r.Key,
                    r.Description,
                    r.Required,
                    r.DefaultValue
                }).ToArray();
                knowledge.Add(strategyKnowledge);
            }
        }

        return knowledge;
    }

    /// <inheritdoc/>
    public override async Task OnMessageAsync(PluginMessage message)
    {
        switch (message.Type)
        {
            case "intelligence.ultimate.list":
                // List all strategies or by category
                if (message.Payload.TryGetValue("category", out var catObj) && catObj is string catName)
                {
                    if (Enum.TryParse<IntelligenceStrategyCategory>(catName, true, out var category))
                    {
                        var strategies = GetStrategiesByCategory(category);
                        // Note: Reply mechanism would need to be handled by caller
                    }
                }
                break;

            case "intelligence.ultimate.select.provider":
                if (message.Payload.TryGetValue("strategyId", out var providerObj) && providerObj is string providerId)
                    SetActiveAIProvider(providerId);
                break;

            case "intelligence.ultimate.select.vector":
                if (message.Payload.TryGetValue("strategyId", out var vectorObj) && vectorObj is string vectorId)
                    SetActiveVectorStore(vectorId);
                break;

            case "intelligence.ultimate.select.graph":
                if (message.Payload.TryGetValue("strategyId", out var graphObj) && graphObj is string graphId)
                    SetActiveKnowledgeGraph(graphId);
                break;

            case "intelligence.ultimate.select.feature":
                if (message.Payload.TryGetValue("strategyId", out var featureObj) && featureObj is string featureId)
                    SetActiveFeature(featureId);
                break;

            case "intelligence.ultimate.configure":
                if (message.Payload.TryGetValue("strategyId", out var configIdObj) && configIdObj is string configId)
                {
                    var strategy = GetStrategy(configId);
                    if (strategy is IntelligenceStrategyBase baseStrategy)
                    {
                        foreach (var kvp in message.Payload.Where(p => p.Key != "strategyId"))
                        {
                            baseStrategy.Configure(kvp.Key, kvp.Value?.ToString() ?? "");
                        }
                    }
                }
                break;

            case "intelligence.ultimate.stats":
                // Get statistics for a strategy
                if (message.Payload.TryGetValue("strategyId", out var statsIdObj) && statsIdObj is string statsId)
                {
                    var strategy = GetStrategy(statsId);
                    var stats = strategy?.GetStatistics();
                    // Note: Reply mechanism would need to be handled by caller
                }
                break;

            // ============== TIERED MEMORY MESSAGE HANDLERS ==============

            case MemoryTopics.Store:
            case MemoryTopics.StoreWithTier:
                await HandleMemoryStoreAsync(message);
                break;

            case MemoryTopics.Recall:
            case MemoryTopics.RecallWithTier:
                await HandleMemoryRecallAsync(message);
                break;

            case MemoryTopics.ConfigureTier:
                await HandleConfigureTierAsync(message);
                break;

            case MemoryTopics.GetTierStats:
                await HandleGetTierStatsAsync(message);
                break;

            case MemoryTopics.EnableTier:
            case MemoryTopics.DisableTier:
                await HandleTierEnableDisableAsync(message);
                break;

            case MemoryTopics.Consolidate:
                await HandleConsolidateAsync(message);
                break;

            case MemoryTopics.Refine:
                await HandleRefineAsync(message);
                break;

            case MemoryTopics.GetEvolution:
                await HandleGetEvolutionAsync(message);
                break;

            case MemoryTopics.Regenerate:
                await HandleRegenerateAsync(message);
                break;

            case MemoryTopics.ValidateRegeneration:
                await HandleValidateRegenerationAsync(message);
                break;

            case MemoryTopics.Flush:
                await HandleFlushAsync(message);
                break;

            case MemoryTopics.Restore:
                await HandleRestoreAsync(message);
                break;

            case MemoryTopics.Forget:
                await HandleForgetAsync(message);
                break;

            case MemoryTopics.Promote:
                await HandlePromoteAsync(message);
                break;

            case MemoryTopics.Demote:
                await HandleDemoteAsync(message);
                break;

            case MemoryTopics.GetStatistics:
                await HandleGetMemoryStatisticsAsync(message);
                break;
        }

        await base.OnMessageAsync(message);
    }

    #region Memory Message Handlers

    private async Task HandleMemoryStoreAsync(PluginMessage message)
    {
        if (_tieredMemoryStrategy == null) return;

        try
        {
            var content = message.Payload.TryGetValue("content", out var c) ? c?.ToString() : null;
            if (string.IsNullOrEmpty(content)) return;

            var tier = MemoryTier.Working;
            if (message.Payload.TryGetValue("tier", out var tierObj) && tierObj is string tierStr)
            {
                Enum.TryParse<MemoryTier>(tierStr, true, out tier);
            }

            var scope = message.Payload.TryGetValue("scope", out var s) ? s?.ToString() ?? "global" : "global";

            Dictionary<string, object>? metadata = null;
            if (message.Payload.TryGetValue("metadata", out var m) && m is Dictionary<string, object> md)
            {
                metadata = md;
            }

            var memoryId = await _tieredMemoryStrategy.StoreWithTierAsync(content, tier, scope, metadata);

            // Response would be sent via message bus reply mechanism
        }
        catch
        {
            // Log error
        }
    }

    private async Task HandleMemoryRecallAsync(PluginMessage message)
    {
        if (_tieredMemoryStrategy == null) return;

        try
        {
            var query = message.Payload.TryGetValue("query", out var q) ? q?.ToString() : null;
            if (string.IsNullOrEmpty(query)) return;

            var minTier = MemoryTier.Working;
            if (message.Payload.TryGetValue("minTier", out var tierObj) && tierObj is string tierStr)
            {
                Enum.TryParse<MemoryTier>(tierStr, true, out minTier);
            }

            var scope = message.Payload.TryGetValue("scope", out var s) ? s?.ToString() ?? "global" : "global";
            var topK = message.Payload.TryGetValue("topK", out var tk) && tk is int tkInt ? tkInt : 10;

            var results = await _tieredMemoryStrategy.RecallWithTierAsync(query, minTier, scope, topK);

            // Response would be sent via message bus reply mechanism
        }
        catch
        {
            // Log error
        }
    }

    private async Task HandleConfigureTierAsync(PluginMessage message)
    {
        if (_tieredMemoryStrategy == null) return;

        try
        {
            if (!message.Payload.TryGetValue("tier", out var tierObj) || tierObj is not string tierStr)
                return;

            if (!Enum.TryParse<MemoryTier>(tierStr, true, out var tier))
                return;

            var enabled = message.Payload.TryGetValue("enabled", out var e) && e is bool eb && eb;
            var persistence = MemoryPersistence.Volatile;
            if (message.Payload.TryGetValue("persistence", out var p) && p is string ps)
            {
                Enum.TryParse<MemoryPersistence>(ps, true, out persistence);
            }

            var maxCapacity = message.Payload.TryGetValue("maxCapacityBytes", out var mc) && mc is long mcl ? mcl : 100 * 1024 * 1024L;

            TimeSpan? ttl = null;
            if (message.Payload.TryGetValue("ttlSeconds", out var ttlObj) && ttlObj is int ttlSec)
            {
                ttl = TimeSpan.FromSeconds(ttlSec);
            }

            await _tieredMemoryStrategy.ConfigureTierAsync(tier, enabled, persistence, maxCapacity, ttl);
        }
        catch
        {
            // Log error
        }
    }

    private async Task HandleGetTierStatsAsync(PluginMessage message)
    {
        if (_tieredMemoryStrategy == null) return;

        try
        {
            if (!message.Payload.TryGetValue("tier", out var tierObj) || tierObj is not string tierStr)
                return;

            if (!Enum.TryParse<MemoryTier>(tierStr, true, out var tier))
                return;

            var stats = await _tieredMemoryStrategy.GetTierStatisticsAsync(tier);

            // Response would be sent via message bus reply mechanism
        }
        catch
        {
            // Log error
        }
    }

    private async Task HandleTierEnableDisableAsync(PluginMessage message)
    {
        if (_tieredMemoryStrategy == null) return;

        try
        {
            if (!message.Payload.TryGetValue("tier", out var tierObj) || tierObj is not string tierStr)
                return;

            if (!Enum.TryParse<MemoryTier>(tierStr, true, out var tier))
                return;

            var enabled = message.Type == MemoryTopics.EnableTier;
            var currentConfig = _tieredMemoryStrategy.Configuration.Tiers.TryGetValue(tier, out var tc)
                ? tc
                : new TierConfig { Tier = tier };

            await _tieredMemoryStrategy.ConfigureTierAsync(tier, enabled, currentConfig.Persistence, currentConfig.MaxCapacityBytes, currentConfig.TTL);
        }
        catch
        {
            // Log error
        }
    }

    private async Task HandleConsolidateAsync(PluginMessage message)
    {
        if (_tieredMemoryStrategy == null) return;

        try
        {
            await _tieredMemoryStrategy.ConsolidateMemoriesAsync();
        }
        catch
        {
            // Log error
        }
    }

    private async Task HandleRefineAsync(PluginMessage message)
    {
        if (_tieredMemoryStrategy == null) return;

        try
        {
            var scope = message.Payload.TryGetValue("scope", out var s) ? s?.ToString() : null;
            await _tieredMemoryStrategy.RefineContextAsync(scope);
        }
        catch
        {
            // Log error
        }
    }

    private Task HandleGetEvolutionAsync(PluginMessage message)
    {
        if (_tieredMemoryStrategy == null) return Task.CompletedTask;

        try
        {
            var metrics = _tieredMemoryStrategy.GetEvolutionMetrics();
            // Response would be sent via message bus reply mechanism
        }
        catch
        {
            // Log error
        }

        return Task.CompletedTask;
    }

    private async Task HandleRegenerateAsync(PluginMessage message)
    {
        if (_tieredMemoryStrategy == null) return;

        try
        {
            var contextEntryId = message.Payload.TryGetValue("contextEntryId", out var id) ? id?.ToString() : null;
            var expectedFormat = message.Payload.TryGetValue("expectedFormat", out var fmt) ? fmt?.ToString() : null;

            if (string.IsNullOrEmpty(contextEntryId) || string.IsNullOrEmpty(expectedFormat))
                return;

            var result = await _tieredMemoryStrategy.RegenerateDataAsync(contextEntryId, expectedFormat);

            // Response would be sent via message bus reply mechanism
        }
        catch
        {
            // Log error
        }
    }

    private Task HandleValidateRegenerationAsync(PluginMessage message)
    {
        // Validation logic would be implemented here
        return Task.CompletedTask;
    }

    private async Task HandleFlushAsync(PluginMessage message)
    {
        if (_tieredMemoryStrategy == null) return;

        try
        {
            await _tieredMemoryStrategy.FlushToPersistentAsync();
        }
        catch
        {
            // Log error
        }
    }

    private async Task HandleRestoreAsync(PluginMessage message)
    {
        if (_tieredMemoryStrategy == null) return;

        try
        {
            await _tieredMemoryStrategy.RestoreFromPersistentAsync();
        }
        catch
        {
            // Log error
        }
    }

    private async Task HandleForgetAsync(PluginMessage message)
    {
        if (_tieredMemoryStrategy == null) return;

        try
        {
            var memoryId = message.Payload.TryGetValue("memoryId", out var id) ? id?.ToString() : null;
            if (string.IsNullOrEmpty(memoryId)) return;

            await _tieredMemoryStrategy.ForgetMemoryAsync(memoryId);
        }
        catch
        {
            // Log error
        }
    }

    private async Task HandlePromoteAsync(PluginMessage message)
    {
        if (_tieredMemoryStrategy == null) return;

        try
        {
            var memoryId = message.Payload.TryGetValue("memoryId", out var id) ? id?.ToString() : null;
            if (string.IsNullOrEmpty(memoryId)) return;

            if (!message.Payload.TryGetValue("targetTier", out var tierObj) || tierObj is not string tierStr)
                return;

            if (!Enum.TryParse<MemoryTier>(tierStr, true, out var targetTier))
                return;

            await _tieredMemoryStrategy.PromoteMemoryAsync(memoryId, targetTier);
        }
        catch
        {
            // Log error
        }
    }

    private async Task HandleDemoteAsync(PluginMessage message)
    {
        if (_tieredMemoryStrategy == null) return;

        try
        {
            var memoryId = message.Payload.TryGetValue("memoryId", out var id) ? id?.ToString() : null;
            if (string.IsNullOrEmpty(memoryId)) return;

            if (!message.Payload.TryGetValue("targetTier", out var tierObj) || tierObj is not string tierStr)
                return;

            if (!Enum.TryParse<MemoryTier>(tierStr, true, out var targetTier))
                return;

            await _tieredMemoryStrategy.DemoteMemoryAsync(memoryId, targetTier);
        }
        catch
        {
            // Log error
        }
    }

    private async Task HandleGetMemoryStatisticsAsync(PluginMessage message)
    {
        if (_tieredMemoryStrategy == null) return;

        try
        {
            var stats = await _tieredMemoryStrategy.GetMemoryStatisticsAsync();
            // Response would be sent via message bus reply mechanism
        }
        catch
        {
            // Log error
        }
    }

    #endregion

    /// <summary>
    /// Gets aggregate statistics across all strategies.
    /// </summary>
    public IntelligencePluginStatistics GetPluginStatistics()
    {
        var allStats = _allStrategies.Values.Select(s => s.GetStatistics()).ToList();

        return new IntelligencePluginStatistics
        {
            TotalStrategies = _allStrategies.Count,
            AvailableStrategies = _allStrategies.Values.Count(s => s.IsAvailable),
            TotalOperations = allStats.Sum(s => s.TotalOperations),
            TotalTokensConsumed = allStats.Sum(s => s.TotalTokensConsumed),
            TotalEmbeddingsGenerated = allStats.Sum(s => s.TotalEmbeddingsGenerated),
            TotalVectorsStored = allStats.Sum(s => s.TotalVectorsStored),
            TotalSearches = allStats.Sum(s => s.TotalSearches),
            TotalNodesCreated = allStats.Sum(s => s.TotalNodesCreated),
            TotalEdgesCreated = allStats.Sum(s => s.TotalEdgesCreated),
            AverageLatencyMs = allStats.Where(s => s.TotalOperations > 0).Select(s => s.AverageLatencyMs).DefaultIfEmpty(0).Average(),
            StrategiesByCategory = new Dictionary<IntelligenceStrategyCategory, int>
            {
                [IntelligenceStrategyCategory.AIProvider] = GetStrategiesByCategory(IntelligenceStrategyCategory.AIProvider).Count,
                [IntelligenceStrategyCategory.VectorStore] = GetStrategiesByCategory(IntelligenceStrategyCategory.VectorStore).Count,
                [IntelligenceStrategyCategory.KnowledgeGraph] = GetStrategiesByCategory(IntelligenceStrategyCategory.KnowledgeGraph).Count,
                [IntelligenceStrategyCategory.Feature] = GetStrategiesByCategory(IntelligenceStrategyCategory.Feature).Count
            }
        };
    }
}

/// <summary>
/// Aggregate statistics for the Ultimate Intelligence plugin.
/// </summary>
public sealed class IntelligencePluginStatistics
{
    /// <summary>Total number of registered strategies.</summary>
    public int TotalStrategies { get; init; }

    /// <summary>Number of available (properly configured) strategies.</summary>
    public int AvailableStrategies { get; init; }

    /// <summary>Total operations across all strategies.</summary>
    public long TotalOperations { get; init; }

    /// <summary>Total tokens consumed across all AI providers.</summary>
    public long TotalTokensConsumed { get; init; }

    /// <summary>Total embeddings generated.</summary>
    public long TotalEmbeddingsGenerated { get; init; }

    /// <summary>Total vectors stored.</summary>
    public long TotalVectorsStored { get; init; }

    /// <summary>Total searches performed.</summary>
    public long TotalSearches { get; init; }

    /// <summary>Total graph nodes created.</summary>
    public long TotalNodesCreated { get; init; }

    /// <summary>Total graph edges created.</summary>
    public long TotalEdgesCreated { get; init; }

    /// <summary>Average latency across all strategies.</summary>
    public double AverageLatencyMs { get; init; }

    /// <summary>Strategy count by category.</summary>
    public Dictionary<IntelligenceStrategyCategory, int> StrategiesByCategory { get; init; } = new();
}
