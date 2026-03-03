using System;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation.Lifecycle;

/// <summary>
/// Storage tier classification for shard placement decisions.
/// Tiers are ordered from hottest (lowest latency, highest cost) to coldest (highest latency, lowest cost).
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 93: VDE 2.0B Shard Lifecycle (VSHL-01)")]
public enum StorageTier : byte
{
    /// <summary>NVMe-backed tier for latency-critical, high-throughput workloads.</summary>
    Hot = 0,

    /// <summary>SSD-backed tier for moderate-throughput workloads with good latency.</summary>
    Warm = 1,

    /// <summary>HDD-backed tier for infrequently accessed data with erasure coding protection.</summary>
    Cold = 2,

    /// <summary>Tape/archive-backed tier for long-term retention with maximum durability.</summary>
    Frozen = 3
}

/// <summary>
/// Performance and cost profile for a storage tier.
/// Provides default characteristics for each <see cref="StorageTier"/> value to guide placement decisions.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 93: VDE 2.0B Shard Lifecycle (VSHL-01)")]
public readonly record struct TierProfile
{
    /// <summary>The storage tier this profile describes.</summary>
    public StorageTier Tier { get; init; }

    /// <summary>Physical media type (e.g., "NVMe", "SSD", "HDD", "Tape").</summary>
    public string MediaType { get; init; }

    /// <summary>Maximum acceptable latency in milliseconds for this tier.</summary>
    public double MaxLatencyMs { get; init; }

    /// <summary>Expected IOPS capacity for this media type.</summary>
    public long IopsCapacity { get; init; }

    /// <summary>Cost per gigabyte per month in USD.</summary>
    public double CostPerGbMonth { get; init; }

    /// <summary>
    /// Durability expressed as number of nines (e.g., 11 = 99.999999999% durability).
    /// </summary>
    public double DurabilityNines { get; init; }

    /// <summary>Whether this media type supports random write operations efficiently.</summary>
    public bool SupportsRandomWrite { get; init; }

    /// <summary>
    /// Returns the default <see cref="TierProfile"/> for the specified storage tier.
    /// </summary>
    /// <param name="tier">The storage tier to retrieve defaults for.</param>
    /// <returns>A <see cref="TierProfile"/> with sensible defaults for the given tier.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="tier"/> is not a defined enum value.</exception>
    public static TierProfile GetDefault(StorageTier tier) => tier switch
    {
        StorageTier.Hot => new TierProfile
        {
            Tier = StorageTier.Hot,
            MediaType = "NVMe",
            MaxLatencyMs = 0.1,
            IopsCapacity = 500_000,
            CostPerGbMonth = 0.20,
            DurabilityNines = 9.0,
            SupportsRandomWrite = true
        },
        StorageTier.Warm => new TierProfile
        {
            Tier = StorageTier.Warm,
            MediaType = "SSD",
            MaxLatencyMs = 1.0,
            IopsCapacity = 100_000,
            CostPerGbMonth = 0.10,
            DurabilityNines = 9.0,
            SupportsRandomWrite = true
        },
        StorageTier.Cold => new TierProfile
        {
            Tier = StorageTier.Cold,
            MediaType = "HDD",
            MaxLatencyMs = 10.0,
            IopsCapacity = 500,
            CostPerGbMonth = 0.02,
            DurabilityNines = 11.0,
            SupportsRandomWrite = false
        },
        StorageTier.Frozen => new TierProfile
        {
            Tier = StorageTier.Frozen,
            MediaType = "Tape",
            MaxLatencyMs = 5000.0,
            IopsCapacity = 1,
            CostPerGbMonth = 0.004,
            DurabilityNines = 13.0,
            SupportsRandomWrite = false
        },
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Undefined storage tier.")
    };
}
