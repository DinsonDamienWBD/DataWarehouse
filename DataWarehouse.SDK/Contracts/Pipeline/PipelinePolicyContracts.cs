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
