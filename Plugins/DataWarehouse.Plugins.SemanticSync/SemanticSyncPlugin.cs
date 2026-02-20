// <copyright file="SemanticSyncPlugin.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Text.Json;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.SemanticSync;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.SemanticSync.Orchestration;
using DataWarehouse.Plugins.SemanticSync.Strategies.Classification;
using DataWarehouse.Plugins.SemanticSync.Strategies.ConflictResolution;
using DataWarehouse.Plugins.SemanticSync.Strategies.EdgeInference;
using DataWarehouse.Plugins.SemanticSync.Strategies.Fidelity;
using DataWarehouse.Plugins.SemanticSync.Strategies.Routing;

namespace DataWarehouse.Plugins.SemanticSync;

/// <summary>
/// Semantic Sync Protocol Plugin - Comprehensive semantic-aware synchronization capabilities.
/// Implements semantic conflict classification, summary-vs-raw routing, conflict resolution,
/// bandwidth-aware fidelity control, and edge inference integration for distributed data sync.
/// </summary>
/// <remarks>
/// <para>
/// This plugin wires all strategies from Plans 03-07 into a working end-to-end sync pipeline:
/// </para>
/// <list type="bullet">
///   <item>Plan 03: Semantic classification (EmbeddingClassifier, RuleBasedClassifier, HybridClassifier)</item>
///   <item>Plan 04: Summary-vs-raw routing (BandwidthAwareSummaryRouter, SummaryGenerator, FidelityDownsampler)</item>
///   <item>Plan 05: Semantic conflict resolution (SemanticMergeResolver, EmbeddingSimilarityDetector, ConflictClassificationEngine)</item>
///   <item>Plan 06: Bandwidth-aware fidelity control (AdaptiveFidelityController, BandwidthBudgetTracker, FidelityPolicyEngine)</item>
///   <item>Plan 07: Edge inference integration (EdgeInferenceCoordinator, LocalModelManager, FederatedSyncLearner)</item>
///   <item>Plan 08: Orchestration (SyncPipeline, SemanticSyncOrchestrator) wiring all strategies together</item>
/// </list>
/// <para>
/// Follows the OrchestrationPluginBase pattern established by UltimateEdgeComputing.
/// All strategies are registered via <see cref="RegisterStrategy"/> and retrieved via <see cref="GetStrategy{T}"/>.
/// Message bus subscriptions enable other plugins (UltimateReplication, AdaptiveTransport, UltimateEdgeComputing)
/// to trigger semantic sync operations.
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic conflict resolution")]
public sealed class SemanticSyncPlugin : OrchestrationPluginBase
{
    private readonly BoundedDictionary<string, StrategyBase> _strategies = new BoundedDictionary<string, StrategyBase>(1000);
    private readonly List<IDisposable> _subscriptions = new();
    private SemanticSyncOrchestrator? _orchestrator;
    private HybridClassifier? _hybridClassifier;
    private BandwidthAwareSummaryRouter? _summaryRouter;
    private SemanticMergeResolver? _conflictResolver;
    private AdaptiveFidelityController? _fidelityController;
    private FederatedSyncLearner? _federatedLearner;
    private BandwidthBudgetTracker? _budgetTracker;
    private bool _initialized;

    /// <summary>Gets the plugin identifier.</summary>
    public override string Id => "semantic-sync-protocol";

    /// <summary>Gets the plugin name.</summary>
    public override string Name => "Semantic Sync Protocol";

    /// <summary>Gets the plugin version.</summary>
    public override string Version => "5.0.0";

    /// <inheritdoc/>
    public override string OrchestrationMode => "SemanticSync";

    /// <summary>Gets the plugin category.</summary>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Gets all registered strategy names for diagnostic purposes.
    /// </summary>
    public IReadOnlyCollection<string> RegisteredStrategyNames => _strategies.Keys.ToArray();

    /// <summary>
    /// Gets the count of registered strategies.
    /// </summary>
    public int StrategyCount => _strategies.Count;

    /// <summary>
    /// Registers a strategy with the specified name.
    /// Thread-safe; overwrites any previously registered strategy with the same name.
    /// </summary>
    /// <param name="name">The unique name for the strategy.</param>
    /// <param name="strategy">The strategy instance to register.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> or <paramref name="strategy"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is empty or whitespace.</exception>
    public void RegisterStrategy(string name, StrategyBase strategy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(strategy);

        _strategies[name] = strategy;
    }

    /// <summary>
    /// Gets a registered strategy by name, cast to the specified type.
    /// </summary>
    /// <typeparam name="T">The expected strategy type.</typeparam>
    /// <param name="name">The strategy name.</param>
    /// <returns>The strategy cast to <typeparamref name="T"/>, or null if not found or wrong type.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> is null.</exception>
    public T? GetStrategy<T>(string name) where T : StrategyBase
    {
        ArgumentNullException.ThrowIfNull(name);

        return _strategies.TryGetValue(name, out var strategy) ? strategy as T : null;
    }

    /// <summary>
    /// Gets a registered strategy by name without type casting.
    /// </summary>
    /// <param name="name">The strategy name.</param>
    /// <returns>The strategy, or null if not found.</returns>
    public StrategyBase? GetStrategy(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _strategies.TryGetValue(name, out var strategy) ? strategy : null;
    }

    /// <summary>
    /// Gets all registered strategies as a read-only dictionary.
    /// </summary>
    /// <returns>A read-only view of all registered strategies.</returns>
    public IReadOnlyDictionary<string, StrategyBase> GetAllStrategies() => _strategies;

    /// <summary>
    /// Removes a strategy by name, disposing it if applicable.
    /// </summary>
    /// <param name="name">The strategy name to remove.</param>
    /// <returns>True if the strategy was found and removed; otherwise false.</returns>
    public bool RemoveStrategy(string name)
    {
        if (_strategies.TryRemove(name, out var strategy))
        {
            strategy.Dispose();
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    protected override async Task OnStartCoreAsync(CancellationToken ct)
    {
        if (_initialized)
            return;

        await base.OnStartCoreAsync(ct);

        // Step 1: Resolve optional IAIProvider from kernel services (null if unavailable)
        IAIProvider? aiProvider = ResolveAIProvider();

        // Step 2: Create helper instances
        var localModelManager = new LocalModelManager();
        _budgetTracker = new BandwidthBudgetTracker();
        var defaultPolicy = new FidelityPolicy(
            MinFidelityForCritical: SyncFidelity.Standard,
            DefaultFidelity: SyncFidelity.Standard,
            BandwidthThresholds: new Dictionary<long, SyncFidelity>
            {
                [100_000_000] = SyncFidelity.Full,
                [10_000_000] = SyncFidelity.Detailed,
                [1_000_000] = SyncFidelity.Standard,
                [100_000] = SyncFidelity.Summarized,
                [0] = SyncFidelity.Metadata
            },
            MaxDeferDuration: TimeSpan.FromMinutes(15));
        var policyEngine = new FidelityPolicyEngine(defaultPolicy);

        // Step 3: Create strategies (dependency injection by construction)
        var embeddingClassifier = aiProvider is not null
            ? new EmbeddingClassifier(aiProvider)
            : new EmbeddingClassifier(NullAIProvider.Instance);
        var ruleClassifier = new RuleBasedClassifier();
        _hybridClassifier = new HybridClassifier(embeddingClassifier, ruleClassifier, 0.7);

        var summaryGenerator = new SummaryGenerator(aiProvider);
        var downsampler = new FidelityDownsampler();
        _summaryRouter = new BandwidthAwareSummaryRouter(summaryGenerator, downsampler);

        var similarityDetector = new EmbeddingSimilarityDetector(aiProvider);
        var conflictEngine = new ConflictClassificationEngine();
        _conflictResolver = new SemanticMergeResolver(similarityDetector, conflictEngine);

        _fidelityController = new AdaptiveFidelityController(_budgetTracker, policyEngine);
        var edgeInference = new EdgeInferenceCoordinator(localModelManager, _hybridClassifier, aiProvider);
        _federatedLearner = new FederatedSyncLearner(localModelManager);

        // Step 4: Register all strategies
        RegisterStrategy("classifier-embedding", embeddingClassifier);
        RegisterStrategy("classifier-rules", ruleClassifier);
        RegisterStrategy("classifier-hybrid", _hybridClassifier);
        RegisterStrategy("router-summary", _summaryRouter);
        RegisterStrategy("resolver-semantic", _conflictResolver);
        RegisterStrategy("fidelity-adaptive", _fidelityController);
        RegisterStrategy("edge-inference", edgeInference);

        // Step 5: Create pipeline and orchestrator
        var pipeline = new SyncPipeline(
            _hybridClassifier,
            _fidelityController,
            _summaryRouter,
            _conflictResolver,
            edgeInference);

        _orchestrator = new SemanticSyncOrchestrator(pipeline, _budgetTracker);
        _orchestrator.StartAsync();

        // Step 6: Subscribe to message bus topics
        SubscribeToTopics();

        // Initialize all registered strategies
        foreach (var kvp in _strategies)
        {
            await kvp.Value.InitializeAsync(ct).ConfigureAwait(false);
        }

        _initialized = true;
    }

    /// <inheritdoc/>
    protected override async Task OnStopCoreAsync()
    {
        // Stop orchestrator
        if (_orchestrator is not null)
        {
            await _orchestrator.StopAsync().ConfigureAwait(false);
        }

        // Unsubscribe from all topics
        foreach (var subscription in _subscriptions)
        {
            try { subscription.Dispose(); }
            catch { /* Best-effort cleanup */ }
        }
        _subscriptions.Clear();

        // Shut down all strategies
        foreach (var kvp in _strategies)
        {
            try
            {
                await kvp.Value.ShutdownAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort: continue shutting down remaining strategies
            }
        }

        _initialized = false;

        await base.OnStopCoreAsync();
    }

    /// <summary>
    /// Subscribes to semantic sync message bus topics for coordination with other plugins.
    /// Routes classify/route/conflict/fidelity/sync requests to the correct strategies.
    /// </summary>
    private void SubscribeToTopics()
    {
        if (MessageBus == null)
            return;

        // Topic: semantic-sync.classify -> classify via hybridClassifier, publish result
        TrySubscribe("semantic-sync.classify", async message =>
        {
            if (_hybridClassifier is null) return;

            var data = ExtractDataFromMessage(message);
            var metadata = ExtractMetadataFromMessage(message);

            var classification = await _hybridClassifier.ClassifyAsync(data, metadata)
                .ConfigureAwait(false);

            await MessageBus.PublishAsync("semantic-sync.classified", new PluginMessage
            {
                Type = "semantic-sync.classified",
                CorrelationId = message.CorrelationId,
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["data_id"] = classification.DataId,
                    ["importance"] = classification.Importance.ToString(),
                    ["confidence"] = classification.Confidence,
                    ["tags"] = classification.SemanticTags,
                    ["domain_hint"] = classification.DomainHint,
                    ["timestamp"] = classification.ClassifiedAt
                }
            }).ConfigureAwait(false);
        });

        // Topic: semantic-sync.route -> route via summaryRouter, publish result
        TrySubscribe("semantic-sync.route", async message =>
        {
            if (_summaryRouter is null || _fidelityController is null) return;

            var dataId = ExtractStringFromPayload(message, "data_id") ?? Guid.NewGuid().ToString("N");
            var classification = ExtractClassificationFromMessage(message);
            var budget = await _fidelityController.GetCurrentBudgetAsync().ConfigureAwait(false);

            var decision = await _summaryRouter.RouteAsync(dataId, classification, budget)
                .ConfigureAwait(false);

            await MessageBus.PublishAsync("semantic-sync.routed", new PluginMessage
            {
                Type = "semantic-sync.routed",
                CorrelationId = message.CorrelationId,
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["data_id"] = decision.DataId,
                    ["fidelity"] = decision.Fidelity.ToString(),
                    ["reason"] = decision.Reason.ToString(),
                    ["requires_summary"] = decision.RequiresSummary,
                    ["estimated_size"] = decision.EstimatedSizeBytes,
                    ["timestamp"] = DateTimeOffset.UtcNow
                }
            }).ConfigureAwait(false);
        });

        // Topic: semantic-sync.conflict -> resolve via conflictResolver, publish result
        TrySubscribe("semantic-sync.conflict", async message =>
        {
            if (_conflictResolver is null) return;

            var dataId = ExtractStringFromPayload(message, "data_id") ?? Guid.NewGuid().ToString("N");
            var localData = ExtractDataFromPayload(message, "local_data");
            var remoteData = ExtractDataFromPayload(message, "remote_data");

            var conflict = await _conflictResolver.DetectConflictAsync(dataId, localData, remoteData)
                .ConfigureAwait(false);

            if (conflict is not null)
            {
                var result = await _conflictResolver.ResolveAsync(conflict).ConfigureAwait(false);

                await MessageBus.PublishAsync("semantic-sync.resolved", new PluginMessage
                {
                    Type = "semantic-sync.resolved",
                    CorrelationId = message.CorrelationId,
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["data_id"] = result.DataId,
                        ["strategy"] = result.Strategy.ToString(),
                        ["similarity"] = result.SemanticSimilarity,
                        ["explanation"] = result.Explanation,
                        ["timestamp"] = DateTimeOffset.UtcNow
                    }
                }).ConfigureAwait(false);
            }
        });

        // Topic: semantic-sync.fidelity.update -> update bandwidth via fidelityController
        TrySubscribe("semantic-sync.fidelity.update", async message =>
        {
            if (_fidelityController is null) return;

            if (message.Payload.TryGetValue("bandwidth_bps", out var bwObj) &&
                bwObj is long bandwidthBps)
            {
                await _fidelityController.UpdateBandwidthAsync(bandwidthBps).ConfigureAwait(false);
            }
            else if (bwObj is int bwInt)
            {
                await _fidelityController.UpdateBandwidthAsync(bwInt).ConfigureAwait(false);
            }
            else if (bwObj is double bwDouble)
            {
                await _fidelityController.UpdateBandwidthAsync((long)bwDouble).ConfigureAwait(false);
            }
        });

        // Topic: semantic-sync.sync-request -> submit to orchestrator, publish result
        TrySubscribe("semantic-sync.sync-request", async message =>
        {
            if (_orchestrator is null) return;

            var dataId = ExtractStringFromPayload(message, "data_id") ?? Guid.NewGuid().ToString("N");
            var data = ExtractDataFromMessage(message);
            var metadata = ExtractMetadataFromMessage(message);

            var result = await _orchestrator.SubmitSyncAsync(dataId, data, metadata, CancellationToken.None)
                .ConfigureAwait(false);

            await MessageBus.PublishAsync("semantic-sync.sync-complete", new PluginMessage
            {
                Type = "semantic-sync.sync-complete",
                CorrelationId = message.CorrelationId,
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["data_id"] = result.DataId,
                    ["status"] = result.Status.ToString(),
                    ["fidelity"] = result.Decision.Fidelity.ToString(),
                    ["original_size"] = result.OriginalSizeBytes,
                    ["payload_size"] = result.PayloadSizeBytes,
                    ["compression_ratio"] = result.CompressionRatio,
                    ["processing_time_ms"] = result.ProcessingTime.TotalMilliseconds,
                    ["timestamp"] = DateTimeOffset.UtcNow
                }
            }).ConfigureAwait(false);
        });

        // Topic: federated-learning.model-aggregated -> forward to federatedLearner
        TrySubscribe("federated-learning.model-aggregated", async message =>
        {
            if (_federatedLearner is null) return;

            if (message.Payload.TryGetValue("model_data", out var modelDataObj) &&
                modelDataObj is string modelDataBase64)
            {
                var modelBytes = Convert.FromBase64String(modelDataBase64);
                await _federatedLearner.OnAggregatedModelReceivedAsync(modelBytes, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else if (message.Payload.TryGetValue("model_json", out var modelJsonObj) &&
                     modelJsonObj is string modelJson)
            {
                var modelBytes = System.Text.Encoding.UTF8.GetBytes(modelJson);
                await _federatedLearner.OnAggregatedModelReceivedAsync(modelBytes, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// Helper to subscribe to a topic with error handling.
    /// </summary>
    private void TrySubscribe(string topic, Func<PluginMessage, Task> handler)
    {
        try
        {
            var subscription = MessageBus!.Subscribe(topic, async message =>
            {
                try
                {
                    await handler(message).ConfigureAwait(false);
                }
                catch
                {
                    // Non-fatal: individual message handling failures don't prevent future operations
                }
            });
            _subscriptions.Add(subscription);
        }
        catch
        {
            // Non-fatal: topic subscription failures degrade functionality but don't prevent operation
        }
    }

    /// <summary>
    /// Attempts to resolve an IAIProvider. Returns null if unavailable (edge/air-gapped environments).
    /// All strategies handle null AI provider gracefully with fallback behavior.
    /// </summary>
    private static IAIProvider? ResolveAIProvider()
    {
        // AI provider is not injected by kernel services.
        // In production, it would be resolved via service registry or message bus request.
        // For now, return null -- all strategies handle this gracefully with rule-based fallbacks.
        return null;
    }

    /// <summary>
    /// Extracts raw data bytes from a plugin message payload.
    /// </summary>
    private static ReadOnlyMemory<byte> ExtractDataFromMessage(PluginMessage message)
    {
        if (message.Payload.TryGetValue("data", out var dataObj))
        {
            if (dataObj is byte[] bytes) return bytes;
            if (dataObj is string base64)
            {
                try { return Convert.FromBase64String(base64); }
                catch { /* Fall through */ }
            }
            if (dataObj is string text)
            {
                return System.Text.Encoding.UTF8.GetBytes(text);
            }
        }

        return ReadOnlyMemory<byte>.Empty;
    }

    /// <summary>
    /// Extracts data bytes from a specific payload key.
    /// </summary>
    private static ReadOnlyMemory<byte> ExtractDataFromPayload(PluginMessage message, string key)
    {
        if (message.Payload.TryGetValue(key, out var dataObj))
        {
            if (dataObj is byte[] bytes) return bytes;
            if (dataObj is string base64)
            {
                try { return Convert.FromBase64String(base64); }
                catch { return System.Text.Encoding.UTF8.GetBytes(base64); }
            }
        }

        return ReadOnlyMemory<byte>.Empty;
    }

    /// <summary>
    /// Extracts metadata dictionary from a plugin message payload.
    /// </summary>
    private static IDictionary<string, string>? ExtractMetadataFromMessage(PluginMessage message)
    {
        if (message.Payload.TryGetValue("metadata", out var metaObj))
        {
            if (metaObj is IDictionary<string, string> dict) return dict;
            if (metaObj is Dictionary<string, object> objDict)
            {
                return objDict.ToDictionary(k => k.Key, v => v.Value?.ToString() ?? string.Empty);
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts a string value from the payload.
    /// </summary>
    private static string? ExtractStringFromPayload(PluginMessage message, string key)
    {
        return message.Payload.TryGetValue(key, out var obj) ? obj?.ToString() : null;
    }

    /// <summary>
    /// Extracts a SemanticClassification from message payload fields.
    /// </summary>
    private static SemanticClassification ExtractClassificationFromMessage(PluginMessage message)
    {
        var dataId = ExtractStringFromPayload(message, "data_id") ?? Guid.NewGuid().ToString("N");

        var importance = SemanticImportance.Normal;
        if (message.Payload.TryGetValue("importance", out var impObj) && impObj is string impStr)
        {
            Enum.TryParse<SemanticImportance>(impStr, ignoreCase: true, out importance);
        }

        double confidence = 0.5;
        if (message.Payload.TryGetValue("confidence", out var confObj))
        {
            if (confObj is double d) confidence = d;
            else if (confObj is float f) confidence = f;
            else if (double.TryParse(confObj?.ToString(), out var parsed)) confidence = parsed;
        }

        var tags = Array.Empty<string>();
        if (message.Payload.TryGetValue("tags", out var tagsObj) && tagsObj is string[] tagArray)
        {
            tags = tagArray;
        }

        var domainHint = ExtractStringFromPayload(message, "domain_hint") ?? "universal";

        return new SemanticClassification(
            DataId: dataId,
            Importance: importance,
            Confidence: confidence,
            SemanticTags: tags,
            DomainHint: domainHint,
            ClassifiedAt: DateTimeOffset.UtcNow);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _orchestrator?.Dispose();
            _orchestrator = null;

            foreach (var subscription in _subscriptions)
            {
                try { subscription.Dispose(); }
                catch { /* Best-effort cleanup */ }
            }
            _subscriptions.Clear();

            foreach (var kvp in _strategies)
            {
                try { kvp.Value.Dispose(); }
                catch { /* Best-effort cleanup */ }
            }
            _strategies.Clear();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    protected override async ValueTask DisposeAsyncCore()
    {
        if (_orchestrator is not null)
        {
            await _orchestrator.DisposeAsync().ConfigureAwait(false);
            _orchestrator = null;
        }

        foreach (var subscription in _subscriptions)
        {
            try { subscription.Dispose(); }
            catch { /* Best-effort cleanup */ }
        }
        _subscriptions.Clear();

        foreach (var kvp in _strategies)
        {
            try { await kvp.Value.DisposeAsync().ConfigureAwait(false); }
            catch { /* Best-effort cleanup */ }
        }
        _strategies.Clear();

        await base.DisposeAsyncCore();
    }
}

/// <summary>
/// Null-object AI provider for environments where AI is unavailable.
/// All methods throw NotSupportedException, causing strategies to fall back to
/// non-AI behavior (rule-based classification, extraction-based summarization, etc.).
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Null AI provider for graceful degradation")]
internal sealed class NullAIProvider : IAIProvider
{
    /// <summary>
    /// Singleton instance of the null AI provider.
    /// </summary>
    public static readonly NullAIProvider Instance = new();

    private NullAIProvider() { }

    /// <inheritdoc/>
    public string ProviderId => "null";

    /// <inheritdoc/>
    public string DisplayName => "Null AI Provider";

    /// <inheritdoc/>
    public bool IsAvailable => false;

    /// <inheritdoc/>
    public AICapabilities Capabilities => AICapabilities.None;

    /// <inheritdoc/>
    public Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("AI provider is not available in this deployment.");

    /// <inheritdoc/>
    public async IAsyncEnumerable<AIStreamChunk> CompleteStreamingAsync(
        AIRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        throw new NotSupportedException("AI provider is not available in this deployment.");
#pragma warning disable CS0162 // Unreachable code -- required for async iterator method to satisfy compiler
        yield break;
#pragma warning restore CS0162
    }

    /// <inheritdoc/>
    public Task<float[]> GetEmbeddingsAsync(string text, CancellationToken ct = default) =>
        throw new NotSupportedException("AI provider is not available in this deployment.");

    /// <inheritdoc/>
    public Task<float[][]> GetEmbeddingsBatchAsync(string[] texts, CancellationToken ct = default) =>
        throw new NotSupportedException("AI provider is not available in this deployment.");
}
