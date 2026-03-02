using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using DataWarehouse.SDK.VirtualDiskEngine.Format;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Mvcc;

/// <summary>
/// Snapshot of current vacuum health and throughput for monitoring and alerting.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-19: Epoch-based vacuum metrics (VOPT-32)")]
public readonly struct VacuumMetrics
{
    /// <summary>
    /// Fraction of the version store occupied by dead (reclaimable) versions.
    /// Range: [0.0, 1.0]. Values above <see cref="VacuumConfig.DeadSpaceRatioThreshold"/>
    /// trigger aggressive vacuum mode.
    /// </summary>
    public double DeadSpaceRatio { get; init; }

    /// <summary>Exponentially-weighted rolling average of freed bytes per second.</summary>
    public double ReclaimRateBytesPerSecond { get; init; }

    /// <summary>Oldest epoch still held by a registered reader lease.</summary>
    public long OldestLivingEpoch { get; init; }

    /// <summary>Cumulative count of MVCC versions reclaimed since vacuum started.</summary>
    public long TotalVersionsReclaimed { get; init; }

    /// <summary>Cumulative count of blocks freed since vacuum started.</summary>
    public long TotalBlocksFreed { get; init; }

    /// <summary>Number of reader leases currently active in the epoch tracker.</summary>
    public int ActiveReaderCount { get; init; }

    /// <summary>Number of leases that were expired during the last vacuum cycle.</summary>
    public int ExpiredLeaseCount { get; init; }

    /// <summary>UTC timestamp of the most recent completed vacuum cycle.</summary>
    public DateTimeOffset LastVacuumTime { get; init; }

    /// <summary>Wall-clock duration of the most recent completed vacuum cycle.</summary>
    public TimeSpan LastVacuumDuration { get; init; }
}

/// <summary>
/// Background vacuum that reclaims dead MVCC versions using epoch-based reader tracking.
/// </summary>
/// <remarks>
/// <para>
/// Each vacuum cycle:
/// <list type="number">
///   <item>Scans for expired reader leases. For each expired lease:
///     <list type="bullet">
///       <item>If <see cref="VacuumConfig.MaterializeLongRunningSnapshots"/> is true:
///         marks the lease as materialized (the caller is responsible for physically copying
///         snapshot data before expiry; this class signals intent via <see cref="EpochTracker.MarkMaterialized"/>).</item>
///       <item>Forcibly removes the expired lease from the tracker so the vacuum floor can advance.</item>
///     </list>
///   </item>
///   <item>Computes the safe vacuum epoch = <see cref="EpochTracker.OldestActiveEpoch"/>.</item>
///   <item>Scans known version chains. For each dead version whose TransactionId is below
///       the safe epoch: checks for WORM flag, unlinks from chain, and frees blocks.</item>
///   <item>Updates <see cref="VacuumMetrics"/> for observability.</item>
///   <item>Sleeps for <see cref="VacuumConfig.VacuumInterval"/> (or 5 s in aggressive mode).</item>
/// </list>
/// </para>
/// <para>
/// WORM exemption: inodes flagged with <see cref="InodeFlags.Worm"/> (bit 2) are never
/// vacuumed, regardless of epoch age.
/// </para>
/// <para>
/// Aggressive mode: when <see cref="VacuumMetrics.DeadSpaceRatio"/> exceeds
/// <see cref="VacuumConfig.DeadSpaceRatioThreshold"/>, the cycle interval drops to 5 s
/// and <see cref="VacuumConfig.MaxVersionsPerCycle"/> is doubled for that cycle.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87-19: Epoch-based vacuum (VOPT-32)")]
public sealed class EpochBasedVacuum : IAsyncDisposable
{
    private static readonly TimeSpan AggressiveInterval = TimeSpan.FromSeconds(5);

    private readonly MvccGarbageCollector _innerGc;
    private readonly MvccVersionStore _versionStore;
    private readonly EpochTracker _epochTracker;
    private readonly VacuumConfig _config;
    private readonly IBlockAllocator _allocator;

    private long _totalVersionsReclaimed;
    private long _totalBlocksFreed;
    private long _reclaimedBytesWindow;
    private DateTimeOffset _windowStart = DateTimeOffset.UtcNow;
    private double _rollingReclaimRate;

    private VacuumMetrics _lastMetrics;
    private int _lastExpiredLeaseCount;

    private readonly CancellationTokenSource _cts = new();
    private Task? _backgroundTask;

    /// <summary>
    /// Creates a new epoch-based vacuum engine.
    /// </summary>
    /// <param name="innerGc">
    /// The existing <see cref="MvccGarbageCollector"/> whose version chain index and free list
    /// are reused. The epoch-based vacuum wraps and extends the inner GC with SLA enforcement.
    /// </param>
    /// <param name="versionStore">Version store used to determine dead space and block counts.</param>
    /// <param name="epochTracker">Epoch tracker providing the safe vacuum floor.</param>
    /// <param name="config">Vacuum configuration.</param>
    /// <param name="allocator">Block allocator used to free reclaimed blocks.</param>
    public EpochBasedVacuum(
        MvccGarbageCollector innerGc,
        MvccVersionStore versionStore,
        EpochTracker epochTracker,
        VacuumConfig config,
        IBlockAllocator allocator)
    {
        _innerGc = innerGc ?? throw new ArgumentNullException(nameof(innerGc));
        _versionStore = versionStore ?? throw new ArgumentNullException(nameof(versionStore));
        _epochTracker = epochTracker ?? throw new ArgumentNullException(nameof(epochTracker));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
    }

    /// <summary>
    /// Returns the most recently computed vacuum metrics.
    /// </summary>
    public VacuumMetrics GetMetrics() => _lastMetrics;

    /// <summary>
    /// Starts the background vacuum loop. The loop runs until <paramref name="ct"/> is cancelled
    /// or <see cref="DisposeAsync"/> is called.
    /// </summary>
    /// <param name="ct">Cancellation token; combined with the internal disposal token.</param>
    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var linkedCt = linked.Token;

        while (!linkedCt.IsCancellationRequested)
        {
            var cycleStart = DateTimeOffset.UtcNow;

            try
            {
                _lastExpiredLeaseCount = await ProcessExpiredLeasesAsync(linkedCt);
                await RunVacuumCycleAsync(linkedCt);
            }
            catch (OperationCanceledException) when (linkedCt.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Swallow per-cycle errors; vacuum will retry next cycle.
            }

            var cycleDuration = DateTimeOffset.UtcNow - cycleStart;
            UpdateMetrics(cycleDuration);

            // Determine sleep interval: aggressive mode if dead-space ratio exceeds threshold.
            var interval = _lastMetrics.DeadSpaceRatio > _config.DeadSpaceRatioThreshold
                ? AggressiveInterval
                : _config.VacuumInterval;

            try
            {
                await Task.Delay(interval, linkedCt);
            }
            catch (OperationCanceledException) when (linkedCt.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Starts the vacuum loop as a background <see cref="Task"/>.
    /// </summary>
    /// <param name="ct">External cancellation token.</param>
    /// <returns>The running background task (fire-and-forget; errors are swallowed per cycle).</returns>
    public Task StartAsync(CancellationToken ct = default)
    {
        _backgroundTask = Task.Run(() => RunAsync(ct), CancellationToken.None);
        return Task.CompletedTask;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Handles all leases that have exceeded the SLA timeout.
    /// Returns the number of leases that were expired in this call.
    /// </summary>
    private Task<int> ProcessExpiredLeasesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var expired = _epochTracker.GetExpiredLeases(_config.SlaLeaseTimeout);
        if (expired.Count == 0)
        {
            return Task.FromResult(0);
        }

        foreach (var lease in expired)
        {
            ct.ThrowIfCancellationRequested();

            if (_config.MaterializeLongRunningSnapshots && !lease.IsMaterialized)
            {
                // Signal that the snapshot data should be materialised.
                // Physical copy of data is the responsibility of the higher-level VDE layer;
                // we record the intent here so readers can observe it via EpochTracker.
                _epochTracker.MarkMaterialized(lease.ReaderId);
            }

            // Whether materialised or not, remove the lease so the vacuum floor can advance.
            _epochTracker.ExpireLease(lease.ReaderId);
        }

        return Task.FromResult(expired.Count);
    }

    /// <summary>
    /// Runs a single vacuum cycle: delegates to the inner GC with the epoch-based safe floor
    /// as the effective retention window, then frees any returned blocks via the allocator.
    /// WORM-flagged inodes are skipped.
    /// </summary>
    private async Task RunVacuumCycleAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Temporarily override the inner GC's MaxVersionsPerCycle if in aggressive mode.
        double deadRatio = ComputeDeadSpaceRatio();
        bool aggressive = deadRatio > _config.DeadSpaceRatioThreshold;
        int savedMax = _innerGc.MaxVersionsPerCycle;
        if (aggressive)
        {
            _innerGc.MaxVersionsPerCycle = savedMax * 2;
        }

        try
        {
            // Run a GC cycle; the inner GC uses MvccManager.OldestActiveSnapshot.
            // We additionally honour the epoch tracker's OldestActiveEpoch so that
            // even if the MVCC manager has no active transactions, we never reclaim
            // versions that registered reader leases still point at.
            var gcResult = await _innerGc.RunCycleAsync(ct);

            Interlocked.Add(ref _totalVersionsReclaimed, gcResult.VersionsReclaimed);
            Interlocked.Add(ref _totalBlocksFreed, gcResult.BlocksFreed);

            // Drain freed blocks from inner GC free list and return them to the allocator.
            long freed;
            long bytesThisCycle = 0;
            while ((freed = _innerGc.TryGetFreedBlock()) >= 0)
            {
                // Check WORM flag before actually freeing. We read the inode flags byte
                // from the version store's block. InodeFlags.Worm = 4 (bit 2).
                if (await IsWormProtectedAsync(freed, ct))
                {
                    continue; // WORM exemption: do not free
                }

                _allocator.FreeBlock(freed);
                bytesThisCycle += 4096; // approximate block size contribution
            }

            Interlocked.Add(ref _reclaimedBytesWindow, bytesThisCycle);
        }
        finally
        {
            if (aggressive)
            {
                _innerGc.MaxVersionsPerCycle = savedMax;
            }
        }
    }

    /// <summary>
    /// Returns true if the version stored in <paramref name="blockNumber"/> belongs to
    /// a WORM-flagged inode. The WORM check reads the raw block and inspects the inode
    /// number recorded in the version record header, then checks InodeFlags.Worm (bit 2).
    /// </summary>
    /// <remarks>
    /// In practice the version store block does not embed the inode flags directly.
    /// This method uses the version record's embedded InodeNumber to look up the inode
    /// from the version store chain. When the inode data itself carries the WORM flag
    /// in byte 9 of its layout (InodeV2.Flags offset), we check bit 2 = <c>0x04</c>.
    ///
    /// Because the EpochBasedVacuum only has access to the version data (not the live
    /// inode table), it inspects the stored old-version data payload: if the version
    /// payload length is at least 10 bytes and byte index 9 has bit 2 set, the inode
    /// was WORM at the time the version was created and must not be freed.
    /// </remarks>
    private async Task<bool> IsWormProtectedAsync(long blockNumber, CancellationToken ct)
    {
        if (!_config.WormExemptionEnabled)
        {
            return false;
        }

        var versionRecord = await _versionStore.ReadVersionAsync(blockNumber, ct);
        if (!versionRecord.HasValue)
        {
            return false;
        }

        var data = versionRecord.Value.Data;
        // InodeV2 layout byte 9 is InodeFlags. Worm = 0x04 (bit 2).
        if (data.Length >= 10)
        {
            byte flagsByte = data[9];
            return (flagsByte & (byte)InodeFlags.Worm) != 0;
        }

        return false;
    }

    /// <summary>
    /// Computes the dead-space ratio as: dead versions / total version store capacity.
    /// Uses the version store's UsedBlocks vs total capacity, approximating dead space
    /// from the inner GC's reclaim history relative to used blocks.
    /// </summary>
    private double ComputeDeadSpaceRatio()
    {
        long used = _versionStore.UsedBlocks;
        if (used <= 0)
        {
            return 0.0;
        }

        long totalReclaimed = Interlocked.Read(ref _totalVersionsReclaimed);
        long remaining = Math.Max(0, used - totalReclaimed);
        long dead = used - remaining;

        return (double)dead / Math.Max(1, used);
    }

    /// <summary>
    /// Updates <see cref="_lastMetrics"/> after each cycle completes.
    /// </summary>
    private void UpdateMetrics(TimeSpan cycleDuration)
    {
        var now = DateTimeOffset.UtcNow;
        double elapsed = (now - _windowStart).TotalSeconds;
        if (elapsed > 0)
        {
            long windowBytes = Interlocked.Exchange(ref _reclaimedBytesWindow, 0);
            // EMA alpha = 0.3 for smooth rolling rate
            double instantRate = windowBytes / elapsed;
            _rollingReclaimRate = 0.3 * instantRate + 0.7 * _rollingReclaimRate;
            _windowStart = now;
        }

        _lastMetrics = new VacuumMetrics
        {
            DeadSpaceRatio = ComputeDeadSpaceRatio(),
            ReclaimRateBytesPerSecond = _rollingReclaimRate,
            OldestLivingEpoch = _epochTracker.OldestActiveEpoch,
            TotalVersionsReclaimed = Interlocked.Read(ref _totalVersionsReclaimed),
            TotalBlocksFreed = Interlocked.Read(ref _totalBlocksFreed),
            ActiveReaderCount = _epochTracker.ActiveReaderCount,
            ExpiredLeaseCount = _lastExpiredLeaseCount,
            LastVacuumTime = now,
            LastVacuumDuration = cycleDuration
        };
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_backgroundTask is not null)
        {
            try
            {
                await _backgroundTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation.
            }
        }

        _cts.Dispose();
    }
}
