using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.VirtualDiskEngine.Mvcc;

/// <summary>
/// Configuration parameters for the epoch-based MVCC vacuum process.
/// Controls SLA lease timeouts, vacuum cycle intervals, WORM exemptions,
/// long-running snapshot materialization, and dead-space triggered aggressive mode.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-19: Epoch-based vacuum config (VOPT-32)")]
public sealed class VacuumConfig
{
    /// <summary>
    /// Maximum time a reader may hold an epoch lease before being considered stale.
    /// Readers that exceed this duration will have their snapshots materialized
    /// (if <see cref="MaterializeLongRunningSnapshots"/> is true) and their leases expired.
    /// Default: 300 seconds.
    /// </summary>
    public TimeSpan SlaLeaseTimeout { get; set; } = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Time between background vacuum cycles under normal (non-aggressive) conditions.
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan VacuumInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum number of dead versions to reclaim per vacuum cycle.
    /// Caps cycle duration to avoid pausing concurrent operations.
    /// Default: 2000.
    /// </summary>
    public int MaxVersionsPerCycle { get; set; } = 2000;

    /// <summary>
    /// When true, WORM-flagged inodes are never subject to vacuum reclamation.
    /// WORM data must not be modified or deleted; this flag enforces that at the GC level.
    /// Default: true.
    /// </summary>
    public bool WormExemptionEnabled { get; set; } = true;

    /// <summary>
    /// When true, long-running read snapshots that approach the SLA timeout are
    /// materialized to a temporary VDE location so the reader can continue beyond the lease.
    /// If false, readers whose leases expire receive <see cref="EpochExpiredException"/> on
    /// their next read attempt.
    /// Default: true.
    /// </summary>
    public bool MaterializeLongRunningSnapshots { get; set; } = true;

    /// <summary>
    /// How far before <see cref="SlaLeaseTimeout"/> to begin materialization warnings.
    /// Readers within this window receive a warning so they can prepare for expiry.
    /// Default: 240 seconds (60 seconds before the 300-second timeout).
    /// </summary>
    public TimeSpan MaterializationWarningThreshold { get; set; } = TimeSpan.FromSeconds(240);

    /// <summary>
    /// Dead-space ratio above which aggressive vacuum mode is triggered.
    /// In aggressive mode the vacuum interval is reduced to 5 seconds and
    /// <see cref="MaxVersionsPerCycle"/> is doubled to reclaim space faster.
    /// Range: [0.0, 1.0]. Default: 0.3 (30 % dead space triggers aggressive mode).
    /// </summary>
    public double DeadSpaceRatioThreshold { get; set; } = 0.3;
}
