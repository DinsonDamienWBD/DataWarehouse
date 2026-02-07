using DataWarehouse.SDK.AI;

namespace DataWarehouse.Plugins.UltimateIntelligence;

/// <summary>
/// Strategy category for intelligence operations.
/// </summary>
public enum IntelligenceStrategyCategory
{
    /// <summary>AI provider strategy (wraps IAIProvider).</summary>
    AIProvider,

    /// <summary>Vector store strategy (wraps IVectorStore).</summary>
    VectorStore,

    /// <summary>Knowledge graph strategy (wraps IKnowledgeGraph).</summary>
    KnowledgeGraph,

    /// <summary>Feature strategy (higher-level AI features).</summary>
    Feature,

    /// <summary>Long-Term Memory (LTM) strategy for persistent memory across sessions.</summary>
    LongTermMemory,

    /// <summary>Large Tabular Model strategy for structured data AI.</summary>
    TabularModel,

    /// <summary>AI Agent strategy for autonomous task execution.</summary>
    Agent
}

/// <summary>
/// Base interface for all intelligence strategies in the Ultimate Intelligence plugin.
/// Provides a unified abstraction across AI providers, vector stores, knowledge graphs, and features.
/// </summary>
public interface IIntelligenceStrategy
{
    /// <summary>
    /// Gets the unique identifier for this strategy.
    /// Format: category-provider-variant (e.g., "provider-openai-gpt4", "vector-pinecone-default").
    /// </summary>
    string StrategyId { get; }

    /// <summary>
    /// Gets the human-readable name of this strategy.
    /// </summary>
    string StrategyName { get; }

    /// <summary>
    /// Gets the category of this strategy.
    /// </summary>
    IntelligenceStrategyCategory Category { get; }

    /// <summary>
    /// Gets information about this strategy's capabilities.
    /// </summary>
    IntelligenceStrategyInfo Info { get; }

    /// <summary>
    /// Gets whether this strategy is currently available and configured.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Validates the strategy configuration.
    /// </summary>
    /// <returns>Validation result with any issues found.</returns>
    IntelligenceValidationResult Validate();

    /// <summary>
    /// Gets usage statistics for this strategy.
    /// </summary>
    IntelligenceStatistics GetStatistics();

    /// <summary>
    /// Resets usage statistics.
    /// </summary>
    void ResetStatistics();
}

/// <summary>
/// Information about an intelligence strategy's capabilities.
/// </summary>
public sealed record IntelligenceStrategyInfo
{
    /// <summary>Provider or service name (e.g., "OpenAI", "Pinecone", "Neo4j").</summary>
    public required string ProviderName { get; init; }

    /// <summary>Description of the strategy's capabilities.</summary>
    public required string Description { get; init; }

    /// <summary>Supported features for this strategy.</summary>
    public required IntelligenceCapabilities Capabilities { get; init; }

    /// <summary>Configuration requirements.</summary>
    public required ConfigurationRequirement[] ConfigurationRequirements { get; init; }

    /// <summary>Estimated cost tier (1=free, 2=low, 3=medium, 4=high, 5=enterprise).</summary>
    public int CostTier { get; init; } = 3;

    /// <summary>Latency characteristics (1=very fast, 5=very slow).</summary>
    public int LatencyTier { get; init; } = 3;

    /// <summary>Whether this strategy requires external network access.</summary>
    public bool RequiresNetworkAccess { get; init; } = true;

    /// <summary>Whether this strategy can run offline/locally.</summary>
    public bool SupportsOfflineMode { get; init; }

    /// <summary>Semantic tags for discovery.</summary>
    public string[] Tags { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Configuration requirement for a strategy.
/// </summary>
public sealed record ConfigurationRequirement
{
    /// <summary>Configuration key name.</summary>
    public required string Key { get; init; }

    /// <summary>Human-readable description.</summary>
    public required string Description { get; init; }

    /// <summary>Whether this configuration is required.</summary>
    public bool Required { get; init; } = true;

    /// <summary>Default value if not configured.</summary>
    public string? DefaultValue { get; init; }

    /// <summary>Whether this is a secret (should be masked).</summary>
    public bool IsSecret { get; init; }
}

/// <summary>
/// Capabilities flags for intelligence strategies.
/// </summary>
[Flags]
public enum IntelligenceCapabilities : long
{
    None = 0,

    // AI Provider capabilities
    TextCompletion = 1 << 0,
    ChatCompletion = 1 << 1,
    Streaming = 1 << 2,
    Embeddings = 1 << 3,
    ImageGeneration = 1 << 4,
    ImageAnalysis = 1 << 5,
    FunctionCalling = 1 << 6,
    CodeGeneration = 1 << 7,

    // Vector Store capabilities
    VectorStorage = 1 << 8,
    VectorSearch = 1 << 9,
    MetadataFiltering = 1 << 10,
    BatchOperations = 1 << 11,

    // Knowledge Graph capabilities
    NodeManagement = 1 << 12,
    EdgeManagement = 1 << 13,
    GraphTraversal = 1 << 14,
    GraphQueries = 1 << 15,
    PathFinding = 1 << 16,

    // Feature capabilities
    SemanticSearch = 1 << 17,
    Classification = 1 << 18,
    AnomalyDetection = 1 << 19,
    Prediction = 1 << 20,
    Clustering = 1 << 21,
    Summarization = 1 << 22,

    // Long-Term Memory capabilities
    MemoryStorage = 1 << 23,
    MemoryRetrieval = 1 << 24,
    MemoryConsolidation = 1 << 25,
    EpisodicMemory = 1 << 26,
    SemanticMemory = 1 << 27,
    WorkingMemory = 1 << 28,
    MemoryDecay = 1 << 29,
    HierarchicalMemory = 1 << 30,

    // Large Tabular Model capabilities
    TabularClassification = 1L << 31,
    TabularRegression = 1L << 32,
    TabularGeneration = 1L << 33,
    FeatureEngineering = 1L << 34,
    MissingValueImputation = 1L << 35,
    AnomalyDetectionTabular = 1L << 36,
    TimeSeriesForecasting = 1L << 37,

    // Agent capabilities
    TaskPlanning = 1L << 38,
    ToolUse = 1L << 39,
    ReasoningChain = 1L << 40,
    SelfReflection = 1L << 41,
    MultiAgentCollaboration = 1L << 42,

    // Common capability groups
    AllAIProvider = TextCompletion | ChatCompletion | Streaming | Embeddings | FunctionCalling,
    AllVectorStore = VectorStorage | VectorSearch | MetadataFiltering | BatchOperations,
    AllKnowledgeGraph = NodeManagement | EdgeManagement | GraphTraversal | GraphQueries | PathFinding,
    AllLongTermMemory = MemoryStorage | MemoryRetrieval | MemoryConsolidation | EpisodicMemory | SemanticMemory,
    AllTabularModel = TabularClassification | TabularRegression | FeatureEngineering | MissingValueImputation,
    AllAgent = TaskPlanning | ToolUse | ReasoningChain | SelfReflection
}

/// <summary>
/// Validation result for strategy configuration.
/// </summary>
public sealed class IntelligenceValidationResult
{
    /// <summary>Whether the configuration is valid.</summary>
    public bool IsValid { get; init; } = true;

    /// <summary>List of validation issues.</summary>
    public List<string> Issues { get; init; } = new();

    /// <summary>List of warnings (non-blocking).</summary>
    public List<string> Warnings { get; init; } = new();

    /// <summary>Creates a successful validation result.</summary>
    public static IntelligenceValidationResult Success() => new();

    /// <summary>Creates a failed validation result with issues.</summary>
    public static IntelligenceValidationResult Failure(params string[] issues) =>
        new() { IsValid = false, Issues = new List<string>(issues) };
}

/// <summary>
/// Usage statistics for intelligence strategies.
/// </summary>
public sealed class IntelligenceStatistics
{
    /// <summary>Total number of operations performed.</summary>
    public long TotalOperations { get; set; }

    /// <summary>Number of successful operations.</summary>
    public long SuccessfulOperations { get; set; }

    /// <summary>Number of failed operations.</summary>
    public long FailedOperations { get; set; }

    /// <summary>Total tokens consumed (for AI providers).</summary>
    public long TotalTokensConsumed { get; set; }

    /// <summary>Total embeddings generated.</summary>
    public long TotalEmbeddingsGenerated { get; set; }

    /// <summary>Total vectors stored.</summary>
    public long TotalVectorsStored { get; set; }

    /// <summary>Total searches performed.</summary>
    public long TotalSearches { get; set; }

    /// <summary>Total graph nodes created.</summary>
    public long TotalNodesCreated { get; set; }

    /// <summary>Total graph edges created.</summary>
    public long TotalEdgesCreated { get; set; }

    /// <summary>Total memories stored (for LTM).</summary>
    public long TotalMemoriesStored { get; set; }

    /// <summary>Total memories retrieved (for LTM).</summary>
    public long TotalMemoriesRetrieved { get; set; }

    /// <summary>Total memory consolidations performed (for LTM).</summary>
    public long TotalConsolidations { get; set; }

    /// <summary>Total tabular predictions made.</summary>
    public long TotalTabularPredictions { get; set; }

    /// <summary>Total rows processed by tabular models.</summary>
    public long TotalRowsProcessed { get; set; }

    /// <summary>Total agent tasks executed.</summary>
    public long TotalAgentTasks { get; set; }

    /// <summary>Average operation latency in milliseconds.</summary>
    public double AverageLatencyMs { get; set; }

    /// <summary>Timestamp when statistics tracking started.</summary>
    public DateTime StartTime { get; init; } = DateTime.UtcNow;

    /// <summary>Timestamp of last operation.</summary>
    public DateTime LastOperationTime { get; set; } = DateTime.UtcNow;
}
