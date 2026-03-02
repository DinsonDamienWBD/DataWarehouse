using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;

namespace DataWarehouse.Plugins.UltimateRAID.Strategies.DeviceLevel;

/// <summary>
/// Shared base for all device-level RAID strategies. Provides common validation,
/// compound device construction, and health inspection logic.
/// </summary>
internal abstract class DeviceLevelRaidStrategyBase : IDeviceRaidStrategy
{
    /// <inheritdoc />
    public abstract DeviceLayoutType Layout { get; }

    /// <inheritdoc />
    public abstract string StrategyName { get; }

    /// <inheritdoc />
    public abstract int MinimumDeviceCount { get; }

    /// <inheritdoc />
    public abstract int FaultTolerance { get; }

    /// <summary>
    /// Gets the parity device count for this layout (0 for non-parity layouts).
    /// </summary>
    protected virtual int ParityDeviceCount => 0;

    /// <summary>
    /// Gets the mirror count for mirror-based layouts (defaults to 2).
    /// </summary>
    protected virtual int MirrorCount => 2;

    /// <inheritdoc />
    public Task<CompoundBlockDevice> CreateCompoundDeviceAsync(
        IReadOnlyList<IPhysicalBlockDevice> devices,
        DeviceRaidConfiguration config,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();

        if (devices.Count < MinimumDeviceCount)
        {
            throw new ArgumentException(
                $"{StrategyName} requires at least {MinimumDeviceCount} devices, got {devices.Count}.",
                nameof(devices));
        }

        ct.ThrowIfCancellationRequested();

        var compoundConfig = BuildCompoundConfiguration(config);
        var compound = new CompoundBlockDevice(devices, compoundConfig);

        return Task.FromResult(compound);
    }

    /// <inheritdoc />
    public async Task RebuildDeviceAsync(
        CompoundBlockDevice compound,
        int failedDeviceIndex,
        IPhysicalBlockDevice replacement,
        CancellationToken ct = default,
        IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(compound);
        ArgumentNullException.ThrowIfNull(replacement);

        if (failedDeviceIndex < 0 || failedDeviceIndex >= compound.Devices.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failedDeviceIndex),
                $"Device index {failedDeviceIndex} is out of range [0, {compound.Devices.Count}).");
        }

        if (!replacement.IsOnline)
        {
            throw new ArgumentException("Replacement device must be online.", nameof(replacement));
        }

        if (replacement.BlockSize != compound.BlockSize)
        {
            throw new ArgumentException(
                $"Replacement device block size {replacement.BlockSize} does not match " +
                $"array block size {compound.BlockSize}.",
                nameof(replacement));
        }

        await RebuildCoreAsync(compound, failedDeviceIndex, replacement, ct, progress)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public DeviceRaidHealth GetHealth(CompoundBlockDevice compound)
    {
        ArgumentNullException.ThrowIfNull(compound);

        int online = 0;
        int offline = 0;

        for (int i = 0; i < compound.Devices.Count; i++)
        {
            if (compound.Devices[i].IsOnline)
                online++;
            else
                offline++;
        }

        var state = DetermineArrayState(online, offline, compound.Devices.Count);
        return new DeviceRaidHealth(state, online, offline, 0.0);
    }

    /// <summary>
    /// Builds the <see cref="CompoundDeviceConfiguration"/> appropriate for this layout.
    /// </summary>
    protected virtual CompoundDeviceConfiguration BuildCompoundConfiguration(DeviceRaidConfiguration config)
    {
        return new CompoundDeviceConfiguration(Layout)
        {
            StripeSizeBlocks = config.StripeSizeBlocks
        };
    }

    /// <summary>
    /// Core rebuild implementation. Override in subclasses for layout-specific reconstruction.
    /// </summary>
    protected abstract Task RebuildCoreAsync(
        CompoundBlockDevice compound,
        int failedDeviceIndex,
        IPhysicalBlockDevice replacement,
        CancellationToken ct,
        IProgress<double>? progress);

    /// <summary>
    /// Determines the array state based on online/offline device counts.
    /// </summary>
    protected virtual ArrayState DetermineArrayState(int online, int offline, int total)
    {
        if (offline == 0)
            return ArrayState.Optimal;

        if (offline <= FaultTolerance)
            return ArrayState.Degraded;

        return ArrayState.Failed;
    }

    /// <summary>
    /// Copies all blocks from one device at given physical offsets to the replacement device,
    /// reconstructing data via XOR of all surviving devices (for parity layouts) or direct
    /// copy (for mirror layouts).
    /// </summary>
    protected static async Task CopyBlocksFromDeviceAsync(
        IPhysicalBlockDevice source,
        IPhysicalBlockDevice destination,
        long blockCount,
        int blockSize,
        CancellationToken ct,
        IProgress<double>? progress)
    {
        var buffer = new byte[blockSize];

        for (long block = 0; block < blockCount; block++)
        {
            ct.ThrowIfCancellationRequested();

            await source.ReadBlockAsync(block, buffer.AsMemory(), ct).ConfigureAwait(false);
            await destination.WriteBlockAsync(block, buffer.AsMemory(), ct).ConfigureAwait(false);

            progress?.Report((double)(block + 1) / blockCount);
        }

        await destination.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reconstructs a failed device's data using XOR of all surviving devices in
    /// parity-based layouts, writing the result to the replacement device.
    /// </summary>
    protected static async Task ReconstructViaXorAsync(
        IReadOnlyList<IPhysicalBlockDevice> devices,
        int failedDeviceIndex,
        IPhysicalBlockDevice replacement,
        long blockCount,
        int blockSize,
        CancellationToken ct,
        IProgress<double>? progress)
    {
        var reconstructed = new byte[blockSize];
        var tempBuffer = new byte[blockSize];

        for (long block = 0; block < blockCount; block++)
        {
            ct.ThrowIfCancellationRequested();

            // Clear reconstruction buffer
            Array.Clear(reconstructed);

            // XOR all surviving devices at this block offset
            for (int d = 0; d < devices.Count; d++)
            {
                if (d == failedDeviceIndex) continue;

                var device = devices[d];
                if (!device.IsOnline)
                {
                    throw new IOException(
                        $"Cannot reconstruct: device {d} is also offline. " +
                        "Multiple device failure exceeds redundancy.");
                }

                await device.ReadBlockAsync(block, tempBuffer.AsMemory(), ct).ConfigureAwait(false);

                for (int b = 0; b < blockSize; b++)
                {
                    reconstructed[b] ^= tempBuffer[b];
                }
            }

            await replacement.WriteBlockAsync(block, reconstructed.AsMemory(), ct).ConfigureAwait(false);

            progress?.Report((double)(block + 1) / blockCount);
        }

        await replacement.FlushAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Device-level RAID 0 (Striped) strategy. Distributes blocks across all devices
/// for maximum throughput. No redundancy: any single device failure results in data loss.
/// </summary>
/// <remarks>
/// Rebuild is not supported for RAID 0 because there is no parity or mirror data
/// to reconstruct from. The <see cref="RebuildCoreAsync"/> method always throws
/// <see cref="NotSupportedException"/>.
/// </remarks>
public sealed class DeviceRaid0Strategy : DeviceLevelRaidStrategyBase
{
    /// <inheritdoc />
    public override DeviceLayoutType Layout => DeviceLayoutType.Striped;

    /// <inheritdoc />
    public override string StrategyName => "Device RAID 0 (Striped)";

    /// <inheritdoc />
    public override int MinimumDeviceCount => 2;

    /// <inheritdoc />
    public override int FaultTolerance => 0;

    /// <inheritdoc />
    protected override Task RebuildCoreAsync(
        CompoundBlockDevice compound,
        int failedDeviceIndex,
        IPhysicalBlockDevice replacement,
        CancellationToken ct,
        IProgress<double>? progress)
    {
        throw new NotSupportedException(
            "RAID 0 does not support rebuild. Data is lost when any device fails.");
    }

    /// <inheritdoc />
    protected override ArrayState DetermineArrayState(int online, int offline, int total)
    {
        // RAID 0 has zero fault tolerance: any offline device means failure
        return offline == 0 ? ArrayState.Optimal : ArrayState.Failed;
    }
}

/// <summary>
/// Device-level RAID 1 (Mirrored) strategy. Writes all data to every device in the array,
/// providing full redundancy at the cost of capacity. The array can survive N-1 device
/// failures as long as at least one device remains online.
/// </summary>
/// <remarks>
/// Rebuild copies data block-by-block from the first available online mirror device
/// to the replacement device.
/// </remarks>
public sealed class DeviceRaid1Strategy : DeviceLevelRaidStrategyBase
{
    /// <inheritdoc />
    public override DeviceLayoutType Layout => DeviceLayoutType.Mirrored;

    /// <inheritdoc />
    public override string StrategyName => "Device RAID 1 (Mirrored)";

    /// <inheritdoc />
    public override int MinimumDeviceCount => 2;

    /// <inheritdoc />
    public override int FaultTolerance => 1;

    /// <inheritdoc />
    protected override CompoundDeviceConfiguration BuildCompoundConfiguration(DeviceRaidConfiguration config)
    {
        return new CompoundDeviceConfiguration(DeviceLayoutType.Mirrored)
        {
            StripeSizeBlocks = config.StripeSizeBlocks,
            MirrorCount = 2
        };
    }

    /// <inheritdoc />
    protected override async Task RebuildCoreAsync(
        CompoundBlockDevice compound,
        int failedDeviceIndex,
        IPhysicalBlockDevice replacement,
        CancellationToken ct,
        IProgress<double>? progress)
    {
        // Find first online source device to copy from
        IPhysicalBlockDevice? source = null;
        for (int i = 0; i < compound.Devices.Count; i++)
        {
            if (i != failedDeviceIndex && compound.Devices[i].IsOnline)
            {
                source = compound.Devices[i];
                break;
            }
        }

        if (source is null)
        {
            throw new InvalidOperationException(
                "Cannot rebuild: no online source devices available.");
        }

        await CopyBlocksFromDeviceAsync(
            source, replacement, source.BlockCount, compound.BlockSize, ct, progress)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override ArrayState DetermineArrayState(int online, int offline, int total)
    {
        if (offline == 0) return ArrayState.Optimal;
        if (online >= 1) return ArrayState.Degraded;
        return ArrayState.Failed;
    }
}

/// <summary>
/// Device-level RAID 5 (Parity) strategy. Stripes data across N-1 devices with a single
/// rotating parity device per stripe group. Tolerates exactly one device failure.
/// </summary>
/// <remarks>
/// Rebuild reconstructs the failed device's data using XOR of all surviving devices
/// (data + parity). This is the standard RAID 5 reconstruction algorithm.
/// </remarks>
public sealed class DeviceRaid5Strategy : DeviceLevelRaidStrategyBase
{
    /// <inheritdoc />
    public override DeviceLayoutType Layout => DeviceLayoutType.Parity;

    /// <inheritdoc />
    public override string StrategyName => "Device RAID 5 (Parity)";

    /// <inheritdoc />
    public override int MinimumDeviceCount => 3;

    /// <inheritdoc />
    public override int FaultTolerance => 1;

    /// <inheritdoc />
    protected override int ParityDeviceCount => 1;

    /// <inheritdoc />
    protected override CompoundDeviceConfiguration BuildCompoundConfiguration(DeviceRaidConfiguration config)
    {
        return new CompoundDeviceConfiguration(DeviceLayoutType.Parity)
        {
            StripeSizeBlocks = config.StripeSizeBlocks,
            ParityDeviceCount = 1
        };
    }

    /// <inheritdoc />
    protected override async Task RebuildCoreAsync(
        CompoundBlockDevice compound,
        int failedDeviceIndex,
        IPhysicalBlockDevice replacement,
        CancellationToken ct,
        IProgress<double>? progress)
    {
        // Determine per-device block count from the smallest device
        long minBlockCount = long.MaxValue;
        for (int i = 0; i < compound.Devices.Count; i++)
        {
            if (i != failedDeviceIndex && compound.Devices[i].IsOnline)
            {
                long bc = compound.Devices[i].BlockCount;
                if (bc < minBlockCount) minBlockCount = bc;
            }
        }

        if (minBlockCount == long.MaxValue)
        {
            throw new InvalidOperationException(
                "Cannot rebuild: no online devices available for reconstruction.");
        }

        await ReconstructViaXorAsync(
            compound.Devices, failedDeviceIndex, replacement,
            minBlockCount, compound.BlockSize, ct, progress)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Device-level RAID 6 (Double Parity) strategy. Stripes data across N-2 devices with
/// two rotating parity devices per stripe group. Tolerates up to two simultaneous
/// device failures.
/// </summary>
/// <remarks>
/// <para>
/// Rebuild uses XOR reconstruction when only one device has failed (same as RAID 5).
/// When two devices are offline and only one is being replaced, the rebuild will
/// succeed only if all remaining devices are online.
/// </para>
/// <para>
/// Full dual-device reconstruction (Reed-Solomon/P+Q) is handled by the underlying
/// <see cref="CompoundBlockDevice"/> parity engine; this strategy orchestrates the
/// block-by-block rebuild loop.
/// </para>
/// </remarks>
public sealed class DeviceRaid6Strategy : DeviceLevelRaidStrategyBase
{
    /// <inheritdoc />
    public override DeviceLayoutType Layout => DeviceLayoutType.DoubleParity;

    /// <inheritdoc />
    public override string StrategyName => "Device RAID 6 (Double Parity)";

    /// <inheritdoc />
    public override int MinimumDeviceCount => 4;

    /// <inheritdoc />
    public override int FaultTolerance => 2;

    /// <inheritdoc />
    protected override int ParityDeviceCount => 2;

    /// <inheritdoc />
    protected override CompoundDeviceConfiguration BuildCompoundConfiguration(DeviceRaidConfiguration config)
    {
        return new CompoundDeviceConfiguration(DeviceLayoutType.DoubleParity)
        {
            StripeSizeBlocks = config.StripeSizeBlocks,
            ParityDeviceCount = 2
        };
    }

    /// <inheritdoc />
    protected override async Task RebuildCoreAsync(
        CompoundBlockDevice compound,
        int failedDeviceIndex,
        IPhysicalBlockDevice replacement,
        CancellationToken ct,
        IProgress<double>? progress)
    {
        // Count offline devices to validate rebuild is possible
        int offlineCount = 0;
        for (int i = 0; i < compound.Devices.Count; i++)
        {
            if (!compound.Devices[i].IsOnline) offlineCount++;
        }

        if (offlineCount > FaultTolerance)
        {
            throw new InvalidOperationException(
                $"Cannot rebuild: {offlineCount} devices are offline, " +
                $"exceeding RAID 6 fault tolerance of {FaultTolerance}.");
        }

        // Determine per-device block count from the smallest online device
        long minBlockCount = long.MaxValue;
        for (int i = 0; i < compound.Devices.Count; i++)
        {
            if (i != failedDeviceIndex && compound.Devices[i].IsOnline)
            {
                long bc = compound.Devices[i].BlockCount;
                if (bc < minBlockCount) minBlockCount = bc;
            }
        }

        if (minBlockCount == long.MaxValue)
        {
            throw new InvalidOperationException(
                "Cannot rebuild: no online devices available for reconstruction.");
        }

        await ReconstructViaXorAsync(
            compound.Devices, failedDeviceIndex, replacement,
            minBlockCount, compound.BlockSize, ct, progress)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Device-level RAID 10 (Striped Mirror) strategy. Creates mirror pairs and stripes
/// data across the pairs. Combines the performance benefits of striping with the
/// redundancy of mirroring.
/// </summary>
/// <remarks>
/// <para>
/// Requires an even number of devices (minimum 4). Devices are grouped into mirror
/// pairs: [0,1], [2,3], [4,5], etc. Each pair holds identical data, and stripes are
/// distributed across the pairs.
/// </para>
/// <para>
/// Rebuild copies data from the surviving mirror in the pair to the replacement device.
/// The array can tolerate one failure per mirror pair without data loss.
/// </para>
/// </remarks>
public sealed class DeviceRaid10Strategy : DeviceLevelRaidStrategyBase
{
    /// <inheritdoc />
    public override DeviceLayoutType Layout => DeviceLayoutType.StripedMirror;

    /// <inheritdoc />
    public override string StrategyName => "Device RAID 10 (Striped Mirror)";

    /// <inheritdoc />
    public override int MinimumDeviceCount => 4;

    /// <inheritdoc />
    public override int FaultTolerance => 1;

    /// <inheritdoc />
    protected override int MirrorCount => 2;

    /// <inheritdoc />
    protected override CompoundDeviceConfiguration BuildCompoundConfiguration(DeviceRaidConfiguration config)
    {
        return new CompoundDeviceConfiguration(DeviceLayoutType.StripedMirror)
        {
            StripeSizeBlocks = config.StripeSizeBlocks,
            MirrorCount = 2
        };
    }

    /// <inheritdoc />
    protected override async Task RebuildCoreAsync(
        CompoundBlockDevice compound,
        int failedDeviceIndex,
        IPhysicalBlockDevice replacement,
        CancellationToken ct,
        IProgress<double>? progress)
    {
        // Determine which mirror pair the failed device belongs to
        int pairIndex = failedDeviceIndex / MirrorCount;
        int pairBase = pairIndex * MirrorCount;

        // Find the surviving mirror in the same pair
        IPhysicalBlockDevice? source = null;
        for (int m = 0; m < MirrorCount; m++)
        {
            int deviceIndex = pairBase + m;
            if (deviceIndex != failedDeviceIndex &&
                deviceIndex < compound.Devices.Count &&
                compound.Devices[deviceIndex].IsOnline)
            {
                source = compound.Devices[deviceIndex];
                break;
            }
        }

        if (source is null)
        {
            throw new InvalidOperationException(
                $"Cannot rebuild: no surviving mirror in pair {pairIndex} " +
                $"(devices {pairBase}..{pairBase + MirrorCount - 1}).");
        }

        await CopyBlocksFromDeviceAsync(
            source, replacement, source.BlockCount, compound.BlockSize, ct, progress)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override ArrayState DetermineArrayState(int online, int offline, int total)
    {
        if (offline == 0) return ArrayState.Optimal;

        // RAID 10 can survive one failure per mirror pair.
        // Worst case: if more pairs have both members offline, it is failed.
        // Simple heuristic: if at least half the devices are online, we are degraded.
        if (online >= total / 2) return ArrayState.Degraded;

        return ArrayState.Failed;
    }
}
