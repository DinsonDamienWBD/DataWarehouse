using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence;

/// <summary>
/// Handles the Intelligence discovery protocol for T127.
/// Subscribes to discovery requests and broadcasts availability announcements.
/// </summary>
public sealed class IntelligenceDiscoveryHandler : IDisposable
{
    private readonly UltimateIntelligencePlugin _plugin;
    private readonly IMessageBus _messageBus;
    private readonly List<IDisposable> _subscriptions = new();
    private bool _disposed;

    /// <summary>
    /// Creates a new discovery handler for the specified plugin.
    /// </summary>
    /// <param name="plugin">The Intelligence plugin to handle discovery for.</param>
    /// <param name="messageBus">The message bus for communication.</param>
    public IntelligenceDiscoveryHandler(UltimateIntelligencePlugin plugin, IMessageBus messageBus)
    {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
    }

    /// <summary>
    /// Starts the discovery handler by subscribing to discovery topics.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task StartAsync(CancellationToken ct = default)
    {
        // Subscribe to discovery requests
        var discoverySub = _messageBus.Subscribe(IntelligenceTopics.Discover, HandleDiscoveryRequestAsync);
        _subscriptions.Add(discoverySub);

        // Subscribe to capability queries
        var capabilitySub = _messageBus.Subscribe(IntelligenceTopics.QueryCapability, HandleCapabilityQueryAsync);
        _subscriptions.Add(capabilitySub);

        // Broadcast availability on startup
        await BroadcastAvailabilityAsync(ct);
    }

    /// <summary>
    /// Stops the discovery handler and broadcasts unavailability.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task StopAsync(CancellationToken ct = default)
    {
        // Broadcast unavailability
        await BroadcastUnavailabilityAsync(ct);

        // Dispose all subscriptions
        foreach (var sub in _subscriptions)
        {
            try { sub.Dispose(); } catch { }
        }
        _subscriptions.Clear();
    }

    /// <summary>
    /// Handles incoming discovery requests.
    /// </summary>
    private async Task HandleDiscoveryRequestAsync(PluginMessage message)
    {
        // Build capability response
        var response = BuildCapabilityResponse();

        // Send response
        var responseMessage = new PluginMessage
        {
            Type = "intelligence.discover.response",
            CorrelationId = message.CorrelationId,
            Source = _plugin.Id,
            Payload = response.ToDictionary()
        };

        await _messageBus.PublishAsync(IntelligenceTopics.DiscoverResponse, responseMessage);
    }

    /// <summary>
    /// Handles capability query requests.
    /// </summary>
    private async Task HandleCapabilityQueryAsync(PluginMessage message)
    {
        var response = BuildCapabilityResponse();

        // If specific capabilities were requested, filter
        if (message.Payload.TryGetValue("requestedCapabilities", out var reqCaps))
        {
            var requested = IntelligenceCapabilities.None;
            if (reqCaps is IntelligenceCapabilities ic)
                requested = ic;
            else if (reqCaps is long longVal)
                requested = (IntelligenceCapabilities)longVal;

            // Check if all requested capabilities are available
            var hasAll = (response.Capabilities & requested) == requested;
            var payload = response.ToDictionary();
            payload["hasRequestedCapabilities"] = hasAll;
            payload["matchedCapabilities"] = (long)(response.Capabilities & requested);

            var responseMessage = new PluginMessage
            {
                Type = "intelligence.capability.query.response",
                CorrelationId = message.CorrelationId,
                Source = _plugin.Id,
                Payload = payload
            };

            await _messageBus.PublishAsync(IntelligenceTopics.QueryCapabilityResponse, responseMessage);
        }
        else
        {
            var responseMessage = new PluginMessage
            {
                Type = "intelligence.capability.query.response",
                CorrelationId = message.CorrelationId,
                Source = _plugin.Id,
                Payload = response.ToDictionary()
            };

            await _messageBus.PublishAsync(IntelligenceTopics.QueryCapabilityResponse, responseMessage);
        }
    }

    /// <summary>
    /// Broadcasts that Intelligence is available.
    /// </summary>
    public async Task BroadcastAvailabilityAsync(CancellationToken ct = default)
    {
        var response = BuildCapabilityResponse();

        var message = new PluginMessage
        {
            Type = "intelligence.available",
            Source = _plugin.Id,
            Payload = response.ToDictionary()
        };

        await _messageBus.PublishAsync(IntelligenceTopics.Available, message, ct);
    }

    /// <summary>
    /// Broadcasts that Intelligence is becoming unavailable.
    /// </summary>
    public async Task BroadcastUnavailabilityAsync(CancellationToken ct = default)
    {
        var message = new PluginMessage
        {
            Type = "intelligence.unavailable",
            Source = _plugin.Id,
            Payload = new Dictionary<string, object>
            {
                ["pluginId"] = _plugin.Id,
                ["pluginName"] = _plugin.Name,
                ["timestamp"] = DateTimeOffset.UtcNow
            }
        };

        await _messageBus.PublishAsync(IntelligenceTopics.Unavailable, message, ct);
    }

    /// <summary>
    /// Broadcasts that capabilities have changed.
    /// </summary>
    public async Task BroadcastCapabilitiesChangedAsync(CancellationToken ct = default)
    {
        var response = BuildCapabilityResponse();

        var message = new PluginMessage
        {
            Type = "intelligence.capabilities.changed",
            Source = _plugin.Id,
            Payload = response.ToDictionary()
        };

        await _messageBus.PublishAsync(IntelligenceTopics.CapabilitiesChanged, message, ct);
    }

    /// <summary>
    /// Builds a capability response based on current plugin state.
    /// </summary>
    private IntelligenceCapabilityResponse BuildCapabilityResponse()
    {
        // Aggregate capabilities from all available strategies
        var capabilities = GetAggregateCapabilities();

        // Get active strategy names
        var providers = _plugin.GetStrategiesByCategory(IntelligenceStrategyCategory.AIProvider)
            .Where(s => s.IsAvailable)
            .Select(s => s.Info.ProviderName)
            .ToArray();

        var vectorStores = _plugin.GetStrategiesByCategory(IntelligenceStrategyCategory.VectorStore)
            .Where(s => s.IsAvailable)
            .Select(s => s.Info.ProviderName)
            .ToArray();

        var graphs = _plugin.GetStrategiesByCategory(IntelligenceStrategyCategory.KnowledgeGraph)
            .Where(s => s.IsAvailable)
            .Select(s => s.Info.ProviderName)
            .ToArray();

        var features = _plugin.GetStrategiesByCategory(IntelligenceStrategyCategory.Feature)
            .Where(s => s.IsAvailable)
            .Select(s => s.StrategyName)
            .ToArray();

        var stats = _plugin.GetPluginStatistics();

        return new IntelligenceCapabilityResponse
        {
            Available = stats.AvailableStrategies > 0,
            Capabilities = capabilities,
            Version = _plugin.Version,
            PluginId = _plugin.Id,
            PluginName = _plugin.Name,
            ActiveProviders = providers,
            ActiveVectorStores = vectorStores,
            ActiveKnowledgeGraphs = graphs,
            ActiveFeatures = features,
            Metadata = new Dictionary<string, object>
            {
                ["totalStrategies"] = stats.TotalStrategies,
                ["availableStrategies"] = stats.AvailableStrategies,
                ["totalOperations"] = stats.TotalOperations,
                ["averageLatencyMs"] = stats.AverageLatencyMs
            }
        };
    }

    /// <summary>
    /// Aggregates capabilities from all available strategies into SDK IntelligenceCapabilities.
    /// </summary>
    private IntelligenceCapabilities GetAggregateCapabilities()
    {
        var caps = IntelligenceCapabilities.None;

        foreach (var strategy in _plugin.GetRegisteredStrategyIds().Select(id => _plugin.GetStrategy(id)).Where(s => s?.IsAvailable == true))
        {
            var strategyCaps = strategy!.Info.Capabilities;

            // Map plugin IntelligenceCapabilities to SDK IntelligenceCapabilities
            // AI Provider capabilities
            if (strategyCaps.HasFlag(Plugins.UltimateIntelligence.IntelligenceCapabilities.TextCompletion))
                caps |= IntelligenceCapabilities.TextCompletion;
            if (strategyCaps.HasFlag(Plugins.UltimateIntelligence.IntelligenceCapabilities.ChatCompletion))
                caps |= IntelligenceCapabilities.Conversation;
            if (strategyCaps.HasFlag(Plugins.UltimateIntelligence.IntelligenceCapabilities.Streaming))
                caps |= IntelligenceCapabilities.Streaming;
            if (strategyCaps.HasFlag(Plugins.UltimateIntelligence.IntelligenceCapabilities.Embeddings))
                caps |= IntelligenceCapabilities.Embeddings;
            if (strategyCaps.HasFlag(Plugins.UltimateIntelligence.IntelligenceCapabilities.ImageGeneration))
                caps |= IntelligenceCapabilities.ContentGeneration;
            if (strategyCaps.HasFlag(Plugins.UltimateIntelligence.IntelligenceCapabilities.ImageAnalysis))
                caps |= IntelligenceCapabilities.ImageAnalysis;
            if (strategyCaps.HasFlag(Plugins.UltimateIntelligence.IntelligenceCapabilities.FunctionCalling))
                caps |= IntelligenceCapabilities.FunctionCalling;
            if (strategyCaps.HasFlag(Plugins.UltimateIntelligence.IntelligenceCapabilities.CodeGeneration))
                caps |= IntelligenceCapabilities.CodeGeneration;

            // Feature capabilities
            if (strategyCaps.HasFlag(Plugins.UltimateIntelligence.IntelligenceCapabilities.SemanticSearch))
                caps |= IntelligenceCapabilities.SemanticSearch;
            if (strategyCaps.HasFlag(Plugins.UltimateIntelligence.IntelligenceCapabilities.Classification))
                caps |= IntelligenceCapabilities.Classification;
            if (strategyCaps.HasFlag(Plugins.UltimateIntelligence.IntelligenceCapabilities.AnomalyDetection))
                caps |= IntelligenceCapabilities.AnomalyDetection;
            if (strategyCaps.HasFlag(Plugins.UltimateIntelligence.IntelligenceCapabilities.Prediction))
                caps |= IntelligenceCapabilities.Prediction;
            if (strategyCaps.HasFlag(Plugins.UltimateIntelligence.IntelligenceCapabilities.Clustering))
                caps |= IntelligenceCapabilities.Clustering;
            if (strategyCaps.HasFlag(Plugins.UltimateIntelligence.IntelligenceCapabilities.Summarization))
                caps |= IntelligenceCapabilities.Summarization;

            // Vector store capabilities
            if (strategyCaps.HasFlag(Plugins.UltimateIntelligence.IntelligenceCapabilities.VectorStorage))
                caps |= IntelligenceCapabilities.SemanticSearch;
            if (strategyCaps.HasFlag(Plugins.UltimateIntelligence.IntelligenceCapabilities.VectorSearch))
                caps |= IntelligenceCapabilities.SemanticSearch;
        }

        // Add common capabilities that are always available when we have strategies
        if (caps != IntelligenceCapabilities.None)
        {
            // If we have embeddings, we can do many things
            if (caps.HasFlag(IntelligenceCapabilities.Embeddings))
            {
                caps |= IntelligenceCapabilities.SimilarityScoring;
                caps |= IntelligenceCapabilities.Clustering;
            }

            // If we have conversation, we can do NLP
            if (caps.HasFlag(IntelligenceCapabilities.Conversation) || caps.HasFlag(IntelligenceCapabilities.TextCompletion))
            {
                caps |= IntelligenceCapabilities.NLP;
                caps |= IntelligenceCapabilities.IntentRecognition;
                caps |= IntelligenceCapabilities.EntityExtraction;
                caps |= IntelligenceCapabilities.KeywordExtraction;
                caps |= IntelligenceCapabilities.SentimentAnalysis;
                caps |= IntelligenceCapabilities.QuestionAnswering;
            }
        }

        return caps;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var sub in _subscriptions)
        {
            try { sub.Dispose(); } catch { }
        }
        _subscriptions.Clear();
    }
}
