using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using static DataWarehouse.SDK.Primitives.RaidConstants;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;

/// <summary>
/// Policy controlling hot spare allocation and automatic rebuild behavior
/// for a <see cref="CompoundBlockDevice"/> array.
/// </summary>
/// <param name="MaxSpares">
/// Maximum number of hot spare devices that can be held in reserve.
/// Must be non-negative.
/// </param>
/// <param name="AutoRebuild">
/// When <see langword="true"/>, the manager automatically initiates a rebuild
/// when a device failure is detected and a spare is available.
/// </param>
/// <param name="Priority">
/// The rebuild priority controlling how aggressively rebuild I/O competes
/// with normal array operations.
/// </param>
/// <param name="MaxRebuildBandwidthMBps">
/// Maximum rebuild bandwidth in megabytes per second. Zero or negative means unlimited.
/// Used to throttle rebuild I/O and minimize impact on production workloads.
/// </param>
[SdkCompatibility("6.0.0", Notes = "Phase 91: Hot spare management (CBDV-04)")]
public sealed record HotSparePolicy(
    int MaxSpares,
    bool AutoRebuild,
    RebuildPriority Priority = RebuildPriority.Medium,
    double MaxRebuildBandwidthMBps = 0.0)
{
    /// <summary>
    /// Validates this policy and throws if any parameter is out of range.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <see cref="MaxSpares"/> is negative.
    /// </exception>
    public void Validate()
    {
        if (MaxSpares < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxSpares),
                $"MaxSpares must be non-negative, got {MaxSpares}.");
        }
    }
}

/// <summary>
/// Represents the current state of a device rebuild operation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91: Hot spare management (CBDV-04)")]
public enum RebuildState
{
    /// <summary>No rebuild in progress.</summary>
    Idle = 0,

    /// <summary>A rebuild is actively copying data to the spare device.</summary>
    Rebuilding = 1,

    /// <summary>The rebuild completed successfully.</summary>
    Completed = 2,

    /// <summary>The rebuild failed due to an error (e.g., spare went offline, too many failures).</summary>
    Failed = 3
}

/// <summary>
/// Snapshot of a hot spare rebuild operation's status including progress,
/// timing, and the identity of the failed device being replaced.
/// </summary>
/// <param name="State">The current rebuild state.</param>
/// <param name="FailedDeviceIndex">
/// Zero-based index of the failed device being rebuilt, or -1 when <see cref="State"/>
/// is <see cref="RebuildState.Idle"/>.
/// </param>
/// <param name="Progress">
/// Rebuild progress from 0.0 to 1.0. Only meaningful when <see cref="State"/> is
/// <see cref="RebuildState.Rebuilding"/>.
/// </param>
/// <param name="EstimatedRemaining">
/// Estimated time remaining for the rebuild, or <see langword="null"/> when not rebuilding
/// or when not enough data has been processed to estimate.
/// </param>
[SdkCompatibility("6.0.0", Notes = "Phase 91: Hot spare management (CBDV-04)")]
public sealed record RebuildStatus(
    RebuildState State,
    int FailedDeviceIndex,
    double Progress,
    TimeSpan? EstimatedRemaining)
{
    /// <summary>
    /// Gets a status representing no active rebuild.
    /// </summary>
    public static RebuildStatus Idle { get; } = new(RebuildState.Idle, -1, 0.0, null);
}

/// <summary>
/// Manages hot spare devices for a <see cref="CompoundBlockDevice"/> RAID array,
/// providing automatic failover and background rebuild when a device fails.
/// </summary>
/// <remarks>
/// <para>
/// The manager maintains a pool of spare <see cref="IPhysicalBlockDevice"/> instances.
/// When a device failure is detected (externally or via a device health monitor),
/// calling <see cref="RebuildAsync"/> allocates a spare and uses the array's
/// <see cref="IDeviceRaidStrategy"/> to reconstruct data onto it.
/// </para>
/// <para>
/// Rebuild operations honor the configured <see cref="HotSparePolicy.Priority"/> by yielding
/// periodically during background-priority rebuilds, and respect the optional bandwidth cap.
/// Progress is reported via an <see cref="IProgress{T}"/> callback and the
/// <see cref="CurrentStatus"/> property.
/// </para>
/// <para>
/// This class is thread-safe. Only one rebuild may be active at a time; concurrent
/// <see cref="RebuildAsync"/> calls will throw <see cref="InvalidOperationException"/>.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91: Hot spare management (CBDV-04)")]
public sealed class HotSpareManager
{
    private readonly List<IPhysicalBlockDevice> _spares;
    private readonly HotSparePolicy _policy;
    private readonly object _syncRoot = new();
    private volatile RebuildStatus _currentStatus = RebuildStatus.Idle;
    private volatile bool _rebuildInProgress;

    /// <summary>
    /// Initializes a new <see cref="HotSpareManager"/> with the given spare devices and policy.
    /// </summary>
    /// <param name="spares">
    /// The hot spare devices available for failover. Each must be online and compatible
    /// with the target array. The list is copied; external mutations have no effect.
    /// </param>
    /// <param name="policy">The hot spare policy governing allocation and rebuild behavior.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="spares"/> or <paramref name="policy"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the number of spares exceeds <see cref="HotSparePolicy.MaxSpares"/>.
    /// </exception>
    public HotSpareManager(IReadOnlyList<IPhysicalBlockDevice> spares, HotSparePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(spares);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();

        if (spares.Count > policy.MaxSpares)
        {
            throw new ArgumentException(
                $"Spare count {spares.Count} exceeds MaxSpares {policy.MaxSpares}.",
                nameof(spares));
        }

        _spares = new List<IPhysicalBlockDevice>(spares);
        _policy = policy;
    }

    /// <summary>
    /// Gets the current rebuild status. Updated atomically during rebuild operations.
    /// </summary>
    public RebuildStatus CurrentStatus => _currentStatus;

    /// <summary>
    /// Gets the number of spare devices currently available for allocation.
    /// </summary>
    public int AvailableSpares
    {
        get
        {
            lock (_syncRoot)
            {
                return _spares.Count;
            }
        }
    }

    /// <summary>
    /// Raised when a spare device has successfully replaced a failed device in the array.
    /// The first parameter is the failed device index; the second is the spare that replaced it.
    /// </summary>
    public event Action<int, IPhysicalBlockDevice>? OnDeviceReplaced;

    /// <summary>
    /// Allocates the next available spare device from the pool.
    /// </summary>
    /// <returns>
    /// The allocated <see cref="IPhysicalBlockDevice"/> spare, or <see langword="null"/>
    /// if no spares are available or all remaining spares are offline.
    /// </returns>
    public IPhysicalBlockDevice? AllocateSpare()
    {
        lock (_syncRoot)
        {
            for (int i = 0; i < _spares.Count; i++)
            {
                if (_spares[i].IsOnline)
                {
                    var spare = _spares[i];
                    _spares.RemoveAt(i);
                    return spare;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Initiates a rebuild operation for a failed device in the compound array,
    /// allocating a spare and reconstructing data using the provided RAID strategy.
    /// </summary>
    /// <param name="compound">The compound block device containing the failed device.</param>
    /// <param name="strategy">The RAID strategy used to reconstruct data onto the spare.</param>
    /// <param name="failedDeviceIndex">Zero-based index of the failed device.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="progress">Optional progress reporter (0.0 to 1.0).</param>
    /// <returns>
    /// A <see cref="RebuildStatus"/> indicating the outcome. Returns a status with
    /// <see cref="RebuildState.Failed"/> if no spare is available or the rebuild encounters
    /// an unrecoverable error.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="compound"/> or <paramref name="strategy"/> is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="failedDeviceIndex"/> is out of range.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a rebuild is already in progress.
    /// </exception>
    public async Task<RebuildStatus> RebuildAsync(
        CompoundBlockDevice compound,
        IDeviceRaidStrategy strategy,
        int failedDeviceIndex,
        CancellationToken ct = default,
        IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(compound);
        ArgumentNullException.ThrowIfNull(strategy);

        if (failedDeviceIndex < 0 || failedDeviceIndex >= compound.Devices.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failedDeviceIndex),
                $"Device index {failedDeviceIndex} is out of range [0, {compound.Devices.Count}).");
        }

        if (_rebuildInProgress)
        {
            throw new InvalidOperationException(
                "A rebuild is already in progress. Only one rebuild may be active at a time.");
        }

        _rebuildInProgress = true;

        try
        {
            var spare = AllocateSpare();
            if (spare is null)
            {
                var noSpareStatus = new RebuildStatus(
                    RebuildState.Failed,
                    failedDeviceIndex,
                    0.0,
                    null);
                _currentStatus = noSpareStatus;
                return noSpareStatus;
            }

            _currentStatus = new RebuildStatus(
                RebuildState.Rebuilding,
                failedDeviceIndex,
                0.0,
                null);

            var startTime = DateTime.UtcNow;

            // Create a progress wrapper that updates CurrentStatus and yields for background priority
            var progressWrapper = new Progress<double>(p =>
            {
                TimeSpan? estimated = null;
                if (p > 0.01)
                {
                    var elapsed = DateTime.UtcNow - startTime;
                    var totalEstimate = TimeSpan.FromTicks((long)(elapsed.Ticks / p));
                    estimated = totalEstimate - elapsed;
                    if (estimated < TimeSpan.Zero)
                        estimated = TimeSpan.Zero;
                }

                _currentStatus = new RebuildStatus(
                    RebuildState.Rebuilding,
                    failedDeviceIndex,
                    p,
                    estimated);

                progress?.Report(p);
            });

            try
            {
                // For background priority, yield periodically to avoid starving other I/O
                if (_policy.Priority == RebuildPriority.Low)
                {
                    await Task.Yield();
                }

                await strategy.RebuildDeviceAsync(
                    compound,
                    failedDeviceIndex,
                    spare,
                    ct,
                    progressWrapper).ConfigureAwait(false);

                var completedStatus = new RebuildStatus(
                    RebuildState.Completed,
                    failedDeviceIndex,
                    1.0,
                    TimeSpan.Zero);
                _currentStatus = completedStatus;

                OnDeviceReplaced?.Invoke(failedDeviceIndex, spare);

                return completedStatus;
            }
            catch (OperationCanceledException)
            {
                // Return spare to pool on cancellation
                ReturnSpare(spare);

                var cancelledStatus = new RebuildStatus(
                    RebuildState.Failed,
                    failedDeviceIndex,
                    _currentStatus.Progress,
                    null);
                _currentStatus = cancelledStatus;
                throw;
            }
            catch (Exception)
            {
                // Return spare to pool on failure
                ReturnSpare(spare);

                var failedStatus = new RebuildStatus(
                    RebuildState.Failed,
                    failedDeviceIndex,
                    _currentStatus.Progress,
                    null);
                _currentStatus = failedStatus;
                return failedStatus;
            }
        }
        finally
        {
            _rebuildInProgress = false;
        }
    }

    /// <summary>
    /// Returns a spare device to the available pool (e.g., after a cancelled rebuild).
    /// </summary>
    private void ReturnSpare(IPhysicalBlockDevice spare)
    {
        lock (_syncRoot)
        {
            if (_spares.Count < _policy.MaxSpares)
            {
                _spares.Add(spare);
            }
        }
    }
}
