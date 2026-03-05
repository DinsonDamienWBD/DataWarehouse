using DataWarehouse.SDK.Contracts;
using Microsoft.Win32.SafeHandles;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.IO;

/// <summary>
/// Cross-platform direct file I/O block device that bypasses the OS page cache where possible.
/// On Windows, uses FILE_FLAG_NO_BUFFERING for true direct I/O. On Linux/macOS, uses
/// WriteThrough as a best-effort bypass (full ODirect requires IoUringBlockDevice on Linux
/// or KqueueBlockDevice on macOS).
/// </summary>
/// <remarks>
/// <para>
/// This implementation enforces buffer alignment in debug builds via <see cref="Debug.Assert"/>.
/// All buffers should be allocated via <see cref="GetAlignedBuffer"/> to ensure DMA compatibility.
/// </para>
/// <para>
/// Batch operations (<see cref="ReadBatchAsync"/> and <see cref="WriteBatchAsync"/>) process
/// requests sequentially and return partial success counts on failure, enabling callers to
/// retry only the failed operations.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "VOPT-79: Cross-platform direct file I/O block device")]
public sealed class DirectFileBlockDevice : IDirectBlockDevice
{
    /// <summary>
    /// FILE_FLAG_NO_BUFFERING raw value (0x20000000). This flag disables the OS page cache
    /// on Windows, forcing all I/O to go directly to/from the physical disk. Cast to FileOptions
    /// because .NET does not expose this flag directly.
    /// </summary>
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    private readonly SafeFileHandle _handle;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile bool _disposed;

    /// <inheritdoc/>
    public int BlockSize { get; }

    /// <inheritdoc/>
    public long BlockCount { get; }

    /// <inheritdoc/>
    public bool IsDirectIo { get; }

    /// <inheritdoc/>
    public int AlignmentRequirement { get; }

    /// <inheritdoc/>
    public int MaxBatchSize => 64;

    /// <summary>
    /// Creates a new direct file block device with platform-optimized I/O flags.
    /// </summary>
    /// <param name="path">Path to the container file.</param>
    /// <param name="blockSize">Size of each block in bytes.</param>
    /// <param name="blockCount">Total number of blocks.</param>
    /// <param name="createNew">If <c>true</c>, creates a new file and pre-allocates space.</param>
    /// <param name="alignmentRequirement">
    /// Buffer alignment in bytes (default 4096). Must be a power of 2.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when block size, block count, or alignment are invalid.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when opening an existing file whose size does not match expected dimensions.
    /// </exception>
    public DirectFileBlockDevice(string path, int blockSize, long blockCount, bool createNew,
        int alignmentRequirement = 4096)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, VdeConstants.MinBlockSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockSize, VdeConstants.MaxBlockSize);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(blockCount, 0L);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(alignmentRequirement, 0);

        if ((alignmentRequirement & (alignmentRequirement - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignmentRequirement), alignmentRequirement,
                "Alignment requirement must be a power of 2.");
        }

        BlockSize = blockSize;
        BlockCount = blockCount;
        AlignmentRequirement = alignmentRequirement;

        var mode = createNew ? FileMode.CreateNew : FileMode.Open;
        var access = FileAccess.ReadWrite;

        FileOptions options;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows: FILE_FLAG_NO_BUFFERING bypasses the OS page cache entirely.
            // Combined with WriteThrough for durability and Asynchronous for async I/O.
            options = FileOptions.Asynchronous | FileOptions.RandomAccess
                    | FileOptions.WriteThrough | NoBuffering;
            IsDirectIo = true;
        }
        else
        {
            // Linux/macOS: WriteThrough provides best-effort cache bypass.
            // Full ODirect on Linux requires IoUringBlockDevice (plan 87-57).
            // Full F_NOCACHE on macOS requires KqueueBlockDevice (plan 87-60).
            options = FileOptions.Asynchronous | FileOptions.RandomAccess
                    | FileOptions.WriteThrough;
            IsDirectIo = false;
        }

        _handle = File.OpenHandle(path, mode, access, FileShare.None, options);

        if (createNew)
        {
            long totalSize = blockSize * blockCount;
            RandomAccess.SetLength(_handle, totalSize);
        }
        else
        {
            long fileSize = RandomAccess.GetLength(_handle);
            long expectedSize = blockSize * blockCount;
            if (fileSize != expectedSize)
            {
                _handle.Dispose();
                throw new InvalidOperationException(
                    $"File size mismatch: expected {expectedSize} bytes " +
                    $"(blockSize={blockSize}, blockCount={blockCount}), found {fileSize} bytes.");
            }
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

        Debug.Assert(buffer.Length % AlignmentRequirement == 0,
            $"Buffer length {buffer.Length} is not aligned to {AlignmentRequirement} bytes.");

        long offset = blockNumber * BlockSize;
        int bytesRead = await RandomAccess.ReadAsync(_handle, buffer[..BlockSize], offset, ct);

        if (bytesRead != BlockSize)
        {
            throw new IOException(
                $"Incomplete block read: expected {BlockSize} bytes, " +
                $"read {bytesRead} bytes at block {blockNumber}.");
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

        Debug.Assert(data.Length % AlignmentRequirement == 0,
            $"Data length {data.Length} is not aligned to {AlignmentRequirement} bytes.");

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
                        $"read {bytesRead} bytes.");
                }

                completed++;
            }
            catch (IOException)
            {
                // Partial success: return count completed so far
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
                    // Partial success: return count completed so far
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
    public IMemoryOwner<byte> GetAlignedBuffer(int byteCount)
    {
        ThrowIfDisposed();
        return new AlignedMemoryOwner(byteCount, AlignmentRequirement);
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
