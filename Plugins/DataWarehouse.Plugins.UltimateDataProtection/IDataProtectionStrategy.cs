using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataProtection
{
    /// <summary>
    /// Categorizes data protection strategies by their backup methodology.
    /// </summary>
    public enum DataProtectionCategory
    {
        /// <summary>Complete system backup capturing all data.</summary>
        FullBackup,

        /// <summary>Backup of changes since the last backup operation.</summary>
        IncrementalBackup,

        /// <summary>Backup of changes since the last full backup.</summary>
        DifferentialBackup,

        /// <summary>Continuous data protection with real-time capture.</summary>
        ContinuousProtection,

        /// <summary>Point-in-time snapshot capabilities.</summary>
        Snapshot,

        /// <summary>Real-time or near-real-time data replication.</summary>
        Replication,

        /// <summary>Long-term archival storage.</summary>
        Archive,

        /// <summary>Disaster recovery orchestration.</summary>
        DisasterRecovery
    }

    /// <summary>
    /// Capability flags for data protection strategies.
    /// </summary>
    [Flags]
    public enum DataProtectionCapabilities
    {
        /// <summary>No special capabilities.</summary>
        None = 0,

        /// <summary>Supports data compression during backup.</summary>
        Compression = 1,

        /// <summary>Supports encryption of backup data.</summary>
        Encryption = 2,

        /// <summary>Supports deduplication to reduce storage.</summary>
        Deduplication = 4,

        /// <summary>Supports point-in-time recovery to any moment.</summary>
        PointInTimeRecovery = 8,

        /// <summary>Supports granular recovery of individual items.</summary>
        GranularRecovery = 16,

        /// <summary>Works across different platforms.</summary>
        CrossPlatform = 32,

        /// <summary>Supports cloud storage as backup target.</summary>
        CloudTarget = 64,

        /// <summary>Supports tape storage as backup target.</summary>
        TapeSupport = 128,

        /// <summary>Application-consistent backups.</summary>
        ApplicationAware = 256,

        /// <summary>VMware vSphere integration.</summary>
        VMwareIntegration = 512,

        /// <summary>Hyper-V integration.</summary>
        HyperVIntegration = 1024,

        /// <summary>Kubernetes workload protection.</summary>
        KubernetesIntegration = 2048,

        /// <summary>Database-aware backup and recovery.</summary>
        DatabaseAware = 4096,

        /// <summary>Uses AI for optimization and recommendations.</summary>
        IntelligenceAware = 8192,

        /// <summary>Supports parallel backup streams.</summary>
        ParallelBackup = 16384,

        /// <summary>Supports instant recovery/mount.</summary>
        InstantRecovery = 32768,

        /// <summary>Immutable backup support (ransomware protection).</summary>
        ImmutableBackup = 65536,

        /// <summary>Supports bandwidth throttling.</summary>
        BandwidthThrottling = 131072,

        /// <summary>Verifies backup integrity automatically.</summary>
        AutoVerification = 262144
    }

    /// <summary>
    /// Core interface for data protection strategies.
    /// Provides comprehensive backup, restore, and catalog operations.
    /// </summary>
    public interface IDataProtectionStrategy
    {
        /// <summary>Gets the unique identifier for this strategy.</summary>
        string StrategyId { get; }

        /// <summary>Gets the human-readable name of this strategy.</summary>
        string StrategyName { get; }

        /// <summary>Gets the category of this protection strategy.</summary>
        DataProtectionCategory Category { get; }

        /// <summary>Gets the capabilities supported by this strategy.</summary>
        DataProtectionCapabilities Capabilities { get; }

        #region Backup Operations

        /// <summary>
        /// Creates a backup using this strategy.
        /// </summary>
        /// <param name="request">Backup request parameters.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Result of the backup operation.</returns>
        Task<BackupResult> CreateBackupAsync(BackupRequest request, CancellationToken ct = default);

        /// <summary>
        /// Gets the progress of an ongoing backup operation.
        /// </summary>
        /// <param name="backupId">The backup operation ID.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Current progress of the backup.</returns>
        Task<BackupProgress> GetBackupProgressAsync(string backupId, CancellationToken ct = default);

        /// <summary>
        /// Cancels an ongoing backup operation.
        /// </summary>
        /// <param name="backupId">The backup operation ID to cancel.</param>
        /// <param name="ct">Cancellation token.</param>
        Task CancelBackupAsync(string backupId, CancellationToken ct = default);

        #endregion

        #region Restore Operations

        /// <summary>
        /// Restores data from a backup.
        /// </summary>
        /// <param name="request">Restore request parameters.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Result of the restore operation.</returns>
        Task<RestoreResult> RestoreAsync(RestoreRequest request, CancellationToken ct = default);

        /// <summary>
        /// Gets the progress of an ongoing restore operation.
        /// </summary>
        /// <param name="restoreId">The restore operation ID.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Current progress of the restore.</returns>
        Task<RestoreProgress> GetRestoreProgressAsync(string restoreId, CancellationToken ct = default);

        /// <summary>
        /// Cancels an ongoing restore operation.
        /// </summary>
        /// <param name="restoreId">The restore operation ID to cancel.</param>
        /// <param name="ct">Cancellation token.</param>
        Task CancelRestoreAsync(string restoreId, CancellationToken ct = default);

        #endregion

        #region Catalog Operations

        /// <summary>
        /// Lists backups matching the specified query.
        /// </summary>
        /// <param name="query">Query parameters for filtering backups.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Enumerable of matching backup catalog entries.</returns>
        Task<IEnumerable<BackupCatalogEntry>> ListBackupsAsync(BackupListQuery query, CancellationToken ct = default);

        /// <summary>
        /// Gets detailed information about a specific backup.
        /// </summary>
        /// <param name="backupId">The backup ID to query.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Backup catalog entry, or null if not found.</returns>
        Task<BackupCatalogEntry?> GetBackupInfoAsync(string backupId, CancellationToken ct = default);

        /// <summary>
        /// Deletes a backup and its associated data.
        /// </summary>
        /// <param name="backupId">The backup ID to delete.</param>
        /// <param name="ct">Cancellation token.</param>
        Task DeleteBackupAsync(string backupId, CancellationToken ct = default);

        #endregion

        #region Validation Operations

        /// <summary>
        /// Validates the integrity of a backup.
        /// </summary>
        /// <param name="backupId">The backup ID to validate.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Validation result with any issues found.</returns>
        Task<ValidationResult> ValidateBackupAsync(string backupId, CancellationToken ct = default);

        /// <summary>
        /// Validates that a restore target is suitable.
        /// </summary>
        /// <param name="request">The restore request to validate.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Validation result for the restore target.</returns>
        Task<ValidationResult> ValidateRestoreTargetAsync(RestoreRequest request, CancellationToken ct = default);

        #endregion

        #region Statistics

        /// <summary>
        /// Gets statistics about this strategy's operations.
        /// </summary>
        DataProtectionStatistics GetStatistics();

        #endregion
    }

    #region Request/Response Types

    /// <summary>
    /// Request parameters for a backup operation.
    /// </summary>
    public sealed class BackupRequest
    {
        /// <summary>Unique identifier for this backup request.</summary>
        public string RequestId { get; init; } = Guid.NewGuid().ToString("N");

        /// <summary>Optional name for the backup.</summary>
        public string? BackupName { get; init; }

        /// <summary>Source paths or identifiers to backup.</summary>
        public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();

        /// <summary>Destination for the backup.</summary>
        public string? Destination { get; init; }

        /// <summary>Enable compression.</summary>
        public bool EnableCompression { get; init; } = true;

        /// <summary>Compression algorithm to use.</summary>
        public string CompressionAlgorithm { get; init; } = "Zstd";

        /// <summary>Enable encryption.</summary>
        public bool EnableEncryption { get; init; }

        /// <summary>Encryption key or key reference.</summary>
        public string? EncryptionKey { get; init; }

        /// <summary>Enable deduplication.</summary>
        public bool EnableDeduplication { get; init; } = true;

        /// <summary>Bandwidth limit in bytes per second (0 = unlimited).</summary>
        public long BandwidthLimit { get; init; }

        /// <summary>Parallel stream count.</summary>
        public int ParallelStreams { get; init; } = 4;

        /// <summary>Tags for the backup.</summary>
        public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();

        /// <summary>Retention policy name to apply.</summary>
        public string? RetentionPolicy { get; init; }

        /// <summary>Additional strategy-specific options.</summary>
        public IReadOnlyDictionary<string, object> Options { get; init; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Result of a backup operation.
    /// </summary>
    public sealed record BackupResult
    {
        /// <summary>Whether the backup completed successfully.</summary>
        public bool Success { get; init; }

        /// <summary>Unique identifier for this backup.</summary>
        public string BackupId { get; init; } = string.Empty;

        /// <summary>Error message if failed.</summary>
        public string? ErrorMessage { get; init; }

        /// <summary>When the backup started.</summary>
        public DateTimeOffset StartTime { get; init; }

        /// <summary>When the backup completed.</summary>
        public DateTimeOffset EndTime { get; init; }

        /// <summary>Duration of the backup operation.</summary>
        public TimeSpan Duration => EndTime - StartTime;

        /// <summary>Total bytes processed.</summary>
        public long TotalBytes { get; init; }

        /// <summary>Total bytes after compression/dedup.</summary>
        public long StoredBytes { get; init; }

        /// <summary>Number of files backed up.</summary>
        public long FileCount { get; init; }

        /// <summary>Deduplication ratio achieved.</summary>
        public double DeduplicationRatio => TotalBytes > 0 ? (double)StoredBytes / TotalBytes : 1.0;

        /// <summary>Average throughput in bytes per second.</summary>
        public double Throughput => Duration.TotalSeconds > 0 ? TotalBytes / Duration.TotalSeconds : 0;

        /// <summary>Warnings generated during backup.</summary>
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Progress information for an ongoing backup.
    /// </summary>
    public sealed record BackupProgress
    {
        /// <summary>The backup ID.</summary>
        public string BackupId { get; init; } = string.Empty;

        /// <summary>Current phase of the backup.</summary>
        public string Phase { get; init; } = string.Empty;

        /// <summary>Percentage complete (0-100).</summary>
        public double PercentComplete { get; init; }

        /// <summary>Bytes processed so far.</summary>
        public long BytesProcessed { get; init; }

        /// <summary>Total bytes to process.</summary>
        public long TotalBytes { get; init; }

        /// <summary>Files processed so far.</summary>
        public long FilesProcessed { get; init; }

        /// <summary>Total files to process.</summary>
        public long TotalFiles { get; init; }

        /// <summary>Current transfer rate in bytes per second.</summary>
        public double CurrentRate { get; init; }

        /// <summary>Estimated time remaining.</summary>
        public TimeSpan? EstimatedTimeRemaining { get; init; }

        /// <summary>Current file being processed.</summary>
        public string? CurrentItem { get; init; }
    }

    /// <summary>
    /// Request parameters for a restore operation.
    /// </summary>
    public sealed class RestoreRequest
    {
        /// <summary>Unique identifier for this restore request.</summary>
        public string RequestId { get; init; } = Guid.NewGuid().ToString("N");

        /// <summary>Backup ID to restore from.</summary>
        public required string BackupId { get; init; }

        /// <summary>Target path for restore.</summary>
        public string? TargetPath { get; init; }

        /// <summary>Point in time to restore to (for CDP).</summary>
        public DateTimeOffset? PointInTime { get; init; }

        /// <summary>Specific items to restore (null = all).</summary>
        public IReadOnlyList<string>? ItemsToRestore { get; init; }

        /// <summary>Overwrite existing files.</summary>
        public bool OverwriteExisting { get; init; }

        /// <summary>Preserve original permissions.</summary>
        public bool PreservePermissions { get; init; } = true;

        /// <summary>Parallel restore streams.</summary>
        public int ParallelStreams { get; init; } = 4;

        /// <summary>Bandwidth limit in bytes per second.</summary>
        public long BandwidthLimit { get; init; }

        /// <summary>Additional strategy-specific options.</summary>
        public IReadOnlyDictionary<string, object> Options { get; init; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Result of a restore operation.
    /// </summary>
    public sealed record RestoreResult
    {
        /// <summary>Whether the restore completed successfully.</summary>
        public bool Success { get; init; }

        /// <summary>Unique identifier for this restore operation.</summary>
        public string RestoreId { get; init; } = string.Empty;

        /// <summary>Error message if failed.</summary>
        public string? ErrorMessage { get; init; }

        /// <summary>When the restore started.</summary>
        public DateTimeOffset StartTime { get; init; }

        /// <summary>When the restore completed.</summary>
        public DateTimeOffset EndTime { get; init; }

        /// <summary>Duration of the restore operation.</summary>
        public TimeSpan Duration => EndTime - StartTime;

        /// <summary>Total bytes restored.</summary>
        public long TotalBytes { get; init; }

        /// <summary>Number of files restored.</summary>
        public long FileCount { get; init; }

        /// <summary>Files that failed to restore.</summary>
        public long FailedFiles { get; init; }

        /// <summary>Files skipped (already exist, etc.).</summary>
        public long SkippedFiles { get; init; }

        /// <summary>Warnings generated during restore.</summary>
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Progress information for an ongoing restore.
    /// </summary>
    public sealed record RestoreProgress
    {
        /// <summary>The restore ID.</summary>
        public string RestoreId { get; init; } = string.Empty;

        /// <summary>Current phase of the restore.</summary>
        public string Phase { get; init; } = string.Empty;

        /// <summary>Percentage complete (0-100).</summary>
        public double PercentComplete { get; init; }

        /// <summary>Bytes restored so far.</summary>
        public long BytesRestored { get; init; }

        /// <summary>Total bytes to restore.</summary>
        public long TotalBytes { get; init; }

        /// <summary>Files restored so far.</summary>
        public long FilesRestored { get; init; }

        /// <summary>Total files to restore.</summary>
        public long TotalFiles { get; init; }

        /// <summary>Current transfer rate in bytes per second.</summary>
        public double CurrentRate { get; init; }

        /// <summary>Estimated time remaining.</summary>
        public TimeSpan? EstimatedTimeRemaining { get; init; }

        /// <summary>Current file being restored.</summary>
        public string? CurrentItem { get; init; }

        /// <summary>Non-fatal warning message for this progress event, if any.</summary>
        public string? Warning { get; init; }
    }

    /// <summary>
    /// Query parameters for listing backups.
    /// </summary>
    public sealed class BackupListQuery
    {
        /// <summary>Filter by backup name pattern.</summary>
        public string? NamePattern { get; init; }

        /// <summary>Filter by source path.</summary>
        public string? SourcePath { get; init; }

        /// <summary>Filter by minimum creation time.</summary>
        public DateTimeOffset? CreatedAfter { get; init; }

        /// <summary>Filter by maximum creation time.</summary>
        public DateTimeOffset? CreatedBefore { get; init; }

        /// <summary>Filter by tags.</summary>
        public IReadOnlyDictionary<string, string>? Tags { get; init; }

        /// <summary>Maximum results to return.</summary>
        public int MaxResults { get; init; } = 100;

        /// <summary>Continuation token for paging.</summary>
        public string? ContinuationToken { get; init; }
    }

    /// <summary>
    /// Entry in the backup catalog.
    /// </summary>
    public sealed record BackupCatalogEntry
    {
        /// <summary>Unique backup ID.</summary>
        public string BackupId { get; init; } = string.Empty;

        /// <summary>Backup name.</summary>
        public string? Name { get; init; }

        /// <summary>Strategy used for this backup.</summary>
        public string StrategyId { get; init; } = string.Empty;

        /// <summary>Protection category.</summary>
        public DataProtectionCategory Category { get; init; }

        /// <summary>When the backup was created.</summary>
        public DateTimeOffset CreatedAt { get; init; }

        /// <summary>When the backup expires (null = never).</summary>
        public DateTimeOffset? ExpiresAt { get; init; }

        /// <summary>Source paths included.</summary>
        public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();

        /// <summary>Backup destination.</summary>
        public string Destination { get; init; } = string.Empty;

        /// <summary>Original data size.</summary>
        public long OriginalSize { get; init; }

        /// <summary>Stored size after compression/dedup.</summary>
        public long StoredSize { get; init; }

        /// <summary>Number of files.</summary>
        public long FileCount { get; init; }

        /// <summary>Whether the backup is encrypted.</summary>
        public bool IsEncrypted { get; init; }

        /// <summary>Whether the backup is compressed.</summary>
        public bool IsCompressed { get; init; }

        /// <summary>Parent backup ID (for incrementals).</summary>
        public string? ParentBackupId { get; init; }

        /// <summary>Backup chain root ID.</summary>
        public string? ChainRootId { get; init; }

        /// <summary>Tags on this backup.</summary>
        public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();

        /// <summary>Last validation time.</summary>
        public DateTimeOffset? LastValidatedAt { get; init; }

        /// <summary>Whether backup passed last validation.</summary>
        public bool? IsValid { get; init; }
    }

    /// <summary>
    /// Result of a validation operation.
    /// </summary>
    public sealed record ValidationResult
    {
        /// <summary>Whether validation passed.</summary>
        public bool IsValid { get; init; }

        /// <summary>When validation was performed.</summary>
        public DateTimeOffset ValidatedAt { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>Validation errors found.</summary>
        public IReadOnlyList<ValidationIssue> Errors { get; init; } = Array.Empty<ValidationIssue>();

        /// <summary>Validation warnings found.</summary>
        public IReadOnlyList<ValidationIssue> Warnings { get; init; } = Array.Empty<ValidationIssue>();

        /// <summary>Validation checks performed.</summary>
        public IReadOnlyList<string> ChecksPerformed { get; init; } = Array.Empty<string>();

        /// <summary>Duration of validation.</summary>
        public TimeSpan Duration { get; init; }
    }

    /// <summary>
    /// An issue found during validation.
    /// </summary>
    public sealed class ValidationIssue
    {
        /// <summary>Issue severity.</summary>
        public ValidationSeverity Severity { get; init; }

        /// <summary>Issue code for categorization.</summary>
        public string Code { get; init; } = string.Empty;

        /// <summary>Human-readable message.</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>Affected item path or identifier.</summary>
        public string? AffectedItem { get; init; }
    }

    /// <summary>
    /// Severity levels for validation issues.
    /// </summary>
    public enum ValidationSeverity
    {
        /// <summary>Informational finding.</summary>
        Info,

        /// <summary>Warning that should be reviewed.</summary>
        Warning,

        /// <summary>Error that prevents restore.</summary>
        Error,

        /// <summary>Critical error indicating corruption.</summary>
        Critical
    }

    /// <summary>
    /// Statistics for data protection operations.
    /// </summary>
    public sealed class DataProtectionStatistics
    {
        /// <summary>Total backup operations performed.</summary>
        public long TotalBackups { get; set; }

        /// <summary>Successful backup operations.</summary>
        public long SuccessfulBackups { get; set; }

        /// <summary>Failed backup operations.</summary>
        public long FailedBackups { get; set; }

        /// <summary>Total restore operations performed.</summary>
        public long TotalRestores { get; set; }

        /// <summary>Successful restore operations.</summary>
        public long SuccessfulRestores { get; set; }

        /// <summary>Failed restore operations.</summary>
        public long FailedRestores { get; set; }

        /// <summary>Total bytes backed up.</summary>
        public long TotalBytesBackedUp { get; set; }

        /// <summary>Total bytes stored (after compression/dedup).</summary>
        public long TotalBytesStored { get; set; }

        /// <summary>Total bytes restored.</summary>
        public long TotalBytesRestored { get; set; }

        /// <summary>Average backup throughput in bytes/second.</summary>
        public double AverageBackupThroughput { get; set; }

        /// <summary>Average restore throughput in bytes/second.</summary>
        public double AverageRestoreThroughput { get; set; }

        /// <summary>Overall deduplication ratio.</summary>
        public double DeduplicationRatio => TotalBytesBackedUp > 0
            ? (double)TotalBytesStored / TotalBytesBackedUp : 1.0;

        /// <summary>Space savings percentage.</summary>
        public double SpaceSavingsPercent => TotalBytesBackedUp > 0
            ? (1.0 - DeduplicationRatio) * 100 : 0;

        /// <summary>Last backup time.</summary>
        public DateTimeOffset? LastBackupTime { get; set; }

        /// <summary>Last restore time.</summary>
        public DateTimeOffset? LastRestoreTime { get; set; }

        /// <summary>Total validation operations.</summary>
        public long TotalValidations { get; set; }

        /// <summary>Successful validations.</summary>
        public long SuccessfulValidations { get; set; }

        /// <summary>Failed validations.</summary>
        public long FailedValidations { get; set; }
    }

    #endregion
}
