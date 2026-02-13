using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

// Explicit alias to resolve ambiguity with other StorageTier definitions in the SDK
using StorageTier = DataWarehouse.SDK.Primitives.StorageTier;

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
    [Obsolete("Use InterfacePluginBase from DataWarehouse.SDK.Contracts.Hierarchy namespace. This class will be removed in Phase 28.")]
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
    [Obsolete("Use InterfacePluginBase from DataWarehouse.SDK.Contracts.Hierarchy namespace. This class will be removed in Phase 28.")]
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
    [Obsolete("Use EncryptionPluginBase from DataWarehouse.SDK.Contracts.Hierarchy namespace. This class will be removed in Phase 28.")]
    public abstract class IntelligenceAwareEncryptionPluginBase : IntelligenceAwarePluginBase
    {
        /// <summary>
        /// Gets the primary encryption algorithm.
        /// </summary>
        public abstract string Algorithm { get; }

        /// <summary>
        /// Gets the plugin category.
        /// </summary>
        public override PluginCategory Category => PluginCategory.SecurityProvider;

        /// <summary>
        /// Gets the plugin sub-category.
        /// </summary>
        public virtual string SubCategory => "Encryption";

        /// <summary>
        /// Gets the quality level of this plugin (0-100).
        /// </summary>
        public virtual int QualityLevel => 50;

        /// <summary>
        /// Gets the default execution order in the pipeline.
        /// </summary>
        public virtual int DefaultOrder => 90;

        /// <summary>
        /// Gets whether this stage can be bypassed.
        /// </summary>
        public virtual bool AllowBypass => false;

        /// <summary>
        /// Gets stages that must precede this one.
        /// </summary>
        public virtual string[] RequiredPrecedingStages => Array.Empty<string>();

        /// <summary>
        /// Gets stages that are incompatible with this one.
        /// </summary>
        public virtual string[] IncompatibleStages => Array.Empty<string>();

        /// <summary>
        /// Transform data during write operations (e.g., encrypt).
        /// </summary>
        public virtual Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            return OnWriteAsync(input, context, args).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Transform data during read operations (e.g., decrypt).
        /// </summary>
        public virtual Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            return OnReadAsync(stored, context, args).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Async version of OnWrite. Override this for proper async support.
        /// </summary>
        protected virtual Task<Stream> OnWriteAsync(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            return Task.FromResult(input);
        }

        /// <summary>
        /// Async version of OnRead. Override this for proper async support.
        /// </summary>
        protected virtual Task<Stream> OnReadAsync(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            return Task.FromResult(stored);
        }

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

        /// <summary>
        /// Detects anomalous encryption patterns that may indicate security issues.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This hook analyzes encryption operations for anomalies such as:
        /// </para>
        /// <list type="bullet">
        ///   <item>Unusual encryption frequency or volume</item>
        ///   <item>Atypical cipher usage patterns</item>
        ///   <item>Potential key reuse issues</item>
        ///   <item>Suspicious timing or sequencing</item>
        /// </list>
        /// </remarks>
        /// <param name="operationMetrics">Metrics about the encryption operation.</param>
        /// <param name="historicalPatterns">Optional historical pattern data for comparison.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Anomaly detection result with findings and recommendations, or null if unavailable.</returns>
        protected async Task<EncryptionAnomalyResult?> OnAnomalyDetectionAsync(
            EncryptionOperationMetrics operationMetrics,
            EncryptionPatternHistory? historicalPatterns = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.AnomalyDetection))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["operationMetrics"] = new Dictionary<string, object>
                {
                    ["algorithm"] = operationMetrics.Algorithm,
                    ["keySize"] = operationMetrics.KeySize,
                    ["dataSize"] = operationMetrics.DataSize,
                    ["operationType"] = operationMetrics.OperationType,
                    ["timestamp"] = operationMetrics.Timestamp.ToString("O"),
                    ["duration"] = operationMetrics.Duration.TotalMilliseconds,
                    ["sourceId"] = operationMetrics.SourceId ?? string.Empty
                },
                ["pluginId"] = Id,
                ["algorithm"] = Algorithm,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (historicalPatterns != null)
            {
                payload["historicalPatterns"] = new Dictionary<string, object>
                {
                    ["averageOperationsPerHour"] = historicalPatterns.AverageOperationsPerHour,
                    ["typicalDataSizeRange"] = new[] { historicalPatterns.TypicalMinDataSize, historicalPatterns.TypicalMaxDataSize },
                    ["commonAlgorithms"] = historicalPatterns.CommonAlgorithms,
                    ["baselineEstablishedAt"] = historicalPatterns.BaselineEstablishedAt.ToString("O")
                };
            }

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestAnomalyDetection,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new EncryptionAnomalyResult
                {
                    IsAnomaly = result.TryGetValue("isAnomaly", out var ia) && ia is true,
                    AnomalyScore = result.TryGetValue("anomalyScore", out var ascore) && ascore is double score ? score : 0.0,
                    AnomalyType = result.TryGetValue("anomalyType", out var atype) && atype is string anomalyType ? anomalyType : null,
                    Description = result.TryGetValue("description", out var desc) && desc is string description ? description : null,
                    Severity = result.TryGetValue("severity", out var sev) && sev is string severity ? severity : "Low",
                    Recommendations = result.TryGetValue("recommendations", out var recs) && recs is string[] recommendations ? recommendations : Array.Empty<string>(),
                    ShouldAlert = result.TryGetValue("shouldAlert", out var alert) && alert is true,
                    ShouldBlock = result.TryGetValue("shouldBlock", out var block) && block is true
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
            metadata["SupportsAnomalyDetection"] = HasCapability(IntelligenceCapabilities.AnomalyDetection);
            return metadata;
        }
    }

    #endregion

    #region Compression Plugin Base

    /// <summary>
    /// Intelligence-aware base class for compression plugins.
    /// Provides hooks for compression algorithm recommendation based on content analysis.
    /// </summary>
    [Obsolete("Use CompressionPluginBase from DataWarehouse.SDK.Contracts.Hierarchy namespace. This class will be removed in Phase 28.")]
    public abstract class IntelligenceAwareCompressionPluginBase : IntelligenceAwarePluginBase
    {
        /// <summary>
        /// Gets the primary compression algorithm.
        /// </summary>
        public abstract string CompressionAlgorithm { get; }

        /// <summary>
        /// Gets the plugin category.
        /// </summary>
        public override PluginCategory Category => PluginCategory.DataTransformationProvider;

        /// <summary>
        /// Gets the plugin sub-category.
        /// </summary>
        public virtual string SubCategory => "Compression";

        /// <summary>
        /// Gets the quality level of this plugin (0-100).
        /// </summary>
        public virtual int QualityLevel => 50;

        /// <summary>
        /// Gets the default execution order in the pipeline.
        /// </summary>
        public virtual int DefaultOrder => 50;

        /// <summary>
        /// Gets whether this stage can be bypassed.
        /// </summary>
        public virtual bool AllowBypass => false;

        /// <summary>
        /// Gets stages that must precede this one.
        /// </summary>
        public virtual string[] RequiredPrecedingStages => Array.Empty<string>();

        /// <summary>
        /// Gets stages that are incompatible with this one.
        /// </summary>
        public virtual string[] IncompatibleStages => Array.Empty<string>();

        /// <summary>
        /// Transform data during write operations (e.g., compress).
        /// </summary>
        public virtual Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            return OnWriteAsync(input, context, args).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Transform data during read operations (e.g., decompress).
        /// </summary>
        public virtual Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            return OnReadAsync(stored, context, args).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Async version of OnWrite. Override this for proper async support.
        /// </summary>
        protected virtual Task<Stream> OnWriteAsync(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            return Task.FromResult(input);
        }

        /// <summary>
        /// Async version of OnRead. Override this for proper async support.
        /// </summary>
        protected virtual Task<Stream> OnReadAsync(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            return Task.FromResult(stored);
        }

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

        /// <summary>
        /// Analyzes content semantically before compression to optimize strategy.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This hook enables AI-powered content analysis to:
        /// </para>
        /// <list type="bullet">
        ///   <item>Identify content type and structure for optimal compression</item>
        ///   <item>Detect already-compressed or incompressible content</item>
        ///   <item>Classify content sensitivity for compliance</item>
        ///   <item>Recommend pre-processing steps (e.g., delta encoding for similar content)</item>
        /// </list>
        /// </remarks>
        /// <param name="content">The content to analyze (or a representative sample).</param>
        /// <param name="contentMetadata">Optional metadata about the content.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Content analysis result with classification and recommendations, or null if unavailable.</returns>
        protected async Task<ContentAnalysisResult?> OnContentAnalysisAsync(
            byte[] content,
            ContentMetadata? contentMetadata = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.Classification))
                return null;

            // Use a sample for large content to reduce latency
            var sampleSize = Math.Min(content.Length, 8192);
            var sample = content.Length > sampleSize ? content.Take(sampleSize).ToArray() : content;

            var payload = new Dictionary<string, object>
            {
                ["sampleHash"] = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(sample)),
                ["contentSize"] = content.Length,
                ["sampleSize"] = sampleSize,
                ["pluginId"] = Id,
                ["algorithm"] = CompressionAlgorithm,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            // Add entropy estimation (helps detect already-compressed content)
            payload["entropyEstimate"] = EstimateEntropy(sample);

            if (contentMetadata != null)
            {
                payload["metadata"] = new Dictionary<string, object>
                {
                    ["fileName"] = contentMetadata.FileName ?? string.Empty,
                    ["mimeType"] = contentMetadata.MimeType ?? string.Empty,
                    ["createdAt"] = contentMetadata.CreatedAt?.ToString("O") ?? string.Empty,
                    ["tags"] = contentMetadata.Tags ?? Array.Empty<string>()
                };
            }

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestClassification,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new ContentAnalysisResult
                {
                    ContentType = result.TryGetValue("contentType", out var ct2) && ct2 is string contentType ? contentType : "unknown",
                    IsCompressible = result.TryGetValue("isCompressible", out var ic) ? ic is not false : true,
                    IsAlreadyCompressed = result.TryGetValue("isAlreadyCompressed", out var iac) && iac is true,
                    EstimatedCompressionRatio = result.TryGetValue("estimatedCompressionRatio", out var ecr) && ecr is double ratio ? ratio : 1.0,
                    RecommendedAlgorithm = result.TryGetValue("recommendedAlgorithm", out var ra) && ra is string algo ? algo : null,
                    RecommendedLevel = result.TryGetValue("recommendedLevel", out var rl) && rl is int level ? level : 6,
                    PreProcessingSteps = result.TryGetValue("preProcessingSteps", out var pps) && pps is string[] steps ? steps : Array.Empty<string>(),
                    SensitivityLevel = result.TryGetValue("sensitivityLevel", out var sl) && sl is string sensitivity ? sensitivity : "Normal",
                    Confidence = result.TryGetValue("confidence", out var conf) && conf is double confidence ? confidence : 0.5
                };
            }

            return null;
        }

        /// <summary>
        /// Estimates the entropy of a data sample (0.0 = no entropy, 8.0 = max entropy).
        /// High entropy typically indicates already-compressed or encrypted content.
        /// </summary>
        private static double EstimateEntropy(byte[] data)
        {
            if (data.Length == 0) return 0.0;

            var frequencies = new int[256];
            foreach (var b in data)
                frequencies[b]++;

            double entropy = 0.0;
            double length = data.Length;

            foreach (var freq in frequencies)
            {
                if (freq > 0)
                {
                    double probability = freq / length;
                    entropy -= probability * Math.Log2(probability);
                }
            }

            return entropy;
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["CompressionType"] = "IntelligenceAware";
            metadata["CompressionAlgorithm"] = CompressionAlgorithm;
            metadata["SupportsCompressionRecommendation"] = HasCapability(IntelligenceCapabilities.CompressionRecommendation);
            metadata["SupportsContentAnalysis"] = HasCapability(IntelligenceCapabilities.Classification);
            return metadata;
        }
    }

    #endregion

    #region Storage Plugin Base

    /// <summary>
    /// Intelligence-aware base class for storage plugins.
    /// Provides hooks for tiering prediction and access pattern analysis.
    /// </summary>
    [Obsolete("Use StoragePluginBase from DataWarehouse.SDK.Contracts.Hierarchy namespace. This class will be removed in Phase 28.")]
    public abstract class IntelligenceAwareStoragePluginBase : IntelligenceAwarePluginBase
    {
        /// <summary>
        /// Gets the storage scheme (e.g., "file", "s3", "azure").
        /// </summary>
        public abstract string StorageScheme { get; }

        /// <summary>
        /// Gets the plugin category.
        /// </summary>
        public override PluginCategory Category => PluginCategory.StorageProvider;

        /// <summary>
        /// Gets the plugin sub-category.
        /// </summary>
        public virtual string SubCategory => "Storage";

        /// <summary>
        /// Gets the quality level of this plugin (0-100).
        /// </summary>
        public virtual int QualityLevel => 50;

        /// <summary>
        /// Gets the default execution order in the pipeline.
        /// </summary>
        public virtual int DefaultOrder => 100;

        /// <summary>
        /// Gets whether this stage can be bypassed.
        /// </summary>
        public virtual bool AllowBypass => false;

        /// <summary>
        /// Gets stages that must precede this one.
        /// </summary>
        public virtual string[] RequiredPrecedingStages => Array.Empty<string>();

        /// <summary>
        /// Gets stages that are incompatible with this one.
        /// </summary>
        public virtual string[] IncompatibleStages => Array.Empty<string>();

        /// <summary>
        /// Transform data during write operations.
        /// </summary>
        public virtual Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            return OnWriteAsync(input, context, args).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Transform data during read operations.
        /// </summary>
        public virtual Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            return OnReadAsync(stored, context, args).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Async version of OnWrite. Override this for proper async support.
        /// </summary>
        protected virtual Task<Stream> OnWriteAsync(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            return Task.FromResult(input);
        }

        /// <summary>
        /// Async version of OnRead. Override this for proper async support.
        /// </summary>
        protected virtual Task<Stream> OnReadAsync(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            return Task.FromResult(stored);
        }

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

        /// <summary>
        /// Classifies data content for compliance, sensitivity, and governance.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This hook enables AI-powered content classification for:
        /// </para>
        /// <list type="bullet">
        ///   <item>Data sensitivity level determination (Public, Internal, Confidential, Restricted)</item>
        ///   <item>Regulatory compliance tagging (GDPR, HIPAA, PCI-DSS, etc.)</item>
        ///   <item>Content category identification for governance policies</item>
        ///   <item>Retention and lifecycle policy recommendations</item>
        /// </list>
        /// </remarks>
        /// <param name="objectId">The object identifier.</param>
        /// <param name="content">Optional content sample for analysis.</param>
        /// <param name="existingMetadata">Existing object metadata.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Data classification result with tags and recommendations, or null if unavailable.</returns>
        protected async Task<DataClassificationResult?> OnDataClassificationAsync(
            string objectId,
            byte[]? content = null,
            Dictionary<string, object>? existingMetadata = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.SensitivityClassification) &&
                !HasCapability(IntelligenceCapabilities.ComplianceClassification))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["objectId"] = objectId,
                ["storageScheme"] = StorageScheme,
                ["pluginId"] = Id,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (content != null)
            {
                // Use sample hash for large content
                var sampleSize = Math.Min(content.Length, 4096);
                var sample = content.Length > sampleSize ? content.Take(sampleSize).ToArray() : content;
                payload["sampleHash"] = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(sample));
                payload["contentSize"] = content.Length;
            }

            if (existingMetadata != null)
                payload["existingMetadata"] = existingMetadata;

            var response = await SendIntelligenceRequestAsync(
                "intelligence.request.data-classification",
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new DataClassificationResult
                {
                    SensitivityLevel = result.TryGetValue("sensitivityLevel", out var sl) && sl is string sensitivity ? sensitivity : "Normal",
                    ComplianceFrameworks = result.TryGetValue("complianceFrameworks", out var cf) && cf is string[] frameworks ? frameworks : Array.Empty<string>(),
                    Categories = result.TryGetValue("categories", out var cats) && cats is string[] categories ? categories : Array.Empty<string>(),
                    Tags = result.TryGetValue("tags", out var tags) && tags is string[] tagArray ? tagArray : Array.Empty<string>(),
                    ContainsPII = result.TryGetValue("containsPII", out var pii) && pii is true,
                    PIITypes = result.TryGetValue("piiTypes", out var piiTypes) && piiTypes is string[] types ? types : Array.Empty<string>(),
                    RetentionPolicy = result.TryGetValue("retentionPolicy", out var rp) && rp is string policy ? policy : null,
                    EncryptionRequired = result.TryGetValue("encryptionRequired", out var er) && er is true,
                    Confidence = result.TryGetValue("confidence", out var conf) && conf is double confidence ? confidence : 0.5,
                    Reasoning = result.TryGetValue("reasoning", out var r) && r is string reason ? reason : null
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
            metadata["SupportsDataClassification"] = HasCapability(IntelligenceCapabilities.SensitivityClassification) ||
                                                      HasCapability(IntelligenceCapabilities.ComplianceClassification);
            return metadata;
        }
    }

    #endregion

    #region Access Control Plugin Base

    /// <summary>
    /// Intelligence-aware base class for access control plugins.
    /// Provides hooks for UEBA (User and Entity Behavior Analytics) and anomaly detection.
    /// </summary>
    [Obsolete("Use SecurityPluginBase from DataWarehouse.SDK.Contracts.Hierarchy namespace. This class will be removed in Phase 28.")]
    public abstract class IntelligenceAwareAccessControlPluginBase : IntelligenceAwarePluginBase
    {
        /// <summary>
        /// Gets the plugin category.
        /// </summary>
        public override PluginCategory Category => PluginCategory.SecurityProvider;

        /// <summary>
        /// Gets the plugin sub-category.
        /// </summary>
        public virtual string SubCategory => "AccessControl";

        /// <summary>
        /// Gets the quality level of this plugin (0-100).
        /// </summary>
        public virtual int QualityLevel => 50;

        /// <summary>
        /// Gets the default execution order in the pipeline.
        /// </summary>
        public virtual int DefaultOrder => 100;

        /// <summary>
        /// Gets whether this stage can be bypassed.
        /// </summary>
        public virtual bool AllowBypass => false;

        /// <summary>
        /// Gets stages that must precede this one.
        /// </summary>
        public virtual string[] RequiredPrecedingStages => Array.Empty<string>();

        /// <summary>
        /// Gets stages that are incompatible with this one.
        /// </summary>
        public virtual string[] IncompatibleStages => Array.Empty<string>();

        /// <summary>
        /// Transform data during write operations.
        /// </summary>
        public virtual Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            return OnWriteAsync(input, context, args).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Transform data during read operations.
        /// </summary>
        public virtual Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            return OnReadAsync(stored, context, args).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Async version of OnWrite. Override this for proper async support.
        /// </summary>
        protected virtual Task<Stream> OnWriteAsync(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            return Task.FromResult(input);
        }

        /// <summary>
        /// Async version of OnRead. Override this for proper async support.
        /// </summary>
        protected virtual Task<Stream> OnReadAsync(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            return Task.FromResult(stored);
        }

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

        /// <summary>
        /// Predicts potential security threats based on current context and historical patterns.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This hook enables AI-powered threat prediction for:
        /// </para>
        /// <list type="bullet">
        ///   <item>Proactive threat identification before attacks occur</item>
        ///   <item>Risk scoring for access requests</item>
        ///   <item>Attack vector prediction based on patterns</item>
        ///   <item>Adaptive security policy recommendations</item>
        /// </list>
        /// </remarks>
        /// <param name="threatContext">Context information for threat analysis.</param>
        /// <param name="historicalThreats">Optional historical threat data.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Threat prediction with risk assessment and recommendations, or null if unavailable.</returns>
        protected async Task<ThreatPredictionResult?> PredictThreatAsync(
            ThreatAnalysisContext threatContext,
            ThreatHistoryData[]? historicalThreats = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.ThreatAssessment))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["threatContext"] = new Dictionary<string, object>
                {
                    ["userId"] = threatContext.UserId ?? string.Empty,
                    ["resourceId"] = threatContext.ResourceId ?? string.Empty,
                    ["actionType"] = threatContext.ActionType ?? string.Empty,
                    ["sourceIp"] = threatContext.SourceIp ?? string.Empty,
                    ["userAgent"] = threatContext.UserAgent ?? string.Empty,
                    ["timestamp"] = threatContext.Timestamp.ToString("O"),
                    ["geoLocation"] = threatContext.GeoLocation ?? string.Empty,
                    ["deviceFingerprint"] = threatContext.DeviceFingerprint ?? string.Empty
                },
                ["pluginId"] = Id,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (historicalThreats != null && historicalThreats.Length > 0)
            {
                payload["historicalThreats"] = historicalThreats.Select(t => new Dictionary<string, object>
                {
                    ["threatType"] = t.ThreatType,
                    ["severity"] = t.Severity,
                    ["timestamp"] = t.Timestamp.ToString("O"),
                    ["wasBlocked"] = t.WasBlocked,
                    ["sourceIp"] = t.SourceIp ?? string.Empty
                }).ToArray();
            }

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestThreatAssessment,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new ThreatPredictionResult
                {
                    ThreatLevel = result.TryGetValue("threatLevel", out var tl) && tl is string level ? level : "Low",
                    RiskScore = result.TryGetValue("riskScore", out var rs) && rs is double score ? score : 0.0,
                    PredictedThreats = result.TryGetValue("predictedThreats", out var pt) && pt is PredictedThreat[] threats ? threats : Array.Empty<PredictedThreat>(),
                    AttackVectors = result.TryGetValue("attackVectors", out var av) && av is string[] vectors ? vectors : Array.Empty<string>(),
                    MitigationRecommendations = result.TryGetValue("mitigationRecommendations", out var mr) && mr is string[] mitigations ? mitigations : Array.Empty<string>(),
                    ShouldBlock = result.TryGetValue("shouldBlock", out var sb) && sb is true,
                    ShouldAlert = result.TryGetValue("shouldAlert", out var sa) && sa is true,
                    RequiresAdditionalAuthentication = result.TryGetValue("requiresAdditionalAuth", out var raa) && raa is true,
                    Confidence = result.TryGetValue("confidence", out var conf) && conf is double confidence ? confidence : 0.5,
                    ExpiresAt = result.TryGetValue("expiresAt", out var exp) && exp is string expStr && DateTimeOffset.TryParse(expStr, out var expDate) ? expDate : null
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
            metadata["SupportsThreatPrediction"] = HasCapability(IntelligenceCapabilities.ThreatAssessment);
            return metadata;
        }
    }

    #endregion

    #region Compliance Plugin Base

    /// <summary>
    /// Intelligence-aware base class for compliance plugins.
    /// Provides hooks for PII detection, compliance classification, and sensitivity assessment.
    /// </summary>
    [Obsolete("Use SecurityPluginBase from DataWarehouse.SDK.Contracts.Hierarchy namespace. This class will be removed in Phase 28.")]
    public abstract class IntelligenceAwareCompliancePluginBase : IntelligenceAwarePluginBase
    {
        /// <summary>
        /// Gets the plugin category.
        /// </summary>
        public override PluginCategory Category => PluginCategory.GovernanceProvider;

        /// <summary>
        /// Gets the plugin sub-category.
        /// </summary>
        public virtual string SubCategory => "Compliance";

        /// <summary>
        /// Gets the quality level of this plugin (0-100).
        /// </summary>
        public virtual int QualityLevel => 50;

        /// <summary>
        /// Gets the default execution order in the pipeline.
        /// </summary>
        public virtual int DefaultOrder => 100;

        /// <summary>
        /// Gets whether this stage can be bypassed.
        /// </summary>
        public virtual bool AllowBypass => false;

        /// <summary>
        /// Gets stages that must precede this one.
        /// </summary>
        public virtual string[] RequiredPrecedingStages => Array.Empty<string>();

        /// <summary>
        /// Gets stages that are incompatible with this one.
        /// </summary>
        public virtual string[] IncompatibleStages => Array.Empty<string>();

        /// <summary>
        /// Transform data during write operations.
        /// </summary>
        public virtual Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            return OnWriteAsync(input, context, args).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Transform data during read operations.
        /// </summary>
        public virtual Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            return OnReadAsync(stored, context, args).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Async version of OnWrite. Override this for proper async support.
        /// </summary>
        protected virtual Task<Stream> OnWriteAsync(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            return Task.FromResult(input);
        }

        /// <summary>
        /// Async version of OnRead. Override this for proper async support.
        /// </summary>
        protected virtual Task<Stream> OnReadAsync(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            return Task.FromResult(stored);
        }

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

        /// <summary>
        /// Generates an AI-powered audit summary narrative from audit log entries.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This hook enables AI-powered audit narrative generation for:
        /// </para>
        /// <list type="bullet">
        ///   <item>Executive-level compliance summaries</item>
        ///   <item>Incident timeline reconstruction</item>
        ///   <item>Regulatory report generation</item>
        ///   <item>Trend analysis and anomaly highlighting</item>
        /// </list>
        /// </remarks>
        /// <param name="auditEntries">The audit log entries to summarize.</param>
        /// <param name="summaryOptions">Options for summary generation.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Generated audit summary with narrative and insights, or null if unavailable.</returns>
        protected async Task<AuditSummaryResult?> GenerateAuditSummaryAsync(
            AuditLogEntry[] auditEntries,
            AuditSummaryOptions? summaryOptions = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.Summarization))
                return null;

            if (auditEntries == null || auditEntries.Length == 0)
                return new AuditSummaryResult { Summary = "No audit entries to summarize.", EntryCount = 0 };

            var payload = new Dictionary<string, object>
            {
                ["auditEntries"] = auditEntries.Select(e => new Dictionary<string, object>
                {
                    ["timestamp"] = e.Timestamp.ToString("O"),
                    ["action"] = e.Action,
                    ["userId"] = e.UserId ?? string.Empty,
                    ["resourceId"] = e.ResourceId ?? string.Empty,
                    ["outcome"] = e.Outcome,
                    ["details"] = e.Details ?? string.Empty,
                    ["severity"] = e.Severity ?? "Info",
                    ["sourceIp"] = e.SourceIp ?? string.Empty
                }).ToArray(),
                ["entryCount"] = auditEntries.Length,
                ["pluginId"] = Id,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (summaryOptions != null)
            {
                payload["options"] = new Dictionary<string, object>
                {
                    ["format"] = summaryOptions.Format ?? "Narrative",
                    ["audience"] = summaryOptions.Audience ?? "Technical",
                    ["maxLength"] = summaryOptions.MaxLength ?? 2000,
                    ["includeRecommendations"] = summaryOptions.IncludeRecommendations,
                    ["highlightAnomalies"] = summaryOptions.HighlightAnomalies,
                    ["complianceFramework"] = summaryOptions.ComplianceFramework ?? string.Empty
                };
            }

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestSummarization,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new AuditSummaryResult
                {
                    Summary = result.TryGetValue("summary", out var sum) && sum is string summary ? summary : string.Empty,
                    EntryCount = auditEntries.Length,
                    TimeRange = new AuditTimeRange
                    {
                        Start = auditEntries.Min(e => e.Timestamp),
                        End = auditEntries.Max(e => e.Timestamp)
                    },
                    KeyFindings = result.TryGetValue("keyFindings", out var kf) && kf is string[] findings ? findings : Array.Empty<string>(),
                    AnomaliesDetected = result.TryGetValue("anomaliesDetected", out var ad) && ad is AuditAnomaly[] anomalies ? anomalies : Array.Empty<AuditAnomaly>(),
                    Recommendations = result.TryGetValue("recommendations", out var recs) && recs is string[] recommendations ? recommendations : Array.Empty<string>(),
                    RiskLevel = result.TryGetValue("riskLevel", out var rl) && rl is string risk ? risk : "Low",
                    ComplianceStatus = result.TryGetValue("complianceStatus", out var cs) && cs is string status ? status : "Unknown",
                    GeneratedAt = DateTimeOffset.UtcNow
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
            metadata["SupportsAuditSummaryGeneration"] = HasCapability(IntelligenceCapabilities.Summarization);
            return metadata;
        }
    }

    #endregion

    #region Data Management Plugin Base

    /// <summary>
    /// Intelligence-aware base class for data management plugins.
    /// Provides hooks for semantic deduplication and lifecycle prediction.
    /// </summary>
    [Obsolete("Use DataManagementPluginBase from DataWarehouse.SDK.Contracts.Hierarchy namespace. This class will be removed in Phase 28.")]
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

        /// <summary>
        /// Performs semantic content indexing for enhanced search and discovery.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This hook enables AI-powered content indexing for:
        /// </para>
        /// <list type="bullet">
        ///   <item>Semantic embedding generation for vector search</item>
        ///   <item>Automatic keyword and topic extraction</item>
        ///   <item>Entity recognition and relationship mapping</item>
        ///   <item>Content summarization for previews</item>
        /// </list>
        /// </remarks>
        /// <param name="contentId">The content identifier.</param>
        /// <param name="content">The content to index (text or bytes).</param>
        /// <param name="existingMetadata">Existing metadata to enhance.</param>
        /// <param name="indexOptions">Options for indexing behavior.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Indexing result with semantic data, or null if unavailable.</returns>
        protected async Task<ContentIndexingResult?> OnContentIndexingAsync(
            string contentId,
            object content,
            Dictionary<string, object>? existingMetadata = null,
            ContentIndexingOptions? indexOptions = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            // Require at least embeddings or keyword extraction capability
            if (!HasCapability(IntelligenceCapabilities.Embeddings) &&
                !HasCapability(IntelligenceCapabilities.KeywordExtraction))
                return null;

            // Prepare content for indexing
            string textContent;
            if (content is string s)
            {
                textContent = s;
            }
            else if (content is byte[] bytes)
            {
                // For binary content, we'll send a hash and size; actual content analysis
                // would require content extraction (not done here)
                textContent = $"[Binary content: {bytes.Length} bytes, SHA256: {Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(bytes))}]";
            }
            else
            {
                textContent = content?.ToString() ?? string.Empty;
            }

            var payload = new Dictionary<string, object>
            {
                ["contentId"] = contentId,
                ["content"] = textContent.Length > 10000 ? textContent.Substring(0, 10000) : textContent, // Limit content size
                ["contentLength"] = textContent.Length,
                ["pluginId"] = Id,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (existingMetadata != null)
                payload["existingMetadata"] = existingMetadata;

            if (indexOptions != null)
            {
                payload["options"] = new Dictionary<string, object>
                {
                    ["generateEmbedding"] = indexOptions.GenerateEmbedding,
                    ["extractKeywords"] = indexOptions.ExtractKeywords,
                    ["extractEntities"] = indexOptions.ExtractEntities,
                    ["generateSummary"] = indexOptions.GenerateSummary,
                    ["maxKeywords"] = indexOptions.MaxKeywords,
                    ["maxEntities"] = indexOptions.MaxEntities,
                    ["summaryMaxLength"] = indexOptions.SummaryMaxLength
                };
            }

            var response = await SendIntelligenceRequestAsync(
                "intelligence.request.content-indexing",
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new ContentIndexingResult
                {
                    ContentId = contentId,
                    Embedding = result.TryGetValue("embedding", out var emb) && emb is float[] embedding ? embedding : null,
                    EmbeddingModel = result.TryGetValue("embeddingModel", out var em) && em is string model ? model : null,
                    Keywords = result.TryGetValue("keywords", out var kw) && kw is KeywordInfo[] keywords ? keywords : Array.Empty<KeywordInfo>(),
                    Entities = result.TryGetValue("entities", out var ent) && ent is IndexedEntity[] entities ? entities : Array.Empty<IndexedEntity>(),
                    Topics = result.TryGetValue("topics", out var top) && top is string[] topics ? topics : Array.Empty<string>(),
                    Summary = result.TryGetValue("summary", out var sum) && sum is string summary ? summary : null,
                    Language = result.TryGetValue("language", out var lang) && lang is string language ? language : null,
                    Sentiment = result.TryGetValue("sentiment", out var sent) && sent is string sentiment ? sentiment : null,
                    SentimentScore = result.TryGetValue("sentimentScore", out var ss) && ss is double sentScore ? sentScore : null,
                    Confidence = result.TryGetValue("confidence", out var conf) && conf is double confidence ? confidence : 0.5,
                    IndexedAt = DateTimeOffset.UtcNow
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
            metadata["SupportsContentIndexing"] = HasCapability(IntelligenceCapabilities.Embeddings) ||
                                                   HasCapability(IntelligenceCapabilities.KeywordExtraction);
            return metadata;
        }
    }

    #endregion

    #region Key Management Plugin Base

    /// <summary>
    /// Intelligence-aware base class for key management plugins.
    /// Provides hooks for AI-powered key usage pattern analysis, rotation prediction,
    /// and compromise risk assessment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Key management plugins can leverage Intelligence for:
    /// </para>
    /// <list type="bullet">
    ///   <item>Anomalous key usage pattern detection</item>
    ///   <item>Predictive key rotation scheduling based on usage patterns</item>
    ///   <item>AI-driven compromise risk assessment</item>
    ///   <item>Intelligent key lifecycle management</item>
    /// </list>
    /// </remarks>
    [Obsolete("Use SecurityPluginBase from DataWarehouse.SDK.Contracts.Hierarchy namespace. This class will be removed in Phase 28.")]
    public abstract class IntelligenceAwareKeyManagementPluginBase : IntelligenceAwarePluginBase
    {
        /// <summary>
        /// Gets the key store type (e.g., "file", "vault", "hsm", "kms").
        /// </summary>
        public abstract string KeyStoreType { get; }

        /// <summary>
        /// Gets whether this key store supports Hardware Security Module (HSM) operations.
        /// </summary>
        public virtual bool SupportsHsm => false;

        /// <summary>
        /// Analyzes key usage patterns for anomalies.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This hook detects anomalous key usage patterns such as:
        /// </para>
        /// <list type="bullet">
        ///   <item>Unusual access frequency or timing</item>
        ///   <item>Access from unexpected sources</item>
        ///   <item>Bulk key operations that may indicate exfiltration</item>
        ///   <item>Keys accessed outside normal business hours</item>
        /// </list>
        /// </remarks>
        /// <param name="keyId">The key identifier being analyzed.</param>
        /// <param name="usageEvents">Recent usage events for the key.</param>
        /// <param name="baselineProfile">Optional baseline usage profile for comparison.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Usage pattern analysis result, or null if unavailable.</returns>
        protected async Task<KeyUsagePatternResult?> OnKeyUsagePatternAsync(
            string keyId,
            KeyUsageEvent[] usageEvents,
            KeyUsageProfile? baselineProfile = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.AnomalyDetection))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["keyId"] = keyId,
                ["keyStoreType"] = KeyStoreType,
                ["usageEvents"] = usageEvents.Select(e => new Dictionary<string, object>
                {
                    ["timestamp"] = e.Timestamp.ToString("O"),
                    ["operation"] = e.Operation,
                    ["userId"] = e.UserId ?? string.Empty,
                    ["sourceIp"] = e.SourceIp ?? string.Empty,
                    ["success"] = e.Success,
                    ["duration"] = e.Duration.TotalMilliseconds
                }).ToArray(),
                ["eventCount"] = usageEvents.Length,
                ["pluginId"] = Id,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (baselineProfile != null)
            {
                payload["baseline"] = new Dictionary<string, object>
                {
                    ["averageDailyUsage"] = baselineProfile.AverageDailyUsage,
                    ["typicalHours"] = baselineProfile.TypicalHours,
                    ["commonOperations"] = baselineProfile.CommonOperations,
                    ["knownSources"] = baselineProfile.KnownSources,
                    ["baselineCreatedAt"] = baselineProfile.BaselineCreatedAt.ToString("O")
                };
            }

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestAnomalyDetection,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new KeyUsagePatternResult
                {
                    KeyId = keyId,
                    IsAnomaly = result.TryGetValue("isAnomaly", out var ia) && ia is true,
                    AnomalyScore = result.TryGetValue("anomalyScore", out var ascore) && ascore is double score ? score : 0.0,
                    AnomalyTypes = result.TryGetValue("anomalyTypes", out var at) && at is string[] types ? types : Array.Empty<string>(),
                    RiskLevel = result.TryGetValue("riskLevel", out var rl) && rl is string risk ? risk : "Low",
                    Findings = result.TryGetValue("findings", out var f) && f is string[] findings ? findings : Array.Empty<string>(),
                    Recommendations = result.TryGetValue("recommendations", out var r) && r is string[] recs ? recs : Array.Empty<string>(),
                    ShouldRotate = result.TryGetValue("shouldRotate", out var sr) && sr is true,
                    ShouldRevoke = result.TryGetValue("shouldRevoke", out var srev) && srev is true,
                    ShouldAlert = result.TryGetValue("shouldAlert", out var sa) && sa is true,
                    AnalyzedAt = DateTimeOffset.UtcNow
                };
            }

            return null;
        }

        /// <summary>
        /// Predicts optimal key rotation timing based on usage patterns and security best practices.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This hook enables AI-driven rotation scheduling based on:
        /// </para>
        /// <list type="bullet">
        ///   <item>Key age and compliance requirements</item>
        ///   <item>Usage frequency and patterns</item>
        ///   <item>Risk factors and threat landscape</item>
        ///   <item>Business impact considerations</item>
        /// </list>
        /// </remarks>
        /// <param name="keyId">The key identifier.</param>
        /// <param name="keyMetadata">Metadata about the key.</param>
        /// <param name="usageHistory">Historical usage data.</param>
        /// <param name="complianceRequirements">Applicable compliance requirements.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Rotation prediction with recommended timing, or null if unavailable.</returns>
        protected async Task<KeyRotationPrediction?> PredictKeyRotationAsync(
            string keyId,
            KeyRotationMetadata keyMetadata,
            KeyUsageHistorySummary? usageHistory = null,
            string[]? complianceRequirements = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.Prediction))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["keyId"] = keyId,
                ["keyStoreType"] = KeyStoreType,
                ["keyMetadata"] = new Dictionary<string, object>
                {
                    ["createdAt"] = keyMetadata.CreatedAt.ToString("O"),
                    ["lastRotatedAt"] = keyMetadata.LastRotatedAt?.ToString("O") ?? string.Empty,
                    ["keySize"] = keyMetadata.KeySize,
                    ["algorithm"] = keyMetadata.Algorithm ?? string.Empty,
                    ["rotationCount"] = keyMetadata.RotationCount,
                    ["isActive"] = keyMetadata.IsActive
                },
                ["pluginId"] = Id,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (usageHistory != null)
            {
                payload["usageHistory"] = new Dictionary<string, object>
                {
                    ["totalOperations"] = usageHistory.TotalOperations,
                    ["uniqueUsers"] = usageHistory.UniqueUsers,
                    ["peakUsageHour"] = usageHistory.PeakUsageHour,
                    ["averageDailyOperations"] = usageHistory.AverageDailyOperations,
                    ["lastUsedAt"] = usageHistory.LastUsedAt?.ToString("O") ?? string.Empty
                };
            }

            if (complianceRequirements != null)
                payload["complianceRequirements"] = complianceRequirements;

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestPrediction,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new KeyRotationPrediction
                {
                    KeyId = keyId,
                    ShouldRotate = result.TryGetValue("shouldRotate", out var sr) && sr is true,
                    RecommendedRotationDate = result.TryGetValue("recommendedRotationDate", out var rrd) && rrd is string dateStr && DateTimeOffset.TryParse(dateStr, out var date) ? date : null,
                    UrgencyLevel = result.TryGetValue("urgencyLevel", out var ul) && ul is string urgency ? urgency : "Normal",
                    RiskIfDelayed = result.TryGetValue("riskIfDelayed", out var rid) && rid is double risk ? risk : 0.0,
                    Reasoning = result.TryGetValue("reasoning", out var r) && r is string reason ? reason : null,
                    ComplianceDrivers = result.TryGetValue("complianceDrivers", out var cd) && cd is string[] drivers ? drivers : Array.Empty<string>(),
                    OptimalRotationWindow = result.TryGetValue("optimalRotationWindow", out var orw) && orw is string window ? window : null,
                    EstimatedDowntime = result.TryGetValue("estimatedDowntime", out var ed) && ed is string downtime && TimeSpan.TryParse(downtime, out var dt) ? dt : null,
                    Confidence = result.TryGetValue("confidence", out var conf) && conf is double confidence ? confidence : 0.5,
                    PredictedAt = DateTimeOffset.UtcNow
                };
            }

            return null;
        }

        /// <summary>
        /// Assesses the risk of key compromise based on various signals.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This hook provides AI-driven threat assessment for keys:
        /// </para>
        /// <list type="bullet">
        ///   <item>Exposure risk from access patterns</item>
        ///   <item>Threat intelligence correlation</item>
        ///   <item>Infrastructure vulnerability assessment</item>
        ///   <item>Historical incident correlation</item>
        /// </list>
        /// </remarks>
        /// <param name="keyId">The key identifier.</param>
        /// <param name="riskFactors">Known risk factors to consider.</param>
        /// <param name="threatIntelligence">Optional threat intelligence data.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Compromise risk assessment, or null if unavailable.</returns>
        protected async Task<KeyCompromiseRiskResult?> GetCompromiseRiskAsync(
            string keyId,
            KeyRiskFactors riskFactors,
            ThreatIntelligenceData[]? threatIntelligence = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.ThreatAssessment))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["keyId"] = keyId,
                ["keyStoreType"] = KeyStoreType,
                ["supportsHsm"] = SupportsHsm,
                ["riskFactors"] = new Dictionary<string, object>
                {
                    ["keyAge"] = riskFactors.KeyAge.TotalDays,
                    ["accessCount"] = riskFactors.AccessCount,
                    ["uniqueAccessors"] = riskFactors.UniqueAccessors,
                    ["hasExternalAccess"] = riskFactors.HasExternalAccess,
                    ["isStoredInHsm"] = riskFactors.IsStoredInHsm,
                    ["hasBeenExported"] = riskFactors.HasBeenExported,
                    ["lastAuditedAt"] = riskFactors.LastAuditedAt?.ToString("O") ?? string.Empty,
                    ["complianceViolations"] = riskFactors.ComplianceViolations
                },
                ["pluginId"] = Id,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (threatIntelligence != null && threatIntelligence.Length > 0)
            {
                payload["threatIntelligence"] = threatIntelligence.Select(ti => new Dictionary<string, object>
                {
                    ["source"] = ti.Source,
                    ["threatType"] = ti.ThreatType,
                    ["severity"] = ti.Severity,
                    ["reportedAt"] = ti.ReportedAt.ToString("O"),
                    ["indicators"] = ti.Indicators
                }).ToArray();
            }

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestThreatAssessment,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new KeyCompromiseRiskResult
                {
                    KeyId = keyId,
                    RiskLevel = result.TryGetValue("riskLevel", out var rl) && rl is string level ? level : "Low",
                    RiskScore = result.TryGetValue("riskScore", out var rs) && rs is double score ? score : 0.0,
                    CompromiseIndicators = result.TryGetValue("compromiseIndicators", out var ci) && ci is CompromiseIndicator[] indicators ? indicators : Array.Empty<CompromiseIndicator>(),
                    VulnerabilityFactors = result.TryGetValue("vulnerabilityFactors", out var vf) && vf is string[] factors ? factors : Array.Empty<string>(),
                    ThreatActors = result.TryGetValue("threatActors", out var ta) && ta is string[] actors ? actors : Array.Empty<string>(),
                    ImmediateActions = result.TryGetValue("immediateActions", out var ia) && ia is string[] actions ? actions : Array.Empty<string>(),
                    LongTermRecommendations = result.TryGetValue("longTermRecommendations", out var ltr) && ltr is string[] recs ? recs : Array.Empty<string>(),
                    ShouldRevokeImmediately = result.TryGetValue("shouldRevokeImmediately", out var sri) && sri is true,
                    ShouldNotifySecurityTeam = result.TryGetValue("shouldNotifySecurityTeam", out var snst) && snst is true,
                    Confidence = result.TryGetValue("confidence", out var conf) && conf is double confidence ? confidence : 0.5,
                    AssessedAt = DateTimeOffset.UtcNow,
                    ValidUntil = result.TryGetValue("validUntil", out var vu) && vu is string vuStr && DateTimeOffset.TryParse(vuStr, out var vuDate) ? vuDate : DateTimeOffset.UtcNow.AddHours(1)
                };
            }

            return null;
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["KeyManagementType"] = "IntelligenceAware";
            metadata["KeyStoreType"] = KeyStoreType;
            metadata["SupportsHsm"] = SupportsHsm;
            metadata["SupportsUsagePatternAnalysis"] = HasCapability(IntelligenceCapabilities.AnomalyDetection);
            metadata["SupportsRotationPrediction"] = HasCapability(IntelligenceCapabilities.Prediction);
            metadata["SupportsCompromiseRiskAssessment"] = HasCapability(IntelligenceCapabilities.ThreatAssessment);
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

    #region Encryption Anomaly Detection Types

    /// <summary>
    /// Metrics for an encryption operation used in anomaly detection.
    /// </summary>
    public sealed class EncryptionOperationMetrics
    {
        /// <summary>The encryption algorithm used.</summary>
        public string Algorithm { get; init; } = string.Empty;

        /// <summary>The key size in bits.</summary>
        public int KeySize { get; init; }

        /// <summary>The size of the data being encrypted in bytes.</summary>
        public long DataSize { get; init; }

        /// <summary>The type of operation (encrypt/decrypt).</summary>
        public string OperationType { get; init; } = "encrypt";

        /// <summary>When the operation occurred.</summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>How long the operation took.</summary>
        public TimeSpan Duration { get; init; }

        /// <summary>Optional source identifier for the operation.</summary>
        public string? SourceId { get; init; }
    }

    /// <summary>
    /// Historical patterns for encryption operations used as baseline for anomaly detection.
    /// </summary>
    public sealed class EncryptionPatternHistory
    {
        /// <summary>Average number of operations per hour in normal conditions.</summary>
        public double AverageOperationsPerHour { get; init; }

        /// <summary>Typical minimum data size for operations.</summary>
        public long TypicalMinDataSize { get; init; }

        /// <summary>Typical maximum data size for operations.</summary>
        public long TypicalMaxDataSize { get; init; }

        /// <summary>List of commonly used algorithms.</summary>
        public string[] CommonAlgorithms { get; init; } = Array.Empty<string>();

        /// <summary>When the baseline was established.</summary>
        public DateTimeOffset BaselineEstablishedAt { get; init; }
    }

    /// <summary>
    /// Result of encryption anomaly detection.
    /// </summary>
    public sealed class EncryptionAnomalyResult
    {
        /// <summary>Whether an anomaly was detected.</summary>
        public bool IsAnomaly { get; init; }

        /// <summary>Anomaly score (0.0-1.0).</summary>
        public double AnomalyScore { get; init; }

        /// <summary>Type of anomaly detected (e.g., "UnusualVolume", "UnusualAlgorithm").</summary>
        public string? AnomalyType { get; init; }

        /// <summary>Human-readable description of the anomaly.</summary>
        public string? Description { get; init; }

        /// <summary>Severity level of the anomaly.</summary>
        public string Severity { get; init; } = "Low";

        /// <summary>Recommended actions to address the anomaly.</summary>
        public string[] Recommendations { get; init; } = Array.Empty<string>();

        /// <summary>Whether an alert should be raised.</summary>
        public bool ShouldAlert { get; init; }

        /// <summary>Whether the operation should be blocked.</summary>
        public bool ShouldBlock { get; init; }
    }

    #endregion

    #region Content Analysis Types

    /// <summary>
    /// Metadata about content being analyzed for compression or other processing.
    /// </summary>
    public sealed class ContentMetadata
    {
        /// <summary>Original file name if applicable.</summary>
        public string? FileName { get; init; }

        /// <summary>MIME type of the content.</summary>
        public string? MimeType { get; init; }

        /// <summary>When the content was created.</summary>
        public DateTimeOffset? CreatedAt { get; init; }

        /// <summary>Tags associated with the content.</summary>
        public string[]? Tags { get; init; }

        /// <summary>Additional custom metadata.</summary>
        public Dictionary<string, object>? CustomMetadata { get; init; }
    }

    /// <summary>
    /// Result of AI-powered content analysis for compression optimization.
    /// </summary>
    public sealed class ContentAnalysisResult
    {
        /// <summary>Detected content type (e.g., "text", "binary", "image").</summary>
        public string ContentType { get; init; } = "unknown";

        /// <summary>Whether the content is compressible.</summary>
        public bool IsCompressible { get; init; } = true;

        /// <summary>Whether the content appears to be already compressed.</summary>
        public bool IsAlreadyCompressed { get; init; }

        /// <summary>Estimated compression ratio achievable.</summary>
        public double EstimatedCompressionRatio { get; init; } = 1.0;

        /// <summary>Recommended compression algorithm.</summary>
        public string? RecommendedAlgorithm { get; init; }

        /// <summary>Recommended compression level.</summary>
        public int RecommendedLevel { get; init; } = 6;

        /// <summary>Pre-processing steps recommended.</summary>
        public string[] PreProcessingSteps { get; init; } = Array.Empty<string>();

        /// <summary>Sensitivity level of the content.</summary>
        public string SensitivityLevel { get; init; } = "Normal";

        /// <summary>Confidence in the analysis (0.0-1.0).</summary>
        public double Confidence { get; init; }
    }

    #endregion

    #region Data Classification Types

    /// <summary>
    /// Result of AI-powered data classification for storage and governance.
    /// </summary>
    public sealed class DataClassificationResult
    {
        /// <summary>Sensitivity level (Public, Internal, Confidential, Restricted).</summary>
        public string SensitivityLevel { get; init; } = "Normal";

        /// <summary>Applicable compliance frameworks.</summary>
        public string[] ComplianceFrameworks { get; init; } = Array.Empty<string>();

        /// <summary>Content categories.</summary>
        public string[] Categories { get; init; } = Array.Empty<string>();

        /// <summary>Classification tags.</summary>
        public string[] Tags { get; init; } = Array.Empty<string>();

        /// <summary>Whether PII was detected.</summary>
        public bool ContainsPII { get; init; }

        /// <summary>Types of PII detected.</summary>
        public string[] PIITypes { get; init; } = Array.Empty<string>();

        /// <summary>Recommended retention policy.</summary>
        public string? RetentionPolicy { get; init; }

        /// <summary>Whether encryption is required.</summary>
        public bool EncryptionRequired { get; init; }

        /// <summary>Confidence in the classification.</summary>
        public double Confidence { get; init; }

        /// <summary>Reasoning for the classification.</summary>
        public string? Reasoning { get; init; }
    }

    #endregion

    #region Audit Types

    /// <summary>
    /// An anomaly detected during compliance audit.
    /// </summary>
    public sealed class AuditAnomaly
    {
        /// <summary>Type of anomaly detected.</summary>
        public string Type { get; init; } = string.Empty;

        /// <summary>Description of the anomaly.</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>Severity level.</summary>
        public string Severity { get; init; } = "Low";

        /// <summary>Affected resources.</summary>
        public string[] AffectedResources { get; init; } = Array.Empty<string>();

        /// <summary>Recommended remediation steps.</summary>
        public string[] RemediationSteps { get; init; } = Array.Empty<string>();
    }

    /// <summary>An audit log entry for compliance analysis.</summary>
    public sealed class AuditLogEntry
    {
        /// <summary>When the event occurred.</summary>
        public DateTimeOffset Timestamp { get; init; }
        /// <summary>The action performed.</summary>
        public string Action { get; init; } = string.Empty;
        /// <summary>User who performed the action.</summary>
        public string? UserId { get; init; }
        /// <summary>Resource affected by the action.</summary>
        public string? ResourceId { get; init; }
        /// <summary>Outcome of the action (Success/Failure).</summary>
        public string Outcome { get; init; } = "Success";
        /// <summary>Additional details about the action.</summary>
        public string? Details { get; init; }
        /// <summary>Severity level of the event.</summary>
        public string? Severity { get; init; }
        /// <summary>Source IP address.</summary>
        public string? SourceIp { get; init; }
    }

    /// <summary>Options for audit summary generation.</summary>
    public sealed class AuditSummaryOptions
    {
        /// <summary>Output format (Narrative, Bullet, Table).</summary>
        public string? Format { get; init; }
        /// <summary>Target audience (Technical, Executive, Compliance).</summary>
        public string? Audience { get; init; }
        /// <summary>Maximum length of the summary.</summary>
        public int? MaxLength { get; init; }
        /// <summary>Whether to include recommendations.</summary>
        public bool IncludeRecommendations { get; init; } = true;
        /// <summary>Whether to highlight anomalies.</summary>
        public bool HighlightAnomalies { get; init; } = true;
        /// <summary>Compliance framework to focus on.</summary>
        public string? ComplianceFramework { get; init; }
    }

    /// <summary>Result of AI-generated audit summary.</summary>
    public sealed class AuditSummaryResult
    {
        /// <summary>The generated summary narrative.</summary>
        public string Summary { get; init; } = string.Empty;
        /// <summary>Number of entries summarized.</summary>
        public int EntryCount { get; init; }
        /// <summary>Time range covered by the summary.</summary>
        public AuditTimeRange? TimeRange { get; init; }
        /// <summary>Key findings from the audit.</summary>
        public string[] KeyFindings { get; init; } = Array.Empty<string>();
        /// <summary>Anomalies detected in the audit data.</summary>
        public AuditAnomaly[] AnomaliesDetected { get; init; } = Array.Empty<AuditAnomaly>();
        /// <summary>Recommended actions based on the audit.</summary>
        public string[] Recommendations { get; init; } = Array.Empty<string>();
        /// <summary>Overall risk level assessment.</summary>
        public string RiskLevel { get; init; } = "Low";
        /// <summary>Compliance status summary.</summary>
        public string ComplianceStatus { get; init; } = "Unknown";
        /// <summary>When the summary was generated.</summary>
        public DateTimeOffset GeneratedAt { get; init; }
    }

    /// <summary>Time range for audit summary.</summary>
    public sealed class AuditTimeRange
    {
        /// <summary>Start of the time range.</summary>
        public DateTimeOffset Start { get; init; }
        /// <summary>End of the time range.</summary>
        public DateTimeOffset End { get; init; }
    }

    #endregion

    #region Threat Prediction Types

    /// <summary>Context for threat analysis.</summary>
    public sealed class ThreatAnalysisContext
    {
        /// <summary>User identifier.</summary>
        public string? UserId { get; init; }
        /// <summary>Resource being accessed.</summary>
        public string? ResourceId { get; init; }
        /// <summary>Action being performed.</summary>
        public string? ActionType { get; init; }
        /// <summary>Source IP address.</summary>
        public string? SourceIp { get; init; }
        /// <summary>User agent string.</summary>
        public string? UserAgent { get; init; }
        /// <summary>When the action is occurring.</summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
        /// <summary>Geographic location.</summary>
        public string? GeoLocation { get; init; }
        /// <summary>Device fingerprint.</summary>
        public string? DeviceFingerprint { get; init; }
    }

    /// <summary>Historical threat data.</summary>
    public sealed class ThreatHistoryData
    {
        /// <summary>Type of threat.</summary>
        public string ThreatType { get; init; } = string.Empty;
        /// <summary>Severity level.</summary>
        public string Severity { get; init; } = "Low";
        /// <summary>When the threat was detected.</summary>
        public DateTimeOffset Timestamp { get; init; }
        /// <summary>Whether the threat was blocked.</summary>
        public bool WasBlocked { get; init; }
        /// <summary>Source IP of the threat.</summary>
        public string? SourceIp { get; init; }
    }

    /// <summary>A predicted threat.</summary>
    public sealed class PredictedThreat
    {
        /// <summary>Type of predicted threat.</summary>
        public string ThreatType { get; init; } = string.Empty;
        /// <summary>Probability of the threat (0.0-1.0).</summary>
        public double Probability { get; init; }
        /// <summary>Potential impact.</summary>
        public string Impact { get; init; } = "Low";
        /// <summary>Description of the threat.</summary>
        public string? Description { get; init; }
    }

    /// <summary>Result of threat prediction.</summary>
    public sealed class ThreatPredictionResult
    {
        /// <summary>Overall threat level.</summary>
        public string ThreatLevel { get; init; } = "Low";
        /// <summary>Risk score (0.0-1.0).</summary>
        public double RiskScore { get; init; }
        /// <summary>Predicted threats.</summary>
        public PredictedThreat[] PredictedThreats { get; init; } = Array.Empty<PredictedThreat>();
        /// <summary>Potential attack vectors.</summary>
        public string[] AttackVectors { get; init; } = Array.Empty<string>();
        /// <summary>Mitigation recommendations.</summary>
        public string[] MitigationRecommendations { get; init; } = Array.Empty<string>();
        /// <summary>Whether to block the action.</summary>
        public bool ShouldBlock { get; init; }
        /// <summary>Whether to raise an alert.</summary>
        public bool ShouldAlert { get; init; }
        /// <summary>Whether additional authentication is required.</summary>
        public bool RequiresAdditionalAuthentication { get; init; }
        /// <summary>Confidence in the prediction.</summary>
        public double Confidence { get; init; }
        /// <summary>When the prediction expires.</summary>
        public DateTimeOffset? ExpiresAt { get; init; }
    }

    #endregion

    #region Content Indexing Types

    /// <summary>Options for content indexing.</summary>
    public sealed class ContentIndexingOptions
    {
        /// <summary>Whether to generate embeddings.</summary>
        public bool GenerateEmbedding { get; init; } = true;
        /// <summary>Whether to extract keywords.</summary>
        public bool ExtractKeywords { get; init; } = true;
        /// <summary>Whether to extract entities.</summary>
        public bool ExtractEntities { get; init; } = true;
        /// <summary>Whether to generate a summary.</summary>
        public bool GenerateSummary { get; init; } = true;
        /// <summary>Maximum number of keywords to extract.</summary>
        public int MaxKeywords { get; init; } = 20;
        /// <summary>Maximum number of entities to extract.</summary>
        public int MaxEntities { get; init; } = 50;
        /// <summary>Maximum length of the summary.</summary>
        public int SummaryMaxLength { get; init; } = 500;
    }

    /// <summary>Result of content indexing.</summary>
    public sealed class ContentIndexingResult
    {
        /// <summary>The content identifier.</summary>
        public string ContentId { get; init; } = string.Empty;
        /// <summary>Generated embedding vector.</summary>
        public float[]? Embedding { get; init; }
        /// <summary>Model used for embedding generation.</summary>
        public string? EmbeddingModel { get; init; }
        /// <summary>Extracted keywords with relevance scores.</summary>
        public KeywordInfo[] Keywords { get; init; } = Array.Empty<KeywordInfo>();
        /// <summary>Extracted entities.</summary>
        public IndexedEntity[] Entities { get; init; } = Array.Empty<IndexedEntity>();
        /// <summary>Detected topics.</summary>
        public string[] Topics { get; init; } = Array.Empty<string>();
        /// <summary>Generated summary.</summary>
        public string? Summary { get; init; }
        /// <summary>Detected language.</summary>
        public string? Language { get; init; }
        /// <summary>Detected sentiment.</summary>
        public string? Sentiment { get; init; }
        /// <summary>Sentiment score (-1.0 to 1.0).</summary>
        public double? SentimentScore { get; init; }
        /// <summary>Confidence in the indexing results.</summary>
        public double Confidence { get; init; }
        /// <summary>When the content was indexed.</summary>
        public DateTimeOffset IndexedAt { get; init; }
    }

    /// <summary>Information about an extracted keyword.</summary>
    public sealed class KeywordInfo
    {
        /// <summary>The keyword.</summary>
        public string Keyword { get; init; } = string.Empty;
        /// <summary>Relevance score (0.0-1.0).</summary>
        public double Relevance { get; init; }
        /// <summary>Number of occurrences.</summary>
        public int Count { get; init; }
    }

    /// <summary>An entity extracted during indexing.</summary>
    public sealed class IndexedEntity
    {
        /// <summary>The entity text.</summary>
        public string Text { get; init; } = string.Empty;
        /// <summary>Entity type.</summary>
        public string Type { get; init; } = string.Empty;
        /// <summary>Confidence score.</summary>
        public double Confidence { get; init; }
    }

    #endregion

    #region Key Management Types

    /// <summary>A key usage event for pattern analysis.</summary>
    public sealed class KeyUsageEvent
    {
        /// <summary>When the event occurred.</summary>
        public DateTimeOffset Timestamp { get; init; }
        /// <summary>Operation performed (GetKey, CreateKey, DeleteKey).</summary>
        public string Operation { get; init; } = string.Empty;
        /// <summary>User who performed the operation.</summary>
        public string? UserId { get; init; }
        /// <summary>Source IP address.</summary>
        public string? SourceIp { get; init; }
        /// <summary>Whether the operation succeeded.</summary>
        public bool Success { get; init; }
        /// <summary>Duration of the operation.</summary>
        public TimeSpan Duration { get; init; }
    }

    /// <summary>Baseline profile for key usage patterns.</summary>
    public sealed class KeyUsageProfile
    {
        /// <summary>Average daily usage count.</summary>
        public double AverageDailyUsage { get; init; }
        /// <summary>Typical hours when key is used (0-23).</summary>
        public int[] TypicalHours { get; init; } = Array.Empty<int>();
        /// <summary>Common operations performed.</summary>
        public string[] CommonOperations { get; init; } = Array.Empty<string>();
        /// <summary>Known source IPs/ranges.</summary>
        public string[] KnownSources { get; init; } = Array.Empty<string>();
        /// <summary>When the baseline was created.</summary>
        public DateTimeOffset BaselineCreatedAt { get; init; }
    }

    /// <summary>Result of key usage pattern analysis.</summary>
    public sealed class KeyUsagePatternResult
    {
        /// <summary>The key identifier.</summary>
        public string KeyId { get; init; } = string.Empty;
        /// <summary>Whether an anomaly was detected.</summary>
        public bool IsAnomaly { get; init; }
        /// <summary>Anomaly score (0.0-1.0).</summary>
        public double AnomalyScore { get; init; }
        /// <summary>Types of anomalies detected.</summary>
        public string[] AnomalyTypes { get; init; } = Array.Empty<string>();
        /// <summary>Risk level assessment.</summary>
        public string RiskLevel { get; init; } = "Low";
        /// <summary>Detailed findings.</summary>
        public string[] Findings { get; init; } = Array.Empty<string>();
        /// <summary>Recommended actions.</summary>
        public string[] Recommendations { get; init; } = Array.Empty<string>();
        /// <summary>Whether key should be rotated.</summary>
        public bool ShouldRotate { get; init; }
        /// <summary>Whether key should be revoked.</summary>
        public bool ShouldRevoke { get; init; }
        /// <summary>Whether an alert should be raised.</summary>
        public bool ShouldAlert { get; init; }
        /// <summary>When the analysis was performed.</summary>
        public DateTimeOffset AnalyzedAt { get; init; }
    }

    /// <summary>Metadata for key rotation prediction.</summary>
    public sealed class KeyRotationMetadata
    {
        /// <summary>When the key was created.</summary>
        public DateTimeOffset CreatedAt { get; init; }
        /// <summary>When the key was last rotated.</summary>
        public DateTimeOffset? LastRotatedAt { get; init; }
        /// <summary>Key size in bits.</summary>
        public int KeySize { get; init; }
        /// <summary>Encryption algorithm.</summary>
        public string? Algorithm { get; init; }
        /// <summary>Number of times the key has been rotated.</summary>
        public int RotationCount { get; init; }
        /// <summary>Whether the key is currently active.</summary>
        public bool IsActive { get; init; }
    }

    /// <summary>Historical usage summary for rotation prediction.</summary>
    public sealed class KeyUsageHistorySummary
    {
        /// <summary>Total number of operations.</summary>
        public long TotalOperations { get; init; }
        /// <summary>Number of unique users.</summary>
        public int UniqueUsers { get; init; }
        /// <summary>Hour with peak usage (0-23).</summary>
        public int PeakUsageHour { get; init; }
        /// <summary>Average daily operations.</summary>
        public double AverageDailyOperations { get; init; }
        /// <summary>When the key was last used.</summary>
        public DateTimeOffset? LastUsedAt { get; init; }
    }

    /// <summary>Prediction for key rotation timing.</summary>
    public sealed class KeyRotationPrediction
    {
        /// <summary>The key identifier.</summary>
        public string KeyId { get; init; } = string.Empty;
        /// <summary>Whether rotation is recommended.</summary>
        public bool ShouldRotate { get; init; }
        /// <summary>Recommended date for rotation.</summary>
        public DateTimeOffset? RecommendedRotationDate { get; init; }
        /// <summary>Urgency level (Low, Normal, High, Critical).</summary>
        public string UrgencyLevel { get; init; } = "Normal";
        /// <summary>Risk score if rotation is delayed (0.0-1.0).</summary>
        public double RiskIfDelayed { get; init; }
        /// <summary>Reasoning for the recommendation.</summary>
        public string? Reasoning { get; init; }
        /// <summary>Compliance requirements driving the recommendation.</summary>
        public string[] ComplianceDrivers { get; init; } = Array.Empty<string>();
        /// <summary>Optimal time window for rotation.</summary>
        public string? OptimalRotationWindow { get; init; }
        /// <summary>Estimated downtime for rotation.</summary>
        public TimeSpan? EstimatedDowntime { get; init; }
        /// <summary>Confidence in the prediction.</summary>
        public double Confidence { get; init; }
        /// <summary>When the prediction was made.</summary>
        public DateTimeOffset PredictedAt { get; init; }
    }

    /// <summary>Risk factors for key compromise assessment.</summary>
    public sealed class KeyRiskFactors
    {
        /// <summary>Age of the key.</summary>
        public TimeSpan KeyAge { get; init; }
        /// <summary>Total access count.</summary>
        public long AccessCount { get; init; }
        /// <summary>Number of unique accessors.</summary>
        public int UniqueAccessors { get; init; }
        /// <summary>Whether the key has external access.</summary>
        public bool HasExternalAccess { get; init; }
        /// <summary>Whether the key is stored in HSM.</summary>
        public bool IsStoredInHsm { get; init; }
        /// <summary>Whether the key has been exported.</summary>
        public bool HasBeenExported { get; init; }
        /// <summary>When the key was last audited.</summary>
        public DateTimeOffset? LastAuditedAt { get; init; }
        /// <summary>Known compliance violations.</summary>
        public int ComplianceViolations { get; init; }
    }

    /// <summary>Threat intelligence data for risk assessment.</summary>
    public sealed class ThreatIntelligenceData
    {
        /// <summary>Source of the intelligence.</summary>
        public string Source { get; init; } = string.Empty;
        /// <summary>Type of threat.</summary>
        public string ThreatType { get; init; } = string.Empty;
        /// <summary>Severity level.</summary>
        public string Severity { get; init; } = "Low";
        /// <summary>When the threat was reported.</summary>
        public DateTimeOffset ReportedAt { get; init; }
        /// <summary>Indicators of compromise.</summary>
        public string[] Indicators { get; init; } = Array.Empty<string>();
    }

    /// <summary>An indicator of potential compromise.</summary>
    public sealed class CompromiseIndicator
    {
        /// <summary>Type of indicator.</summary>
        public string Type { get; init; } = string.Empty;
        /// <summary>Description of the indicator.</summary>
        public string Description { get; init; } = string.Empty;
        /// <summary>Severity level.</summary>
        public string Severity { get; init; } = "Low";
        /// <summary>When the indicator was detected.</summary>
        public DateTimeOffset DetectedAt { get; init; }
    }

    /// <summary>Result of key compromise risk assessment.</summary>
    public sealed class KeyCompromiseRiskResult
    {
        /// <summary>The key identifier.</summary>
        public string KeyId { get; init; } = string.Empty;
        /// <summary>Overall risk level.</summary>
        public string RiskLevel { get; init; } = "Low";
        /// <summary>Risk score (0.0-1.0).</summary>
        public double RiskScore { get; init; }
        /// <summary>Detected compromise indicators.</summary>
        public CompromiseIndicator[] CompromiseIndicators { get; init; } = Array.Empty<CompromiseIndicator>();
        /// <summary>Vulnerability factors identified.</summary>
        public string[] VulnerabilityFactors { get; init; } = Array.Empty<string>();
        /// <summary>Potential threat actors.</summary>
        public string[] ThreatActors { get; init; } = Array.Empty<string>();
        /// <summary>Immediate actions to take.</summary>
        public string[] ImmediateActions { get; init; } = Array.Empty<string>();
        /// <summary>Long-term recommendations.</summary>
        public string[] LongTermRecommendations { get; init; } = Array.Empty<string>();
        /// <summary>Whether to revoke the key immediately.</summary>
        public bool ShouldRevokeImmediately { get; init; }
        /// <summary>Whether to notify the security team.</summary>
        public bool ShouldNotifySecurityTeam { get; init; }
        /// <summary>Confidence in the assessment.</summary>
        public double Confidence { get; init; }
        /// <summary>When the assessment was performed.</summary>
        public DateTimeOffset AssessedAt { get; init; }
        /// <summary>How long the assessment is valid.</summary>
        public DateTimeOffset ValidUntil { get; init; }
    }

    #endregion

    // ================================================================================
    // T127.2: INTELLIGENCE-AWARE DATABASE PLUGIN BASE
    // ================================================================================

    #region Database Plugin Base

    /// <summary>
    /// Intelligence-aware base class for database plugins.
    /// Provides hooks for AI-powered query optimization, schema inference,
    /// predictive caching, and anomaly detection in database operations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Database plugins can leverage Intelligence for:
    /// </para>
    /// <list type="bullet">
    ///   <item>Query optimization and rewriting recommendations</item>
    ///   <item>Schema inference and enrichment from sample data</item>
    ///   <item>Predictive query result caching based on access patterns</item>
    ///   <item>Anomaly detection in query patterns and performance</item>
    ///   <item>Index recommendation based on query analysis</item>
    ///   <item>Data quality assessment and validation</item>
    /// </list>
    /// </remarks>
    [Obsolete("Use StoragePluginBase from DataWarehouse.SDK.Contracts.Hierarchy namespace. This class will be removed in Phase 28.")]
    public abstract class IntelligenceAwareDatabasePluginBase : IntelligenceAwarePluginBase
    {
        /// <summary>
        /// Gets the database engine name (e.g., "MySQL", "MongoDB", "PostgreSQL").
        /// </summary>
        public abstract string Engine { get; }

        /// <summary>
        /// Gets the database category (e.g., "Relational", "NoSQL", "Graph").
        /// </summary>
        public abstract string DatabaseCategory { get; }

        /// <summary>
        /// Gets the plugin category.
        /// </summary>
        public override PluginCategory Category => PluginCategory.FeatureProvider;

        /// <summary>
        /// Gets the plugin sub-category.
        /// </summary>
        public virtual string SubCategory => "Database";

        /// <summary>
        /// Gets the quality level of this plugin (0-100).
        /// </summary>
        public virtual int QualityLevel => 50;

        /// <summary>
        /// Gets the default execution order in the pipeline.
        /// </summary>
        public virtual int DefaultOrder => 100;

        /// <summary>
        /// Gets whether this stage can be bypassed.
        /// </summary>
        public virtual bool AllowBypass => false;

        /// <summary>
        /// Gets stages that must precede this one.
        /// </summary>
        public virtual string[] RequiredPrecedingStages => Array.Empty<string>();

        /// <summary>
        /// Gets stages that are incompatible with this one.
        /// </summary>
        public virtual string[] IncompatibleStages => Array.Empty<string>();

        /// <summary>
        /// Gets AI-powered query optimization recommendations.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This hook analyzes queries and provides optimization suggestions:
        /// </para>
        /// <list type="bullet">
        ///   <item>Query rewriting for better performance</item>
        ///   <item>Index usage recommendations</item>
        ///   <item>Join order optimization</item>
        ///   <item>Subquery optimization</item>
        /// </list>
        /// </remarks>
        /// <param name="query">The query to optimize.</param>
        /// <param name="schemaInfo">Optional schema information for context.</param>
        /// <param name="queryHistory">Optional historical query data for pattern analysis.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Query optimization result with recommendations, or null if unavailable.</returns>
        protected async Task<DatabaseQueryOptimizationResult?> OnQueryOptimizationAsync(
            string query,
            DatabaseSchemaInfo? schemaInfo = null,
            QueryHistoryEntry[]? queryHistory = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.QueryOptimization))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["query"] = query,
                ["engine"] = Engine,
                ["databaseCategory"] = DatabaseCategory,
                ["pluginId"] = Id,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (schemaInfo != null)
            {
                payload["schemaInfo"] = new Dictionary<string, object>
                {
                    ["tables"] = schemaInfo.Tables ?? Array.Empty<string>(),
                    ["indexes"] = schemaInfo.Indexes ?? Array.Empty<string>(),
                    ["relationships"] = schemaInfo.Relationships ?? Array.Empty<string>(),
                    ["columnStats"] = schemaInfo.ColumnStatistics ?? new Dictionary<string, object>()
                };
            }

            if (queryHistory != null && queryHistory.Length > 0)
            {
                payload["queryHistory"] = queryHistory.Select(q => new Dictionary<string, object>
                {
                    ["queryHash"] = q.QueryHash,
                    ["executionTime"] = q.ExecutionTimeMs,
                    ["rowsExamined"] = q.RowsExamined,
                    ["rowsReturned"] = q.RowsReturned,
                    ["timestamp"] = q.Timestamp.ToString("O")
                }).ToArray();
            }

            var response = await SendIntelligenceRequestAsync(
                "intelligence.request.query-optimization",
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new DatabaseQueryOptimizationResult
                {
                    OriginalQuery = query,
                    OptimizedQuery = result.TryGetValue("optimizedQuery", out var oq) && oq is string optimized ? optimized : query,
                    Recommendations = result.TryGetValue("recommendations", out var recs) && recs is QueryRecommendation[] recommendations ? recommendations : Array.Empty<QueryRecommendation>(),
                    SuggestedIndexes = result.TryGetValue("suggestedIndexes", out var idx) && idx is IndexSuggestion[] indexes ? indexes : Array.Empty<IndexSuggestion>(),
                    EstimatedSpeedup = result.TryGetValue("estimatedSpeedup", out var speedup) && speedup is double speed ? speed : 1.0,
                    PerformanceIssues = result.TryGetValue("performanceIssues", out var issues) && issues is string[] issueList ? issueList : Array.Empty<string>(),
                    Confidence = result.TryGetValue("confidence", out var conf) && conf is double confidence ? confidence : 0.5
                };
            }

            return null;
        }

        /// <summary>
        /// Infers schema from sample data using AI.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This hook enables AI-powered schema inference for:
        /// </para>
        /// <list type="bullet">
        ///   <item>Data type detection from samples</item>
        ///   <item>Relationship discovery between entities</item>
        ///   <item>Primary key and foreign key suggestions</item>
        ///   <item>Constraint recommendations</item>
        /// </list>
        /// </remarks>
        /// <param name="sampleData">Sample data for schema inference.</param>
        /// <param name="existingSchema">Optional existing schema for enhancement.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Inferred schema with field descriptions and relationships, or null if unavailable.</returns>
        protected async Task<DatabaseSchemaInferenceResult?> OnSchemaInferenceAsync(
            object[] sampleData,
            DatabaseSchemaInfo? existingSchema = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.SchemaInference))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["sampleData"] = sampleData,
                ["sampleCount"] = sampleData.Length,
                ["engine"] = Engine,
                ["databaseCategory"] = DatabaseCategory,
                ["pluginId"] = Id,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (existingSchema != null)
            {
                payload["existingSchema"] = existingSchema;
            }

            var response = await SendIntelligenceRequestAsync(
                "intelligence.request.schema-inference",
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new DatabaseSchemaInferenceResult
                {
                    InferredFields = result.TryGetValue("inferredFields", out var fields) && fields is InferredField[] fieldList ? fieldList : Array.Empty<InferredField>(),
                    SuggestedPrimaryKey = result.TryGetValue("suggestedPrimaryKey", out var pk) && pk is string primaryKey ? primaryKey : null,
                    SuggestedForeignKeys = result.TryGetValue("suggestedForeignKeys", out var fks) && fks is ForeignKeySuggestion[] foreignKeys ? foreignKeys : Array.Empty<ForeignKeySuggestion>(),
                    SuggestedIndexes = result.TryGetValue("suggestedIndexes", out var idx) && idx is string[] indexes ? indexes : Array.Empty<string>(),
                    DataQualityIssues = result.TryGetValue("dataQualityIssues", out var issues) && issues is DataQualityIssue[] issueList ? issueList : Array.Empty<DataQualityIssue>(),
                    Confidence = result.TryGetValue("confidence", out var conf) && conf is double confidence ? confidence : 0.5
                };
            }

            return null;
        }

        /// <summary>
        /// Detects anomalies in query patterns and database operations.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This hook analyzes database operations for anomalies such as:
        /// </para>
        /// <list type="bullet">
        ///   <item>Unusual query patterns indicating potential SQL injection</item>
        ///   <item>Performance degradation trends</item>
        ///   <item>Excessive data access patterns</item>
        ///   <item>Schema modification attempts</item>
        /// </list>
        /// </remarks>
        /// <param name="operationMetrics">Metrics about the database operation.</param>
        /// <param name="baselineMetrics">Optional baseline metrics for comparison.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Anomaly detection result, or null if unavailable.</returns>
        protected async Task<DatabaseAnomalyResult?> OnDatabaseAnomalyDetectionAsync(
            DatabaseOperationMetrics operationMetrics,
            DatabaseBaselineMetrics? baselineMetrics = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.AnomalyDetection))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["operationMetrics"] = new Dictionary<string, object>
                {
                    ["queryType"] = operationMetrics.QueryType,
                    ["executionTimeMs"] = operationMetrics.ExecutionTimeMs,
                    ["rowsAffected"] = operationMetrics.RowsAffected,
                    ["rowsExamined"] = operationMetrics.RowsExamined,
                    ["timestamp"] = operationMetrics.Timestamp.ToString("O"),
                    ["userId"] = operationMetrics.UserId ?? string.Empty,
                    ["sourceIp"] = operationMetrics.SourceIp ?? string.Empty,
                    ["database"] = operationMetrics.Database ?? string.Empty,
                    ["table"] = operationMetrics.Table ?? string.Empty
                },
                ["engine"] = Engine,
                ["pluginId"] = Id,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (baselineMetrics != null)
            {
                payload["baseline"] = new Dictionary<string, object>
                {
                    ["averageExecutionTimeMs"] = baselineMetrics.AverageExecutionTimeMs,
                    ["averageRowsAffected"] = baselineMetrics.AverageRowsAffected,
                    ["typicalQueryPatterns"] = baselineMetrics.TypicalQueryPatterns,
                    ["typicalAccessHours"] = baselineMetrics.TypicalAccessHours,
                    ["baselineCreatedAt"] = baselineMetrics.BaselineCreatedAt.ToString("O")
                };
            }

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestAnomalyDetection,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new DatabaseAnomalyResult
                {
                    IsAnomaly = result.TryGetValue("isAnomaly", out var ia) && ia is true,
                    AnomalyScore = result.TryGetValue("anomalyScore", out var ascore) && ascore is double score ? score : 0.0,
                    AnomalyType = result.TryGetValue("anomalyType", out var atype) && atype is string anomalyType ? anomalyType : null,
                    ThreatLevel = result.TryGetValue("threatLevel", out var tl) && tl is string level ? level : "Low",
                    Description = result.TryGetValue("description", out var desc) && desc is string description ? description : null,
                    PotentialThreats = result.TryGetValue("potentialThreats", out var threats) && threats is string[] threatList ? threatList : Array.Empty<string>(),
                    Recommendations = result.TryGetValue("recommendations", out var recs) && recs is string[] recommendations ? recommendations : Array.Empty<string>(),
                    ShouldBlock = result.TryGetValue("shouldBlock", out var block) && block is true,
                    ShouldAlert = result.TryGetValue("shouldAlert", out var alert) && alert is true,
                    RequiresReview = result.TryGetValue("requiresReview", out var review) && review is true
                };
            }

            return null;
        }

        /// <summary>
        /// Predicts query result for potential cache preloading.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This hook enables predictive caching by analyzing:
        /// </para>
        /// <list type="bullet">
        ///   <item>Query patterns and access frequency</item>
        ///   <item>Time-based access patterns</item>
        ///   <item>User behavior patterns</item>
        ///   <item>Data staleness and update frequency</item>
        /// </list>
        /// </remarks>
        /// <param name="query">The query to analyze for caching.</param>
        /// <param name="queryHistory">Historical query execution data.</param>
        /// <param name="context">Intelligence context for the operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Cache prediction result, or null if unavailable.</returns>
        protected async Task<QueryCachePrediction?> OnPredictiveCacheAnalysisAsync(
            string query,
            QueryHistoryEntry[]? queryHistory = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.AccessPatternPrediction) &&
                !HasCapability(IntelligenceCapabilities.Prediction))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["query"] = query,
                ["queryHash"] = ComputeQueryHash(query),
                ["engine"] = Engine,
                ["pluginId"] = Id,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (queryHistory != null && queryHistory.Length > 0)
            {
                payload["queryHistory"] = queryHistory.Select(q => new Dictionary<string, object>
                {
                    ["queryHash"] = q.QueryHash,
                    ["executionTime"] = q.ExecutionTimeMs,
                    ["timestamp"] = q.Timestamp.ToString("O"),
                    ["rowsReturned"] = q.RowsReturned,
                    ["cacheHit"] = q.WasCacheHit
                }).ToArray();
            }

            var response = await SendIntelligenceRequestAsync(
                "intelligence.request.cache-prediction",
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new QueryCachePrediction
                {
                    QueryHash = ComputeQueryHash(query),
                    ShouldCache = result.TryGetValue("shouldCache", out var sc) && sc is true,
                    ShouldPreload = result.TryGetValue("shouldPreload", out var sp) && sp is true,
                    RecommendedTtl = result.TryGetValue("recommendedTtl", out var ttl) && ttl is string ttlStr && TimeSpan.TryParse(ttlStr, out var ttlVal) ? ttlVal : TimeSpan.FromMinutes(15),
                    PredictedAccessCount = result.TryGetValue("predictedAccessCount", out var pac) && pac is int count ? count : 0,
                    NextPredictedAccess = result.TryGetValue("nextPredictedAccess", out var npa) && npa is string npaStr && DateTimeOffset.TryParse(npaStr, out var npaDate) ? npaDate : null,
                    AccessPattern = result.TryGetValue("accessPattern", out var ap) && ap is string pattern ? pattern : "Unknown",
                    CachePriority = result.TryGetValue("cachePriority", out var cp) && cp is int priority ? priority : 50,
                    Confidence = result.TryGetValue("confidence", out var conf) && conf is double confidence ? confidence : 0.5
                };
            }

            return null;
        }

        /// <summary>
        /// Computes a hash for a query string for caching purposes.
        /// </summary>
        private static string ComputeQueryHash(string query)
        {
            var normalizedQuery = System.Text.RegularExpressions.Regex.Replace(query.Trim().ToLowerInvariant(), @"\s+", " ", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));
            var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedQuery));
            return Convert.ToHexString(hashBytes)[..16];
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["DatabaseType"] = "IntelligenceAware";
            metadata["Engine"] = Engine;
            metadata["DatabaseCategory"] = DatabaseCategory;
            metadata["SupportsQueryOptimization"] = HasCapability(IntelligenceCapabilities.QueryOptimization);
            metadata["SupportsSchemaInference"] = HasCapability(IntelligenceCapabilities.SchemaInference);
            metadata["SupportsAnomalyDetection"] = HasCapability(IntelligenceCapabilities.AnomalyDetection);
            metadata["SupportsPredictiveCaching"] = HasCapability(IntelligenceCapabilities.AccessPatternPrediction) ||
                                                     HasCapability(IntelligenceCapabilities.Prediction);
            return metadata;
        }
    }

    #endregion

    // ================================================================================
    // T127.5 & T127.7: PREDICTIVE CACHING AND SMART OPTIMIZATION INTERFACES
    // ================================================================================

    #region Predictive Caching Interface

    /// <summary>
    /// Interface for plugins that support AI-powered predictive caching.
    /// Enables cache preloading based on access pattern prediction.
    /// </summary>
    public interface IPredictiveCachingAware
    {
        /// <summary>
        /// Gets whether predictive caching is enabled.
        /// </summary>
        bool IsPredictiveCachingEnabled { get; }

        /// <summary>
        /// Gets the current cache preload queue size.
        /// </summary>
        int PreloadQueueSize { get; }

        /// <summary>
        /// Analyzes access patterns and returns items to preload.
        /// </summary>
        /// <param name="recentAccesses">Recent access history.</param>
        /// <param name="maxItems">Maximum items to return for preloading.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>List of items recommended for cache preloading.</returns>
        Task<IEnumerable<PreloadRecommendation>> GetPreloadRecommendationsAsync(
            AccessHistoryEntry[] recentAccesses,
            int maxItems = 10,
            CancellationToken ct = default);

        /// <summary>
        /// Reports a cache hit or miss for learning.
        /// </summary>
        /// <param name="itemId">The item identifier.</param>
        /// <param name="wasHit">Whether it was a cache hit.</param>
        /// <param name="accessMetadata">Optional access metadata.</param>
        /// <param name="ct">Cancellation token.</param>
        Task ReportCacheAccessAsync(
            string itemId,
            bool wasHit,
            Dictionary<string, object>? accessMetadata = null,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Recommendation for cache preloading.
    /// </summary>
    public sealed class PreloadRecommendation
    {
        /// <summary>Item identifier to preload.</summary>
        public string ItemId { get; init; } = string.Empty;
        /// <summary>Priority for preloading (higher = more urgent).</summary>
        public int Priority { get; init; }
        /// <summary>Predicted time of next access.</summary>
        public DateTimeOffset? PredictedAccessTime { get; init; }
        /// <summary>Confidence in the prediction (0.0-1.0).</summary>
        public double Confidence { get; init; }
        /// <summary>Reason for the recommendation.</summary>
        public string? Reason { get; init; }
    }

    #endregion

    #region Smart Optimization Interface

    /// <summary>
    /// Interface for plugins that support AI-powered smart optimization callbacks.
    /// Provides a unified interface for optimization recommendations across plugin types.
    /// </summary>
    public interface ISmartOptimizationAware
    {
        /// <summary>
        /// Gets whether smart optimization is enabled.
        /// </summary>
        bool IsSmartOptimizationEnabled { get; }

        /// <summary>
        /// Requests optimization analysis for the current state.
        /// </summary>
        /// <param name="optimizationType">Type of optimization to analyze.</param>
        /// <param name="currentMetrics">Current performance/state metrics.</param>
        /// <param name="constraints">Optional optimization constraints.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Optimization recommendations.</returns>
        Task<OptimizationAnalysisResult?> AnalyzeOptimizationAsync(
            OptimizationType optimizationType,
            Dictionary<string, object> currentMetrics,
            OptimizationConstraints? constraints = null,
            CancellationToken ct = default);

        /// <summary>
        /// Applies an optimization recommendation.
        /// </summary>
        /// <param name="recommendation">The recommendation to apply.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Result of applying the optimization.</returns>
        Task<OptimizationApplicationResult> ApplyOptimizationAsync(
            OptimizationRecommendation recommendation,
            CancellationToken ct = default);

        /// <summary>
        /// Event raised when optimization opportunities are detected.
        /// </summary>
        event EventHandler<OptimizationOpportunityEventArgs>? OptimizationOpportunityDetected;
    }

    /// <summary>
    /// Types of optimization that can be analyzed.
    /// </summary>
    public enum OptimizationType
    {
        /// <summary>Performance optimization.</summary>
        Performance,
        /// <summary>Cost optimization.</summary>
        Cost,
        /// <summary>Storage efficiency optimization.</summary>
        Storage,
        /// <summary>Resource utilization optimization.</summary>
        ResourceUtilization,
        /// <summary>Query performance optimization.</summary>
        Query,
        /// <summary>Cache efficiency optimization.</summary>
        Cache,
        /// <summary>Network bandwidth optimization.</summary>
        Network,
        /// <summary>Security posture optimization.</summary>
        Security,
        /// <summary>Compliance optimization.</summary>
        Compliance,
        /// <summary>General/multi-dimensional optimization.</summary>
        General
    }

    /// <summary>
    /// Constraints for optimization analysis.
    /// </summary>
    public sealed class OptimizationConstraints
    {
        /// <summary>Maximum acceptable latency increase.</summary>
        public double? MaxLatencyIncreasePercent { get; init; }
        /// <summary>Maximum acceptable cost.</summary>
        public decimal? MaxCost { get; init; }
        /// <summary>Minimum acceptable performance level.</summary>
        public double? MinPerformanceLevel { get; init; }
        /// <summary>Required compliance frameworks.</summary>
        public string[]? RequiredCompliance { get; init; }
        /// <summary>Features that must be preserved.</summary>
        public string[]? PreserveFeatures { get; init; }
        /// <summary>Maximum downtime during optimization.</summary>
        public TimeSpan? MaxDowntime { get; init; }
    }

    /// <summary>
    /// Result of optimization analysis.
    /// </summary>
    public sealed class OptimizationAnalysisResult
    {
        /// <summary>Current optimization score (0-100).</summary>
        public int CurrentScore { get; init; }
        /// <summary>Potential optimized score (0-100).</summary>
        public int PotentialScore { get; init; }
        /// <summary>Identified optimization opportunities.</summary>
        public OptimizationRecommendation[] Recommendations { get; init; } = Array.Empty<OptimizationRecommendation>();
        /// <summary>Areas already optimized.</summary>
        public string[] OptimizedAreas { get; init; } = Array.Empty<string>();
        /// <summary>Areas needing attention.</summary>
        public string[] AreasNeedingAttention { get; init; } = Array.Empty<string>();
        /// <summary>When the analysis was performed.</summary>
        public DateTimeOffset AnalyzedAt { get; init; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// An optimization recommendation.
    /// </summary>
    public sealed class OptimizationRecommendation
    {
        /// <summary>Unique identifier for the recommendation.</summary>
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        /// <summary>Type of optimization.</summary>
        public OptimizationType Type { get; init; }
        /// <summary>Title of the recommendation.</summary>
        public string Title { get; init; } = string.Empty;
        /// <summary>Detailed description.</summary>
        public string Description { get; init; } = string.Empty;
        /// <summary>Impact level (Low, Medium, High, Critical).</summary>
        public string Impact { get; init; } = "Medium";
        /// <summary>Effort required (Low, Medium, High).</summary>
        public string Effort { get; init; } = "Medium";
        /// <summary>Estimated improvement percentage.</summary>
        public double EstimatedImprovement { get; init; }
        /// <summary>Estimated cost savings.</summary>
        public decimal? EstimatedCostSavings { get; init; }
        /// <summary>Whether this can be auto-applied.</summary>
        public bool CanAutoApply { get; init; }
        /// <summary>Steps to implement manually.</summary>
        public string[] ImplementationSteps { get; init; } = Array.Empty<string>();
        /// <summary>Potential risks of implementing.</summary>
        public string[] Risks { get; init; } = Array.Empty<string>();
        /// <summary>Confidence in the recommendation (0.0-1.0).</summary>
        public double Confidence { get; init; }
    }

    /// <summary>
    /// Result of applying an optimization.
    /// </summary>
    public sealed class OptimizationApplicationResult
    {
        /// <summary>Whether the optimization was applied successfully.</summary>
        public bool Success { get; init; }
        /// <summary>The recommendation that was applied.</summary>
        public string RecommendationId { get; init; } = string.Empty;
        /// <summary>Actual improvement achieved.</summary>
        public double? ActualImprovement { get; init; }
        /// <summary>Actual cost savings achieved.</summary>
        public decimal? ActualCostSavings { get; init; }
        /// <summary>Error message if failed.</summary>
        public string? ErrorMessage { get; init; }
        /// <summary>When the optimization was applied.</summary>
        public DateTimeOffset AppliedAt { get; init; } = DateTimeOffset.UtcNow;
        /// <summary>Metrics before optimization.</summary>
        public Dictionary<string, object>? MetricsBefore { get; init; }
        /// <summary>Metrics after optimization.</summary>
        public Dictionary<string, object>? MetricsAfter { get; init; }
    }

    /// <summary>
    /// Event arguments for optimization opportunity detection.
    /// </summary>
    public sealed class OptimizationOpportunityEventArgs : EventArgs
    {
        /// <summary>The detected opportunity.</summary>
        public required OptimizationRecommendation Opportunity { get; init; }
        /// <summary>When the opportunity was detected.</summary>
        public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;
        /// <summary>Urgency level (Low, Normal, High, Urgent).</summary>
        public string Urgency { get; init; } = "Normal";
    }

    #endregion

    // ================================================================================
    // T127.2 DATABASE SUPPORTING TYPES
    // ================================================================================

    #region Database Types

    /// <summary>
    /// Schema information for query optimization.
    /// </summary>
    public sealed class DatabaseSchemaInfo
    {
        /// <summary>Table names in the database.</summary>
        public string[]? Tables { get; init; }
        /// <summary>Index names in the database.</summary>
        public string[]? Indexes { get; init; }
        /// <summary>Relationship descriptions.</summary>
        public string[]? Relationships { get; init; }
        /// <summary>Column statistics for query planning.</summary>
        public Dictionary<string, object>? ColumnStatistics { get; init; }
    }

    /// <summary>
    /// Historical query execution entry.
    /// </summary>
    public sealed class QueryHistoryEntry
    {
        /// <summary>Hash of the query for grouping.</summary>
        public string QueryHash { get; init; } = string.Empty;
        /// <summary>Execution time in milliseconds.</summary>
        public double ExecutionTimeMs { get; init; }
        /// <summary>Number of rows examined.</summary>
        public long RowsExamined { get; init; }
        /// <summary>Number of rows returned.</summary>
        public long RowsReturned { get; init; }
        /// <summary>When the query was executed.</summary>
        public DateTimeOffset Timestamp { get; init; }
        /// <summary>Whether the result was from cache.</summary>
        public bool WasCacheHit { get; init; }
    }

    /// <summary>
    /// Result of database query optimization analysis.
    /// </summary>
    public sealed class DatabaseQueryOptimizationResult
    {
        /// <summary>The original query.</summary>
        public string OriginalQuery { get; init; } = string.Empty;
        /// <summary>The optimized query.</summary>
        public string OptimizedQuery { get; init; } = string.Empty;
        /// <summary>Optimization recommendations.</summary>
        public QueryRecommendation[] Recommendations { get; init; } = Array.Empty<QueryRecommendation>();
        /// <summary>Suggested indexes to create.</summary>
        public IndexSuggestion[] SuggestedIndexes { get; init; } = Array.Empty<IndexSuggestion>();
        /// <summary>Estimated speedup factor.</summary>
        public double EstimatedSpeedup { get; init; } = 1.0;
        /// <summary>Identified performance issues.</summary>
        public string[] PerformanceIssues { get; init; } = Array.Empty<string>();
        /// <summary>Confidence in the optimization.</summary>
        public double Confidence { get; init; }
    }

    /// <summary>
    /// A query optimization recommendation.
    /// </summary>
    public sealed class QueryRecommendation
    {
        /// <summary>Type of recommendation.</summary>
        public string Type { get; init; } = string.Empty;
        /// <summary>Description of the recommendation.</summary>
        public string Description { get; init; } = string.Empty;
        /// <summary>Impact level.</summary>
        public string Impact { get; init; } = "Medium";
        /// <summary>Suggested change.</summary>
        public string? SuggestedChange { get; init; }
    }

    /// <summary>
    /// A suggested index for query optimization.
    /// </summary>
    public sealed class IndexSuggestion
    {
        /// <summary>Table name for the index.</summary>
        public string Table { get; init; } = string.Empty;
        /// <summary>Columns to include in the index.</summary>
        public string[] Columns { get; init; } = Array.Empty<string>();
        /// <summary>Index type (e.g., BTREE, HASH).</summary>
        public string IndexType { get; init; } = "BTREE";
        /// <summary>Estimated improvement.</summary>
        public double EstimatedImprovement { get; init; }
        /// <summary>Reason for the suggestion.</summary>
        public string? Reason { get; init; }
    }

    /// <summary>
    /// Result of database schema inference.
    /// </summary>
    public sealed class DatabaseSchemaInferenceResult
    {
        /// <summary>Inferred fields with types and descriptions.</summary>
        public InferredField[] InferredFields { get; init; } = Array.Empty<InferredField>();
        /// <summary>Suggested primary key field.</summary>
        public string? SuggestedPrimaryKey { get; init; }
        /// <summary>Suggested foreign key relationships.</summary>
        public ForeignKeySuggestion[] SuggestedForeignKeys { get; init; } = Array.Empty<ForeignKeySuggestion>();
        /// <summary>Suggested indexes.</summary>
        public string[] SuggestedIndexes { get; init; } = Array.Empty<string>();
        /// <summary>Data quality issues found.</summary>
        public DataQualityIssue[] DataQualityIssues { get; init; } = Array.Empty<DataQualityIssue>();
        /// <summary>Confidence in the inference.</summary>
        public double Confidence { get; init; }
    }

    /// <summary>
    /// An inferred field from schema inference.
    /// </summary>
    public sealed class InferredField
    {
        /// <summary>Field name.</summary>
        public string Name { get; init; } = string.Empty;
        /// <summary>Inferred data type.</summary>
        public string DataType { get; init; } = string.Empty;
        /// <summary>Whether the field appears nullable.</summary>
        public bool IsNullable { get; init; }
        /// <summary>Whether the field appears unique.</summary>
        public bool IsUnique { get; init; }
        /// <summary>AI-generated description.</summary>
        public string? Description { get; init; }
        /// <summary>Sample values found.</summary>
        public string[]? SampleValues { get; init; }
        /// <summary>Suggested constraints.</summary>
        public string[]? SuggestedConstraints { get; init; }
    }

    /// <summary>
    /// A suggested foreign key relationship.
    /// </summary>
    public sealed class ForeignKeySuggestion
    {
        /// <summary>Source table.</summary>
        public string SourceTable { get; init; } = string.Empty;
        /// <summary>Source column.</summary>
        public string SourceColumn { get; init; } = string.Empty;
        /// <summary>Target table.</summary>
        public string TargetTable { get; init; } = string.Empty;
        /// <summary>Target column.</summary>
        public string TargetColumn { get; init; } = string.Empty;
        /// <summary>Confidence in the suggestion.</summary>
        public double Confidence { get; init; }
    }

    /// <summary>
    /// A data quality issue found during analysis.
    /// </summary>
    public sealed class DataQualityIssue
    {
        /// <summary>Type of issue.</summary>
        public string IssueType { get; init; } = string.Empty;
        /// <summary>Affected field.</summary>
        public string? AffectedField { get; init; }
        /// <summary>Description of the issue.</summary>
        public string Description { get; init; } = string.Empty;
        /// <summary>Severity level.</summary>
        public string Severity { get; init; } = "Low";
        /// <summary>Suggested fix.</summary>
        public string? SuggestedFix { get; init; }
    }

    /// <summary>
    /// Metrics for a database operation.
    /// </summary>
    public sealed class DatabaseOperationMetrics
    {
        /// <summary>Type of query (SELECT, INSERT, UPDATE, DELETE).</summary>
        public string QueryType { get; init; } = string.Empty;
        /// <summary>Execution time in milliseconds.</summary>
        public double ExecutionTimeMs { get; init; }
        /// <summary>Number of rows affected.</summary>
        public long RowsAffected { get; init; }
        /// <summary>Number of rows examined.</summary>
        public long RowsExamined { get; init; }
        /// <summary>When the operation occurred.</summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
        /// <summary>User who performed the operation.</summary>
        public string? UserId { get; init; }
        /// <summary>Source IP address.</summary>
        public string? SourceIp { get; init; }
        /// <summary>Database name.</summary>
        public string? Database { get; init; }
        /// <summary>Table name.</summary>
        public string? Table { get; init; }
    }

    /// <summary>
    /// Baseline metrics for anomaly detection.
    /// </summary>
    public sealed class DatabaseBaselineMetrics
    {
        /// <summary>Average execution time in milliseconds.</summary>
        public double AverageExecutionTimeMs { get; init; }
        /// <summary>Average rows affected.</summary>
        public double AverageRowsAffected { get; init; }
        /// <summary>Typical query patterns.</summary>
        public string[] TypicalQueryPatterns { get; init; } = Array.Empty<string>();
        /// <summary>Typical access hours (0-23).</summary>
        public int[] TypicalAccessHours { get; init; } = Array.Empty<int>();
        /// <summary>When the baseline was created.</summary>
        public DateTimeOffset BaselineCreatedAt { get; init; }
    }

    /// <summary>
    /// Result of database anomaly detection.
    /// </summary>
    public sealed class DatabaseAnomalyResult
    {
        /// <summary>Whether an anomaly was detected.</summary>
        public bool IsAnomaly { get; init; }
        /// <summary>Anomaly score (0.0-1.0).</summary>
        public double AnomalyScore { get; init; }
        /// <summary>Type of anomaly detected.</summary>
        public string? AnomalyType { get; init; }
        /// <summary>Threat level assessment.</summary>
        public string ThreatLevel { get; init; } = "Low";
        /// <summary>Description of the anomaly.</summary>
        public string? Description { get; init; }
        /// <summary>Potential threats identified.</summary>
        public string[] PotentialThreats { get; init; } = Array.Empty<string>();
        /// <summary>Recommended actions.</summary>
        public string[] Recommendations { get; init; } = Array.Empty<string>();
        /// <summary>Whether to block the operation.</summary>
        public bool ShouldBlock { get; init; }
        /// <summary>Whether to raise an alert.</summary>
        public bool ShouldAlert { get; init; }
        /// <summary>Whether manual review is required.</summary>
        public bool RequiresReview { get; init; }
    }

    /// <summary>
    /// Prediction for query caching behavior.
    /// </summary>
    public sealed class QueryCachePrediction
    {
        /// <summary>Hash of the query.</summary>
        public string QueryHash { get; init; } = string.Empty;
        /// <summary>Whether to cache this query's results.</summary>
        public bool ShouldCache { get; init; }
        /// <summary>Whether to preload this query's results.</summary>
        public bool ShouldPreload { get; init; }
        /// <summary>Recommended time-to-live for cache.</summary>
        public TimeSpan RecommendedTtl { get; init; }
        /// <summary>Predicted number of accesses in the next hour.</summary>
        public int PredictedAccessCount { get; init; }
        /// <summary>Predicted time of next access.</summary>
        public DateTimeOffset? NextPredictedAccess { get; init; }
        /// <summary>Detected access pattern type.</summary>
        public string AccessPattern { get; init; } = "Unknown";
        /// <summary>Cache priority (0-100, higher = more important).</summary>
        public int CachePriority { get; init; }
        /// <summary>Confidence in the prediction.</summary>
        public double Confidence { get; init; }
    }

    #endregion
}
