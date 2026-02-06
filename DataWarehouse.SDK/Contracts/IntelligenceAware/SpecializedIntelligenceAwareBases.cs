using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.IntelligenceAware
{
    // ================================================================================
    // T127: SPECIALIZED INTELLIGENCE-AWARE BASE CLASSES
    // Each base class provides domain-specific hooks for Intelligence integration.
    // ================================================================================

    #region Connector Plugin Base

    /// <summary>
    /// Intelligence-aware base class for connector plugins.
    /// Provides hooks for AI-powered request/response transformation, schema enrichment,
    /// and intelligent query optimization.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Connector plugins can leverage Intelligence for:
    /// </para>
    /// <list type="bullet">
    ///   <item>Request payload transformation and optimization</item>
    ///   <item>Response enrichment with semantic metadata</item>
    ///   <item>Intelligent schema discovery and mapping</item>
    ///   <item>Query optimization recommendations</item>
    ///   <item>Anomaly detection in responses</item>
    /// </list>
    /// </remarks>
    public abstract class IntelligenceAwareConnectorPluginBase : IntelligenceAwarePluginBase
    {
        /// <summary>
        /// Gets the connector protocol type (e.g., "REST", "SQL", "GraphQL").
        /// </summary>
        public abstract string Protocol { get; }

        /// <summary>
        /// Transforms a request payload using Intelligence before sending.
        /// </summary>
        /// <param name="request">The original request payload.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Transformed request, or original if Intelligence unavailable.</returns>
        protected async Task<Dictionary<string, object>> TransformRequestAsync(
            Dictionary<string, object> request,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.RequestTransformation))
                return request;

            var payload = new Dictionary<string, object>
            {
                ["request"] = request,
                ["protocol"] = Protocol,
                ["connectorId"] = Id,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.TransformRequest,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true &&
                response.Payload is Dictionary<string, object> result &&
                result.TryGetValue("transformedRequest", out var transformed) &&
                transformed is Dictionary<string, object> transformedRequest)
            {
                return transformedRequest;
            }

            return request;
        }

        /// <summary>
        /// Transforms a response payload using Intelligence after receiving.
        /// </summary>
        /// <param name="response">The original response payload.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Transformed response with enrichments.</returns>
        protected async Task<ConnectorResponseEnrichment> TransformResponseAsync(
            Dictionary<string, object> response,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.ResponseTransformation))
                return new ConnectorResponseEnrichment { OriginalResponse = response };

            var payload = new Dictionary<string, object>
            {
                ["response"] = response,
                ["protocol"] = Protocol,
                ["connectorId"] = Id,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            var result = await SendIntelligenceRequestAsync(
                IntelligenceTopics.TransformResponse,
                payload,
                context?.Timeout,
                ct);

            if (result?.Success == true && result.Payload is Dictionary<string, object> enriched)
            {
                return new ConnectorResponseEnrichment
                {
                    OriginalResponse = response,
                    EnrichedResponse = enriched.TryGetValue("transformedResponse", out var t) && t is Dictionary<string, object> tr ? tr : response,
                    Metadata = enriched.TryGetValue("metadata", out var m) && m is Dictionary<string, object> meta ? meta : null,
                    Entities = enriched.TryGetValue("entities", out var e) && e is ExtractedEntity[] entities ? entities : null,
                    Classifications = enriched.TryGetValue("classifications", out var c) && c is ClassificationResult[] cls ? cls : null
                };
            }

            return new ConnectorResponseEnrichment { OriginalResponse = response };
        }

        /// <summary>
        /// Enriches a discovered schema with semantic metadata.
        /// </summary>
        /// <param name="schema">The discovered schema.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Enriched schema with AI-generated metadata.</returns>
        protected async Task<SchemaEnrichmentResult> EnrichSchemaAsync(
            Dictionary<string, object> schema,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.SchemaInference))
                return new SchemaEnrichmentResult { OriginalSchema = schema };

            var payload = new Dictionary<string, object>
            {
                ["schema"] = schema,
                ["protocol"] = Protocol,
                ["connectorId"] = Id,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.EnrichSchema,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new SchemaEnrichmentResult
                {
                    OriginalSchema = schema,
                    EnrichedSchema = result.TryGetValue("enrichedSchema", out var es) && es is Dictionary<string, object> enrichedSchema ? enrichedSchema : schema,
                    FieldDescriptions = result.TryGetValue("fieldDescriptions", out var fd) && fd is Dictionary<string, string> descriptions ? descriptions : null,
                    RelationshipSuggestions = result.TryGetValue("relationships", out var rs) && rs is RelationshipSuggestion[] relationships ? relationships : null,
                    PIISensitivity = result.TryGetValue("piiSensitivity", out var pii) && pii is Dictionary<string, string> piiMap ? piiMap : null
                };
            }

            return new SchemaEnrichmentResult { OriginalSchema = schema };
        }

        /// <summary>
        /// Gets query optimization recommendations.
        /// </summary>
        /// <param name="query">The query to optimize.</param>
        /// <param name="schema">Optional schema context.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Optimization recommendations.</returns>
        protected async Task<QueryOptimizationResult?> GetQueryOptimizationAsync(
            string query,
            Dictionary<string, object>? schema = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.QueryOptimization))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["query"] = query,
                ["protocol"] = Protocol,
                ["connectorId"] = Id,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (schema != null)
                payload["schema"] = schema;

            var response = await SendIntelligenceRequestAsync(
                "intelligence.request.query-optimization",
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new QueryOptimizationResult
                {
                    OriginalQuery = query,
                    OptimizedQuery = result.TryGetValue("optimizedQuery", out var oq) && oq is string optimized ? optimized : query,
                    Recommendations = result.TryGetValue("recommendations", out var recs) && recs is string[] recommendations ? recommendations : Array.Empty<string>(),
                    EstimatedImprovement = result.TryGetValue("estimatedImprovement", out var ei) && ei is double improvement ? improvement : 0.0
                };
            }

            return null;
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["ConnectorType"] = "IntelligenceAware";
            metadata["Protocol"] = Protocol;
            metadata["SupportsRequestTransformation"] = HasCapability(IntelligenceCapabilities.RequestTransformation);
            metadata["SupportsResponseTransformation"] = HasCapability(IntelligenceCapabilities.ResponseTransformation);
            metadata["SupportsSchemaEnrichment"] = HasCapability(IntelligenceCapabilities.SchemaInference);
            return metadata;
        }
    }

    #endregion

    #region Interface Plugin Base

    /// <summary>
    /// Intelligence-aware base class for interface plugins (REST, gRPC, WebSocket, etc.).
    /// Provides hooks for NLP processing, intent recognition, and conversational interfaces.
    /// </summary>
    public abstract class IntelligenceAwareInterfacePluginBase : IntelligenceAwarePluginBase
    {
        /// <summary>
        /// Gets the interface protocol type (e.g., "REST", "gRPC", "WebSocket").
        /// </summary>
        public abstract string InterfaceProtocol { get; }

        /// <summary>
        /// Gets the interface endpoint port (if applicable).
        /// </summary>
        public virtual int? Port => null;

        /// <summary>
        /// Parses natural language input to extract intent and entities.
        /// </summary>
        /// <param name="input">Natural language input from user.</param>
        /// <param name="availableIntents">Optional list of available intents to match.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Parsed intent result, or null if unavailable.</returns>
        protected async Task<IntentParseResult?> ParseIntentAsync(
            string input,
            string[]? availableIntents = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.IntentRecognition))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["input"] = input,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (availableIntents != null)
                payload["availableIntents"] = availableIntents;

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestIntent,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new IntentParseResult
                {
                    Intent = result.TryGetValue("intent", out var i) && i is string intent ? intent : null,
                    Confidence = result.TryGetValue("confidence", out var c) && c is double conf ? conf : 0.0,
                    Entities = result.TryGetValue("entities", out var e) && e is Dictionary<string, object> entities ? entities : new Dictionary<string, object>(),
                    AlternativeIntents = result.TryGetValue("alternatives", out var a) && a is IntentAlternative[] alts ? alts : Array.Empty<IntentAlternative>()
                };
            }

            return null;
        }

        /// <summary>
        /// Generates a conversational response to user input.
        /// </summary>
        /// <param name="userInput">The user's message.</param>
        /// <param name="conversationHistory">Previous messages in the conversation.</param>
        /// <param name="systemPrompt">Optional system prompt for context.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Generated response, or null if unavailable.</returns>
        protected async Task<ConversationResponse?> GenerateConversationResponseAsync(
            string userInput,
            ConversationMessage[]? conversationHistory = null,
            string? systemPrompt = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.Conversation))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["userInput"] = userInput,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N"),
                ["sessionId"] = context?.SessionId ?? Guid.NewGuid().ToString("N")
            };

            if (conversationHistory != null)
                payload["history"] = conversationHistory;
            if (systemPrompt != null)
                payload["systemPrompt"] = systemPrompt;
            if (context?.MaxTokens != null)
                payload["maxTokens"] = context.MaxTokens.Value;
            if (context?.Temperature != null)
                payload["temperature"] = context.Temperature.Value;

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestConversation,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new ConversationResponse
                {
                    Response = result.TryGetValue("response", out var r) && r is string resp ? resp : string.Empty,
                    Intent = result.TryGetValue("detectedIntent", out var i) && i is string intent ? intent : null,
                    Entities = result.TryGetValue("entities", out var e) && e is Dictionary<string, object> entities ? entities : null,
                    SuggestedActions = result.TryGetValue("suggestedActions", out var sa) && sa is string[] actions ? actions : null,
                    Confidence = result.TryGetValue("confidence", out var c) && c is double conf ? conf : 1.0
                };
            }

            return null;
        }

        /// <summary>
        /// Detects the language of input text.
        /// </summary>
        /// <param name="text">The text to analyze.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Detected language code, or null if unavailable.</returns>
        protected async Task<LanguageDetectionResult?> DetectLanguageAsync(
            string text,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.LanguageDetection))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["text"] = text,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            var response = await SendIntelligenceRequestAsync(
                "intelligence.request.language-detection",
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new LanguageDetectionResult
                {
                    LanguageCode = result.TryGetValue("languageCode", out var lc) && lc is string code ? code : "unknown",
                    LanguageName = result.TryGetValue("languageName", out var ln) && ln is string name ? name : "Unknown",
                    Confidence = result.TryGetValue("confidence", out var c) && c is double conf ? conf : 0.0,
                    Alternatives = result.TryGetValue("alternatives", out var a) && a is LanguageAlternative[] alts ? alts : Array.Empty<LanguageAlternative>()
                };
            }

            return null;
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["InterfaceType"] = "IntelligenceAware";
            metadata["InterfaceProtocol"] = InterfaceProtocol;
            if (Port.HasValue) metadata["Port"] = Port.Value;
            metadata["SupportsNLP"] = HasCapability(IntelligenceCapabilities.NLP);
            metadata["SupportsConversation"] = HasCapability(IntelligenceCapabilities.Conversation);
            metadata["SupportsIntentRecognition"] = HasCapability(IntelligenceCapabilities.IntentRecognition);
            return metadata;
        }
    }

    #endregion

    #region Encryption Plugin Base

    /// <summary>
    /// Intelligence-aware base class for encryption plugins.
    /// Provides hooks for cipher recommendation and threat assessment.
    /// </summary>
    public abstract class IntelligenceAwareEncryptionPluginBase : IntelligenceAwarePluginBase
    {
        /// <summary>
        /// Gets the primary encryption algorithm.
        /// </summary>
        public abstract string Algorithm { get; }

        /// <summary>
        /// Gets a cipher recommendation based on content analysis.
        /// </summary>
        /// <param name="contentType">Type of content being encrypted.</param>
        /// <param name="contentSize">Size of content in bytes.</param>
        /// <param name="securityRequirements">Optional security requirements.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Cipher recommendation, or null if unavailable.</returns>
        protected async Task<CipherRecommendation?> GetCipherRecommendationAsync(
            string contentType,
            long contentSize,
            SecurityRequirements? securityRequirements = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.CipherRecommendation))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["contentType"] = contentType,
                ["contentSize"] = contentSize,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (securityRequirements != null)
            {
                payload["securityLevel"] = securityRequirements.Level.ToString();
                payload["complianceRequired"] = securityRequirements.ComplianceFrameworks;
            }

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestCipherRecommendation,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new CipherRecommendation
                {
                    RecommendedAlgorithm = result.TryGetValue("algorithm", out var a) && a is string algo ? algo : Algorithm,
                    KeySize = result.TryGetValue("keySize", out var ks) && ks is int keySize ? keySize : 256,
                    Mode = result.TryGetValue("mode", out var m) && m is string mode ? mode : "GCM",
                    Reasoning = result.TryGetValue("reasoning", out var r) && r is string reason ? reason : null,
                    Confidence = result.TryGetValue("confidence", out var c) && c is double conf ? conf : 0.5,
                    PerformanceImpact = result.TryGetValue("performanceImpact", out var p) && p is string perf ? perf : "Medium"
                };
            }

            return null;
        }

        /// <summary>
        /// Assesses threats for encrypted data.
        /// </summary>
        /// <param name="metadata">Metadata about the encrypted content.</param>
        /// <param name="accessPattern">Recent access pattern information.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Threat assessment result, or null if unavailable.</returns>
        protected async Task<ThreatAssessmentResult?> AssessThreatsAsync(
            Dictionary<string, object> metadata,
            AccessPatternInfo? accessPattern = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.ThreatAssessment))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["metadata"] = metadata,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (accessPattern != null)
                payload["accessPattern"] = accessPattern;

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestThreatAssessment,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new ThreatAssessmentResult
                {
                    ThreatLevel = result.TryGetValue("threatLevel", out var tl) && tl is string level ? level : "Low",
                    RiskScore = result.TryGetValue("riskScore", out var rs) && rs is double score ? score : 0.0,
                    Threats = result.TryGetValue("threats", out var t) && t is ThreatInfo[] threats ? threats : Array.Empty<ThreatInfo>(),
                    Recommendations = result.TryGetValue("recommendations", out var r) && r is string[] recs ? recs : Array.Empty<string>()
                };
            }

            return null;
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["EncryptionType"] = "IntelligenceAware";
            metadata["Algorithm"] = Algorithm;
            metadata["SupportsCipherRecommendation"] = HasCapability(IntelligenceCapabilities.CipherRecommendation);
            metadata["SupportsThreatAssessment"] = HasCapability(IntelligenceCapabilities.ThreatAssessment);
            return metadata;
        }
    }

    #endregion

    #region Compression Plugin Base

    /// <summary>
    /// Intelligence-aware base class for compression plugins.
    /// Provides hooks for compression algorithm recommendation based on content analysis.
    /// </summary>
    public abstract class IntelligenceAwareCompressionPluginBase : IntelligenceAwarePluginBase
    {
        /// <summary>
        /// Gets the primary compression algorithm.
        /// </summary>
        public abstract string CompressionAlgorithm { get; }

        /// <summary>
        /// Gets a compression recommendation based on content analysis.
        /// </summary>
        /// <param name="contentType">Type of content being compressed.</param>
        /// <param name="contentSize">Size of content in bytes.</param>
        /// <param name="sampleData">Optional sample of the content for analysis.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Compression recommendation, or null if unavailable.</returns>
        protected async Task<CompressionRecommendation?> GetCompressionRecommendationAsync(
            string contentType,
            long contentSize,
            byte[]? sampleData = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.CompressionRecommendation))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["contentType"] = contentType,
                ["contentSize"] = contentSize,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (sampleData != null)
                payload["sampleHash"] = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(sampleData));

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestCompressionRecommendation,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new CompressionRecommendation
                {
                    RecommendedAlgorithm = result.TryGetValue("algorithm", out var a) && a is string algo ? algo : CompressionAlgorithm,
                    CompressionLevel = result.TryGetValue("compressionLevel", out var cl) && cl is int level ? level : 6,
                    EstimatedRatio = result.TryGetValue("estimatedRatio", out var er) && er is double ratio ? ratio : 1.0,
                    Reasoning = result.TryGetValue("reasoning", out var r) && r is string reason ? reason : null,
                    IsAlreadyCompressed = result.TryGetValue("isAlreadyCompressed", out var ac) && ac is true,
                    ShouldSkipCompression = result.TryGetValue("shouldSkip", out var ss) && ss is true
                };
            }

            return null;
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["CompressionType"] = "IntelligenceAware";
            metadata["CompressionAlgorithm"] = CompressionAlgorithm;
            metadata["SupportsCompressionRecommendation"] = HasCapability(IntelligenceCapabilities.CompressionRecommendation);
            return metadata;
        }
    }

    #endregion

    #region Storage Plugin Base

    /// <summary>
    /// Intelligence-aware base class for storage plugins.
    /// Provides hooks for tiering prediction and access pattern analysis.
    /// </summary>
    public abstract class IntelligenceAwareStoragePluginBase : IntelligenceAwarePluginBase
    {
        /// <summary>
        /// Gets the storage scheme (e.g., "file", "s3", "azure").
        /// </summary>
        public abstract string StorageScheme { get; }

        /// <summary>
        /// Gets a storage tier recommendation based on access patterns.
        /// </summary>
        /// <param name="objectId">The object identifier.</param>
        /// <param name="accessHistory">Recent access history.</param>
        /// <param name="objectMetadata">Object metadata.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Tiering recommendation, or null if unavailable.</returns>
        protected async Task<TieringRecommendation?> GetTieringRecommendationAsync(
            string objectId,
            AccessHistoryEntry[]? accessHistory = null,
            Dictionary<string, object>? objectMetadata = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.TieringRecommendation))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["objectId"] = objectId,
                ["storageScheme"] = StorageScheme,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (accessHistory != null)
                payload["accessHistory"] = accessHistory;
            if (objectMetadata != null)
                payload["metadata"] = objectMetadata;

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestTieringRecommendation,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new TieringRecommendation
                {
                    RecommendedTier = result.TryGetValue("recommendedTier", out var rt) && rt is string tier
                        ? Enum.TryParse<StorageTier>(tier, true, out var st) ? st : StorageTier.Hot
                        : StorageTier.Hot,
                    CurrentTier = result.TryGetValue("currentTier", out var ct2) && ct2 is string current
                        ? Enum.TryParse<StorageTier>(current, true, out var cst) ? cst : StorageTier.Hot
                        : StorageTier.Hot,
                    Confidence = result.TryGetValue("confidence", out var c) && c is double conf ? conf : 0.5,
                    PredictedAccessFrequency = result.TryGetValue("predictedAccessFrequency", out var paf) && paf is string freq ? freq : "Unknown",
                    EstimatedCostSavings = result.TryGetValue("estimatedCostSavings", out var ecs) && ecs is decimal savings ? savings : 0m,
                    Reasoning = result.TryGetValue("reasoning", out var r) && r is string reason ? reason : null
                };
            }

            return null;
        }

        /// <summary>
        /// Predicts future access patterns for an object.
        /// </summary>
        /// <param name="objectId">The object identifier.</param>
        /// <param name="accessHistory">Recent access history.</param>
        /// <param name="predictionHorizon">How far ahead to predict.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Access prediction, or null if unavailable.</returns>
        protected async Task<AccessPatternPrediction?> PredictAccessPatternAsync(
            string objectId,
            AccessHistoryEntry[]? accessHistory = null,
            TimeSpan? predictionHorizon = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.AccessPatternPrediction))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["objectId"] = objectId,
                ["predictionHorizon"] = (predictionHorizon ?? TimeSpan.FromDays(30)).TotalHours,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (accessHistory != null)
                payload["accessHistory"] = accessHistory;

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestAccessPrediction,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new AccessPatternPrediction
                {
                    PredictedAccessCount = result.TryGetValue("predictedAccessCount", out var pac) && pac is int count ? count : 0,
                    AccessProbability = result.TryGetValue("accessProbability", out var ap) && ap is double prob ? prob : 0.0,
                    PeakAccessTimes = result.TryGetValue("peakAccessTimes", out var pat) && pat is string[] times ? times : Array.Empty<string>(),
                    Confidence = result.TryGetValue("confidence", out var c) && c is double conf ? conf : 0.5,
                    PatternType = result.TryGetValue("patternType", out var pt) && pt is string pattern ? pattern : "Unknown"
                };
            }

            return null;
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["StorageType"] = "IntelligenceAware";
            metadata["StorageScheme"] = StorageScheme;
            metadata["SupportsTieringRecommendation"] = HasCapability(IntelligenceCapabilities.TieringRecommendation);
            metadata["SupportsAccessPrediction"] = HasCapability(IntelligenceCapabilities.AccessPatternPrediction);
            return metadata;
        }
    }

    #endregion

    #region Access Control Plugin Base

    /// <summary>
    /// Intelligence-aware base class for access control plugins.
    /// Provides hooks for UEBA (User and Entity Behavior Analytics) and anomaly detection.
    /// </summary>
    public abstract class IntelligenceAwareAccessControlPluginBase : IntelligenceAwarePluginBase
    {
        /// <summary>
        /// Analyzes user behavior for anomalies.
        /// </summary>
        /// <param name="userId">The user identifier.</param>
        /// <param name="recentActions">Recent user actions.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Behavior analysis result, or null if unavailable.</returns>
        protected async Task<BehaviorAnalysisResult?> AnalyzeUserBehaviorAsync(
            string userId,
            UserAction[]? recentActions = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.BehaviorAnalytics))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["userId"] = userId,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (recentActions != null)
                payload["recentActions"] = recentActions;

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestBehaviorAnalysis,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new BehaviorAnalysisResult
                {
                    RiskScore = result.TryGetValue("riskScore", out var rs) && rs is double score ? score : 0.0,
                    IsAnomaly = result.TryGetValue("isAnomaly", out var ia) && ia is true,
                    AnomalyTypes = result.TryGetValue("anomalyTypes", out var at) && at is string[] types ? types : Array.Empty<string>(),
                    Recommendations = result.TryGetValue("recommendations", out var r) && r is string[] recs ? recs : Array.Empty<string>(),
                    BehaviorProfile = result.TryGetValue("behaviorProfile", out var bp) && bp is Dictionary<string, object> profile ? profile : null
                };
            }

            return null;
        }

        /// <summary>
        /// Gets access control recommendations based on content sensitivity.
        /// </summary>
        /// <param name="resourceId">The resource identifier.</param>
        /// <param name="contentClassification">Classification of the content.</param>
        /// <param name="currentPermissions">Current permission settings.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Access control recommendations, or null if unavailable.</returns>
        protected async Task<AccessControlRecommendation?> GetAccessRecommendationsAsync(
            string resourceId,
            string? contentClassification = null,
            Dictionary<string, object>? currentPermissions = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.AccessControlRecommendation))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["resourceId"] = resourceId,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (contentClassification != null)
                payload["classification"] = contentClassification;
            if (currentPermissions != null)
                payload["currentPermissions"] = currentPermissions;

            var response = await SendIntelligenceRequestAsync(
                "intelligence.request.access-recommendation",
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new AccessControlRecommendation
                {
                    RecommendedPermissions = result.TryGetValue("recommendedPermissions", out var rp) && rp is Dictionary<string, object> perms ? perms : new Dictionary<string, object>(),
                    Changes = result.TryGetValue("changes", out var c) && c is PermissionChange[] changes ? changes : Array.Empty<PermissionChange>(),
                    Reasoning = result.TryGetValue("reasoning", out var r) && r is string reason ? reason : null,
                    ComplianceAlignment = result.TryGetValue("complianceAlignment", out var ca) && ca is string[] compliance ? compliance : Array.Empty<string>()
                };
            }

            return null;
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["AccessControlType"] = "IntelligenceAware";
            metadata["SupportsBehaviorAnalytics"] = HasCapability(IntelligenceCapabilities.BehaviorAnalytics);
            metadata["SupportsAccessRecommendation"] = HasCapability(IntelligenceCapabilities.AccessControlRecommendation);
            return metadata;
        }
    }

    #endregion

    #region Compliance Plugin Base

    /// <summary>
    /// Intelligence-aware base class for compliance plugins.
    /// Provides hooks for PII detection, compliance classification, and sensitivity assessment.
    /// </summary>
    public abstract class IntelligenceAwareCompliancePluginBase : IntelligenceAwarePluginBase
    {
        /// <summary>
        /// Gets compliance classification for content.
        /// </summary>
        /// <param name="content">The content to classify.</param>
        /// <param name="applicableFrameworks">Compliance frameworks to check (e.g., "GDPR", "HIPAA").</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Compliance classification, or null if unavailable.</returns>
        protected async Task<ComplianceClassificationResult?> ClassifyForComplianceAsync(
            string content,
            string[]? applicableFrameworks = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.ComplianceClassification))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["content"] = content,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (applicableFrameworks != null)
                payload["frameworks"] = applicableFrameworks;

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestComplianceClassification,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new ComplianceClassificationResult
                {
                    ApplicableFrameworks = result.TryGetValue("applicableFrameworks", out var af) && af is string[] frameworks ? frameworks : Array.Empty<string>(),
                    SensitivityLevel = result.TryGetValue("sensitivityLevel", out var sl) && sl is string level ? level : "Unknown",
                    Classifications = result.TryGetValue("classifications", out var c) && c is ComplianceTag[] tags ? tags : Array.Empty<ComplianceTag>(),
                    RetentionRequirements = result.TryGetValue("retentionRequirements", out var rr) && rr is RetentionRequirement[] reqs ? reqs : null,
                    Recommendations = result.TryGetValue("recommendations", out var r) && r is string[] recs ? recs : Array.Empty<string>()
                };
            }

            return null;
        }

        /// <summary>
        /// Gets data sensitivity classification.
        /// </summary>
        /// <param name="content">The content to classify.</param>
        /// <param name="contentType">Type of content.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Sensitivity classification, or null if unavailable.</returns>
        protected async Task<SensitivityClassificationResult?> ClassifySensitivityAsync(
            string content,
            string? contentType = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.SensitivityClassification))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["content"] = content,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (contentType != null)
                payload["contentType"] = contentType;

            var response = await SendIntelligenceRequestAsync(
                "intelligence.request.sensitivity-classification",
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new SensitivityClassificationResult
                {
                    SensitivityLevel = result.TryGetValue("sensitivityLevel", out var sl) && sl is string level ? level : "Public",
                    Confidence = result.TryGetValue("confidence", out var c) && c is double conf ? conf : 0.5,
                    Indicators = result.TryGetValue("indicators", out var i) && i is string[] indicators ? indicators : Array.Empty<string>(),
                    HandlingInstructions = result.TryGetValue("handlingInstructions", out var hi) && hi is string instructions ? instructions : null
                };
            }

            return null;
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["ComplianceType"] = "IntelligenceAware";
            metadata["SupportsPIIDetection"] = HasCapability(IntelligenceCapabilities.PIIDetection);
            metadata["SupportsComplianceClassification"] = HasCapability(IntelligenceCapabilities.ComplianceClassification);
            metadata["SupportsSensitivityClassification"] = HasCapability(IntelligenceCapabilities.SensitivityClassification);
            return metadata;
        }
    }

    #endregion

    #region Data Management Plugin Base

    /// <summary>
    /// Intelligence-aware base class for data management plugins.
    /// Provides hooks for semantic deduplication and lifecycle prediction.
    /// </summary>
    public abstract class IntelligenceAwareDataManagementPluginBase : IntelligenceAwarePluginBase
    {
        /// <summary>
        /// Checks for semantic duplicates of content.
        /// </summary>
        /// <param name="contentId">The content identifier.</param>
        /// <param name="contentText">Text content or description.</param>
        /// <param name="embedding">Optional pre-computed embedding.</param>
        /// <param name="threshold">Similarity threshold (0.0-1.0).</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Deduplication result, or null if unavailable.</returns>
        protected async Task<SemanticDeduplicationResult?> CheckSemanticDuplicatesAsync(
            string contentId,
            string contentText,
            float[]? embedding = null,
            double threshold = 0.9,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.SemanticDeduplication))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["contentId"] = contentId,
                ["contentText"] = contentText,
                ["threshold"] = threshold,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (embedding != null)
                payload["embedding"] = embedding;

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestSemanticDedup,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new SemanticDeduplicationResult
                {
                    HasDuplicates = result.TryGetValue("hasDuplicates", out var hd) && hd is true,
                    Duplicates = result.TryGetValue("duplicates", out var d) && d is DuplicateMatch[] matches ? matches : Array.Empty<DuplicateMatch>(),
                    RecommendedAction = result.TryGetValue("recommendedAction", out var ra) && ra is string action ? action : "Keep"
                };
            }

            return null;
        }

        /// <summary>
        /// Predicts optimal lifecycle transitions for data.
        /// </summary>
        /// <param name="objectId">The object identifier.</param>
        /// <param name="metadata">Object metadata.</param>
        /// <param name="accessHistory">Recent access history.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Lifecycle prediction, or null if unavailable.</returns>
        protected async Task<LifecyclePrediction?> PredictLifecycleAsync(
            string objectId,
            Dictionary<string, object>? metadata = null,
            AccessHistoryEntry[]? accessHistory = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.DataLifecyclePrediction))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["objectId"] = objectId,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (metadata != null)
                payload["metadata"] = metadata;
            if (accessHistory != null)
                payload["accessHistory"] = accessHistory;

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestLifecyclePrediction,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new LifecyclePrediction
                {
                    RecommendedTransition = result.TryGetValue("recommendedTransition", out var rt) && rt is string transition ? transition : "None",
                    TransitionDate = result.TryGetValue("transitionDate", out var td) && td is string date && DateTimeOffset.TryParse(date, out var dto) ? dto : null,
                    RetentionPeriod = result.TryGetValue("retentionPeriod", out var rp) && rp is string period ? TimeSpan.TryParse(period, out var ts) ? ts : null : null,
                    Confidence = result.TryGetValue("confidence", out var c) && c is double conf ? conf : 0.5,
                    Reasoning = result.TryGetValue("reasoning", out var r) && r is string reason ? reason : null
                };
            }

            return null;
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["DataManagementType"] = "IntelligenceAware";
            metadata["SupportsSemanticDeduplication"] = HasCapability(IntelligenceCapabilities.SemanticDeduplication);
            metadata["SupportsLifecyclePrediction"] = HasCapability(IntelligenceCapabilities.DataLifecyclePrediction);
            return metadata;
        }
    }

    #endregion

    // ================================================================================
    // SUPPORTING TYPES
    // ================================================================================

    #region Connector Types

    /// <summary>Response enrichment from connector transformation.</summary>
    public sealed class ConnectorResponseEnrichment
    {
        public Dictionary<string, object>? OriginalResponse { get; init; }
        public Dictionary<string, object>? EnrichedResponse { get; init; }
        public Dictionary<string, object>? Metadata { get; init; }
        public ExtractedEntity[]? Entities { get; init; }
        public ClassificationResult[]? Classifications { get; init; }
    }

    /// <summary>Schema enrichment result.</summary>
    public sealed class SchemaEnrichmentResult
    {
        public Dictionary<string, object>? OriginalSchema { get; init; }
        public Dictionary<string, object>? EnrichedSchema { get; init; }
        public Dictionary<string, string>? FieldDescriptions { get; init; }
        public RelationshipSuggestion[]? RelationshipSuggestions { get; init; }
        public Dictionary<string, string>? PIISensitivity { get; init; }
    }

    /// <summary>Suggested relationship between schema elements.</summary>
    public sealed class RelationshipSuggestion
    {
        public string SourceField { get; init; } = string.Empty;
        public string TargetField { get; init; } = string.Empty;
        public string RelationshipType { get; init; } = string.Empty;
        public double Confidence { get; init; }
    }

    /// <summary>Query optimization result.</summary>
    public sealed class QueryOptimizationResult
    {
        public string OriginalQuery { get; init; } = string.Empty;
        public string OptimizedQuery { get; init; } = string.Empty;
        public string[] Recommendations { get; init; } = Array.Empty<string>();
        public double EstimatedImprovement { get; init; }
    }

    #endregion

    #region Interface Types

    /// <summary>Result of intent parsing.</summary>
    public sealed class IntentParseResult
    {
        public string? Intent { get; init; }
        public double Confidence { get; init; }
        public Dictionary<string, object> Entities { get; init; } = new();
        public IntentAlternative[] AlternativeIntents { get; init; } = Array.Empty<IntentAlternative>();
    }

    /// <summary>Alternative intent suggestion.</summary>
    public sealed class IntentAlternative
    {
        public string Intent { get; init; } = string.Empty;
        public double Confidence { get; init; }
    }

    /// <summary>Conversation response.</summary>
    public sealed class ConversationResponse
    {
        public string Response { get; init; } = string.Empty;
        public string? Intent { get; init; }
        public Dictionary<string, object>? Entities { get; init; }
        public string[]? SuggestedActions { get; init; }
        public double Confidence { get; init; }
    }

    /// <summary>Conversation message for history.</summary>
    public sealed class ConversationMessage
    {
        public string Role { get; init; } = "user";
        public string Content { get; init; } = string.Empty;
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    }

    /// <summary>Language detection result.</summary>
    public sealed class LanguageDetectionResult
    {
        public string LanguageCode { get; init; } = "unknown";
        public string LanguageName { get; init; } = "Unknown";
        public double Confidence { get; init; }
        public LanguageAlternative[] Alternatives { get; init; } = Array.Empty<LanguageAlternative>();
    }

    /// <summary>Alternative language suggestion.</summary>
    public sealed class LanguageAlternative
    {
        public string LanguageCode { get; init; } = string.Empty;
        public double Confidence { get; init; }
    }

    #endregion

    #region Encryption Types

    /// <summary>Security requirements for cipher recommendation.</summary>
    public sealed class SecurityRequirements
    {
        public SecurityLevel Level { get; init; } = SecurityLevel.Standard;
        public string[] ComplianceFrameworks { get; init; } = Array.Empty<string>();
    }

    /// <summary>Cipher recommendation result.</summary>
    public sealed class CipherRecommendation
    {
        public string RecommendedAlgorithm { get; init; } = string.Empty;
        public int KeySize { get; init; }
        public string Mode { get; init; } = string.Empty;
        public string? Reasoning { get; init; }
        public double Confidence { get; init; }
        public string PerformanceImpact { get; init; } = "Medium";
    }

    /// <summary>Access pattern information.</summary>
    public sealed class AccessPatternInfo
    {
        public int RecentAccessCount { get; init; }
        public string[] AccessTypes { get; init; } = Array.Empty<string>();
        public string[] SourceIPs { get; init; } = Array.Empty<string>();
    }

    /// <summary>Threat assessment result.</summary>
    public sealed class ThreatAssessmentResult
    {
        public string ThreatLevel { get; init; } = "Low";
        public double RiskScore { get; init; }
        public ThreatInfo[] Threats { get; init; } = Array.Empty<ThreatInfo>();
        public string[] Recommendations { get; init; } = Array.Empty<string>();
    }

    /// <summary>Information about a detected threat.</summary>
    public sealed class ThreatInfo
    {
        public string Type { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public double Severity { get; init; }
    }

    #endregion

    #region Compression Types

    /// <summary>Compression recommendation result.</summary>
    public sealed class CompressionRecommendation
    {
        public string RecommendedAlgorithm { get; init; } = string.Empty;
        public int CompressionLevel { get; init; }
        public double EstimatedRatio { get; init; }
        public string? Reasoning { get; init; }
        public bool IsAlreadyCompressed { get; init; }
        public bool ShouldSkipCompression { get; init; }
    }

    #endregion

    #region Storage Types

    /// <summary>Access history entry.</summary>
    public sealed class AccessHistoryEntry
    {
        public DateTimeOffset Timestamp { get; init; }
        public string AccessType { get; init; } = "Read";
        public string? UserId { get; init; }
    }

    /// <summary>Tiering recommendation result.</summary>
    public sealed class TieringRecommendation
    {
        public StorageTier RecommendedTier { get; init; }
        public StorageTier CurrentTier { get; init; }
        public double Confidence { get; init; }
        public string PredictedAccessFrequency { get; init; } = "Unknown";
        public decimal EstimatedCostSavings { get; init; }
        public string? Reasoning { get; init; }
    }

    /// <summary>Access pattern prediction.</summary>
    public sealed class AccessPatternPrediction
    {
        public int PredictedAccessCount { get; init; }
        public double AccessProbability { get; init; }
        public string[] PeakAccessTimes { get; init; } = Array.Empty<string>();
        public double Confidence { get; init; }
        public string PatternType { get; init; } = "Unknown";
    }

    #endregion

    #region Access Control Types

    /// <summary>User action for behavior analysis.</summary>
    public sealed class UserAction
    {
        public DateTimeOffset Timestamp { get; init; }
        public string ActionType { get; init; } = string.Empty;
        public string ResourceId { get; init; } = string.Empty;
        public string? SourceIP { get; init; }
    }

    /// <summary>Behavior analysis result.</summary>
    public sealed class BehaviorAnalysisResult
    {
        public double RiskScore { get; init; }
        public bool IsAnomaly { get; init; }
        public string[] AnomalyTypes { get; init; } = Array.Empty<string>();
        public string[] Recommendations { get; init; } = Array.Empty<string>();
        public Dictionary<string, object>? BehaviorProfile { get; init; }
    }

    /// <summary>Access control recommendation.</summary>
    public sealed class AccessControlRecommendation
    {
        public Dictionary<string, object> RecommendedPermissions { get; init; } = new();
        public PermissionChange[] Changes { get; init; } = Array.Empty<PermissionChange>();
        public string? Reasoning { get; init; }
        public string[] ComplianceAlignment { get; init; } = Array.Empty<string>();
    }

    /// <summary>Permission change suggestion.</summary>
    public sealed class PermissionChange
    {
        public string Principal { get; init; } = string.Empty;
        public string Permission { get; init; } = string.Empty;
        public string ChangeType { get; init; } = string.Empty; // "Grant", "Revoke", "Modify"
        public string? Reason { get; init; }
    }

    #endregion

    #region Compliance Types

    /// <summary>Compliance classification result.</summary>
    public sealed class ComplianceClassificationResult
    {
        public string[] ApplicableFrameworks { get; init; } = Array.Empty<string>();
        public string SensitivityLevel { get; init; } = "Unknown";
        public ComplianceTag[] Classifications { get; init; } = Array.Empty<ComplianceTag>();
        public RetentionRequirement[]? RetentionRequirements { get; init; }
        public string[] Recommendations { get; init; } = Array.Empty<string>();
    }

    /// <summary>Compliance tag.</summary>
    public sealed class ComplianceTag
    {
        public string Framework { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public double Confidence { get; init; }
    }

    /// <summary>Retention requirement.</summary>
    public sealed class RetentionRequirement
    {
        public string Framework { get; init; } = string.Empty;
        public TimeSpan MinimumRetention { get; init; }
        public string? Reason { get; init; }
    }

    /// <summary>Sensitivity classification result.</summary>
    public sealed class SensitivityClassificationResult
    {
        public string SensitivityLevel { get; init; } = "Public";
        public double Confidence { get; init; }
        public string[] Indicators { get; init; } = Array.Empty<string>();
        public string? HandlingInstructions { get; init; }
    }

    #endregion

    #region Data Management Types

    /// <summary>Semantic deduplication result.</summary>
    public sealed class SemanticDeduplicationResult
    {
        public bool HasDuplicates { get; init; }
        public DuplicateMatch[] Duplicates { get; init; } = Array.Empty<DuplicateMatch>();
        public string RecommendedAction { get; init; } = "Keep";
    }

    /// <summary>Duplicate match information.</summary>
    public sealed class DuplicateMatch
    {
        public string ContentId { get; init; } = string.Empty;
        public double Similarity { get; init; }
        public string? Reason { get; init; }
    }

    /// <summary>Lifecycle prediction result.</summary>
    public sealed class LifecyclePrediction
    {
        public string RecommendedTransition { get; init; } = "None";
        public DateTimeOffset? TransitionDate { get; init; }
        public TimeSpan? RetentionPeriod { get; init; }
        public double Confidence { get; init; }
        public string? Reasoning { get; init; }
    }

    #endregion
}
