using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;

/// <summary>
/// Temperature class derived from the 2-bit SLC_Wear_Hint in the SPOL module.
/// Drives allocation group selection in the Semantic Wear-Leveling Allocator.
/// Maps directly to the Expected_TTL hint carried in extent metadata.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Semantic wear-leveling temperature class (VOPT-41)")]
public enum TemperatureClass : byte
{
    /// <summary>Frequently-written, short-lived data (logs, write buffers). ~20% of AGs.</summary>
    Hot = 0,

    /// <summary>Moderately-written data with medium TTL (indexes, working sets). ~30% of AGs.</summary>
    Warm = 1,

    /// <summary>Infrequently-written, long-lived data (user files, snapshots). ~35% of AGs.</summary>
    Cold = 2,

    /// <summary>Rarely-written, archival data (backups, compliance blobs). ~15% of AGs.</summary>
    Frozen = 3
}

/// <summary>
/// Configuration for the Semantic Wear-Leveling Allocator (SWLV).
///
/// SWLV segregates writes into AllocationGroups by Expected_TTL hint so that
/// groups of same-temperature data can be reclaimed together by Background Vacuum
/// without copying still-live data — eliminating write amplification on conventional NVMe.
///
/// SWLV activates ONLY when <see cref="ZnsAwareActive"/> is <c>false</c>.
/// On ZNS devices the epoch-based zone allocator (ZNSM) is strictly superior and
/// SWLV is bypassed entirely.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Semantic wear-leveling config (VOPT-41)")]
public sealed class WearLevelingConfig
{
    /// <summary>
    /// Master switch for semantic wear-leveling.
    /// When <c>false</c>, the allocator delegates all operations to the inner allocator
    /// with no temperature-based routing.
    /// Default: <c>true</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Set to <c>true</c> at mount time when a ZNS-capable device is detected.
    /// When <c>true</c>, SWLV is entirely disabled — all allocation calls pass through
    /// to the inner allocator. ZNS epoch-based zone allocation supersedes SWLV.
    /// Default: <c>false</c>.
    /// </summary>
    public bool ZnsAwareActive { get; set; } = false;

    // ── Allocation group ratio configuration ──────────────────────────────────

    /// <summary>
    /// Fraction of total allocation groups reserved for <see cref="TemperatureClass.Hot"/> data.
    /// Default: 0.20 (20%).
    /// </summary>
    public double HotGroupRatio { get; set; } = 0.20;

    /// <summary>
    /// Fraction of total allocation groups reserved for <see cref="TemperatureClass.Warm"/> data.
    /// Default: 0.30 (30%).
    /// </summary>
    public double WarmGroupRatio { get; set; } = 0.30;

    /// <summary>
    /// Fraction of total allocation groups reserved for <see cref="TemperatureClass.Cold"/> data.
    /// Default: 0.35 (35%).
    /// </summary>
    public double ColdGroupRatio { get; set; } = 0.35;

    /// <summary>
    /// Fraction of total allocation groups reserved for <see cref="TemperatureClass.Frozen"/> data.
    /// Default: 0.15 (15%).
    /// </summary>
    public double FrozenGroupRatio { get; set; } = 0.15;

    // ── Background vacuum configuration ───────────────────────────────────────

    /// <summary>
    /// How frequently Background Vacuum checks each temperature group for reclaimable groups.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan VacuumGroupReclaimInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Dead-block ratio threshold above which a group becomes a reclamation candidate.
    /// A value of 0.9 means: reclaim when more than 90% of blocks in the group are dead.
    /// Default: 0.90.
    /// </summary>
    public double GroupReclaimThreshold { get; set; } = 0.90;

    /// <summary>
    /// Validates that the four group ratios sum to approximately 1.0 (within 1%).
    /// </summary>
    /// <returns><c>true</c> if ratios are valid; <c>false</c> otherwise.</returns>
    public bool AreRatiosValid()
    {
        double sum = HotGroupRatio + WarmGroupRatio + ColdGroupRatio + FrozenGroupRatio;
        return Math.Abs(sum - 1.0) < 0.01;
    }
}
