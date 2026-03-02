using System;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Integrity;

/// <summary>
/// Configuration for epoch-batched Merkle tree updates (VOPT-30).
/// Controls the background batch interval, maximum dirty blocks per epoch,
/// WAL recovery behaviour, and dirty queue capacity.
/// </summary>
/// <remarks>
/// The defaults are calibrated for a write-heavy workload where synchronous per-block
/// Merkle updates would reduce throughput by 10-50x:
/// <list type="bullet">
///   <item>5 s interval: amortises root recomputation across thousands of writes per epoch.</item>
///   <item>8 192 blocks per batch: bounds worst-case CPU per epoch to ~100 ms on typical hardware.</item>
///   <item>65 536 queue entries: absorbs micro-bursts without blocking the write hot path.</item>
/// </list>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87-17: Epoch-batched Merkle config (VOPT-30)")]
public sealed class MerkleBatchConfig
{
    /// <summary>
    /// How often the background thread wakes to process the dirty-block queue.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan BatchInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum number of dirty block entries drained per batch epoch.
    /// Caps worst-case CPU time per epoch.
    /// Default: 8 192.
    /// </summary>
    public int MaxDirtyBlocksPerBatch { get; set; } = 8_192;

    /// <summary>
    /// When true, <c>EpochBatchedMerkleUpdater.RecoverFromWalAsync</c> is
    /// available to rebuild the dirty set from WAL entries after a crash.
    /// Default: true.
    /// </summary>
    public bool WalRecoveryEnabled { get; set; } = true;

    /// <summary>
    /// Maximum number of entries that can reside in the dirty queue before
    /// back-pressure warnings are logged.  The queue itself is unbounded;
    /// this is a soft cap used purely as a signal.
    /// Default: 65 536.
    /// </summary>
    public int DirtyQueueCapacity { get; set; } = 65_536;
}
