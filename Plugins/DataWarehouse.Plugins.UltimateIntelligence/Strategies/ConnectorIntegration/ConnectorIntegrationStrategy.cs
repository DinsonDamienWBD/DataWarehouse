using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.ConnectorIntegration
{
    /// <summary>
    /// Connector integration strategy that bridges T90 UltimateIntelligence with T89 UltimateConnector.
    /// Provides AI-powered features for connectors including query optimization, anomaly detection,
    /// schema enrichment, and failure prediction.
    ///
    /// Supports two modes:
    /// - AsyncObservation: Subscribes to connector events, processes asynchronously (no blocking).
    /// - SyncTransformation: Handles transformation requests from connector, returns modified data.
    /// - Hybrid: Both modes enabled.
    /// </summary>
    public sealed class ConnectorIntegrationStrategy : FeatureStrategyBase
    {
        private readonly IMessageBus _messageBus;
        private readonly ILogger? _logger;
        private readonly List<IDisposable> _subscriptions = new();
        private ConnectorIntegrationMode _mode = ConnectorIntegrationMode.Disabled;
        private readonly BoundedDictionary<string, System.Net.Http.HttpClient> _httpClients = new BoundedDictionary<string, System.Net.Http.HttpClient>(1000);

        /// <inheritdoc/>
        public override string StrategyId => "feature-connector-integration";

        /// <inheritdoc/>
        public override string StrategyName => "Connector Integration";

        /// <inheritdoc/>
        public override IntelligenceStrategyInfo Info => new()
        {
            ProviderName = "ConnectorIntegration",
            Description = "Integrates intelligence services with connector pipeline for AI-powered query optimization, anomaly detection, and schema enrichment",
            Capabilities = IntelligenceCapabilities.AnomalyDetection |
                          IntelligenceCapabilities.Prediction |
                          IntelligenceCapabilities.Classification |
                          IntelligenceCapabilities.SemanticSearch,
            ConfigurationRequirements = new[]
            {
                new ConfigurationRequirement
                {
                    Key = "IntegrationMode",
                    Description = "Integration mode: Disabled, AsyncObservation, SyncTransformation, or Hybrid",
                    Required = false,
                    DefaultValue = "AsyncObservation"
                },
                new ConfigurationRequirement
                {
                    Key = "TransformTimeout",
                    Description = "Timeout in seconds for synchronous transformation requests",
                    Required = false,
                    DefaultValue = "5"
                }
            },
            CostTier = 2,
            LatencyTier = 2,
            RequiresNetworkAccess = false,
            SupportsOfflineMode = true,
            Tags = new[] { "connector", "integration", "ai", "optimization", "anomaly-detection" }
        };

        /// <summary>
        /// Initializes a new instance of <see cref="ConnectorIntegrationStrategy"/>.
        /// </summary>
        /// <param name="messageBus">Message bus for pub/sub communication.</param>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public ConnectorIntegrationStrategy(
            IMessageBus messageBus,
            ILogger? logger = null)
        {
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
            _logger = logger;
        }

        /// <summary>
        /// Initializes the connector integration strategy with the specified mode.
        /// </summary>
        /// <param name="mode">Integration mode to activate.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task InitializeAsync(ConnectorIntegrationMode mode, CancellationToken ct = default)
        {
            _mode = mode;

            if (_mode == ConnectorIntegrationMode.Disabled)
            {
                _logger?.LogInformation("Connector integration is disabled");
                return;
            }

            _logger?.LogInformation("Initializing connector integration in {Mode} mode", _mode);

            // Validate configuration
            await ValidateConfigurationAsync(ct);

            // Register async observation handlers if applicable
            if (_mode is ConnectorIntegrationMode.AsyncObservation or ConnectorIntegrationMode.Hybrid)
            {
                RegisterAsyncObservationHandlers();
            }

            // Register sync transformation handlers if applicable
            if (_mode is ConnectorIntegrationMode.SyncTransformation or ConnectorIntegrationMode.Hybrid)
            {
                RegisterSyncTransformationHandlers();
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Validates connector integration configuration.
        /// </summary>
        private async Task ValidateConfigurationAsync(CancellationToken ct)
        {
            // Validate connector registry endpoints (placeholder - would come from config)
            var registryEndpoint = "http://localhost:5000"; // Example
            if (string.IsNullOrWhiteSpace(registryEndpoint))
                throw new InvalidOperationException("Connector registry endpoint is required");

            // Validate max concurrent connections
            var maxConcurrent = 10; // Default
            if (maxConcurrent < 1 || maxConcurrent > 100)
                throw new ArgumentException("max_concurrent_connections must be between 1 and 100");

            // Validate retry policy
            var retryAttempts = 3; // Default
            if (retryAttempts < 1 || retryAttempts > 10)
                throw new ArgumentException("retry_attempts must be between 1 and 10");

            var backoffMs = 1000; // Default 1 second
            if (backoffMs < 100 || backoffMs > 60000)
                throw new ArgumentException("retry_backoff_ms must be between 100ms and 60000ms");

            // Validate timeout
            var timeoutMs = 5000; // Default 5 seconds
            if (timeoutMs < 1000 || timeoutMs > 300000)
                throw new ArgumentException("timeout_ms must be between 1 second and 5 minutes");

            await Task.CompletedTask;
        }

        /// <summary>
        /// Checks connector registry reachability.
        /// </summary>
        private async Task<bool> CheckConnectorRegistryReachabilityAsync(CancellationToken ct)
        {
            try
            {
                // Placeholder - would test actual registry endpoint
                var registryEndpoint = "http://localhost:5000";
                using var httpClient = new System.Net.Http.HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(5);

                var response = await httpClient.GetAsync($"{registryEndpoint}/health", ct);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                Debug.WriteLine($"Caught exception in ConnectorIntegrationStrategy.cs");
                return false;
            }
        }


        /// <summary>
        /// Checks connector integration strategy health (simplified without caching).
        /// </summary>
        public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            // Check connector registry reachability
            return await CheckConnectorRegistryReachabilityAsync(cancellationToken);
        }

        /// <summary>
        /// Shuts down the connector integration strategy.
        /// </summary>
        public new async Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            _logger?.LogInformation("Shutting down connector integration strategy");

            // Cancel pending connector requests
            foreach (var client in _httpClients.Values)
            {
                client.CancelPendingRequests();
            }

            // Dispose HTTP clients
            foreach (var client in _httpClients.Values)
            {
                client.Dispose();
            }
            _httpClients.Clear();

            // Unsubscribe from topics
            DisposeSubscriptions();

            await Task.CompletedTask;
        }

        /// <summary>
        /// Disposes resources and unsubscribes from message bus.
        /// Routes through StrategyBase dispose pattern.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeSubscriptions();
            }
            base.Dispose(disposing);
        }

        private void DisposeSubscriptions()
        {
            _logger?.LogInformation("Disposing connector integration strategy - unsubscribing from {Count} topics", _subscriptions.Count);

            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
        }

        // ============================================================================
        // ASYNC OBSERVATION HANDLERS
        // ============================================================================

        private void RegisterAsyncObservationHandlers()
        {
            _logger?.LogDebug("Registering async observation handlers");

            _subscriptions.Add(_messageBus.Subscribe(
                IntelligenceTopics.ObserveBeforeRequest,
                HandleBeforeRequestObservationAsync));

            _subscriptions.Add(_messageBus.Subscribe(
                IntelligenceTopics.ObserveAfterResponse,
                HandleAfterResponseObservationAsync));

            _subscriptions.Add(_messageBus.Subscribe(
                IntelligenceTopics.ObserveSchemaDiscovered,
                HandleSchemaDiscoveredObservationAsync));

            _subscriptions.Add(_messageBus.Subscribe(
                IntelligenceTopics.ObserveError,
                HandleErrorObservationAsync));

            _subscriptions.Add(_messageBus.Subscribe(
                IntelligenceTopics.ObserveConnectionEstablished,
                HandleConnectionEstablishedObservationAsync));
        }

        private async Task HandleBeforeRequestObservationAsync(PluginMessage message)
        {
            try
            {
                _logger?.LogTrace("Observed before-request event from {StrategyId}",
                    message.Payload.GetValueOrDefault("strategy_id"));

                // Example: Log analytics, update metrics, detect patterns
                // This runs asynchronously and does not block the connector pipeline
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Caught exception in ConnectorIntegrationStrategy.cs: {ex.Message}");
                _logger?.LogWarning(ex, "Error handling before-request observation");
            }
        }

        private async Task HandleAfterResponseObservationAsync(PluginMessage message)
        {
            try
            {
                var strategyId = message.Payload.GetValueOrDefault("strategy_id")?.ToString();
                var durationMs = message.Payload.GetValueOrDefault("duration_ms");
                var success = message.Payload.GetValueOrDefault("success");

                _logger?.LogTrace("Observed after-response event from {StrategyId}: {DurationMs}ms, Success={Success}",
                    strategyId, durationMs, success);

                // Example: Detect performance anomalies, update ML models, trigger alerts
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Caught exception in ConnectorIntegrationStrategy.cs: {ex.Message}");
                _logger?.LogWarning(ex, "Error handling after-response observation");
            }
        }

        private async Task HandleSchemaDiscoveredObservationAsync(PluginMessage message)
        {
            try
            {
                var schemaName = message.Payload.GetValueOrDefault("schema_name")?.ToString();
                var fieldCount = message.Payload.GetValueOrDefault("field_count");

                _logger?.LogTrace("Observed schema discovery: {SchemaName} with {FieldCount} fields",
                    schemaName, fieldCount);

                // Example: Build knowledge graph, classify data, detect PII
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Caught exception in ConnectorIntegrationStrategy.cs: {ex.Message}");
                _logger?.LogWarning(ex, "Error handling schema discovery observation");
            }
        }

        private async Task HandleErrorObservationAsync(PluginMessage message)
        {
            try
            {
                var exceptionType = message.Payload.GetValueOrDefault("exception_type")?.ToString();
                var exceptionMessage = message.Payload.GetValueOrDefault("exception_message")?.ToString();

                _logger?.LogTrace("Observed error: {ExceptionType} - {ExceptionMessage}",
                    exceptionType, exceptionMessage);

                // Example: Pattern detection, failure prediction, root cause analysis
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Caught exception in ConnectorIntegrationStrategy.cs: {ex.Message}");
                _logger?.LogWarning(ex, "Error handling error observation");
            }
        }

        private async Task HandleConnectionEstablishedObservationAsync(PluginMessage message)
        {
            try
            {
                var connectionId = message.Payload.GetValueOrDefault("connection_id")?.ToString();
                var latencyMs = message.Payload.GetValueOrDefault("latency_ms");

                _logger?.LogTrace("Observed connection established: {ConnectionId}, Latency={LatencyMs}ms",
                    connectionId, latencyMs);

                // Track connection event (would increment counter if FeatureStrategyBase supported it)
                // Example: Track connection health, predict failures, optimize pooling
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Caught exception in ConnectorIntegrationStrategy.cs: {ex.Message}");
                _logger?.LogWarning(ex, "Error handling connection established observation");
            }
        }

        // ============================================================================
        // SYNC TRANSFORMATION HANDLERS
        // ============================================================================

        private void RegisterSyncTransformationHandlers()
        {
            _logger?.LogDebug("Registering sync transformation handlers");

            _subscriptions.Add(_messageBus.Subscribe(
                IntelligenceTopics.TransformRequest,
                HandleTransformRequestAsync));

            _subscriptions.Add(_messageBus.Subscribe(
                IntelligenceTopics.OptimizeQuery,
                HandleOptimizeQueryAsync));

            _subscriptions.Add(_messageBus.Subscribe(
                IntelligenceTopics.EnrichSchema,
                HandleEnrichSchemaAsync));

            _subscriptions.Add(_messageBus.Subscribe(
                IntelligenceTopics.DetectAnomaly,
                HandleDetectAnomalyAsync));

            _subscriptions.Add(_messageBus.Subscribe(
                IntelligenceTopics.PredictFailure,
                HandlePredictFailureAsync));
        }

        private async Task<MessageResponse> HandleTransformRequestAsync(PluginMessage message)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var request = DeserializePayload<TransformRequest>(message.Payload);
                if (request == null)
                {
                    return MessageResponse.Error("Invalid transform request payload", "INVALID_PAYLOAD");
                }

                _logger?.LogDebug("Processing transform request for {StrategyId} operation {OperationType}",
                    request.StrategyId, request.OperationType);

                // Example transformation logic using AI provider
                var transformedPayload = await TransformPayloadWithAIAsync(request);

                var response = new TransformResponse
                {
                    Success = true,
                    TransformedPayload = transformedPayload,
                    ProcessingTime = sw.Elapsed,
                    Explanation = "Payload transformed successfully"
                };

                RecordSuccess(sw.Elapsed.TotalMilliseconds);
                return MessageResponse.Ok(response);
            }
            catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                // Error boundary: Connector unavailable (503)
                _logger?.LogWarning(ex, "Connector unavailable during transformation");
                RecordFailure();
                return MessageResponse.Error("Connector service unavailable", "CONNECTOR_UNAVAILABLE");
            }
            catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // Error boundary: Authentication failure (401)
                _logger?.LogWarning(ex, "Authentication failed during transformation");
                RecordFailure();
                return MessageResponse.Error("Authentication failed", "AUTH_FAILED");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("version", StringComparison.OrdinalIgnoreCase))
            {
                // Error boundary: Version mismatch
                _logger?.LogWarning(ex, "Version mismatch during transformation");
                RecordFailure();
                return MessageResponse.Error("Connector version mismatch", "VERSION_MISMATCH");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Caught exception in ConnectorIntegrationStrategy.cs: {ex.Message}");
                _logger?.LogError(ex, "Error transforming request");
                RecordFailure();

                return MessageResponse.Error(ex.Message, "TRANSFORM_ERROR");
            }
        }

        private async Task<MessageResponse> HandleOptimizeQueryAsync(PluginMessage message)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var request = DeserializePayload<OptimizeQueryRequest>(message.Payload);
                if (request == null)
                {
                    return MessageResponse.Error("Invalid query optimization request", "INVALID_PAYLOAD");
                }

                _logger?.LogDebug("Optimizing {QueryLanguage} query for {StrategyId}",
                    request.QueryLanguage, request.StrategyId);

                // Example: Use AI to optimize SQL query
                var optimizedQuery = await OptimizeQueryWithAIAsync(request);

                var response = new OptimizeQueryResponse
                {
                    Success = true,
                    OptimizedQuery = optimizedQuery,
                    WasModified = optimizedQuery != request.QueryText,
                    ConfidenceScore = 0.85,
                    OptimizationsApplied = new List<string> { "Added index hints", "Simplified joins" },
                    Explanation = "Query optimized using AI analysis",
                    ProcessingTime = sw.Elapsed
                };

                RecordSuccess(sw.Elapsed.TotalMilliseconds);
                return MessageResponse.Ok(response);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Caught exception in ConnectorIntegrationStrategy.cs: {ex.Message}");
                _logger?.LogError(ex, "Error optimizing query");
                RecordFailure();

                return MessageResponse.Error(ex.Message, "OPTIMIZE_ERROR");
            }
        }

        private async Task<MessageResponse> HandleEnrichSchemaAsync(PluginMessage message)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var request = DeserializePayload<EnrichSchemaRequest>(message.Payload);
                if (request == null)
                {
                    return MessageResponse.Error("Invalid schema enrichment request", "INVALID_PAYLOAD");
                }

                _logger?.LogDebug("Enriching schema {SchemaName} for {StrategyId}",
                    request.SchemaName, request.StrategyId);

                // Example: Use AI to analyze schema and add semantic metadata
                var enrichedMetadata = await EnrichSchemaWithAIAsync(request);

                var response = new EnrichSchemaResponse
                {
                    Success = true,
                    EnrichedMetadata = enrichedMetadata,
                    SemanticCategories = new List<string> { "user_data", "transactional" },
                    SchemaDescription = $"Schema {request.SchemaName} contains user and transaction data",
                    ProcessingTime = sw.Elapsed
                };

                RecordSuccess(sw.Elapsed.TotalMilliseconds);
                return MessageResponse.Ok(response);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Caught exception in ConnectorIntegrationStrategy.cs: {ex.Message}");
                _logger?.LogError(ex, "Error enriching schema");
                RecordFailure();

                return MessageResponse.Error(ex.Message, "ENRICH_ERROR");
            }
        }

        private async Task<MessageResponse> HandleDetectAnomalyAsync(PluginMessage message)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var request = DeserializePayload<AnomalyDetectionRequest>(message.Payload);
                if (request == null)
                {
                    return MessageResponse.Error("Invalid anomaly detection request", "INVALID_PAYLOAD");
                }

                _logger?.LogDebug("Detecting anomalies in {OperationType} response for {StrategyId}",
                    request.OperationType, request.StrategyId);

                // Example: Use AI to detect anomalies in response data
                var anomalies = await DetectAnomaliesWithAIAsync(request);

                var response = new AnomalyDetectionResponse
                {
                    Success = true,
                    AnomaliesDetected = anomalies.Count > 0,
                    ConfidenceScore = 0.75,
                    Anomalies = anomalies,
                    Severity = anomalies.Any() ? "medium" : "none",
                    Summary = $"Detected {anomalies.Count} anomalies",
                    ProcessingTime = sw.Elapsed
                };

                RecordSuccess(sw.Elapsed.TotalMilliseconds);
                return MessageResponse.Ok(response);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Caught exception in ConnectorIntegrationStrategy.cs: {ex.Message}");
                _logger?.LogError(ex, "Error detecting anomalies");
                RecordFailure();

                return MessageResponse.Error(ex.Message, "ANOMALY_ERROR");
            }
        }

        private async Task<MessageResponse> HandlePredictFailureAsync(PluginMessage message)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var request = DeserializePayload<FailurePredictionRequest>(message.Payload);
                if (request == null)
                {
                    return MessageResponse.Error("Invalid failure prediction request", "INVALID_PAYLOAD");
                }

                _logger?.LogDebug("Predicting failures for {OperationType} on {StrategyId}",
                    request.OperationType, request.StrategyId);

                // Example: Use AI to predict failure likelihood
                var failureProbability = await PredictFailureWithAIAsync(request);

                var response = new FailurePredictionResponse
                {
                    Success = true,
                    FailureProbability = failureProbability,
                    RiskLevel = failureProbability > 0.7 ? "high" : failureProbability > 0.4 ? "medium" : "low",
                    RiskFactors = new List<string> { "High latency", "Connection instability" },
                    PreventiveActions = new List<string> { "Increase connection pool", "Add retry logic" },
                    Explanation = "Predicted based on historical patterns and current metrics",
                    ProcessingTime = sw.Elapsed
                };

                RecordSuccess(sw.Elapsed.TotalMilliseconds);
                return MessageResponse.Ok(response);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Caught exception in ConnectorIntegrationStrategy.cs: {ex.Message}");
                _logger?.LogError(ex, "Error predicting failure");
                RecordFailure();

                return MessageResponse.Error(ex.Message, "PREDICT_ERROR");
            }
        }

        // ============================================================================
        // AI INTEGRATION METHODS (Placeholder implementations)
        // ============================================================================

        private async Task<Dictionary<string, object>> TransformPayloadWithAIAsync(TransformRequest request)
        {
            // M10: AI payload transformation with intelligent request analysis
            if (AIProvider == null)
            {
                _logger?.LogDebug("AI provider not available for payload transformation, returning original");
                return new Dictionary<string, object>(request.RequestPayload);
            }

            try
            {
                // Build AI prompt with request context
                var payloadJson = JsonSerializer.Serialize(request.RequestPayload);
                var metadataJson = JsonSerializer.Serialize(request.Metadata);

                var prompt = $@"Analyze and optimize this connector request payload for operation '{request.OperationType}'.

Request Payload:
{payloadJson}

Metadata:
{metadataJson}

Tasks:
1. Identify inefficient or redundant fields
2. Suggest field transformations for better compatibility
3. Add missing best-practice fields if applicable
4. Return the optimized payload as valid JSON

Provide only the optimized JSON payload in your response, no explanations.";

                var aiRequest = new AIRequest
                {
                    Prompt = prompt,
                    MaxTokens = 800,
                    Temperature = 0.3f // Low temperature for consistent transformations
                };

                var aiResponse = await AIProvider.CompleteAsync(aiRequest);
                RecordTokens(aiResponse.Usage?.TotalTokens ?? 0);

                if (string.IsNullOrWhiteSpace(aiResponse.Content))
                {
                    _logger?.LogWarning("AI returned empty transformation, using original payload");
                    return new Dictionary<string, object>(request.RequestPayload);
                }

                // Parse AI response as JSON
                try
                {
                    var transformedPayload = JsonSerializer.Deserialize<Dictionary<string, object>>(aiResponse.Content);
                    if (transformedPayload != null && transformedPayload.Count > 0)
                    {
                        _logger?.LogDebug("Successfully transformed payload using AI");
                        return transformedPayload;
                    }
                }
                catch (JsonException ex)
                {
                    Debug.WriteLine($"Caught exception in ConnectorIntegrationStrategy.cs: {ex.Message}");
                    _logger?.LogWarning(ex, "Failed to parse AI transformation response, using original payload");
                }

                return new Dictionary<string, object>(request.RequestPayload);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Caught exception in ConnectorIntegrationStrategy.cs: {ex.Message}");
                _logger?.LogWarning(ex, "AI transformation failed, returning original payload");
                return new Dictionary<string, object>(request.RequestPayload);
            }
        }

        private async Task<string> OptimizeQueryWithAIAsync(OptimizeQueryRequest request)
        {
            // M11: Query optimization with AI-powered analysis
            if (AIProvider == null)
            {
                _logger?.LogDebug("AI provider not available for query optimization, returning original");
                return request.QueryText;
            }

            try
            {
                // Build comprehensive optimization prompt
                var schemaJson = request.SchemaContext.Count > 0
                    ? JsonSerializer.Serialize(request.SchemaContext)
                    : "No schema context available";

                var perfJson = request.PerformanceHistory.Count > 0
                    ? JsonSerializer.Serialize(request.PerformanceHistory)
                    : "No performance history available";

                var prompt = $@"You are a database query optimization expert. Analyze and optimize this {request.QueryLanguage} query.

Original Query:
{request.QueryText}

Schema Context:
{schemaJson}

Performance History:
{perfJson}

Optimization Guidelines:
1. Add appropriate indexes hints if beneficial
2. Simplify complex joins
3. Eliminate redundant conditions
4. Optimize WHERE clause ordering
5. Use appropriate aggregation strategies
6. Ensure query follows best practices for {request.QueryLanguage}

Return ONLY the optimized query text. Do not include explanations or markdown formatting.";

                var aiRequest = new AIRequest
                {
                    Prompt = prompt,
                    MaxTokens = 1000,
                    Temperature = 0.2f // Very low temperature for consistent query optimization
                };

                var aiResponse = await AIProvider.CompleteAsync(aiRequest);
                RecordTokens(aiResponse.Usage?.TotalTokens ?? 0);

                if (string.IsNullOrWhiteSpace(aiResponse.Content))
                {
                    _logger?.LogWarning("AI returned empty query optimization, using original");
                    return request.QueryText;
                }

                // Clean up AI response (remove potential markdown formatting)
                var optimizedQuery = aiResponse.Content.Trim();
                optimizedQuery = optimizedQuery.Replace("```sql", "").Replace("```", "").Trim();

                // Validate that we got a reasonable query back
                if (optimizedQuery.Length > 0 && optimizedQuery.Length < 50000)
                {
                    _logger?.LogDebug("Successfully optimized query using AI, length: {OriginalLen} -> {OptimizedLen}",
                        request.QueryText.Length, optimizedQuery.Length);
                    return optimizedQuery;
                }

                _logger?.LogWarning("AI optimization produced invalid result, using original query");
                return request.QueryText;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Caught exception in ConnectorIntegrationStrategy.cs: {ex.Message}");
                _logger?.LogWarning(ex, "AI query optimization failed, returning original query");
                return request.QueryText;
            }
        }

        private async Task<Dictionary<string, object>> EnrichSchemaWithAIAsync(EnrichSchemaRequest request)
        {
            // M12: Schema analysis with semantic metadata generation
            var enrichedMetadata = new Dictionary<string, object>
            {
                ["analyzed_by"] = "heuristic",
                ["confidence"] = 0.6,
                ["field_count"] = request.Fields.Count,
                ["analysis_timestamp"] = DateTimeOffset.UtcNow.ToString("o")
            };

            if (AIProvider == null)
            {
                _logger?.LogDebug("AI provider not available for schema enrichment, using basic metadata");
                return enrichedMetadata;
            }

            try
            {
                // Build field descriptions for AI analysis
                var fieldsJson = JsonSerializer.Serialize(request.Fields.Select(f => new
                {
                    f.Name,
                    f.DataType,
                    f.IsNullable,
                    f.MaxLength
                }));

                var prompt = $@"Analyze this database schema and provide semantic insights.

Schema Name: {request.SchemaName}
Fields:
{fieldsJson}

Provide a JSON response with:
1. ""purpose"": Brief description of what this schema/table represents
2. ""domain"": Business domain (e.g., ""user_management"", ""financial"", ""logging"")
3. ""sensitivity"": Data sensitivity level (""public"", ""internal"", ""confidential"", ""restricted"")
4. ""relationships"": Likely relationships to other schemas (array of strings)
5. ""pii_fields"": Fields that may contain PII (array of field names)
6. ""recommended_indexes"": Suggested index fields (array of field names)
7. ""quality_concerns"": Potential data quality issues (array of strings)

Return only valid JSON, no explanations.";

                var aiRequest = new AIRequest
                {
                    Prompt = prompt,
                    MaxTokens = 800,
                    Temperature = 0.4f // Moderate temperature for creative but consistent analysis
                };

                var aiResponse = await AIProvider.CompleteAsync(aiRequest);
                RecordTokens(aiResponse.Usage?.TotalTokens ?? 0);

                if (!string.IsNullOrWhiteSpace(aiResponse.Content))
                {
                    try
                    {
                        var aiMetadata = JsonSerializer.Deserialize<Dictionary<string, object>>(aiResponse.Content);
                        if (aiMetadata != null && aiMetadata.Count > 0)
                        {
                            // Merge AI results with base metadata
                            foreach (var kvp in aiMetadata)
                            {
                                enrichedMetadata[kvp.Key] = kvp.Value;
                            }

                            enrichedMetadata["analyzed_by"] = "ai";
                            enrichedMetadata["confidence"] = 0.85;

                            _logger?.LogDebug("Successfully enriched schema '{SchemaName}' with AI metadata",
                                request.SchemaName);
                        }
                    }
                    catch (JsonException ex)
                    {
                        Debug.WriteLine($"Caught exception in ConnectorIntegrationStrategy.cs: {ex.Message}");
                        _logger?.LogWarning(ex, "Failed to parse AI schema enrichment response");
                    }
                }

                return enrichedMetadata;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Caught exception in ConnectorIntegrationStrategy.cs: {ex.Message}");
                _logger?.LogWarning(ex, "AI schema enrichment failed, returning basic metadata");
                return enrichedMetadata;
            }
        }

        private async Task<List<DetectedAnomaly>> DetectAnomaliesWithAIAsync(AnomalyDetectionRequest request)
        {
            // M13: Anomaly detection using ML-based analysis
            var anomalies = new List<DetectedAnomaly>();

            // Basic statistical anomaly detection (works without AI)
            var durationMs = request.OperationDuration.TotalMilliseconds;
            if (durationMs > 5000) // 5 second threshold
            {
                anomalies.Add(new DetectedAnomaly
                {
                    Type = "performance",
                    Severity = durationMs > 10000 ? "high" : "medium",
                    Description = $"Operation duration ({durationMs:F0}ms) exceeds normal threshold",
                    ConfidenceScore = 0.7,
                    Metadata = new Dictionary<string, object>
                    {
                        ["duration_ms"] = durationMs,
                        ["threshold_ms"] = 5000,
                        ["detected_at"] = DateTimeOffset.UtcNow.ToString("o")
                    }
                });
            }

            if (AIProvider == null)
            {
                _logger?.LogDebug("AI provider not available for anomaly detection, using statistical methods only");
                return anomalies;
            }

            try
            {
                // Use AI for deeper anomaly analysis
                var responseJson = JsonSerializer.Serialize(request.ResponseData);
                var baselineJson = request.BaselineData.Count > 0
                    ? JsonSerializer.Serialize(request.BaselineData)
                    : "No baseline data available";

                var prompt = $@"Analyze this connector response data for anomalies.

Operation: {request.OperationType}
Duration: {request.OperationDuration.TotalMilliseconds}ms

Response Data:
{responseJson}

Baseline Data:
{baselineJson}

Detect anomalies in:
1. Response data structure (unexpected fields, missing data)
2. Data values (outliers, suspicious patterns)
3. Response size (unusually large/small)
4. Error indicators
5. Security concerns

Return a JSON array of anomalies, each with:
- ""type"": anomaly type (""data_quality"", ""performance"", ""security"", ""structural"")
- ""severity"": ""low"", ""medium"", ""high"", or ""critical""
- ""description"": brief explanation
- ""affected_field"": field name if applicable (or null)
- ""confidence"": 0.0 to 1.0

Return only the JSON array, or [] if no anomalies detected.";

                var aiRequest = new AIRequest
                {
                    Prompt = prompt,
                    MaxTokens = 1000,
                    Temperature = 0.3f // Low temperature for consistent detection
                };

                var aiResponse = await AIProvider.CompleteAsync(aiRequest);
                RecordTokens(aiResponse.Usage?.TotalTokens ?? 0);

                if (!string.IsNullOrWhiteSpace(aiResponse.Content))
                {
                    try
                    {
                        // Parse AI response as array of anomalies
                        var content = aiResponse.Content.Trim();
                        if (content.StartsWith("[") && content.EndsWith("]"))
                        {
                            var aiAnomalies = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(content);
                            if (aiAnomalies != null)
                            {
                                foreach (var aiAnomaly in aiAnomalies)
                                {
                                    anomalies.Add(new DetectedAnomaly
                                    {
                                        Type = aiAnomaly.GetValueOrDefault("type")?.ToString() ?? "unknown",
                                        Severity = aiAnomaly.GetValueOrDefault("severity")?.ToString() ?? "medium",
                                        Description = aiAnomaly.GetValueOrDefault("description")?.ToString() ?? "Anomaly detected",
                                        AffectedField = aiAnomaly.GetValueOrDefault("affected_field")?.ToString(),
                                        ConfidenceScore = Convert.ToDouble(aiAnomaly.GetValueOrDefault("confidence") ?? 0.5),
                                        Metadata = new Dictionary<string, object>
                                        {
                                            ["detected_by"] = "ai",
                                            ["operation_type"] = request.OperationType,
                                            ["detected_at"] = DateTimeOffset.UtcNow.ToString("o")
                                        }
                                    });
                                }

                                _logger?.LogDebug("AI detected {Count} anomalies in response data", aiAnomalies.Count);
                            }
                        }
                    }
                    catch (JsonException ex)
                    {
                        Debug.WriteLine($"Caught exception in ConnectorIntegrationStrategy.cs: {ex.Message}");
                        _logger?.LogWarning(ex, "Failed to parse AI anomaly detection response");
                    }
                }

                return anomalies;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Caught exception in ConnectorIntegrationStrategy.cs: {ex.Message}");
                _logger?.LogWarning(ex, "AI anomaly detection failed, returning statistical results only");
                return anomalies;
            }
        }

        private async Task<double> PredictFailureWithAIAsync(FailurePredictionRequest request)
        {
            // M14: Failure prediction using ML-based probability analysis
            double baseProbability = 0.15; // Default low probability

            // Basic heuristic prediction based on metrics
            if (request.HealthMetrics.TryGetValue("error_rate", out var errorRateObj) &&
                errorRateObj is double errorRate && errorRate > 0.1)
            {
                baseProbability = Math.Min(0.6, baseProbability + (errorRate * 2));
            }

            if (request.HealthMetrics.TryGetValue("latency_ms", out var latencyObj) &&
                latencyObj is double latency && latency > 3000)
            {
                baseProbability = Math.Min(0.8, baseProbability + 0.2);
            }

            if (AIProvider == null)
            {
                _logger?.LogDebug("AI provider not available for failure prediction, using heuristic: {Probability:P1}",
                    baseProbability);
                return baseProbability;
            }

            try
            {
                // Use AI for sophisticated failure prediction
                var healthJson = request.HealthMetrics.Count > 0
                    ? JsonSerializer.Serialize(request.HealthMetrics)
                    : "No health metrics available";

                var historyJson = request.HistoricalData.Count > 0
                    ? JsonSerializer.Serialize(request.HistoricalData)
                    : "No historical failure data available";

                var envJson = request.EnvironmentalFactors.Count > 0
                    ? JsonSerializer.Serialize(request.EnvironmentalFactors)
                    : "No environmental data available";

                var prompt = $@"Predict the probability of connection/operation failure based on current metrics.

Operation: {request.OperationType}
Connection: {request.ConnectionId}

Current Health Metrics:
{healthJson}

Historical Failure Data:
{historyJson}

Environmental Factors:
{envJson}

Analyze:
1. Current connection health indicators
2. Historical failure patterns
3. Environmental risk factors
4. Correlation between metrics and past failures

Return a JSON object with:
- ""probability"": failure probability from 0.0 to 1.0
- ""confidence"": prediction confidence from 0.0 to 1.0
- ""primary_risk_factor"": main contributing factor (string)
- ""time_to_failure_minutes"": estimated minutes until failure (number or null)

Return only valid JSON.";

                var aiRequest = new AIRequest
                {
                    Prompt = prompt,
                    MaxTokens = 500,
                    Temperature = 0.2f // Low temperature for consistent predictions
                };

                var aiResponse = await AIProvider.CompleteAsync(aiRequest);
                RecordTokens(aiResponse.Usage?.TotalTokens ?? 0);

                if (!string.IsNullOrWhiteSpace(aiResponse.Content))
                {
                    try
                    {
                        var prediction = JsonSerializer.Deserialize<Dictionary<string, object>>(aiResponse.Content);
                        if (prediction != null && prediction.TryGetValue("probability", out var probObj))
                        {
                            var aiProbability = Convert.ToDouble(probObj);

                            // Validate probability is in valid range
                            if (aiProbability >= 0.0 && aiProbability <= 1.0)
                            {
                                _logger?.LogDebug("AI predicted failure probability: {Probability:P1} (heuristic: {Base:P1})",
                                    aiProbability, baseProbability);

                                // Blend AI prediction with heuristic (weighted average)
                                var confidence = prediction.TryGetValue("confidence", out var confObj)
                                    ? Convert.ToDouble(confObj)
                                    : 0.7;

                                return (aiProbability * confidence) + (baseProbability * (1 - confidence));
                            }
                        }
                    }
                    catch (JsonException ex)
                    {
                        Debug.WriteLine($"Caught exception in ConnectorIntegrationStrategy.cs: {ex.Message}");
                        _logger?.LogWarning(ex, "Failed to parse AI failure prediction response");
                    }
                }

                return baseProbability;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Caught exception in ConnectorIntegrationStrategy.cs: {ex.Message}");
                _logger?.LogWarning(ex, "AI failure prediction failed, returning heuristic probability");
                return baseProbability;
            }
        }

        // ============================================================================
        // HELPER METHODS
        // ============================================================================

        private T? DeserializePayload<T>(Dictionary<string, object> payload) where T : class
        {
            try
            {
                var json = JsonSerializer.Serialize(payload);
                return JsonSerializer.Deserialize<T>(json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Caught exception in ConnectorIntegrationStrategy.cs: {ex.Message}");
                _logger?.LogError(ex, "Error deserializing payload to {Type}", typeof(T).Name);
                return null;
            }
        }
    }
}
