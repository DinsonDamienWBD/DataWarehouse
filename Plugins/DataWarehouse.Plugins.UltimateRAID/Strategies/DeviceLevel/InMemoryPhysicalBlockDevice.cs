using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine;
using DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;

namespace DataWarehouse.Plugins.UltimateRAID.Strategies.DeviceLevel;

/// <summary>
/// In-memory implementation of <see cref="IPhysicalBlockDevice"/> for testing device-level
/// RAID operations without real hardware. Stores blocks in a <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// keyed by block number, providing thread-safe read/write semantics.
/// </summary>
/// <remarks>
/// <para>
/// This is a production-quality test helper, not a stub. It faithfully implements the full
/// <see cref="IPhysicalBlockDevice"/> contract including scatter-gather I/O, TRIM, health
/// monitoring, and online/offline state simulation for fault-injection testing.
/// </para>
/// <para>
/// The <see cref="SetOnline"/> method enables controlled failure simulation in integration tests
/// by taking devices offline mid-operation, exercising RAID failover and reconstruction paths.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91: Test helper (CBDV-05)")]
public sealed class InMemoryPhysicalBlockDevice : IPhysicalBlockDevice
{
    private readonly ConcurrentDictionary<long, byte[]> _blocks = new();
    private readonly int _blockSize;
    private readonly long _blockCount;
    private readonly PhysicalDeviceInfo _deviceInfo;
    private volatile bool _isOnline = true;
    private volatile bool _disposed;
    private long _totalBytesWritten;
    private long _totalBytesRead;

    /// <summary>
    /// Initializes a new <see cref="InMemoryPhysicalBlockDevice"/> with the specified geometry.
    /// </summary>
    /// <param name="deviceId">Unique identifier for this device.</param>
    /// <param name="blockSize">Block size in bytes. Must be positive and a power of two.</param>
    /// <param name="blockCount">Total number of blocks. Must be positive.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="deviceId"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="blockSize"/> is not a positive power of two,
    /// or <paramref name="blockCount"/> is not positive.
    /// </exception>
    public InMemoryPhysicalBlockDevice(string deviceId, int blockSize, long blockCount)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        if (blockSize <= 0 || (blockSize & (blockSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize), $"Block size must be a positive power of two, got {blockSize}.");
        if (blockCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockCount), $"Block count must be positive, got {blockCount}.");

        _blockSize = blockSize;
        _blockCount = blockCount;

        _deviceInfo = new PhysicalDeviceInfo(
            DeviceId: deviceId,
            DevicePath: $"/dev/inmem/{deviceId}",
            SerialNumber: $"INMEM-{deviceId}",
            ModelNumber: "InMemory Test Device",
            FirmwareVersion: "1.0.0",
            MediaType: MediaType.RamDisk,
            BusType: BusType.VirtIo,
            Transport: DeviceTransport.Virtual,
            CapacityBytes: (long)blockSize * blockCount,
            PhysicalSectorSize: blockSize,
            LogicalSectorSize: blockSize,
            OptimalIoSize: blockSize * 8,
            SupportsTrim: true,
            SupportsVolatileWriteCache: false,
            NvmeNamespaceId: 0,
            ControllerPath: null,
            NumaNode: null);
    }

    /// <inheritdoc />
    public string DeviceId => _deviceInfo.DeviceId;

    /// <inheritdoc />
    public int BlockSize => _blockSize;

    /// <inheritdoc />
    public long BlockCount => _blockCount;

    /// <inheritdoc />
    public bool IsOnline => _isOnline;

    /// <inheritdoc />
    public PhysicalDeviceInfo DeviceInfo => _deviceInfo;

    /// <inheritdoc />
    public long PhysicalSectorSize => _blockSize;

    /// <inheritdoc />
    public long LogicalSectorSize => _blockSize;

    /// <summary>
    /// Sets the online/offline state of this device. Used for simulating device failures
    /// in integration tests.
    /// </summary>
    /// <param name="online">
    /// When <see langword="true"/>, the device accepts I/O operations.
    /// When <see langword="false"/>, all I/O operations throw <see cref="System.IO.IOException"/>.
    /// </param>
    public void SetOnline(bool online) => _isOnline = online;

    /// <summary>
    /// Gets the number of blocks currently stored (written at least once).
    /// </summary>
    public int StoredBlockCount => _blocks.Count;

    /// <inheritdoc />
    public Task ReadBlockAsync(long blockNumber, Memory<byte> buffer, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ThrowIfOffline();
        ValidateBlockNumber(blockNumber);

        if (buffer.Length < _blockSize)
            throw new ArgumentException($"Buffer must be at least {_blockSize} bytes, got {buffer.Length}.", nameof(buffer));

        ct.ThrowIfCancellationRequested();

        if (_blocks.TryGetValue(blockNumber, out var data))
        {
            data.AsSpan(0, _blockSize).CopyTo(buffer.Span);
        }
        else
        {
            // Unwritten blocks return zeros
            buffer.Span.Slice(0, _blockSize).Clear();
        }

        Interlocked.Add(ref _totalBytesRead, _blockSize);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task WriteBlockAsync(long blockNumber, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ThrowIfOffline();
        ValidateBlockNumber(blockNumber);

        if (data.Length < _blockSize)
            throw new ArgumentException($"Data must be at least {_blockSize} bytes, got {data.Length}.", nameof(data));

        ct.ThrowIfCancellationRequested();

        var block = new byte[_blockSize];
        data.Span.Slice(0, _blockSize).CopyTo(block);
        _blocks[blockNumber] = block;

        Interlocked.Add(ref _totalBytesWritten, _blockSize);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task FlushAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ThrowIfOffline();
        ct.ThrowIfCancellationRequested();
        // No-op for in-memory storage
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task TrimAsync(long blockNumber, int blockCount, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ThrowIfOffline();
        ct.ThrowIfCancellationRequested();

        for (long b = blockNumber; b < blockNumber + blockCount && b < _blockCount; b++)
        {
            _blocks.TryRemove(b, out _);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> ReadScatterAsync(
        IReadOnlyList<(long blockNumber, Memory<byte> buffer)> operations,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ThrowIfOffline();
        ArgumentNullException.ThrowIfNull(operations);
        ct.ThrowIfCancellationRequested();

        int completed = 0;
        for (int i = 0; i < operations.Count; i++)
        {
            var (blockNumber, buffer) = operations[i];
            ValidateBlockNumber(blockNumber);

            if (buffer.Length < _blockSize)
                throw new ArgumentException($"Buffer at index {i} is too small ({buffer.Length} < {_blockSize}).");

            if (_blocks.TryGetValue(blockNumber, out var data))
            {
                data.AsSpan(0, _blockSize).CopyTo(buffer.Span);
            }
            else
            {
                buffer.Span.Slice(0, _blockSize).Clear();
            }

            Interlocked.Add(ref _totalBytesRead, _blockSize);
            completed++;
        }

        return Task.FromResult(completed);
    }

    /// <inheritdoc />
    public Task<int> WriteGatherAsync(
        IReadOnlyList<(long blockNumber, ReadOnlyMemory<byte> data)> operations,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ThrowIfOffline();
        ArgumentNullException.ThrowIfNull(operations);
        ct.ThrowIfCancellationRequested();

        int completed = 0;
        for (int i = 0; i < operations.Count; i++)
        {
            var (blockNumber, opData) = operations[i];
            ValidateBlockNumber(blockNumber);

            if (opData.Length < _blockSize)
                throw new ArgumentException($"Data at index {i} is too small ({opData.Length} < {_blockSize}).");

            var block = new byte[_blockSize];
            opData.Span.Slice(0, _blockSize).CopyTo(block);
            _blocks[blockNumber] = block;

            Interlocked.Add(ref _totalBytesWritten, _blockSize);
            completed++;
        }

        return Task.FromResult(completed);
    }

    /// <inheritdoc />
    public Task<PhysicalDeviceHealth> GetHealthAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        var health = new PhysicalDeviceHealth(
            IsHealthy: _isOnline,
            TemperatureCelsius: 25.0,
            WearLevelPercent: 0.0,
            TotalBytesWritten: Interlocked.Read(ref _totalBytesWritten),
            TotalBytesRead: Interlocked.Read(ref _totalBytesRead),
            UncorrectableErrors: 0,
            ReallocatedSectors: 0,
            PowerOnHours: 1,
            EstimatedRemainingLife: TimeSpan.FromDays(3650),
            RawSmartAttributes: new Dictionary<string, string>
            {
                ["StoredBlocks"] = _blocks.Count.ToString(),
                ["Online"] = _isOnline.ToString()
            });

        return Task.FromResult(health);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _blocks.Clear();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(InMemoryPhysicalBlockDevice));
    }

    private void ThrowIfOffline()
    {
        if (!_isOnline)
            throw new System.IO.IOException($"Device {_deviceInfo.DeviceId} is offline.");
    }

    private void ValidateBlockNumber(long blockNumber)
    {
        if (blockNumber < 0 || blockNumber >= _blockCount)
            throw new ArgumentOutOfRangeException(
                nameof(blockNumber),
                $"Block number {blockNumber} is out of range [0, {_blockCount}).");
    }
}
