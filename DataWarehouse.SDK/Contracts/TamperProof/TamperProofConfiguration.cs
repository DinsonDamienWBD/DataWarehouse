// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System.Text.Json.Serialization;

namespace DataWarehouse.SDK.Contracts.TamperProof;

/// <summary>
/// Main configuration for tamper-proof storage.
/// Structural settings become immutable after first write (sealed).
/// Behavioral settings can be changed at runtime.
/// </summary>
public class TamperProofConfiguration
{
    // === STRUCTURAL (Immutable after seal) ===

    /// <summary>
    /// Storage instances configuration for the four tiers (data, metadata, worm, blockchain).
    /// Cannot be changed after the first write operation.
    /// </summary>
    public required StorageInstancesConfig StorageInstances { get; init; }

    /// <summary>
    /// RAID configuration for data sharding.
    /// Determines how data is split and protected across shards.
    /// Cannot be changed after the first write operation.
    /// </summary>
    public required RaidConfig Raid { get; init; }

    /// <summary>
    /// Hash algorithm for integrity verification.
    /// Cannot be changed after the first write operation.
    /// Default: SHA256.
    /// </summary>
    public HashAlgorithmType HashAlgorithm { get; init; } = HashAlgorithmType.SHA256;

    /// <summary>
    /// Consensus mode for distributed deployments.
    /// Cannot be changed after the first write operation.
    /// Default: SingleWriter.
    /// </summary>
    public ConsensusMode ConsensusMode { get; init; } = ConsensusMode.SingleWriter;

    /// <summary>
    /// WORM enforcement mode.
    /// Cannot be changed after the first write operation.
    /// Default: Software.
    /// </summary>
    public WormEnforcementMode WormMode { get; init; } = WormEnforcementMode.Software;

    /// <summary>
    /// Default retention period for WORM storage.
    /// Cannot be changed after the first write operation.
    /// Default: 7 years.
    /// </summary>
    public TimeSpan DefaultRetentionPeriod { get; init; } = TimeSpan.FromDays(7 * 365);

    /// <summary>
    /// Behavior when a transactional write fails partway through.
    /// Cannot be changed after the first write operation.
    /// Default: Strict (rollback all on failure).
    /// </summary>
    public TransactionFailureBehavior TransactionFailureBehavior { get; init; } = TransactionFailureBehavior.Strict;

    // === BEHAVIORAL (Can change at runtime) ===

    /// <summary>
    /// Behavior when tampering is detected. Changeable at runtime.
    /// Default: AutoRecoverWithReport.
    /// </summary>
    public TamperRecoveryBehavior RecoveryBehavior { get; set; } = TamperRecoveryBehavior.AutoRecoverWithReport;

    /// <summary>
    /// Default read mode for verification. Changeable at runtime.
    /// Default: Verified.
    /// </summary>
    public ReadMode DefaultReadMode { get; set; } = ReadMode.Verified;

    /// <summary>
    /// Blockchain batching configuration. Changeable at runtime.
    /// </summary>
    public BlockchainBatchConfig BlockchainBatching { get; set; } = new();

    /// <summary>
    /// Alert/notification settings for tamper incidents. Changeable at runtime.
    /// </summary>
    public AlertConfig Alerts { get; set; } = new();

    /// <summary>
    /// Content padding configuration (Phase 1 padding). Changeable at runtime.
    /// Applied before integrity hash, hides true data size.
    /// </summary>
    public ContentPaddingConfig ContentPadding { get; set; } = new();

    /// <summary>
    /// Maximum number of concurrent write operations. Changeable at runtime.
    /// Default: 4 (one per storage tier).
    /// </summary>
    public int MaxConcurrentWrites { get; set; } = 4;

    /// <summary>
    /// Timeout for individual storage operations. Changeable at runtime.
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Interval between background integrity scans. Changeable at runtime.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan? BackgroundScanInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Number of blocks to scan per batch during background integrity scans.
    /// Changeable at runtime.
    /// Default: 100.
    /// </summary>
    public int BackgroundScanBatchSize { get; set; } = 100;

    /// <summary>
    /// Whether to automatically start the background integrity scanner on plugin initialization.
    /// Default: false (manual start required).
    /// </summary>
    public bool AutoStartBackgroundScanner { get; set; } = false;

    /// <summary>
    /// Creates a deep clone of this configuration.
    /// </summary>
    public TamperProofConfiguration Clone()
    {
        return new TamperProofConfiguration
        {
            StorageInstances = StorageInstances.Clone(),
            Raid = Raid.Clone(),
            HashAlgorithm = HashAlgorithm,
            ConsensusMode = ConsensusMode,
            WormMode = WormMode,
            DefaultRetentionPeriod = DefaultRetentionPeriod,
            TransactionFailureBehavior = TransactionFailureBehavior,
            RecoveryBehavior = RecoveryBehavior,
            DefaultReadMode = DefaultReadMode,
            BlockchainBatching = BlockchainBatching.Clone(),
            Alerts = Alerts.Clone(),
            ContentPadding = ContentPadding.Clone(),
            MaxConcurrentWrites = MaxConcurrentWrites,
            OperationTimeout = OperationTimeout,
            BackgroundScanInterval = BackgroundScanInterval,
            BackgroundScanBatchSize = BackgroundScanBatchSize,
            AutoStartBackgroundScanner = AutoStartBackgroundScanner
        };
    }

    /// <summary>
    /// Validates the configuration for consistency and completeness.
    /// </summary>
    /// <returns>List of validation errors, empty if valid.</returns>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        // Validate storage instances
        if (StorageInstances is null)
        {
            errors.Add("StorageInstances configuration is required.");
        }
        else
        {
            errors.AddRange(StorageInstances.Validate());
        }

        // Validate RAID config
        if (Raid is null)
        {
            errors.Add("Raid configuration is required.");
        }
        else
        {
            errors.AddRange(Raid.Validate());
        }

        // Validate timeouts
        if (OperationTimeout <= TimeSpan.Zero)
        {
            errors.Add("OperationTimeout must be positive.");
        }

        if (MaxConcurrentWrites <= 0)
        {
            errors.Add("MaxConcurrentWrites must be positive.");
        }

        // Validate retention period
        if (DefaultRetentionPeriod <= TimeSpan.Zero)
        {
            errors.Add("DefaultRetentionPeriod must be positive.");
        }

        return errors;
    }
}

/// <summary>
/// Configuration for the four storage instances.
/// </summary>
public class StorageInstancesConfig
{
    /// <summary>
    /// Primary data storage instance for RAID shards.
    /// </summary>
    public required StorageInstanceConfig Data { get; init; }

    /// <summary>
    /// Metadata/manifest storage instance.
    /// </summary>
    public required StorageInstanceConfig Metadata { get; init; }

    /// <summary>
    /// WORM vault storage instance for disaster recovery.
    /// </summary>
    public required StorageInstanceConfig Worm { get; init; }

    /// <summary>
    /// Blockchain anchor storage instance for audit trail.
    /// </summary>
    public required StorageInstanceConfig Blockchain { get; init; }

    /// <summary>
    /// Gets all configured instances as a dictionary keyed by instance ID.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, StorageInstanceConfig> AllInstances => new Dictionary<string, StorageInstanceConfig>
    {
        [Data.InstanceId] = Data,
        [Metadata.InstanceId] = Metadata,
        [Worm.InstanceId] = Worm,
        [Blockchain.InstanceId] = Blockchain
    };

    /// <summary>
    /// Creates a deep clone of this configuration.
    /// </summary>
    public StorageInstancesConfig Clone()
    {
        return new StorageInstancesConfig
        {
            Data = Data.Clone(),
            Metadata = Metadata.Clone(),
            Worm = Worm.Clone(),
            Blockchain = Blockchain.Clone()
        };
    }

    /// <summary>
    /// Validates the storage instances configuration.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (Data is null)
        {
            errors.Add("Data storage instance is required.");
        }
        else
        {
            errors.AddRange(Data.Validate().Select(e => $"Data instance: {e}"));
        }

        if (Metadata is null)
        {
            errors.Add("Metadata storage instance is required.");
        }
        else
        {
            errors.AddRange(Metadata.Validate().Select(e => $"Metadata instance: {e}"));
        }

        if (Worm is null)
        {
            errors.Add("Worm storage instance is required.");
        }
        else
        {
            errors.AddRange(Worm.Validate().Select(e => $"Worm instance: {e}"));
        }

        if (Blockchain is null)
        {
            errors.Add("Blockchain storage instance is required.");
        }
        else
        {
            errors.AddRange(Blockchain.Validate().Select(e => $"Blockchain instance: {e}"));
        }

        // Check for duplicate instance IDs
        var instanceIds = new[] { Data?.InstanceId, Metadata?.InstanceId, Worm?.InstanceId, Blockchain?.InstanceId }
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList();

        if (instanceIds.Count != instanceIds.Distinct().Count())
        {
            errors.Add("Storage instance IDs must be unique.");
        }

        return errors;
    }
}

/// <summary>
/// Configuration for a single storage instance.
/// </summary>
public class StorageInstanceConfig
{
    /// <summary>
    /// Unique instance identifier (e.g., "data", "worm").
    /// </summary>
    public required string InstanceId { get; init; }

    /// <summary>
    /// Plugin ID to use for this instance (e.g., "com.datawarehouse.storage.s3").
    /// </summary>
    public required string PluginId { get; init; }

    /// <summary>
    /// Plugin-specific configuration parameters.
    /// </summary>
    public Dictionary<string, object> PluginConfig { get; init; } = new();

    /// <summary>
    /// Connection string or path for the storage (plugin-specific interpretation).
    /// </summary>
    public string? ConnectionString { get; init; }

    /// <summary>
    /// Whether this instance is required for operations.
    /// If false, operations can proceed in degraded mode without this instance.
    /// Default: true.
    /// </summary>
    public bool IsRequired { get; init; } = true;

    /// <summary>
    /// Creates a deep clone of this configuration.
    /// </summary>
    public StorageInstanceConfig Clone()
    {
        return new StorageInstanceConfig
        {
            InstanceId = InstanceId,
            PluginId = PluginId,
            PluginConfig = new Dictionary<string, object>(PluginConfig),
            ConnectionString = ConnectionString,
            IsRequired = IsRequired
        };
    }

    /// <summary>
    /// Validates the storage instance configuration.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(InstanceId))
        {
            errors.Add("InstanceId is required.");
        }

        if (string.IsNullOrWhiteSpace(PluginId))
        {
            errors.Add("PluginId is required.");
        }

        return errors;
    }
}

/// <summary>
/// RAID configuration for data sharding.
/// </summary>
public class RaidConfig
{
    /// <summary>
    /// Number of data shards to split data into.
    /// Default: 4.
    /// </summary>
    public int DataShards { get; init; } = 4;

    /// <summary>
    /// Number of parity shards for redundancy.
    /// Default: 2.
    /// </summary>
    public int ParityShards { get; init; } = 2;

    /// <summary>
    /// Fixed shard size in bytes. If 0, shards are sized based on data.
    /// Default: 0 (variable sizing).
    /// </summary>
    public long FixedShardSize { get; init; } = 0;

    /// <summary>
    /// Minimum shard size in bytes when using variable sizing.
    /// Default: 64KB.
    /// </summary>
    public long MinShardSize { get; init; } = 64 * 1024;

    /// <summary>
    /// Maximum shard size in bytes when using variable sizing.
    /// Default: 64MB.
    /// </summary>
    public long MaxShardSize { get; init; } = 64 * 1024 * 1024;

    /// <summary>
    /// Shard padding configuration (optional, user-configurable).
    /// When enabled, final shard is padded to uniform size.
    /// </summary>
    public ShardPaddingConfig Padding { get; init; } = new();

    /// <summary>
    /// Total number of shards (data + parity).
    /// </summary>
    [JsonIgnore]
    public int TotalShards => DataShards + ParityShards;

    /// <summary>
    /// Creates a deep clone of this configuration.
    /// </summary>
    public RaidConfig Clone()
    {
        return new RaidConfig
        {
            DataShards = DataShards,
            ParityShards = ParityShards,
            FixedShardSize = FixedShardSize,
            MinShardSize = MinShardSize,
            MaxShardSize = MaxShardSize,
            Padding = Padding.Clone()
        };
    }

    /// <summary>
    /// Validates the RAID configuration.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (DataShards < 1)
        {
            errors.Add("DataShards must be at least 1.");
        }

        if (ParityShards < 0)
        {
            errors.Add("ParityShards cannot be negative.");
        }

        if (FixedShardSize < 0)
        {
            errors.Add("FixedShardSize cannot be negative.");
        }

        if (MinShardSize <= 0)
        {
            errors.Add("MinShardSize must be positive.");
        }

        if (MaxShardSize < MinShardSize)
        {
            errors.Add("MaxShardSize must be greater than or equal to MinShardSize.");
        }

        if (Padding is not null)
        {
            errors.AddRange(Padding.Validate().Select(e => $"Padding: {e}"));
        }

        return errors;
    }
}

/// <summary>
/// Configuration for shard padding (Phase 3 padding, NOT covered by integrity hash).
/// Optional and user-configurable. Details are saved in manifest for reversal during read.
/// </summary>
public class ShardPaddingConfig
{
    /// <summary>
    /// Whether shard padding is enabled.
    /// When enabled, the final shard is padded to uniform size.
    /// Default: false (disabled by default, user must explicitly enable).
    /// </summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Padding byte value when not using random padding.
    /// Default: 0x00.
    /// </summary>
    public byte PaddingByte { get; init; } = 0x00;

    /// <summary>
    /// Whether to use random padding bytes (more secure but not reproducible).
    /// When enabled, PaddingByte is ignored.
    /// Default: false.
    /// </summary>
    public bool UseRandomPadding { get; init; } = false;

    /// <summary>
    /// Target size to pad shards to. If 0, uses the size of the largest shard.
    /// Default: 0 (automatic).
    /// </summary>
    public long TargetSize { get; init; } = 0;

    /// <summary>
    /// Creates a deep clone of this configuration.
    /// </summary>
    public ShardPaddingConfig Clone()
    {
        return new ShardPaddingConfig
        {
            Enabled = Enabled,
            PaddingByte = PaddingByte,
            UseRandomPadding = UseRandomPadding,
            TargetSize = TargetSize
        };
    }

    /// <summary>
    /// Validates the shard padding configuration.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (TargetSize < 0)
        {
            errors.Add("TargetSize cannot be negative.");
        }

        return errors;
    }
}

/// <summary>
/// Configuration for content padding (Phase 1 padding, covered by integrity hash).
/// Applied to data before integrity hash to hide true data size.
/// </summary>
public class ContentPaddingConfig
{
    /// <summary>
    /// Whether content padding is enabled.
    /// Default: false.
    /// </summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Pad data to a multiple of this size in bytes (e.g., 4096 for block alignment).
    /// Default: 4096.
    /// </summary>
    public int PadToMultipleOf { get; init; } = 4096;

    /// <summary>
    /// Minimum padding to add in bytes.
    /// Default: 0.
    /// </summary>
    public int MinimumPadding { get; init; } = 0;

    /// <summary>
    /// Maximum padding to add in bytes (randomized within min-max range).
    /// Default: 4096.
    /// </summary>
    public int MaximumPadding { get; init; } = 4096;

    /// <summary>
    /// Padding byte value when not using random padding.
    /// Default: 0x00.
    /// </summary>
    public byte PaddingByte { get; init; } = 0x00;

    /// <summary>
    /// Whether to use random padding bytes.
    /// Default: false.
    /// </summary>
    public bool UseRandomPadding { get; init; } = false;

    /// <summary>
    /// Creates a deep clone of this configuration.
    /// </summary>
    public ContentPaddingConfig Clone()
    {
        return new ContentPaddingConfig
        {
            Enabled = Enabled,
            PadToMultipleOf = PadToMultipleOf,
            MinimumPadding = MinimumPadding,
            MaximumPadding = MaximumPadding,
            PaddingByte = PaddingByte,
            UseRandomPadding = UseRandomPadding
        };
    }

    /// <summary>
    /// Validates the content padding configuration.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (PadToMultipleOf <= 0)
        {
            errors.Add("PadToMultipleOf must be positive.");
        }

        if (MinimumPadding < 0)
        {
            errors.Add("MinimumPadding cannot be negative.");
        }

        if (MaximumPadding < MinimumPadding)
        {
            errors.Add("MaximumPadding must be greater than or equal to MinimumPadding.");
        }

        return errors;
    }
}

/// <summary>
/// Blockchain batching configuration for efficient anchoring.
/// </summary>
public class BlockchainBatchConfig
{
    /// <summary>
    /// Maximum number of objects per batch anchor.
    /// Default: 100.
    /// </summary>
    public int MaxBatchSize { get; init; } = 100;

    /// <summary>
    /// Maximum time to wait before flushing a partial batch.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan MaxBatchDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether to wait for anchor confirmation before returning from write.
    /// Default: true.
    /// </summary>
    public bool WaitForConfirmation { get; init; } = true;

    /// <summary>
    /// Number of confirmations required before considering anchor final.
    /// Default: 1.
    /// </summary>
    public int RequiredConfirmations { get; init; } = 1;

    /// <summary>
    /// Creates a deep clone of this configuration.
    /// </summary>
    public BlockchainBatchConfig Clone()
    {
        return new BlockchainBatchConfig
        {
            MaxBatchSize = MaxBatchSize,
            MaxBatchDelay = MaxBatchDelay,
            WaitForConfirmation = WaitForConfirmation,
            RequiredConfirmations = RequiredConfirmations
        };
    }

    /// <summary>
    /// Validates the blockchain batch configuration.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (MaxBatchSize <= 0)
        {
            errors.Add("MaxBatchSize must be positive.");
        }

        if (MaxBatchDelay <= TimeSpan.Zero)
        {
            errors.Add("MaxBatchDelay must be positive.");
        }

        if (RequiredConfirmations < 1)
        {
            errors.Add("RequiredConfirmations must be at least 1.");
        }

        return errors;
    }
}

/// <summary>
/// Alert configuration for tamper incidents.
/// </summary>
public class AlertConfig
{
    /// <summary>
    /// Webhook URLs to notify on tamper detection.
    /// </summary>
    public List<string> WebhookUrls { get; init; } = new();

    /// <summary>
    /// Email addresses for alert notifications.
    /// </summary>
    public List<string> EmailAddresses { get; init; } = new();

    /// <summary>
    /// Whether to publish alerts to the message bus.
    /// Default: true.
    /// </summary>
    public bool PublishToMessageBus { get; init; } = true;

    /// <summary>
    /// Message bus topic for alerts.
    /// Default: "tamperproof.alerts".
    /// </summary>
    public string MessageBusTopic { get; init; } = "tamperproof.alerts";

    /// <summary>
    /// Minimum severity level for alerts (filters out lower severity).
    /// Default: all severities.
    /// </summary>
    public AlertSeverity MinimumSeverity { get; init; } = AlertSeverity.Info;

    /// <summary>
    /// Whether to include full incident details in alerts.
    /// Default: true.
    /// </summary>
    public bool IncludeDetails { get; init; } = true;

    /// <summary>
    /// Creates a deep clone of this configuration.
    /// </summary>
    public AlertConfig Clone()
    {
        return new AlertConfig
        {
            WebhookUrls = new List<string>(WebhookUrls),
            EmailAddresses = new List<string>(EmailAddresses),
            PublishToMessageBus = PublishToMessageBus,
            MessageBusTopic = MessageBusTopic,
            MinimumSeverity = MinimumSeverity,
            IncludeDetails = IncludeDetails
        };
    }
}

/// <summary>
/// Alert severity levels.
/// </summary>
public enum AlertSeverity
{
    /// <summary>Informational alert.</summary>
    Info,

    /// <summary>Warning - attention may be needed.</summary>
    Warning,

    /// <summary>Error - action required.</summary>
    Error,

    /// <summary>Critical - immediate action required.</summary>
    Critical
}

/// <summary>
/// WORM retention policy for an object.
/// </summary>
public class WormRetentionPolicy
{
    /// <summary>
    /// Retention period from write time.
    /// </summary>
    public required TimeSpan RetentionPeriod { get; init; }

    /// <summary>
    /// Absolute expiry time (computed from write time + retention period).
    /// </summary>
    public DateTimeOffset? ExpiryTime { get; init; }

    /// <summary>
    /// Whether this object is under legal hold (prevents deletion even after expiry).
    /// </summary>
    public bool HasLegalHold { get; init; } = false;

    /// <summary>
    /// Legal hold identifiers if under hold.
    /// </summary>
    public List<string> LegalHoldIds { get; init; } = new();

    /// <summary>
    /// Creates a standard retention policy with the specified period.
    /// </summary>
    public static WormRetentionPolicy Standard(TimeSpan period) => new() { RetentionPeriod = period };

    /// <summary>
    /// Creates a policy with legal hold.
    /// </summary>
    public static WormRetentionPolicy WithLegalHold(TimeSpan period, string holdId) => new()
    {
        RetentionPeriod = period,
        HasLegalHold = true,
        LegalHoldIds = new List<string> { holdId }
    };
}
