namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.ConnectorIntegration
{
    /// <summary>
    /// Message bus topics for intelligence services integration with connectors.
    /// Supports both async (fire-and-forget) and sync (request/response) modes.
    /// </summary>
    public static class IntelligenceTopics
    {
        // === CONNECTOR INTEGRATION ===

        /// <summary>Request transformation of connector payloads (sync mode).</summary>
        public const string TransformRequest = "intelligence.connector.transform-request";
        public const string TransformResponse = "intelligence.connector.transform-response";

        /// <summary>Optimize queries before execution.</summary>
        public const string OptimizeQuery = "intelligence.connector.optimize-query";
        public const string OptimizeQueryResponse = "intelligence.connector.optimize-query.response";

        /// <summary>Enrich schema with semantic metadata.</summary>
        public const string EnrichSchema = "intelligence.connector.enrich-schema";
        public const string EnrichSchemaResponse = "intelligence.connector.enrich-schema.response";

        /// <summary>Detect anomalies in responses.</summary>
        public const string DetectAnomaly = "intelligence.connector.detect-anomaly";
        public const string DetectAnomalyResponse = "intelligence.connector.detect-anomaly.response";

        /// <summary>Predict connection/operation failures.</summary>
        public const string PredictFailure = "intelligence.connector.predict-failure";
        public const string PredictFailureResponse = "intelligence.connector.predict-failure.response";

        // === ASYNC OBSERVATION TOPICS (from MessageBusInterceptorBridge) ===

        /// <summary>Observe connector before-request events (async).</summary>
        public const string ObserveBeforeRequest = "connector.interceptor.before-request";

        /// <summary>Observe connector after-response events (async).</summary>
        public const string ObserveAfterResponse = "connector.interceptor.after-response";

        /// <summary>Observe schema discovery events (async).</summary>
        public const string ObserveSchemaDiscovered = "connector.interceptor.on-schema";

        /// <summary>Observe error events (async).</summary>
        public const string ObserveError = "connector.interceptor.on-error";

        /// <summary>Observe connection establishment events (async).</summary>
        public const string ObserveConnectionEstablished = "connector.interceptor.on-connect";
    }
}
