using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Integrity;

/// <summary>
/// Response policy applied when an entropy anomaly is detected during a write.
/// </summary>
public enum PanicResponse
{
    /// <summary>
    /// Create an immutable snapshot only. The suspect write still proceeds.
    /// </summary>
    SnapshotOnly,

    /// <summary>
    /// Create an immutable snapshot and fire the <see cref="EntropyTriggeredPanicFork.OnPanicFork"/>
    /// alert event. The suspect write still proceeds.
    /// </summary>
    SnapshotAndAlert,

    /// <summary>
    /// Create an immutable snapshot, fire the alert event, and block the triggering write.
    /// This is the most aggressive response: the volume is protected but the write is rejected.
    /// </summary>
    SnapshotAndBlock
}

/// <summary>
/// Configuration for the entropy-triggered panic fork subsystem (VOPT-57).
/// Controls entropy thresholds, baseline trust, rate limiting, and response policy.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: VOPT-57 entropy-triggered panic fork")]
public sealed class PanicForkConfig
{
    /// <summary>
    /// Absolute Shannon entropy ceiling (bits/byte, max 8.0 for purely random bytes).
    /// Any write whose entropy exceeds this value triggers a panic fork regardless of baseline.
    /// Default is 7.5 — well above normal compressed or encrypted data written by the user
    /// but below the theoretical maximum.
    /// </summary>
    public double EntropyThreshold { get; set; } = 7.5;

    /// <summary>
    /// Expected minimum baseline entropy for typical payloads (text-like data, structured records).
    /// Used only when the baseline has not yet been established (i.e., fewer writes than
    /// <see cref="MinWritesBeforeBaseline"/>).
    /// </summary>
    public double EntropyBaselineMin { get; set; } = 3.0;

    /// <summary>
    /// Expected maximum baseline entropy for typical payloads (compressed data, media).
    /// Used only when the baseline has not yet been established.
    /// </summary>
    public double EntropyBaselineMax { get; set; } = 6.5;

    /// <summary>
    /// Number of the most-recent write entropies kept in the sliding window.
    /// The window average smooths single-block outliers and reduces false positives.
    /// </summary>
    public int WindowSizeBlocks { get; set; } = 64;

    /// <summary>
    /// Multiplier applied to the running standard deviation when evaluating whether a write is
    /// anomalous relative to the established baseline.
    /// Trigger condition: entropy &gt; baseline.Mean + AnomalyDeviationFactor * baseline.StdDev.
    /// A value of 2.0 corresponds to the 95th-percentile threshold.
    /// </summary>
    public double AnomalyDeviationFactor { get; set; } = 2.0;

    /// <summary>
    /// Minimum number of writes required before the running baseline statistics are trusted.
    /// Until this threshold is reached, the system falls back to static threshold comparisons
    /// against <see cref="EntropyThreshold"/>.
    /// </summary>
    public int MinWritesBeforeBaseline { get; set; } = 1000;

    /// <summary>
    /// Maximum number of panic forks that may be triggered within a single rolling hour.
    /// Prevents snapshot storms under a sustained, high-rate ransomware write attack.
    /// </summary>
    public int MaxPanicForksPerHour { get; set; } = 3;

    /// <summary>
    /// When true, behaves as an alias for <see cref="PanicResponse.SnapshotAndBlock"/>: the
    /// write that triggered the fork is rejected after the snapshot is safely created.
    /// Kept for convenience; the <see cref="Response"/> property takes precedence.
    /// </summary>
    public bool BlockSuspectWrites { get; set; } = false;

    /// <summary>
    /// Determines the action taken when an entropy anomaly is detected.
    /// Defaults to <see cref="PanicResponse.SnapshotAndAlert"/> — the snapshot is created and
    /// the alert event is fired, but the write is allowed to continue.
    /// </summary>
    public PanicResponse Response { get; set; } = PanicResponse.SnapshotAndAlert;
}
