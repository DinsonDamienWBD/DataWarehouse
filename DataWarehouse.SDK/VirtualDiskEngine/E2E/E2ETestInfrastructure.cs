using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;

namespace DataWarehouse.SDK.VirtualDiskEngine.E2E;

/// <summary>
/// Shared test helpers for all E2E tests in the VDE stack.
/// Provides device creation, compound device assembly, VDE lifecycle,
/// assertion utilities, deterministic data generation, and round-trip verification.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 95: E2E Test Infrastructure")]
public static class E2ETestInfrastructure
{
    /// <summary>
    /// Creates an array of <see cref="InMemoryPhysicalBlockDevice"/> instances
    /// for use as a device pool in E2E tests.
    /// </summary>
    /// <param name="count">Number of devices to create.</param>
    /// <param name="blockSize">Block size in bytes per device. Default 4096.</param>
    /// <param name="blockCount">Number of blocks per device. Default 65536 (256 MB with 4K blocks).</param>
    /// <returns>Array of in-memory physical block devices.</returns>
    public static InMemoryPhysicalBlockDevice[] CreateInMemoryDevices(
        int count, int blockSize = 4096, long blockCount = 65536)
    {
        var devices = new InMemoryPhysicalBlockDevice[count];
        for (int i = 0; i < count; i++)
        {
            devices[i] = new InMemoryPhysicalBlockDevice(
                deviceId: $"e2e-dev-{i:D3}",
                blockSize: blockSize,
                blockCount: blockCount);
        }
        return devices;
    }

    /// <summary>
    /// Creates a <see cref="CompoundBlockDevice"/> over the given physical devices
    /// using the specified RAID layout.
    /// </summary>
    /// <param name="devices">Physical devices to aggregate.</param>
    /// <param name="layout">RAID layout type.</param>
    /// <param name="stripeSizeBlocks">Stripe size in blocks. Default 256.</param>
    /// <returns>A compound block device.</returns>
    public static CompoundBlockDevice CreateCompoundDevice(
        IPhysicalBlockDevice[] devices,
        DeviceLayoutType layout,
        int stripeSizeBlocks = 256)
    {
        var config = new CompoundDeviceConfiguration(layout)
        {
            StripeSizeBlocks = stripeSizeBlocks
        };
        return new CompoundBlockDevice(devices, config);
    }

    /// <summary>
    /// Creates a VDE backed by a temporary container file, initializes it, and returns
    /// the VDE along with the temp file path for cleanup.
    /// </summary>
    /// <param name="testName">Test name used to generate a unique container file name.</param>
    /// <param name="blockSize">Block size in bytes. Default 4096.</param>
    /// <param name="totalBlocks">Total number of blocks for the container. Default 65536.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of (VDE instance, temp container path).</returns>
    public static async Task<(VirtualDiskEngine vde, string tempPath)> CreateVdeWithTempFile(
        string testName,
        int blockSize = 4096,
        long totalBlocks = 65536,
        CancellationToken ct = default)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "dw-e2e-tests");
        Directory.CreateDirectory(tempDir);

        string tempPath = Path.Combine(tempDir, $"{testName}-{Guid.NewGuid():N}.dwvd");

        var options = new VdeOptions
        {
            ContainerPath = tempPath,
            BlockSize = blockSize,
            TotalBlocks = totalBlocks,
            AutoCreateContainer = true,
            EnableChecksumVerification = true
        };

        var vde = new VirtualDiskEngine(options);
        await vde.InitializeAsync(ct).ConfigureAwait(false);

        return (vde, tempPath);
    }

    /// <summary>
    /// Standard assertion helper. Throws <see cref="InvalidOperationException"/> on failure.
    /// </summary>
    /// <param name="condition">Condition that must be true.</param>
    /// <param name="message">Failure message.</param>
    public static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"E2E Assertion failed: {message}");
    }

    /// <summary>
    /// Asserts that a physical block device reports valid SMART health data.
    /// Verifies temperature is non-negative, wear level is in [0, 100], and
    /// uncorrectable errors are non-negative.
    /// </summary>
    /// <param name="device">The physical block device to check.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task AssertDeviceHealth(
        IPhysicalBlockDevice device,
        CancellationToken ct = default)
    {
        var health = await device.GetHealthAsync(ct).ConfigureAwait(false);

        Assert(health.TemperatureCelsius >= 0,
            $"Temperature must be >= 0, got {health.TemperatureCelsius}");
        Assert(health.WearLevelPercent >= 0 && health.WearLevelPercent <= 100,
            $"WearLevel must be in [0, 100], got {health.WearLevelPercent}");
        Assert(health.UncorrectableErrors >= 0,
            $"UncorrectableErrors must be >= 0, got {health.UncorrectableErrors}");
    }

    /// <summary>
    /// Generates a deterministic byte array of the specified size using a repeating
    /// pattern based on block index XOR seed for round-trip verification.
    /// </summary>
    /// <param name="sizeBytes">Size of the test data in bytes.</param>
    /// <param name="seed">Optional seed for the pattern. Default 0x42.</param>
    /// <returns>Deterministic byte array.</returns>
    public static byte[] GenerateTestData(int sizeBytes, byte seed = 0x42)
    {
        var data = new byte[sizeBytes];
        for (int i = 0; i < sizeBytes; i++)
        {
            data[i] = (byte)((i & 0xFF) ^ seed ^ ((i >> 8) & 0xFF));
        }
        return data;
    }

    /// <summary>
    /// Verifies that data retrieved from a stream matches the original byte array exactly.
    /// </summary>
    /// <param name="original">Original data that was stored.</param>
    /// <param name="retrieved">Stream retrieved from the VDE.</param>
    public static async Task VerifyDataRoundTrip(byte[] original, Stream retrieved)
    {
        using var ms = new MemoryStream();
        await retrieved.CopyToAsync(ms).ConfigureAwait(false);
        var retrievedBytes = ms.ToArray();

        Assert(retrievedBytes.Length == original.Length,
            $"Data length mismatch: expected {original.Length}, got {retrievedBytes.Length}");

        for (int i = 0; i < original.Length; i++)
        {
            if (original[i] != retrievedBytes[i])
            {
                Assert(false,
                    $"Data mismatch at byte {i}: expected 0x{original[i]:X2}, got 0x{retrievedBytes[i]:X2}");
            }
        }
    }

    /// <summary>
    /// Safely disposes a VDE and cleans up the temporary container file.
    /// </summary>
    /// <param name="vde">VDE to dispose (may be null).</param>
    /// <param name="tempPath">Temporary file path to delete (may be null).</param>
    public static async Task CleanupVde(VirtualDiskEngine? vde, string? tempPath)
    {
        if (vde != null)
        {
            try { await vde.DisposeAsync().ConfigureAwait(false); }
            catch { /* Best effort cleanup */ }
        }

        if (tempPath != null)
        {
            try { File.Delete(tempPath); }
            catch { /* Best effort cleanup */ }
        }
    }
}

/// <summary>
/// In-memory implementation of <see cref="IPhysicalBlockDevice"/> for E2E tests.
/// Stores blocks in a <see cref="ConcurrentDictionary{TKey,TValue}"/> with thread-safe
/// read/write semantics. Supports full IPhysicalBlockDevice contract including TRIM,
/// scatter-gather I/O, SMART health monitoring, and online/offline failure injection.
/// </summary>
/// <remarks>
/// SDK tests cannot reference plugin assemblies, so this provides a local implementation
/// following the same pattern as the plugin version in UltimateRAID.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 95: E2E Test In-Memory Physical Block Device")]
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
    /// Initializes a new in-memory physical block device with the specified geometry.
    /// </summary>
    /// <param name="deviceId">Unique identifier for this device.</param>
    /// <param name="blockSize">Block size in bytes. Must be a positive power of two.</param>
    /// <param name="blockCount">Total number of blocks. Must be positive.</param>
    public InMemoryPhysicalBlockDevice(string deviceId, int blockSize, long blockCount)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        if (blockSize <= 0 || (blockSize & (blockSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be a positive power of two, got {blockSize}.");
        if (blockCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockCount),
                $"Block count must be positive, got {blockCount}.");

        _blockSize = blockSize;
        _blockCount = blockCount;

        _deviceInfo = new PhysicalDeviceInfo(
            DeviceId: deviceId,
            DevicePath: $"/dev/inmem/{deviceId}",
            SerialNumber: $"INMEM-{deviceId}",
            ModelNumber: "InMemory E2E Test Device",
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
    /// Sets the online/offline state of this device for fault-injection testing.
    /// When offline, all I/O operations throw <see cref="IOException"/>.
    /// </summary>
    /// <param name="online">True to accept I/O, false to simulate device failure.</param>
    public void SetOnline(bool online) => _isOnline = online;

    /// <summary>
    /// Gets the number of blocks currently stored (written at least once).
    /// </summary>
    public int StoredBlockCount => _blocks.Count;

    /// <summary>
    /// Gets the total bytes written to this device since creation.
    /// </summary>
    public long TotalBytesWritten => Interlocked.Read(ref _totalBytesWritten);

    /// <summary>
    /// Gets the total bytes read from this device since creation.
    /// </summary>
    public long TotalBytesRead => Interlocked.Read(ref _totalBytesRead);

    /// <inheritdoc />
    public Task ReadBlockAsync(long blockNumber, Memory<byte> buffer, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ThrowIfOffline();
        ValidateBlockNumber(blockNumber);

        if (buffer.Length < _blockSize)
            throw new ArgumentException(
                $"Buffer must be at least {_blockSize} bytes, got {buffer.Length}.", nameof(buffer));

        ct.ThrowIfCancellationRequested();

        if (_blocks.TryGetValue(blockNumber, out var data))
        {
            data.AsSpan(0, _blockSize).CopyTo(buffer.Span);
        }
        else
        {
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
            throw new ArgumentException(
                $"Data must be at least {_blockSize} bytes, got {data.Length}.", nameof(data));

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
                throw new ArgumentException(
                    $"Buffer at index {i} is too small ({buffer.Length} < {_blockSize}).");

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
                throw new ArgumentException(
                    $"Data at index {i} is too small ({opData.Length} < {_blockSize}).");

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
            throw new IOException($"Device {_deviceInfo.DeviceId} is offline.");
    }

    private void ValidateBlockNumber(long blockNumber)
    {
        if (blockNumber < 0 || blockNumber >= _blockCount)
            throw new ArgumentOutOfRangeException(
                nameof(blockNumber),
                $"Block number {blockNumber} is out of range [0, {_blockCount}).");
    }
}
