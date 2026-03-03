using System;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation.Lifecycle;

/// <summary>
/// Defines placement rules for a storage tier: replication factor, erasure coding parameters,
/// rack-awareness constraints, and cost/durability ceilings.
/// </summary>
/// <remarks>
/// Hot and Warm tiers typically use full replication (ReplicaCount >= 2) with no erasure coding.
/// Cold and Frozen tiers typically use erasure coding (ErasureDataShards > 0) with ReplicaCount = 1.
/// The <see cref="Validate"/> method enforces these constraints.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 93: VDE 2.0B Shard Lifecycle (VSHL-01)")]
public sealed record PlacementPolicy
{
    /// <summary>Which storage tier this policy targets.</summary>
    public StorageTier TargetTier { get; init; }

    /// <summary>
    /// Number of full replicas (used primarily for Hot/Warm tiers). Must be >= 1.
    /// </summary>
    public int ReplicaCount { get; init; }

    /// <summary>
    /// Number of data shards for erasure coding (used for Cold/Frozen tiers).
    /// Set to 0 to disable erasure coding (pure replication mode).
    /// </summary>
    public int ErasureDataShards { get; init; }

    /// <summary>
    /// Number of parity shards for erasure coding.
    /// Only meaningful when <see cref="ErasureDataShards"/> > 0.
    /// </summary>
    public int ErasureParityShards { get; init; }

    /// <summary>
    /// When true, no two replicas or erasure shards may reside on the same rack ID.
    /// Falls back to best-effort if insufficient distinct racks are available.
    /// </summary>
    public bool RackAware { get; init; }

    /// <summary>
    /// Maximum cost per GB per month in USD. The placement engine rejects placements exceeding this ceiling.
    /// </summary>
    public double MaxCostPerGbMonth { get; init; }

    /// <summary>
    /// Minimum durability requirement expressed as number of nines.
    /// </summary>
    public double MinDurabilityNines { get; init; }

    /// <summary>
    /// Validates this policy's parameter combinations and throws <see cref="ArgumentException"/>
    /// for invalid configurations.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when any constraint is violated.</exception>
    public void Validate()
    {
        if (ReplicaCount < 1)
            throw new ArgumentException(
                $"ReplicaCount must be at least 1, got {ReplicaCount}.",
                nameof(ReplicaCount));

        if (ReplicaCount > LifecycleConstants.MaxReplicaCount)
            throw new ArgumentException(
                $"ReplicaCount must not exceed {LifecycleConstants.MaxReplicaCount}, got {ReplicaCount}.",
                nameof(ReplicaCount));

        if (ErasureDataShards > 0 && ErasureDataShards < LifecycleConstants.MinErasureDataShards)
            throw new ArgumentException(
                $"ErasureDataShards must be at least {LifecycleConstants.MinErasureDataShards} when enabled, got {ErasureDataShards}.",
                nameof(ErasureDataShards));

        if (ErasureDataShards > 0 && ErasureParityShards < 1)
            throw new ArgumentException(
                $"ErasureParityShards must be at least 1 when erasure coding is enabled, got {ErasureParityShards}.",
                nameof(ErasureParityShards));

        int totalErasure = ErasureDataShards + ErasureParityShards;
        if (totalErasure > LifecycleConstants.MaxErasureTotalShards)
            throw new ArgumentException(
                $"Total erasure shards (data + parity) must not exceed {LifecycleConstants.MaxErasureTotalShards}, got {totalErasure}.",
                nameof(ErasureDataShards));

        if (ErasureDataShards < 0)
            throw new ArgumentException(
                $"ErasureDataShards must be non-negative, got {ErasureDataShards}.",
                nameof(ErasureDataShards));

        if (ErasureParityShards < 0)
            throw new ArgumentException(
                $"ErasureParityShards must be non-negative, got {ErasureParityShards}.",
                nameof(ErasureParityShards));

        if (MaxCostPerGbMonth <= 0)
            throw new ArgumentException(
                $"MaxCostPerGbMonth must be positive, got {MaxCostPerGbMonth}.",
                nameof(MaxCostPerGbMonth));

        if (MinDurabilityNines <= 0)
            throw new ArgumentException(
                $"MinDurabilityNines must be positive, got {MinDurabilityNines}.",
                nameof(MinDurabilityNines));
    }

    /// <summary>
    /// Creates the default Hot tier policy: 3x replication, rack-aware, NVMe-grade cost ceiling.
    /// </summary>
    public static PlacementPolicy HotDefault() => new()
    {
        TargetTier = StorageTier.Hot,
        ReplicaCount = 3,
        ErasureDataShards = 0,
        ErasureParityShards = 0,
        RackAware = true,
        MaxCostPerGbMonth = 0.30,
        MinDurabilityNines = 9.0
    };

    /// <summary>
    /// Creates the default Warm tier policy: 2x replication, rack-aware, SSD-grade cost ceiling.
    /// </summary>
    public static PlacementPolicy WarmDefault() => new()
    {
        TargetTier = StorageTier.Warm,
        ReplicaCount = 2,
        ErasureDataShards = 0,
        ErasureParityShards = 0,
        RackAware = true,
        MaxCostPerGbMonth = 0.15,
        MinDurabilityNines = 9.0
    };

    /// <summary>
    /// Creates the default Cold tier policy: 1 replica + 8+3 erasure coding, rack-aware, HDD-grade cost ceiling.
    /// </summary>
    public static PlacementPolicy ColdDefault() => new()
    {
        TargetTier = StorageTier.Cold,
        ReplicaCount = 1,
        ErasureDataShards = 8,
        ErasureParityShards = 3,
        RackAware = true,
        MaxCostPerGbMonth = 0.05,
        MinDurabilityNines = 11.0
    };

    /// <summary>
    /// Creates the default Frozen tier policy: 1 replica + 16+4 erasure coding, rack-aware, tape-grade cost ceiling.
    /// </summary>
    public static PlacementPolicy FrozenDefault() => new()
    {
        TargetTier = StorageTier.Frozen,
        ReplicaCount = 1,
        ErasureDataShards = 16,
        ErasureParityShards = 4,
        RackAware = true,
        MaxCostPerGbMonth = 0.01,
        MinDurabilityNines = 13.0
    };
}
