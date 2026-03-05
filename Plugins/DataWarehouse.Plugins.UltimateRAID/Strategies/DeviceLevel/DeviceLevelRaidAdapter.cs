using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateRAID.Strategies.DeviceLevel;

/// <summary>
/// Factory and lifecycle adapter for device-level RAID arrays. Orchestrates validation,
/// logging, and health-state initialization when creating RAID arrays from physical
/// block devices via <see cref="IDeviceRaidStrategy"/> implementations.
/// </summary>
/// <remarks>
/// <para>
/// This adapter is the primary entry point for plugin code that needs to create
/// device-level RAID arrays. It validates inputs, selects or accepts an
/// <see cref="IDeviceRaidStrategy"/>, and returns a fully initialized
/// <see cref="CompoundBlockDevice"/>.
/// </para>
/// <para>
/// All operations are logged when an <see cref="ILogger"/> is provided.
/// The adapter does not own or dispose the resulting compound devices.
/// </para>
/// </remarks>
public sealed class DeviceLevelRaidAdapter
{
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new <see cref="DeviceLevelRaidAdapter"/> with optional logging support.
    /// </summary>
    /// <param name="logger">
    /// Optional logger for recording array creation and health events.
    /// Pass null to disable logging.
    /// </param>
    public DeviceLevelRaidAdapter(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Creates a device-level RAID array from the supplied physical devices using the
    /// given strategy and configuration.
    /// </summary>
    /// <param name="devices">
    /// The physical block devices to aggregate. Must meet the minimum count for the
    /// strategy's layout type. All devices must be online with matching block sizes.
    /// </param>
    /// <param name="strategy">
    /// The device-level RAID strategy implementation (RAID 0/1/5/6/10).
    /// </param>
    /// <param name="config">
    /// Configuration specifying stripe size, spare count, and rebuild priority.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="CompoundBlockDevice"/> presenting the array as a single logical block device.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="devices"/>, <paramref name="strategy"/>,
    /// or <paramref name="config"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when devices are empty, have mismatched block sizes, contain offline devices,
    /// or do not meet the minimum count for the layout.
    /// </exception>
    public async Task<CompoundBlockDevice> CreateRaidArrayAsync(
        IReadOnlyList<IPhysicalBlockDevice> devices,
        IDeviceRaidStrategy strategy,
        DeviceRaidConfiguration config,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(config);

        if (devices.Count == 0)
        {
            throw new ArgumentException("At least one device is required.", nameof(devices));
        }

        // Validate all devices are online
        for (int i = 0; i < devices.Count; i++)
        {
            if (devices[i] is null)
            {
                throw new ArgumentException($"Device at index {i} is null.", nameof(devices));
            }

            if (!devices[i].IsOnline)
            {
                throw new ArgumentException(
                    $"Device at index {i} ({devices[i].DeviceInfo.DeviceId}) is offline.",
                    nameof(devices));
            }
        }

        // Validate matching block sizes
        int baseBlockSize = devices[0].BlockSize;
        for (int i = 1; i < devices.Count; i++)
        {
            if (devices[i].BlockSize != baseBlockSize)
            {
                throw new ArgumentException(
                    $"Device at index {i} has block size {devices[i].BlockSize}, " +
                    $"expected {baseBlockSize}. All devices must have matching block sizes.",
                    nameof(devices));
            }
        }

        // Validate device count against strategy minimum
        if (devices.Count < strategy.MinimumDeviceCount)
        {
            throw new ArgumentException(
                $"{strategy.StrategyName} requires at least {strategy.MinimumDeviceCount} devices, " +
                $"got {devices.Count}.",
                nameof(devices));
        }

        _logger?.LogInformation(
            "Creating device-level RAID array: strategy={Strategy}, layout={Layout}, " +
            "devices={DeviceCount}, stripeSize={StripeSizeBlocks} blocks, spares={SpareCount}",
            strategy.StrategyName,
            config.Layout,
            devices.Count,
            config.StripeSizeBlocks,
            config.SpareCount);

        var compound = await strategy.CreateCompoundDeviceAsync(devices, config, ct)
            .ConfigureAwait(false);

        var health = strategy.GetHealth(compound);

        _logger?.LogInformation(
            "Device-level RAID array created: blockCount={BlockCount}, blockSize={BlockSize}, " +
            "state={State}, onlineDevices={Online}, offlineDevices={Offline}",
            compound.BlockCount,
            compound.BlockSize,
            health.State,
            health.OnlineDevices,
            health.OfflineDevices);

        return compound;
    }

    /// <summary>
    /// Creates a device-level RAID array using a synchronous factory method.
    /// Equivalent to <see cref="CreateRaidArrayAsync"/> but runs synchronously
    /// for scenarios where async is not required.
    /// </summary>
    /// <param name="devices">The physical block devices to aggregate.</param>
    /// <param name="strategy">The device-level RAID strategy implementation.</param>
    /// <param name="config">Configuration specifying RAID parameters.</param>
    /// <returns>A configured <see cref="CompoundBlockDevice"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when any parameter is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when validation fails.
    /// </exception>
    public CompoundBlockDevice CreateRaidArray(
        IReadOnlyList<IPhysicalBlockDevice> devices,
        IDeviceRaidStrategy strategy,
        DeviceRaidConfiguration config)
    {
        return CreateRaidArrayAsync(devices, strategy, config, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// Returns a strategy instance for the specified layout type.
    /// </summary>
    /// <param name="layout">The desired RAID layout type.</param>
    /// <returns>An <see cref="IDeviceRaidStrategy"/> implementation for the layout.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="layout"/> is not a supported device-level RAID type.
    /// </exception>
    public static IDeviceRaidStrategy GetStrategy(DeviceLayoutType layout)
    {
        return layout switch
        {
            DeviceLayoutType.Striped => new DeviceRaid0Strategy(),
            DeviceLayoutType.Mirrored => new DeviceRaid1Strategy(),
            DeviceLayoutType.Parity => new DeviceRaid5Strategy(),
            DeviceLayoutType.DoubleParity => new DeviceRaid6Strategy(),
            DeviceLayoutType.StripedMirror => new DeviceRaid10Strategy(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(layout),
                $"No device-level RAID strategy exists for layout: {layout}.")
        };
    }
}
