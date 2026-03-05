using DataWarehouse.SDK.Contracts;
using Microsoft.Win32.SafeHandles;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.IO;

/// <summary>
/// Block device implementation for raw partition I/O without filesystem overhead.
/// Opens raw device paths (e.g., <c>/dev/nvme0n1p1</c>, <c>\\.\PhysicalDrive0</c>)
/// and performs direct I/O using platform-specific native methods.
/// </summary>
/// <remarks>
/// <para>
/// Raw partition access bypasses both the filesystem and OS page cache, providing
/// the lowest-overhead kernel I/O path. This is below <see cref="DirectFileBlockDevice"/>
/// in the performance hierarchy.
/// </para>
/// <para>
/// <strong>Safety:</strong> The constructor reads the first block and validates the
/// DWVD magic bytes (<see cref="VdeConstants.MagicBytes"/>) at the superblock location.
/// If the magic is not found and <see cref="BlockDeviceOptions.AllowNonDwvd"/> is not
/// explicitly set to <c>true</c>, an <see cref="InvalidOperationException"/> is thrown.
/// This fail-closed design prevents accidental overwrites of non-DWVD partitions.
/// </para>
/// <para>
/// <strong>Privileges:</strong> Raw partition access requires elevated privileges
/// (Administrator on Windows, root on Linux/macOS). An <see cref="UnauthorizedAccessException"/>
/// with a helpful message is thrown if privileges are insufficient.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "VOPT-82: Raw partition I/O block device for direct device access")]
public sealed class RawPartitionBlockDevice : IBatchBlockDevice
{
    private readonly SafeFileHandle _handle;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile bool _disposed;

    /// <inheritdoc/>
    public int BlockSize { get; }

    /// <inheritdoc/>
    public long BlockCount { get; }

    /// <inheritdoc/>
    public int MaxBatchSize => 64;

    /// <summary>
    /// Gets the raw device path this block device operates on.
    /// </summary>
    public string DevicePath { get; }

    /// <summary>
    /// Gets the detected physical sector size of the device in bytes.
    /// </summary>
    public int SectorSize { get; }

    /// <summary>
    /// Creates a new raw partition block device for the specified device path.
    /// </summary>
    /// <param name="devicePath">
    /// The raw device path. Examples:
    /// <list type="bullet">
    /// <item><description>Linux: <c>/dev/nvme0n1p1</c>, <c>/dev/sda</c></description></item>
    /// <item><description>Windows: <c>\\.\PhysicalDrive0</c>, <c>\\.\HarddiskVolume1</c></description></item>
    /// <item><description>macOS: <c>/dev/rdisk2</c></description></item>
    /// </list>
    /// </param>
    /// <param name="blockSize">Size of each block in bytes.</param>
    /// <param name="options">
    /// Optional configuration. Set <see cref="BlockDeviceOptions.AllowNonDwvd"/> to <c>true</c>
    /// to bypass DWVD magic validation (dangerous).
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="devicePath"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when block size is invalid.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// Thrown when the process lacks sufficient privileges. Includes helpful guidance message.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the partition does not contain a DWVD signature and
    /// <see cref="BlockDeviceOptions.AllowNonDwvd"/> is not <c>true</c>.
    /// </exception>
    public RawPartitionBlockDevice(string devicePath, int blockSize, BlockDeviceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(devicePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, VdeConstants.MinBlockSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockSize, VdeConstants.MaxBlockSize);

        if ((blockSize & (blockSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize,
                "Block size must be a power of 2.");
        }

        DevicePath = devicePath;
        BlockSize = blockSize;

        // Detect partition geometry via platform-specific ioctls
        try
        {
            (int sectorSize, long totalBytes) = RawPartitionNativeMethods.GetPartitionGeometry(devicePath);
            SectorSize = sectorSize;
            BlockCount = totalBytes / blockSize;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException(
                "Raw partition access requires elevated privileges " +
                "(root on Linux/macOS, Administrator on Windows).", ex);
        }

        // Open device handle with direct I/O flags
        SafeFileHandle? handle = null;
        try
        {
            FileOptions fileOptions;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows: FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH | Asynchronous
                fileOptions = FileOptions.Asynchronous | FileOptions.WriteThrough
                            | (FileOptions)0x20000000;
            }
            else
            {
                // Linux/macOS: WriteThrough + Asynchronous (ODirect applied by native open)
                fileOptions = FileOptions.Asynchronous | FileOptions.WriteThrough;
            }

            handle = File.OpenHandle(
                devicePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                fileOptions);

            // DWVD magic validation (fail-closed)
            bool allowNonDwvd = options?.AllowNonDwvd ?? false;
            if (!allowNonDwvd && BlockCount > 0)
            {
                ValidateDwvdMagic(handle, blockSize);
            }

            _handle = handle;
            handle = null; // Prevent disposal in finally
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException(
                "Raw partition access requires elevated privileges " +
                "(root on Linux/macOS, Administrator on Windows).", ex);
        }
        finally
        {
            handle?.Dispose();
        }
    }

    /// <inheritdoc/>
    public async Task ReadBlockAsync(long blockNumber, Memory<byte> buffer, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ValidateBlockNumber(blockNumber);

        if (buffer.Length < BlockSize)
        {
            throw new ArgumentException(
                $"Buffer must be at least {BlockSize} bytes, but was {buffer.Length} bytes.",
                nameof(buffer));
        }

        long offset = blockNumber * BlockSize;
        int bytesRead = await RandomAccess.ReadAsync(_handle, buffer[..BlockSize], offset, ct);

        if (bytesRead != BlockSize)
        {
            throw new IOException(
                $"Incomplete block read: expected {BlockSize} bytes, " +
                $"read {bytesRead} bytes at block {blockNumber} on device '{DevicePath}'.");
        }
    }

    /// <inheritdoc/>
    public async Task WriteBlockAsync(long blockNumber, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ValidateBlockNumber(blockNumber);

        if (data.Length != BlockSize)
        {
            throw new ArgumentException(
                $"Data must be exactly {BlockSize} bytes, but was {data.Length} bytes.",
                nameof(data));
        }

        long offset = blockNumber * BlockSize;

        await _writeLock.WaitAsync(ct);
        try
        {
            await RandomAccess.WriteAsync(_handle, data, offset, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<int> ReadBatchAsync(IReadOnlyList<BlockRange> requests, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(requests);

        if (requests.Count > MaxBatchSize)
        {
            throw new ArgumentException(
                $"Batch size {requests.Count} exceeds maximum {MaxBatchSize}.",
                nameof(requests));
        }

        int completed = 0;
        for (int i = 0; i < requests.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var req = requests[i];
            ValidateBlockNumber(req.BlockNumber);

            if (req.BlockNumber + req.BlockCount > BlockCount)
            {
                throw new ArgumentOutOfRangeException(nameof(requests),
                    $"Request {i} extends beyond device: blocks {req.BlockNumber}..{req.BlockNumber + req.BlockCount - 1}, " +
                    $"device has {BlockCount} blocks.");
            }

            int totalBytes = req.BlockCount * BlockSize;
            if (req.Buffer.Length < totalBytes)
            {
                throw new ArgumentException(
                    $"Request {i} buffer too small: need {totalBytes} bytes, have {req.Buffer.Length}.",
                    nameof(requests));
            }

            try
            {
                long offset = req.BlockNumber * BlockSize;
                int bytesRead = await RandomAccess.ReadAsync(_handle, req.Buffer[..totalBytes], offset, ct);

                if (bytesRead != totalBytes)
                {
                    throw new IOException(
                        $"Incomplete batch read at request {i}: expected {totalBytes} bytes, " +
                        $"read {bytesRead} bytes on device '{DevicePath}'.");
                }

                completed++;
            }
            catch (IOException)
            {
                return completed;
            }
        }

        return completed;
    }

    /// <inheritdoc/>
    public async Task<int> WriteBatchAsync(IReadOnlyList<WriteRequest> requests, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(requests);

        if (requests.Count > MaxBatchSize)
        {
            throw new ArgumentException(
                $"Batch size {requests.Count} exceeds maximum {MaxBatchSize}.",
                nameof(requests));
        }

        int completed = 0;
        await _writeLock.WaitAsync(ct);
        try
        {
            for (int i = 0; i < requests.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var req = requests[i];
                ValidateBlockNumber(req.BlockNumber);

                if (req.BlockNumber + req.BlockCount > BlockCount)
                {
                    throw new ArgumentOutOfRangeException(nameof(requests),
                        $"Request {i} extends beyond device: blocks {req.BlockNumber}..{req.BlockNumber + req.BlockCount - 1}, " +
                        $"device has {BlockCount} blocks.");
                }

                int totalBytes = req.BlockCount * BlockSize;
                if (req.Data.Length != totalBytes)
                {
                    throw new ArgumentException(
                        $"Request {i} data size mismatch: expected {totalBytes} bytes, have {req.Data.Length}.",
                        nameof(requests));
                }

                try
                {
                    long offset = req.BlockNumber * BlockSize;
                    await RandomAccess.WriteAsync(_handle, req.Data, offset, ct);
                    completed++;
                }
                catch (IOException)
                {
                    return completed;
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }

        return completed;
    }

    /// <inheritdoc/>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        await _writeLock.WaitAsync(ct);
        try
        {
            RandomAccess.FlushToDisk(_handle);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _writeLock.WaitAsync();
        try
        {
            _handle.Dispose();
        }
        finally
        {
            _writeLock.Release();
            _writeLock.Dispose();
        }
    }

    /// <summary>
    /// Validates that the first block contains DWVD magic bytes at the superblock location.
    /// Throws <see cref="InvalidOperationException"/> if magic is not found (fail-closed).
    /// </summary>
    private static void ValidateDwvdMagic(SafeFileHandle handle, int blockSize)
    {
        // Read the first block synchronously to check for DWVD magic
        byte[] buffer = new byte[blockSize];
        int bytesRead = RandomAccess.Read(handle, buffer.AsSpan(), 0);

        if (bytesRead < 4)
        {
            throw new InvalidOperationException(
                "Partition does not contain DWVD signature. " +
                "Use BlockDeviceOptions.AllowNonDwvd = true to override (DANGEROUS).");
        }

        // DWVD magic is at the start of the superblock (first 4 bytes of block 0)
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0, 4));

        if (magic != VdeConstants.MagicBytes)
        {
            throw new InvalidOperationException(
                "Partition does not contain DWVD signature. " +
                "Use BlockDeviceOptions.AllowNonDwvd = true to override (DANGEROUS).");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void ValidateBlockNumber(long blockNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(blockNumber, BlockCount);
    }
}
