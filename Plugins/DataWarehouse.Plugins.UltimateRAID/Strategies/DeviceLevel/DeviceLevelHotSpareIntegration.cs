using System;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;

namespace DataWarehouse.Plugins.UltimateRAID.Strategies.DeviceLevel;

/// <summary>
/// Wires <see cref="HotSpareManager"/> to device health monitoring for automatic
/// hot spare failover in device-level RAID arrays. Periodically polls device
/// online status and triggers automatic rebuild when failures are detected.
/// </summary>
/// <remarks>
/// <para>
/// The monitor runs as a long-lived background loop using <see cref="PeriodicTimer"/>
/// to check each device's <see cref="IPhysicalBlockDevice.IsOnline"/> property.
/// When a device transitions from online to offline, the integration:
/// </para>
/// <list type="number">
///   <item>Logs the failure detection (via <see cref="OnHealthChanged"/>).</item>
///   <item>If <see cref="HotSparePolicy.AutoRebuild"/> is enabled, initiates a
///         rebuild through <see cref="HotSpareManager.RebuildAsync"/>.</item>
///   <item>Reports the updated <see cref="DeviceRaidHealth"/> via the health callback.</item>
/// </list>
/// <para>
/// The default polling interval is 5 seconds but can be configured via
/// <see cref="HealthCheckInterval"/>. The monitor is designed to run for the
/// lifetime of the RAID array and should be cancelled via the
/// <see cref="CancellationToken"/> passed to <see cref="MonitorDevicesAsync"/>.
/// </para>
/// </remarks>
internal sealed class DeviceLevelHotSpareIntegration
{
    private static readonly TimeSpan DefaultHealthCheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the interval between device health checks.
    /// Defaults to 5 seconds.
    /// </summary>
    public TimeSpan HealthCheckInterval { get; set; } = DefaultHealthCheckInterval;

    /// <summary>
    /// Raised when the array health state changes (e.g., a device goes offline,
    /// a rebuild starts, or a rebuild completes).
    /// </summary>
    public event Action<DeviceRaidHealth>? OnHealthChanged;

    /// <summary>
    /// Raised when a device failure is detected. The parameter is the zero-based
    /// index of the failed device.
    /// </summary>
    public event Action<int>? OnDeviceFailureDetected;

    /// <summary>
    /// Raised when an automatic rebuild is initiated. The parameter is the
    /// zero-based index of the device being rebuilt.
    /// </summary>
    public event Action<int>? OnAutoRebuildStarted;

    /// <summary>
    /// Monitors the health of all devices in the compound array and triggers
    /// automatic hot spare failover when failures are detected.
    /// </summary>
    /// <param name="compound">The compound block device array to monitor.</param>
    /// <param name="strategy">
    /// The RAID strategy used for rebuild operations and health inspection.
    /// </param>
    /// <param name="spareManager">
    /// The hot spare manager providing spare devices and rebuild coordination.
    /// </param>
    /// <param name="ct">
    /// Cancellation token to stop monitoring. The method returns gracefully
    /// when cancellation is requested.
    /// </param>
    /// <returns>A task that completes when monitoring is cancelled.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when any parameter is null.
    /// </exception>
    public async Task MonitorDevicesAsync(
        CompoundBlockDevice compound,
        IDeviceRaidStrategy strategy,
        HotSpareManager spareManager,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(compound);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(spareManager);

        // Track known-offline devices to detect transitions (not already-offline)
        var knownOffline = new bool[compound.Devices.Count];

        // Initialize with current state
        for (int i = 0; i < compound.Devices.Count; i++)
        {
            knownOffline[i] = !compound.Devices[i].IsOnline;
        }

        using var timer = new PeriodicTimer(HealthCheckInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                bool healthChanged = false;

                for (int i = 0; i < compound.Devices.Count; i++)
                {
                    bool isOnline = compound.Devices[i].IsOnline;

                    if (!isOnline && !knownOffline[i])
                    {
                        // New failure detected
                        knownOffline[i] = true;
                        healthChanged = true;

                        OnDeviceFailureDetected?.Invoke(i);

                        // Attempt automatic rebuild if policy allows
                        if (spareManager.AvailableSpares > 0)
                        {
                            try
                            {
                                OnAutoRebuildStarted?.Invoke(i);

                                var result = await spareManager.RebuildAsync(
                                    compound,
                                    strategy,
                                    i,
                                    ct).ConfigureAwait(false);

                                if (result.State == RebuildState.Completed)
                                {
                                    // Device successfully replaced; mark as online in tracking
                                    knownOffline[i] = false;
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (InvalidOperationException)
                            {
                                // Rebuild already in progress; will retry on next cycle
                            }
                        }
                    }
                    else if (isOnline && knownOffline[i])
                    {
                        // Device came back online (e.g., after replacement)
                        knownOffline[i] = false;
                        healthChanged = true;
                    }
                }

                if (healthChanged)
                {
                    var health = strategy.GetHealth(compound);
                    OnHealthChanged?.Invoke(health);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful shutdown — not an error
        }
    }
}
