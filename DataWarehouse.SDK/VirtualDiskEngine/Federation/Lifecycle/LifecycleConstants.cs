using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation.Lifecycle;

/// <summary>
/// Shared constants for the VDE 2.0B shard lifecycle subsystem.
/// Defines thresholds for split/merge decisions, migration batch sizes, and replication bounds.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 93: VDE 2.0B Shard Lifecycle (VSHL-01)")]
public static class LifecycleConstants
{
    /// <summary>
    /// Default utilization percentage above which a shard becomes a split candidate.
    /// </summary>
    public const double DefaultSplitThresholdPercent = 80.0;

    /// <summary>
    /// Default utilization percentage below which two adjacent shards become merge candidates.
    /// Both adjacent shards must be below this threshold for a merge to be proposed.
    /// </summary>
    public const double DefaultMergeThresholdPercent = 20.0;

    /// <summary>
    /// Number of blocks copied per migration iteration.
    /// Balances throughput against impact on foreground I/O.
    /// </summary>
    public const int DefaultMigrationBatchSizeBlocks = 4096;

    /// <summary>
    /// Absolute ceiling on replica count for any placement policy.
    /// </summary>
    public const int MaxReplicaCount = 6;

    /// <summary>
    /// Minimum number of data shards when erasure coding is enabled (must be >= 2 for meaningful redundancy).
    /// </summary>
    public const int MinErasureDataShards = 2;

    /// <summary>
    /// Maximum total shards (data + parity) for erasure coding.
    /// </summary>
    public const int MaxErasureTotalShards = 32;
}
