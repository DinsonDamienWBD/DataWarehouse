using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using static DataWarehouse.SDK.Primitives.RaidConstants;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;

/// <summary>
/// Represents the health state of a device-level RAID array.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91: Device-level RAID (CBDV-02)")]
public enum ArrayState
{
    /// <summary>All devices online, no rebuild in progress.</summary>
    Optimal = 0,

    /// <summary>One or more devices offline but the array is still operational.</summary>
    Degraded = 1,

    /// <summary>A failed device is being rebuilt onto a replacement.</summary>
    Rebuilding = 2,

    /// <summary>Too many devices are offline; array is non-functional.</summary>
    Failed = 3
}

/// <summary>
/// Configuration record for device-level RAID array creation.
/// Specifies the RAID layout geometry and rebuild parameters.
/// </summary>
/// <param name="Layout">The RAID layout type (maps to RAID 0/1/5/6/10).</param>
/// <param name="StripeSizeBlocks">
/// Stripe size in blocks. Must be a positive power of two. Default 256.
/// </param>
/// <param name="SpareCount">
/// Number of hot spare devices reserved for automatic rebuild. Default 0.
/// </param>
/// <param name="Priority">
/// Rebuild priority controlling resource allocation during device replacement.
/// </param>
[SdkCompatibility("6.0.0", Notes = "Phase 91: Device-level RAID (CBDV-02)")]
public sealed record DeviceRaidConfiguration(
    DeviceLayoutType Layout,
    int StripeSizeBlocks = 256,
    int SpareCount = 0,
    RebuildPriority Priority = RebuildPriority.Medium)
{
    /// <summary>
    /// Validates this configuration and throws if any parameter is out of range.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <see cref="StripeSizeBlocks"/> is not a positive power of two,
    /// or when <see cref="SpareCount"/> is negative.
    /// </exception>
    public void Validate()
    {
        if (StripeSizeBlocks <= 0 || (StripeSizeBlocks & (StripeSizeBlocks - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(StripeSizeBlocks),
                $"StripeSizeBlocks must be a positive power of two, got {StripeSizeBlocks}.");
        }

        if (SpareCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SpareCount),
                $"SpareCount must be non-negative, got {SpareCount}.");
        }

        if (!Enum.IsDefined(Layout))
        {
            throw new ArgumentOutOfRangeException(
                nameof(Layout),
                $"Undefined layout type: {Layout}.");
        }
    }
}

/// <summary>
/// Snapshot of a device-level RAID array's current health status.
/// </summary>
/// <param name="State">The current array state (Optimal, Degraded, Rebuilding, Failed).</param>
/// <param name="OnlineDevices">Number of devices currently online and serving I/O.</param>
/// <param name="OfflineDevices">Number of devices currently offline or failed.</param>
/// <param name="RebuildProgress">
/// Rebuild progress from 0.0 to 1.0. Only meaningful when <see cref="State"/> is
/// <see cref="ArrayState.Rebuilding"/>; otherwise 0.0.
/// </param>
[SdkCompatibility("6.0.0", Notes = "Phase 91: Device-level RAID (CBDV-02)")]
public sealed record DeviceRaidHealth(
    ArrayState State,
    int OnlineDevices,
    int OfflineDevices,
    double RebuildProgress)
{
    /// <summary>
    /// Gets a health snapshot representing a fully healthy array.
    /// </summary>
    /// <param name="deviceCount">Total device count in the array.</param>
    /// <returns>An optimal health record with all devices online.</returns>
    public static DeviceRaidHealth Optimal(int deviceCount) =>
        new(ArrayState.Optimal, deviceCount, 0, 0.0);
}

/// <summary>
/// Defines a strategy for creating and managing device-level RAID arrays that produce
/// <see cref="CompoundBlockDevice"/> instances. Each implementation maps to a specific
/// RAID level (0/1/5/6/10) and handles array creation, rebuild, and health monitoring
/// at the physical block device level.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the data-level <see cref="DataWarehouse.SDK.Contracts.RAID.IRaidStrategy"/>,
/// this interface operates directly on <see cref="IPhysicalBlockDevice"/> instances and
/// produces a <see cref="CompoundBlockDevice"/> that can be consumed by any code expecting
/// an <see cref="IBlockDevice"/>.
/// </para>
/// <para>
/// Implementations must be thread-safe. The <see cref="CreateCompoundDeviceAsync"/> method
/// validates device compatibility and constructs the appropriate compound configuration.
/// The <see cref="RebuildDeviceAsync"/> method performs block-by-block data reconstruction
/// from parity or mirror sources onto a replacement device.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91: Device-level RAID (CBDV-02)")]
public interface IDeviceRaidStrategy
{
    /// <summary>
    /// Gets the RAID layout type implemented by this strategy.
    /// </summary>
    DeviceLayoutType Layout { get; }

    /// <summary>
    /// Gets a human-readable name for this device-level RAID strategy.
    /// </summary>
    string StrategyName { get; }

    /// <summary>
    /// Gets the minimum number of physical devices required by this strategy.
    /// </summary>
    int MinimumDeviceCount { get; }

    /// <summary>
    /// Gets the number of device failures this strategy can tolerate without data loss.
    /// </summary>
    int FaultTolerance { get; }

    /// <summary>
    /// Creates a <see cref="CompoundBlockDevice"/> from the supplied physical devices
    /// using this strategy's RAID layout.
    /// </summary>
    /// <param name="devices">
    /// The physical block devices to aggregate. Must meet the minimum count for this layout.
    /// All devices must be online and have matching block sizes.
    /// </param>
    /// <param name="config">
    /// The device RAID configuration specifying stripe size, spare count, and rebuild priority.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A configured <see cref="CompoundBlockDevice"/> presenting the device array
    /// as a single logical block device.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="devices"/> or <paramref name="config"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when device count is insufficient, devices are offline, or block sizes mismatch.
    /// </exception>
    Task<CompoundBlockDevice> CreateCompoundDeviceAsync(
        IReadOnlyList<IPhysicalBlockDevice> devices,
        DeviceRaidConfiguration config,
        CancellationToken ct = default);

    /// <summary>
    /// Rebuilds a failed device in the compound array by reconstructing its data
    /// from parity or mirror sources onto a replacement device.
    /// </summary>
    /// <param name="compound">The compound block device containing the failed device.</param>
    /// <param name="failedDeviceIndex">
    /// Zero-based index of the failed device within <see cref="CompoundBlockDevice.Devices"/>.
    /// </param>
    /// <param name="replacement">
    /// The replacement physical block device. Must be online and have a compatible block size
    /// and sufficient capacity.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="progress">Optional progress reporter (0.0 to 1.0).</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="compound"/> or <paramref name="replacement"/> is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="failedDeviceIndex"/> is out of range.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the array has too many failed devices to reconstruct data.
    /// </exception>
    Task RebuildDeviceAsync(
        CompoundBlockDevice compound,
        int failedDeviceIndex,
        IPhysicalBlockDevice replacement,
        CancellationToken ct = default,
        IProgress<double>? progress = null);

    /// <summary>
    /// Returns a snapshot of the current health status of the compound device array.
    /// </summary>
    /// <param name="compound">The compound block device to inspect.</param>
    /// <returns>
    /// A <see cref="DeviceRaidHealth"/> record describing the array state,
    /// online/offline device counts, and rebuild progress.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="compound"/> is null.
    /// </exception>
    DeviceRaidHealth GetHealth(CompoundBlockDevice compound);
}
