using System;

namespace DataWarehouse.SDK.Contracts.IntelligenceAware
{
    /// <summary>
    /// Defines the AI capabilities that can be provided by Universal Intelligence (T90).
    /// Plugins check these flags to determine which AI features are available before
    /// requesting them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The flags are organized into logical groups:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Core AI</b>: Embeddings, NLP, Conversation</item>
    ///   <item><b>Analysis</b>: Classification, AnomalyDetection, SentimentAnalysis</item>
    ///   <item><b>Prediction</b>: Prediction, AccessPatternPrediction, FailurePrediction</item>
    ///   <item><b>Content</b>: Summarization, EntityExtraction, ContentGeneration</item>
    ///   <item><b>Security</b>: PIIDetection, ThreatAssessment</item>
    ///   <item><b>Data</b>: SemanticDeduplication, DataLifecyclePrediction</item>
    /// </list>
    /// <para>
    /// Use bitwise operations to check for multiple capabilities:
    /// <code>
    /// if ((capabilities &amp; (IntelligenceCapabilities.Embeddings | IntelligenceCapabilities.Classification)) != 0)
    /// {
    ///     // At least one of Embeddings or Classification is available
    /// }
    /// </code>
    /// </para>
    /// </remarks>
    [Flags]
    public enum IntelligenceCapabilities : long
    {
        /// <summary>
        /// No Intelligence capabilities available.
        /// </summary>
        None = 0,

        // ========================================
        // Core AI Capabilities (bits 0-7)
        // ========================================

        /// <summary>
        /// Generate vector embeddings from text or data.
        /// Required for semantic search, similarity matching, and clustering.
        /// </summary>
        Embeddings = 1L << 0,

        /// <summary>
        /// Natural Language Processing for text understanding.
        /// Enables intent parsing, language detection, and text analysis.
        /// </summary>
        NLP = 1L << 1,

        /// <summary>
        /// Multi-turn conversation and chat capabilities.
        /// Supports context-aware dialogue and follow-up questions.
        /// </summary>
        Conversation = 1L << 2,

        /// <summary>
        /// Text completion and generation.
        /// Enables prompt-based content generation.
        /// </summary>
        TextCompletion = 1L << 3,

        /// <summary>
        /// Function/tool calling with structured output.
        /// Allows AI to invoke predefined functions with type-safe arguments.
        /// </summary>
        FunctionCalling = 1L << 4,

        /// <summary>
        /// Streaming response support for real-time output.
        /// Enables progressive display of AI responses.
        /// </summary>
        Streaming = 1L << 5,

        /// <summary>
        /// Code generation and analysis capabilities.
        /// Supports writing, reviewing, and explaining code.
        /// </summary>
        CodeGeneration = 1L << 6,

        /// <summary>
        /// Image analysis and understanding.
        /// Enables visual content analysis and description.
        /// </summary>
        ImageAnalysis = 1L << 7,

        // ========================================
        // Analysis Capabilities (bits 8-15)
        // ========================================

        /// <summary>
        /// Content classification into predefined categories.
        /// Supports multi-label and hierarchical classification.
        /// </summary>
        Classification = 1L << 8,

        /// <summary>
        /// Detect anomalies and outliers in data patterns.
        /// Useful for security monitoring and data quality checks.
        /// </summary>
        AnomalyDetection = 1L << 9,

        /// <summary>
        /// Sentiment analysis for text content.
        /// Determines positive, negative, or neutral sentiment.
        /// </summary>
        SentimentAnalysis = 1L << 10,

        /// <summary>
        /// Topic modeling and extraction.
        /// Identifies themes and subjects in content.
        /// </summary>
        TopicModeling = 1L << 11,

        /// <summary>
        /// Intent recognition from user input.
        /// Determines what action a user wants to perform.
        /// </summary>
        IntentRecognition = 1L << 12,

        /// <summary>
        /// Language detection for multilingual content.
        /// Identifies the language of text content.
        /// </summary>
        LanguageDetection = 1L << 13,

        /// <summary>
        /// Clustering similar items together.
        /// Groups related content without predefined categories.
        /// </summary>
        Clustering = 1L << 14,

        /// <summary>
        /// Similarity scoring between items.
        /// Quantifies how similar two pieces of content are.
        /// </summary>
        SimilarityScoring = 1L << 15,

        // ========================================
        // Prediction Capabilities (bits 16-23)
        // ========================================

        /// <summary>
        /// General prediction and forecasting.
        /// Predicts future values or outcomes based on patterns.
        /// </summary>
        Prediction = 1L << 16,

        /// <summary>
        /// Predict access patterns for storage tiering.
        /// Recommends optimal storage tier based on predicted access frequency.
        /// </summary>
        AccessPatternPrediction = 1L << 17,

        /// <summary>
        /// Predict system or component failures.
        /// Enables proactive maintenance and alerting.
        /// </summary>
        FailurePrediction = 1L << 18,

        /// <summary>
        /// Predict optimal data lifecycle transitions.
        /// Recommends when to archive, tier, or delete data.
        /// </summary>
        DataLifecyclePrediction = 1L << 19,

        /// <summary>
        /// Predict query performance and resource needs.
        /// Estimates execution time and resource consumption.
        /// </summary>
        QueryPrediction = 1L << 20,

        /// <summary>
        /// Predict user behavior patterns.
        /// Anticipates user actions for proactive features.
        /// </summary>
        BehaviorPrediction = 1L << 21,

        /// <summary>
        /// Time series forecasting.
        /// Predicts future values in temporal sequences.
        /// </summary>
        TimeSeriesForecasting = 1L << 22,

        /// <summary>
        /// Capacity planning predictions.
        /// Forecasts storage and compute requirements.
        /// </summary>
        CapacityPrediction = 1L << 23,

        // ========================================
        // Content Capabilities (bits 24-31)
        // ========================================

        /// <summary>
        /// Summarize long content into concise form.
        /// Supports extractive and abstractive summarization.
        /// </summary>
        Summarization = 1L << 24,

        /// <summary>
        /// Extract named entities from text.
        /// Identifies people, places, organizations, dates, etc.
        /// </summary>
        EntityExtraction = 1L << 25,

        /// <summary>
        /// Generate content from prompts or templates.
        /// Creates descriptions, documentation, and explanatory text.
        /// </summary>
        ContentGeneration = 1L << 26,

        /// <summary>
        /// Extract key phrases and keywords.
        /// Identifies important terms for indexing and search.
        /// </summary>
        KeywordExtraction = 1L << 27,

        /// <summary>
        /// Translate between languages.
        /// Supports multi-language content management.
        /// </summary>
        Translation = 1L << 28,

        /// <summary>
        /// Question answering over documents.
        /// Provides answers based on content analysis.
        /// </summary>
        QuestionAnswering = 1L << 29,

        /// <summary>
        /// Semantic search across content.
        /// Finds relevant content based on meaning, not just keywords.
        /// </summary>
        SemanticSearch = 1L << 30,

        /// <summary>
        /// Rewrite or paraphrase content.
        /// Transforms text while preserving meaning.
        /// </summary>
        ContentRewriting = 1L << 31,

        // ========================================
        // Security Capabilities (bits 32-39)
        // ========================================

        /// <summary>
        /// Detect Personally Identifiable Information (PII).
        /// Identifies sensitive data for compliance and protection.
        /// </summary>
        PIIDetection = 1L << 32,

        /// <summary>
        /// Assess security threats and risks.
        /// Evaluates potential vulnerabilities and attack vectors.
        /// </summary>
        ThreatAssessment = 1L << 33,

        /// <summary>
        /// Recommend encryption algorithms and strengths.
        /// Suggests optimal cipher based on content and requirements.
        /// </summary>
        CipherRecommendation = 1L << 34,

        /// <summary>
        /// User and Entity Behavior Analytics (UEBA).
        /// Detects unusual user activity patterns.
        /// </summary>
        BehaviorAnalytics = 1L << 35,

        /// <summary>
        /// Access control recommendations.
        /// Suggests appropriate permissions based on content and context.
        /// </summary>
        AccessControlRecommendation = 1L << 36,

        /// <summary>
        /// Compliance classification and tagging.
        /// Identifies regulatory requirements (GDPR, HIPAA, etc.).
        /// </summary>
        ComplianceClassification = 1L << 37,

        /// <summary>
        /// Data sensitivity classification.
        /// Determines confidentiality level of content.
        /// </summary>
        SensitivityClassification = 1L << 38,

        /// <summary>
        /// Anomaly-based intrusion detection.
        /// Identifies potential security breaches from patterns.
        /// </summary>
        IntrusionDetection = 1L << 39,

        // ========================================
        // Data Management Capabilities (bits 40-47)
        // ========================================

        /// <summary>
        /// Semantic deduplication based on meaning.
        /// Identifies duplicate content even with textual differences.
        /// </summary>
        SemanticDeduplication = 1L << 40,

        /// <summary>
        /// Recommend compression algorithms.
        /// Suggests optimal compression based on content analysis.
        /// </summary>
        CompressionRecommendation = 1L << 41,

        /// <summary>
        /// Recommend storage tier placement.
        /// Suggests optimal tier based on content characteristics.
        /// </summary>
        TieringRecommendation = 1L << 42,

        /// <summary>
        /// Schema inference and enrichment.
        /// Automatically derives and enhances data schemas.
        /// </summary>
        SchemaInference = 1L << 43,

        /// <summary>
        /// Data quality assessment.
        /// Evaluates completeness, consistency, and accuracy.
        /// </summary>
        DataQualityAssessment = 1L << 44,

        /// <summary>
        /// Relationship discovery between data items.
        /// Identifies connections and dependencies.
        /// </summary>
        RelationshipDiscovery = 1L << 45,

        /// <summary>
        /// Metadata enrichment from content analysis.
        /// Automatically generates tags and descriptions.
        /// </summary>
        MetadataEnrichment = 1L << 46,

        /// <summary>
        /// Query optimization recommendations.
        /// Suggests improved query patterns and indexes.
        /// </summary>
        QueryOptimization = 1L << 47,

        // ========================================
        // Connector Capabilities (bits 48-55)
        // ========================================

        /// <summary>
        /// Transform connector request payloads.
        /// Modifies outgoing requests with AI enhancement.
        /// </summary>
        RequestTransformation = 1L << 48,

        /// <summary>
        /// Transform connector response payloads.
        /// Enriches incoming responses with AI analysis.
        /// </summary>
        ResponseTransformation = 1L << 49,

        /// <summary>
        /// Schema mapping and transformation.
        /// Maps between different data schemas intelligently.
        /// </summary>
        SchemaMapping = 1L << 50,

        /// <summary>
        /// Protocol translation assistance.
        /// Helps convert between different protocols.
        /// </summary>
        ProtocolTranslation = 1L << 51,

        // ========================================
        // Capability Groups
        // ========================================

        /// <summary>
        /// All core AI capabilities for general use.
        /// </summary>
        AllCore = Embeddings | NLP | Conversation | TextCompletion | FunctionCalling | Streaming,

        /// <summary>
        /// All analysis capabilities.
        /// </summary>
        AllAnalysis = Classification | AnomalyDetection | SentimentAnalysis | TopicModeling |
                      IntentRecognition | LanguageDetection | Clustering | SimilarityScoring,

        /// <summary>
        /// All prediction capabilities.
        /// </summary>
        AllPrediction = Prediction | AccessPatternPrediction | FailurePrediction | DataLifecyclePrediction |
                        QueryPrediction | BehaviorPrediction | TimeSeriesForecasting | CapacityPrediction,

        /// <summary>
        /// All content processing capabilities.
        /// </summary>
        AllContent = Summarization | EntityExtraction | ContentGeneration | KeywordExtraction |
                     Translation | QuestionAnswering | SemanticSearch | ContentRewriting,

        /// <summary>
        /// All security-related capabilities.
        /// </summary>
        AllSecurity = PIIDetection | ThreatAssessment | CipherRecommendation | BehaviorAnalytics |
                      AccessControlRecommendation | ComplianceClassification | SensitivityClassification | IntrusionDetection,

        /// <summary>
        /// All data management capabilities.
        /// </summary>
        AllDataManagement = SemanticDeduplication | CompressionRecommendation | TieringRecommendation |
                           SchemaInference | DataQualityAssessment | RelationshipDiscovery | MetadataEnrichment | QueryOptimization,

        /// <summary>
        /// All connector integration capabilities.
        /// </summary>
        AllConnector = RequestTransformation | ResponseTransformation | SchemaMapping | ProtocolTranslation
    }
}
