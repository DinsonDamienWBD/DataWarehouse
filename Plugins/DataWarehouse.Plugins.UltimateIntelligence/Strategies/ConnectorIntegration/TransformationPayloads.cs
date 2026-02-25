using System;
using System.Collections.Generic;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.ConnectorIntegration
{
    // ============================================================================
    // TRANSFORM REQUEST/RESPONSE PAYLOADS
    // ============================================================================

    /// <summary>
    /// Request payload for general transformation operations.
    /// Sent from connector to intelligence service via message bus.
    /// </summary>
    public sealed record TransformRequest
    {
        /// <summary>Strategy identifier processing the request.</summary>
        public required string StrategyId { get; init; }

        /// <summary>Connection identifier.</summary>
        public required string ConnectionId { get; init; }

        /// <summary>Type of operation (connect, query, write, etc.).</summary>
        public required string OperationType { get; init; }

        /// <summary>Request payload to be transformed.</summary>
        public required Dictionary<string, object> RequestPayload { get; init; }

        /// <summary>Additional metadata for context.</summary>
        public Dictionary<string, object> Metadata { get; init; } = new();

        /// <summary>Timestamp when request was initiated.</summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Response payload for transformation operations.
    /// Returned from intelligence service to connector via message bus.
    /// </summary>
    public sealed record TransformResponse
    {
        /// <summary>Whether the transformation was successful.</summary>
        public required bool Success { get; init; }

        /// <summary>Transformed request payload (if successful).</summary>
        public Dictionary<string, object> TransformedPayload { get; init; } = new();

        /// <summary>Error message if transformation failed.</summary>
        public string? ErrorMessage { get; init; }

        /// <summary>Time spent processing the transformation.</summary>
        public TimeSpan ProcessingTime { get; init; }

        /// <summary>Additional metadata from transformation process.</summary>
        public Dictionary<string, object> Metadata { get; init; } = new();

        /// <summary>AI-generated explanation of what was transformed and why.</summary>
        public string? Explanation { get; init; }
    }

    // ============================================================================
    // QUERY OPTIMIZATION PAYLOADS
    // ============================================================================

    /// <summary>
    /// Request to optimize a query before execution.
    /// </summary>
    public sealed record OptimizeQueryRequest
    {
        /// <summary>Strategy identifier processing the query.</summary>
        public required string StrategyId { get; init; }

        /// <summary>Connection identifier.</summary>
        public required string ConnectionId { get; init; }

        /// <summary>Original query text (SQL, Cypher, MongoDB query, etc.).</summary>
        public required string QueryText { get; init; }

        /// <summary>Query language type (sql, cypher, mongo, etc.).</summary>
        public string QueryLanguage { get; init; } = "sql";

        /// <summary>Query parameters if parameterized.</summary>
        public Dictionary<string, object> Parameters { get; init; } = new();

        /// <summary>Schema context for optimization.</summary>
        public Dictionary<string, object> SchemaContext { get; init; } = new();

        /// <summary>Historical performance data for similar queries.</summary>
        public Dictionary<string, object> PerformanceHistory { get; init; } = new();

        /// <summary>Timestamp of optimization request.</summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Response with optimized query.
    /// </summary>
    public sealed record OptimizeQueryResponse
    {
        /// <summary>Whether optimization was successful.</summary>
        public required bool Success { get; init; }

        /// <summary>Optimized query text.</summary>
        public string? OptimizedQuery { get; init; }

        /// <summary>Whether the optimized query differs from original.</summary>
        public bool WasModified { get; init; }

        /// <summary>Confidence score of optimization (0.0-1.0).</summary>
        public double ConfidenceScore { get; init; }

        /// <summary>Predicted performance improvement percentage.</summary>
        public double? PredictedImprovement { get; init; }

        /// <summary>List of optimizations applied.</summary>
        public List<string> OptimizationsApplied { get; init; } = new();

        /// <summary>AI-generated explanation of optimizations.</summary>
        public string? Explanation { get; init; }

        /// <summary>Error message if optimization failed.</summary>
        public string? ErrorMessage { get; init; }

        /// <summary>Time spent optimizing.</summary>
        public TimeSpan ProcessingTime { get; init; }
    }

    // ============================================================================
    // SCHEMA ENRICHMENT PAYLOADS
    // ============================================================================

    /// <summary>
    /// Request to enrich schema with semantic metadata.
    /// </summary>
    public sealed record EnrichSchemaRequest
    {
        /// <summary>Strategy identifier that discovered the schema.</summary>
        public required string StrategyId { get; init; }

        /// <summary>Connection identifier.</summary>
        public required string ConnectionId { get; init; }

        /// <summary>Schema name (table, collection, topic).</summary>
        public required string SchemaName { get; init; }

        /// <summary>Field definitions discovered.</summary>
        public required List<SchemaFieldDefinition> Fields { get; init; }

        /// <summary>Existing metadata if any.</summary>
        public Dictionary<string, object> ExistingMetadata { get; init; } = new();

        /// <summary>Timestamp of enrichment request.</summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Field definition for schema enrichment.
    /// </summary>
    public sealed record SchemaFieldDefinition
    {
        /// <summary>Field name.</summary>
        public required string Name { get; init; }

        /// <summary>Data type.</summary>
        public required string DataType { get; init; }

        /// <summary>Whether field is nullable.</summary>
        public bool IsNullable { get; init; } = true;

        /// <summary>Maximum length for string/binary fields.</summary>
        public int? MaxLength { get; init; }

        /// <summary>Existing metadata for this field.</summary>
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    /// <summary>
    /// Response with enriched schema metadata.
    /// </summary>
    public sealed record EnrichSchemaResponse
    {
        /// <summary>Whether enrichment was successful.</summary>
        public required bool Success { get; init; }

        /// <summary>Enriched metadata for the schema.</summary>
        public Dictionary<string, object> EnrichedMetadata { get; init; } = new();

        /// <summary>Enriched metadata per field.</summary>
        public Dictionary<string, FieldEnrichment> FieldEnrichments { get; init; } = new();

        /// <summary>Detected semantic categories (e.g., "personal_data", "financial").</summary>
        public List<string> SemanticCategories { get; init; } = new();

        /// <summary>Suggested indexes based on AI analysis.</summary>
        public List<string> SuggestedIndexes { get; init; } = new();

        /// <summary>Data quality issues detected.</summary>
        public List<string> QualityIssues { get; init; } = new();

        /// <summary>AI-generated schema description.</summary>
        public string? SchemaDescription { get; init; }

        /// <summary>Error message if enrichment failed.</summary>
        public string? ErrorMessage { get; init; }

        /// <summary>Time spent enriching.</summary>
        public TimeSpan ProcessingTime { get; init; }
    }

    /// <summary>
    /// Enrichment data for a single field.
    /// </summary>
    public sealed record FieldEnrichment
    {
        /// <summary>AI-generated field description.</summary>
        public string? Description { get; init; }

        /// <summary>Semantic type (email, phone, ssn, etc.).</summary>
        public string? SemanticType { get; init; }

        /// <summary>Detected PII classification.</summary>
        public string? PiiClassification { get; init; }

        /// <summary>Suggested validation rules.</summary>
        public List<string> ValidationRules { get; init; } = new();

        /// <summary>Example values if available.</summary>
        public List<string> ExampleValues { get; init; } = new();

        /// <summary>Additional enrichment metadata.</summary>
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    // ============================================================================
    // ANOMALY DETECTION PAYLOADS
    // ============================================================================

    /// <summary>
    /// Request to detect anomalies in response data.
    /// </summary>
    public sealed record AnomalyDetectionRequest
    {
        /// <summary>Strategy identifier.</summary>
        public required string StrategyId { get; init; }

        /// <summary>Connection identifier.</summary>
        public required string ConnectionId { get; init; }

        /// <summary>Operation type that produced the response.</summary>
        public required string OperationType { get; init; }

        /// <summary>Response data to analyze for anomalies.</summary>
        public required Dictionary<string, object> ResponseData { get; init; }

        /// <summary>Duration of the operation.</summary>
        public TimeSpan OperationDuration { get; init; }

        /// <summary>Historical baseline data for comparison.</summary>
        public Dictionary<string, object> BaselineData { get; init; } = new();

        /// <summary>Timestamp of detection request.</summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Response with detected anomalies.
    /// </summary>
    public sealed record AnomalyDetectionResponse
    {
        /// <summary>Whether analysis was successful.</summary>
        public required bool Success { get; init; }

        /// <summary>Whether anomalies were detected.</summary>
        public bool AnomaliesDetected { get; init; }

        /// <summary>Confidence score that anomalies are real (0.0-1.0).</summary>
        public double ConfidenceScore { get; init; }

        /// <summary>List of detected anomalies.</summary>
        public List<DetectedAnomaly> Anomalies { get; init; } = new();

        /// <summary>Overall anomaly severity (low, medium, high, critical).</summary>
        public string? Severity { get; init; }

        /// <summary>AI-generated summary of findings.</summary>
        public string? Summary { get; init; }

        /// <summary>Recommended actions.</summary>
        public List<string> RecommendedActions { get; init; } = new();

        /// <summary>Error message if detection failed.</summary>
        public string? ErrorMessage { get; init; }

        /// <summary>Time spent on detection.</summary>
        public TimeSpan ProcessingTime { get; init; }
    }

    /// <summary>
    /// Details of a detected anomaly.
    /// </summary>
    public sealed record DetectedAnomaly
    {
        /// <summary>Type of anomaly (performance, data_quality, security, etc.).</summary>
        public required string Type { get; init; }

        /// <summary>Severity level.</summary>
        public required string Severity { get; init; }

        /// <summary>Description of the anomaly.</summary>
        public required string Description { get; init; }

        /// <summary>Specific field or metric affected.</summary>
        public string? AffectedField { get; init; }

        /// <summary>Expected value or range.</summary>
        public string? ExpectedValue { get; init; }

        /// <summary>Actual value observed.</summary>
        public string? ActualValue { get; init; }

        /// <summary>Confidence score (0.0-1.0).</summary>
        public double ConfidenceScore { get; init; }

        /// <summary>Additional context metadata.</summary>
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    // ============================================================================
    // FAILURE PREDICTION PAYLOADS
    // ============================================================================

    /// <summary>
    /// Request to predict connection/operation failures.
    /// </summary>
    public sealed record FailurePredictionRequest
    {
        /// <summary>Strategy identifier.</summary>
        public required string StrategyId { get; init; }

        /// <summary>Connection identifier.</summary>
        public required string ConnectionId { get; init; }

        /// <summary>Operation type to predict for.</summary>
        public required string OperationType { get; init; }

        /// <summary>Current connection health metrics.</summary>
        public Dictionary<string, object> HealthMetrics { get; init; } = new();

        /// <summary>Historical failure data.</summary>
        public Dictionary<string, object> HistoricalData { get; init; } = new();

        /// <summary>Environmental factors (network conditions, load, etc.).</summary>
        public Dictionary<string, object> EnvironmentalFactors { get; init; } = new();

        /// <summary>Timestamp of prediction request.</summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Response with failure prediction.
    /// </summary>
    public sealed record FailurePredictionResponse
    {
        /// <summary>Whether analysis was successful.</summary>
        public required bool Success { get; init; }

        /// <summary>Probability of failure (0.0-1.0).</summary>
        public double FailureProbability { get; init; }

        /// <summary>Predicted time until failure (if applicable).</summary>
        public TimeSpan? PredictedTimeToFailure { get; init; }

        /// <summary>Most likely failure types.</summary>
        public List<PredictedFailureType> LikelyFailures { get; init; } = new();

        /// <summary>Risk level (low, medium, high, critical).</summary>
        public string RiskLevel { get; init; } = "low";

        /// <summary>Contributing risk factors.</summary>
        public List<string> RiskFactors { get; init; } = new();

        /// <summary>Recommended preventive actions.</summary>
        public List<string> PreventiveActions { get; init; } = new();

        /// <summary>AI-generated explanation.</summary>
        public string? Explanation { get; init; }

        /// <summary>Error message if prediction failed.</summary>
        public string? ErrorMessage { get; init; }

        /// <summary>Time spent on prediction.</summary>
        public TimeSpan ProcessingTime { get; init; }
    }

    /// <summary>
    /// Predicted failure type with probability.
    /// </summary>
    public sealed record PredictedFailureType
    {
        /// <summary>Type of failure (timeout, connection_loss, auth_failure, etc.).</summary>
        public required string FailureType { get; init; }

        /// <summary>Probability of this specific failure (0.0-1.0).</summary>
        public required double Probability { get; init; }

        /// <summary>Description of the failure scenario.</summary>
        public string? Description { get; init; }

        /// <summary>Estimated impact if failure occurs.</summary>
        public string? EstimatedImpact { get; init; }
    }
}
