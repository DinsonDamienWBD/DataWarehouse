using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.AI
{
    #region Core Interfaces

    /// <summary>
    /// Provider-agnostic interface for semantic content analysis.
    /// Implementations can use any AI provider (OpenAI, Claude, Gemini, etc.)
    /// or statistical/rule-based methods when AI is unavailable.
    /// </summary>
    public interface ISemanticAnalyzer
    {
        /// <summary>
        /// Whether an AI provider is configured and available for enhanced analysis.
        /// When false, implementations should fall back to statistical methods.
        /// </summary>
        bool IsAIAvailable { get; }

        /// <summary>
        /// Analyzes content and returns comprehensive semantic information.
        /// </summary>
        Task<SemanticAnalysisResult> AnalyzeAsync(
            string content,
            SemanticAnalysisOptions? options = null,
            CancellationToken ct = default);

        /// <summary>
        /// Analyzes content from a stream with specified content type.
        /// </summary>
        Task<SemanticAnalysisResult> AnalyzeStreamAsync(
            Stream content,
            string contentType,
            SemanticAnalysisOptions? options = null,
            CancellationToken ct = default);

        /// <summary>
        /// Batch analysis of multiple content items.
        /// </summary>
        Task<IReadOnlyList<SemanticAnalysisResult>> AnalyzeBatchAsync(
            IEnumerable<string> contents,
            SemanticAnalysisOptions? options = null,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Interface for categorizing content into hierarchical categories.
    /// </summary>
    public interface IContentCategorizer
    {
        /// <summary>
        /// Categorizes content into one or more categories.
        /// </summary>
        Task<CategorizationResult> CategorizeAsync(
            string content,
            CategorizationOptions? options = null,
            CancellationToken ct = default);

        /// <summary>
        /// Trains the categorizer with example content for a category.
        /// </summary>
        Task TrainCategoryAsync(
            string categoryId,
            IEnumerable<string> examples,
            CancellationToken ct = default);

        /// <summary>
        /// Registers a custom category definition.
        /// </summary>
        void RegisterCategory(CategoryDefinition category);

        /// <summary>
        /// Gets all registered categories.
        /// </summary>
        IReadOnlyList<CategoryDefinition> GetCategories();
    }

    /// <summary>
    /// Interface for extracting named entities from content.
    /// </summary>
    public interface IEntityExtractor
    {
        /// <summary>
        /// Extracts entities (people, places, organizations, dates, etc.) from content.
        /// </summary>
        Task<EntityExtractionResult> ExtractEntitiesAsync(
            string content,
            EntityExtractionOptions? options = null,
            CancellationToken ct = default);

        /// <summary>
        /// Registers a custom entity type with patterns.
        /// </summary>
        void RegisterEntityType(CustomEntityTypeDefinition entityType);

        /// <summary>
        /// Gets all supported entity types.
        /// </summary>
        IReadOnlyList<string> GetSupportedEntityTypes();
    }

    /// <summary>
    /// Interface for detecting and protecting personally identifiable information.
    /// </summary>
    public interface IPIIDetector
    {
        /// <summary>
        /// Detects PII in content with compliance assessment.
        /// </summary>
        Task<PIIDetectionResult> DetectPIIAsync(
            string content,
            PIIDetectionOptions? options = null,
            CancellationToken ct = default);

        /// <summary>
        /// Redacts detected PII from content.
        /// </summary>
        Task<PIIRedactionResult> RedactPIIAsync(
            string content,
            PIIRedactionOptions? options = null,
            CancellationToken ct = default);

        /// <summary>
        /// Registers a custom PII pattern.
        /// </summary>
        void RegisterPIIPattern(CustomPIIPatternDefinition pattern);

        /// <summary>
        /// Gets all supported PII types.
        /// </summary>
        IReadOnlyList<PIITypeInfo> GetSupportedPIITypes();
    }

    /// <summary>
    /// Interface for generating content summaries.
    /// </summary>
    public interface IContentSummarizer
    {
        /// <summary>
        /// Generates a summary of the content.
        /// </summary>
        Task<SummaryResult> SummarizeAsync(
            string content,
            SummaryOptions? options = null,
            CancellationToken ct = default);

        /// <summary>
        /// Generates a combined summary of multiple documents.
        /// </summary>
        Task<SummaryResult> SummarizeMultipleAsync(
            IEnumerable<string> contents,
            SummaryOptions? options = null,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Interface for detecting content language.
    /// </summary>
    public interface ILanguageDetector
    {
        /// <summary>
        /// Detects the primary language of content.
        /// </summary>
        Task<LanguageDetectionResult> DetectLanguageAsync(
            string content,
            LanguageDetectionOptions? options = null,
            CancellationToken ct = default);

        /// <summary>
        /// Gets all supported language codes.
        /// </summary>
        IReadOnlyList<LanguageInfo> GetSupportedLanguages();
    }

    /// <summary>
    /// Interface for sentiment and emotion analysis.
    /// </summary>
    public interface ISentimentAnalyzer
    {
        /// <summary>
        /// Analyzes sentiment of content.
        /// </summary>
        Task<SentimentResult> AnalyzeSentimentAsync(
            string content,
            SentimentOptions? options = null,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Interface for finding semantic duplicates and similar content.
    /// </summary>
    public interface IDuplicateDetector
    {
        /// <summary>
        /// Detects semantic duplicates of content within a corpus.
        /// </summary>
        Task<DuplicateDetectionResult> DetectDuplicatesAsync(
            string content,
            string contentId,
            IEnumerable<ContentItem> corpus,
            DuplicateDetectionOptions? options = null,
            CancellationToken ct = default);

        /// <summary>
        /// Computes similarity between two pieces of content.
        /// </summary>
        Task<float> ComputeSimilarityAsync(
            string content1,
            string content2,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Interface for discovering relationships between documents.
    /// </summary>
    public interface IRelationshipDiscoverer
    {
        /// <summary>
        /// Discovers relationships between a document and a corpus.
        /// </summary>
        Task<RelationshipDiscoveryResult> DiscoverRelationshipsAsync(
            ContentItem document,
            IEnumerable<ContentItem> corpus,
            RelationshipDiscoveryOptions? options = null,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Interface for natural language query parsing and understanding.
    /// </summary>
    public interface INaturalLanguageQueryParser
    {
        /// <summary>
        /// Parses a natural language query into structured intent.
        /// </summary>
        Task<QueryParseResult> ParseQueryAsync(
            string query,
            QueryParseOptions? options = null,
            CancellationToken ct = default);

        /// <summary>
        /// Generates a natural language explanation for a query result or decision.
        /// </summary>
        Task<string> ExplainAsync(
            string context,
            string question,
            CancellationToken ct = default);
    }

    #endregion

    #region Options Classes

    /// <summary>
    /// Options for semantic analysis.
    /// </summary>
    public class SemanticAnalysisOptions
    {
        /// <summary>Whether to include categorization.</summary>
        public bool IncludeCategorization { get; init; } = true;
        /// <summary>Whether to include entity extraction.</summary>
        public bool IncludeEntities { get; init; } = true;
        /// <summary>Whether to include sentiment analysis.</summary>
        public bool IncludeSentiment { get; init; } = true;
        /// <summary>Whether to include language detection.</summary>
        public bool IncludeLanguage { get; init; } = true;
        /// <summary>Whether to include summarization.</summary>
        public bool IncludeSummary { get; init; } = true;
        /// <summary>Whether to include PII detection.</summary>
        public bool IncludePII { get; init; } = false;
        /// <summary>Prefer AI-based analysis when available.</summary>
        public bool PreferAI { get; init; } = true;
        /// <summary>Minimum confidence threshold for results.</summary>
        public float MinConfidence { get; init; } = 0.5f;
    }

    /// <summary>
    /// Options for content categorization.
    /// </summary>
    public class CategorizationOptions
    {
        /// <summary>Prefer AI-based categorization when available.</summary>
        public bool PreferAI { get; init; } = true;
        /// <summary>Minimum confidence threshold.</summary>
        public float MinConfidence { get; init; } = 0.3f;
        /// <summary>Maximum number of categories to return.</summary>
        public int MaxCategories { get; init; } = 5;
        /// <summary>Include parent categories in hierarchy.</summary>
        public bool IncludeHierarchy { get; init; } = true;
        /// <summary>Specific category IDs to consider (null = all).</summary>
        public IReadOnlyList<string>? CandidateCategories { get; init; }
    }

    /// <summary>
    /// Options for entity extraction.
    /// </summary>
    public class EntityExtractionOptions
    {
        /// <summary>Use AI-enhanced extraction when available.</summary>
        public bool UseAI { get; init; } = true;
        /// <summary>Include surrounding context for each entity.</summary>
        public bool IncludeContext { get; init; } = true;
        /// <summary>Size of context window in characters.</summary>
        public int ContextWindowSize { get; init; } = 50;
        /// <summary>Specific entity types to extract (null = all).</summary>
        public IReadOnlyList<string>? EntityTypes { get; init; }
        /// <summary>Minimum confidence threshold.</summary>
        public float MinConfidence { get; init; } = 0.5f;
        /// <summary>Whether to link entities to knowledge graph.</summary>
        public bool EnableEntityLinking { get; init; } = false;
    }

    /// <summary>
    /// Options for PII detection.
    /// </summary>
    public class PIIDetectionOptions
    {
        /// <summary>Use AI-enhanced detection when available.</summary>
        public bool UseAI { get; init; } = true;
        /// <summary>Include surrounding context for each PII instance.</summary>
        public bool IncludeContext { get; init; } = true;
        /// <summary>Specific PII types to detect (null = all).</summary>
        public IReadOnlyList<PIIType>? PIITypes { get; init; }
        /// <summary>Compliance regulations to check against.</summary>
        public IReadOnlyList<string>? ComplianceRegulations { get; init; }
        /// <summary>Minimum confidence threshold.</summary>
        public float MinConfidence { get; init; } = 0.7f;
    }

    /// <summary>
    /// Options for PII redaction.
    /// </summary>
    public class PIIRedactionOptions
    {
        /// <summary>Redaction strategy to use.</summary>
        public RedactionStrategy Strategy { get; init; } = RedactionStrategy.Mask;
        /// <summary>Custom mask character (for Mask strategy).</summary>
        public char MaskCharacter { get; init; } = '*';
        /// <summary>Specific PII types to redact (null = all detected).</summary>
        public IReadOnlyList<PIIType>? PIITypes { get; init; }
        /// <summary>Preserve format where possible (e.g., XXX-XX-XXXX for SSN).</summary>
        public bool PreserveFormat { get; init; } = true;
    }

    /// <summary>
    /// Options for content summarization.
    /// </summary>
    public record SummaryOptions
    {
        /// <summary>Target length of summary in words.</summary>
        public int TargetLengthWords { get; init; } = 100;
        /// <summary>Prefer abstractive (AI) over extractive summarization.</summary>
        public bool PreferAbstractive { get; init; } = true;
        /// <summary>Specific aspects to focus on in the summary.</summary>
        public IReadOnlyList<string>? FocusAspects { get; init; }
        /// <summary>Include key phrases in the result.</summary>
        public bool IncludeKeyPhrases { get; init; } = true;
    }

    /// <summary>
    /// Options for language detection.
    /// </summary>
    public class LanguageDetectionOptions
    {
        /// <summary>Use AI-enhanced detection when available.</summary>
        public bool UseAI { get; init; } = true;
        /// <summary>Detect multiple languages in mixed-language content.</summary>
        public bool DetectMultiple { get; init; } = true;
        /// <summary>Detect script type (Latin, Cyrillic, etc.).</summary>
        public bool IncludeScript { get; init; } = true;
    }

    /// <summary>
    /// Options for sentiment analysis.
    /// </summary>
    public class SentimentOptions
    {
        /// <summary>Use AI-enhanced analysis when available.</summary>
        public bool UseAI { get; init; } = true;
        /// <summary>Perform sentence-level sentiment analysis.</summary>
        public bool AnalyzeSentences { get; init; } = false;
        /// <summary>Perform aspect-based sentiment analysis.</summary>
        public bool AnalyzeAspects { get; init; } = false;
        /// <summary>Specific aspects to analyze sentiment for.</summary>
        public IReadOnlyList<string>? Aspects { get; init; }
        /// <summary>Detect specific emotions beyond positive/negative.</summary>
        public bool DetectEmotions { get; init; } = false;
    }

    /// <summary>
    /// Options for duplicate detection.
    /// </summary>
    public class DuplicateDetectionOptions
    {
        /// <summary>Enable semantic (embedding-based) duplicate detection.</summary>
        public bool EnableSemanticDetection { get; init; } = true;
        /// <summary>Threshold for exact duplicate detection (hash-based).</summary>
        public float ExactDuplicateThreshold { get; init; } = 0.99f;
        /// <summary>Threshold for near-duplicate detection (semantic).</summary>
        public float NearDuplicateThreshold { get; init; } = 0.85f;
        /// <summary>Maximum number of duplicates to return.</summary>
        public int MaxResults { get; init; } = 10;
    }

    /// <summary>
    /// Options for relationship discovery.
    /// </summary>
    public class RelationshipDiscoveryOptions
    {
        /// <summary>Relationship types to discover.</summary>
        public IReadOnlyList<RelationshipType>? RelationshipTypes { get; init; }
        /// <summary>Minimum similarity for content relationships.</summary>
        public float MinSimilarity { get; init; } = 0.5f;
        /// <summary>Maximum relationships to return per type.</summary>
        public int MaxRelationshipsPerType { get; init; } = 10;
        /// <summary>Store discovered relationships in knowledge graph.</summary>
        public bool StoreInKnowledgeGraph { get; init; } = false;
    }

    /// <summary>
    /// Options for natural language query parsing.
    /// </summary>
    public class QueryParseOptions
    {
        /// <summary>Use AI-enhanced parsing when available.</summary>
        public bool UseAI { get; init; } = true;
        /// <summary>Include confidence scores for parsed elements.</summary>
        public bool IncludeConfidence { get; init; } = true;
        /// <summary>Context from previous queries in conversation.</summary>
        public IReadOnlyList<string>? ConversationContext { get; init; }
        /// <summary>Domain-specific vocabulary to consider.</summary>
        public IReadOnlyList<string>? DomainVocabulary { get; init; }
    }

    #endregion

    #region Result Classes

    /// <summary>
    /// Comprehensive semantic analysis result.
    /// </summary>
    public record SemanticAnalysisResult
    {
        /// <summary>Whether analysis was successful.</summary>
        public bool Success { get; init; }
        /// <summary>Error message if not successful.</summary>
        public string? Error { get; init; }
        /// <summary>Categorization result.</summary>
        public CategorizationResult? Categorization { get; init; }
        /// <summary>Entity extraction result.</summary>
        public EntityExtractionResult? Entities { get; init; }
        /// <summary>Sentiment analysis result.</summary>
        public SentimentResult? Sentiment { get; init; }
        /// <summary>Language detection result.</summary>
        public LanguageDetectionResult? Language { get; init; }
        /// <summary>Summary result.</summary>
        public SummaryResult? Summary { get; init; }
        /// <summary>PII detection result.</summary>
        public PIIDetectionResult? PII { get; init; }
        /// <summary>Processing duration.</summary>
        public TimeSpan Duration { get; init; }
        /// <summary>Whether AI was used for analysis.</summary>
        public bool UsedAI { get; init; }
    }

    /// <summary>
    /// Categorization result.
    /// </summary>
    public record CategorizationResult
    {
        /// <summary>Primary category.</summary>
        public CategoryMatch? PrimaryCategory { get; init; }
        /// <summary>All matched categories ordered by confidence.</summary>
        public IReadOnlyList<CategoryMatch> Categories { get; init; } = Array.Empty<CategoryMatch>();
        /// <summary>Overall confidence score.</summary>
        public float Confidence { get; init; }
        /// <summary>Whether AI was used.</summary>
        public bool UsedAI { get; init; }
    }

    /// <summary>
    /// A matched category with confidence.
    /// </summary>
    public record CategoryMatch
    {
        /// <summary>Category ID.</summary>
        public string Id { get; init; } = string.Empty;
        /// <summary>Category name.</summary>
        public string Name { get; init; } = string.Empty;
        /// <summary>Parent category path.</summary>
        public IReadOnlyList<string> Path { get; init; } = Array.Empty<string>();
        /// <summary>Confidence score (0-1).</summary>
        public float Confidence { get; init; }
    }

    /// <summary>
    /// Entity extraction result.
    /// </summary>
    public record EntityExtractionResult
    {
        /// <summary>All extracted entities.</summary>
        public IReadOnlyList<ExtractedEntity> Entities { get; init; } = Array.Empty<ExtractedEntity>();
        /// <summary>Total entity count.</summary>
        public int TotalCount => Entities.Count;
        /// <summary>Entity counts by type.</summary>
        public IReadOnlyDictionary<string, int> CountsByType { get; init; } = new Dictionary<string, int>();
        /// <summary>Whether AI was used.</summary>
        public bool UsedAI { get; init; }
    }

    /// <summary>
    /// An extracted entity.
    /// </summary>
    public record ExtractedEntity
    {
        /// <summary>Entity text as it appears in content.</summary>
        public string Text { get; init; } = string.Empty;
        /// <summary>Normalized/canonical form.</summary>
        public string? NormalizedText { get; init; }
        /// <summary>Entity type (PERSON, ORGANIZATION, LOCATION, DATE, etc.).</summary>
        public string Type { get; init; } = string.Empty;
        /// <summary>Start position in content.</summary>
        public int StartIndex { get; init; }
        /// <summary>End position in content.</summary>
        public int EndIndex { get; init; }
        /// <summary>Confidence score (0-1).</summary>
        public float Confidence { get; init; }
        /// <summary>Surrounding context.</summary>
        public string? Context { get; init; }
        /// <summary>Knowledge graph entity ID if linked.</summary>
        public string? LinkedEntityId { get; init; }
        /// <summary>Additional metadata.</summary>
        public IReadOnlyDictionary<string, object>? Metadata { get; init; }
    }

    /// <summary>
    /// PII detection result.
    /// </summary>
    public record PIIDetectionResult
    {
        /// <summary>All detected PII instances.</summary>
        public IReadOnlyList<DetectedPII> Items { get; init; } = Array.Empty<DetectedPII>();
        /// <summary>Total PII count.</summary>
        public int TotalCount => Items.Count;
        /// <summary>Counts by PII type.</summary>
        public IReadOnlyDictionary<PIIType, int> CountsByType { get; init; } = new Dictionary<PIIType, int>();
        /// <summary>Risk level assessment.</summary>
        public PIIRiskLevel RiskLevel { get; init; }
        /// <summary>Compliance violations detected.</summary>
        public IReadOnlyList<ComplianceViolation> ComplianceViolations { get; init; } = Array.Empty<ComplianceViolation>();
        /// <summary>Whether AI was used.</summary>
        public bool UsedAI { get; init; }
    }

    /// <summary>
    /// A detected PII instance.
    /// </summary>
    public record DetectedPII
    {
        /// <summary>PII text as it appears in content.</summary>
        public string Text { get; init; } = string.Empty;
        /// <summary>PII type.</summary>
        public PIIType Type { get; init; }
        /// <summary>Start position in content.</summary>
        public int StartIndex { get; init; }
        /// <summary>End position in content.</summary>
        public int EndIndex { get; init; }
        /// <summary>Confidence score (0-1).</summary>
        public float Confidence { get; init; }
        /// <summary>Surrounding context.</summary>
        public string? Context { get; init; }
        /// <summary>Suggested redaction.</summary>
        public string? SuggestedRedaction { get; init; }
    }

    /// <summary>
    /// PII redaction result.
    /// </summary>
    public record PIIRedactionResult
    {
        /// <summary>Original text.</summary>
        public string OriginalText { get; init; } = string.Empty;
        /// <summary>Redacted text.</summary>
        public string RedactedText { get; init; } = string.Empty;
        /// <summary>Number of PII instances redacted.</summary>
        public int RedactionCount { get; init; }
        /// <summary>Details of each redaction.</summary>
        public IReadOnlyList<RedactionDetail> Redactions { get; init; } = Array.Empty<RedactionDetail>();
    }

    /// <summary>
    /// Detail of a single redaction.
    /// </summary>
    public record RedactionDetail
    {
        /// <summary>Original text that was redacted.</summary>
        public string OriginalText { get; init; } = string.Empty;
        /// <summary>Replacement text.</summary>
        public string RedactedText { get; init; } = string.Empty;
        /// <summary>PII type.</summary>
        public PIIType Type { get; init; }
        /// <summary>Position in original text.</summary>
        public int Position { get; init; }
    }

    /// <summary>
    /// Summary result.
    /// </summary>
    public record SummaryResult
    {
        /// <summary>Generated summary text.</summary>
        public string Summary { get; init; } = string.Empty;
        /// <summary>Summary type (extractive or abstractive).</summary>
        public SummaryType Type { get; init; }
        /// <summary>Key phrases extracted from content.</summary>
        public IReadOnlyList<string> KeyPhrases { get; init; } = Array.Empty<string>();
        /// <summary>Compression ratio (summary length / original length).</summary>
        public float CompressionRatio { get; init; }
        /// <summary>Whether AI was used.</summary>
        public bool UsedAI { get; init; }
    }

    /// <summary>
    /// Language detection result.
    /// </summary>
    public record LanguageDetectionResult
    {
        /// <summary>Primary detected language.</summary>
        public DetectedLanguage PrimaryLanguage { get; init; } = new();
        /// <summary>All detected languages (for mixed content).</summary>
        public IReadOnlyList<DetectedLanguage> Languages { get; init; } = Array.Empty<DetectedLanguage>();
        /// <summary>Whether content contains multiple languages.</summary>
        public bool IsMultilingual { get; init; }
        /// <summary>Whether AI was used.</summary>
        public bool UsedAI { get; init; }
    }

    /// <summary>
    /// A detected language.
    /// </summary>
    public record DetectedLanguage
    {
        /// <summary>ISO 639-1 language code (e.g., "en", "es", "zh").</summary>
        public string Code { get; init; } = "en";
        /// <summary>Language name in English.</summary>
        public string Name { get; init; } = "English";
        /// <summary>Script type (Latin, Cyrillic, Han, etc.).</summary>
        public string? Script { get; init; }
        /// <summary>Confidence score (0-1).</summary>
        public float Confidence { get; init; }
        /// <summary>Percentage of content in this language.</summary>
        public float Percentage { get; init; }
    }

    /// <summary>
    /// Sentiment analysis result.
    /// </summary>
    public record SentimentResult
    {
        /// <summary>Overall sentiment.</summary>
        public Sentiment OverallSentiment { get; init; }
        /// <summary>Sentiment score (-1 to 1, negative to positive).</summary>
        public float Score { get; init; }
        /// <summary>Confidence in the sentiment assessment.</summary>
        public float Confidence { get; init; }
        /// <summary>Detected emotions.</summary>
        public IReadOnlyDictionary<string, float>? Emotions { get; init; }
        /// <summary>Sentence-level sentiment (if requested).</summary>
        public IReadOnlyList<SentenceSentiment>? SentenceSentiments { get; init; }
        /// <summary>Aspect-based sentiment (if requested).</summary>
        public IReadOnlyDictionary<string, SentimentResult>? AspectSentiments { get; init; }
        /// <summary>Whether AI was used.</summary>
        public bool UsedAI { get; init; }
    }

    /// <summary>
    /// Sentiment for a single sentence.
    /// </summary>
    public record SentenceSentiment
    {
        /// <summary>Sentence text.</summary>
        public string Text { get; init; } = string.Empty;
        /// <summary>Sentiment classification.</summary>
        public Sentiment Sentiment { get; init; }
        /// <summary>Sentiment score.</summary>
        public float Score { get; init; }
    }

    /// <summary>
    /// Duplicate detection result.
    /// </summary>
    public record DuplicateDetectionResult
    {
        /// <summary>Exact duplicates found.</summary>
        public IReadOnlyList<DuplicateMatch> ExactDuplicates { get; init; } = Array.Empty<DuplicateMatch>();
        /// <summary>Near duplicates found.</summary>
        public IReadOnlyList<DuplicateMatch> NearDuplicates { get; init; } = Array.Empty<DuplicateMatch>();
        /// <summary>Total duplicates found.</summary>
        public int TotalCount => ExactDuplicates.Count + NearDuplicates.Count;
        /// <summary>Whether semantic detection was used.</summary>
        public bool UsedSemanticDetection { get; init; }
    }

    /// <summary>
    /// A duplicate match.
    /// </summary>
    public record DuplicateMatch
    {
        /// <summary>ID of the duplicate content.</summary>
        public string ContentId { get; init; } = string.Empty;
        /// <summary>Similarity score (0-1).</summary>
        public float Similarity { get; init; }
        /// <summary>Type of duplicate match.</summary>
        public DuplicateType Type { get; init; }
    }

    /// <summary>
    /// Relationship discovery result.
    /// </summary>
    public record RelationshipDiscoveryResult
    {
        /// <summary>Discovered relationships.</summary>
        public IReadOnlyList<DiscoveredRelationship> Relationships { get; init; } = Array.Empty<DiscoveredRelationship>();
        /// <summary>Total relationships found.</summary>
        public int TotalCount => Relationships.Count;
    }

    /// <summary>
    /// A discovered relationship.
    /// </summary>
    public record DiscoveredRelationship
    {
        /// <summary>Source content ID.</summary>
        public string SourceId { get; init; } = string.Empty;
        /// <summary>Target content ID.</summary>
        public string TargetId { get; init; } = string.Empty;
        /// <summary>Relationship type.</summary>
        public RelationshipType Type { get; init; }
        /// <summary>Relationship strength/confidence (0-1).</summary>
        public float Strength { get; init; }
        /// <summary>Additional relationship metadata.</summary>
        public IReadOnlyDictionary<string, object>? Metadata { get; init; }
    }

    /// <summary>
    /// Natural language query parse result.
    /// </summary>
    public record QueryParseResult
    {
        /// <summary>Detected intent.</summary>
        public string Intent { get; init; } = string.Empty;
        /// <summary>Intent confidence (0-1).</summary>
        public float Confidence { get; init; }
        /// <summary>Extracted entities/parameters.</summary>
        public IReadOnlyDictionary<string, object> Parameters { get; init; } = new Dictionary<string, object>();
        /// <summary>Translated query for execution.</summary>
        public string? TranslatedQuery { get; init; }
        /// <summary>Suggested clarifying questions.</summary>
        public IReadOnlyList<string>? ClarifyingQuestions { get; init; }
        /// <summary>Whether AI was used for parsing.</summary>
        public bool UsedAI { get; init; }
    }

    #endregion

    #region Definition Classes

    /// <summary>
    /// Definition of a content category.
    /// </summary>
    public class CategoryDefinition
    {
        /// <summary>Unique category ID.</summary>
        public string Id { get; init; } = string.Empty;
        /// <summary>Category name.</summary>
        public string Name { get; init; } = string.Empty;
        /// <summary>Category description.</summary>
        public string? Description { get; init; }
        /// <summary>Parent category ID (null for root).</summary>
        public string? ParentId { get; init; }
        /// <summary>Keywords associated with this category.</summary>
        public IReadOnlyList<string>? Keywords { get; init; }
        /// <summary>Example content for training.</summary>
        public IReadOnlyList<string>? Examples { get; init; }
    }

    /// <summary>
    /// Definition of a custom entity type.
    /// </summary>
    public class CustomEntityTypeDefinition
    {
        /// <summary>Entity type name.</summary>
        public string Name { get; init; } = string.Empty;
        /// <summary>Description of the entity type.</summary>
        public string? Description { get; init; }
        /// <summary>Regex patterns for detection.</summary>
        public IReadOnlyList<string>? Patterns { get; init; }
        /// <summary>Example values.</summary>
        public IReadOnlyList<string>? Examples { get; init; }
    }

    /// <summary>
    /// Definition of a custom PII pattern.
    /// </summary>
    public class CustomPIIPatternDefinition
    {
        /// <summary>Pattern name.</summary>
        public string Name { get; init; } = string.Empty;
        /// <summary>PII type this pattern detects.</summary>
        public PIIType Type { get; init; }
        /// <summary>Regex pattern.</summary>
        public string Pattern { get; init; } = string.Empty;
        /// <summary>Validation function (optional).</summary>
        public Func<string, bool>? Validator { get; init; }
        /// <summary>Confidence boost for matches.</summary>
        public float ConfidenceBoost { get; init; }
    }

    /// <summary>
    /// Information about a PII type.
    /// </summary>
    public record PIITypeInfo
    {
        /// <summary>PII type.</summary>
        public PIIType Type { get; init; }
        /// <summary>Display name.</summary>
        public string Name { get; init; } = string.Empty;
        /// <summary>Description.</summary>
        public string? Description { get; init; }
        /// <summary>Applicable regulations.</summary>
        public IReadOnlyList<string>? Regulations { get; init; }
    }

    /// <summary>
    /// Information about a supported language.
    /// </summary>
    public record LanguageInfo
    {
        /// <summary>ISO 639-1 code.</summary>
        public string Code { get; init; } = string.Empty;
        /// <summary>Language name.</summary>
        public string Name { get; init; } = string.Empty;
        /// <summary>Native name.</summary>
        public string? NativeName { get; init; }
        /// <summary>Script type.</summary>
        public string? Script { get; init; }
    }

    /// <summary>
    /// A content item for duplicate detection/relationship discovery.
    /// </summary>
    public class ContentItem
    {
        /// <summary>Unique content ID.</summary>
        public string Id { get; init; } = string.Empty;
        /// <summary>Content text.</summary>
        public string Text { get; init; } = string.Empty;
        /// <summary>Pre-computed embedding (optional).</summary>
        public float[]? Embedding { get; init; }
        /// <summary>Content metadata.</summary>
        public IReadOnlyDictionary<string, object>? Metadata { get; init; }
    }

    /// <summary>
    /// A compliance violation.
    /// </summary>
    public record ComplianceViolation
    {
        /// <summary>Regulation code (GDPR, HIPAA, etc.).</summary>
        public string Regulation { get; init; } = string.Empty;
        /// <summary>Specific rule/article violated.</summary>
        public string? Rule { get; init; }
        /// <summary>Violation description.</summary>
        public string Description { get; init; } = string.Empty;
        /// <summary>Severity level.</summary>
        public ViolationSeverity Severity { get; init; }
        /// <summary>Related PII items.</summary>
        public IReadOnlyList<DetectedPII>? RelatedPII { get; init; }
    }

    #endregion

    #region Enums

    /// <summary>
    /// Types of personally identifiable information.
    /// </summary>
    public enum PIIType
    {
        /// <summary>Unknown or custom PII type.</summary>
        Unknown,
        /// <summary>Full name.</summary>
        Name,
        /// <summary>Email address.</summary>
        Email,
        /// <summary>Phone number.</summary>
        Phone,
        /// <summary>Physical address.</summary>
        Address,
        /// <summary>Social Security Number.</summary>
        SSN,
        /// <summary>Credit card number.</summary>
        CreditCard,
        /// <summary>Bank account number.</summary>
        BankAccount,
        /// <summary>Date of birth.</summary>
        DateOfBirth,
        /// <summary>Driver's license number.</summary>
        DriversLicense,
        /// <summary>Passport number.</summary>
        Passport,
        /// <summary>IP address.</summary>
        IPAddress,
        /// <summary>Medical record number.</summary>
        MedicalRecordNumber,
        /// <summary>National ID (various countries).</summary>
        NationalId,
        /// <summary>Tax ID / Employer ID.</summary>
        TaxId,
        /// <summary>Username or login ID.</summary>
        Username,
        /// <summary>Password or credential.</summary>
        Password,
        /// <summary>Biometric data indicator.</summary>
        BiometricData,
        /// <summary>GPS coordinates or precise location.</summary>
        Location,
        /// <summary>Vehicle identification number.</summary>
        VIN,
        /// <summary>Device identifier.</summary>
        DeviceId
    }

    /// <summary>
    /// PII risk levels.
    /// </summary>
    public enum PIIRiskLevel
    {
        /// <summary>No PII detected.</summary>
        None,
        /// <summary>Low-risk PII (e.g., name only).</summary>
        Low,
        /// <summary>Medium-risk PII (e.g., email, phone).</summary>
        Medium,
        /// <summary>High-risk PII (e.g., SSN, financial data).</summary>
        High,
        /// <summary>Critical-risk PII (e.g., health records, credentials).</summary>
        Critical
    }

    /// <summary>
    /// PII redaction strategies.
    /// </summary>
    public enum RedactionStrategy
    {
        /// <summary>Mask with characters (e.g., *****).</summary>
        Mask,
        /// <summary>Replace with type label (e.g., [EMAIL]).</summary>
        TypeLabel,
        /// <summary>Replace with hash.</summary>
        Hash,
        /// <summary>Remove entirely.</summary>
        Remove,
        /// <summary>Replace with synthetic data.</summary>
        Synthetic
    }

    /// <summary>
    /// Compliance violation severity.
    /// </summary>
    public enum ViolationSeverity
    {
        /// <summary>Informational only.</summary>
        Info,
        /// <summary>Minor violation.</summary>
        Minor,
        /// <summary>Moderate violation.</summary>
        Moderate,
        /// <summary>Severe violation.</summary>
        Severe,
        /// <summary>Critical violation requiring immediate action.</summary>
        Critical
    }

    /// <summary>
    /// Summary types.
    /// </summary>
    public enum SummaryType
    {
        /// <summary>Extractive (selects key sentences).</summary>
        Extractive,
        /// <summary>Abstractive (generates new text).</summary>
        Abstractive
    }

    /// <summary>
    /// Sentiment classifications.
    /// </summary>
    public enum Sentiment
    {
        /// <summary>Very negative sentiment.</summary>
        VeryNegative,
        /// <summary>Negative sentiment.</summary>
        Negative,
        /// <summary>Neutral sentiment.</summary>
        Neutral,
        /// <summary>Positive sentiment.</summary>
        Positive,
        /// <summary>Very positive sentiment.</summary>
        VeryPositive
    }

    /// <summary>
    /// Duplicate match types.
    /// </summary>
    public enum DuplicateType
    {
        /// <summary>Exact content match.</summary>
        Exact,
        /// <summary>Near-duplicate (minor differences).</summary>
        Near,
        /// <summary>Semantically similar content.</summary>
        Semantic
    }

    /// <summary>
    /// Relationship types between content.
    /// </summary>
    public enum RelationshipType
    {
        /// <summary>Content similarity.</summary>
        Similar,
        /// <summary>Reference/citation.</summary>
        References,
        /// <summary>Topic overlap.</summary>
        TopicOverlap,
        /// <summary>Temporal relationship.</summary>
        Temporal,
        /// <summary>Author relationship.</summary>
        SameAuthor,
        /// <summary>Version relationship.</summary>
        Version,
        /// <summary>Response/reply.</summary>
        Response,
        /// <summary>Contains relationship.</summary>
        Contains,
        /// <summary>Depends on.</summary>
        DependsOn
    }

    #endregion
}
