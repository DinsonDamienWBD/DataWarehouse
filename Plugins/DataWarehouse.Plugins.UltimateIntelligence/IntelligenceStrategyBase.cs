using System.Collections.Concurrent;
using System.Diagnostics;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.Plugins.UltimateIntelligence;

/// <summary>
/// Abstract base class for intelligence strategies.
/// Provides common functionality for configuration, statistics tracking, and validation.
/// </summary>
public abstract class IntelligenceStrategyBase : IIntelligenceStrategy
{
    private long _totalOperations;
    private long _successfulOperations;
    private long _failedOperations;
    private long _totalTokensConsumed;
    private long _totalEmbeddingsGenerated;
    private long _totalVectorsStored;
    private long _totalSearches;
    private long _totalNodesCreated;
    private long _totalEdgesCreated;
    private long _totalLatencyMsTicks;
    private readonly DateTime _startTime = DateTime.UtcNow;
    private DateTime _lastOperationTime = DateTime.UtcNow;

    /// <summary>
    /// Configuration dictionary for this strategy.
    /// </summary>
    protected readonly ConcurrentDictionary<string, string> Configuration = new();

    /// <inheritdoc/>
    public abstract string StrategyId { get; }

    /// <inheritdoc/>
    public abstract string StrategyName { get; }

    /// <inheritdoc/>
    public abstract IntelligenceStrategyCategory Category { get; }

    /// <inheritdoc/>
    public abstract IntelligenceStrategyInfo Info { get; }

    /// <inheritdoc/>
    public virtual bool IsAvailable => Validate().IsValid;

    /// <summary>
    /// Sets a configuration value.
    /// </summary>
    /// <param name="key">Configuration key.</param>
    /// <param name="value">Configuration value.</param>
    public void Configure(string key, string value)
    {
        Configuration[key] = value;
    }

    /// <summary>
    /// Gets a configuration value.
    /// </summary>
    /// <param name="key">Configuration key.</param>
    /// <returns>Configuration value, or null if not set.</returns>
    protected string? GetConfig(string key)
    {
        return Configuration.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>
    /// Gets a required configuration value.
    /// </summary>
    /// <param name="key">Configuration key.</param>
    /// <returns>Configuration value.</returns>
    /// <exception cref="InvalidOperationException">If the configuration is not set.</exception>
    protected string GetRequiredConfig(string key)
    {
        return GetConfig(key)
            ?? throw new InvalidOperationException($"Required configuration '{key}' is not set for {StrategyId}");
    }

    /// <inheritdoc/>
    public virtual IntelligenceValidationResult Validate()
    {
        var issues = new List<string>();
        var warnings = new List<string>();

        foreach (var requirement in Info.ConfigurationRequirements)
        {
            var value = GetConfig(requirement.Key);

            if (requirement.Required && string.IsNullOrEmpty(value))
            {
                issues.Add($"Required configuration '{requirement.Key}' is not set");
            }
            else if (string.IsNullOrEmpty(value) && requirement.DefaultValue == null)
            {
                warnings.Add($"Optional configuration '{requirement.Key}' is not set and has no default");
            }
        }

        return new IntelligenceValidationResult
        {
            IsValid = issues.Count == 0,
            Issues = issues,
            Warnings = warnings
        };
    }

    /// <inheritdoc/>
    public virtual IntelligenceStatistics GetStatistics()
    {
        var totalOps = Interlocked.Read(ref _totalOperations);
        return new IntelligenceStatistics
        {
            TotalOperations = totalOps,
            SuccessfulOperations = Interlocked.Read(ref _successfulOperations),
            FailedOperations = Interlocked.Read(ref _failedOperations),
            TotalTokensConsumed = Interlocked.Read(ref _totalTokensConsumed),
            TotalEmbeddingsGenerated = Interlocked.Read(ref _totalEmbeddingsGenerated),
            TotalVectorsStored = Interlocked.Read(ref _totalVectorsStored),
            TotalSearches = Interlocked.Read(ref _totalSearches),
            TotalNodesCreated = Interlocked.Read(ref _totalNodesCreated),
            TotalEdgesCreated = Interlocked.Read(ref _totalEdgesCreated),
            AverageLatencyMs = totalOps > 0 ? Interlocked.Read(ref _totalLatencyMsTicks) / 1000.0 / totalOps : 0,
            StartTime = _startTime,
            LastOperationTime = _lastOperationTime
        };
    }

    /// <inheritdoc/>
    public virtual void ResetStatistics()
    {
        Interlocked.Exchange(ref _totalOperations, 0);
        Interlocked.Exchange(ref _successfulOperations, 0);
        Interlocked.Exchange(ref _failedOperations, 0);
        Interlocked.Exchange(ref _totalTokensConsumed, 0);
        Interlocked.Exchange(ref _totalEmbeddingsGenerated, 0);
        Interlocked.Exchange(ref _totalVectorsStored, 0);
        Interlocked.Exchange(ref _totalSearches, 0);
        Interlocked.Exchange(ref _totalNodesCreated, 0);
        Interlocked.Exchange(ref _totalEdgesCreated, 0);
        Interlocked.Exchange(ref _totalLatencyMsTicks, 0);
    }

    /// <summary>
    /// Records a successful operation.
    /// </summary>
    /// <param name="latencyMs">Operation latency in milliseconds.</param>
    protected void RecordSuccess(double latencyMs)
    {
        Interlocked.Increment(ref _totalOperations);
        Interlocked.Increment(ref _successfulOperations);
        Interlocked.Add(ref _totalLatencyMsTicks, (long)(latencyMs * 1000)); // Store as microseconds for precision
        _lastOperationTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Records a failed operation.
    /// </summary>
    protected void RecordFailure()
    {
        Interlocked.Increment(ref _totalOperations);
        Interlocked.Increment(ref _failedOperations);
        _lastOperationTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Records tokens consumed (for AI providers).
    /// </summary>
    protected void RecordTokens(int tokens)
    {
        Interlocked.Add(ref _totalTokensConsumed, tokens);
    }

    /// <summary>
    /// Records embeddings generated.
    /// </summary>
    protected void RecordEmbeddings(int count)
    {
        Interlocked.Add(ref _totalEmbeddingsGenerated, count);
    }

    /// <summary>
    /// Records vectors stored.
    /// </summary>
    protected void RecordVectorsStored(int count)
    {
        Interlocked.Add(ref _totalVectorsStored, count);
    }

    /// <summary>
    /// Records a search operation.
    /// </summary>
    protected void RecordSearch()
    {
        Interlocked.Increment(ref _totalSearches);
    }

    /// <summary>
    /// Records nodes created.
    /// </summary>
    protected void RecordNodesCreated(int count)
    {
        Interlocked.Add(ref _totalNodesCreated, count);
    }

    /// <summary>
    /// Records edges created.
    /// </summary>
    protected void RecordEdgesCreated(int count)
    {
        Interlocked.Add(ref _totalEdgesCreated, count);
    }

    /// <summary>
    /// Executes an operation with timing and error tracking.
    /// </summary>
    protected async Task<T> ExecuteWithTrackingAsync<T>(Func<Task<T>> operation)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await operation();
            sw.Stop();
            RecordSuccess(sw.Elapsed.TotalMilliseconds);
            return result;
        }
        catch
        {
            sw.Stop();
            RecordFailure();
            throw;
        }
    }

    /// <summary>
    /// Executes an operation with timing and error tracking.
    /// </summary>
    protected async Task ExecuteWithTrackingAsync(Func<Task> operation)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await operation();
            sw.Stop();
            RecordSuccess(sw.Elapsed.TotalMilliseconds);
        }
        catch
        {
            sw.Stop();
            RecordFailure();
            throw;
        }
    }

    /// <summary>
    /// Gets static knowledge about this Intelligence strategy.
    /// </summary>
    public virtual SDK.AI.KnowledgeObject GetStrategyKnowledge()
    {
        return new SDK.AI.KnowledgeObject
        {
            Id = $"intelligence.strategy.{StrategyId}",
            Topic = $"intelligence.{Category.ToString().ToLowerInvariant()}",
            SourcePluginId = "com.datawarehouse.intelligence.ultimate",
            SourcePluginName = StrategyName,
            KnowledgeType = "capability",
            Description = Info.Description,
            Payload = new Dictionary<string, object>
            {
                ["strategyId"] = StrategyId,
                ["category"] = Category.ToString(),
                ["provider"] = Info.ProviderName,
                ["capabilities"] = Info.Capabilities.ToString(),
                ["costTier"] = Info.CostTier,
                ["latencyTier"] = Info.LatencyTier,
                ["requiresNetwork"] = Info.RequiresNetworkAccess,
                ["supportsOffline"] = Info.SupportsOfflineMode
            },
            Tags = Info.Tags.Concat(new[] { "intelligence", Category.ToString().ToLowerInvariant() }).ToArray()
        };
    }

    /// <summary>
    /// Gets the registered capability for this strategy.
    /// </summary>
    public virtual SDK.Contracts.RegisteredCapability GetStrategyCapability()
    {
        return new SDK.Contracts.RegisteredCapability
        {
            CapabilityId = $"intelligence.strategy.{StrategyId}",
            DisplayName = StrategyName,
            Description = Info.Description,
            Category = SDK.Contracts.CapabilityCategory.AI,
            SubCategory = Category.ToString(),
            PluginId = "com.datawarehouse.intelligence.ultimate",
            PluginName = "Ultimate Intelligence",
            PluginVersion = "1.0.0",
            Tags = Info.Tags,
            Metadata = new Dictionary<string, object>
            {
                ["provider"] = Info.ProviderName,
                ["costTier"] = Info.CostTier,
                ["latencyTier"] = Info.LatencyTier,
                ["requiresNetwork"] = Info.RequiresNetworkAccess,
                ["supportsOffline"] = Info.SupportsOfflineMode
            },
            SemanticDescription = $"Use {Info.ProviderName} for {Info.Description}"
        };
    }
}

/// <summary>
/// Base class for AI provider strategies that implement IAIProvider.
/// </summary>
public abstract class AIProviderStrategyBase : IntelligenceStrategyBase, IAIProvider
{
    /// <inheritdoc/>
    public override IntelligenceStrategyCategory Category => IntelligenceStrategyCategory.AIProvider;

    /// <inheritdoc/>
    public abstract string ProviderId { get; }

    /// <inheritdoc/>
    public abstract string DisplayName { get; }

    /// <inheritdoc/>
    public abstract AICapabilities Capabilities { get; }

    /// <inheritdoc/>
    public abstract Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract IAsyncEnumerable<AIStreamChunk> CompleteStreamingAsync(AIRequest request, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<float[]> GetEmbeddingsAsync(string text, CancellationToken ct = default);

    /// <inheritdoc/>
    public virtual async Task<float[][]> GetEmbeddingsBatchAsync(string[] texts, CancellationToken ct = default)
    {
        var results = new float[texts.Length][];
        for (int i = 0; i < texts.Length; i++)
        {
            results[i] = await GetEmbeddingsAsync(texts[i], ct);
        }
        return results;
    }
}

/// <summary>
/// Base class for vector store strategies that implement IVectorStore.
/// </summary>
public abstract class VectorStoreStrategyBase : IntelligenceStrategyBase, IVectorStore
{
    /// <inheritdoc/>
    public override IntelligenceStrategyCategory Category => IntelligenceStrategyCategory.VectorStore;

    /// <inheritdoc/>
    public abstract Task StoreAsync(string id, float[] vector, Dictionary<string, object>? metadata = null, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task StoreBatchAsync(IEnumerable<VectorEntry> entries, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<VectorEntry?> GetAsync(string id, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task DeleteAsync(string id, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<IEnumerable<VectorMatch>> SearchAsync(float[] query, int topK = 10, float minScore = 0.0f, Dictionary<string, object>? filter = null, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<long> CountAsync(CancellationToken ct = default);
}

/// <summary>
/// Base class for knowledge graph strategies that implement IKnowledgeGraph.
/// </summary>
public abstract class KnowledgeGraphStrategyBase : IntelligenceStrategyBase, IKnowledgeGraph
{
    /// <inheritdoc/>
    public override IntelligenceStrategyCategory Category => IntelligenceStrategyCategory.KnowledgeGraph;

    /// <inheritdoc/>
    public abstract Task<GraphNode> AddNodeAsync(string label, Dictionary<string, object>? properties = null, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<GraphEdge> AddEdgeAsync(string fromNodeId, string toNodeId, string relationship, Dictionary<string, object>? properties = null, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<GraphNode?> GetNodeAsync(string nodeId, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<IEnumerable<GraphEdge>> GetEdgesAsync(string nodeId, EdgeDirection direction = EdgeDirection.Both, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<IEnumerable<GraphNode>> FindNodesByLabelAsync(string label, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<IEnumerable<GraphNode>> FindNodesByPropertyAsync(string key, object value, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<GraphTraversalResult> TraverseAsync(string startNodeId, GraphTraversalOptions options, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<GraphPath?> FindPathAsync(string fromNodeId, string toNodeId, int maxDepth = 10, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<GraphQueryResult> QueryAsync(string query, Dictionary<string, object>? parameters = null, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task DeleteNodeAsync(string nodeId, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task DeleteEdgeAsync(string edgeId, CancellationToken ct = default);
}

/// <summary>
/// Base class for feature strategies (higher-level AI features).
/// </summary>
public abstract class FeatureStrategyBase : IntelligenceStrategyBase
{
    /// <inheritdoc/>
    public override IntelligenceStrategyCategory Category => IntelligenceStrategyCategory.Feature;

    /// <summary>
    /// Gets the AI provider to use for this feature.
    /// </summary>
    protected IAIProvider? AIProvider { get; private set; }

    /// <summary>
    /// Gets the vector store to use for this feature.
    /// </summary>
    protected IVectorStore? VectorStore { get; private set; }

    /// <summary>
    /// Gets the knowledge graph to use for this feature.
    /// </summary>
    protected IKnowledgeGraph? KnowledgeGraph { get; private set; }

    /// <summary>
    /// Sets the AI provider for this feature.
    /// </summary>
    public void SetAIProvider(IAIProvider provider)
    {
        AIProvider = provider;
    }

    /// <summary>
    /// Sets the vector store for this feature.
    /// </summary>
    public void SetVectorStore(IVectorStore store)
    {
        VectorStore = store;
    }

    /// <summary>
    /// Sets the knowledge graph for this feature.
    /// </summary>
    public void SetKnowledgeGraph(IKnowledgeGraph graph)
    {
        KnowledgeGraph = graph;
    }
}
