using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.SDK.Contracts
{
    #region Storage Pool - Combines Multiple Storage Providers

    /// <summary>
    /// Combines multiple storage providers into a unified pool.
    /// Supports various distribution strategies (RAID, cache, tiering, etc.)
    /// User-overridable at runtime.
    /// </summary>
    public interface IStoragePool
    {
        string PoolId { get; }
        string Name { get; }
        IStorageStrategy Strategy { get; }
        IReadOnlyList<IStorageProvider> Providers { get; }

        void AddProvider(IStorageProvider provider, StorageRole role = StorageRole.Primary);
        bool RemoveProvider(string providerId);
        void SetStrategy(IStorageStrategy strategy);

        Task<StorageResult> SaveAsync(Uri uri, Stream data, StorageIntent? intent = null, CancellationToken ct = default);
        Task<Stream> LoadAsync(Uri uri, CancellationToken ct = default);
        Task DeleteAsync(Uri uri, CancellationToken ct = default);
        Task<StoragePoolHealth> GetHealthAsync(CancellationToken ct = default);
        Task<RepairResult> RepairAsync(string? targetProviderId = null, CancellationToken ct = default);
    }

    /// <summary>Role of a storage provider within a pool.</summary>
    public enum StorageRole
    {
        Primary, Cache, WriteAheadLog, Journal, Mirror, Parity, Archive, HotStandby
    }

    /// <summary>Result of a storage operation.</summary>
    public class StorageResult
    {
        public bool Success { get; init; }
        public Uri? StoredUri { get; init; }
        public long BytesWritten { get; init; }
        public TimeSpan Duration { get; init; }
        public string[] ProvidersUsed { get; init; } = Array.Empty<string>();
        public string? Error { get; init; }
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    /// <summary>Health status of a storage pool.</summary>
    public class StoragePoolHealth
    {
        public bool IsHealthy { get; init; }
        public int TotalProviders { get; init; }
        public int HealthyProviders { get; init; }
        public int DegradedProviders { get; init; }
        public int FailedProviders { get; init; }
        public long TotalCapacityBytes { get; init; }
        public long UsedCapacityBytes { get; init; }
        public double UsagePercent => TotalCapacityBytes > 0 ? (double)UsedCapacityBytes / TotalCapacityBytes * 100 : 0;
        public List<ProviderHealth> ProviderDetails { get; init; } = new();
    }

    public class ProviderHealth
    {
        public string ProviderId { get; init; } = string.Empty;
        public StorageRole Role { get; init; }
        public bool IsHealthy { get; init; }
        public TimeSpan? LastResponseTime { get; init; }
        public string? LastError { get; init; }
    }

    public class RepairResult
    {
        public bool Success { get; init; }
        public int ItemsChecked { get; init; }
        public int ItemsRepaired { get; init; }
        public int ItemsFailed { get; init; }
        public List<string> Errors { get; init; } = new();
    }

    #endregion

    #region Storage Strategy - Distribution Patterns

    /// <summary>
    /// Defines how data is distributed across storage providers in a pool.
    /// </summary>
    public interface IStorageStrategy
    {
        string StrategyId { get; }
        string Name { get; }
        StorageStrategyType Type { get; }

        IEnumerable<ProviderWritePlan> PlanWrite(IReadOnlyList<IStorageProvider> providers, StorageIntent? intent, long dataSize);
        IEnumerable<IStorageProvider> PlanRead(IReadOnlyList<IStorageProvider> providers, StorageIntent? intent);
        RecoveryPlan OnProviderFailure(IStorageProvider failedProvider, IReadOnlyList<IStorageProvider> remainingProviders);
        StrategyValidation Validate(IReadOnlyList<IStorageProvider> providers);
    }

    public enum StorageStrategyType
    {
        Simple, Striped, Mirrored, StripedParity, DoubleParity,
        Cached, Tiered, WriteAheadLog, Journaled, RealTime, Custom
    }

    public class ProviderWritePlan
    {
        public IStorageProvider Provider { get; init; } = null!;
        public StorageRole Role { get; init; }
        public bool IsRequired { get; init; } = true;
        public int Priority { get; init; }
        public WriteMode Mode { get; init; } = WriteMode.Full;
    }

    public enum WriteMode { Full, Stripe, Parity, MetadataOnly, WriteAheadLog }

    public class RecoveryPlan
    {
        public bool CanRecover { get; init; }
        public string[] RecoverySteps { get; init; } = Array.Empty<string>();
        public IStorageProvider? FailoverProvider { get; init; }
        public bool RequiresManualIntervention { get; init; }
        public string? Message { get; init; }
    }

    public class StrategyValidation
    {
        public bool IsValid { get; init; }
        public int MinimumProviders { get; init; }
        public int CurrentProviders { get; init; }
        public List<string> Errors { get; init; } = new();
        public List<string> Warnings { get; init; } = new();
    }

    #endregion

    #region Hybrid Storage - Automatic Indexing Pipeline

    /// <summary>
    /// Hybrid storage that automatically triggers indexing pipelines.
    /// On write: Save → SQL metadata → OCR → NoSQL text → Embeddings → Vector DB → Summary
    /// </summary>
    public interface IHybridStorage : IStoragePool
    {
        HybridStorageConfig Config { get; }
        void Configure(HybridStorageConfig config);
        Task<HybridSaveResult> SaveWithIndexingAsync(Uri uri, Stream data, StorageIntent? intent = null, CancellationToken ct = default);
        Task<IndexingStatus> GetIndexingStatusAsync(Uri uri, CancellationToken ct = default);
        Task<IndexingStatus> ReindexAsync(Uri uri, CancellationToken ct = default);
        ISearchOrchestrator SearchOrchestrator { get; }
    }

    public class HybridStorageConfig
    {
        public bool EnableSqlMetadata { get; init; } = true;
        public bool EnableOcr { get; init; } = true;
        public bool EnableNoSqlText { get; init; } = true;
        public bool EnableVectorEmbeddings { get; init; } = true;
        public bool EnableAiSummary { get; init; } = false;
        public bool EnableEventTriggers { get; init; } = false;
        public List<string> OcrFileTypes { get; init; } = new() { ".pdf", ".png", ".jpg", ".jpeg", ".tiff", ".bmp" };
        public long MaxOcrFileSizeBytes { get; init; } = 50 * 1024 * 1024;
        public string? EmbeddingModel { get; init; }
        public string? SummaryModel { get; init; }
        public List<EventTriggerConfig> EventTriggers { get; init; } = new();
    }

    public class EventTriggerConfig
    {
        public string Name { get; init; } = string.Empty;
        public EventTriggerType Type { get; init; }
        public string Endpoint { get; init; } = string.Empty;
        public Dictionary<string, string> Headers { get; init; } = new();
    }

    public enum EventTriggerType { Webhook, AwsLambda, AzureFunction, GoogleCloudFunction, ServiceBus, EventGrid, Kafka, RabbitMQ }

    public class HybridSaveResult : StorageResult
    {
        public string IndexingJobId { get; init; } = string.Empty;
        public TimeSpan? EstimatedIndexingTime { get; init; }
        public string[] PlannedIndexingStages { get; init; } = Array.Empty<string>();
    }

    public class IndexingStatus
    {
        public Uri Uri { get; init; } = null!;
        public IndexingState State { get; init; }
        public DateTime? StartedAt { get; init; }
        public DateTime? CompletedAt { get; init; }
        public Dictionary<string, StageStatus> Stages { get; init; } = new();
        public string? Error { get; init; }
    }

    public enum IndexingState { NotStarted, InProgress, Completed, PartiallyCompleted, Failed }

    public class StageStatus
    {
        public string StageName { get; init; } = string.Empty;
        public bool Completed { get; init; }
        public bool Skipped { get; init; }
        public string? Error { get; init; }
        public TimeSpan? Duration { get; init; }
    }

    #endregion

    #region Search Orchestrator - Multi-Threaded Search

    /// <summary>
    /// Orchestrates multi-threaded search across SQL, NoSQL, Vector, and AI.
    /// Thread A (SQL ~10ms) | Thread B (NoSQL ~50ms) | Thread C (Vector ~200ms) | Thread D (AI ~3-10s)
    /// </summary>
    public interface ISearchOrchestrator
    {
        Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default);
        IAsyncEnumerable<SearchResultBatch> SearchStreamingAsync(SearchQuery query, CancellationToken ct = default);
        IReadOnlyList<SearchProviderInfo> GetProviders();
        void Configure(SearchOrchestratorConfig config);
    }

    public class SearchQuery
    {
        public string QueryText { get; init; } = string.Empty;
        public float[]? QueryVector { get; init; }
        public Dictionary<string, object>? Filters { get; init; }
        public DateRange? DateRange { get; init; }
        public int MaxResultsPerProvider { get; init; } = 20;
        public int MaxTotalResults { get; init; } = 50;
        public TimeSpan? ProviderTimeout { get; init; }
        public SearchProviderType[]? EnabledProviders { get; init; }
        public bool EnableAiRefinement { get; init; } = true;
        public string? UserContext { get; init; }
    }

    public class DateRange { public DateTime? From { get; init; } public DateTime? To { get; init; } }

    public enum SearchProviderType { SqlMetadata, NoSqlKeyword, VectorSemantic, AiAgent }

    public class SearchProviderInfo
    {
        public SearchProviderType Type { get; init; }
        public string Name { get; init; } = string.Empty;
        public bool IsAvailable { get; init; }
        public TimeSpan TypicalLatency { get; init; }
        public string? PluginId { get; init; }
    }

    public class SearchResult
    {
        public int TotalCount { get; init; }
        public List<SearchResultItem> Items { get; init; } = new();
        public Dictionary<SearchProviderType, ProviderSearchResult> ProviderResults { get; init; } = new();
        public string? AiReasoning { get; init; }
        public TimeSpan Duration { get; init; }
        public Dictionary<SearchProviderType, string> Errors { get; init; } = new();
    }

    public class SearchResultBatch
    {
        public SearchProviderType Source { get; init; }
        public List<SearchResultItem> Items { get; init; } = new();
        public TimeSpan Latency { get; init; }
        public bool IsFinal { get; init; }
    }

    public class SearchResultItem
    {
        public Uri Uri { get; init; } = null!;
        public string Title { get; init; } = string.Empty;
        public string? Snippet { get; init; }
        public double Score { get; init; }
        public SearchProviderType Source { get; init; }
        public MatchType MatchType { get; init; }
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    public enum MatchType { Exact, Keyword, Fuzzy, Semantic, AiInferred }

    public class ProviderSearchResult
    {
        public SearchProviderType Provider { get; init; }
        public int Count { get; init; }
        public TimeSpan Latency { get; init; }
        public List<SearchResultItem> Items { get; init; } = new();
        public string? Error { get; init; }
    }

    public class SearchOrchestratorConfig
    {
        public TimeSpan DefaultProviderTimeout { get; init; } = TimeSpan.FromSeconds(5);
        public TimeSpan MaxTotalTimeout { get; init; } = TimeSpan.FromSeconds(30);
        public bool EnableParallelExecution { get; init; } = true;
        public MergeStrategy MergeStrategy { get; init; } = MergeStrategy.ScoreWeighted;
        public Dictionary<SearchProviderType, double> ProviderWeights { get; init; } = new()
        {
            [SearchProviderType.SqlMetadata] = 1.0,
            [SearchProviderType.NoSqlKeyword] = 1.2,
            [SearchProviderType.VectorSemantic] = 1.5,
            [SearchProviderType.AiAgent] = 2.0
        };
    }

    public enum MergeStrategy { Union, ScoreWeighted, ReciprocalRankFusion, AiRanked }

    #endregion

    #region Real-Time Storage - High-Stakes Data (Government, Healthcare, Banks)

    /// <summary>
    /// High-performance, maximum-reliability storage for critical data.
    /// Guarantees: Synchronous multi-site replication, point-in-time recovery,
    /// immutable audit trail, cryptographic verification, compliance-ready.
    /// </summary>
    public interface IRealTimeStorage : IStoragePool
    {
        ComplianceMode ComplianceMode { get; }
        void SetComplianceMode(ComplianceMode mode);

        Task<RealTimeSaveResult> SaveSynchronousAsync(Uri uri, Stream data, RealTimeWriteOptions options, CancellationToken ct = default);
        Task<RealTimeReadResult> ReadVerifiedAsync(Uri uri, CancellationToken ct = default);
        Task<Stream> ReadAtPointInTimeAsync(Uri uri, DateTime pointInTime, CancellationToken ct = default);
        IAsyncEnumerable<AuditEntry> GetAuditTrailAsync(Uri uri, DateTime? from = null, DateTime? to = null, CancellationToken ct = default);
        Task<IntegrityReport> VerifyIntegrityAsync(Uri uri, CancellationToken ct = default);
        Task<ReplicationStatus> GetReplicationStatusAsync(CancellationToken ct = default);
        Task<LockResult> LockAsync(Uri uri, LockReason reason, string authorizedBy, DateTime? expiresAt = null, CancellationToken ct = default);
        Task ReleaseLockAsync(Uri uri, string authorizedBy, CancellationToken ct = default);
    }

    public enum ComplianceMode { None, HIPAA, Financial, Government, GDPR, Custom }

    public class RealTimeWriteOptions
    {
        public int MinimumConfirmations { get; init; } = 2;
        public bool RequireAllSites { get; init; } = false;
        public EncryptionLevel Encryption { get; init; } = EncryptionLevel.AES256;
        public bool GenerateHash { get; init; } = true;
        public HashAlgorithmType HashAlgorithm { get; init; } = HashAlgorithmType.SHA256;
        public TimeSpan? RetentionPeriod { get; init; }
        public string? Classification { get; init; }
        public string? InitiatedBy { get; init; }
        public string? Reason { get; init; }
    }

    public enum EncryptionLevel { None, AES128, AES256, AES256WithHSM, Custom }
    public enum HashAlgorithmType { SHA256, SHA384, SHA512, BLAKE2, BLAKE3 }

    public class RealTimeSaveResult : StorageResult
    {
        public string[] ConfirmedSites { get; init; } = Array.Empty<string>();
        public string? DataHash { get; init; }
        public long VersionNumber { get; init; }
        public DateTime ReplicatedAt { get; init; }
        public string AuditId { get; init; } = string.Empty;
    }

    public class RealTimeReadResult
    {
        public bool Success { get; init; }
        public Stream? Data { get; init; }
        public bool IntegrityVerified { get; init; }
        public string? DataHash { get; init; }
        public long VersionNumber { get; init; }
        public DateTime LastModified { get; init; }
        public string? Error { get; init; }
    }

    public class AuditEntry
    {
        public string AuditId { get; init; } = string.Empty;
        public DateTime Timestamp { get; init; }
        public AuditAction Action { get; init; }
        public string UserId { get; init; } = string.Empty;
        public string? Reason { get; init; }
        public string? DataHash { get; init; }
        public long? VersionNumber { get; init; }
        public Dictionary<string, object> Details { get; init; } = new();
    }

    public enum AuditAction { Created, Read, Updated, Deleted, Locked, Unlocked, AccessGranted, AccessRevoked, Replicated, Verified, Exported }

    public class IntegrityReport
    {
        public bool IsValid { get; init; }
        public Uri Uri { get; init; } = null!;
        public string ExpectedHash { get; init; } = string.Empty;
        public Dictionary<string, SiteIntegrity> SiteResults { get; init; } = new();
        public DateTime VerifiedAt { get; init; }
    }

    public class SiteIntegrity
    {
        public string SiteId { get; init; } = string.Empty;
        public bool IsValid { get; init; }
        public string ActualHash { get; init; } = string.Empty;
        public DateTime LastVerified { get; init; }
    }

    public class ReplicationStatus
    {
        public int TotalSites { get; init; }
        public int SyncedSites { get; init; }
        public TimeSpan? ReplicationLag { get; init; }
        public List<SiteReplicationInfo> Sites { get; init; } = new();
    }

    public class SiteReplicationInfo
    {
        public string SiteId { get; init; } = string.Empty;
        public string Location { get; init; } = string.Empty;
        public bool IsSynced { get; init; }
        public DateTime LastSyncTime { get; init; }
        public TimeSpan? Lag { get; init; }
        public long PendingBytes { get; init; }
    }

    public enum LockReason { LegalHold, Investigation, Compliance, Regulatory, Preservation, Custom }

    public class LockResult
    {
        public bool Success { get; init; }
        public string LockId { get; init; } = string.Empty;
        public DateTime LockedAt { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public string? Error { get; init; }
    }

    #endregion
}
