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

        /// <summary>
        /// Saves multiple items in a batch for improved performance.
        /// </summary>
        Task<BatchStorageResult> SaveBatchAsync(IEnumerable<BatchSaveItem> items, StorageIntent? intent = null, CancellationToken ct = default);

        /// <summary>
        /// Deletes multiple items in a batch for improved performance.
        /// </summary>
        Task<BatchStorageResult> DeleteBatchAsync(IEnumerable<Uri> uris, CancellationToken ct = default);

        /// <summary>
        /// Checks existence of multiple items in a batch.
        /// </summary>
        Task<Dictionary<Uri, bool>> ExistsBatchAsync(IEnumerable<Uri> uris, CancellationToken ct = default);
    }

    /// <summary>
    /// Item for batch save operations.
    /// </summary>
    public class BatchSaveItem
    {
        public required Uri Uri { get; init; }
        public required Stream Data { get; init; }

        public BatchSaveItem() { }

        [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
        public BatchSaveItem(Uri uri, Stream data)
        {
            Uri = uri;
            Data = data;
        }
    }

    /// <summary>
    /// Result of a batch storage operation.
    /// </summary>
    public class BatchStorageResult
    {
        public int TotalItems { get; init; }
        public int SuccessCount { get; init; }
        public int FailureCount { get; init; }
        public TimeSpan Duration { get; init; }
        public List<BatchItemResult> Results { get; init; } = new();

        public bool AllSucceeded => FailureCount == 0;
        public bool AllFailed => SuccessCount == 0;
        public bool PartialSuccess => SuccessCount > 0 && FailureCount > 0;
    }

    /// <summary>
    /// Result for a single item in a batch operation.
    /// </summary>
    public class BatchItemResult
    {
        public required Uri Uri { get; init; }
        public bool Success { get; init; }
        public string? Error { get; init; }
        public long BytesWritten { get; init; }
    }

    /// <summary>
    /// Roles a storage provider can serve within a pool.
    /// Multiple roles can be combined with flags.
    /// </summary>
    [Flags]
    public enum StorageRole
    {
        /// <summary>No specific role.</summary>
        None = 0,

        /// <summary>Primary storage for data persistence.</summary>
        Primary = 1,

        /// <summary>Cache storage for fast access.</summary>
        Cache = 2,

        /// <summary>Index storage for search operations.</summary>
        Index = 4,

        /// <summary>Archive storage for long-term retention.</summary>
        Archive = 8,

        /// <summary>Backup storage for disaster recovery.</summary>
        Backup = 16,

        /// <summary>Metadata storage for manifest/index data.</summary>
        Metadata = 32,

        /// <summary>Temporary storage for transient data.</summary>
        Temporary = 64,

        /// <summary>Read replica for load distribution.</summary>
        ReadReplica = 128,

        /// <summary>Write-ahead log for transaction durability.</summary>
        WriteAheadLog = 256,

        /// <summary>Journal storage for transaction logging.</summary>
        Journal = 512,

        /// <summary>Mirror storage for redundancy.</summary>
        Mirror = 1024,

        /// <summary>Parity storage for RAID-like protection.</summary>
        Parity = 2048,

        /// <summary>Hot standby for failover.</summary>
        HotStandby = 4096,

        /// <summary>All roles combined.</summary>
        All = Primary | Cache | Index | Archive | Backup | Metadata | Temporary | ReadReplica | WriteAheadLog | Journal | Mirror | Parity | HotStandby
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
        public required IStorageProvider Provider { get; init; }
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


    #region Shared Storage Types

    public class DateRange { public DateTime? From { get; init; } public DateTime? To { get; init; } }

    public enum MatchType { Exact, Keyword, Fuzzy, Semantic, AiInferred }

    public enum ComplianceMode { None, HIPAA, Financial, Government, GDPR, Custom }

    public enum EncryptionLevel { None, AES128, AES256, AES256WithHSM, Custom }
    public enum HashAlgorithmType { SHA256, SHA384, SHA512, BLAKE2, BLAKE3 }

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

    #endregion
}
