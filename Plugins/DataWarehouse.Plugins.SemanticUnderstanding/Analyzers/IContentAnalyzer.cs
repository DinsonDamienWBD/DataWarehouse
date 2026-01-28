using DataWarehouse.Plugins.SemanticUnderstanding.Models;

namespace DataWarehouse.Plugins.SemanticUnderstanding.Analyzers
{
    /// <summary>
    /// Interface for content analyzers that extract semantic information.
    /// </summary>
    public interface IContentAnalyzer
    {
        /// <summary>
        /// Content types this analyzer can handle.
        /// </summary>
        string[] SupportedContentTypes { get; }

        /// <summary>
        /// Whether this analyzer can process the given content type.
        /// </summary>
        bool CanAnalyze(string contentType);

        /// <summary>
        /// Extract text content for analysis.
        /// </summary>
        Task<string> ExtractTextAsync(Stream content, string contentType, CancellationToken ct = default);

        /// <summary>
        /// Analyze content and return comprehensive results.
        /// </summary>
        Task<ContentAnalysisResult> AnalyzeAsync(
            Stream content,
            string contentType,
            ContentAnalysisOptions options,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Combined result of content analysis.
    /// </summary>
    public sealed record ContentAnalysisResult
    {
        /// <summary>Extracted text content.</summary>
        public string Text { get; init; } = string.Empty;

        /// <summary>Detected content type.</summary>
        public string? DetectedContentType { get; init; }

        /// <summary>Language detection result.</summary>
        public LanguageDetectionResult? LanguageResult { get; init; }

        /// <summary>Extracted entities.</summary>
        public EntityExtractionResult? EntityResult { get; init; }

        /// <summary>Generated summary.</summary>
        public SummarizationResult? SummaryResult { get; init; }

        /// <summary>Sentiment analysis result.</summary>
        public SentimentAnalysisResult? SentimentResult { get; init; }

        /// <summary>PII detection result.</summary>
        public PIIDetectionResult? PIIResult { get; init; }

        /// <summary>Categorization result.</summary>
        public CategorizationResult? CategoryResult { get; init; }

        /// <summary>Generated embeddings.</summary>
        public float[]? Embeddings { get; init; }

        /// <summary>Extracted keywords.</summary>
        public List<WeightedKeyword> Keywords { get; init; } = new();

        /// <summary>Content hash for duplicate detection.</summary>
        public string? ContentHash { get; init; }

        /// <summary>Overall processing duration.</summary>
        public TimeSpan Duration { get; init; }

        /// <summary>Whether analysis was successful.</summary>
        public bool Success { get; init; }

        /// <summary>Error message if unsuccessful.</summary>
        public string? ErrorMessage { get; init; }

        /// <summary>Additional metadata.</summary>
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    /// <summary>
    /// Options for content analysis.
    /// </summary>
    public sealed record ContentAnalysisOptions
    {
        /// <summary>Extract text content.</summary>
        public bool ExtractText { get; init; } = true;

        /// <summary>Detect language.</summary>
        public bool DetectLanguage { get; init; } = true;

        /// <summary>Extract entities.</summary>
        public bool ExtractEntities { get; init; } = true;

        /// <summary>Generate summary.</summary>
        public bool GenerateSummary { get; init; } = true;

        /// <summary>Analyze sentiment.</summary>
        public bool AnalyzeSentiment { get; init; } = true;

        /// <summary>Detect PII.</summary>
        public bool DetectPII { get; init; } = true;

        /// <summary>Categorize content.</summary>
        public bool Categorize { get; init; } = true;

        /// <summary>Generate embeddings.</summary>
        public bool GenerateEmbeddings { get; init; } = true;

        /// <summary>Extract keywords.</summary>
        public bool ExtractKeywords { get; init; } = true;

        /// <summary>Compute content hash.</summary>
        public bool ComputeHash { get; init; } = true;

        /// <summary>Maximum text length to process.</summary>
        public int MaxTextLength { get; init; } = 100000;

        /// <summary>Target summary length.</summary>
        public int TargetSummaryLength { get; init; } = 200;

        /// <summary>Maximum keywords to extract.</summary>
        public int MaxKeywords { get; init; } = 20;

        /// <summary>Embedding dimension.</summary>
        public int EmbeddingDimension { get; init; } = 384;

        /// <summary>Additional context for analysis.</summary>
        public Dictionary<string, object> Context { get; init; } = new();
    }

    /// <summary>
    /// Keyword with weight/importance score.
    /// </summary>
    public sealed class WeightedKeyword
    {
        /// <summary>The keyword text.</summary>
        public required string Keyword { get; init; }

        /// <summary>Weight/importance score (0-1).</summary>
        public float Weight { get; init; }

        /// <summary>Frequency in text.</summary>
        public int Frequency { get; init; }

        /// <summary>TF-IDF score.</summary>
        public float TfIdf { get; init; }
    }

    /// <summary>
    /// Result of language detection.
    /// </summary>
    public sealed record LanguageDetectionResult
    {
        /// <summary>Primary detected language (ISO 639-1 code).</summary>
        public required string PrimaryLanguage { get; init; }

        /// <summary>Confidence in primary language (0-1).</summary>
        public float Confidence { get; init; }

        /// <summary>All detected languages with scores.</summary>
        public Dictionary<string, float> LanguageScores { get; init; } = new();

        /// <summary>Whether document is multilingual.</summary>
        public bool IsMultilingual { get; init; }

        /// <summary>Detected script (Latin, Cyrillic, etc.).</summary>
        public ScriptType Script { get; init; }

        /// <summary>Processing duration.</summary>
        public TimeSpan Duration { get; init; }
    }

    /// <summary>
    /// Script types for language detection.
    /// </summary>
    public enum ScriptType
    {
        /// <summary>Unknown script.</summary>
        Unknown,
        /// <summary>Latin alphabet.</summary>
        Latin,
        /// <summary>Cyrillic alphabet.</summary>
        Cyrillic,
        /// <summary>Greek alphabet.</summary>
        Greek,
        /// <summary>Arabic script.</summary>
        Arabic,
        /// <summary>Hebrew script.</summary>
        Hebrew,
        /// <summary>CJK (Chinese, Japanese, Korean).</summary>
        CJK,
        /// <summary>Devanagari (Hindi, Sanskrit).</summary>
        Devanagari,
        /// <summary>Thai script.</summary>
        Thai,
        /// <summary>Mixed scripts.</summary>
        Mixed
    }

    /// <summary>
    /// Result of content summarization.
    /// </summary>
    public sealed record SummarizationResult
    {
        /// <summary>Generated summary.</summary>
        public string Summary { get; init; } = string.Empty;

        /// <summary>Summary type (extractive or abstractive).</summary>
        public SummarizationType Type { get; init; }

        /// <summary>Compression ratio (summary length / original length).</summary>
        public float CompressionRatio { get; init; }

        /// <summary>Key sentences extracted (for extractive).</summary>
        public List<string> KeySentences { get; init; } = new();

        /// <summary>Main topics identified.</summary>
        public List<string> MainTopics { get; init; } = new();

        /// <summary>Processing duration.</summary>
        public TimeSpan Duration { get; init; }

        /// <summary>Whether AI was used.</summary>
        public bool UsedAI { get; init; }
    }

    /// <summary>
    /// Type of summarization.
    /// </summary>
    public enum SummarizationType
    {
        /// <summary>Extractive summary (key sentences from text).</summary>
        Extractive,
        /// <summary>Abstractive summary (generated paraphrase).</summary>
        Abstractive,
        /// <summary>Hybrid approach.</summary>
        Hybrid
    }

    /// <summary>
    /// Duplicate detection result.
    /// </summary>
    public sealed class DuplicateDetectionResult
    {
        /// <summary>Whether duplicates were found.</summary>
        public bool HasDuplicates => Duplicates.Count > 0;

        /// <summary>Found duplicates.</summary>
        public List<DuplicateMatch> Duplicates { get; init; } = new();

        /// <summary>Near-duplicates (high similarity but not exact).</summary>
        public List<DuplicateMatch> NearDuplicates { get; init; } = new();

        /// <summary>Processing duration.</summary>
        public TimeSpan Duration { get; init; }

        /// <summary>Documents compared.</summary>
        public int DocumentsCompared { get; init; }
    }

    /// <summary>
    /// A duplicate document match.
    /// </summary>
    public sealed class DuplicateMatch
    {
        /// <summary>Source document ID.</summary>
        public required string SourceId { get; init; }

        /// <summary>Duplicate document ID.</summary>
        public required string DuplicateId { get; init; }

        /// <summary>Similarity score (0-1).</summary>
        public float Similarity { get; init; }

        /// <summary>Type of duplicate.</summary>
        public DuplicateType Type { get; init; }

        /// <summary>Matching features.</summary>
        public List<string> MatchingFeatures { get; init; } = new();

        /// <summary>Recommendation for handling.</summary>
        public DuplicateRecommendation Recommendation { get; init; }
    }

    /// <summary>
    /// Types of duplicates.
    /// </summary>
    public enum DuplicateType
    {
        /// <summary>Exact duplicate (identical content).</summary>
        Exact,
        /// <summary>Near-duplicate (very similar content).</summary>
        NearDuplicate,
        /// <summary>Semantic duplicate (same meaning, different wording).</summary>
        Semantic,
        /// <summary>Partial duplicate (subset of content matches).</summary>
        Partial,
        /// <summary>Version duplicate (different version of same document).</summary>
        Version
    }

    /// <summary>
    /// Recommendation for handling duplicates.
    /// </summary>
    public enum DuplicateRecommendation
    {
        /// <summary>Keep original, delete duplicate.</summary>
        DeleteDuplicate,
        /// <summary>Keep newer version.</summary>
        KeepNewer,
        /// <summary>Keep older version.</summary>
        KeepOlder,
        /// <summary>Merge documents.</summary>
        Merge,
        /// <summary>Link as versions.</summary>
        LinkAsVersions,
        /// <summary>Keep both (different purposes).</summary>
        KeepBoth
    }
}
