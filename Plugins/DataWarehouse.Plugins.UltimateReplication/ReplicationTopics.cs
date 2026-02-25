namespace DataWarehouse.Plugins.UltimateReplication
{
    /// <summary>
    /// Message bus topics for replication operations and Intelligence integration.
    /// Defines the protocol for replication-related communication including
    /// conflict prediction, strategy selection, and AI-enhanced optimization.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The replication communication protocol uses three categories of topics:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Core Operations</b>: Topics for standard replication operations</item>
    ///   <item><b>Intelligence Requests</b>: Topics for AI-enhanced replication features</item>
    ///   <item><b>Events</b>: Topics for replication events and notifications</item>
    /// </list>
    /// <para>
    /// All topics follow the naming convention: <c>replication.ultimate.[category].[operation]</c>
    /// </para>
    /// </remarks>
    public static class ReplicationTopics
    {
        // ========================================
        // Namespace Prefix
        // ========================================

        /// <summary>
        /// Base prefix for all Ultimate Replication topics.
        /// </summary>
        public const string Prefix = "replication.ultimate";

        // ========================================
        // Core Replication Operations
        // ========================================

        /// <summary>
        /// Topic for initiating replication operations.
        /// </summary>
        /// <remarks>
        /// Request payload:
        /// <list type="bullet">
        ///   <item><c>sourceNode</c>: Source node ID</item>
        ///   <item><c>targetNodes</c>: Array of target node IDs</item>
        ///   <item><c>data</c>: Data payload (base64 encoded)</item>
        ///   <item><c>metadata</c>: Optional metadata dictionary</item>
        /// </list>
        /// Response:
        /// <list type="bullet">
        ///   <item><c>success</c>: Boolean indicating success</item>
        ///   <item><c>replicationId</c>: Unique replication operation ID</item>
        ///   <item><c>targetNodes</c>: Nodes that received the data</item>
        /// </list>
        /// </remarks>
        public const string Replicate = $"{Prefix}.replicate";

        /// <summary>
        /// Topic for synchronizing data across nodes.
        /// </summary>
        public const string Sync = $"{Prefix}.sync";

        /// <summary>
        /// Topic for triggering anti-entropy synchronization.
        /// </summary>
        public const string AntiEntropy = $"{Prefix}.anti-entropy";

        /// <summary>
        /// Topic for requesting replication status.
        /// </summary>
        public const string Status = $"{Prefix}.status";

        // ========================================
        // Strategy Selection and Management
        // ========================================

        /// <summary>
        /// Topic for selecting a replication strategy.
        /// </summary>
        /// <remarks>
        /// Request payload:
        /// <list type="bullet">
        ///   <item><c>strategy</c>: Strategy name (e.g., "CRDT", "MultiMaster")</item>
        ///   <item><c>autoSelect</c>: If true, let Intelligence select optimal strategy</item>
        ///   <item><c>requirements</c>: Optional requirements for auto-selection</item>
        /// </list>
        /// </remarks>
        public const string SelectStrategy = $"{Prefix}.select";

        /// <summary>
        /// Topic for listing available strategies.
        /// </summary>
        public const string ListStrategies = $"{Prefix}.strategies.list";

        /// <summary>
        /// Topic for getting strategy information.
        /// </summary>
        public const string StrategyInfo = $"{Prefix}.strategies.info";

        /// <summary>
        /// Topic for requesting Intelligence to recommend optimal strategy.
        /// </summary>
        public const string RecommendStrategy = $"{Prefix}.strategies.recommend";

        // ========================================
        // Conflict Management
        // ========================================

        /// <summary>
        /// Topic for conflict detection events.
        /// Published when a replication conflict is detected.
        /// </summary>
        /// <remarks>
        /// Event payload:
        /// <list type="bullet">
        ///   <item><c>conflictId</c>: Unique conflict identifier</item>
        ///   <item><c>dataId</c>: Data identifier in conflict</item>
        ///   <item><c>localVersion</c>: Local vector clock</item>
        ///   <item><c>remoteVersion</c>: Remote vector clock</item>
        ///   <item><c>localNodeId</c>: Local node ID</item>
        ///   <item><c>remoteNodeId</c>: Remote node ID</item>
        ///   <item><c>detectedAt</c>: Timestamp of detection</item>
        /// </list>
        /// </remarks>
        public const string Conflict = $"{Prefix}.conflict";

        /// <summary>
        /// Topic for conflict resolution events.
        /// Published when a conflict has been resolved.
        /// </summary>
        public const string ConflictResolved = $"{Prefix}.conflict.resolved";

        /// <summary>
        /// Topic for requesting conflict resolution.
        /// </summary>
        public const string ConflictResolve = $"{Prefix}.conflict.resolve";

        // ========================================
        // Lag Monitoring
        // ========================================

        /// <summary>
        /// Topic for replication lag measurements.
        /// Published periodically with lag statistics.
        /// </summary>
        /// <remarks>
        /// Event payload:
        /// <list type="bullet">
        ///   <item><c>sourceNode</c>: Source node ID</item>
        ///   <item><c>targetNode</c>: Target node ID</item>
        ///   <item><c>lagMs</c>: Current lag in milliseconds</item>
        ///   <item><c>maxLagMs</c>: Maximum observed lag</item>
        ///   <item><c>avgLagMs</c>: Average lag</item>
        ///   <item><c>timestamp</c>: Measurement timestamp</item>
        /// </list>
        /// </remarks>
        public const string Lag = $"{Prefix}.lag";

        /// <summary>
        /// Topic for requesting current lag status.
        /// </summary>
        public const string LagRequest = $"{Prefix}.lag.request";

        /// <summary>
        /// Topic for lag threshold violation alerts.
        /// </summary>
        public const string LagAlert = $"{Prefix}.lag.alert";

        // ========================================
        // Health and Diagnostics
        // ========================================

        /// <summary>
        /// Topic for replication health status.
        /// Published periodically with health metrics.
        /// </summary>
        public const string Health = $"{Prefix}.health";

        /// <summary>
        /// Topic for replication metrics and statistics.
        /// </summary>
        public const string Metrics = $"{Prefix}.metrics";

        /// <summary>
        /// Topic for node connectivity status.
        /// </summary>
        public const string NodeStatus = $"{Prefix}.node.status";

        // ========================================
        // Intelligence-Enhanced Features
        // ========================================

        /// <summary>
        /// Topic for requesting conflict prediction via Intelligence.
        /// </summary>
        /// <remarks>
        /// Request payload:
        /// <list type="bullet">
        ///   <item><c>sourceNode</c>: Source node ID</item>
        ///   <item><c>targetNodes</c>: Target node IDs</item>
        ///   <item><c>dataPattern</c>: Data access pattern information</item>
        ///   <item><c>historicalConflicts</c>: Past conflict data</item>
        /// </list>
        /// Response:
        /// <list type="bullet">
        ///   <item><c>conflictProbability</c>: Predicted probability (0.0-1.0)</item>
        ///   <item><c>riskFactors</c>: Array of identified risk factors</item>
        ///   <item><c>recommendations</c>: Suggested mitigation strategies</item>
        /// </list>
        /// </remarks>
        public const string PredictConflict = $"{Prefix}.predict.conflict";

        /// <summary>
        /// Response topic for conflict prediction requests.
        /// </summary>
        public const string PredictConflictResponse = $"{Prefix}.predict.conflict.response";

        /// <summary>
        /// Topic for requesting optimal consistency model via Intelligence.
        /// </summary>
        /// <remarks>
        /// Request payload:
        /// <list type="bullet">
        ///   <item><c>dataType</c>: Type of data being replicated</item>
        ///   <item><c>accessPattern</c>: Read/write pattern</item>
        ///   <item><c>latencyRequirements</c>: Maximum acceptable latency</item>
        ///   <item><c>consistencyPreference</c>: Preferred consistency level</item>
        /// </list>
        /// Response:
        /// <list type="bullet">
        ///   <item><c>recommendedModel</c>: Recommended ConsistencyModel</item>
        ///   <item><c>confidence</c>: Confidence score (0.0-1.0)</item>
        ///   <item><c>reasoning</c>: Explanation for recommendation</item>
        /// </list>
        /// </remarks>
        public const string OptimizeConsistency = $"{Prefix}.optimize.consistency";

        /// <summary>
        /// Response topic for consistency optimization requests.
        /// </summary>
        public const string OptimizeConsistencyResponse = $"{Prefix}.optimize.consistency.response";

        /// <summary>
        /// Topic for requesting AI-based replica routing decisions.
        /// </summary>
        /// <remarks>
        /// Request payload:
        /// <list type="bullet">
        ///   <item><c>availableReplicas</c>: Array of available replica node IDs</item>
        ///   <item><c>currentLag</c>: Current lag for each replica</item>
        ///   <item><c>replicaLoad</c>: Current load on each replica</item>
        ///   <item><c>operationType</c>: Read or Write operation</item>
        /// </list>
        /// Response:
        /// <list type="bullet">
        ///   <item><c>selectedReplica</c>: Recommended replica node ID</item>
        ///   <item><c>confidence</c>: Confidence in selection (0.0-1.0)</item>
        ///   <item><c>alternativeReplicas</c>: Fallback replicas in order</item>
        /// </list>
        /// </remarks>
        public const string RouteRequest = $"{Prefix}.route.request";

        /// <summary>
        /// Response topic for routing requests.
        /// </summary>
        public const string RouteRequestResponse = $"{Prefix}.route.request.response";

        /// <summary>
        /// Topic for sending lag data to Intelligence for learning.
        /// Used to improve future predictions and recommendations.
        /// </summary>
        public const string LagFeedback = $"{Prefix}.lag.feedback";

        /// <summary>
        /// Topic for sending conflict resolution outcomes to Intelligence.
        /// Used to improve future conflict prediction and resolution.
        /// </summary>
        public const string ConflictFeedback = $"{Prefix}.conflict.feedback";

        /// <summary>
        /// Topic for requesting performance optimization suggestions.
        /// </summary>
        public const string OptimizePerformance = $"{Prefix}.optimize.performance";

        /// <summary>
        /// Response topic for performance optimization requests.
        /// </summary>
        public const string OptimizePerformanceResponse = $"{Prefix}.optimize.performance.response";

        // ========================================
        // Topic Patterns
        // ========================================

        /// <summary>
        /// Pattern for subscribing to all replication events.
        /// </summary>
        public const string AllEvents = $"{Prefix}.*";

        /// <summary>
        /// Pattern for subscribing to all conflict-related topics.
        /// </summary>
        public const string AllConflicts = $"{Prefix}.conflict.*";

        /// <summary>
        /// Pattern for subscribing to all Intelligence request topics.
        /// </summary>
        public const string AllIntelligenceRequests = $"{Prefix}.predict.*";

        /// <summary>
        /// Pattern for subscribing to all strategy-related topics.
        /// </summary>
        public const string AllStrategyTopics = $"{Prefix}.strategies.*";
    }
}
