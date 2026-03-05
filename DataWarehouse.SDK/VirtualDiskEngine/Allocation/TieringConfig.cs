using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Allocation;

/// <summary>
/// Classifies the heat tier of an extent based on access frequency and recency.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Heat-driven tiering configuration (VOPT-51)")]
public enum HeatTier : byte
{
    /// <summary>Frequently accessed extents — placed on the fastest storage tier.</summary>
    Hot = 0,

    /// <summary>Moderately accessed extents — placed on standard storage.</summary>
    Warm = 1,

    /// <summary>Infrequently accessed extents — placed on capacity-optimized storage.</summary>
    Cold = 2,

    /// <summary>Extents with no recent access — placed on archival storage.</summary>
    Frozen = 3,
}

/// <summary>
/// Configuration for heat-driven tiering allocation, including access-frequency thresholds,
/// migration rate limits, and per-tier shard placement mappings.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Heat-driven tiering configuration (VOPT-51)")]
public sealed class TieringConfig
{
    /// <summary>
    /// Minimum number of accesses per hour for an extent to be classified as <see cref="HeatTier.Hot"/>.
    /// Extents meeting or exceeding this threshold are considered hot.
    /// Default: 10 accesses/hour.
    /// </summary>
    public int HotThresholdAccessesPerHour { get; set; } = 10;

    /// <summary>
    /// Number of hours without access after which an extent is classified as <see cref="HeatTier.Cold"/>.
    /// Default: 24 hours (1 day).
    /// </summary>
    public int ColdThresholdHoursWithoutAccess { get; set; } = 24;

    /// <summary>
    /// Number of days without access after which an extent is classified as <see cref="HeatTier.Frozen"/>.
    /// Default: 30 days.
    /// </summary>
    public int FrozenThresholdDaysWithoutAccess { get; set; } = 30;

    /// <summary>
    /// Maximum bytes per second consumed by background migration operations.
    /// This budget limits the I/O impact on foreground workloads.
    /// Default: 100 MB/s.
    /// </summary>
    public long MigrationBudgetBytesPerSecond { get; set; } = 100L * 1024 * 1024;

    /// <summary>
    /// Interval between background heat-scan and migration cycles.
    /// Default: 15 minutes.
    /// </summary>
    public TimeSpan HeatScanInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Maximum number of extents migrated in a single background cycle.
    /// This caps the per-cycle I/O burst.
    /// Default: 64.
    /// </summary>
    public int MaxMigrationsPerCycle { get; set; } = 64;

    /// <summary>
    /// Maps each <see cref="HeatTier"/> to the target ShardId for placement.
    /// Shards correspond to physical or logical storage tiers (e.g., NVMe, SSD, HDD, tape).
    /// If a tier is not present in this dictionary, no placement constraint is applied.
    /// </summary>
    public Dictionary<HeatTier, ushort> TierToShardId { get; set; } = new()
    {
        { HeatTier.Hot,    0 },
        { HeatTier.Warm,   1 },
        { HeatTier.Cold,   2 },
        { HeatTier.Frozen, 3 },
    };
}
