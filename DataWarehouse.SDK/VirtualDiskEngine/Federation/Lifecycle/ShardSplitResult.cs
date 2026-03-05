using System;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Federation;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation.Lifecycle;

/// <summary>
/// Status of a shard split operation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 93: VDE 2.0B Shard Lifecycle (VSHL-02)")]
public enum SplitStatus : byte
{
    /// <summary>Split completed successfully; both halves are live.</summary>
    Completed = 0,

    /// <summary>Split is in progress; data redistribution is ongoing.</summary>
    InProgress = 1,

    /// <summary>Split failed; original shard was restored (rollback).</summary>
    Failed = 2,

    /// <summary>Split was skipped because preconditions were not met.</summary>
    Skipped = 3
}

/// <summary>
/// Result of a shard split operation, capturing the original descriptor, both halves,
/// placement decision for the newly created shard, redistribution metrics, and timing.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 93: VDE 2.0B Shard Lifecycle (VSHL-02)")]
public sealed record ShardSplitResult
{
    /// <summary>Outcome status of the split operation.</summary>
    public required SplitStatus Status { get; init; }

    /// <summary>The shard descriptor before the split was applied.</summary>
    public required DataShardDescriptor OriginalShard { get; init; }

    /// <summary>
    /// New shard covering [originalStart, midpoint). Reuses the original VDE ID.
    /// Null when <see cref="Status"/> is <see cref="SplitStatus.Skipped"/> or <see cref="SplitStatus.Failed"/>.
    /// </summary>
    public DataShardDescriptor? LowerHalf { get; init; }

    /// <summary>
    /// New shard covering [midpoint, originalEnd). Allocated a fresh VDE ID.
    /// Null when <see cref="Status"/> is <see cref="SplitStatus.Skipped"/> or <see cref="SplitStatus.Failed"/>.
    /// </summary>
    public DataShardDescriptor? UpperHalf { get; init; }

    /// <summary>
    /// Placement decision for the newly created upper-half shard.
    /// Null when <see cref="Status"/> is <see cref="SplitStatus.Skipped"/> or <see cref="SplitStatus.Failed"/>.
    /// </summary>
    public ShardPlacement? NewShardPlacement { get; init; }

    /// <summary>Number of blocks copied from the original shard to the new upper-half shard.</summary>
    public long BlocksRedistributed { get; init; }

    /// <summary>Wall-clock duration of the split operation in milliseconds.</summary>
    public long ElapsedMs { get; init; }

    /// <summary>
    /// Human-readable failure or skip reason. Populated only when
    /// <see cref="Status"/> is <see cref="SplitStatus.Failed"/> or <see cref="SplitStatus.Skipped"/>.
    /// </summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// Creates a <see cref="ShardSplitResult"/> indicating the split was skipped.
    /// </summary>
    /// <param name="shard">The shard that was evaluated but not split.</param>
    /// <param name="reason">Why the split was skipped (e.g., slot range too narrow).</param>
    /// <returns>A result with <see cref="SplitStatus.Skipped"/> status.</returns>
    public static ShardSplitResult Skipped(DataShardDescriptor shard, string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);

        return new ShardSplitResult
        {
            Status = SplitStatus.Skipped,
            OriginalShard = shard,
            LowerHalf = null,
            UpperHalf = null,
            NewShardPlacement = null,
            BlocksRedistributed = 0,
            ElapsedMs = 0,
            FailureReason = reason
        };
    }
}
