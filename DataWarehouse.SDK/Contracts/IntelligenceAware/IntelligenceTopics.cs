namespace DataWarehouse.SDK.Contracts.IntelligenceAware
{
    /// <summary>
    /// Standard message bus topics for Universal Intelligence communication.
    /// These topics define the protocol for discovering and interacting with
    /// the T90 Intelligence system.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Intelligence communication protocol uses three categories of topics:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Discovery</b>: Topics for finding and querying Intelligence availability</item>
    ///   <item><b>Request</b>: Topics for requesting specific AI operations</item>
    ///   <item><b>Broadcast</b>: Topics for system-wide Intelligence announcements</item>
    /// </list>
    /// <para>
    /// All topics follow the naming convention: <c>intelligence.[category].[operation]</c>
    /// </para>
    /// </remarks>
    public static class IntelligenceTopics
    {
        // ========================================
        // Discovery Topics
        // ========================================

        /// <summary>
        /// Topic for discovering Intelligence availability.
        /// Send a request to this topic; T90 responds with capability information.
        /// </summary>
        /// <remarks>
        /// Request payload should include:
        /// <list type="bullet">
        ///   <item><c>requestorId</c>: Plugin ID making the request</item>
        ///   <item><c>requestedCapabilities</c>: Optional specific capabilities to check</item>
        /// </list>
        /// Response payload includes:
        /// <list type="bullet">
        ///   <item><c>available</c>: Boolean indicating availability</item>
        ///   <item><c>capabilities</c>: IntelligenceCapabilities flags</item>
        ///   <item><c>version</c>: T90 version string</item>
        /// </list>
        /// </remarks>
        public const string Discover = "intelligence.discover";

        /// <summary>
        /// Response topic for discovery requests.
        /// T90 publishes responses to this topic.
        /// </summary>
        public const string DiscoverResponse = "intelligence.discover.response";

        /// <summary>
        /// Topic for querying specific capabilities.
        /// Returns detailed information about a specific capability.
        /// </summary>
        public const string QueryCapability = "intelligence.capability.query";

        /// <summary>
        /// Response topic for capability queries.
        /// </summary>
        public const string QueryCapabilityResponse = "intelligence.capability.query.response";

        // ========================================
        // Broadcast Topics
        // ========================================

        /// <summary>
        /// Broadcast topic when T90 becomes available.
        /// Published by T90 on successful startup.
        /// </summary>
        /// <remarks>
        /// Payload includes:
        /// <list type="bullet">
        ///   <item><c>capabilities</c>: Available IntelligenceCapabilities</item>
        ///   <item><c>version</c>: T90 version</item>
        ///   <item><c>providers</c>: List of active AI providers</item>
        /// </list>
        /// Plugins implementing <see cref="IIntelligenceAwareNotifiable"/> subscribe
        /// to this topic to receive availability notifications.
        /// </remarks>
        public const string Available = "intelligence.available";

        /// <summary>
        /// Broadcast topic when T90 is shutting down.
        /// Published by T90 before stopping.
        /// </summary>
        /// <remarks>
        /// Plugins should switch to fallback behavior when receiving this message.
        /// </remarks>
        public const string Unavailable = "intelligence.unavailable";

        /// <summary>
        /// Broadcast topic when Intelligence capabilities change.
        /// Published when providers are added, removed, or their capabilities change.
        /// </summary>
        public const string CapabilitiesChanged = "intelligence.capabilities.changed";

        // ========================================
        // Core AI Request Topics
        // ========================================

        /// <summary>
        /// Request topic for generating embeddings.
        /// </summary>
        /// <remarks>
        /// Request payload:
        /// <list type="bullet">
        ///   <item><c>texts</c>: Array of strings to embed</item>
        ///   <item><c>model</c>: Optional model preference</item>
        ///   <item><c>dimensions</c>: Optional embedding dimensions</item>
        /// </list>
        /// Response:
        /// <list type="bullet">
        ///   <item><c>embeddings</c>: Array of float[] vectors</item>
        ///   <item><c>model</c>: Model used</item>
        ///   <item><c>dimensions</c>: Actual dimensions</item>
        /// </list>
        /// </remarks>
        public const string RequestEmbeddings = "intelligence.request.embeddings";

        /// <summary>
        /// Response topic for embedding requests.
        /// </summary>
        public const string RequestEmbeddingsResponse = "intelligence.request.embeddings.response";

        /// <summary>
        /// Request topic for text classification.
        /// </summary>
        /// <remarks>
        /// Request payload:
        /// <list type="bullet">
        ///   <item><c>text</c>: Text to classify</item>
        ///   <item><c>categories</c>: Array of possible categories</item>
        ///   <item><c>multiLabel</c>: Boolean for multi-label classification</item>
        /// </list>
        /// Response:
        /// <list type="bullet">
        ///   <item><c>classifications</c>: Array of { category, confidence }</item>
        /// </list>
        /// </remarks>
        public const string RequestClassification = "intelligence.request.classification";

        /// <summary>
        /// Response topic for classification requests.
        /// </summary>
        public const string RequestClassificationResponse = "intelligence.request.classification.response";

        /// <summary>
        /// Request topic for anomaly detection.
        /// </summary>
        /// <remarks>
        /// Request payload:
        /// <list type="bullet">
        ///   <item><c>data</c>: Data points to analyze</item>
        ///   <item><c>baseline</c>: Optional baseline for comparison</item>
        ///   <item><c>sensitivity</c>: Detection sensitivity (0.0-1.0)</item>
        /// </list>
        /// Response:
        /// <list type="bullet">
        ///   <item><c>anomalies</c>: Array of detected anomalies with scores</item>
        ///   <item><c>isAnomaly</c>: Boolean overall assessment</item>
        /// </list>
        /// </remarks>
        public const string RequestAnomalyDetection = "intelligence.request.anomaly";

        /// <summary>
        /// Response topic for anomaly detection requests.
        /// </summary>
        public const string RequestAnomalyDetectionResponse = "intelligence.request.anomaly.response";

        /// <summary>
        /// Request topic for predictions.
        /// </summary>
        public const string RequestPrediction = "intelligence.request.prediction";

        /// <summary>
        /// Response topic for prediction requests.
        /// </summary>
        public const string RequestPredictionResponse = "intelligence.request.prediction.response";

        /// <summary>
        /// Request topic for text completion/generation.
        /// </summary>
        public const string RequestCompletion = "intelligence.request.completion";

        /// <summary>
        /// Response topic for completion requests.
        /// </summary>
        public const string RequestCompletionResponse = "intelligence.request.completion.response";

        /// <summary>
        /// Request topic for conversation/chat.
        /// </summary>
        public const string RequestConversation = "intelligence.request.conversation";

        /// <summary>
        /// Response topic for conversation requests.
        /// </summary>
        public const string RequestConversationResponse = "intelligence.request.conversation.response";

        /// <summary>
        /// Request topic for summarization.
        /// </summary>
        public const string RequestSummarization = "intelligence.request.summarization";

        /// <summary>
        /// Response topic for summarization requests.
        /// </summary>
        public const string RequestSummarizationResponse = "intelligence.request.summarization.response";

        /// <summary>
        /// Request topic for entity extraction.
        /// </summary>
        public const string RequestEntityExtraction = "intelligence.request.entities";

        /// <summary>
        /// Response topic for entity extraction requests.
        /// </summary>
        public const string RequestEntityExtractionResponse = "intelligence.request.entities.response";

        /// <summary>
        /// Request topic for sentiment analysis.
        /// </summary>
        public const string RequestSentiment = "intelligence.request.sentiment";

        /// <summary>
        /// Response topic for sentiment analysis requests.
        /// </summary>
        public const string RequestSentimentResponse = "intelligence.request.sentiment.response";

        /// <summary>
        /// Request topic for semantic search.
        /// </summary>
        public const string RequestSemanticSearch = "intelligence.request.semantic-search";

        /// <summary>
        /// Response topic for semantic search requests.
        /// </summary>
        public const string RequestSemanticSearchResponse = "intelligence.request.semantic-search.response";

        /// <summary>
        /// Request topic for intent recognition.
        /// </summary>
        public const string RequestIntent = "intelligence.request.intent";

        /// <summary>
        /// Response topic for intent recognition requests.
        /// </summary>
        public const string RequestIntentResponse = "intelligence.request.intent.response";

        // ========================================
        // Security Request Topics
        // ========================================

        /// <summary>
        /// Request topic for PII detection.
        /// </summary>
        public const string RequestPIIDetection = "intelligence.request.pii-detection";

        /// <summary>
        /// Response topic for PII detection requests.
        /// </summary>
        public const string RequestPIIDetectionResponse = "intelligence.request.pii-detection.response";

        /// <summary>
        /// Request topic for threat assessment.
        /// </summary>
        public const string RequestThreatAssessment = "intelligence.request.threat-assessment";

        /// <summary>
        /// Response topic for threat assessment requests.
        /// </summary>
        public const string RequestThreatAssessmentResponse = "intelligence.request.threat-assessment.response";

        /// <summary>
        /// Request topic for cipher/encryption recommendations.
        /// </summary>
        public const string RequestCipherRecommendation = "intelligence.request.cipher-recommendation";

        /// <summary>
        /// Response topic for cipher recommendation requests.
        /// </summary>
        public const string RequestCipherRecommendationResponse = "intelligence.request.cipher-recommendation.response";

        /// <summary>
        /// Request topic for behavior analysis (UEBA).
        /// </summary>
        public const string RequestBehaviorAnalysis = "intelligence.request.behavior-analysis";

        /// <summary>
        /// Response topic for behavior analysis requests.
        /// </summary>
        public const string RequestBehaviorAnalysisResponse = "intelligence.request.behavior-analysis.response";

        /// <summary>
        /// Request topic for compliance classification.
        /// </summary>
        public const string RequestComplianceClassification = "intelligence.request.compliance-classification";

        /// <summary>
        /// Response topic for compliance classification requests.
        /// </summary>
        public const string RequestComplianceClassificationResponse = "intelligence.request.compliance-classification.response";

        // ========================================
        // Data Management Request Topics
        // ========================================

        /// <summary>
        /// Request topic for compression recommendations.
        /// </summary>
        public const string RequestCompressionRecommendation = "intelligence.request.compression-recommendation";

        /// <summary>
        /// Response topic for compression recommendation requests.
        /// </summary>
        public const string RequestCompressionRecommendationResponse = "intelligence.request.compression-recommendation.response";

        /// <summary>
        /// Request topic for storage tiering recommendations.
        /// </summary>
        public const string RequestTieringRecommendation = "intelligence.request.tiering-recommendation";

        /// <summary>
        /// Response topic for tiering recommendation requests.
        /// </summary>
        public const string RequestTieringRecommendationResponse = "intelligence.request.tiering-recommendation.response";

        /// <summary>
        /// Request topic for access pattern prediction.
        /// </summary>
        public const string RequestAccessPrediction = "intelligence.request.access-prediction";

        /// <summary>
        /// Response topic for access prediction requests.
        /// </summary>
        public const string RequestAccessPredictionResponse = "intelligence.request.access-prediction.response";

        /// <summary>
        /// Request topic for semantic deduplication analysis.
        /// </summary>
        public const string RequestSemanticDedup = "intelligence.request.semantic-dedup";

        /// <summary>
        /// Response topic for semantic deduplication requests.
        /// </summary>
        public const string RequestSemanticDedupResponse = "intelligence.request.semantic-dedup.response";

        /// <summary>
        /// Request topic for data lifecycle predictions.
        /// </summary>
        public const string RequestLifecyclePrediction = "intelligence.request.lifecycle-prediction";

        /// <summary>
        /// Response topic for lifecycle prediction requests.
        /// </summary>
        public const string RequestLifecyclePredictionResponse = "intelligence.request.lifecycle-prediction.response";

        // ========================================
        // Connector Integration Topics
        // ========================================

        /// <summary>
        /// Request topic for transforming connector requests.
        /// </summary>
        public const string TransformRequest = "intelligence.connector.transform-request";

        /// <summary>
        /// Response topic for request transformation.
        /// </summary>
        public const string TransformRequestResponse = "intelligence.connector.transform-request.response";

        /// <summary>
        /// Request topic for transforming connector responses.
        /// </summary>
        public const string TransformResponse = "intelligence.connector.transform-response";

        /// <summary>
        /// Response topic for response transformation.
        /// </summary>
        public const string TransformResponseResult = "intelligence.connector.transform-response.response";

        /// <summary>
        /// Request topic for schema enrichment.
        /// </summary>
        public const string EnrichSchema = "intelligence.connector.enrich-schema";

        /// <summary>
        /// Response topic for schema enrichment requests.
        /// </summary>
        public const string EnrichSchemaResponse = "intelligence.connector.enrich-schema.response";

        // ========================================
        // Topic Patterns
        // ========================================

        /// <summary>
        /// Pattern for subscribing to all Intelligence request topics.
        /// </summary>
        public const string AllRequestsPattern = "intelligence.request.*";

        /// <summary>
        /// Pattern for subscribing to all Intelligence broadcast topics.
        /// </summary>
        public const string AllBroadcastsPattern = "intelligence.*";

        /// <summary>
        /// Pattern for subscribing to all connector integration topics.
        /// </summary>
        public const string AllConnectorPattern = "intelligence.connector.*";
    }
}
