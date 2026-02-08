using DataWarehouse.Plugins.UltimateDataProtection.Versioning;

namespace DataWarehouse.Plugins.UltimateDataProtection
{
    /// <summary>
    /// Capability flags for the data protection provider.
    /// </summary>
    [Flags]
    public enum DataProtectionProviderCapabilities
    {
        /// <summary>No special capabilities.</summary>
        None = 0,

        /// <summary>Supports backup operations.</summary>
        Backup = 1,

        /// <summary>Supports versioning operations.</summary>
        Versioning = 2,

        /// <summary>Supports restore operations.</summary>
        Restore = 4,

        /// <summary>Supports intelligence-driven operations.</summary>
        Intelligence = 8,

        /// <summary>Supports continuous data protection.</summary>
        CDP = 16,

        /// <summary>Supports disaster recovery orchestration.</summary>
        DisasterRecovery = 32,

        /// <summary>Supports compliance and legal hold.</summary>
        Compliance = 64,

        /// <summary>Supports air-gapped backup isolation.</summary>
        AirGapped = 128
    }

    /// <summary>
    /// Overall status of the data protection system.
    /// </summary>
    public sealed record DataProtectionStatus
    {
        /// <summary>Whether the system is operational.</summary>
        public bool IsOperational { get; init; }

        /// <summary>Current operational mode.</summary>
        public string Mode { get; init; } = "Normal";

        /// <summary>Active backup operations count.</summary>
        public int ActiveBackupOperations { get; init; }

        /// <summary>Active restore operations count.</summary>
        public int ActiveRestoreOperations { get; init; }

        /// <summary>Pending version count.</summary>
        public int PendingVersions { get; init; }

        /// <summary>Last backup time.</summary>
        public DateTimeOffset? LastBackupTime { get; init; }

        /// <summary>Last restore time.</summary>
        public DateTimeOffset? LastRestoreTime { get; init; }

        /// <summary>Total protected data size in bytes.</summary>
        public long TotalProtectedBytes { get; init; }

        /// <summary>Total storage used in bytes.</summary>
        public long TotalStorageUsedBytes { get; init; }

        /// <summary>Protection coverage percentage (0-100).</summary>
        public double ProtectionCoveragePercent { get; init; }

        /// <summary>Any active alerts.</summary>
        public IReadOnlyList<string> ActiveAlerts { get; init; } = Array.Empty<string>();

        /// <summary>Status timestamp.</summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Health status of the data protection system.
    /// </summary>
    public sealed record DataProtectionHealth
    {
        /// <summary>Overall health status.</summary>
        public HealthStatus Status { get; init; }

        /// <summary>Backup subsystem health.</summary>
        public SubsystemHealth BackupHealth { get; init; } = new();

        /// <summary>Versioning subsystem health.</summary>
        public SubsystemHealth VersioningHealth { get; init; } = new();

        /// <summary>Restore subsystem health.</summary>
        public SubsystemHealth RestoreHealth { get; init; } = new();

        /// <summary>Intelligence subsystem health.</summary>
        public SubsystemHealth IntelligenceHealth { get; init; } = new();

        /// <summary>Storage health metrics.</summary>
        public StorageHealth StorageHealth { get; init; } = new();

        /// <summary>Health check timestamp.</summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>Detailed health messages.</summary>
        public IReadOnlyList<HealthMessage> Messages { get; init; } = Array.Empty<HealthMessage>();
    }

    /// <summary>
    /// Overall health status levels.
    /// </summary>
    public enum HealthStatus
    {
        /// <summary>System is healthy.</summary>
        Healthy,

        /// <summary>System has warnings but is operational.</summary>
        Degraded,

        /// <summary>System has critical issues.</summary>
        Unhealthy,

        /// <summary>Health status unknown.</summary>
        Unknown
    }

    /// <summary>
    /// Health status for a subsystem.
    /// </summary>
    public sealed record SubsystemHealth
    {
        /// <summary>Subsystem health status.</summary>
        public HealthStatus Status { get; init; } = HealthStatus.Unknown;

        /// <summary>Whether the subsystem is available.</summary>
        public bool IsAvailable { get; init; }

        /// <summary>Last operation time.</summary>
        public DateTimeOffset? LastOperationTime { get; init; }

        /// <summary>Error count in last hour.</summary>
        public int RecentErrorCount { get; init; }

        /// <summary>Health check details.</summary>
        public string? Details { get; init; }
    }

    /// <summary>
    /// Storage health metrics.
    /// </summary>
    public sealed record StorageHealth
    {
        /// <summary>Storage health status.</summary>
        public HealthStatus Status { get; init; } = HealthStatus.Unknown;

        /// <summary>Total storage capacity in bytes.</summary>
        public long TotalCapacityBytes { get; init; }

        /// <summary>Used storage in bytes.</summary>
        public long UsedBytes { get; init; }

        /// <summary>Available storage in bytes.</summary>
        public long AvailableBytes => TotalCapacityBytes - UsedBytes;

        /// <summary>Storage utilization percentage.</summary>
        public double UtilizationPercent => TotalCapacityBytes > 0
            ? (double)UsedBytes / TotalCapacityBytes * 100 : 0;

        /// <summary>Storage I/O latency in milliseconds.</summary>
        public double IoLatencyMs { get; init; }

        /// <summary>Storage throughput in bytes per second.</summary>
        public double ThroughputBytesPerSecond { get; init; }
    }

    /// <summary>
    /// A health check message.
    /// </summary>
    public sealed record HealthMessage
    {
        /// <summary>Message severity.</summary>
        public HealthStatus Severity { get; init; }

        /// <summary>Subsystem that generated the message.</summary>
        public string Subsystem { get; init; } = string.Empty;

        /// <summary>Message code.</summary>
        public string Code { get; init; } = string.Empty;

        /// <summary>Message text.</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>Message timestamp.</summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Master provider interface for data protection operations.
    /// Provides unified access to backup, versioning, restore, and intelligence subsystems.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The data protection provider serves as the central orchestrator for all data protection
    /// operations. It manages the following subsystems:
    /// </para>
    /// <list type="bullet">
    ///   <item>Backup: Full, incremental, differential, and continuous backup operations</item>
    ///   <item>Versioning: Version creation, management, and retention policies</item>
    ///   <item>Restore: Point-in-time and granular recovery operations</item>
    ///   <item>Intelligence: AI-driven optimization and recommendations</item>
    /// </list>
    /// </remarks>
    public interface IDataProtectionProvider
    {
        /// <summary>
        /// Gets the unique identifier for this provider.
        /// </summary>
        string ProviderId { get; }

        /// <summary>
        /// Gets the human-readable name of this provider.
        /// </summary>
        string ProviderName { get; }

        /// <summary>
        /// Gets the provider version.
        /// </summary>
        string ProviderVersion { get; }

        /// <summary>
        /// Gets the capabilities supported by this provider.
        /// </summary>
        DataProtectionProviderCapabilities Capabilities { get; }

        #region Subsystem Access

        /// <summary>
        /// Gets the backup subsystem for backup operations.
        /// </summary>
        IBackupSubsystem Backup { get; }

        /// <summary>
        /// Gets the versioning subsystem for version management.
        /// </summary>
        IVersioningSubsystem Versioning { get; }

        /// <summary>
        /// Gets the restore subsystem for recovery operations.
        /// </summary>
        IRestoreSubsystem Restore { get; }

        /// <summary>
        /// Gets the intelligence subsystem for AI-driven operations.
        /// </summary>
        IIntelligenceSubsystem Intelligence { get; }

        #endregion

        #region Unified Operations

        /// <summary>
        /// Gets the current status of the data protection system.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Current data protection status.</returns>
        Task<DataProtectionStatus> GetStatusAsync(CancellationToken ct = default);

        /// <summary>
        /// Performs a comprehensive health check of all subsystems.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Health check results.</returns>
        Task<DataProtectionHealth> HealthCheckAsync(CancellationToken ct = default);

        /// <summary>
        /// Initializes all subsystems and prepares for operations.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        Task InitializeAsync(CancellationToken ct = default);

        /// <summary>
        /// Gracefully shuts down all subsystems.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        Task ShutdownAsync(CancellationToken ct = default);

        #endregion
    }

    /// <summary>
    /// Interface for the backup subsystem.
    /// </summary>
    public interface IBackupSubsystem
    {
        /// <summary>
        /// Gets the available backup strategies.
        /// </summary>
        IReadOnlyCollection<IDataProtectionStrategy> Strategies { get; }

        /// <summary>
        /// Creates a backup using the specified strategy.
        /// </summary>
        /// <param name="strategyId">Strategy ID to use.</param>
        /// <param name="request">Backup request parameters.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Backup result.</returns>
        Task<BackupResult> CreateBackupAsync(string strategyId, BackupRequest request, CancellationToken ct = default);

        /// <summary>
        /// Gets the progress of an ongoing backup.
        /// </summary>
        /// <param name="backupId">Backup operation ID.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Backup progress.</returns>
        Task<BackupProgress> GetProgressAsync(string backupId, CancellationToken ct = default);

        /// <summary>
        /// Cancels an ongoing backup.
        /// </summary>
        /// <param name="backupId">Backup operation ID.</param>
        /// <param name="ct">Cancellation token.</param>
        Task CancelAsync(string backupId, CancellationToken ct = default);

        /// <summary>
        /// Lists backups matching the query.
        /// </summary>
        /// <param name="query">Query parameters.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Matching backup entries.</returns>
        Task<IEnumerable<BackupCatalogEntry>> ListBackupsAsync(BackupListQuery query, CancellationToken ct = default);

        /// <summary>
        /// Validates a backup's integrity.
        /// </summary>
        /// <param name="backupId">Backup ID to validate.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Validation result.</returns>
        Task<ValidationResult> ValidateAsync(string backupId, CancellationToken ct = default);
    }

    /// <summary>
    /// Interface for the restore subsystem.
    /// </summary>
    public interface IRestoreSubsystem
    {
        /// <summary>
        /// Restores data from a backup.
        /// </summary>
        /// <param name="request">Restore request parameters.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Restore result.</returns>
        Task<RestoreResult> RestoreAsync(RestoreRequest request, CancellationToken ct = default);

        /// <summary>
        /// Gets the progress of an ongoing restore.
        /// </summary>
        /// <param name="restoreId">Restore operation ID.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Restore progress.</returns>
        Task<RestoreProgress> GetProgressAsync(string restoreId, CancellationToken ct = default);

        /// <summary>
        /// Cancels an ongoing restore.
        /// </summary>
        /// <param name="restoreId">Restore operation ID.</param>
        /// <param name="ct">Cancellation token.</param>
        Task CancelAsync(string restoreId, CancellationToken ct = default);

        /// <summary>
        /// Validates restore target compatibility.
        /// </summary>
        /// <param name="request">Restore request to validate.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Validation result.</returns>
        Task<ValidationResult> ValidateTargetAsync(RestoreRequest request, CancellationToken ct = default);

        /// <summary>
        /// Lists available recovery points for an item.
        /// </summary>
        /// <param name="itemId">Item identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Available recovery points.</returns>
        Task<IEnumerable<RecoveryPoint>> ListRecoveryPointsAsync(string itemId, CancellationToken ct = default);
    }

    /// <summary>
    /// Represents a point-in-time recovery option.
    /// </summary>
    public sealed record RecoveryPoint
    {
        /// <summary>Recovery point identifier.</summary>
        public string RecoveryPointId { get; init; } = string.Empty;

        /// <summary>Associated backup ID.</summary>
        public string BackupId { get; init; } = string.Empty;

        /// <summary>Associated version ID, if any.</summary>
        public string? VersionId { get; init; }

        /// <summary>Recovery point timestamp.</summary>
        public DateTimeOffset Timestamp { get; init; }

        /// <summary>Recovery point type.</summary>
        public RecoveryPointType Type { get; init; }

        /// <summary>Whether this recovery point is verified.</summary>
        public bool IsVerified { get; init; }

        /// <summary>Estimated recovery time.</summary>
        public TimeSpan? EstimatedRecoveryTime { get; init; }

        /// <summary>Recovery point description.</summary>
        public string? Description { get; init; }
    }

    /// <summary>
    /// Types of recovery points.
    /// </summary>
    public enum RecoveryPointType
    {
        /// <summary>Full backup recovery point.</summary>
        FullBackup,

        /// <summary>Incremental backup recovery point.</summary>
        IncrementalBackup,

        /// <summary>Snapshot recovery point.</summary>
        Snapshot,

        /// <summary>Version recovery point.</summary>
        Version,

        /// <summary>CDP journal recovery point.</summary>
        CdpJournal,

        /// <summary>Synthetic recovery point.</summary>
        Synthetic
    }

    /// <summary>
    /// Interface for the intelligence subsystem.
    /// </summary>
    public interface IIntelligenceSubsystem
    {
        /// <summary>
        /// Gets whether intelligence features are available.
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Recommends the optimal backup strategy for a scenario.
        /// </summary>
        /// <param name="context">Scenario context.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Strategy recommendation.</returns>
        Task<StrategyRecommendation> RecommendStrategyAsync(
            Dictionary<string, object> context,
            CancellationToken ct = default);

        /// <summary>
        /// Recommends the optimal recovery point.
        /// </summary>
        /// <param name="itemId">Item to recover.</param>
        /// <param name="targetTime">Target recovery time.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Recovery point recommendation.</returns>
        Task<RecoveryPointRecommendation> RecommendRecoveryPointAsync(
            string itemId,
            DateTimeOffset? targetTime,
            CancellationToken ct = default);

        /// <summary>
        /// Detects anomalies in backup data.
        /// </summary>
        /// <param name="backupId">Backup to analyze.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Anomaly detection results.</returns>
        Task<AnomalyDetectionResult> DetectAnomaliesAsync(
            string backupId,
            CancellationToken ct = default);

        /// <summary>
        /// Predicts storage requirements.
        /// </summary>
        /// <param name="daysAhead">Days to predict ahead.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Storage prediction.</returns>
        Task<StoragePrediction> PredictStorageAsync(
            int daysAhead,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Strategy recommendation from intelligence subsystem.
    /// </summary>
    public sealed record StrategyRecommendation
    {
        /// <summary>Recommended strategy ID.</summary>
        public string StrategyId { get; init; } = string.Empty;

        /// <summary>Strategy name.</summary>
        public string StrategyName { get; init; } = string.Empty;

        /// <summary>Confidence score (0.0 - 1.0).</summary>
        public double Confidence { get; init; }

        /// <summary>Reasoning for the recommendation.</summary>
        public string Reasoning { get; init; } = string.Empty;

        /// <summary>Alternative strategies.</summary>
        public IReadOnlyList<string> Alternatives { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Recovery point recommendation from intelligence subsystem.
    /// </summary>
    public sealed record RecoveryPointRecommendation
    {
        /// <summary>Recommended recovery point ID.</summary>
        public string RecoveryPointId { get; init; } = string.Empty;

        /// <summary>Confidence score (0.0 - 1.0).</summary>
        public double Confidence { get; init; }

        /// <summary>Reasoning for the recommendation.</summary>
        public string Reasoning { get; init; } = string.Empty;

        /// <summary>Estimated data loss if recovered to this point.</summary>
        public TimeSpan? EstimatedDataLoss { get; init; }

        /// <summary>Estimated recovery time.</summary>
        public TimeSpan? EstimatedRecoveryTime { get; init; }
    }

    /// <summary>
    /// Anomaly detection results.
    /// </summary>
    public sealed record AnomalyDetectionResult
    {
        /// <summary>Whether anomalies were detected.</summary>
        public bool AnomaliesDetected { get; init; }

        /// <summary>List of detected anomalies.</summary>
        public IReadOnlyList<DetectedAnomaly> Anomalies { get; init; } = Array.Empty<DetectedAnomaly>();

        /// <summary>Overall risk score (0.0 - 1.0).</summary>
        public double RiskScore { get; init; }

        /// <summary>Analysis timestamp.</summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// A detected anomaly.
    /// </summary>
    public sealed record DetectedAnomaly
    {
        /// <summary>Anomaly type.</summary>
        public string AnomalyType { get; init; } = string.Empty;

        /// <summary>Severity level.</summary>
        public AnomalySeverity Severity { get; init; }

        /// <summary>Description of the anomaly.</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>Affected items.</summary>
        public IReadOnlyList<string> AffectedItems { get; init; } = Array.Empty<string>();

        /// <summary>Recommended action.</summary>
        public string? RecommendedAction { get; init; }
    }

    /// <summary>
    /// Anomaly severity levels.
    /// </summary>
    public enum AnomalySeverity
    {
        /// <summary>Informational anomaly.</summary>
        Info,

        /// <summary>Low severity.</summary>
        Low,

        /// <summary>Medium severity.</summary>
        Medium,

        /// <summary>High severity - potential ransomware.</summary>
        High,

        /// <summary>Critical - likely ransomware or corruption.</summary>
        Critical
    }

    /// <summary>
    /// Storage prediction results.
    /// </summary>
    public sealed record StoragePrediction
    {
        /// <summary>Prediction end date.</summary>
        public DateTimeOffset PredictionDate { get; init; }

        /// <summary>Predicted total storage bytes.</summary>
        public long PredictedStorageBytes { get; init; }

        /// <summary>Predicted growth rate per day.</summary>
        public long PredictedDailyGrowthBytes { get; init; }

        /// <summary>Days until storage is full (null if not applicable).</summary>
        public int? DaysUntilFull { get; init; }

        /// <summary>Confidence score (0.0 - 1.0).</summary>
        public double Confidence { get; init; }

        /// <summary>Recommendations for storage optimization.</summary>
        public IReadOnlyList<string> Recommendations { get; init; } = Array.Empty<string>();
    }
}
