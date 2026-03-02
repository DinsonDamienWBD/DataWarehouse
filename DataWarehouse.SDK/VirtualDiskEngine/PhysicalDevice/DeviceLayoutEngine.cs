using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;

/// <summary>
/// Maps logical block addresses to physical (device, block) tuples based on the
/// configured RAID layout. Supports stripe (RAID 0), mirror (RAID 1), single parity
/// (RAID 5), double parity (RAID 6), and striped-mirror (RAID 10) layouts.
/// </summary>
/// <remarks>
/// <para>
/// The engine is stateless after construction — all mapping is purely arithmetic.
/// Thread safety is guaranteed because no mutable state exists.
/// </para>
/// <para>
/// Stripe calculations use integer division and modulo arithmetic to distribute
/// blocks across data devices. Parity layouts use rotating parity assignment
/// (left-symmetric) to balance parity load across all devices.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91: CompoundBlockDevice (CBDV-01)")]
public sealed class DeviceLayoutEngine
{
    private readonly CompoundDeviceConfiguration _config;
    private readonly int _deviceCount;
    private readonly int _dataDeviceCount;
    private readonly int _stripeSizeBlocks;

    /// <summary>
    /// Gets the total number of devices in the array.
    /// </summary>
    public int DeviceCount => _deviceCount;

    /// <summary>
    /// Gets the number of data-carrying devices (excludes parity devices for RAID 5/6).
    /// </summary>
    public int DataDeviceCount => _dataDeviceCount;

    /// <summary>
    /// Initializes a new <see cref="DeviceLayoutEngine"/> for the given configuration and device count.
    /// </summary>
    /// <param name="config">The compound device configuration.</param>
    /// <param name="deviceCount">The total number of physical devices in the array.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="config"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="deviceCount"/> does not meet the minimum for the layout type,
    /// or when <see cref="CompoundDeviceConfiguration.StripeSizeBlocks"/> is not a power of two.
    /// </exception>
    public DeviceLayoutEngine(CompoundDeviceConfiguration config, int deviceCount)
    {
        ArgumentNullException.ThrowIfNull(config);

        _config = config;
        _deviceCount = deviceCount;
        _stripeSizeBlocks = config.StripeSizeBlocks;

        if (_stripeSizeBlocks <= 0 || (_stripeSizeBlocks & (_stripeSizeBlocks - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(config),
                $"StripeSizeBlocks must be a positive power of two, got {_stripeSizeBlocks}.");
        }

        ValidateDeviceCount(config.Layout, deviceCount, config.MirrorCount, config.ParityDeviceCount);

        _dataDeviceCount = config.Layout switch
        {
            DeviceLayoutType.Striped => deviceCount,
            DeviceLayoutType.Mirrored => 1, // all devices hold the same data
            DeviceLayoutType.Parity => deviceCount - config.ParityDeviceCount,
            DeviceLayoutType.DoubleParity => deviceCount - config.ParityDeviceCount,
            DeviceLayoutType.StripedMirror => deviceCount / config.MirrorCount,
            _ => throw new ArgumentOutOfRangeException(nameof(config), $"Unsupported layout: {config.Layout}")
        };
    }

    /// <summary>
    /// Maps a logical block address to one or more physical (device, block) locations
    /// based on the configured layout.
    /// </summary>
    /// <param name="logicalBlock">The logical block number (zero-based).</param>
    /// <returns>
    /// An array of <see cref="BlockMapping"/> values. For striped layouts this contains
    /// a single entry; for mirrored layouts it contains one entry per mirror; for parity
    /// layouts it contains the data mapping plus parity device mappings.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="logicalBlock"/> is negative.
    /// </exception>
    public BlockMapping[] MapLogicalToPhysical(long logicalBlock)
    {
        if (logicalBlock < 0)
            throw new ArgumentOutOfRangeException(nameof(logicalBlock), "Logical block must be non-negative.");

        return _config.Layout switch
        {
            DeviceLayoutType.Striped => MapStriped(logicalBlock),
            DeviceLayoutType.Mirrored => MapMirrored(logicalBlock),
            DeviceLayoutType.Parity => MapParity(logicalBlock, 1),
            DeviceLayoutType.DoubleParity => MapParity(logicalBlock, _config.ParityDeviceCount),
            DeviceLayoutType.StripedMirror => MapStripedMirror(logicalBlock),
            _ => throw new InvalidOperationException($"Unsupported layout: {_config.Layout}")
        };
    }

    /// <summary>
    /// Calculates the total number of usable logical blocks given the per-device block count.
    /// </summary>
    /// <param name="perDeviceBlockCount">The number of blocks available on each physical device.</param>
    /// <returns>The total logical block count visible to consumers of the compound device.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="perDeviceBlockCount"/> is not positive.
    /// </exception>
    public long CalculateTotalLogicalBlocks(long perDeviceBlockCount)
    {
        if (perDeviceBlockCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(perDeviceBlockCount), "Per-device block count must be positive.");

        return _config.Layout switch
        {
            // RAID 0: full capacity of all devices
            DeviceLayoutType.Striped => perDeviceBlockCount * _dataDeviceCount,

            // RAID 1: capacity of a single device (all mirrors identical)
            DeviceLayoutType.Mirrored => perDeviceBlockCount,

            // RAID 5: (N-1) * perDevice
            DeviceLayoutType.Parity => perDeviceBlockCount * _dataDeviceCount,

            // RAID 6: (N-2) * perDevice
            DeviceLayoutType.DoubleParity => perDeviceBlockCount * _dataDeviceCount,

            // RAID 10: half capacity (mirror pairs)
            DeviceLayoutType.StripedMirror => perDeviceBlockCount * _dataDeviceCount,

            _ => throw new InvalidOperationException($"Unsupported layout: {_config.Layout}")
        };
    }

    /// <summary>
    /// Gets the indices of parity devices for a given stripe group in parity layouts.
    /// </summary>
    /// <param name="stripeGroup">The stripe group number.</param>
    /// <param name="parityCount">Number of parity devices (1 for RAID 5, 2 for RAID 6).</param>
    /// <returns>Array of device indices assigned to parity for this stripe group.</returns>
    public int[] GetParityDeviceIndices(long stripeGroup, int parityCount)
    {
        var indices = new int[parityCount];
        for (int p = 0; p < parityCount; p++)
        {
            // Left-symmetric rotation: parity device rotates with each stripe group
            indices[p] = (int)((_deviceCount - 1 - p - stripeGroup % _deviceCount + _deviceCount * 2) % _deviceCount);
        }
        return indices;
    }

    /// <summary>
    /// Gets the data device index within a stripe group, accounting for rotating parity.
    /// </summary>
    /// <param name="stripeGroup">The stripe group number.</param>
    /// <param name="dataIndex">The data device index within data devices (0-based).</param>
    /// <param name="parityCount">Number of parity devices.</param>
    /// <returns>The physical device index for the given data slot.</returns>
    public int GetDataDeviceIndex(long stripeGroup, int dataIndex, int parityCount)
    {
        var parityIndices = GetParityDeviceIndices(stripeGroup, parityCount);
        int assigned = 0;
        for (int d = 0; d < _deviceCount; d++)
        {
            bool isParity = false;
            for (int p = 0; p < parityIndices.Length; p++)
            {
                if (parityIndices[p] == d)
                {
                    isParity = true;
                    break;
                }
            }
            if (!isParity)
            {
                if (assigned == dataIndex)
                    return d;
                assigned++;
            }
        }
        throw new InvalidOperationException($"Data index {dataIndex} not found in stripe group {stripeGroup}.");
    }

    private BlockMapping[] MapStriped(long logicalBlock)
    {
        int deviceIndex = (int)((logicalBlock / _stripeSizeBlocks) % _dataDeviceCount);
        long physicalBlock = (logicalBlock / ((long)_stripeSizeBlocks * _dataDeviceCount)) * _stripeSizeBlocks
                             + (logicalBlock % _stripeSizeBlocks);

        return new[] { new BlockMapping(deviceIndex, physicalBlock) };
    }

    private BlockMapping[] MapMirrored(long logicalBlock)
    {
        // All mirror devices hold the same block at the same physical offset
        int mirrorCount = Math.Min(_config.MirrorCount, _deviceCount);
        var mappings = new BlockMapping[mirrorCount];
        for (int i = 0; i < mirrorCount; i++)
        {
            mappings[i] = new BlockMapping(i, logicalBlock);
        }
        return mappings;
    }

    private BlockMapping[] MapParity(long logicalBlock, int parityCount)
    {
        // Stripe group and position within stripe
        long stripeGroup = logicalBlock / ((long)_stripeSizeBlocks * _dataDeviceCount);
        long offsetInStripe = logicalBlock % ((long)_stripeSizeBlocks * _dataDeviceCount);
        int dataDeviceOrdinal = (int)(offsetInStripe / _stripeSizeBlocks);
        long blockInStripe = offsetInStripe % _stripeSizeBlocks;

        // Physical block within the stripe group
        long physicalBlock = stripeGroup * _stripeSizeBlocks + blockInStripe;

        // Get the actual device index accounting for rotating parity
        int dataDeviceIndex = GetDataDeviceIndex(stripeGroup, dataDeviceOrdinal, parityCount);

        // Build result: data mapping + parity mappings
        var parityIndices = GetParityDeviceIndices(stripeGroup, parityCount);
        var mappings = new BlockMapping[1 + parityCount];
        mappings[0] = new BlockMapping(dataDeviceIndex, physicalBlock);
        for (int p = 0; p < parityCount; p++)
        {
            mappings[1 + p] = new BlockMapping(parityIndices[p], physicalBlock);
        }

        return mappings;
    }

    private BlockMapping[] MapStripedMirror(long logicalBlock)
    {
        int pairCount = _deviceCount / _config.MirrorCount;
        int mirrorCount = _config.MirrorCount;

        // Stripe across mirror pairs
        int pairIndex = (int)((logicalBlock / _stripeSizeBlocks) % pairCount);
        long physicalBlock = (logicalBlock / ((long)_stripeSizeBlocks * pairCount)) * _stripeSizeBlocks
                             + (logicalBlock % _stripeSizeBlocks);

        // Write to all members of the pair
        var mappings = new BlockMapping[mirrorCount];
        int baseDevice = pairIndex * mirrorCount;
        for (int m = 0; m < mirrorCount; m++)
        {
            mappings[m] = new BlockMapping(baseDevice + m, physicalBlock);
        }

        return mappings;
    }

    private static void ValidateDeviceCount(
        DeviceLayoutType layout, int deviceCount, int mirrorCount, int parityDeviceCount)
    {
        (int minimum, string reason) = layout switch
        {
            DeviceLayoutType.Striped => (2, "Striped layout requires at least 2 devices"),
            DeviceLayoutType.Mirrored => (2, "Mirrored layout requires at least 2 devices"),
            DeviceLayoutType.Parity => (3, "Parity (RAID 5) layout requires at least 3 devices"),
            DeviceLayoutType.DoubleParity => (4, "Double parity (RAID 6) layout requires at least 4 devices"),
            DeviceLayoutType.StripedMirror => (4, "Striped mirror (RAID 10) layout requires at least 4 devices"),
            _ => throw new ArgumentOutOfRangeException(nameof(layout), $"Unsupported layout: {layout}")
        };

        if (deviceCount < minimum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deviceCount),
                $"{reason}. Got {deviceCount}.");
        }

        if (layout == DeviceLayoutType.StripedMirror && deviceCount % mirrorCount != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deviceCount),
                $"Striped mirror requires device count ({deviceCount}) to be a multiple of mirror count ({mirrorCount}).");
        }

        if (layout is DeviceLayoutType.Parity or DeviceLayoutType.DoubleParity)
        {
            int dataDevices = deviceCount - parityDeviceCount;
            if (dataDevices < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(deviceCount),
                    $"Parity layout requires at least 1 data device. Got {deviceCount} total with {parityDeviceCount} parity devices.");
            }
        }
    }
}
