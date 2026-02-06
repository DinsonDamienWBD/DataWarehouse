namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.ConnectorIntegration
{
    /// <summary>
    /// Configuration modes for connector-intelligence integration.
    /// </summary>
    public enum ConnectorIntegrationMode
    {
        /// <summary>Disabled - no connector integration.</summary>
        Disabled = 0,

        /// <summary>
        /// Async observation only (Option A) - T90 subscribes to connector events,
        /// processes them asynchronously, but does not block the connector pipeline.
        /// Good for logging, analytics, anomaly detection without latency impact.
        /// </summary>
        AsyncObservation = 1,

        /// <summary>
        /// Synchronous transformation (Option B) - T90 receives requests from connector,
        /// processes them, and returns transformed results. Connector waits for response.
        /// Good for query optimization, schema enrichment, request transformation.
        /// </summary>
        SyncTransformation = 2,

        /// <summary>
        /// Hybrid mode - both async observation AND sync transformation.
        /// Best flexibility but highest resource usage.
        /// </summary>
        Hybrid = 3
    }
}
