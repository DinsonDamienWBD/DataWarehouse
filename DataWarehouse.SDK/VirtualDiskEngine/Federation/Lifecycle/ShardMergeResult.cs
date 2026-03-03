using System;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Federation;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation.Lifecycle;

/// <summary>
/// Status of a shard merge operation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 93: VDE 2.0B Shard Lifecycle (VSHL-03)")]
public enum MergeStatus : byte
{
    /// <summary>Merge completed successfully: two shards combined into one.</summary>
    Completed = 0,

    /// <summary>Merge is in progress (data migration underway).</summary>
    InProgress = 1,

    /// <summary>Merge failed after partial execution; rollback was attempted.</summary>
    Failed = 2,

    /// <summary>Merge was skipped due to validation failure (non-adjacent, unavailable, etc.).</summary>
    Skipped = 3
}

/// <summary>
/// Result of a shard merge operation, capturing before/after descriptors, migration metrics,
/// and the decommissioned VDE identifier.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 93: VDE 2.0B Shard Lifecycle (VSHL-03)")]
public sealed record ShardMergeResult
{
    /// <summary>
    /// The outcome status of the merge operation.
    /// </summary>
    public required MergeStatus Status { get; init; }

    /// <summary>
    /// The lower-slot shard descriptor before the merge.
    /// </summary>
    public required DataShardDescriptor LowerShard { get; init; }

    /// <summary>
    /// The upper-slot shard descriptor before the merge.
    /// </summary>
    public required DataShardDescriptor UpperShard { get; init; }

    /// <summary>
    /// The combined shard descriptor after the merge (slot range = union of lower and upper).
    /// Default value when Status is Skipped or Failed.
    /// </summary>
    public required DataShardDescriptor MergedShard { get; init; }

    /// <summary>
    /// The VDE instance ID that was freed and decommissioned during the merge.
    /// <see cref="Guid.Empty"/> when Status is Skipped or Failed.
    /// </summary>
    public required Guid DecommissionedVdeId { get; init; }

    /// <summary>
    /// Number of blocks migrated from the absorbed shard to the surviving shard.
    /// </summary>
    public required long BlocksMigrated { get; init; }

    /// <summary>
    /// Wall-clock time of the merge operation in milliseconds.
    /// </summary>
    public required long ElapsedMs { get; init; }

    /// <summary>
    /// Human-readable reason for failure or skip. Populated only when
    /// <see cref="Status"/> is <see cref="MergeStatus.Failed"/> or <see cref="MergeStatus.Skipped"/>.
    /// </summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// Creates a <see cref="MergeStatus.Skipped"/> result indicating the merge was not performed.
    /// </summary>
    /// <param name="lower">The lower-slot shard that was a candidate.</param>
    /// <param name="upper">The upper-slot shard that was a candidate.</param>
    /// <param name="reason">The reason the merge was skipped.</param>
    /// <returns>A skipped merge result with default merged shard and empty decommissioned ID.</returns>
    public static ShardMergeResult Skipped(DataShardDescriptor lower, DataShardDescriptor upper, string reason)
    {
        return new ShardMergeResult
        {
            Status = MergeStatus.Skipped,
            LowerShard = lower,
            UpperShard = upper,
            MergedShard = default,
            DecommissionedVdeId = Guid.Empty,
            BlocksMigrated = 0,
            ElapsedMs = 0,
            FailureReason = reason
        };
    }
}
