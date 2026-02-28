using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Storage.Placement;

/// <summary>
/// Describes the storage class characteristics of a node or object.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public enum StorageClass
{
    Hot,
    Warm,
    Cool,
    Cold,
    Archive,
    NVMe,
    SSD,
    HDD,
    Tape,
    DNA,
    Holographic
}

/// <summary>
/// Describes the type of placement constraint applied during placement computation.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public enum PlacementConstraintType
{
    Zone,
    Rack,
    Host,
    Tag,
    StorageClass,
    Compliance
}

/// <summary>
/// Status of a rebalance job lifecycle.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public enum RebalanceStatus
{
    Pending,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Describes a storage node in the cluster topology for placement decisions.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public sealed record NodeDescriptor(
    string NodeId,
    string Zone,
    string Rack,
    string Host,
    IReadOnlyDictionary<string, string> Tags,
    IReadOnlyList<StorageClass> StorageClasses,
    long CapacityBytes,
    long UsedBytes,
    double Weight = 1.0);

/// <summary>
/// Specifies the target object and its placement requirements.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public sealed record PlacementTarget(
    string ObjectKey,
    long ObjectSize,
    int ReplicaCount,
    StorageClass? RequiredStorageClass = null,
    IReadOnlyList<string>? RequiredZones = null,
    IReadOnlyDictionary<string, string>? RequiredTags = null,
    IReadOnlyList<string>? ComplianceRegions = null);

/// <summary>
/// The result of a placement computation: which nodes to place replicas on.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public sealed record PlacementDecision(
    IReadOnlyList<NodeDescriptor> TargetNodes,
    NodeDescriptor PrimaryNode,
    IReadOnlyList<NodeDescriptor> ReplicaNodes,
    string PlacementRuleId,
    bool Deterministic,
    DateTimeOffset Timestamp);

/// <summary>
/// A constraint that must be satisfied during placement (e.g., zone separation, tag matching).
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public sealed record PlacementConstraint(
    PlacementConstraintType ConstraintType,
    string Key,
    string Value,
    bool Required);

/// <summary>
/// Quantifies the "data gravity" of an object -- how strongly it is bound to its current location.
/// Higher composite score means more gravity (harder/costlier to move).
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public sealed record DataGravityScore(
    string ObjectKey,
    string CurrentNode,
    double AccessFrequency,
    DateTimeOffset LastAccessUtc,
    int ColocatedDependencies,
    decimal EgressCostPerGB,
    double LatencyMs,
    double ComplianceWeight,
    double CompositeScore);

/// <summary>
/// A single object move within a rebalance plan.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public sealed record RebalanceMove(
    string ObjectKey,
    string SourceNode,
    string TargetNode,
    long SizeBytes,
    int Priority);

/// <summary>
/// A plan describing a set of object moves for rebalancing, with cost/duration estimates.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public sealed record RebalancePlan(
    IReadOnlyList<RebalanceMove> Moves,
    double EstimatedDurationSeconds,
    long EstimatedEgressBytes,
    decimal EstimatedCost);

/// <summary>
/// Tracks the lifecycle and progress of a rebalance operation.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public sealed record RebalanceJob(
    string JobId,
    RebalancePlan Plan,
    RebalanceStatus Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    int TotalMoves,
    int CompletedMoves,
    int FailedMoves);

/// <summary>
/// Options controlling how a rebalance plan is generated.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public sealed record RebalanceOptions(
    int MaxMoves,
    long MaxEgressBytes,
    decimal MaxCostBudget,
    double MinGravityThreshold,
    bool DryRun);
