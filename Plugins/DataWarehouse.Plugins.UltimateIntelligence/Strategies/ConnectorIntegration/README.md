# Connector Integration for UltimateIntelligence

This directory contains the connector integration handlers that bridge the **T90 UltimateIntelligence** plugin with the **T89 UltimateConnector** plugin via the message bus.

## Overview

The connector integration provides AI-powered features for database and service connectors:
- **Query Optimization**: Use AI to analyze and optimize SQL/NoSQL queries
- **Schema Enrichment**: Add semantic metadata to discovered schemas
- **Anomaly Detection**: Detect unusual patterns in response data
- **Failure Prediction**: Predict connection/operation failures before they occur
- **Request Transformation**: Intelligently transform connector payloads

## Architecture

### Integration Modes

Three integration modes are supported:

1. **AsyncObservation** (Default)
   - T90 subscribes to connector events (before-request, after-response, schema-discovered, error, connection-established)
   - Processes events asynchronously without blocking the connector pipeline
   - Good for: Analytics, logging, pattern detection, anomaly detection
   - Zero latency impact on connectors

2. **SyncTransformation**
   - Connector sends transformation requests to T90 and waits for response
   - T90 processes requests synchronously and returns transformed data
   - Good for: Query optimization, schema enrichment, request transformation
   - Adds latency but provides intelligent transformation

3. **Hybrid**
   - Both async observation AND sync transformation enabled
   - Best flexibility but highest resource usage

### Message Bus Topics

#### Async Observation Topics (Published by Connector)
- `connector.interceptor.before-request` - Before each connection operation
- `connector.interceptor.after-response` - After each connection operation
- `connector.interceptor.on-schema` - When schema is discovered
- `connector.interceptor.on-error` - When errors occur
- `connector.interceptor.on-connect` - When connections are established

#### Sync Request Topics (Connector → Intelligence)
- `intelligence.connector.transform-request` - General transformation requests
- `intelligence.connector.optimize-query` - Query optimization requests
- `intelligence.connector.enrich-schema` - Schema enrichment requests
- `intelligence.connector.detect-anomaly` - Anomaly detection requests
- `intelligence.connector.predict-failure` - Failure prediction requests

#### Sync Response Topics (Intelligence → Connector)
- `intelligence.connector.transform-response` - Transformation results
- `intelligence.connector.optimize-query.response` - Optimized queries
- `intelligence.connector.enrich-schema.response` - Enriched schemas
- `intelligence.connector.detect-anomaly.response` - Detected anomalies
- `intelligence.connector.predict-failure.response` - Failure predictions

## Files

### IntelligenceTopics.cs
Defines all message bus topic constants for connector-intelligence integration.

### ConnectorIntegrationMode.cs
Enum defining the three integration modes: Disabled, AsyncObservation, SyncTransformation, Hybrid.

### TransformationPayloads.cs
Request/response payload classes for all intelligence operations:
- TransformRequest/TransformResponse
- OptimizeQueryRequest/OptimizeQueryResponse
- EnrichSchemaRequest/EnrichSchemaResponse
- AnomalyDetectionRequest/AnomalyDetectionResponse
- FailurePredictionRequest/FailurePredictionResponse

### ConnectorIntegrationStrategy.cs
The main strategy class that handles message bus subscriptions and processes intelligence requests.

## Usage

### In UltimateIntelligencePlugin.cs

```csharp
using DataWarehouse.Plugins.UltimateIntelligence.Strategies.ConnectorIntegration;

public override async Task StartAsync(CancellationToken ct)
{
    // ... other initialization ...

    // Initialize connector integration
    var integrationMode = GetConnectorIntegrationMode(); // From configuration
    if (integrationMode != ConnectorIntegrationMode.Disabled)
    {
        var integrationStrategy = new ConnectorIntegrationStrategy(_messageBus, _logger);
        await integrationStrategy.InitializeAsync(integrationMode, ct);

        // Optionally set AI providers for intelligent processing
        integrationStrategy.SetAIProvider(GetActiveAIProvider());
        integrationStrategy.SetVectorStore(GetActiveVectorStore());
    }
}

private ConnectorIntegrationMode GetConnectorIntegrationMode()
{
    var mode = _configuration.GetValue<string>("Intelligence:ConnectorIntegration:Mode", "AsyncObservation");
    return Enum.Parse<ConnectorIntegrationMode>(mode, ignoreCase: true);
}
```

### Configuration Example

```json
{
  "Intelligence": {
    "ConnectorIntegration": {
      "Mode": "Hybrid",
      "TransformTimeout": 5
    }
  }
}
```

## How It Works

### Async Observation Flow
1. Connector executes operation (e.g., SQL query)
2. Connector's MessageBusInterceptorBridge publishes event to `connector.interceptor.after-response`
3. T90's ConnectorIntegrationStrategy receives event asynchronously
4. T90 processes event (logs analytics, detects patterns, updates ML models)
5. Connector is NOT blocked - continues execution

### Sync Transformation Flow
1. Connector prepares to execute query
2. Connector sends request to `intelligence.connector.optimize-query` topic
3. T90's ConnectorIntegrationStrategy receives request
4. T90 uses AI to analyze and optimize query
5. T90 responds with optimized query via `intelligence.connector.optimize-query.response`
6. Connector receives optimized query and executes it
7. Connector waits for response - adds latency but improves query

## AI Integration Points

The ConnectorIntegrationStrategy includes placeholder methods for AI integration:

- **TransformPayloadWithAIAsync**: Use AI to intelligently transform request payloads
- **OptimizeQueryWithAIAsync**: Use LLM to analyze and optimize SQL/NoSQL queries
- **EnrichSchemaWithAIAsync**: Use AI to classify schemas and detect PII
- **DetectAnomaliesWithAIAsync**: Use ML models to detect anomalies in response data
- **PredictFailureWithAIAsync**: Use ML models to predict operation failures

These methods can leverage the configured AI providers (OpenAI, Claude, Azure OpenAI, etc.) and vector stores for intelligent processing.

## Example Use Cases

### Query Optimization
```csharp
// Connector sends unoptimized query
var request = new OptimizeQueryRequest
{
    StrategyId = "postgres-primary",
    ConnectionId = "conn-123",
    QueryText = "SELECT * FROM users WHERE email LIKE '%@gmail.com'",
    QueryLanguage = "sql"
};

// T90 responds with optimized query
var response = new OptimizeQueryResponse
{
    Success = true,
    OptimizedQuery = "SELECT * FROM users WHERE email_domain = 'gmail.com' USE INDEX(idx_email_domain)",
    WasModified = true,
    OptimizationsApplied = new[] { "Converted LIKE to equality", "Added index hint" },
    PredictedImprovement = 87.5 // 87.5% faster
};
```

### Anomaly Detection
```csharp
// Connector sends response data for analysis
var request = new AnomalyDetectionRequest
{
    StrategyId = "mongodb-replica",
    ConnectionId = "conn-456",
    OperationType = "query",
    ResponseData = responsePayload,
    OperationDuration = TimeSpan.FromMilliseconds(3500)
};

// T90 detects anomalies
var response = new AnomalyDetectionResponse
{
    Success = true,
    AnomaliesDetected = true,
    Anomalies = new[]
    {
        new DetectedAnomaly
        {
            Type = "performance",
            Severity = "high",
            Description = "Query duration 7x higher than baseline",
            ExpectedValue = "500ms",
            ActualValue = "3500ms",
            ConfidenceScore = 0.92
        }
    }
};
```

### Schema Enrichment
```csharp
// Connector discovers schema
var request = new EnrichSchemaRequest
{
    SchemaName = "users",
    Fields = new[]
    {
        new SchemaFieldDefinition { Name = "email", DataType = "varchar" },
        new SchemaFieldDefinition { Name = "ssn", DataType = "varchar" }
    }
};

// T90 enriches with semantic metadata
var response = new EnrichSchemaResponse
{
    Success = true,
    SemanticCategories = new[] { "personal_data", "pii_sensitive" },
    FieldEnrichments = new Dictionary<string, FieldEnrichment>
    {
        ["email"] = new() { SemanticType = "email", PiiClassification = "low" },
        ["ssn"] = new() { SemanticType = "ssn", PiiClassification = "high" }
    },
    SuggestedIndexes = new[] { "CREATE INDEX idx_email ON users(email)" }
};
```

## Performance Considerations

- **AsyncObservation**: Zero latency impact, fire-and-forget
- **SyncTransformation**: Adds 10-500ms latency depending on AI processing
- **Timeout Configuration**: Default 5 seconds, configurable via TransformTimeout
- **Resource Usage**: Hybrid mode uses more CPU/memory but provides full capabilities

## Future Enhancements

- [ ] Adaptive caching for optimized queries
- [ ] Real-time ML model training from observed patterns
- [ ] Automatic index recommendation based on query patterns
- [ ] Multi-provider consensus for critical transformations
- [ ] Knowledge graph integration for schema relationships
- [ ] Psychometric analysis of data access patterns
