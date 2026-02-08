using DataWarehouse.Plugins.UltimateDataProtection.Versioning;

namespace DataWarehouse.Plugins.UltimateDataProtection.Configuration
{
    /// <summary>
    /// Master configuration record for the Ultimate Data Protection plugin.
    /// Controls all subsystems: backup, versioning, and intelligence.
    /// </summary>
    public sealed record DataProtectionConfig
    {
        /// <summary>Enable the backup subsystem.</summary>
        public bool EnableBackup { get; init; } = true;

        /// <summary>Enable the versioning subsystem.</summary>
        public bool EnableVersioning { get; init; } = true;

        /// <summary>Enable the intelligence subsystem.</summary>
        public bool EnableIntelligence { get; init; } = true;

        /// <summary>Backup subsystem configuration.</summary>
        public BackupConfig Backup { get; init; } = new();

        /// <summary>Versioning subsystem configuration.</summary>
        public VersioningConfig Versioning { get; init; } = new();

        /// <summary>Intelligence subsystem configuration.</summary>
        public IntelligenceConfig Intelligence { get; init; } = new();

        /// <summary>Multi-instance profile name for isolating configurations.</summary>
        public string? ProfileName { get; init; }

        /// <summary>Enable global deduplication across all backups.</summary>
        public bool EnableGlobalDeduplication { get; init; } = true;

        /// <summary>Enable global compression for all operations.</summary>
        public bool EnableGlobalCompression { get; init; } = true;

        /// <summary>Global encryption settings.</summary>
        public EncryptionSettings? EncryptionSettings { get; init; }

        /// <summary>Storage location for backup catalog and metadata.</summary>
        public string CatalogStoragePath { get; init; } = "./data/protection/catalog";

        /// <summary>Storage location for backup data.</summary>
        public string DataStoragePath { get; init; } = "./data/protection/backups";

        /// <summary>Maximum parallel operations (0 = auto-detect).</summary>
        public int MaxParallelOperations { get; init; } = 0;

        /// <summary>Enable automatic integrity verification.</summary>
        public bool EnableAutoVerification { get; init; } = true;

        /// <summary>Verification schedule (cron expression).</summary>
        public string? VerificationSchedule { get; init; }

        /// <summary>
        /// Validates the configuration for correctness.
        /// </summary>
        /// <returns>Validation errors, or empty if valid.</returns>
        public IEnumerable<string> Validate()
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(CatalogStoragePath))
                errors.Add("CatalogStoragePath cannot be empty");

            if (string.IsNullOrWhiteSpace(DataStoragePath))
                errors.Add("DataStoragePath cannot be empty");

            if (MaxParallelOperations < 0)
                errors.Add("MaxParallelOperations cannot be negative");

            if (EnableBackup)
            {
                errors.AddRange(Backup.Validate());
            }

            if (EnableVersioning)
            {
                errors.AddRange(Versioning.Validate());
            }

            if (EnableIntelligence)
            {
                errors.AddRange(Intelligence.Validate());
            }

            return errors;
        }
    }

    /// <summary>
    /// Configuration for the backup subsystem.
    /// Controls backup strategies, schedules, and retention policies.
    /// </summary>
    public sealed record BackupConfig
    {
        /// <summary>Flags indicating which backup strategies are enabled.</summary>
        public BackupStrategyFlags EnabledStrategies { get; init; } = BackupStrategyFlags.All;

        /// <summary>Backup schedules to execute automatically.</summary>
        public IReadOnlyList<ScheduleConfig> Schedules { get; init; } = Array.Empty<ScheduleConfig>();

        /// <summary>Default retention policy to apply to backups.</summary>
        public string DefaultRetentionPolicy { get; init; } = "standard";

        /// <summary>Default compression algorithm for backups.</summary>
        public string DefaultCompressionAlgorithm { get; init; } = "Zstd";

        /// <summary>Default compression level (1-9).</summary>
        public int DefaultCompressionLevel { get; init; } = 6;

        /// <summary>Enable encryption by default for all backups.</summary>
        public bool DefaultEnableEncryption { get; init; } = false;

        /// <summary>Default encryption algorithm.</summary>
        public string DefaultEncryptionAlgorithm { get; init; } = "AES256";

        /// <summary>Enable deduplication by default for all backups.</summary>
        public bool DefaultEnableDeduplication { get; init; } = true;

        /// <summary>Deduplication block size in bytes.</summary>
        public int DeduplicationBlockSize { get; init; } = 4096;

        /// <summary>Default bandwidth limit in bytes per second (0 = unlimited).</summary>
        public long DefaultBandwidthLimit { get; init; } = 0;

        /// <summary>Default number of parallel backup streams.</summary>
        public int DefaultParallelStreams { get; init; } = 4;

        /// <summary>Enable automatic backup verification.</summary>
        public bool EnableAutoVerification { get; init; } = true;

        /// <summary>Cloud backup targets configuration.</summary>
        public IReadOnlyList<CloudTargetConfig> CloudTargets { get; init; } = Array.Empty<CloudTargetConfig>();

        /// <summary>Tape backup configuration.</summary>
        public TapeConfig? TapeConfig { get; init; }

        /// <summary>Maximum backup retention period (null = configurable per backup).</summary>
        public TimeSpan? MaxRetentionPeriod { get; init; }

        /// <summary>Enable immutable backups (ransomware protection).</summary>
        public bool EnableImmutableBackups { get; init; } = true;

        /// <summary>Immutable backup lock duration.</summary>
        public TimeSpan ImmutableLockDuration { get; init; } = TimeSpan.FromDays(30);

        /// <summary>
        /// Validates the backup configuration.
        /// </summary>
        /// <returns>Validation errors.</returns>
        public IEnumerable<string> Validate()
        {
            var errors = new List<string>();

            if (DefaultCompressionLevel < 1 || DefaultCompressionLevel > 9)
                errors.Add("DefaultCompressionLevel must be between 1 and 9");

            if (DeduplicationBlockSize < 512 || DeduplicationBlockSize > 1048576)
                errors.Add("DeduplicationBlockSize must be between 512 and 1048576");

            if (DefaultParallelStreams < 1)
                errors.Add("DefaultParallelStreams must be at least 1");

            if (DefaultBandwidthLimit < 0)
                errors.Add("DefaultBandwidthLimit cannot be negative");

            if (ImmutableLockDuration < TimeSpan.Zero)
                errors.Add("ImmutableLockDuration cannot be negative");

            foreach (var schedule in Schedules)
            {
                errors.AddRange(schedule.Validate());
            }

            return errors;
        }
    }

    /// <summary>
    /// Flags for enabling/disabling backup strategies.
    /// </summary>
    [Flags]
    public enum BackupStrategyFlags : long
    {
        /// <summary>No strategies enabled.</summary>
        None = 0,

        /// <summary>Full backup strategies.</summary>
        Full = 1 << 0,

        /// <summary>Incremental backup strategies.</summary>
        Incremental = 1 << 1,

        /// <summary>Differential backup strategies.</summary>
        Differential = 1 << 2,

        /// <summary>Continuous data protection (CDP).</summary>
        ContinuousProtection = 1 << 3,

        /// <summary>Snapshot strategies.</summary>
        Snapshot = 1 << 4,

        /// <summary>Replication strategies.</summary>
        Replication = 1 << 5,

        /// <summary>Archive strategies.</summary>
        Archive = 1 << 6,

        /// <summary>Disaster recovery strategies.</summary>
        DisasterRecovery = 1 << 7,

        /// <summary>Cloud backup strategies.</summary>
        Cloud = 1 << 8,

        /// <summary>Database-specific strategies.</summary>
        Database = 1 << 9,

        /// <summary>Kubernetes workload strategies.</summary>
        Kubernetes = 1 << 10,

        /// <summary>VMware integration strategies.</summary>
        VMware = 1 << 11,

        /// <summary>Hyper-V integration strategies.</summary>
        HyperV = 1 << 12,

        /// <summary>Intelligent/AI-driven strategies.</summary>
        Intelligence = 1 << 13,

        /// <summary>All strategies enabled.</summary>
        All = ~0L
    }

    /// <summary>
    /// Configuration for a backup schedule.
    /// Defines when and how backups should be executed automatically.
    /// </summary>
    public sealed record ScheduleConfig
    {
        /// <summary>Unique name for this schedule.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Cron expression defining the schedule.</summary>
        public string CronExpression { get; init; } = string.Empty;

        /// <summary>Type of backup strategy to use.</summary>
        public DataProtectionCategory StrategyType { get; init; } = DataProtectionCategory.FullBackup;

        /// <summary>Specific strategy ID to use (null = auto-select).</summary>
        public string? StrategyId { get; init; }

        /// <summary>Conditions that must be met for the backup to run.</summary>
        public ScheduleConditions? Conditions { get; init; }

        /// <summary>Priority level for scheduling (1 = highest).</summary>
        public int Priority { get; init; } = 5;

        /// <summary>Sources to back up.</summary>
        public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();

        /// <summary>Destination for the backup.</summary>
        public string? Destination { get; init; }

        /// <summary>Retention policy for scheduled backups.</summary>
        public string? RetentionPolicy { get; init; }

        /// <summary>Enable this schedule.</summary>
        public bool Enabled { get; init; } = true;

        /// <summary>Tags to apply to backups created by this schedule.</summary>
        public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();

        /// <summary>
        /// Validates the schedule configuration.
        /// </summary>
        /// <returns>Validation errors.</returns>
        public IEnumerable<string> Validate()
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(Name))
                errors.Add("Schedule Name cannot be empty");

            if (string.IsNullOrWhiteSpace(CronExpression))
                errors.Add("Schedule CronExpression cannot be empty");

            if (Priority < 1 || Priority > 10)
                errors.Add("Schedule Priority must be between 1 and 10");

            if (Sources.Count == 0)
                errors.Add($"Schedule '{Name}' must have at least one source");

            return errors;
        }
    }

    /// <summary>
    /// Conditions that must be met for a scheduled backup to run.
    /// </summary>
    public sealed record ScheduleConditions
    {
        /// <summary>Minimum free disk space required (bytes).</summary>
        public long? MinimumFreeDiskSpace { get; init; }

        /// <summary>Maximum allowed CPU usage percentage (0-100).</summary>
        public double? MaxCpuUsage { get; init; }

        /// <summary>Time window when backups are allowed (start).</summary>
        public TimeOnly? AllowedWindowStart { get; init; }

        /// <summary>Time window when backups are allowed (end).</summary>
        public TimeOnly? AllowedWindowEnd { get; init; }

        /// <summary>Days of week when backups are allowed.</summary>
        public DayOfWeek[]? AllowedDays { get; init; }

        /// <summary>Skip backup if system is on battery power.</summary>
        public bool SkipOnBattery { get; init; }

        /// <summary>Skip backup if network is metered.</summary>
        public bool SkipOnMeteredNetwork { get; init; }
    }

    /// <summary>
    /// Configuration for a cloud backup target.
    /// </summary>
    public sealed record CloudTargetConfig
    {
        /// <summary>Target name/identifier.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Cloud provider type.</summary>
        public CloudProvider Provider { get; init; }

        /// <summary>Connection string or endpoint URL.</summary>
        public string ConnectionString { get; init; } = string.Empty;

        /// <summary>Bucket or container name.</summary>
        public string BucketName { get; init; } = string.Empty;

        /// <summary>Credentials reference (key name in secret store).</summary>
        public string? CredentialsRef { get; init; }

        /// <summary>Storage class/tier for uploads.</summary>
        public string? StorageClass { get; init; }

        /// <summary>Enable server-side encryption.</summary>
        public bool EnableServerSideEncryption { get; init; } = true;

        /// <summary>Maximum upload bandwidth (bytes/sec, 0 = unlimited).</summary>
        public long MaxUploadBandwidth { get; init; } = 0;

        /// <summary>Retry policy for failed operations.</summary>
        public RetryPolicy RetryPolicy { get; init; } = new();
    }

    /// <summary>
    /// Cloud provider types.
    /// </summary>
    public enum CloudProvider
    {
        /// <summary>Amazon S3.</summary>
        AwsS3,

        /// <summary>Azure Blob Storage.</summary>
        AzureBlob,

        /// <summary>Google Cloud Storage.</summary>
        GoogleCloud,

        /// <summary>MinIO or S3-compatible.</summary>
        S3Compatible,

        /// <summary>Wasabi.</summary>
        Wasabi,

        /// <summary>Backblaze B2.</summary>
        BackblazeB2,

        /// <summary>Custom provider.</summary>
        Custom
    }

    /// <summary>
    /// Retry policy for failed operations.
    /// </summary>
    public sealed record RetryPolicy
    {
        /// <summary>Maximum retry attempts.</summary>
        public int MaxRetries { get; init; } = 3;

        /// <summary>Delay between retries.</summary>
        public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

        /// <summary>Use exponential backoff.</summary>
        public bool UseExponentialBackoff { get; init; } = true;

        /// <summary>Maximum backoff duration.</summary>
        public TimeSpan MaxBackoffDuration { get; init; } = TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Configuration for tape backup operations.
    /// </summary>
    public sealed record TapeConfig
    {
        /// <summary>Tape library device path.</summary>
        public string DevicePath { get; init; } = string.Empty;

        /// <summary>Media changer device path.</summary>
        public string? ChangerPath { get; init; }

        /// <summary>Block size for tape writes.</summary>
        public int BlockSize { get; init; } = 65536;

        /// <summary>Enable hardware compression.</summary>
        public bool EnableHardwareCompression { get; init; } = true;

        /// <summary>Eject tape after backup completion.</summary>
        public bool EjectAfterBackup { get; init; } = false;

        /// <summary>Barcode scanner enabled.</summary>
        public bool BarcodeScannerEnabled { get; init; } = false;
    }

    /// <summary>
    /// Configuration for the versioning subsystem.
    /// Controls how versions are created, stored, and managed.
    /// </summary>
    public sealed record VersioningConfig
    {
        /// <summary>Versioning mode.</summary>
        public VersioningMode Mode { get; init; } = VersioningMode.EventBased;

        /// <summary>Minimum interval between version creations.</summary>
        public TimeSpan? MinimumInterval { get; init; } = TimeSpan.FromMinutes(5);

        /// <summary>Maximum versions to retain per item (null = unlimited).</summary>
        public int? MaxVersionsPerItem { get; init; }

        /// <summary>Retention period for versions (null = indefinite).</summary>
        public TimeSpan? RetentionPeriod { get; init; }

        /// <summary>Storage tier transition rules.</summary>
        public IReadOnlyList<TierTransitionRule> TierTransitions { get; init; } = new[]
        {
            new TierTransitionRule
            {
                AgeThreshold = TimeSpan.FromDays(30),
                TargetTier = StorageTier.InfrequentAccess,
                SkipImmutable = false,
                SkipLegalHold = true
            },
            new TierTransitionRule
            {
                AgeThreshold = TimeSpan.FromDays(90),
                TargetTier = StorageTier.Archive,
                SkipImmutable = false,
                SkipLegalHold = true
            },
            new TierTransitionRule
            {
                AgeThreshold = TimeSpan.FromDays(365),
                TargetTier = StorageTier.DeepArchive,
                SkipImmutable = false,
                SkipLegalHold = true
            }
        };

        /// <summary>Intelligence threshold for intelligent versioning mode (0.0 - 1.0).</summary>
        public double IntelligenceThreshold { get; init; } = 0.5;

        /// <summary>Events that trigger version creation (for event-based mode).</summary>
        public IReadOnlyList<string> TriggerEvents { get; init; } = new[]
        {
            "save",
            "commit",
            "publish",
            "schema-change",
            "major-update"
        };

        /// <summary>Schedule expression for scheduled versioning mode.</summary>
        public string? ScheduleExpression { get; init; }

        /// <summary>Enable compression for version storage.</summary>
        public bool EnableCompression { get; init; } = true;

        /// <summary>Compression algorithm for versions.</summary>
        public string CompressionAlgorithm { get; init; } = "Zstd";

        /// <summary>Enable deduplication for version storage.</summary>
        public bool EnableDeduplication { get; init; } = true;

        /// <summary>Minimum change size to trigger versioning (bytes).</summary>
        public long? MinimumChangeSize { get; init; }

        /// <summary>Minimum change percentage to trigger versioning.</summary>
        public double? MinimumChangePercentage { get; init; }

        /// <summary>Enable automatic version verification.</summary>
        public bool EnableAutoVerification { get; init; } = true;

        /// <summary>Verification interval.</summary>
        public TimeSpan VerificationInterval { get; init; } = TimeSpan.FromDays(7);

        /// <summary>Storage location for version metadata.</summary>
        public string? MetadataStoragePath { get; init; }

        /// <summary>Storage location for version content.</summary>
        public string? ContentStoragePath { get; init; }

        /// <summary>
        /// Validates the versioning configuration.
        /// </summary>
        /// <returns>Validation errors.</returns>
        public IEnumerable<string> Validate()
        {
            var errors = new List<string>();

            if (MinimumInterval.HasValue && MinimumInterval.Value < TimeSpan.Zero)
                errors.Add("MinimumInterval cannot be negative");

            if (MaxVersionsPerItem.HasValue && MaxVersionsPerItem.Value < 1)
                errors.Add("MaxVersionsPerItem must be at least 1");

            if (RetentionPeriod.HasValue && RetentionPeriod.Value < TimeSpan.Zero)
                errors.Add("RetentionPeriod cannot be negative");

            if (IntelligenceThreshold < 0.0 || IntelligenceThreshold > 1.0)
                errors.Add("IntelligenceThreshold must be between 0.0 and 1.0");

            if (Mode == VersioningMode.Scheduled && string.IsNullOrWhiteSpace(ScheduleExpression))
                errors.Add("ScheduleExpression is required for Scheduled versioning mode");

            if (VerificationInterval < TimeSpan.Zero)
                errors.Add("VerificationInterval cannot be negative");

            return errors;
        }
    }

    /// <summary>
    /// Configuration for the intelligence subsystem.
    /// Controls AI-driven features like anomaly detection and recommendations.
    /// </summary>
    public sealed record IntelligenceConfig
    {
        /// <summary>Enable anomaly detection for backups and data.</summary>
        public bool EnableAnomalyDetection { get; init; } = true;

        /// <summary>Enable predictive analysis and forecasting.</summary>
        public bool EnablePredictions { get; init; } = true;

        /// <summary>Enable AI-driven recommendations.</summary>
        public bool EnableRecommendations { get; init; } = true;

        /// <summary>Message bus topic for AI provider communication.</summary>
        public string AIProviderTopic { get; init; } = "ai.intelligence.request";

        /// <summary>Anomaly detection threshold (0.0 - 1.0).</summary>
        public double AnomalyThreshold { get; init; } = 0.75;

        /// <summary>Backup size deviation threshold for anomaly detection (percentage).</summary>
        public double BackupSizeDeviationThreshold { get; init; } = 50.0;

        /// <summary>Backup duration deviation threshold for anomaly detection (percentage).</summary>
        public double BackupDurationDeviationThreshold { get; init; } = 100.0;

        /// <summary>Enable pattern learning from backup history.</summary>
        public bool EnablePatternLearning { get; init; } = true;

        /// <summary>Minimum history days required for pattern analysis.</summary>
        public int MinimumHistoryDays { get; init; } = 7;

        /// <summary>Enable automatic optimization suggestions.</summary>
        public bool EnableAutoOptimization { get; init; } = true;

        /// <summary>Enable capacity forecasting.</summary>
        public bool EnableCapacityForecasting { get; init; } = true;

        /// <summary>Forecast horizon in days.</summary>
        public int ForecastHorizonDays { get; init; } = 90;

        /// <summary>Enable failure prediction.</summary>
        public bool EnableFailurePrediction { get; init; } = true;

        /// <summary>Failure prediction confidence threshold (0.0 - 1.0).</summary>
        public double FailurePredictionThreshold { get; init; } = 0.8;

        /// <summary>Enable intelligent backup scheduling.</summary>
        public bool EnableIntelligentScheduling { get; init; } = true;

        /// <summary>Model update interval.</summary>
        public TimeSpan ModelUpdateInterval { get; init; } = TimeSpan.FromHours(24);

        /// <summary>Maximum AI request timeout.</summary>
        public TimeSpan AIRequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>Enable telemetry collection for model improvement.</summary>
        public bool EnableTelemetry { get; init; } = true;

        /// <summary>
        /// Validates the intelligence configuration.
        /// </summary>
        /// <returns>Validation errors.</returns>
        public IEnumerable<string> Validate()
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(AIProviderTopic))
                errors.Add("AIProviderTopic cannot be empty");

            if (AnomalyThreshold < 0.0 || AnomalyThreshold > 1.0)
                errors.Add("AnomalyThreshold must be between 0.0 and 1.0");

            if (BackupSizeDeviationThreshold < 0.0)
                errors.Add("BackupSizeDeviationThreshold cannot be negative");

            if (BackupDurationDeviationThreshold < 0.0)
                errors.Add("BackupDurationDeviationThreshold cannot be negative");

            if (MinimumHistoryDays < 1)
                errors.Add("MinimumHistoryDays must be at least 1");

            if (ForecastHorizonDays < 1)
                errors.Add("ForecastHorizonDays must be at least 1");

            if (FailurePredictionThreshold < 0.0 || FailurePredictionThreshold > 1.0)
                errors.Add("FailurePredictionThreshold must be between 0.0 and 1.0");

            if (ModelUpdateInterval < TimeSpan.Zero)
                errors.Add("ModelUpdateInterval cannot be negative");

            if (AIRequestTimeout < TimeSpan.Zero)
                errors.Add("AIRequestTimeout cannot be negative");

            return errors;
        }
    }

    /// <summary>
    /// Encryption settings for data protection.
    /// </summary>
    public sealed record EncryptionSettings
    {
        /// <summary>Default encryption algorithm.</summary>
        public string Algorithm { get; init; } = "AES256-GCM";

        /// <summary>Key derivation function.</summary>
        public string KeyDerivationFunction { get; init; } = "PBKDF2";

        /// <summary>Key derivation iterations.</summary>
        public int KeyDerivationIterations { get; init; } = 100000;

        /// <summary>Master key reference (key name in key vault).</summary>
        public string? MasterKeyRef { get; init; }

        /// <summary>Enable key rotation.</summary>
        public bool EnableKeyRotation { get; init; } = true;

        /// <summary>Key rotation interval.</summary>
        public TimeSpan KeyRotationInterval { get; init; } = TimeSpan.FromDays(90);

        /// <summary>Use hardware security module (HSM) if available.</summary>
        public bool UseHSM { get; init; } = false;

        /// <summary>HSM provider configuration.</summary>
        public string? HSMProvider { get; init; }
    }
}
