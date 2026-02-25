namespace DataWarehouse.SDK.Contracts.Pipeline;

/// <summary>
/// Policy hierarchy level. Defines where a pipeline policy is applied.
/// Lower levels inherit from higher levels, with more specific levels overriding general ones.
/// </summary>
public enum PolicyLevel
{
    /// <summary>System-wide default. Set by instance administrator.</summary>
    Instance = 0,
    /// <summary>User group level. Overrides instance for group members.</summary>
    UserGroup = 1,
    /// <summary>Individual user level. Overrides group for this user.</summary>
    User = 2,
    /// <summary>Per-operation level. Overrides user for this specific call.</summary>
    Operation = 3
}

/// <summary>
/// Defines how existing data is handled when a pipeline policy changes.
/// </summary>
public enum MigrationBehavior
{
    /// <summary>Existing data keeps its current pipeline settings.</summary>
    KeepExisting,
    /// <summary>A background job re-processes existing blobs with the new policy.</summary>
    MigrateInBackground,
    /// <summary>Data is re-processed with the new policy on next read access.</summary>
    MigrateOnNextAccess
}

/// <summary>
/// Base class for all policy components (stages, terminals, future policy types).
/// Contains common properties for hierarchical policy resolution:
/// - Enabled: Whether the component is active
/// - PluginId/StrategyName: Which plugin/strategy to use
/// - Parameters: Configuration parameters
/// - AllowChildOverride: Whether child levels can override this configuration
/// </summary>
public abstract class PolicyComponentBase
{
    /// <summary>Whether this component is enabled. Null = inherit from parent level.</summary>
    public bool? Enabled { get; init; }

    /// <summary>Specific plugin ID to use. Null = inherit from parent or auto-select.</summary>
    public string? PluginId { get; init; }

    /// <summary>Specific strategy/algorithm name within the plugin. Null = inherit or auto-select.</summary>
    public string? StrategyName { get; init; }

    /// <summary>Component-specific parameters. Merged with parent parameters (child values override).</summary>
    public Dictionary<string, object>? Parameters { get; init; }

    /// <summary>
    /// If true, child levels (UserGroup/User/Operation) can override this component's settings.
    /// If false, this level's settings are locked and cannot be changed by descendants.
    /// Default is true (allow overrides).
    /// </summary>
    public bool AllowChildOverride { get; init; } = true;

    /// <summary>
    /// Timeout for this component's operations. Null = inherit from parent or use default.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Priority order for execution or fallback selection.
    /// Lower numbers execute first. Null = inherit from parent.
    /// </summary>
    public int? Priority { get; init; }
}

/// <summary>
/// Per-stage policy configuration. Each stage (encryption, compression, RAID, etc.)
/// can be individually configured at any policy level.
/// Inherits common properties from PolicyComponentBase.
/// For type-safe configuration, use the specific derived classes (CompressionStagePolicy, etc.)
/// </summary>
public class PipelineStagePolicy : PolicyComponentBase
{
    /// <summary>Stage type identifier (e.g., "Encryption", "Compression", "RAID").</summary>
    public virtual string StageType { get; init; } = string.Empty;

    /// <summary>Execution order override within the pipeline. Null = inherit from parent.</summary>
    public int? Order { get; init; }
}

/// <summary>
/// Compression stage policy with type-safe compression-specific settings.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class CompressionStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for compression.</summary>
    public override string StageType { get; init; } = "Compression";

    /// <summary>
    /// Compression level (0-9 or algorithm-specific).
    /// Higher = better compression, slower. Null = inherit or use default.
    /// </summary>
    public int? CompressionLevel { get; init; }

    /// <summary>
    /// Minimum data size in bytes to apply compression.
    /// Data smaller than this is stored uncompressed. Null = inherit or compress all.
    /// </summary>
    public long? MinSizeToCompress { get; init; }

    /// <summary>
    /// Content types that should NOT be compressed (already compressed formats).
    /// Example: ["image/jpeg", "video/mp4", "application/zip"]
    /// Null = inherit from parent.
    /// </summary>
    public List<string>? ExcludeContentTypes { get; init; }

    /// <summary>
    /// Whether to use dictionary-based compression for better ratios on similar data.
    /// Null = inherit from parent.
    /// </summary>
    public bool? UseDictionary { get; init; }

    /// <summary>
    /// Memory budget for compression in MB. Higher = potentially better compression.
    /// Null = inherit or use default.
    /// </summary>
    public int? MemoryBudgetMB { get; init; }
}

/// <summary>
/// Encryption stage policy with type-safe encryption-specific settings.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class EncryptionStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for encryption.</summary>
    public override string StageType { get; init; } = "Encryption";

    /// <summary>
    /// Key identifier for encryption. Null = inherit or use default key.
    /// </summary>
    public string? KeyId { get; init; }

    /// <summary>
    /// Key version to use. Null = use latest version.
    /// </summary>
    public int? KeyVersion { get; init; }

    /// <summary>
    /// Encryption algorithm override. Null = inherit or use strategy default.
    /// Examples: "AES-256-GCM", "ChaCha20-Poly1305"
    /// </summary>
    public string? Algorithm { get; init; }

    /// <summary>
    /// Key rotation interval. Null = inherit or use default.
    /// </summary>
    public TimeSpan? KeyRotationInterval { get; init; }

    /// <summary>
    /// Whether to use envelope encryption (encrypt data key with master key).
    /// Null = inherit from parent.
    /// </summary>
    public bool? UseEnvelopeEncryption { get; init; }

    /// <summary>
    /// HSM slot/key identifier for hardware-backed encryption.
    /// Null = use software encryption.
    /// </summary>
    public string? HsmKeyId { get; init; }

    /// <summary>
    /// Whether to include authentication tag (AEAD). Default true for GCM modes.
    /// Null = inherit from parent.
    /// </summary>
    public bool? IncludeAuthTag { get; init; }
}

/// <summary>
/// RAID stage policy for data redundancy and striping.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class RaidStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for RAID.</summary>
    public override string StageType { get; init; } = "RAID";

    /// <summary>
    /// RAID level (0=striping, 1=mirroring, 5=parity, 6=double parity, etc.).
    /// Null = inherit from parent.
    /// </summary>
    public int? RaidLevel { get; init; }

    /// <summary>
    /// Number of data chunks for striping. Null = inherit or auto-calculate.
    /// </summary>
    public int? DataChunks { get; init; }

    /// <summary>
    /// Number of parity chunks. Null = inherit based on RAID level.
    /// </summary>
    public int? ParityChunks { get; init; }

    /// <summary>
    /// Chunk size in bytes. Null = inherit or use default.
    /// </summary>
    public int? ChunkSizeBytes { get; init; }

    /// <summary>
    /// Target storage backends for chunk distribution.
    /// Null = inherit or use all available backends.
    /// </summary>
    public List<string>? TargetBackends { get; init; }

    /// <summary>
    /// Whether to verify chunks on read (detect corruption).
    /// Null = inherit from parent.
    /// </summary>
    public bool? VerifyOnRead { get; init; }

    /// <summary>
    /// Whether to auto-repair corrupted chunks from parity.
    /// Null = inherit from parent.
    /// </summary>
    public bool? AutoRepair { get; init; }
}

/// <summary>
/// Integrity/checksum stage policy for data verification.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class IntegrityStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for integrity.</summary>
    public override string StageType { get; init; } = "Integrity";

    /// <summary>
    /// Hash algorithm for integrity verification.
    /// Examples: "SHA256", "SHA512", "BLAKE3", "XXH3"
    /// Null = inherit from parent.
    /// </summary>
    public string? HashAlgorithm { get; init; }

    /// <summary>
    /// Whether to verify integrity on every read.
    /// Null = inherit from parent.
    /// </summary>
    public bool? VerifyOnRead { get; init; }

    /// <summary>
    /// Whether to fail the operation if integrity check fails.
    /// If false, logs warning but continues. Null = inherit from parent.
    /// </summary>
    public bool? FailOnMismatch { get; init; }

    /// <summary>
    /// Whether to store hash in manifest (default) or alongside data.
    /// Null = inherit from parent.
    /// </summary>
    public bool? StoreHashInManifest { get; init; }
}

/// <summary>
/// Deduplication stage policy for eliminating duplicate data.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class DeduplicationStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for deduplication.</summary>
    public override string StageType { get; init; } = "Deduplication";

    /// <summary>
    /// Deduplication scope: "global" (cross-tenant), "tenant", "user", "blob".
    /// Null = inherit from parent.
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>
    /// Chunking algorithm: "fixed", "variable", "content-defined".
    /// Null = inherit from parent.
    /// </summary>
    public string? ChunkingAlgorithm { get; init; }

    /// <summary>
    /// Target chunk size in bytes (for variable chunking, this is average).
    /// Null = inherit from parent.
    /// </summary>
    public int? ChunkSizeBytes { get; init; }

    /// <summary>
    /// Minimum chunk size in bytes (for variable chunking).
    /// Null = inherit from parent.
    /// </summary>
    public int? MinChunkSizeBytes { get; init; }

    /// <summary>
    /// Maximum chunk size in bytes (for variable chunking).
    /// Null = inherit from parent.
    /// </summary>
    public int? MaxChunkSizeBytes { get; init; }

    /// <summary>
    /// Whether to use inline dedup (during write) vs background.
    /// Null = inherit from parent.
    /// </summary>
    public bool? InlineDedup { get; init; }
}

/// <summary>
/// Transit encryption stage policy for data in transit.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class TransitEncryptionStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for transit encryption.</summary>
    public override string StageType { get; init; } = "TransitEncryption";

    /// <summary>
    /// TLS version requirement. Examples: "1.2", "1.3".
    /// Null = inherit from parent.
    /// </summary>
    public string? MinTlsVersion { get; init; }

    /// <summary>
    /// Allowed cipher suites. Null = use secure defaults.
    /// </summary>
    public List<string>? AllowedCipherSuites { get; init; }

    /// <summary>
    /// Whether to require mutual TLS (client certificates).
    /// Null = inherit from parent.
    /// </summary>
    public bool? RequireMutualTls { get; init; }

    /// <summary>
    /// Certificate thumbprint to use for this connection.
    /// Null = use default certificate.
    /// </summary>
    public string? CertificateThumbprint { get; init; }
}

/// <summary>
/// Backup stage policy for data protection and recovery.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class BackupStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for backup.</summary>
    public override string StageType { get; init; } = "Backup";

    /// <summary>
    /// Backup schedule (cron expression or interval).
    /// Examples: "0 2 * * *" (daily at 2am), "PT1H" (every hour)
    /// Null = inherit from parent.
    /// </summary>
    public string? Schedule { get; init; }

    /// <summary>
    /// Backup type: "full", "incremental", "differential", "continuous".
    /// Null = inherit from parent.
    /// </summary>
    public string? BackupType { get; init; }

    /// <summary>
    /// Retention period for backups. Null = inherit from parent.
    /// </summary>
    public TimeSpan? RetentionPeriod { get; init; }

    /// <summary>
    /// Number of backup copies to retain. Null = inherit from parent.
    /// </summary>
    public int? RetainCopies { get; init; }

    /// <summary>
    /// Target storage terminal for backups (e.g., "archive", "offsite").
    /// Null = use default backup terminal.
    /// </summary>
    public string? TargetTerminal { get; init; }

    /// <summary>
    /// Whether to verify backup integrity after creation.
    /// Null = inherit from parent.
    /// </summary>
    public bool? VerifyAfterBackup { get; init; }

    /// <summary>
    /// Whether to compress backups. Null = inherit from parent.
    /// </summary>
    public bool? CompressBackups { get; init; }

    /// <summary>
    /// Whether to encrypt backups. Null = inherit from parent.
    /// </summary>
    public bool? EncryptBackups { get; init; }
}

/// <summary>
/// Replication stage policy for data distribution and disaster recovery.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class ReplicationStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for replication.</summary>
    public override string StageType { get; init; } = "Replication";

    /// <summary>
    /// Replication mode: "sync" (wait for ack), "async" (fire and forget).
    /// Null = inherit from parent.
    /// </summary>
    public string? Mode { get; init; }

    /// <summary>
    /// Target regions for geo-replication.
    /// Null = inherit from parent.
    /// </summary>
    public List<string>? TargetRegions { get; init; }

    /// <summary>
    /// Minimum number of replicas required for write success.
    /// Null = inherit from parent.
    /// </summary>
    public int? MinReplicas { get; init; }

    /// <summary>
    /// Conflict resolution strategy: "last-write-wins", "first-write-wins", "merge", "manual".
    /// Null = inherit from parent.
    /// </summary>
    public string? ConflictResolution { get; init; }

    /// <summary>
    /// Maximum replication lag allowed before alerting.
    /// Null = inherit from parent.
    /// </summary>
    public TimeSpan? MaxLag { get; init; }

    /// <summary>
    /// Whether to replicate deletes. Null = inherit from parent.
    /// </summary>
    public bool? ReplicateDeletes { get; init; }
}

/// <summary>
/// Versioning stage policy for data version management.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class VersioningStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for versioning.</summary>
    public override string StageType { get; init; } = "Versioning";

    /// <summary>
    /// Maximum number of versions to retain per object.
    /// Null = inherit from parent (default: unlimited).
    /// </summary>
    public int? MaxVersions { get; init; }

    /// <summary>
    /// Version retention period. Versions older than this are deleted.
    /// Null = inherit from parent.
    /// </summary>
    public TimeSpan? VersionRetention { get; init; }

    /// <summary>
    /// Whether to keep delete markers. Null = inherit from parent.
    /// </summary>
    public bool? KeepDeleteMarkers { get; init; }

    /// <summary>
    /// Versioning scheme: "timestamp", "sequential", "semantic", "uuid".
    /// Null = inherit from parent.
    /// </summary>
    public string? VersioningScheme { get; init; }

    /// <summary>
    /// Whether to enable branching (git-like version trees).
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableBranching { get; init; }
}

/// <summary>
/// Retention stage policy for data lifecycle management.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class RetentionStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for retention.</summary>
    public override string StageType { get; init; } = "Retention";

    /// <summary>
    /// Minimum retention period (data cannot be deleted before this).
    /// Null = inherit from parent.
    /// </summary>
    public TimeSpan? MinRetention { get; init; }

    /// <summary>
    /// Maximum retention period (data auto-deleted after this).
    /// Null = inherit from parent (no auto-delete).
    /// </summary>
    public TimeSpan? MaxRetention { get; init; }

    /// <summary>
    /// Legal hold status. If true, data cannot be deleted regardless of retention.
    /// Null = inherit from parent.
    /// </summary>
    public bool? LegalHold { get; init; }

    /// <summary>
    /// Retention class: "regulatory", "business", "temporary".
    /// Null = inherit from parent.
    /// </summary>
    public string? RetentionClass { get; init; }

    /// <summary>
    /// Action when retention expires: "delete", "archive", "notify".
    /// Null = inherit from parent.
    /// </summary>
    public string? ExpirationAction { get; init; }
}

/// <summary>
/// Tiering stage policy for automatic data placement.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class TieringStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for tiering.</summary>
    public override string StageType { get; init; } = "Tiering";

    /// <summary>
    /// Initial tier for new data: "hot", "warm", "cold", "archive".
    /// Null = inherit from parent.
    /// </summary>
    public string? InitialTier { get; init; }

    /// <summary>
    /// Time before moving to warm tier. Null = inherit from parent.
    /// </summary>
    public TimeSpan? HotToWarmAfter { get; init; }

    /// <summary>
    /// Time before moving to cold tier. Null = inherit from parent.
    /// </summary>
    public TimeSpan? WarmToColdAfter { get; init; }

    /// <summary>
    /// Time before moving to archive tier. Null = inherit from parent.
    /// </summary>
    public TimeSpan? ColdToArchiveAfter { get; init; }

    /// <summary>
    /// Access count threshold for tier promotion.
    /// Null = inherit from parent.
    /// </summary>
    public int? PromoteOnAccessCount { get; init; }

    /// <summary>
    /// Whether to use AI-based access prediction for tiering.
    /// Null = inherit from parent.
    /// </summary>
    public bool? UseAIPrediction { get; init; }
}

/// <summary>
/// Compliance stage policy for regulatory requirements.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class ComplianceStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for compliance.</summary>
    public override string StageType { get; init; } = "Compliance";

    /// <summary>
    /// Compliance frameworks to enforce: "gdpr", "hipaa", "sox", "pci-dss", "fedramp".
    /// Null = inherit from parent.
    /// </summary>
    public List<string>? Frameworks { get; init; }

    /// <summary>
    /// Data classification: "public", "internal", "confidential", "restricted".
    /// Null = inherit from parent.
    /// </summary>
    public string? DataClassification { get; init; }

    /// <summary>
    /// Geographic restrictions for data residency.
    /// Null = inherit from parent.
    /// </summary>
    public List<string>? AllowedRegions { get; init; }

    /// <summary>
    /// Whether to enable PII detection and masking.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnablePiiDetection { get; init; }

    /// <summary>
    /// Whether to require consent tracking for personal data.
    /// Null = inherit from parent.
    /// </summary>
    public bool? RequireConsent { get; init; }

    /// <summary>
    /// Right-to-erasure handling: "immediate", "scheduled", "audit-only".
    /// Null = inherit from parent.
    /// </summary>
    public string? ErasurePolicy { get; init; }
}

/// <summary>
/// Access control stage policy for authorization.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class AccessControlStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for access control.</summary>
    public override string StageType { get; init; } = "AccessControl";

    /// <summary>
    /// Access control model: "rbac", "abac", "acl", "capability".
    /// Null = inherit from parent.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Default permission for new objects: "deny-all", "owner-only", "inherit".
    /// Null = inherit from parent.
    /// </summary>
    public string? DefaultPermission { get; init; }

    /// <summary>
    /// Required roles for read access. Null = inherit from parent.
    /// </summary>
    public List<string>? ReadRoles { get; init; }

    /// <summary>
    /// Required roles for write access. Null = inherit from parent.
    /// </summary>
    public List<string>? WriteRoles { get; init; }

    /// <summary>
    /// Required roles for delete access. Null = inherit from parent.
    /// </summary>
    public List<string>? DeleteRoles { get; init; }

    /// <summary>
    /// Whether to enforce zero-trust verification on every access.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnforceZeroTrust { get; init; }

    /// <summary>
    /// Maximum session duration before re-authentication.
    /// Null = inherit from parent.
    /// </summary>
    public TimeSpan? SessionTimeout { get; init; }
}

/// <summary>
/// Audit stage policy for logging and compliance tracking.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class AuditStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for audit.</summary>
    public override string StageType { get; init; } = "Audit";

    /// <summary>
    /// Audit level: "none", "summary", "detailed", "forensic".
    /// Null = inherit from parent.
    /// </summary>
    public string? AuditLevel { get; init; }

    /// <summary>
    /// Events to audit: "read", "write", "delete", "access-denied", "config-change".
    /// Null = inherit from parent (default: all).
    /// </summary>
    public List<string>? AuditEvents { get; init; }

    /// <summary>
    /// Retention period for audit logs. Null = inherit from parent.
    /// </summary>
    public TimeSpan? LogRetention { get; init; }

    /// <summary>
    /// Whether to use tamper-proof logging (blockchain/WORM).
    /// Null = inherit from parent.
    /// </summary>
    public bool? TamperProof { get; init; }

    /// <summary>
    /// Whether to include full payload in audit logs.
    /// Null = inherit from parent (default: metadata only).
    /// </summary>
    public bool? IncludePayload { get; init; }

    /// <summary>
    /// External SIEM endpoint for log forwarding.
    /// Null = no external forwarding.
    /// </summary>
    public string? SiemEndpoint { get; init; }
}

/// <summary>
/// Snapshot stage policy for point-in-time recovery.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class SnapshotStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for snapshots.</summary>
    public override string StageType { get; init; } = "Snapshot";

    /// <summary>
    /// Snapshot schedule (cron expression).
    /// Null = inherit from parent.
    /// </summary>
    public string? Schedule { get; init; }

    /// <summary>
    /// Snapshot type: "full", "incremental", "copy-on-write".
    /// Null = inherit from parent.
    /// </summary>
    public string? SnapshotType { get; init; }

    /// <summary>
    /// Number of snapshots to retain. Null = inherit from parent.
    /// </summary>
    public int? RetainCount { get; init; }

    /// <summary>
    /// Retention period for snapshots. Null = inherit from parent.
    /// </summary>
    public TimeSpan? RetentionPeriod { get; init; }

    /// <summary>
    /// Whether to enable continuous data protection (CDP).
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableCdp { get; init; }

    /// <summary>
    /// Recovery point objective (RPO) - max data loss acceptable.
    /// Null = inherit from parent.
    /// </summary>
    public TimeSpan? Rpo { get; init; }
}

/// <summary>
/// WORM (Write Once Read Many) stage policy for immutable storage.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class WormStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for WORM.</summary>
    public override string StageType { get; init; } = "Worm";

    /// <summary>
    /// WORM mode: "governance" (admin can override), "compliance" (truly immutable).
    /// Null = inherit from parent.
    /// </summary>
    public string? WormMode { get; init; }

    /// <summary>
    /// Lock period during which data cannot be modified or deleted.
    /// Null = inherit from parent.
    /// </summary>
    public TimeSpan? LockPeriod { get; init; }

    /// <summary>
    /// Whether to extend lock period automatically on access.
    /// Null = inherit from parent.
    /// </summary>
    public bool? ExtendOnAccess { get; init; }

    /// <summary>
    /// Whether to allow lock period extension by admin.
    /// Null = inherit from parent.
    /// </summary>
    public bool? AllowExtension { get; init; }

    /// <summary>
    /// Legal hold identifier if under legal preservation.
    /// Null = no legal hold.
    /// </summary>
    public string? LegalHoldId { get; init; }
}

/// <summary>
/// Caching stage policy for performance optimization.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class CachingStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for caching.</summary>
    public override string StageType { get; init; } = "Caching";

    /// <summary>
    /// Cache tier: "memory", "ssd", "distributed", "cdn".
    /// Null = inherit from parent.
    /// </summary>
    public string? CacheTier { get; init; }

    /// <summary>
    /// TTL for cached data. Null = inherit from parent.
    /// </summary>
    public TimeSpan? Ttl { get; init; }

    /// <summary>
    /// Maximum cache size in bytes. Null = inherit from parent.
    /// </summary>
    public long? MaxSizeBytes { get; init; }

    /// <summary>
    /// Eviction policy: "lru", "lfu", "fifo", "ttl".
    /// Null = inherit from parent.
    /// </summary>
    public string? EvictionPolicy { get; init; }

    /// <summary>
    /// Whether to warm cache on startup. Null = inherit from parent.
    /// </summary>
    public bool? WarmOnStartup { get; init; }

    /// <summary>
    /// Whether to use read-through caching. Null = inherit from parent.
    /// </summary>
    public bool? ReadThrough { get; init; }

    /// <summary>
    /// Whether to use write-through caching. Null = inherit from parent.
    /// </summary>
    public bool? WriteThrough { get; init; }
}

/// <summary>
/// Throttling stage policy for rate limiting and resource management.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class ThrottlingStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for throttling.</summary>
    public override string StageType { get; init; } = "Throttling";

    /// <summary>
    /// Maximum requests per second. Null = inherit from parent.
    /// </summary>
    public int? MaxRequestsPerSecond { get; init; }

    /// <summary>
    /// Maximum bytes per second. Null = inherit from parent.
    /// </summary>
    public long? MaxBytesPerSecond { get; init; }

    /// <summary>
    /// Burst allowance (requests above limit for short period).
    /// Null = inherit from parent.
    /// </summary>
    public int? BurstAllowance { get; init; }

    /// <summary>
    /// Throttling scope: "global", "per-user", "per-tenant", "per-operation".
    /// Null = inherit from parent.
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>
    /// Action when throttled: "queue", "reject", "degrade".
    /// Null = inherit from parent.
    /// </summary>
    public string? ThrottleAction { get; init; }

    /// <summary>
    /// Priority for queue ordering. Lower = higher priority.
    /// Null = inherit from parent.
    /// </summary>
    public int? QueuePriority { get; init; }
}

/// <summary>
/// Multi-level pipeline policy. Defines pipeline behavior at a specific level
/// (Instance, UserGroup, User, or Operation). Nullable fields indicate "inherit from parent".
/// </summary>
public class PipelinePolicy
{
    /// <summary>Unique policy identifier.</summary>
    public string PolicyId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Human-readable policy name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Policy level this applies to.</summary>
    public PolicyLevel Level { get; init; }

    /// <summary>
    /// Scope identifier. Interpretation depends on Level:
    /// - Instance: instance ID (usually "default")
    /// - UserGroup: group ID
    /// - User: user ID
    /// - Operation: operation correlation ID
    /// </summary>
    public string ScopeId { get; init; } = string.Empty;

    /// <summary>Per-stage policy overrides at this level. Only non-null stages are overridden.</summary>
    public List<PipelineStagePolicy> Stages { get; init; } = new();

    /// <summary>
    /// Terminal stage configurations for fan-out storage.
    /// Each terminal receives the final processed (compressed/encrypted) stream.
    /// </summary>
    public List<TerminalStagePolicy> Terminals { get; init; } = new();

    /// <summary>
    /// Overall pipeline stage ordering override. Null = inherit from parent.
    /// Example: ["Compression", "Encryption", "RAID", "Integrity"]
    /// </summary>
    public List<string>? StageOrder { get; init; }

    /// <summary>Policy version. Incremented on each update for migration tracking.</summary>
    public long Version { get; init; } = 1;

    /// <summary>When this policy was created or last updated.</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Who created or last updated this policy.</summary>
    public string UpdatedBy { get; init; } = string.Empty;

    /// <summary>Migration behavior when this policy changes.</summary>
    public MigrationBehavior MigrationBehavior { get; init; } = MigrationBehavior.KeepExisting;

    /// <summary>
    /// If true, this policy is immutable once set (Instance-level only).
    /// Used for high-security deployments where pipeline config must never change.
    /// </summary>
    public bool IsImmutable { get; init; }

    /// <summary>Optional description of why this policy was set.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// Records the exact plugin, strategy, and parameters that executed for a pipeline stage.
/// Stored per-blob in the manifest to enable exact reverse pipeline reconstruction on read.
/// </summary>
public record PipelineStageSnapshot
{
    /// <summary>Stage type (e.g., "Encryption", "Compression").</summary>
    public required string StageType { get; init; }

    /// <summary>Plugin ID that executed this stage.</summary>
    public required string PluginId { get; init; }

    /// <summary>Strategy/algorithm name that was used.</summary>
    public required string StrategyName { get; init; }

    /// <summary>Execution order position.</summary>
    public int Order { get; init; }

    /// <summary>Parameters used during execution.</summary>
    public Dictionary<string, object> Parameters { get; init; } = new();

    /// <summary>When this stage was executed.</summary>
    public DateTimeOffset ExecutedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Plugin version that executed this stage.</summary>
    public string? PluginVersion { get; init; }
}

/// <summary>
/// Resolves the effective pipeline configuration for a given scope by walking
/// the policy hierarchy: Instance → UserGroup → User → Operation.
/// First non-null value wins per stage field (with AllowChildOverride enforcement).
/// </summary>
public interface IPipelineConfigProvider
{
    /// <summary>
    /// Resolves the effective pipeline policy for a given user/group/operation context.
    /// Walks the hierarchy and merges policies.
    /// </summary>
    Task<PipelinePolicy> ResolveEffectivePolicyAsync(
        string? userId = null,
        string? groupId = null,
        string? operationId = null,
        CancellationToken ct = default);

    /// <summary>Gets a policy at a specific level and scope.</summary>
    Task<PipelinePolicy?> GetPolicyAsync(PolicyLevel level, string scopeId, CancellationToken ct = default);

    /// <summary>Sets or updates a policy at a specific level and scope.</summary>
    Task SetPolicyAsync(PipelinePolicy policy, CancellationToken ct = default);

    /// <summary>Deletes a policy at a specific level and scope.</summary>
    Task<bool> DeletePolicyAsync(PolicyLevel level, string scopeId, CancellationToken ct = default);

    /// <summary>Lists all policies at a given level.</summary>
    Task<IReadOnlyList<PipelinePolicy>> ListPoliciesAsync(PolicyLevel level, CancellationToken ct = default);

    /// <summary>
    /// Visualizes the resolved pipeline for a given context, showing which level
    /// each setting was inherited from.
    /// </summary>
    Task<EffectivePolicyVisualization> VisualizeEffectivePolicyAsync(
        string? userId = null,
        string? groupId = null,
        string? operationId = null,
        CancellationToken ct = default);
}

/// <summary>
/// Shows the effective pipeline settings and which level each setting was inherited from.
/// </summary>
public class EffectivePolicyVisualization
{
    /// <summary>The resolved pipeline policy.</summary>
    public PipelinePolicy ResolvedPolicy { get; init; } = new();

    /// <summary>Per-stage attribution showing which level provided each setting.</summary>
    public List<StageAttribution> StageAttributions { get; init; } = new();

    /// <summary>Describes which policy level provided which stage setting.</summary>
    public record StageAttribution
    {
        public required string StageType { get; init; }
        public required string FieldName { get; init; }
        public required PolicyLevel SourceLevel { get; init; }
        public required string SourcePolicyId { get; init; }
        public object? Value { get; init; }
    }
}

/// <summary>
/// Handles background re-processing of blobs when pipeline policy changes.
/// Supports batch migration, lazy migration, throttling, and rollback.
/// </summary>
public interface IPipelineMigrationEngine
{
    /// <summary>Starts a background migration job for blobs affected by a policy change.</summary>
    Task<MigrationJob> StartMigrationAsync(
        PipelinePolicy oldPolicy,
        PipelinePolicy newPolicy,
        MigrationOptions? options = null,
        CancellationToken ct = default);

    /// <summary>Gets the status of a migration job.</summary>
    Task<MigrationJob?> GetMigrationStatusAsync(string jobId, CancellationToken ct = default);

    /// <summary>Cancels and rolls back a migration job.</summary>
    Task<bool> CancelMigrationAsync(string jobId, CancellationToken ct = default);

    /// <summary>Lists all active and recent migration jobs.</summary>
    Task<IReadOnlyList<MigrationJob>> ListMigrationsAsync(CancellationToken ct = default);

    /// <summary>
    /// Performs lazy migration on a single blob during read access.
    /// Called by the pipeline orchestrator when a blob's policy version is stale.
    /// </summary>
    Task<Stream> MigrateOnAccessAsync(
        Stream currentData,
        PipelineStageSnapshot[] currentStages,
        PipelinePolicy targetPolicy,
        CancellationToken ct = default);
}

/// <summary>Represents a pipeline migration job.</summary>
public class MigrationJob
{
    /// <summary>Unique job identifier.</summary>
    public string JobId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Source policy ID being migrated from.</summary>
    public string SourcePolicyId { get; init; } = string.Empty;

    /// <summary>Target policy ID being migrated to.</summary>
    public string TargetPolicyId { get; init; } = string.Empty;

    /// <summary>Current job status.</summary>
    public MigrationJobStatus Status { get; set; }

    /// <summary>Total number of blobs to migrate.</summary>
    public long TotalBlobs { get; set; }

    /// <summary>Number of blobs successfully processed.</summary>
    public long ProcessedBlobs { get; set; }

    /// <summary>Number of blobs that failed migration.</summary>
    public long FailedBlobs { get; set; }

    /// <summary>Migration progress percentage.</summary>
    public double ProgressPercent => TotalBlobs > 0 ? (double)ProcessedBlobs / TotalBlobs * 100.0 : 0;

    /// <summary>When the job started.</summary>
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the job completed or failed.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Error message if the job failed.</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>Migration job status.</summary>
public enum MigrationJobStatus
{
    /// <summary>Job is queued but not yet started.</summary>
    Queued,
    /// <summary>Job is actively processing blobs.</summary>
    Running,
    /// <summary>Job is temporarily paused.</summary>
    Paused,
    /// <summary>Job completed successfully.</summary>
    Completed,
    /// <summary>Job failed with errors.</summary>
    Failed,
    /// <summary>Job was cancelled by user.</summary>
    Cancelled,
    /// <summary>Job is rolling back changes.</summary>
    RollingBack
}

/// <summary>Options for migration jobs.</summary>
public class MigrationOptions
{
    /// <summary>Maximum blobs per second to process (throttling).</summary>
    public int? MaxBlobsPerSecond { get; init; }

    /// <summary>Only migrate blobs matching these criteria.</summary>
    public MigrationFilter? Filter { get; init; }

    /// <summary>Number of parallel workers.</summary>
    public int Parallelism { get; init; } = 4;
}

/// <summary>Filter for partial migration.</summary>
public class MigrationFilter
{
    /// <summary>Only migrate blobs in this container.</summary>
    public string? ContainerId { get; init; }

    /// <summary>Only migrate blobs owned by this user.</summary>
    public string? OwnerId { get; init; }

    /// <summary>Only migrate blobs in this storage tier.</summary>
    public string? StorageTier { get; init; }

    /// <summary>Only migrate blobs with this tag.</summary>
    public Dictionary<string, string>? RequiredTags { get; init; }

    /// <summary>Only migrate blobs created after this date.</summary>
    public DateTimeOffset? CreatedAfter { get; init; }

    /// <summary>Only migrate blobs created before this date.</summary>
    public DateTimeOffset? CreatedBefore { get; init; }

    /// <summary>Maximum blob size in bytes.</summary>
    public long? MaxSizeBytes { get; init; }
}

/// <summary>
/// Configuration for a terminal stage in the pipeline.
/// Inherits common properties from PolicyComponentBase.
/// Nullable properties indicate "inherit from parent level".
/// </summary>
public class TerminalStagePolicy : PolicyComponentBase
{
    /// <summary>
    /// Terminal type identifier (e.g., "primary", "metadata", "worm", "replica", "index").
    /// </summary>
    public string TerminalType { get; init; } = string.Empty;

    /// <summary>
    /// Storage tier for this terminal. Determines hot/warm/cold/archive placement.
    /// If null, inherit from parent or auto-select based on strategy.
    /// </summary>
    public Contracts.StorageTier? StorageTier { get; init; }

    /// <summary>
    /// Execution mode: Parallel (fan-out) or Sequential. Null = inherit from parent.
    /// </summary>
    public TerminalExecutionMode? ExecutionMode { get; init; }

    /// <summary>
    /// Whether failure of this terminal fails the entire write operation.
    /// Critical terminals (like primary) should be true.
    /// Non-critical (like search index) can be false for best-effort.
    /// Null = inherit from parent (default: true for primary/metadata, false for others).
    /// </summary>
    public bool? FailureIsCritical { get; init; }

    /// <summary>
    /// Storage path pattern for this terminal. Supports placeholders:
    /// {blobId}, {userId}, {date}, {year}, {month}, {day}, {tier}
    /// If null, inherit from parent or use default path pattern.
    /// Example: "/data/{tier}/{year}/{month}/{blobId}"
    /// </summary>
    public string? StoragePathPattern { get; init; }

    /// <summary>
    /// Retention policy for data stored in this terminal.
    /// Null = inherit from parent or no explicit retention.
    /// </summary>
    public TimeSpan? RetentionPeriod { get; init; }

    /// <summary>
    /// Whether to enable versioning for this terminal (if supported by backend).
    /// Null = inherit from parent or use backend default.
    /// </summary>
    public bool? EnableVersioning { get; init; }
}

/// <summary>
/// Execution mode for terminal stages.
/// </summary>
public enum TerminalExecutionMode
{
    /// <summary>Execute in parallel with other terminals (fan-out).</summary>
    Parallel = 0,

    /// <summary>Execute sequentially in priority order.</summary>
    Sequential = 1,

    /// <summary>Execute after all parallel terminals complete.</summary>
    AfterParallel = 2
}

// ============================================================================
// TIER 1-2 ULTIMATE PLUGIN POLICIES
// ============================================================================

/// <summary>
/// Universal Intelligence (T90) stage policy for AI-powered features.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class IntelligenceStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for intelligence.</summary>
    public override string StageType { get; init; } = "Intelligence";

    /// <summary>
    /// AI provider: "openai", "anthropic", "ollama", "azure-openai", "local".
    /// Null = inherit from parent.
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>
    /// Model identifier for the AI provider.
    /// Examples: "gpt-4", "claude-3-opus", "llama-3.1"
    /// Null = inherit from parent.
    /// </summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// Maximum tokens for AI responses. Null = inherit from parent.
    /// </summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// Temperature for AI responses (0.0-2.0). Lower = more deterministic.
    /// Null = inherit from parent.
    /// </summary>
    public double? Temperature { get; init; }

    /// <summary>
    /// Whether to enable semantic search. Null = inherit from parent.
    /// </summary>
    public bool? EnableSemanticSearch { get; init; }

    /// <summary>
    /// Embedding model for vector operations.
    /// Null = use provider default.
    /// </summary>
    public string? EmbeddingModel { get; init; }

    /// <summary>
    /// Whether to cache AI responses. Null = inherit from parent.
    /// </summary>
    public bool? EnableCaching { get; init; }

    /// <summary>
    /// Knowledge graph mode: "none", "local", "distributed".
    /// Null = inherit from parent.
    /// </summary>
    public string? KnowledgeGraphMode { get; init; }
}

/// <summary>
/// Universal Observability (T100) stage policy for monitoring and telemetry.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class ObservabilityStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for observability.</summary>
    public override string StageType { get; init; } = "Observability";

    /// <summary>
    /// Metrics backend: "prometheus", "graphite", "statsd", "otlp", "cloudwatch".
    /// Null = inherit from parent.
    /// </summary>
    public string? MetricsBackend { get; init; }

    /// <summary>
    /// Logging backend: "serilog", "nlog", "elasticsearch", "loki", "cloudwatch".
    /// Null = inherit from parent.
    /// </summary>
    public string? LoggingBackend { get; init; }

    /// <summary>
    /// Tracing backend: "jaeger", "zipkin", "otlp", "xray".
    /// Null = inherit from parent.
    /// </summary>
    public string? TracingBackend { get; init; }

    /// <summary>
    /// Minimum log level: "trace", "debug", "info", "warn", "error", "fatal".
    /// Null = inherit from parent.
    /// </summary>
    public string? MinLogLevel { get; init; }

    /// <summary>
    /// Metrics collection interval. Null = inherit from parent.
    /// </summary>
    public TimeSpan? MetricsInterval { get; init; }

    /// <summary>
    /// Whether to enable distributed tracing. Null = inherit from parent.
    /// </summary>
    public bool? EnableDistributedTracing { get; init; }

    /// <summary>
    /// Sampling rate for traces (0.0-1.0). Null = inherit from parent.
    /// </summary>
    public double? TraceSamplingRate { get; init; }

    /// <summary>
    /// Whether to enable health checks. Null = inherit from parent.
    /// </summary>
    public bool? EnableHealthChecks { get; init; }

    /// <summary>
    /// Health check interval. Null = inherit from parent.
    /// </summary>
    public TimeSpan? HealthCheckInterval { get; init; }
}

/// <summary>
/// Universal Dashboards (T101) stage policy for visualization.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class DashboardStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for dashboards.</summary>
    public override string StageType { get; init; } = "Dashboard";

    /// <summary>
    /// Dashboard backend: "grafana", "kibana", "datadog", "custom".
    /// Null = inherit from parent.
    /// </summary>
    public string? Backend { get; init; }

    /// <summary>
    /// Dashboard refresh interval. Null = inherit from parent.
    /// </summary>
    public TimeSpan? RefreshInterval { get; init; }

    /// <summary>
    /// Whether to enable real-time updates. Null = inherit from parent.
    /// </summary>
    public bool? EnableRealTime { get; init; }

    /// <summary>
    /// Default time range for charts: "1h", "6h", "24h", "7d", "30d".
    /// Null = inherit from parent.
    /// </summary>
    public string? DefaultTimeRange { get; init; }

    /// <summary>
    /// Whether to enable alerting. Null = inherit from parent.
    /// </summary>
    public bool? EnableAlerting { get; init; }

    /// <summary>
    /// Alert notification channels. Null = inherit from parent.
    /// </summary>
    public List<string>? AlertChannels { get; init; }
}

/// <summary>
/// Ultimate Database Protocol (T102) stage policy for database access.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class DatabaseProtocolStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for database protocol.</summary>
    public override string StageType { get; init; } = "DatabaseProtocol";

    /// <summary>
    /// Database type: "postgresql", "mysql", "sqlserver", "oracle", "mongodb", "cassandra".
    /// Null = inherit from parent.
    /// </summary>
    public string? DatabaseType { get; init; }

    /// <summary>
    /// Connection string or reference. Null = inherit from parent.
    /// </summary>
    public string? ConnectionString { get; init; }

    /// <summary>
    /// Connection pool size. Null = inherit from parent.
    /// </summary>
    public int? PoolSize { get; init; }

    /// <summary>
    /// Command timeout. Null = inherit from parent.
    /// </summary>
    public TimeSpan? CommandTimeout { get; init; }

    /// <summary>
    /// Whether to enable connection pooling. Null = inherit from parent.
    /// </summary>
    public bool? EnablePooling { get; init; }

    /// <summary>
    /// Whether to enable query caching. Null = inherit from parent.
    /// </summary>
    public bool? EnableQueryCaching { get; init; }

    /// <summary>
    /// Transaction isolation level: "read-uncommitted", "read-committed", "repeatable-read", "serializable".
    /// Null = inherit from parent.
    /// </summary>
    public string? IsolationLevel { get; init; }
}

/// <summary>
/// Ultimate Database Storage (T103) stage policy for database-backed storage.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class DatabaseStorageStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for database storage.</summary>
    public override string StageType { get; init; } = "DatabaseStorage";

    /// <summary>
    /// Storage engine: "innodb", "myisam", "rocksdb", "wiredtiger".
    /// Null = inherit from parent.
    /// </summary>
    public string? StorageEngine { get; init; }

    /// <summary>
    /// Table partitioning strategy: "none", "range", "hash", "list".
    /// Null = inherit from parent.
    /// </summary>
    public string? PartitioningStrategy { get; init; }

    /// <summary>
    /// Maximum blob size in bytes before external storage.
    /// Null = inherit from parent.
    /// </summary>
    public long? MaxInlineBlobSize { get; init; }

    /// <summary>
    /// Whether to compress blobs in database. Null = inherit from parent.
    /// </summary>
    public bool? CompressBlobs { get; init; }

    /// <summary>
    /// Index type: "btree", "hash", "gin", "gist".
    /// Null = inherit from parent.
    /// </summary>
    public string? IndexType { get; init; }
}

/// <summary>
/// Ultimate Data Management (T104) stage policy for data lifecycle.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class DataManagementStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for data management.</summary>
    public override string StageType { get; init; } = "DataManagement";

    /// <summary>
    /// Lifecycle policy: "manual", "age-based", "access-based", "ai-driven".
    /// Null = inherit from parent.
    /// </summary>
    public string? LifecyclePolicy { get; init; }

    /// <summary>
    /// Whether to enable block-level tiering. Null = inherit from parent.
    /// </summary>
    public bool? EnableBlockLevelTiering { get; init; }

    /// <summary>
    /// Whether to enable data branching (git-for-data). Null = inherit from parent.
    /// </summary>
    public bool? EnableBranching { get; init; }

    /// <summary>
    /// Default branch name for new data. Null = inherit from parent.
    /// </summary>
    public string? DefaultBranch { get; init; }

    /// <summary>
    /// Whether to enable probabilistic storage. Null = inherit from parent.
    /// </summary>
    public bool? EnableProbabilisticStorage { get; init; }

    /// <summary>
    /// Accuracy level for probabilistic storage (0.0-1.0).
    /// Null = inherit from parent.
    /// </summary>
    public double? ProbabilisticAccuracy { get; init; }

    /// <summary>
    /// Whether to enable spatial anchoring. Null = inherit from parent.
    /// </summary>
    public bool? EnableSpatialAnchors { get; init; }

    /// <summary>
    /// Deduplication mode: "none", "inline", "background", "global".
    /// Null = inherit from parent.
    /// </summary>
    public string? DeduplicationMode { get; init; }
}

/// <summary>
/// Ultimate Resilience (T105) stage policy for fault tolerance.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class ResilienceStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for resilience.</summary>
    public override string StageType { get; init; } = "Resilience";

    /// <summary>
    /// Circuit breaker failure threshold. Null = inherit from parent.
    /// </summary>
    public int? CircuitBreakerThreshold { get; init; }

    /// <summary>
    /// Circuit breaker reset timeout. Null = inherit from parent.
    /// </summary>
    public TimeSpan? CircuitBreakerTimeout { get; init; }

    /// <summary>
    /// Retry count for failed operations. Null = inherit from parent.
    /// </summary>
    public int? RetryCount { get; init; }

    /// <summary>
    /// Retry delay strategy: "fixed", "exponential", "jitter".
    /// Null = inherit from parent.
    /// </summary>
    public string? RetryStrategy { get; init; }

    /// <summary>
    /// Base retry delay. Null = inherit from parent.
    /// </summary>
    public TimeSpan? RetryDelay { get; init; }

    /// <summary>
    /// Bulkhead isolation: max concurrent operations.
    /// Null = inherit from parent.
    /// </summary>
    public int? BulkheadMaxConcurrent { get; init; }

    /// <summary>
    /// Bulkhead queue size. Null = inherit from parent.
    /// </summary>
    public int? BulkheadQueueSize { get; init; }

    /// <summary>
    /// Whether to enable fallback behavior. Null = inherit from parent.
    /// </summary>
    public bool? EnableFallback { get; init; }

    /// <summary>
    /// Fallback strategy: "cache", "default", "degraded", "fail-fast".
    /// Null = inherit from parent.
    /// </summary>
    public string? FallbackStrategy { get; init; }

    /// <summary>
    /// Whether to enable chaos engineering. Null = inherit from parent.
    /// </summary>
    public bool? EnableChaosEngineering { get; init; }
}

/// <summary>
/// Ultimate Deployment (T106) stage policy for deployment automation.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class DeploymentStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for deployment.</summary>
    public override string StageType { get; init; } = "Deployment";

    /// <summary>
    /// Deployment strategy: "rolling", "blue-green", "canary", "recreate".
    /// Null = inherit from parent.
    /// </summary>
    public string? DeploymentStrategy { get; init; }

    /// <summary>
    /// Canary percentage for gradual rollout (0-100).
    /// Null = inherit from parent.
    /// </summary>
    public int? CanaryPercentage { get; init; }

    /// <summary>
    /// Rolling update batch size. Null = inherit from parent.
    /// </summary>
    public int? RollingBatchSize { get; init; }

    /// <summary>
    /// Health check grace period. Null = inherit from parent.
    /// </summary>
    public TimeSpan? HealthCheckGracePeriod { get; init; }

    /// <summary>
    /// Rollback trigger: "manual", "health-check", "metrics", "error-rate".
    /// Null = inherit from parent.
    /// </summary>
    public string? RollbackTrigger { get; init; }

    /// <summary>
    /// Whether to enable automatic rollback. Null = inherit from parent.
    /// </summary>
    public bool? EnableAutoRollback { get; init; }

    /// <summary>
    /// Target environments. Null = inherit from parent.
    /// </summary>
    public List<string>? TargetEnvironments { get; init; }
}

/// <summary>
/// Ultimate Sustainability (T107) stage policy for green computing.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class SustainabilityStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for sustainability.</summary>
    public override string StageType { get; init; } = "Sustainability";

    /// <summary>
    /// Carbon awareness mode: "none", "schedule", "region", "both".
    /// Null = inherit from parent.
    /// </summary>
    public string? CarbonAwarenessMode { get; init; }

    /// <summary>
    /// Preferred low-carbon regions. Null = inherit from parent.
    /// </summary>
    public List<string>? LowCarbonRegions { get; init; }

    /// <summary>
    /// Power efficiency mode: "performance", "balanced", "efficiency".
    /// Null = inherit from parent.
    /// </summary>
    public string? PowerMode { get; init; }

    /// <summary>
    /// Whether to enable workload scheduling for renewable energy.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableRenewableScheduling { get; init; }

    /// <summary>
    /// Carbon footprint reporting interval. Null = inherit from parent.
    /// </summary>
    public TimeSpan? CarbonReportingInterval { get; init; }

    /// <summary>
    /// Maximum carbon intensity (gCO2/kWh) for workload execution.
    /// Null = no limit.
    /// </summary>
    public double? MaxCarbonIntensity { get; init; }
}

/// <summary>
/// Ultimate Interface (T109) stage policy for API exposure.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class InterfaceStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for interface.</summary>
    public override string StageType { get; init; } = "Interface";

    /// <summary>
    /// API types to expose: "rest", "grpc", "graphql", "websocket", "sql".
    /// Null = inherit from parent.
    /// </summary>
    public List<string>? ApiTypes { get; init; }

    /// <summary>
    /// Base URL for REST API. Null = inherit from parent.
    /// </summary>
    public string? RestBaseUrl { get; init; }

    /// <summary>
    /// gRPC port. Null = inherit from parent.
    /// </summary>
    public int? GrpcPort { get; init; }

    /// <summary>
    /// Whether to enable OpenAPI documentation. Null = inherit from parent.
    /// </summary>
    public bool? EnableOpenApi { get; init; }

    /// <summary>
    /// Whether to enable GraphQL playground. Null = inherit from parent.
    /// </summary>
    public bool? EnableGraphQlPlayground { get; init; }

    /// <summary>
    /// CORS allowed origins. Null = inherit from parent.
    /// </summary>
    public List<string>? CorsOrigins { get; init; }

    /// <summary>
    /// Rate limit per client per minute. Null = inherit from parent.
    /// </summary>
    public int? RateLimitPerMinute { get; init; }

    /// <summary>
    /// Whether to require authentication for all endpoints.
    /// Null = inherit from parent.
    /// </summary>
    public bool? RequireAuthentication { get; init; }

    /// <summary>
    /// Authentication schemes: "jwt", "apikey", "oauth2", "mtls".
    /// Null = inherit from parent.
    /// </summary>
    public List<string>? AuthSchemes { get; init; }
}

/// <summary>
/// Ultimate Data Format (T110) stage policy for data parsing/serialization.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class DataFormatStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for data format.</summary>
    public override string StageType { get; init; } = "DataFormat";

    /// <summary>
    /// Default input format: "json", "xml", "csv", "parquet", "avro", "protobuf".
    /// Null = auto-detect.
    /// </summary>
    public string? DefaultInputFormat { get; init; }

    /// <summary>
    /// Default output format. Null = same as input.
    /// </summary>
    public string? DefaultOutputFormat { get; init; }

    /// <summary>
    /// Whether to validate schema on input. Null = inherit from parent.
    /// </summary>
    public bool? ValidateSchema { get; init; }

    /// <summary>
    /// Schema registry URL for Avro/Protobuf. Null = no registry.
    /// </summary>
    public string? SchemaRegistryUrl { get; init; }

    /// <summary>
    /// Date/time format pattern. Null = ISO 8601.
    /// </summary>
    public string? DateTimeFormat { get; init; }

    /// <summary>
    /// Whether to pretty-print output. Null = inherit from parent.
    /// </summary>
    public bool? PrettyPrint { get; init; }

    /// <summary>
    /// Character encoding: "utf-8", "utf-16", "ascii".
    /// Null = UTF-8.
    /// </summary>
    public string? Encoding { get; init; }
}

/// <summary>
/// Ultimate Compute (T111) stage policy for compute-on-storage.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class ComputeStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for compute.</summary>
    public override string StageType { get; init; } = "Compute";

    /// <summary>
    /// Compute runtime: "wasm", "javascript", "python", "native".
    /// Null = inherit from parent.
    /// </summary>
    public string? Runtime { get; init; }

    /// <summary>
    /// Maximum execution time per operation. Null = inherit from parent.
    /// </summary>
    public TimeSpan? MaxExecutionTime { get; init; }

    /// <summary>
    /// Memory limit per execution in MB. Null = inherit from parent.
    /// </summary>
    public int? MemoryLimitMB { get; init; }

    /// <summary>
    /// CPU limit (cores or millicores). Null = inherit from parent.
    /// </summary>
    public double? CpuLimit { get; init; }

    /// <summary>
    /// Whether to enable GPU acceleration. Null = inherit from parent.
    /// </summary>
    public bool? EnableGpuAcceleration { get; init; }

    /// <summary>
    /// Sandbox mode: "strict", "permissive", "none".
    /// Null = inherit from parent.
    /// </summary>
    public string? SandboxMode { get; init; }

    /// <summary>
    /// Whether to cache compiled code. Null = inherit from parent.
    /// </summary>
    public bool? CacheCompiledCode { get; init; }
}

/// <summary>
/// Ultimate Connector (T125) stage policy for external system integration.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class ConnectorStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for connector.</summary>
    public override string StageType { get; init; } = "Connector";

    /// <summary>
    /// Connector type: "database", "api", "file", "messaging", "erp", "crm".
    /// Null = inherit from parent.
    /// </summary>
    public string? ConnectorType { get; init; }

    /// <summary>
    /// Connection URL or identifier. Null = inherit from parent.
    /// </summary>
    public string? ConnectionUrl { get; init; }

    /// <summary>
    /// Authentication method: "basic", "oauth2", "apikey", "certificate".
    /// Null = inherit from parent.
    /// </summary>
    public string? AuthMethod { get; init; }

    /// <summary>
    /// Sync mode: "full", "incremental", "cdc".
    /// Null = inherit from parent.
    /// </summary>
    public string? SyncMode { get; init; }

    /// <summary>
    /// Sync schedule (cron expression). Null = manual.
    /// </summary>
    public string? SyncSchedule { get; init; }

    /// <summary>
    /// Whether to enable bi-directional sync. Null = inherit from parent.
    /// </summary>
    public bool? BidirectionalSync { get; init; }

    /// <summary>
    /// Conflict resolution: "source-wins", "destination-wins", "merge", "manual".
    /// Null = inherit from parent.
    /// </summary>
    public string? ConflictResolution { get; init; }

    /// <summary>
    /// Batch size for sync operations. Null = inherit from parent.
    /// </summary>
    public int? BatchSize { get; init; }
}

// ============================================================================
// PHASE 5 ACTIVE STORAGE FEATURE POLICIES (T73-T89)
// ============================================================================

/// <summary>
/// Canary Objects (T73) stage policy for breach detection.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class CanaryStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for canary.</summary>
    public override string StageType { get; init; } = "Canary";

    /// <summary>
    /// Canary density: percentage of objects to protect (0.0-1.0).
    /// Null = inherit from parent.
    /// </summary>
    public double? CanaryDensity { get; init; }

    /// <summary>
    /// Alert channels: "email", "sms", "webhook", "pagerduty".
    /// Null = inherit from parent.
    /// </summary>
    public List<string>? AlertChannels { get; init; }

    /// <summary>
    /// Alert severity: "low", "medium", "high", "critical".
    /// Null = inherit from parent.
    /// </summary>
    public string? AlertSeverity { get; init; }

    /// <summary>
    /// Whether to include decoy content. Null = inherit from parent.
    /// </summary>
    public bool? IncludeDecoyContent { get; init; }

    /// <summary>
    /// Whether to enable silent tracking. Null = inherit from parent.
    /// </summary>
    public bool? SilentTracking { get; init; }
}

/// <summary>
/// Steganographic Sharding (T74) stage policy for covert storage.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class SteganographyStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for steganography.</summary>
    public override string StageType { get; init; } = "Steganography";

    /// <summary>
    /// Embedding algorithm: "lsb", "dct", "dwt", "spread-spectrum".
    /// Null = inherit from parent.
    /// </summary>
    public string? Algorithm { get; init; }

    /// <summary>
    /// Cover media type: "image", "audio", "video", "document".
    /// Null = inherit from parent.
    /// </summary>
    public string? CoverMediaType { get; init; }

    /// <summary>
    /// Embedding capacity percentage (0.0-1.0).
    /// Null = inherit from parent.
    /// </summary>
    public double? EmbeddingCapacity { get; init; }

    /// <summary>
    /// Whether to use error correction. Null = inherit from parent.
    /// </summary>
    public bool? UseErrorCorrection { get; init; }

    /// <summary>
    /// Minimum cover media count for sharding.
    /// Null = inherit from parent.
    /// </summary>
    public int? MinShardCount { get; init; }
}

/// <summary>
/// Digital Dead Drops (T76) stage policy for ephemeral sharing.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class EphemeralSharingStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for ephemeral sharing.</summary>
    public override string StageType { get; init; } = "EphemeralSharing";

    /// <summary>
    /// Maximum access count before deletion.
    /// Null = inherit from parent.
    /// </summary>
    public int? MaxAccessCount { get; init; }

    /// <summary>
    /// Time-to-live for ephemeral data.
    /// Null = inherit from parent.
    /// </summary>
    public TimeSpan? Ttl { get; init; }

    /// <summary>
    /// Whether to require authentication for access.
    /// Null = inherit from parent.
    /// </summary>
    public bool? RequireAuth { get; init; }

    /// <summary>
    /// Whether to enable read receipts. Null = inherit from parent.
    /// </summary>
    public bool? EnableReadReceipts { get; init; }

    /// <summary>
    /// Whether to enable secure deletion (overwrite). Null = inherit from parent.
    /// </summary>
    public bool? SecureDelete { get; init; }

    /// <summary>
    /// Notification on access: "none", "email", "webhook".
    /// Null = inherit from parent.
    /// </summary>
    public string? AccessNotification { get; init; }
}

/// <summary>
/// Sovereignty Geofencing (T77) stage policy for data residency.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class GeofencingStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for geofencing.</summary>
    public override string StageType { get; init; } = "Geofencing";

    /// <summary>
    /// Allowed geographic regions (ISO 3166-1 country codes).
    /// Null = inherit from parent.
    /// </summary>
    public List<string>? AllowedRegions { get; init; }

    /// <summary>
    /// Denied geographic regions (ISO 3166-1 country codes).
    /// Null = inherit from parent.
    /// </summary>
    public List<string>? DeniedRegions { get; init; }

    /// <summary>
    /// Enforcement mode: "strict" (fail), "audit" (log only).
    /// Null = inherit from parent.
    /// </summary>
    public string? EnforcementMode { get; init; }

    /// <summary>
    /// Whether to verify IP geolocation on access.
    /// Null = inherit from parent.
    /// </summary>
    public bool? VerifyAccessLocation { get; init; }

    /// <summary>
    /// Geolocation provider: "maxmind", "ip2location", "custom".
    /// Null = inherit from parent.
    /// </summary>
    public string? GeolocationProvider { get; init; }

    /// <summary>
    /// Whether to enable cross-border transfer logging.
    /// Null = inherit from parent.
    /// </summary>
    public bool? LogCrossBorderTransfers { get; init; }
}

/// <summary>
/// Ultimate Data Protection (T80) stage policy for backup/recovery.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class DataProtectionStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for data protection.</summary>
    public override string StageType { get; init; } = "DataProtection";

    /// <summary>
    /// RPO (Recovery Point Objective). Null = inherit from parent.
    /// </summary>
    public TimeSpan? Rpo { get; init; }

    /// <summary>
    /// RTO (Recovery Time Objective). Null = inherit from parent.
    /// </summary>
    public TimeSpan? Rto { get; init; }

    /// <summary>
    /// Backup destinations. Null = inherit from parent.
    /// </summary>
    public List<string>? BackupDestinations { get; init; }

    /// <summary>
    /// Whether to enable continuous data protection.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableCdp { get; init; }

    /// <summary>
    /// Whether to enable instant recovery. Null = inherit from parent.
    /// </summary>
    public bool? EnableInstantRecovery { get; init; }

    /// <summary>
    /// Backup verification mode: "none", "checksum", "restore-test".
    /// Null = inherit from parent.
    /// </summary>
    public string? VerificationMode { get; init; }

    /// <summary>
    /// Disaster recovery site. Null = no DR site.
    /// </summary>
    public string? DrSite { get; init; }
}

/// <summary>
/// Data Branching (T82) stage policy for git-for-data.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class BranchingStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for branching.</summary>
    public override string StageType { get; init; } = "Branching";

    /// <summary>
    /// Default branch name for new data.
    /// Null = inherit from parent.
    /// </summary>
    public string? DefaultBranch { get; init; }

    /// <summary>
    /// Maximum branches per object.
    /// Null = inherit from parent.
    /// </summary>
    public int? MaxBranches { get; init; }

    /// <summary>
    /// Branch retention period. Null = inherit from parent.
    /// </summary>
    public TimeSpan? BranchRetention { get; init; }

    /// <summary>
    /// Merge strategy: "fast-forward", "three-way", "ours", "theirs".
    /// Null = inherit from parent.
    /// </summary>
    public string? MergeStrategy { get; init; }

    /// <summary>
    /// Whether to enable branch protection rules.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableBranchProtection { get; init; }

    /// <summary>
    /// Protected branch patterns. Null = inherit from parent.
    /// </summary>
    public List<string>? ProtectedBranches { get; init; }
}

/// <summary>
/// Generative Compression (T84) stage policy for AI-powered compression.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class GenerativeCompressionStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for generative compression.</summary>
    public override string StageType { get; init; } = "GenerativeCompression";

    /// <summary>
    /// Model type: "vae", "gan", "diffusion", "transformer".
    /// Null = inherit from parent.
    /// </summary>
    public string? ModelType { get; init; }

    /// <summary>
    /// Content type: "image", "audio", "video", "text".
    /// Null = auto-detect.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Quality target (0.0-1.0). Higher = better quality, less compression.
    /// Null = inherit from parent.
    /// </summary>
    public double? QualityTarget { get; init; }

    /// <summary>
    /// Compression ratio target. Null = inherit from parent.
    /// </summary>
    public double? CompressionRatioTarget { get; init; }

    /// <summary>
    /// Whether to store reconstruction metadata.
    /// Null = inherit from parent.
    /// </summary>
    public bool? StoreReconstructionMetadata { get; init; }

    /// <summary>
    /// Whether to enable lossless mode (if possible).
    /// Null = inherit from parent.
    /// </summary>
    public bool? LosslessMode { get; init; }
}

/// <summary>
/// Probabilistic Storage (T85) stage policy for approximate storage.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class ProbabilisticStorageStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for probabilistic storage.</summary>
    public override string StageType { get; init; } = "ProbabilisticStorage";

    /// <summary>
    /// Data structure: "bloom-filter", "count-min-sketch", "hyperloglog", "cuckoo-filter".
    /// Null = inherit from parent.
    /// </summary>
    public string? DataStructure { get; init; }

    /// <summary>
    /// Target false positive rate (0.0-1.0).
    /// Null = inherit from parent.
    /// </summary>
    public double? FalsePositiveRate { get; init; }

    /// <summary>
    /// Expected element count for sizing.
    /// Null = inherit from parent.
    /// </summary>
    public long? ExpectedElements { get; init; }

    /// <summary>
    /// Whether to enable scalable structures.
    /// Null = inherit from parent.
    /// </summary>
    public bool? Scalable { get; init; }

    /// <summary>
    /// Hash function count. Null = auto-calculate.
    /// </summary>
    public int? HashFunctions { get; init; }
}

/// <summary>
/// Self-Emulating Objects (T86) stage policy for format preservation.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class SelfEmulatingStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for self-emulating objects.</summary>
    public override string StageType { get; init; } = "SelfEmulating";

    /// <summary>
    /// Emulation runtime: "wasm", "javascript", "native".
    /// Null = inherit from parent.
    /// </summary>
    public string? EmulationRuntime { get; init; }

    /// <summary>
    /// Whether to bundle viewer application.
    /// Null = inherit from parent.
    /// </summary>
    public bool? BundleViewer { get; init; }

    /// <summary>
    /// Whether to bundle format libraries.
    /// Null = inherit from parent.
    /// </summary>
    public bool? BundleFormatLibraries { get; init; }

    /// <summary>
    /// Maximum bundle size in MB. Null = inherit from parent.
    /// </summary>
    public int? MaxBundleSizeMB { get; init; }

    /// <summary>
    /// Format migration strategy: "preserve", "convert", "both".
    /// Null = inherit from parent.
    /// </summary>
    public string? MigrationStrategy { get; init; }

    /// <summary>
    /// Offline access mode: "full", "minimal", "none".
    /// Null = inherit from parent.
    /// </summary>
    public string? OfflineMode { get; init; }
}

/// <summary>
/// Spatial AR Anchors (T87) stage policy for location-based data.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class SpatialAnchorStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for spatial anchors.</summary>
    public override string StageType { get; init; } = "SpatialAnchor";

    /// <summary>
    /// Anchor provider: "arcore", "arkit", "azure-spatial-anchors", "custom".
    /// Null = inherit from parent.
    /// </summary>
    public string? AnchorProvider { get; init; }

    /// <summary>
    /// Anchor precision in meters. Null = inherit from parent.
    /// </summary>
    public double? PrecisionMeters { get; init; }

    /// <summary>
    /// Anchor lifetime. Null = permanent.
    /// </summary>
    public TimeSpan? AnchorLifetime { get; init; }

    /// <summary>
    /// Whether to enable anchor sharing between users.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableSharing { get; init; }

    /// <summary>
    /// Visibility radius in meters. Null = inherit from parent.
    /// </summary>
    public double? VisibilityRadius { get; init; }

    /// <summary>
    /// Whether to enable indoor positioning.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableIndoorPositioning { get; init; }
}

/// <summary>
/// Psychometric Indexing (T88) stage policy for cognitive search.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class PsychometricIndexingStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for psychometric indexing.</summary>
    public override string StageType { get; init; } = "PsychometricIndexing";

    /// <summary>
    /// Index dimensions: "emotional", "conceptual", "temporal", "spatial", "social".
    /// Null = inherit from parent.
    /// </summary>
    public List<string>? IndexDimensions { get; init; }

    /// <summary>
    /// Whether to enable sentiment analysis.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableSentimentAnalysis { get; init; }

    /// <summary>
    /// Whether to enable concept extraction.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableConceptExtraction { get; init; }

    /// <summary>
    /// Whether to enable temporal context.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableTemporalContext { get; init; }

    /// <summary>
    /// AI model for analysis. Null = inherit from parent.
    /// </summary>
    public string? AnalysisModel { get; init; }

    /// <summary>
    /// Privacy level: "full", "anonymized", "aggregated".
    /// Null = inherit from parent.
    /// </summary>
    public string? PrivacyLevel { get; init; }
}

/// <summary>
/// Forensic Watermarking (T89) stage policy for traitor tracing.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class WatermarkingStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for watermarking.</summary>
    public override string StageType { get; init; } = "Watermarking";

    /// <summary>
    /// Watermark type: "visible", "invisible", "fragile", "robust".
    /// Null = inherit from parent.
    /// </summary>
    public string? WatermarkType { get; init; }

    /// <summary>
    /// Content to embed: "user-id", "timestamp", "custom", "fingerprint".
    /// Null = inherit from parent.
    /// </summary>
    public string? WatermarkContent { get; init; }

    /// <summary>
    /// Embedding strength (0.0-1.0). Higher = more robust, more visible.
    /// Null = inherit from parent.
    /// </summary>
    public double? EmbeddingStrength { get; init; }

    /// <summary>
    /// Whether to watermark on download only.
    /// Null = inherit from parent (watermark on storage).
    /// </summary>
    public bool? WatermarkOnDownload { get; init; }

    /// <summary>
    /// Whether to enable collusion resistance.
    /// Null = inherit from parent.
    /// </summary>
    public bool? CollusionResistant { get; init; }

    /// <summary>
    /// Detection threshold (0.0-1.0).
    /// Null = inherit from parent.
    /// </summary>
    public double? DetectionThreshold { get; init; }
}

// ============================================================================
// STANDALONE PLUGIN POLICIES (T78, T79, T83, T86, T123, T124)
// ============================================================================

/// <summary>
/// Protocol Morphing (T78) stage policy for adaptive transport.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class ProtocolMorphingStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for protocol morphing.</summary>
    public override string StageType { get; init; } = "ProtocolMorphing";

    /// <summary>
    /// Base protocol: "http", "https", "websocket", "quic".
    /// Null = inherit from parent.
    /// </summary>
    public string? BaseProtocol { get; init; }

    /// <summary>
    /// Morphing mode: "stealth", "adaptive", "mimicry".
    /// Null = inherit from parent.
    /// </summary>
    public string? MorphingMode { get; init; }

    /// <summary>
    /// Target protocols to mimic: "https-web", "video-stream", "voip".
    /// Null = inherit from parent.
    /// </summary>
    public List<string>? TargetProtocols { get; init; }

    /// <summary>
    /// Whether to enable traffic shaping.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableTrafficShaping { get; init; }

    /// <summary>
    /// Whether to rotate protocols periodically.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableProtocolRotation { get; init; }

    /// <summary>
    /// Protocol rotation interval. Null = inherit from parent.
    /// </summary>
    public TimeSpan? RotationInterval { get; init; }
}

/// <summary>
/// Air-Gap Bridge (T79) stage policy for offline data transfer.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class AirGapStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for air-gap.</summary>
    public override string StageType { get; init; } = "AirGap";

    /// <summary>
    /// Transfer medium: "usb", "optical", "qr-code", "audio", "visual".
    /// Null = inherit from parent.
    /// </summary>
    public string? TransferMedium { get; init; }

    /// <summary>
    /// Whether to require two-person integrity.
    /// Null = inherit from parent.
    /// </summary>
    public bool? RequireTwoPersonIntegrity { get; init; }

    /// <summary>
    /// Maximum transfer size in MB. Null = inherit from parent.
    /// </summary>
    public long? MaxTransferSizeMB { get; init; }

    /// <summary>
    /// Whether to enable transfer logging.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableTransferLogging { get; init; }

    /// <summary>
    /// Whether to enable data verification.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableVerification { get; init; }

    /// <summary>
    /// Encryption algorithm for transfer: "aes-256-gcm", "chacha20".
    /// Null = inherit from parent.
    /// </summary>
    public string? TransferEncryption { get; init; }

    /// <summary>
    /// Whether to enable one-time-use keys.
    /// Null = inherit from parent.
    /// </summary>
    public bool? OneTimeKeys { get; init; }
}

/// <summary>
/// Data Marketplace (T83) stage policy for commerce features.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class MarketplaceStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for marketplace.</summary>
    public override string StageType { get; init; } = "Marketplace";

    /// <summary>
    /// Pricing model: "fixed", "auction", "subscription", "pay-per-use".
    /// Null = inherit from parent.
    /// </summary>
    public string? PricingModel { get; init; }

    /// <summary>
    /// Currency: "usd", "eur", "crypto".
    /// Null = inherit from parent.
    /// </summary>
    public string? Currency { get; init; }

    /// <summary>
    /// Whether to enable data previews.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnablePreviews { get; init; }

    /// <summary>
    /// Whether to enable licensing enforcement.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableLicensing { get; init; }

    /// <summary>
    /// Default license type: "single-use", "unlimited", "time-limited".
    /// Null = inherit from parent.
    /// </summary>
    public string? DefaultLicenseType { get; init; }

    /// <summary>
    /// Revenue share percentage (0.0-1.0).
    /// Null = inherit from parent.
    /// </summary>
    public double? RevenueSharePercent { get; init; }

    /// <summary>
    /// Whether to enable usage tracking.
    /// Null = inherit from parent.
    /// </summary>
    public bool? TrackUsage { get; init; }
}

/// <summary>
/// Air-Gap Convergence Orchestrator (T123) stage policy for instance federation.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class AirGapConvergenceStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for air-gap convergence.</summary>
    public override string StageType { get; init; } = "AirGapConvergence";

    /// <summary>
    /// Discovery mode: "manual", "broadcast", "multicast".
    /// Null = inherit from parent.
    /// </summary>
    public string? DiscoveryMode { get; init; }

    /// <summary>
    /// Schema merge strategy: "union", "intersection", "primary-wins".
    /// Null = inherit from parent.
    /// </summary>
    public string? SchemaMergeStrategy { get; init; }

    /// <summary>
    /// Conflict resolution: "timestamp", "vector-clock", "manual".
    /// Null = inherit from parent.
    /// </summary>
    public string? ConflictResolution { get; init; }

    /// <summary>
    /// Whether to enable automatic federation.
    /// Null = inherit from parent.
    /// </summary>
    public bool? AutoFederate { get; init; }

    /// <summary>
    /// Convergence check interval. Null = inherit from parent.
    /// </summary>
    public TimeSpan? ConvergenceInterval { get; init; }

    /// <summary>
    /// Maximum federation depth (hops).
    /// Null = inherit from parent.
    /// </summary>
    public int? MaxFederationDepth { get; init; }
}

/// <summary>
/// EHT Orchestrator (T124) stage policy for maximum local processing.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class EhtStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for EHT.</summary>
    public override string StageType { get; init; } = "Eht";

    /// <summary>
    /// Processing mode: "local-first", "edge", "hybrid".
    /// Null = inherit from parent.
    /// </summary>
    public string? ProcessingMode { get; init; }

    /// <summary>
    /// Maximum local storage quota in GB.
    /// Null = inherit from parent.
    /// </summary>
    public long? LocalStorageQuotaGB { get; init; }

    /// <summary>
    /// Whether to enable offline operation.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableOfflineMode { get; init; }

    /// <summary>
    /// Sync strategy: "opportunistic", "scheduled", "manual".
    /// Null = inherit from parent.
    /// </summary>
    public string? SyncStrategy { get; init; }

    /// <summary>
    /// Priority data types for local caching.
    /// Null = inherit from parent.
    /// </summary>
    public List<string>? PriorityDataTypes { get; init; }

    /// <summary>
    /// Whether to enable compute offloading.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableComputeOffloading { get; init; }
}

// ============================================================================
// ENTERPRISE FEATURE POLICIES (T60, SEARCH, FEDERATION, etc.)
// ============================================================================

/// <summary>
/// AEDS (T60) stage policy for active enterprise distribution.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class DistributionStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for distribution.</summary>
    public override string StageType { get; init; } = "Distribution";

    /// <summary>
    /// Distribution mode: "push", "pull", "hybrid".
    /// Null = inherit from parent.
    /// </summary>
    public string? Mode { get; init; }

    /// <summary>
    /// Target subscribers. Null = inherit from parent.
    /// </summary>
    public List<string>? Subscribers { get; init; }

    /// <summary>
    /// Content filter expression. Null = all content.
    /// </summary>
    public string? FilterExpression { get; init; }

    /// <summary>
    /// Whether to enable predictive distribution.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnablePredictiveDistribution { get; init; }

    /// <summary>
    /// Distribution priority level: "low", "normal", "high", "critical".
    /// Null = inherit from parent.
    /// </summary>
    public string? DistributionPriority { get; init; }

    /// <summary>
    /// Whether to enable multicast delivery.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableMulticast { get; init; }

    /// <summary>
    /// Delivery confirmation mode: "none", "ack", "delivery-receipt".
    /// Null = inherit from parent.
    /// </summary>
    public string? ConfirmationMode { get; init; }
}

/// <summary>
/// Search stage policy for content search capabilities.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class SearchStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for search.</summary>
    public override string StageType { get; init; } = "Search";

    /// <summary>
    /// Search backend: "elasticsearch", "opensearch", "meilisearch", "typesense", "embedded".
    /// Null = inherit from parent.
    /// </summary>
    public string? Backend { get; init; }

    /// <summary>
    /// Whether to enable full-text search.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableFullTextSearch { get; init; }

    /// <summary>
    /// Whether to enable semantic search (vector-based).
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableSemanticSearch { get; init; }

    /// <summary>
    /// Analyzers for text processing. Null = inherit from parent.
    /// </summary>
    public List<string>? Analyzers { get; init; }

    /// <summary>
    /// Index refresh interval. Null = inherit from parent.
    /// </summary>
    public TimeSpan? RefreshInterval { get; init; }

    /// <summary>
    /// Maximum search results. Null = inherit from parent.
    /// </summary>
    public int? MaxResults { get; init; }

    /// <summary>
    /// Whether to enable faceted search.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableFacets { get; init; }

    /// <summary>
    /// Whether to enable fuzzy matching.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableFuzzyMatching { get; init; }
}

/// <summary>
/// Indexing stage policy for metadata indexing.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class IndexingStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for indexing.</summary>
    public override string StageType { get; init; } = "Indexing";

    /// <summary>
    /// Index type: "btree", "hash", "inverted", "vector".
    /// Null = inherit from parent.
    /// </summary>
    public string? IndexType { get; init; }

    /// <summary>
    /// Fields to index. Null = all fields.
    /// </summary>
    public List<string>? IndexedFields { get; init; }

    /// <summary>
    /// Whether to enable content extraction for indexing.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableContentExtraction { get; init; }

    /// <summary>
    /// Index update mode: "sync", "async", "batch".
    /// Null = inherit from parent.
    /// </summary>
    public string? UpdateMode { get; init; }

    /// <summary>
    /// Batch size for async indexing. Null = inherit from parent.
    /// </summary>
    public int? BatchSize { get; init; }

    /// <summary>
    /// Whether to enable index compression.
    /// Null = inherit from parent.
    /// </summary>
    public bool? CompressIndex { get; init; }
}

/// <summary>
/// Notification stage policy for event notifications.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class NotificationStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for notification.</summary>
    public override string StageType { get; init; } = "Notification";

    /// <summary>
    /// Notification channels: "email", "sms", "webhook", "push", "slack".
    /// Null = inherit from parent.
    /// </summary>
    public List<string>? Channels { get; init; }

    /// <summary>
    /// Events to notify on: "create", "update", "delete", "access", "error".
    /// Null = inherit from parent.
    /// </summary>
    public List<string>? Events { get; init; }

    /// <summary>
    /// Notification batching interval. Null = immediate.
    /// </summary>
    public TimeSpan? BatchInterval { get; init; }

    /// <summary>
    /// Whether to enable digest notifications.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableDigest { get; init; }

    /// <summary>
    /// Quiet hours (no notifications). Format: "22:00-08:00".
    /// Null = no quiet hours.
    /// </summary>
    public string? QuietHours { get; init; }

    /// <summary>
    /// Whether to enable escalation.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableEscalation { get; init; }

    /// <summary>
    /// Escalation timeout. Null = inherit from parent.
    /// </summary>
    public TimeSpan? EscalationTimeout { get; init; }
}

/// <summary>
/// Sync stage policy for data synchronization.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class SyncStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for sync.</summary>
    public override string StageType { get; init; } = "Sync";

    /// <summary>
    /// Sync direction: "push", "pull", "bidirectional".
    /// Null = inherit from parent.
    /// </summary>
    public string? Direction { get; init; }

    /// <summary>
    /// Sync targets. Null = inherit from parent.
    /// </summary>
    public List<string>? Targets { get; init; }

    /// <summary>
    /// Sync schedule (cron expression). Null = real-time.
    /// </summary>
    public string? Schedule { get; init; }

    /// <summary>
    /// Conflict resolution: "local-wins", "remote-wins", "newest-wins", "merge".
    /// Null = inherit from parent.
    /// </summary>
    public string? ConflictResolution { get; init; }

    /// <summary>
    /// Whether to enable delta sync.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableDeltaSync { get; init; }

    /// <summary>
    /// Bandwidth limit in KB/s. Null = unlimited.
    /// </summary>
    public int? BandwidthLimitKBps { get; init; }

    /// <summary>
    /// Whether to enable offline queue.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableOfflineQueue { get; init; }
}

/// <summary>
/// Federation stage policy for distributed data federation.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class FederationStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for federation.</summary>
    public override string StageType { get; init; } = "Federation";

    /// <summary>
    /// Federation mode: "peer-to-peer", "hub-spoke", "mesh".
    /// Null = inherit from parent.
    /// </summary>
    public string? Mode { get; init; }

    /// <summary>
    /// Federation partners. Null = inherit from parent.
    /// </summary>
    public List<string>? Partners { get; init; }

    /// <summary>
    /// Trust level: "untrusted", "verified", "trusted".
    /// Null = inherit from parent.
    /// </summary>
    public string? TrustLevel { get; init; }

    /// <summary>
    /// Whether to enable cross-query.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableCrossQuery { get; init; }

    /// <summary>
    /// Query timeout across federation. Null = inherit from parent.
    /// </summary>
    public TimeSpan? QueryTimeout { get; init; }

    /// <summary>
    /// Data sharing policy: "full", "metadata-only", "query-only".
    /// Null = inherit from parent.
    /// </summary>
    public string? SharingPolicy { get; init; }

    /// <summary>
    /// Whether to cache federated results locally.
    /// Null = inherit from parent.
    /// </summary>
    public bool? CacheFederatedResults { get; init; }
}

/// <summary>
/// Fan-Out Write stage policy for T104 orchestration.
/// Controls parallel writes to multiple destinations.
/// Inherits Enabled, PluginId, StrategyName, AllowChildOverride from base.
/// </summary>
public class FanOutStagePolicy : PipelineStagePolicy
{
    /// <summary>Fixed stage type for fan-out.</summary>
    public override string StageType { get; init; } = "FanOut";

    /// <summary>
    /// Whether to enable metadata storage destination.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableMetadataStorage { get; init; }

    /// <summary>
    /// Whether to enable full-text indexing destination.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableFullTextIndex { get; init; }

    /// <summary>
    /// Whether to enable vector/semantic indexing destination.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableVectorIndex { get; init; }

    /// <summary>
    /// Whether to enable cache layer destination.
    /// Null = inherit from parent.
    /// </summary>
    public bool? EnableCaching { get; init; }

    /// <summary>
    /// Timeout for optional (non-required) destinations.
    /// Null = inherit from parent.
    /// </summary>
    public TimeSpan? OptionalDestinationTimeout { get; init; }

    /// <summary>
    /// Whether to wait for all destinations or just required ones.
    /// Null = inherit from parent.
    /// </summary>
    public bool? WaitForAllDestinations { get; init; }

    /// <summary>
    /// Content processing types to enable.
    /// Null = inherit from parent.
    /// </summary>
    public List<string>? EnabledProcessingTypes { get; init; }

    /// <summary>
    /// Cache TTL for content metadata.
    /// Null = inherit from parent.
    /// </summary>
    public TimeSpan? CacheTTL { get; init; }

    /// <summary>
    /// Cache strategy to use: "inmemory", "hybrid", "writethru", "writebehind".
    /// Null = inherit from parent.
    /// </summary>
    public string? CacheStrategy { get; init; }

    /// <summary>
    /// Indexing strategy to use: "fulltext", "metadata", "semantic", "all".
    /// Null = inherit from parent.
    /// </summary>
    public string? IndexingStrategy { get; init; }
}
