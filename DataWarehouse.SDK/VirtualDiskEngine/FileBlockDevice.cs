using DataWarehouse.SDK.Contracts;
using Microsoft.Win32.SafeHandles;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine;

/// <summary>
/// File-backed implementation of <see cref="IBlockDevice"/> using the RandomAccess API.
/// Provides thread-safe block I/O operations on a regular file.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: Virtual Disk Engine (VDE-01/VDE-04)")]
public sealed class FileBlockDevice : IBlockDevice
{
    private readonly SafeFileHandle _handle;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    /// <inheritdoc/>
    public int BlockSize { get; }

    /// <inheritdoc/>
    public long BlockCount { get; }

    /// <summary>
    /// Creates a new file-backed block device.
    /// </summary>
    /// <param name="path">Path to the container file.</param>
    /// <param name="blockSize">Size of each block in bytes.</param>
    /// <param name="blockCount">Total number of blocks.</param>
    /// <param name="createNew">If true, creates a new file and pre-allocates space. If false, opens an existing file.</param>
    public FileBlockDevice(string path, int blockSize, long blockCount, bool createNew)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, VdeConstants.MinBlockSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockSize, VdeConstants.MaxBlockSize);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(blockCount, 0L);

        BlockSize = blockSize;
        BlockCount = blockCount;

        var options = FileOptions.Asynchronous | FileOptions.RandomAccess;
        var mode = createNew ? FileMode.CreateNew : FileMode.Open;
        var access = FileAccess.ReadWrite;

        _handle = File.OpenHandle(path, mode, access, FileShare.None, options);

        if (createNew)
        {
            // Pre-allocate file to full size
            long totalSize = blockSize * blockCount;
            RandomAccess.SetLength(_handle, totalSize);
        }
        else
        {
            // Validate existing file size
            long fileSize = RandomAccess.GetLength(_handle);
            long expectedSize = blockSize * blockCount;
            if (fileSize != expectedSize)
            {
                _handle.Dispose();
                throw new InvalidOperationException(
                    $"File size mismatch: expected {expectedSize} bytes (blockSize={blockSize}, blockCount={blockCount}), found {fileSize} bytes.");
            }
        }
    }

    /// <inheritdoc/>
    public async Task ReadBlockAsync(long blockNumber, Memory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(blockNumber, BlockCount);

        if (buffer.Length < BlockSize)
        {
            throw new ArgumentException($"Buffer must be at least {BlockSize} bytes, but was {buffer.Length} bytes.", nameof(buffer));
        }

        long offset = blockNumber * BlockSize;
        int bytesRead = await RandomAccess.ReadAsync(_handle, buffer[..BlockSize], offset, ct);

        if (bytesRead != BlockSize)
        {
            throw new IOException($"Incomplete block read: expected {BlockSize} bytes, read {bytesRead} bytes at block {blockNumber}.");
        }
    }

    /// <inheritdoc/>
    public async Task WriteBlockAsync(long blockNumber, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(blockNumber, BlockCount);

        if (data.Length != BlockSize)
        {
            throw new ArgumentException($"Data must be exactly {BlockSize} bytes, but was {data.Length} bytes.", nameof(data));
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
    public async Task FlushAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

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
}
