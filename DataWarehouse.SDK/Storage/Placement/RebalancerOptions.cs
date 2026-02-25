using System;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Storage.Placement;

/// <summary>
/// Configuration for the autonomous rebalancer's behavior including check intervals,
/// thresholds, migration limits, and cost budgets.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Autonomous rebalancer")]
public sealed record RebalancerOptions
{
    /// <summary>
    /// How often to check for imbalance (default: every 5 minutes).
    /// </summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Imbalance threshold before triggering rebalance.
    /// Ratio of (max_used - min_used) / total_capacity. Default 0.10 (10%).
    /// </summary>
    public double ImbalanceThreshold { get; init; } = 0.10;

    /// <summary>
    /// Maximum concurrent migration jobs.
    /// </summary>
    public int MaxConcurrentMigrations { get; init; } = 3;

    /// <summary>
    /// Maximum moves per rebalance cycle.
    /// </summary>
    public int MaxMovesPerCycle { get; init; } = 1000;

    /// <summary>
    /// Maximum egress bytes per rebalance cycle (prevents cost runaway).
    /// </summary>
    public long MaxEgressBytesPerCycle { get; init; } = 10L * 1024 * 1024 * 1024; // 10 GB

    /// <summary>
    /// Maximum cost budget per rebalance cycle.
    /// </summary>
    public decimal MaxCostPerCycle { get; init; } = 100.00m;

    /// <summary>
    /// Minimum gravity score to protect an object from rebalancing.
    /// Objects with gravity >= this threshold are never moved.
    /// </summary>
    public double GravityProtectionThreshold { get; init; } = 0.9;

    /// <summary>
    /// Throttle for data movement (bytes per second per migration).
    /// </summary>
    public long ThrottleBytesPerSec { get; init; } = 50 * 1024 * 1024; // 50 MB/s

    /// <summary>
    /// Enable read forwarding during migrations so reads are redirected from old to new location.
    /// </summary>
    public bool EnableReadForwarding { get; init; } = true;

    /// <summary>
    /// Enable checksum validation after migration to ensure data integrity.
    /// </summary>
    public bool ValidateChecksums { get; init; } = true;

    /// <summary>
    /// Quiet hours: skip rebalancing during peak traffic.
    /// Format: (StartHourUtc, EndHourUtc). Null = no quiet hours.
    /// </summary>
    public (int StartHour, int EndHour)? QuietHours { get; init; }

    /// <summary>
    /// Scoring weights for gravity computation used when evaluating which objects to move.
    /// </summary>
    public GravityScoringWeights ScoringWeights { get; init; } = GravityScoringWeights.Default;

    /// <summary>
    /// Default options for balanced behavior.
    /// </summary>
    public static RebalancerOptions Default => new();

    /// <summary>
    /// Aggressive rebalancing for maintenance windows with tighter thresholds
    /// and higher concurrency/throughput.
    /// </summary>
    public static RebalancerOptions Aggressive => new()
    {
        CheckInterval = TimeSpan.FromMinutes(1),
        ImbalanceThreshold = 0.05,
        MaxConcurrentMigrations = 10,
        MaxMovesPerCycle = 10000,
        ThrottleBytesPerSec = 200 * 1024 * 1024
    };

    /// <summary>
    /// Conservative rebalancing for production with relaxed thresholds
    /// and minimal concurrent load.
    /// </summary>
    public static RebalancerOptions Conservative => new()
    {
        CheckInterval = TimeSpan.FromMinutes(30),
        ImbalanceThreshold = 0.20,
        MaxConcurrentMigrations = 1,
        MaxMovesPerCycle = 100,
        ThrottleBytesPerSec = 10 * 1024 * 1024
    };
}
