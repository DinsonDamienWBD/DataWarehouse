using System.Collections.Concurrent;
using System.Reflection;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Contracts.StorageProcessing;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStorageProcessing;

/// <summary>
/// Ultimate Storage Processing Plugin - Comprehensive on-storage processing strategy implementation.
///
/// Implements 47+ storage processing strategies across categories:
/// - Compression: Zstd, LZ4, Brotli, Snappy, Transparent, Content-Aware
/// - Build: .NET, TypeScript, Rust, Go, Docker, Bazel, Gradle, Maven, npm
/// - Document: Markdown, LaTeX, Jupyter, Sass, Minification
/// - Media: FFmpeg, ImageMagick, WebP, AVIF, HLS, DASH
/// - GameAsset: Texture, Mesh, Audio, Shader, Bundling, LOD
/// - Data: Parquet, Index, Vector, Validation, Schema
/// - IndustryFirst: Build Cache, Incremental, Predictive, GPU, Cost, Dependency
///
/// Features:
/// - Auto-discovery of all strategies via assembly scanning
/// - Job scheduling with priority queues and concurrency control
/// - Shared caching for processing results
/// - Intelligence-aware strategy selection
/// - In-place data processing without transfer
/// </summary>
public sealed class UltimateStorageProcessingPlugin : IntelligenceAwarePluginBase, IDisposable
{
    private readonly StorageProcessingStrategyRegistryInternal _registry;
    private readonly ConcurrentDictionary<string, long> _usageStats = new();
    private bool _disposed;
    private bool _initialized;

    // Statistics
    private long _totalProcessed;
    private long _totalFailures;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.storageprocessing.ultimate";

    /// <inheritdoc/>
    public override string Name => "Ultimate Storage Processing";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Semantic description of this plugin for AI discovery.
    /// </summary>
    public string SemanticDescription =>
        "Ultimate storage processing plugin providing 47+ on-storage processing strategy implementations including " +
        "Compression (Zstd, LZ4, Brotli, Snappy, Transparent, Content-Aware), " +
        "Build (.NET, TypeScript, Rust, Go, Docker, Bazel, Gradle, Maven, npm), " +
        "Document (Markdown, LaTeX, Jupyter, Sass, Minification), " +
        "Media (FFmpeg, ImageMagick, WebP, AVIF, HLS, DASH), " +
        "GameAsset (Texture, Mesh, Audio, Shader, Bundling, LOD), " +
        "Data (Parquet, Index, Vector, Validation, Schema), and " +
        "IndustryFirst (Build Cache, Incremental, Predictive, GPU, Cost, Dependency). " +
        "Supports in-place data processing without transfer, query pushdown, and aggregation.";

    /// <summary>
    /// Semantic tags for AI discovery and categorization.
    /// </summary>
    public string[] SemanticTags =>
    [
        "storage-processing", "compression", "build", "document", "media", "game-asset",
        "data", "industry-first", "query-pushdown", "aggregation", "in-place", "compute"
    ];

    /// <summary>
    /// Gets the storage processing strategy registry.
    /// </summary>
    internal StorageProcessingStrategyRegistryInternal Registry => _registry;

    /// <summary>
    /// Declares all capabilities provided by this plugin.
    /// </summary>
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
    {
        get
        {
            var capabilities = new List<RegisteredCapability>
            {
                new()
                {
                    CapabilityId = $"{Id}.storageprocessing",
                    DisplayName = "Ultimate Storage Processing",
                    Description = SemanticDescription,
                    Category = SDK.Contracts.CapabilityCategory.Compute,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = [.. SemanticTags]
                }
            };

            // Per AD-05 (Phase 25b): capability registration is now plugin-level responsibility.
            foreach (var strategy in _registry.GetAllStrategies())
            {
                if (strategy is StorageProcessingStrategyBase baseStrategy)
                {
                    capabilities.Add(new RegisteredCapability
                    {
                        CapabilityId = $"storageprocessing.{baseStrategy.StrategyId}",
                        DisplayName = baseStrategy.Name,
                        Description = baseStrategy.Description,
                        Category = SDK.Contracts.CapabilityCategory.Custom,
                        SubCategory = "StorageProcessing",
                        PluginId = Id,
                        PluginName = Name,
                        PluginVersion = Version,
                        Tags = new[] { "storage-processing", baseStrategy.StrategyId },
                        SemanticDescription = baseStrategy.Description
                    });
                }
            }

            return capabilities.AsReadOnly();
        }
    }

    /// <summary>
    /// Initializes a new instance of the Ultimate Storage Processing plugin.
    /// </summary>
    public UltimateStorageProcessingPlugin()
    {
        _registry = new StorageProcessingStrategyRegistryInternal();
    }

    /// <inheritdoc/>
    protected override async Task OnStartCoreAsync(CancellationToken ct)
    {
        if (_initialized) return;

        // Auto-discover all strategies in this assembly
        _registry.DiscoverStrategies(Assembly.GetExecutingAssembly());

        // Configure Intelligence on each strategy and publish registration events
        var strategies = _registry.GetAllStrategies();
        foreach (var strategy in strategies)
        {
            if (strategy is StorageProcessingStrategyBase baseStrategy && MessageBus != null)
            {
                baseStrategy.ConfigureIntelligence(MessageBus);
            }

            await PublishStrategyRegisteredAsync(strategy);
        }

        _initialized = true;
    }

    /// <inheritdoc/>
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
    {
        await RegisterStorageProcessingCapabilitiesAsync(ct);
    }

    /// <inheritdoc/>
    protected override Task OnStartWithoutIntelligenceAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task OnStopCoreAsync()
    {
        _usageStats.Clear();
        _initialized = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        var response = await base.OnHandshakeAsync(request);
        response.Metadata["RegisteredStrategies"] = _registry.Count.ToString();
        response.Metadata["SemanticDescription"] = SemanticDescription;

        var categoryCounts = _registry.GetAllStrategies()
            .OfType<StorageProcessingStrategyBase>()
            .GroupBy(s => ExtractCategory(s.StrategyId))
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var cat in categoryCounts)
        {
            response.Metadata[$"Category.{cat.Key}"] = cat.Value.ToString();
        }

        return response;
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        var strategies = _registry.GetAllStrategies();
        var categoryCounts = strategies
            .OfType<StorageProcessingStrategyBase>()
            .GroupBy(s => ExtractCategory(s.StrategyId))
            .ToDictionary(g => g.Key, g => g.Count());

        return new List<KnowledgeObject>
        {
            new()
            {
                Id = $"{Id}:overview",
                Topic = "storage-processing",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = "Ultimate Storage Processing Overview",
                Payload = new Dictionary<string, object>
                {
                    ["description"] = SemanticDescription,
                    ["totalStrategies"] = strategies.Count,
                    ["categories"] = categoryCounts,
                    ["features"] = new Dictionary<string, object>
                    {
                        ["supportsCompression"] = true,
                        ["supportsBuild"] = true,
                        ["supportsDocument"] = true,
                        ["supportsMedia"] = true,
                        ["supportsGameAsset"] = true,
                        ["supportsData"] = true,
                        ["supportsIndustryFirst"] = true
                    }
                },
                Tags = SemanticTags
            }
        }.AsReadOnly();
    }

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return
        [
            new() { Name = "storageprocessing.process", DisplayName = "Process", Description = "Execute a storage processing query using the selected strategy" },
            new() { Name = "storageprocessing.query", DisplayName = "Query", Description = "Execute a query against stored data with filtering and projection" },
            new() { Name = "storageprocessing.aggregate", DisplayName = "Aggregate", Description = "Perform aggregation operations at the storage layer" },
            new() { Name = "storageprocessing.list-strategies", DisplayName = "List Strategies", Description = "List available storage processing strategies" },
            new() { Name = "storageprocessing.list-categories", DisplayName = "List Categories", Description = "List strategies by category" },
            new() { Name = "storageprocessing.stats", DisplayName = "Statistics", Description = "Get storage processing usage statistics" }
        ];
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["TotalStrategies"] = _registry.Count;
        metadata["SemanticDescription"] = SemanticDescription;
        metadata["SemanticTags"] = SemanticTags;
        return metadata;
    }

    /// <inheritdoc/>
    public override Task OnMessageAsync(PluginMessage message)
    {
        return message.Type switch
        {
            "storageprocessing.list-strategies" => HandleListStrategiesAsync(message),
            "storageprocessing.list-categories" => HandleListCategoriesAsync(message),
            "storageprocessing.stats" => HandleStatsAsync(message),
            _ => base.OnMessageAsync(message)
        };
    }

    /// <summary>
    /// Gets a storage processing strategy by its identifier.
    /// </summary>
    /// <param name="strategyId">The strategy identifier.</param>
    /// <returns>The strategy, or null if not found.</returns>
    public IStorageProcessingStrategy? GetStrategy(string strategyId)
    {
        var strategy = _registry.GetStrategy(strategyId);
        if (strategy != null)
        {
            _usageStats.AddOrUpdate(strategyId, 1, (_, count) => count + 1);
        }
        return strategy;
    }

    /// <summary>
    /// Processes a query using the specified strategy.
    /// </summary>
    /// <param name="strategyId">The strategy identifier.</param>
    /// <param name="query">The processing query to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The processing result.</returns>
    /// <exception cref="ArgumentException">Thrown if the strategy is not found.</exception>
    public async Task<ProcessingResult> ProcessAsync(string strategyId, ProcessingQuery query, CancellationToken ct = default)
    {
        var strategy = GetStrategy(strategyId)
            ?? throw new ArgumentException($"Strategy '{strategyId}' not found", nameof(strategyId));

        try
        {
            var result = await strategy.ProcessAsync(query, ct);
            Interlocked.Increment(ref _totalProcessed);
            return result;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _totalProcessed);
            Interlocked.Increment(ref _totalFailures);
            return new ProcessingResult
            {
                Data = new Dictionary<string, object?>
                {
                    ["error"] = ex.Message,
                    ["strategyId"] = strategyId
                },
                Metadata = new ProcessingMetadata
                {
                    RowsProcessed = 0,
                    RowsReturned = 0,
                    BytesProcessed = 0,
                    ProcessingTimeMs = 0
                }
            };
        }
    }

    #region Message Handlers

    private Task HandleListStrategiesAsync(PluginMessage message)
    {
        var strategies = _registry.GetAllStrategies();
        var strategyList = strategies.OfType<StorageProcessingStrategyBase>().Select(s => new Dictionary<string, object>
        {
            ["id"] = s.StrategyId,
            ["displayName"] = s.Name,
            ["supportsFiltering"] = s.Capabilities.SupportsFiltering,
            ["supportsAggregation"] = s.Capabilities.SupportsAggregation,
            ["supportsProjection"] = s.Capabilities.SupportsProjection,
            ["maxQueryComplexity"] = s.Capabilities.MaxQueryComplexity
        }).ToList();

        message.Payload["strategies"] = strategyList;
        message.Payload["count"] = strategyList.Count;
        return Task.CompletedTask;
    }

    private Task HandleListCategoriesAsync(PluginMessage message)
    {
        var categories = _registry.GetAllCategories();
        var categoryCounts = new Dictionary<string, int>();
        foreach (var category in categories)
        {
            categoryCounts[category] = _registry.GetStrategiesByCategory(category).Count;
        }

        message.Payload["categories"] = categoryCounts;
        return Task.CompletedTask;
    }

    private Task HandleStatsAsync(PluginMessage message)
    {
        message.Payload["registeredStrategies"] = _registry.Count;
        message.Payload["usageByStrategy"] = new Dictionary<string, long>(_usageStats);
        message.Payload["totalProcessed"] = Interlocked.Read(ref _totalProcessed);
        message.Payload["totalFailures"] = Interlocked.Read(ref _totalFailures);
        message.Payload["initialized"] = _initialized;
        return Task.CompletedTask;
    }

    #endregion

    #region Internal Helpers

    private async Task RegisterStorageProcessingCapabilitiesAsync(CancellationToken ct)
    {
        if (MessageBus == null) return;

        var strategies = _registry.GetAllStrategies();
        var categoryCounts = strategies
            .OfType<StorageProcessingStrategyBase>()
            .GroupBy(s => ExtractCategory(s.StrategyId))
            .ToDictionary(g => g.Key, g => g.Count());

        await MessageBus.PublishAsync(IntelligenceTopics.QueryCapability, new PluginMessage
        {
            Type = "capability.register",
            Source = Id,
            Payload = new Dictionary<string, object>
            {
                ["pluginId"] = Id,
                ["pluginName"] = Name,
                ["pluginType"] = "storage-processing",
                ["capabilities"] = new Dictionary<string, object>
                {
                    ["strategyCount"] = strategies.Count,
                    ["categories"] = categoryCounts,
                    ["supportsCompression"] = true,
                    ["supportsBuild"] = true,
                    ["supportsDocument"] = true,
                    ["supportsMedia"] = true,
                    ["supportsGameAsset"] = true,
                    ["supportsData"] = true,
                    ["supportsIndustryFirst"] = true
                },
                ["semanticDescription"] = SemanticDescription,
                ["tags"] = SemanticTags
            }
        }, ct);
    }

    private async Task PublishStrategyRegisteredAsync(IStorageProcessingStrategy strategy)
    {
        if (MessageBus == null) return;

        try
        {
            var eventMessage = new PluginMessage
            {
                Type = "storageprocessing.strategy.registered",
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["strategyId"] = strategy.StrategyId,
                    ["displayName"] = strategy.Name,
                    ["supportsFiltering"] = strategy.Capabilities.SupportsFiltering,
                    ["supportsAggregation"] = strategy.Capabilities.SupportsAggregation
                }
            };

            await MessageBus.PublishAsync("storageprocessing.strategy.registered", eventMessage);
        }
        catch
        {
            // Gracefully handle message bus unavailability
        }
    }

    private static string ExtractCategory(string strategyId)
    {
        var dashIndex = strategyId.IndexOf('-');
        return dashIndex > 0 ? strategyId[..dashIndex] : strategyId;
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes plugin resources.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_disposed) return;

            foreach (var strategy in _registry.GetAllStrategies())
            {
            if (strategy is IDisposable disposable)
            {
            try { disposable.Dispose(); }
            catch { /* Ignore disposal errors */ }
            }
            }

            _disposed = true;
        }
        base.Dispose(disposing);
    }

    #endregion
}
