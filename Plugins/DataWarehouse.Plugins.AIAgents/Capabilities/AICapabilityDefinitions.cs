// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.ComponentModel;
using System.Reflection;

namespace DataWarehouse.Plugins.AIAgents.Capabilities;

/// <summary>
/// Attribute to link a sub-capability to its parent capability.
/// Used for hierarchical capability management where toggling a parent
/// can optionally affect all children (based on ChildrenMirrorParent setting).
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public sealed class ParentCapabilityAttribute : Attribute
{
    /// <summary>
    /// Gets the parent capability that this sub-capability belongs to.
    /// </summary>
    public AICapability Parent { get; }

    /// <summary>
    /// Creates a new instance of the ParentCapabilityAttribute.
    /// </summary>
    /// <param name="parent">The parent capability.</param>
    public ParentCapabilityAttribute(AICapability parent)
    {
        Parent = parent;
    }
}

/// <summary>
/// Defines the primary AI capability domains available in the DataWarehouse AI platform.
/// Each capability represents a major functional area that can be enabled/disabled at the instance level.
/// </summary>
[Flags]
public enum AICapability
{
    /// <summary>
    /// No capabilities enabled.
    /// </summary>
    None = 0,

    /// <summary>
    /// Interactive chat and conversational AI.
    /// Includes multi-turn conversations, context management, and dialogue systems.
    /// </summary>
    [Description("Interactive Chat and Conversations")]
    Chat = 1 << 0,

    /// <summary>
    /// Natural Language Processing capabilities.
    /// Includes query parsing, intent recognition, entity extraction, and language understanding.
    /// </summary>
    [Description("Natural Language Processing")]
    NLP = 1 << 1,

    /// <summary>
    /// Semantic understanding and analysis.
    /// Includes document understanding, semantic search preparation, and content classification.
    /// </summary>
    [Description("Semantic Understanding")]
    Semantics = 1 << 2,

    /// <summary>
    /// Text and content generation.
    /// Includes summaries, descriptions, code generation, and creative writing.
    /// </summary>
    [Description("Content Generation")]
    Generation = 1 << 3,

    /// <summary>
    /// Vector embeddings generation.
    /// Includes text embeddings, document embeddings, and similarity computations.
    /// </summary>
    [Description("Embeddings Generation")]
    Embeddings = 1 << 4,

    /// <summary>
    /// Vision and image understanding.
    /// Includes image analysis, OCR, visual search, and multimodal understanding.
    /// </summary>
    [Description("Vision and Image Analysis")]
    Vision = 1 << 5,

    /// <summary>
    /// Audio processing capabilities.
    /// Includes speech-to-text, text-to-speech, audio analysis, and transcription.
    /// </summary>
    [Description("Audio Processing")]
    Audio = 1 << 6,

    /// <summary>
    /// AI Operations and autonomous management.
    /// Includes anomaly detection, optimization, predictive analytics, and self-healing.
    /// </summary>
    [Description("AI Operations")]
    AIOps = 1 << 7,

    /// <summary>
    /// Knowledge management and retrieval.
    /// Includes knowledge graphs, RAG, fact extraction, and knowledge base management.
    /// </summary>
    [Description("Knowledge Management")]
    Knowledge = 1 << 8,

    /// <summary>
    /// Code understanding and generation.
    /// Includes code analysis, refactoring suggestions, and automated code generation.
    /// </summary>
    [Description("Code Intelligence")]
    CodeIntelligence = 1 << 9,

    /// <summary>
    /// Data analysis and insights.
    /// Includes pattern recognition, trend analysis, and statistical insights.
    /// </summary>
    [Description("Data Analysis")]
    DataAnalysis = 1 << 10,

    /// <summary>
    /// Security and compliance analysis.
    /// Includes PII detection, threat analysis, and compliance checking.
    /// </summary>
    [Description("Security Analysis")]
    Security = 1 << 11,

    /// <summary>
    /// All capabilities enabled.
    /// </summary>
    All = Chat | NLP | Semantics | Generation | Embeddings | Vision | Audio | AIOps | Knowledge | CodeIntelligence | DataAnalysis | Security
}

/// <summary>
/// Defines granular sub-capabilities within each major capability domain.
/// These provide fine-grained control over specific AI functions.
/// Each sub-capability is linked to a parent capability via the [ParentCapability] attribute.
/// </summary>
public enum AISubCapability
{
    // ============================================
    // Chat Sub-Capabilities (100-199)
    // ============================================

    /// <summary>Single-turn question answering.</summary>
    [ParentCapability(AICapability.Chat)]
    ChatQA = 100,

    /// <summary>Multi-turn conversational dialogue.</summary>
    [ParentCapability(AICapability.Chat)]
    ChatMultiTurn = 101,

    /// <summary>Function/tool calling from chat.</summary>
    [ParentCapability(AICapability.Chat)]
    ChatFunctionCalling = 102,

    /// <summary>Streaming chat responses.</summary>
    [ParentCapability(AICapability.Chat)]
    ChatStreaming = 103,

    /// <summary>System prompt customization.</summary>
    [ParentCapability(AICapability.Chat)]
    ChatSystemPrompts = 104,

    // ============================================
    // NLP Sub-Capabilities (200-299)
    // ============================================

    /// <summary>Natural language to structured query parsing.</summary>
    [ParentCapability(AICapability.NLP)]
    NLPQueryParsing = 200,

    /// <summary>Intent recognition and classification.</summary>
    [ParentCapability(AICapability.NLP)]
    NLPIntentRecognition = 201,

    /// <summary>Named entity extraction.</summary>
    [ParentCapability(AICapability.NLP)]
    NLPEntityExtraction = 202,

    /// <summary>Sentiment analysis.</summary>
    [ParentCapability(AICapability.NLP)]
    NLPSentimentAnalysis = 203,

    /// <summary>Language detection.</summary>
    [ParentCapability(AICapability.NLP)]
    NLPLanguageDetection = 204,

    /// <summary>Text translation.</summary>
    [ParentCapability(AICapability.NLP)]
    NLPTranslation = 205,

    /// <summary>Keyword extraction.</summary>
    [ParentCapability(AICapability.NLP)]
    NLPKeywordExtraction = 206,

    /// <summary>Topic modeling and classification.</summary>
    [ParentCapability(AICapability.NLP)]
    NLPTopicModeling = 207,

    // ============================================
    // Semantics Sub-Capabilities (300-399)
    // ============================================

    /// <summary>Automatic content categorization.</summary>
    [ParentCapability(AICapability.Semantics)]
    SemanticsCategorization = 300,

    /// <summary>Automatic tagging and labeling.</summary>
    [ParentCapability(AICapability.Semantics)]
    SemanticsAutoTagging = 301,

    /// <summary>Document similarity analysis.</summary>
    [ParentCapability(AICapability.Semantics)]
    SemanticsSimilarity = 302,

    /// <summary>Duplicate/near-duplicate detection.</summary>
    [ParentCapability(AICapability.Semantics)]
    SemanticsDuplicateDetection = 303,

    /// <summary>Content clustering.</summary>
    [ParentCapability(AICapability.Semantics)]
    SemanticsClustering = 304,

    /// <summary>Semantic search query understanding.</summary>
    [ParentCapability(AICapability.Semantics)]
    SemanticsSearchEnhancement = 305,

    /// <summary>Relationship extraction between entities.</summary>
    [ParentCapability(AICapability.Semantics)]
    SemanticsRelationshipExtraction = 306,

    // ============================================
    // Generation Sub-Capabilities (400-499)
    // ============================================

    /// <summary>Document summarization.</summary>
    [ParentCapability(AICapability.Generation)]
    GenerationSummarization = 400,

    /// <summary>Metadata description generation.</summary>
    [ParentCapability(AICapability.Generation)]
    GenerationDescriptions = 401,

    /// <summary>Title and headline generation.</summary>
    [ParentCapability(AICapability.Generation)]
    GenerationTitles = 402,

    /// <summary>Query suggestion generation.</summary>
    [ParentCapability(AICapability.Generation)]
    GenerationQuerySuggestions = 403,

    /// <summary>Report generation.</summary>
    [ParentCapability(AICapability.Generation)]
    GenerationReports = 404,

    /// <summary>Template-based content generation.</summary>
    [ParentCapability(AICapability.Generation)]
    GenerationTemplates = 405,

    /// <summary>Data-to-text generation.</summary>
    [ParentCapability(AICapability.Generation)]
    GenerationDataNarration = 406,

    // ============================================
    // Embeddings Sub-Capabilities (500-599)
    // ============================================

    /// <summary>Text/document embeddings.</summary>
    [ParentCapability(AICapability.Embeddings)]
    EmbeddingsText = 500,

    /// <summary>Query embeddings for search.</summary>
    [ParentCapability(AICapability.Embeddings)]
    EmbeddingsQuery = 501,

    /// <summary>Image embeddings.</summary>
    [ParentCapability(AICapability.Embeddings)]
    EmbeddingsImage = 502,

    /// <summary>Code embeddings.</summary>
    [ParentCapability(AICapability.Embeddings)]
    EmbeddingsCode = 503,

    /// <summary>Multi-modal embeddings.</summary>
    [ParentCapability(AICapability.Embeddings)]
    EmbeddingsMultiModal = 504,

    /// <summary>Sparse embeddings (e.g., SPLADE).</summary>
    [ParentCapability(AICapability.Embeddings)]
    EmbeddingsSparse = 505,

    // ============================================
    // Vision Sub-Capabilities (600-699)
    // ============================================

    /// <summary>Image content description.</summary>
    [ParentCapability(AICapability.Vision)]
    VisionImageDescription = 600,

    /// <summary>Optical character recognition.</summary>
    [ParentCapability(AICapability.Vision)]
    VisionOCR = 601,

    /// <summary>Object detection and localization.</summary>
    [ParentCapability(AICapability.Vision)]
    VisionObjectDetection = 602,

    /// <summary>Face detection (not recognition).</summary>
    [ParentCapability(AICapability.Vision)]
    VisionFaceDetection = 603,

    /// <summary>Document/form understanding.</summary>
    [ParentCapability(AICapability.Vision)]
    VisionDocumentAnalysis = 604,

    /// <summary>Chart and diagram understanding.</summary>
    [ParentCapability(AICapability.Vision)]
    VisionChartAnalysis = 605,

    /// <summary>Image classification.</summary>
    [ParentCapability(AICapability.Vision)]
    VisionClassification = 606,

    /// <summary>Visual question answering.</summary>
    [ParentCapability(AICapability.Vision)]
    VisionVQA = 607,

    // ============================================
    // Audio Sub-Capabilities (700-799)
    // ============================================

    /// <summary>Speech-to-text transcription.</summary>
    [ParentCapability(AICapability.Audio)]
    AudioTranscription = 700,

    /// <summary>Text-to-speech synthesis.</summary>
    [ParentCapability(AICapability.Audio)]
    AudioSpeechSynthesis = 701,

    /// <summary>Speaker diarization.</summary>
    [ParentCapability(AICapability.Audio)]
    AudioSpeakerDiarization = 702,

    /// <summary>Audio event detection.</summary>
    [ParentCapability(AICapability.Audio)]
    AudioEventDetection = 703,

    /// <summary>Music analysis and tagging.</summary>
    [ParentCapability(AICapability.Audio)]
    AudioMusicAnalysis = 704,

    /// <summary>Audio summarization.</summary>
    [ParentCapability(AICapability.Audio)]
    AudioSummarization = 705,

    // ============================================
    // AIOps Sub-Capabilities (800-899)
    // ============================================

    /// <summary>Anomaly detection in metrics and data.</summary>
    [ParentCapability(AICapability.AIOps)]
    AIOpsAnomalyDetection = 800,

    /// <summary>Storage tiering optimization.</summary>
    [ParentCapability(AICapability.AIOps)]
    AIOpsTieringOptimization = 801,

    /// <summary>Cost optimization recommendations.</summary>
    [ParentCapability(AICapability.AIOps)]
    AIOpsCostOptimization = 802,

    /// <summary>Predictive scaling.</summary>
    [ParentCapability(AICapability.AIOps)]
    AIOpsPredictiveScaling = 803,

    /// <summary>Capacity planning.</summary>
    [ParentCapability(AICapability.AIOps)]
    AIOpsCapacityPlanning = 804,

    /// <summary>Performance tuning.</summary>
    [ParentCapability(AICapability.AIOps)]
    AIOpsPerformanceTuning = 805,

    /// <summary>Failure prediction.</summary>
    [ParentCapability(AICapability.AIOps)]
    AIOpsFailurePrediction = 806,

    /// <summary>Query optimization.</summary>
    [ParentCapability(AICapability.AIOps)]
    AIOpsQueryOptimization = 807,

    /// <summary>Self-healing operations.</summary>
    [ParentCapability(AICapability.AIOps)]
    AIOpsSelfHealing = 808,

    // ============================================
    // Knowledge Sub-Capabilities (900-999)
    // ============================================

    /// <summary>Knowledge graph construction.</summary>
    [ParentCapability(AICapability.Knowledge)]
    KnowledgeGraphConstruction = 900,

    /// <summary>Fact extraction.</summary>
    [ParentCapability(AICapability.Knowledge)]
    KnowledgeFactExtraction = 901,

    /// <summary>RAG (Retrieval-Augmented Generation).</summary>
    [ParentCapability(AICapability.Knowledge)]
    KnowledgeRAG = 902,

    /// <summary>Question answering over knowledge bases.</summary>
    [ParentCapability(AICapability.Knowledge)]
    KnowledgeQA = 903,

    /// <summary>Knowledge base maintenance.</summary>
    [ParentCapability(AICapability.Knowledge)]
    KnowledgeMaintenance = 904,

    /// <summary>Entity linking and resolution.</summary>
    [ParentCapability(AICapability.Knowledge)]
    KnowledgeEntityLinking = 905,

    // ============================================
    // Code Intelligence Sub-Capabilities (1000-1099)
    // ============================================

    /// <summary>Code analysis and understanding.</summary>
    [ParentCapability(AICapability.CodeIntelligence)]
    CodeAnalysis = 1000,

    /// <summary>Code generation.</summary>
    [ParentCapability(AICapability.CodeIntelligence)]
    CodeGeneration = 1001,

    /// <summary>Code review and suggestions.</summary>
    [ParentCapability(AICapability.CodeIntelligence)]
    CodeReview = 1002,

    /// <summary>Code documentation generation.</summary>
    [ParentCapability(AICapability.CodeIntelligence)]
    CodeDocumentation = 1003,

    /// <summary>Code translation between languages.</summary>
    [ParentCapability(AICapability.CodeIntelligence)]
    CodeTranslation = 1004,

    /// <summary>Bug detection and fixing suggestions.</summary>
    [ParentCapability(AICapability.CodeIntelligence)]
    CodeBugDetection = 1005,

    // ============================================
    // Data Analysis Sub-Capabilities (1100-1199)
    // ============================================

    /// <summary>Pattern recognition in data.</summary>
    [ParentCapability(AICapability.DataAnalysis)]
    DataPatternRecognition = 1100,

    /// <summary>Trend analysis.</summary>
    [ParentCapability(AICapability.DataAnalysis)]
    DataTrendAnalysis = 1101,

    /// <summary>Statistical insights generation.</summary>
    [ParentCapability(AICapability.DataAnalysis)]
    DataStatisticalInsights = 1102,

    /// <summary>Data quality assessment.</summary>
    [ParentCapability(AICapability.DataAnalysis)]
    DataQualityAssessment = 1103,

    /// <summary>Data profiling.</summary>
    [ParentCapability(AICapability.DataAnalysis)]
    DataProfiling = 1104,

    /// <summary>Correlation analysis.</summary>
    [ParentCapability(AICapability.DataAnalysis)]
    DataCorrelationAnalysis = 1105,

    // ============================================
    // Security Sub-Capabilities (1200-1299)
    // ============================================

    /// <summary>PII (Personally Identifiable Information) detection.</summary>
    [ParentCapability(AICapability.Security)]
    SecurityPIIDetection = 1200,

    /// <summary>Sensitive data classification.</summary>
    [ParentCapability(AICapability.Security)]
    SecurityDataClassification = 1201,

    /// <summary>Threat detection in logs.</summary>
    [ParentCapability(AICapability.Security)]
    SecurityThreatDetection = 1202,

    /// <summary>Compliance checking.</summary>
    [ParentCapability(AICapability.Security)]
    SecurityComplianceCheck = 1203,

    /// <summary>Access pattern analysis.</summary>
    [ParentCapability(AICapability.Security)]
    SecurityAccessAnalysis = 1204,

    /// <summary>Data masking recommendations.</summary>
    [ParentCapability(AICapability.Security)]
    SecurityMaskingRecommendations = 1205
}

/// <summary>
/// Provides extension methods for AICapability and AISubCapability enums.
/// </summary>
public static class AICapabilityExtensions
{
    // Cache for parent capability lookups (populated on first access)
    private static readonly Lazy<Dictionary<AISubCapability, AICapability>> _parentCache = new(BuildParentCache);

    /// <summary>
    /// Gets the parent capability for a sub-capability using the [ParentCapability] attribute.
    /// Falls back to range-based lookup if attribute is not present.
    /// </summary>
    /// <param name="subCapability">The sub-capability to get the parent for.</param>
    /// <returns>The parent capability.</returns>
    public static AICapability GetParentCapability(this AISubCapability subCapability)
    {
        if (_parentCache.Value.TryGetValue(subCapability, out var parent))
        {
            return parent;
        }

        // Fallback to range-based lookup for backwards compatibility
        var value = (int)subCapability;
        return value switch
        {
            >= 100 and < 200 => AICapability.Chat,
            >= 200 and < 300 => AICapability.NLP,
            >= 300 and < 400 => AICapability.Semantics,
            >= 400 and < 500 => AICapability.Generation,
            >= 500 and < 600 => AICapability.Embeddings,
            >= 600 and < 700 => AICapability.Vision,
            >= 700 and < 800 => AICapability.Audio,
            >= 800 and < 900 => AICapability.AIOps,
            >= 900 and < 1000 => AICapability.Knowledge,
            >= 1000 and < 1100 => AICapability.CodeIntelligence,
            >= 1100 and < 1200 => AICapability.DataAnalysis,
            >= 1200 and < 1300 => AICapability.Security,
            _ => AICapability.None
        };
    }

    /// <summary>
    /// Builds the parent capability cache from [ParentCapability] attributes.
    /// </summary>
    private static Dictionary<AISubCapability, AICapability> BuildParentCache()
    {
        var cache = new Dictionary<AISubCapability, AICapability>();
        var type = typeof(AISubCapability);

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var attr = field.GetCustomAttribute<ParentCapabilityAttribute>();
            if (attr != null && Enum.TryParse<AISubCapability>(field.Name, out var subCap))
            {
                cache[subCap] = attr.Parent;
            }
        }

        return cache;
    }

    /// <summary>
    /// Gets the [ParentCapability] attribute for a sub-capability, if present.
    /// </summary>
    /// <param name="subCapability">The sub-capability.</param>
    /// <returns>The attribute, or null if not present.</returns>
    public static ParentCapabilityAttribute? GetParentCapabilityAttribute(this AISubCapability subCapability)
    {
        var type = typeof(AISubCapability);
        var field = type.GetField(subCapability.ToString());
        return field?.GetCustomAttribute<ParentCapabilityAttribute>();
    }

    /// <summary>
    /// Gets all sub-capabilities for a given capability.
    /// </summary>
    /// <param name="capability">The capability to get sub-capabilities for.</param>
    /// <returns>Collection of sub-capabilities.</returns>
    public static IEnumerable<AISubCapability> GetSubCapabilities(this AICapability capability)
    {
        var (min, max) = capability switch
        {
            AICapability.Chat => (100, 199),
            AICapability.NLP => (200, 299),
            AICapability.Semantics => (300, 399),
            AICapability.Generation => (400, 499),
            AICapability.Embeddings => (500, 599),
            AICapability.Vision => (600, 699),
            AICapability.Audio => (700, 799),
            AICapability.AIOps => (800, 899),
            AICapability.Knowledge => (900, 999),
            AICapability.CodeIntelligence => (1000, 1099),
            AICapability.DataAnalysis => (1100, 1199),
            AICapability.Security => (1200, 1299),
            _ => (0, 0)
        };

        if (min == 0 && max == 0)
            return Enumerable.Empty<AISubCapability>();

        return Enum.GetValues<AISubCapability>()
            .Where(sc => (int)sc >= min && (int)sc <= max);
    }

    /// <summary>
    /// Gets a human-readable description for the capability.
    /// </summary>
    /// <param name="capability">The capability to describe.</param>
    /// <returns>Human-readable description.</returns>
    public static string GetDescription(this AICapability capability)
    {
        var field = capability.GetType().GetField(capability.ToString());
        var attribute = field?.GetCustomAttributes(typeof(DescriptionAttribute), false)
            .FirstOrDefault() as DescriptionAttribute;
        return attribute?.Description ?? capability.ToString();
    }

    /// <summary>
    /// Checks if a capability requires a specific provider feature.
    /// </summary>
    /// <param name="capability">The capability to check.</param>
    /// <returns>Required provider features.</returns>
    public static ProviderRequirements GetProviderRequirements(this AICapability capability)
    {
        return capability switch
        {
            AICapability.Chat => new ProviderRequirements
            {
                RequiresChatCompletion = true,
                RequiresStreaming = true // Optional but preferred
            },
            AICapability.Embeddings => new ProviderRequirements
            {
                RequiresEmbeddings = true
            },
            AICapability.Vision => new ProviderRequirements
            {
                RequiresVision = true
            },
            AICapability.Generation => new ProviderRequirements
            {
                RequiresChatCompletion = true
            },
            _ => new ProviderRequirements()
        };
    }
}

/// <summary>
/// Defines provider requirements for a capability.
/// </summary>
public sealed class ProviderRequirements
{
    /// <summary>Requires chat completion support.</summary>
    public bool RequiresChatCompletion { get; init; }

    /// <summary>Requires streaming support.</summary>
    public bool RequiresStreaming { get; init; }

    /// <summary>Requires embedding generation.</summary>
    public bool RequiresEmbeddings { get; init; }

    /// <summary>Requires vision/image analysis.</summary>
    public bool RequiresVision { get; init; }

    /// <summary>Requires function/tool calling.</summary>
    public bool RequiresFunctionCalling { get; init; }

    /// <summary>Requires audio processing.</summary>
    public bool RequiresAudio { get; init; }
}

/// <summary>
/// Provides comprehensive metadata about a capability for discovery and configuration.
/// </summary>
/// <param name="Capability">The primary capability.</param>
/// <param name="SubCapability">Optional sub-capability for granular operations.</param>
/// <param name="DisplayName">Human-readable display name.</param>
/// <param name="Description">Detailed description of the capability.</param>
/// <param name="Requirements">Provider requirements for this capability.</param>
/// <param name="DefaultEnabled">Whether this capability is enabled by default.</param>
/// <param name="RequiresAuth">Whether this capability requires authentication.</param>
/// <param name="Category">Grouping category for UI organization.</param>
/// <param name="SupportedProviders">List of provider types that support this capability.</param>
public sealed record CapabilityDescriptor(
    AICapability Capability,
    AISubCapability? SubCapability,
    string DisplayName,
    string Description,
    ProviderRequirements Requirements,
    bool DefaultEnabled = true,
    bool RequiresAuth = true,
    string? Category = null,
    IReadOnlyList<string>? SupportedProviders = null
)
{
    /// <summary>
    /// Gets the unique identifier for this capability descriptor.
    /// </summary>
    public string Id => SubCapability.HasValue
        ? $"{Capability}.{SubCapability.Value}"
        : Capability.ToString();

    /// <summary>
    /// Checks if a provider type supports this capability.
    /// </summary>
    /// <param name="providerType">The provider type to check.</param>
    /// <returns>True if supported or if no restrictions are defined.</returns>
    public bool IsProviderSupported(string providerType)
    {
        if (SupportedProviders == null || SupportedProviders.Count == 0)
            return true;

        return SupportedProviders.Contains(providerType, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates a descriptor for a parent capability with default settings.
    /// </summary>
    public static CapabilityDescriptor ForCapability(AICapability capability)
    {
        return new CapabilityDescriptor(
            Capability: capability,
            SubCapability: null,
            DisplayName: capability.GetDescription(),
            Description: $"Provides {capability.GetDescription()} functionality.",
            Requirements: capability.GetProviderRequirements()
        );
    }

    /// <summary>
    /// Creates a descriptor for a sub-capability.
    /// </summary>
    public static CapabilityDescriptor ForSubCapability(AISubCapability subCapability, string displayName, string description)
    {
        return new CapabilityDescriptor(
            Capability: subCapability.GetParentCapability(),
            SubCapability: subCapability,
            DisplayName: displayName,
            Description: description,
            Requirements: subCapability.GetParentCapability().GetProviderRequirements()
        );
    }
}

/// <summary>
/// Extended capability handler interface with async operations and health checking.
/// </summary>
public interface ICapabilityHandlerEx : ICapabilityHandler
{
    /// <summary>
    /// Gets the primary capability domain this handler manages.
    /// </summary>
    AICapability Capability { get; }

    /// <summary>
    /// Gets the sub-capabilities this handler supports.
    /// </summary>
    IReadOnlySet<AISubCapability> SubCapabilities { get; }

    /// <summary>
    /// Checks if the handler and its configured provider are healthy.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Health check result.</returns>
    Task<CapabilityHealthResult> CheckHealthAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets estimated cost for an operation.
    /// </summary>
    /// <param name="subCapability">The sub-capability to estimate.</param>
    /// <param name="estimatedInputTokens">Estimated input tokens.</param>
    /// <param name="estimatedOutputTokens">Estimated output tokens.</param>
    /// <returns>Estimated cost in USD.</returns>
    decimal EstimateCost(AISubCapability subCapability, int estimatedInputTokens, int estimatedOutputTokens);

    /// <summary>
    /// Gets the recommended provider for a specific sub-capability.
    /// </summary>
    /// <param name="subCapability">The sub-capability.</param>
    /// <returns>Recommended provider name.</returns>
    string? GetRecommendedProvider(AISubCapability subCapability);
}

/// <summary>
/// Result of a capability health check.
/// </summary>
public sealed class CapabilityHealthResult
{
    /// <summary>Whether the capability is healthy and available.</summary>
    public bool IsHealthy { get; init; }

    /// <summary>Status message.</summary>
    public string Status { get; init; } = "Unknown";

    /// <summary>Last successful operation timestamp.</summary>
    public DateTime? LastSuccess { get; init; }

    /// <summary>Last error timestamp.</summary>
    public DateTime? LastError { get; init; }

    /// <summary>Last error message if any.</summary>
    public string? LastErrorMessage { get; init; }

    /// <summary>Average latency in milliseconds.</summary>
    public double? AverageLatencyMs { get; init; }

    /// <summary>Success rate (0-1).</summary>
    public double? SuccessRate { get; init; }

    /// <summary>Configured provider name.</summary>
    public string? ProviderName { get; init; }

    /// <summary>Provider availability status.</summary>
    public bool ProviderAvailable { get; init; }

    /// <summary>Creates a healthy result.</summary>
    public static CapabilityHealthResult Healthy(string? providerName = null) => new()
    {
        IsHealthy = true,
        Status = "Healthy",
        ProviderName = providerName,
        ProviderAvailable = true
    };

    /// <summary>Creates an unhealthy result.</summary>
    public static CapabilityHealthResult Unhealthy(string reason, string? providerName = null) => new()
    {
        IsHealthy = false,
        Status = "Unhealthy",
        LastErrorMessage = reason,
        LastError = DateTime.UtcNow,
        ProviderName = providerName,
        ProviderAvailable = false
    };

    /// <summary>Creates a degraded result.</summary>
    public static CapabilityHealthResult Degraded(string reason, string? providerName = null) => new()
    {
        IsHealthy = true, // Still functional but with issues
        Status = "Degraded",
        LastErrorMessage = reason,
        ProviderName = providerName,
        ProviderAvailable = true
    };
}
