using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Integrity;

/// <summary>
/// Configuration for the probabilistic corruption radar (VOPT-48).
/// Controls sampling parameters, alert thresholds, and scan frequency.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: Probabilistic corruption radar config (VOPT-48)")]
public sealed class CorruptionRadarConfig
{
    /// <summary>
    /// Number of TRLR blocks to sample per scan.
    /// Larger values improve statistical accuracy but increase scan time.
    /// Default: 1024 TRLR blocks.
    /// </summary>
    public int SampleSize { get; set; } = 1024;

    /// <summary>
    /// Confidence level for the Wilson score interval (0.0 to 1.0).
    /// Default: 0.95 (95% confidence).
    /// </summary>
    public double ConfidenceLevel { get; set; } = 0.95;

    /// <summary>
    /// Alert threshold for estimated corruption rate (0.0 to 1.0).
    /// An alert is triggered if the estimated corruption rate exceeds this value.
    /// Default: 0.001 (0.1%).
    /// </summary>
    public double AlertThreshold { get; set; } = 0.001;

    /// <summary>
    /// Interval between periodic scans in continuous monitoring mode.
    /// Default: 1 hour.
    /// </summary>
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Maximum number of times to retry reading a suspect block before confirming corruption.
    /// Default: 3 retries.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Optional deterministic random seed for reproducible sampling (e.g., in tests).
    /// Null uses a random seed derived from the current time.
    /// Default: null (random).
    /// </summary>
    public int? RandomSeed { get; set; }
}
