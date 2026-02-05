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
/// Per-stage policy configuration. Each stage (encryption, compression, RAID, etc.)
/// can be individually configured at any policy level.
/// </summary>
public class PipelineStagePolicy
{
    /// <summary>Stage type identifier (e.g., "Encryption", "Compression", "RAID").</summary>
    public string StageType { get; init; } = string.Empty;

    /// <summary>Whether this stage is enabled. Null = inherit from parent level.</summary>
    public bool? Enabled { get; init; }

    /// <summary>Specific plugin ID to use. Null = inherit from parent or auto-select.</summary>
    public string? PluginId { get; init; }

    /// <summary>Specific strategy/algorithm name within the plugin. Null = inherit or auto-select.</summary>
    public string? StrategyName { get; init; }

    /// <summary>Execution order override. Null = inherit from parent.</summary>
    public int? Order { get; init; }

    /// <summary>Stage-specific parameters (e.g., compression level, key rotation interval).</summary>
    public Dictionary<string, object>? Parameters { get; init; }

    /// <summary>
    /// If true, child levels (UserGroup/User/Operation) can override this stage's settings.
    /// If false, this level's settings are locked and cannot be changed by descendants.
    /// Default is true (allow overrides).
    /// </summary>
    public bool AllowChildOverride { get; init; } = true;
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
