namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory;

/// <summary>
/// Message bus topics for tiered memory operations.
/// Provides comprehensive topic definitions for all memory-related messaging.
/// </summary>
public static class MemoryTopics
{
    /// <summary>Base prefix for all memory topics.</summary>
    public const string Prefix = "intelligence.memory";

    #region Store Operations

    /// <summary>Store a memory (default tier).</summary>
    public const string Store = $"{Prefix}.store";

    /// <summary>Response for store operation.</summary>
    public const string StoreResponse = $"{Prefix}.store.response";

    /// <summary>Store a memory with explicit tier.</summary>
    public const string StoreWithTier = $"{Prefix}.store.tier";

    /// <summary>Response for tier-specific store operation.</summary>
    public const string StoreWithTierResponse = $"{Prefix}.store.tier.response";

    /// <summary>Batch store multiple memories.</summary>
    public const string StoreBatch = $"{Prefix}.store.batch";

    /// <summary>Response for batch store operation.</summary>
    public const string StoreBatchResponse = $"{Prefix}.store.batch.response";

    #endregion

    #region Recall Operations

    /// <summary>Recall memories based on query.</summary>
    public const string Recall = $"{Prefix}.recall";

    /// <summary>Response for recall operation.</summary>
    public const string RecallResponse = $"{Prefix}.recall.response";

    /// <summary>Recall memories with tier filtering.</summary>
    public const string RecallWithTier = $"{Prefix}.recall.tier";

    /// <summary>Response for tier-filtered recall operation.</summary>
    public const string RecallWithTierResponse = $"{Prefix}.recall.tier.response";

    /// <summary>Recall by memory ID.</summary>
    public const string RecallById = $"{Prefix}.recall.id";

    /// <summary>Response for ID-based recall.</summary>
    public const string RecallByIdResponse = $"{Prefix}.recall.id.response";

    /// <summary>Recall by scope/namespace.</summary>
    public const string RecallByScope = $"{Prefix}.recall.scope";

    /// <summary>Response for scope-based recall.</summary>
    public const string RecallByScopeResponse = $"{Prefix}.recall.scope.response";

    #endregion

    #region Tier Management

    /// <summary>Configure a specific tier.</summary>
    public const string ConfigureTier = $"{Prefix}.tier.configure";

    /// <summary>Response for tier configuration.</summary>
    public const string ConfigureTierResponse = $"{Prefix}.tier.configure.response";

    /// <summary>Get statistics for a tier.</summary>
    public const string GetTierStats = $"{Prefix}.tier.stats";

    /// <summary>Response for tier statistics.</summary>
    public const string GetTierStatsResponse = $"{Prefix}.tier.stats.response";

    /// <summary>Enable a tier.</summary>
    public const string EnableTier = $"{Prefix}.tier.enable";

    /// <summary>Response for tier enable operation.</summary>
    public const string EnableTierResponse = $"{Prefix}.tier.enable.response";

    /// <summary>Disable a tier.</summary>
    public const string DisableTier = $"{Prefix}.tier.disable";

    /// <summary>Response for tier disable operation.</summary>
    public const string DisableTierResponse = $"{Prefix}.tier.disable.response";

    /// <summary>Get all tier configurations.</summary>
    public const string GetAllTierConfigs = $"{Prefix}.tier.configs";

    /// <summary>Response for all tier configurations.</summary>
    public const string GetAllTierConfigsResponse = $"{Prefix}.tier.configs.response";

    /// <summary>Promote memory to higher tier.</summary>
    public const string Promote = $"{Prefix}.tier.promote";

    /// <summary>Response for promotion operation.</summary>
    public const string PromoteResponse = $"{Prefix}.tier.promote.response";

    /// <summary>Demote memory to lower tier.</summary>
    public const string Demote = $"{Prefix}.tier.demote";

    /// <summary>Response for demotion operation.</summary>
    public const string DemoteResponse = $"{Prefix}.tier.demote.response";

    #endregion

    #region Evolution Operations

    /// <summary>Consolidate memories across tiers.</summary>
    public const string Consolidate = $"{Prefix}.consolidate";

    /// <summary>Response for consolidation operation.</summary>
    public const string ConsolidateResponse = $"{Prefix}.consolidate.response";

    /// <summary>Refine context (compress/summarize).</summary>
    public const string Refine = $"{Prefix}.refine";

    /// <summary>Response for refinement operation.</summary>
    public const string RefineResponse = $"{Prefix}.refine.response";

    /// <summary>Get evolution metrics.</summary>
    public const string GetEvolution = $"{Prefix}.evolution.metrics";

    /// <summary>Response for evolution metrics.</summary>
    public const string GetEvolutionResponse = $"{Prefix}.evolution.metrics.response";

    /// <summary>Trigger automatic tier management.</summary>
    public const string AutoManage = $"{Prefix}.evolution.auto-manage";

    /// <summary>Response for auto-management operation.</summary>
    public const string AutoManageResponse = $"{Prefix}.evolution.auto-manage.response";

    #endregion

    #region Regeneration Operations

    /// <summary>Regenerate data from context.</summary>
    public const string Regenerate = $"{Prefix}.regenerate";

    /// <summary>Response for regeneration operation.</summary>
    public const string RegenerateResponse = $"{Prefix}.regenerate.response";

    /// <summary>Validate regenerated content.</summary>
    public const string ValidateRegeneration = $"{Prefix}.regenerate.validate";

    /// <summary>Response for regeneration validation.</summary>
    public const string ValidateRegenerationResponse = $"{Prefix}.regenerate.validate.response";

    #endregion

    #region Persistence Operations

    /// <summary>Flush volatile memory to persistent storage.</summary>
    public const string Flush = $"{Prefix}.flush";

    /// <summary>Response for flush operation.</summary>
    public const string FlushResponse = $"{Prefix}.flush.response";

    /// <summary>Restore memory from persistent storage.</summary>
    public const string Restore = $"{Prefix}.restore";

    /// <summary>Response for restore operation.</summary>
    public const string RestoreResponse = $"{Prefix}.restore.response";

    /// <summary>Export memory to external format.</summary>
    public const string Export = $"{Prefix}.export";

    /// <summary>Response for export operation.</summary>
    public const string ExportResponse = $"{Prefix}.export.response";

    /// <summary>Import memory from external format.</summary>
    public const string Import = $"{Prefix}.import";

    /// <summary>Response for import operation.</summary>
    public const string ImportResponse = $"{Prefix}.import.response";

    #endregion

    #region Maintenance Operations

    /// <summary>Forget/delete a specific memory.</summary>
    public const string Forget = $"{Prefix}.forget";

    /// <summary>Response for forget operation.</summary>
    public const string ForgetResponse = $"{Prefix}.forget.response";

    /// <summary>Clear all memories in a scope.</summary>
    public const string ClearScope = $"{Prefix}.clear.scope";

    /// <summary>Response for scope clear operation.</summary>
    public const string ClearScopeResponse = $"{Prefix}.clear.scope.response";

    /// <summary>Clear all memories in a tier.</summary>
    public const string ClearTier = $"{Prefix}.clear.tier";

    /// <summary>Response for tier clear operation.</summary>
    public const string ClearTierResponse = $"{Prefix}.clear.tier.response";

    /// <summary>Run garbage collection on expired entries.</summary>
    public const string GarbageCollect = $"{Prefix}.gc";

    /// <summary>Response for garbage collection operation.</summary>
    public const string GarbageCollectResponse = $"{Prefix}.gc.response";

    #endregion

    #region Statistics and Monitoring

    /// <summary>Get overall memory statistics.</summary>
    public const string GetStatistics = $"{Prefix}.stats";

    /// <summary>Response for statistics request.</summary>
    public const string GetStatisticsResponse = $"{Prefix}.stats.response";

    /// <summary>Get memory health status.</summary>
    public const string GetHealth = $"{Prefix}.health";

    /// <summary>Response for health status.</summary>
    public const string GetHealthResponse = $"{Prefix}.health.response";

    /// <summary>Memory event notification (async).</summary>
    public const string Event = $"{Prefix}.event";

    /// <summary>Memory alert notification (async).</summary>
    public const string Alert = $"{Prefix}.alert";

    #endregion

    #region Encoding Operations

    /// <summary>Encode/compress context.</summary>
    public const string Encode = $"{Prefix}.encode";

    /// <summary>Response for encode operation.</summary>
    public const string EncodeResponse = $"{Prefix}.encode.response";

    /// <summary>Decode/decompress context.</summary>
    public const string Decode = $"{Prefix}.decode";

    /// <summary>Response for decode operation.</summary>
    public const string DecodeResponse = $"{Prefix}.decode.response";

    #endregion
}

/// <summary>
/// Payload structures for memory message bus operations.
/// </summary>
public static class MemoryPayloads
{
    /// <summary>
    /// Payload for store operations.
    /// </summary>
    public sealed record StorePayload
    {
        /// <summary>Content to store.</summary>
        public required string Content { get; init; }

        /// <summary>Target tier (optional, defaults to Working).</summary>
        public MemoryTier? Tier { get; init; }

        /// <summary>Scope/namespace (optional).</summary>
        public string? Scope { get; init; }

        /// <summary>Additional metadata.</summary>
        public Dictionary<string, object>? Metadata { get; init; }
    }

    /// <summary>
    /// Payload for recall operations.
    /// </summary>
    public sealed record RecallPayload
    {
        /// <summary>Query string for semantic search.</summary>
        public required string Query { get; init; }

        /// <summary>Minimum tier to search from.</summary>
        public MemoryTier? MinTier { get; init; }

        /// <summary>Scope/namespace to search in.</summary>
        public string? Scope { get; init; }

        /// <summary>Maximum number of results.</summary>
        public int TopK { get; init; } = 10;

        /// <summary>Minimum relevance score (0.0-1.0).</summary>
        public float MinRelevance { get; init; } = 0.0f;
    }

    /// <summary>
    /// Payload for tier configuration.
    /// </summary>
    public sealed record TierConfigPayload
    {
        /// <summary>Target tier.</summary>
        public required MemoryTier Tier { get; init; }

        /// <summary>Whether tier is enabled.</summary>
        public bool? Enabled { get; init; }

        /// <summary>Persistence mode.</summary>
        public MemoryPersistence? Persistence { get; init; }

        /// <summary>Maximum capacity in bytes.</summary>
        public long? MaxCapacityBytes { get; init; }

        /// <summary>Time-to-live in seconds (null for no expiration).</summary>
        public int? TTLSeconds { get; init; }
    }

    /// <summary>
    /// Payload for tier movement (promote/demote).
    /// </summary>
    public sealed record TierMovePayload
    {
        /// <summary>Memory ID to move.</summary>
        public required string MemoryId { get; init; }

        /// <summary>Target tier.</summary>
        public required MemoryTier TargetTier { get; init; }
    }

    /// <summary>
    /// Payload for regeneration operations.
    /// </summary>
    public sealed record RegeneratePayload
    {
        /// <summary>Context entry ID to regenerate from.</summary>
        public required string ContextEntryId { get; init; }

        /// <summary>Expected output format.</summary>
        public required string ExpectedFormat { get; init; }
    }

    /// <summary>
    /// Response payload for store operations.
    /// </summary>
    public sealed record StoreResponse
    {
        /// <summary>Whether operation succeeded.</summary>
        public bool Success { get; init; }

        /// <summary>Assigned memory ID.</summary>
        public string? MemoryId { get; init; }

        /// <summary>Error message if failed.</summary>
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Response payload for recall operations.
    /// </summary>
    public sealed record RecallResponse
    {
        /// <summary>Whether operation succeeded.</summary>
        public bool Success { get; init; }

        /// <summary>Retrieved memories.</summary>
        public List<RetrievedMemoryDto>? Memories { get; init; }

        /// <summary>Error message if failed.</summary>
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// DTO for retrieved memory in responses.
    /// </summary>
    public sealed record RetrievedMemoryDto
    {
        /// <summary>Memory ID.</summary>
        public required string Id { get; init; }

        /// <summary>Memory content.</summary>
        public required string Content { get; init; }

        /// <summary>Relevance score.</summary>
        public float RelevanceScore { get; init; }

        /// <summary>Memory tier.</summary>
        public MemoryTier? Tier { get; init; }

        /// <summary>Creation timestamp.</summary>
        public DateTime CreatedAt { get; init; }

        /// <summary>Additional metadata.</summary>
        public Dictionary<string, object>? Metadata { get; init; }
    }

    /// <summary>
    /// Response payload for tier statistics.
    /// </summary>
    public sealed record TierStatsResponse
    {
        /// <summary>Whether operation succeeded.</summary>
        public bool Success { get; init; }

        /// <summary>Tier statistics.</summary>
        public TierStatistics? Statistics { get; init; }

        /// <summary>Error message if failed.</summary>
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Response payload for evolution metrics.
    /// </summary>
    public sealed record EvolutionMetricsResponse
    {
        /// <summary>Whether operation succeeded.</summary>
        public bool Success { get; init; }

        /// <summary>Evolution metrics.</summary>
        public ContextEvolutionMetrics? Metrics { get; init; }

        /// <summary>Error message if failed.</summary>
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Response payload for regeneration operations.
    /// </summary>
    public sealed record RegenerateResponse
    {
        /// <summary>Whether regeneration succeeded.</summary>
        public bool Success { get; init; }

        /// <summary>Regenerated data.</summary>
        public string? RegeneratedData { get; init; }

        /// <summary>Confidence score.</summary>
        public double ConfidenceScore { get; init; }

        /// <summary>Error message if failed.</summary>
        public string? ErrorMessage { get; init; }
    }
}
