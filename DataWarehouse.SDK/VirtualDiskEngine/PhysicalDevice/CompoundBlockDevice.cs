using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;

/// <summary>
/// Presents an array of <see cref="IPhysicalBlockDevice"/> instances as a single
/// <see cref="IBlockDevice"/> using configurable RAID layouts (stripe, mirror,
/// parity, double parity, striped mirror).
/// </summary>
/// <remarks>
/// <para>
/// This is the foundational abstraction for device-level RAID in the VDE layer.
/// Any code that operates on <see cref="IBlockDevice"/> works identically with
/// <see cref="CompoundBlockDevice"/>, enabling transparent multi-device aggregation.
/// </para>
/// <para>
/// For parity layouts (RAID 5/6), write operations use read-modify-write with
/// XOR-based parity calculation. A per-stripe-group semaphore serializes parity
/// writes to ensure atomicity of the read-modify-write cycle. Data reads and
/// striped writes are fully concurrent.
/// </para>
/// <para>
/// Mirror reads use failover: the first online device is tried, and if it fails
/// the next mirror is attempted. Mirror writes go to all devices in parallel.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91: CompoundBlockDevice (CBDV-01)")]
public sealed class CompoundBlockDevice : IBlockDevice
{
    private readonly IReadOnlyList<IPhysicalBlockDevice> _devices;
    private readonly CompoundDeviceConfiguration _config;
    private readonly DeviceLayoutEngine _layout;
    private readonly int _blockSize;
    private readonly long _blockCount;
    private readonly SemaphoreSlim _parityWriteLock;
    private volatile bool _disposed;

    /// <inheritdoc />
    public int BlockSize => _blockSize;

    /// <inheritdoc />
    public long BlockCount => _blockCount;

    /// <summary>
    /// Gets the underlying physical devices that compose this compound device.
    /// </summary>
    public IReadOnlyList<IPhysicalBlockDevice> Devices => _devices;

    /// <summary>
    /// Gets the configuration used to construct this compound device.
    /// </summary>
    public CompoundDeviceConfiguration Configuration => _config;

    /// <summary>
    /// Gets the active layout type for this compound device.
    /// </summary>
    public DeviceLayoutType ActiveLayout => _config.Layout;

    /// <summary>
    /// Initializes a new <see cref="CompoundBlockDevice"/> over the given physical devices.
    /// </summary>
    /// <param name="devices">The physical devices to aggregate. Must meet minimum count for the layout type.</param>
    /// <param name="config">The compound device configuration specifying RAID layout and parameters.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="devices"/> or <paramref name="config"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when any device is offline, when block sizes do not match, or when the
    /// device count is insufficient for the configured layout type.
    /// </exception>
    public CompoundBlockDevice(IReadOnlyList<IPhysicalBlockDevice> devices, CompoundDeviceConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(config);

        if (devices.Count == 0)
            throw new ArgumentException("At least one device is required.", nameof(devices));

        // Validate all devices are online
        for (int i = 0; i < devices.Count; i++)
        {
            if (devices[i] is null)
                throw new ArgumentException($"Device at index {i} is null.", nameof(devices));
            if (!devices[i].IsOnline)
                throw new ArgumentException($"Device at index {i} ({devices[i].DeviceInfo.DeviceId}) is offline.", nameof(devices));
        }

        // Determine block size
        _blockSize = config.OverrideBlockSize.HasValue
            ? checked((int)config.OverrideBlockSize.Value)
            : devices[0].BlockSize;

        // Validate matching block sizes (unless override is set)
        if (!config.OverrideBlockSize.HasValue)
        {
            for (int i = 1; i < devices.Count; i++)
            {
                if (devices[i].BlockSize != _blockSize)
                {
                    throw new ArgumentException(
                        $"Device at index {i} has block size {devices[i].BlockSize}, " +
                        $"expected {_blockSize}. Set OverrideBlockSize to force a common size.",
                        nameof(devices));
                }
            }
        }

        _devices = devices;
        _config = config;

        // DeviceLayoutEngine validates device count against layout type
        _layout = new DeviceLayoutEngine(config, devices.Count);

        // Calculate logical block count based on the smallest device
        long minDeviceBlocks = long.MaxValue;
        for (int i = 0; i < devices.Count; i++)
        {
            long deviceBlocks = devices[i].BlockCount;
            if (deviceBlocks < minDeviceBlocks)
                minDeviceBlocks = deviceBlocks;
        }
        _blockCount = _layout.CalculateTotalLogicalBlocks(minDeviceBlocks);

        // Parity writes need serialization per stripe group; use a single semaphore
        // for simplicity (could be sharded by stripe group for higher concurrency)
        _parityWriteLock = new SemaphoreSlim(1, 1);
    }

    /// <inheritdoc />
    public async Task ReadBlockAsync(long blockNumber, Memory<byte> buffer, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ValidateBlockNumber(blockNumber);
        ValidateBuffer(buffer);

        var mappings = _layout.MapLogicalToPhysical(blockNumber);

        switch (_config.Layout)
        {
            case DeviceLayoutType.Striped:
                await ReadFromDevice(mappings[0], buffer, ct).ConfigureAwait(false);
                break;

            case DeviceLayoutType.Mirrored:
                await ReadFromMirror(mappings, buffer, ct).ConfigureAwait(false);
                break;

            case DeviceLayoutType.Parity:
            case DeviceLayoutType.DoubleParity:
                await ReadFromParityArray(blockNumber, mappings, buffer, ct).ConfigureAwait(false);
                break;

            case DeviceLayoutType.StripedMirror:
                await ReadFromMirror(mappings, buffer, ct).ConfigureAwait(false);
                break;

            default:
                throw new InvalidOperationException($"Unsupported layout: {_config.Layout}");
        }
    }

    /// <inheritdoc />
    public async Task WriteBlockAsync(long blockNumber, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ValidateBlockNumber(blockNumber);
        if (data.Length < _blockSize)
            throw new ArgumentException($"Data must be at least {_blockSize} bytes, got {data.Length}.", nameof(data));

        var mappings = _layout.MapLogicalToPhysical(blockNumber);

        switch (_config.Layout)
        {
            case DeviceLayoutType.Striped:
                await WriteToDevice(mappings[0], data, ct).ConfigureAwait(false);
                break;

            case DeviceLayoutType.Mirrored:
                await WriteToAllMirrors(mappings, data, ct).ConfigureAwait(false);
                break;

            case DeviceLayoutType.Parity:
            case DeviceLayoutType.DoubleParity:
                await WriteToParityArray(blockNumber, mappings, data, ct).ConfigureAwait(false);
                break;

            case DeviceLayoutType.StripedMirror:
                await WriteToAllMirrors(mappings, data, ct).ConfigureAwait(false);
                break;

            default:
                throw new InvalidOperationException($"Unsupported layout: {_config.Layout}");
        }
    }

    /// <inheritdoc />
    public async Task FlushAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var tasks = new Task[_devices.Count];
        for (int i = 0; i < _devices.Count; i++)
        {
            tasks[i] = _devices[i].FlushAsync(ct);
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        var exceptions = new List<Exception>();
        for (int i = 0; i < _devices.Count; i++)
        {
            try
            {
                await _devices[i].DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }

        _parityWriteLock.Dispose();

        if (exceptions.Count > 0)
            throw new AggregateException("One or more devices failed to dispose.", exceptions);
    }

    #region Read Helpers

    private async Task ReadFromDevice(BlockMapping mapping, Memory<byte> buffer, CancellationToken ct)
    {
        var device = _devices[mapping.DeviceIndex];
        if (!device.IsOnline)
            throw new IOException($"Device {mapping.DeviceIndex} is offline and no redundancy is available for striped layout.");

        await device.ReadBlockAsync(mapping.PhysicalBlock, buffer, ct).ConfigureAwait(false);
    }

    private async Task ReadFromMirror(BlockMapping[] mappings, Memory<byte> buffer, CancellationToken ct)
    {
        // Try each mirror in order until one succeeds
        Exception? lastException = null;
        for (int i = 0; i < mappings.Length; i++)
        {
            var device = _devices[mappings[i].DeviceIndex];
            if (!device.IsOnline) continue;

            try
            {
                await device.ReadBlockAsync(mappings[i].PhysicalBlock, buffer, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
            }
        }

        throw new IOException(
            "All mirror devices are offline or failed to read.",
            lastException);
    }

    private async Task ReadFromParityArray(
        long logicalBlock, BlockMapping[] mappings, Memory<byte> buffer, CancellationToken ct)
    {
        // mappings[0] = data device, mappings[1..] = parity devices
        var dataMapping = mappings[0];
        var dataDevice = _devices[dataMapping.DeviceIndex];

        if (dataDevice.IsOnline)
        {
            // Normal path: read directly from data device
            try
            {
                await dataDevice.ReadBlockAsync(dataMapping.PhysicalBlock, buffer, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fall through to reconstruction
            }
        }

        // Degraded path: reconstruct data using XOR of all other devices in the stripe
        await ReconstructViaXor(logicalBlock, dataMapping.DeviceIndex, buffer, ct).ConfigureAwait(false);
    }

    private async Task ReconstructViaXor(
        long logicalBlock, int failedDeviceIndex, Memory<byte> buffer, CancellationToken ct)
    {
        int parityCount = _config.Layout == DeviceLayoutType.DoubleParity
            ? _config.ParityDeviceCount
            : 1;

        long stripeGroup = logicalBlock /
            ((long)_config.StripeSizeBlocks * _layout.DataDeviceCount);
        long blockInStripe = logicalBlock % _config.StripeSizeBlocks;
        long physicalBlock = stripeGroup * _config.StripeSizeBlocks + blockInStripe;

        // Clear the output buffer
        buffer.Span.Slice(0, _blockSize).Clear();

        var tempBuffer = new byte[_blockSize];

        // XOR all surviving devices (data + parity) at the same physical block offset
        for (int d = 0; d < _devices.Count; d++)
        {
            if (d == failedDeviceIndex) continue;

            var device = _devices[d];
            if (!device.IsOnline)
                throw new IOException($"Cannot reconstruct: device {d} is also offline. Multiple device failure exceeds redundancy.");

            await device.ReadBlockAsync(physicalBlock, tempBuffer.AsMemory(), ct).ConfigureAwait(false);

            // XOR into buffer
            var bufferSpan = buffer.Span;
            for (int b = 0; b < _blockSize; b++)
            {
                bufferSpan[b] ^= tempBuffer[b];
            }
        }
    }

    #endregion

    #region Write Helpers

    private async Task WriteToDevice(BlockMapping mapping, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var device = _devices[mapping.DeviceIndex];
        if (!device.IsOnline)
            throw new IOException($"Device {mapping.DeviceIndex} is offline.");

        await device.WriteBlockAsync(mapping.PhysicalBlock, data, ct).ConfigureAwait(false);
    }

    private async Task WriteToAllMirrors(BlockMapping[] mappings, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var tasks = new Task[mappings.Length];
        for (int i = 0; i < mappings.Length; i++)
        {
            var device = _devices[mappings[i].DeviceIndex];
            if (!device.IsOnline)
                throw new IOException($"Mirror device {mappings[i].DeviceIndex} is offline. Cannot write to degraded mirror.");

            tasks[i] = device.WriteBlockAsync(mappings[i].PhysicalBlock, data, ct);
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task WriteToParityArray(
        long logicalBlock, BlockMapping[] mappings, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        // Read-modify-write must be atomic per stripe group
        await _parityWriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dataMapping = mappings[0];
            int parityCount = mappings.Length - 1;

            // Read old data from the target device
            var oldData = new byte[_blockSize];
            var dataDevice = _devices[dataMapping.DeviceIndex];
            if (dataDevice.IsOnline)
            {
                try
                {
                    await dataDevice.ReadBlockAsync(dataMapping.PhysicalBlock, oldData.AsMemory(), ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // If we can't read old data, zero it (first write to this block)
                    Array.Clear(oldData);
                }
            }
            else
            {
                Array.Clear(oldData);
            }

            // For each parity device, update parity using XOR differential:
            // newParity = oldParity XOR oldData XOR newData
            var parityTasks = new List<Task>(parityCount + 1);

            for (int p = 0; p < parityCount; p++)
            {
                var parityMapping = mappings[1 + p];
                var parityDevice = _devices[parityMapping.DeviceIndex];
                if (!parityDevice.IsOnline)
                    throw new IOException($"Parity device {parityMapping.DeviceIndex} is offline.");

                // Read old parity
                var oldParity = new byte[_blockSize];
                try
                {
                    await parityDevice.ReadBlockAsync(parityMapping.PhysicalBlock, oldParity.AsMemory(), ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Array.Clear(oldParity);
                }

                // Compute new parity: oldParity XOR oldData XOR newData
                var newParity = new byte[_blockSize];
                var dataSpan = data.Span;
                for (int b = 0; b < _blockSize; b++)
                {
                    newParity[b] = (byte)(oldParity[b] ^ oldData[b] ^ dataSpan[b]);
                }

                // Write new parity
                parityTasks.Add(parityDevice.WriteBlockAsync(
                    parityMapping.PhysicalBlock, newParity.AsMemory(), ct));
            }

            // Write new data
            if (!dataDevice.IsOnline)
                throw new IOException($"Data device {dataMapping.DeviceIndex} is offline.");

            parityTasks.Add(dataDevice.WriteBlockAsync(dataMapping.PhysicalBlock, data, ct));

            await Task.WhenAll(parityTasks).ConfigureAwait(false);
        }
        finally
        {
            _parityWriteLock.Release();
        }
    }

    #endregion

    #region Validation

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CompoundBlockDevice));
    }

    private void ValidateBlockNumber(long blockNumber)
    {
        if (blockNumber < 0 || blockNumber >= _blockCount)
            throw new ArgumentOutOfRangeException(
                nameof(blockNumber),
                $"Block number {blockNumber} is out of range [0, {_blockCount}).");
    }

    private void ValidateBuffer(Memory<byte> buffer)
    {
        if (buffer.Length < _blockSize)
            throw new ArgumentException(
                $"Buffer must be at least {_blockSize} bytes, got {buffer.Length}.",
                nameof(buffer));
    }

    #endregion
}
