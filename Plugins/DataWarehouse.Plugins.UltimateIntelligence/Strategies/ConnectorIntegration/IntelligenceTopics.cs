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

        // === LONG-TERM MEMORY ===

        /// <summary>Store a memory.</summary>
        public const string MemoryStore = "intelligence.memory.store";
        public const string MemoryStoreResponse = "intelligence.memory.store.response";

        /// <summary>Recall memories based on query.</summary>
        public const string MemoryRecall = "intelligence.memory.recall";
        public const string MemoryRecallResponse = "intelligence.memory.recall.response";

        /// <summary>Trigger memory consolidation.</summary>
        public const string MemoryConsolidate = "intelligence.memory.consolidate";
        public const string MemoryConsolidateResponse = "intelligence.memory.consolidate.response";

        // === TABULAR MODELS ===

        /// <summary>Train a tabular model.</summary>
        public const string TabularTrain = "intelligence.tabular.train";
        public const string TabularTrainResponse = "intelligence.tabular.train.response";

        /// <summary>Make predictions with a tabular model.</summary>
        public const string TabularPredict = "intelligence.tabular.predict";
        public const string TabularPredictResponse = "intelligence.tabular.predict.response";

        /// <summary>Explain tabular model predictions.</summary>
        public const string TabularExplain = "intelligence.tabular.explain";
        public const string TabularExplainResponse = "intelligence.tabular.explain.response";

        // === AI AGENTS ===

        /// <summary>Execute an agent task.</summary>
        public const string AgentExecute = "intelligence.agent.execute";
        public const string AgentExecuteResponse = "intelligence.agent.execute.response";

        /// <summary>Register a tool for agent use.</summary>
        public const string AgentRegisterTool = "intelligence.agent.register-tool";
        public const string AgentRegisterToolResponse = "intelligence.agent.register-tool.response";

        /// <summary>Get current agent state.</summary>
        public const string AgentState = "intelligence.agent.state";
        public const string AgentStateResponse = "intelligence.agent.state.response";

        // === EVOLVING INTELLIGENCE ===

        /// <summary>Record learning interactions.</summary>
        public const string EvolutionLearn = "intelligence.evolution.learn";
        public const string EvolutionLearnResponse = "intelligence.evolution.learn.response";

        /// <summary>Get expertise score for a domain.</summary>
        public const string EvolutionExpertise = "intelligence.evolution.expertise";
        public const string EvolutionExpertiseResponse = "intelligence.evolution.expertise.response";

        /// <summary>Trigger adaptive behavior adjustment.</summary>
        public const string EvolutionAdapt = "intelligence.evolution.adapt";
        public const string EvolutionAdaptResponse = "intelligence.evolution.adapt.response";
    }
}
