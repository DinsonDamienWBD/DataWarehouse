using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation.Lifecycle;

/// <summary>
/// Role of a placement target within a shard's replication or erasure coding topology.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 93: VDE 2.0B Shard Lifecycle (VSHL-01)")]
public enum PlacementRole : byte
{
    /// <summary>Primary replica that serves read/write operations.</summary>
    Primary = 0,

    /// <summary>Secondary replica for fault tolerance (full data copy).</summary>
    Replica = 1,

    /// <summary>Erasure coding data shard (stores original data stripe).</summary>
    ErasureData = 2,

    /// <summary>Erasure coding parity shard (stores computed parity stripe).</summary>
    ErasureParity = 3
}

/// <summary>
/// Identifies a specific storage device and rack selected as a placement target.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 93: VDE 2.0B Shard Lifecycle (VSHL-01)")]
public readonly record struct PlacementTarget
{
    /// <summary>The compound block device that will host this shard replica or erasure stripe.</summary>
    public Guid CompoundBlockDeviceId { get; init; }

    /// <summary>Rack identifier for rack-awareness constraint enforcement.</summary>
    public string RackId { get; init; }

    /// <summary>The role this target plays in the placement topology.</summary>
    public PlacementRole Role { get; init; }
}

/// <summary>
/// Result of a placement decision: assigns a shard to a storage tier with specific
/// replica targets, erasure configuration, and cost estimate.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 93: VDE 2.0B Shard Lifecycle (VSHL-01)")]
public readonly record struct ShardPlacement
{
    /// <summary>The VDE shard identifier this placement decision applies to.</summary>
    public Guid ShardVdeId { get; init; }

    /// <summary>The storage tier assigned by the placement engine.</summary>
    public StorageTier AssignedTier { get; init; }

    /// <summary>The policy that was evaluated to produce this placement.</summary>
    public PlacementPolicy AppliedPolicy { get; init; }

    /// <summary>Ordered list of target devices for replicas or erasure stripes.</summary>
    public IReadOnlyList<PlacementTarget> Targets { get; init; }

    /// <summary>Estimated monthly cost in USD for this placement.</summary>
    public double EstimatedMonthlyCostUsd { get; init; }

    /// <summary>UTC ticks when this placement decision was computed.</summary>
    public long PlacementTimestampUtcTicks { get; init; }

    /// <summary>
    /// Optional warning message when placement was degraded (e.g., insufficient rack diversity).
    /// Null when placement fully satisfies all policy constraints.
    /// </summary>
    public string? Warning { get; init; }
}
