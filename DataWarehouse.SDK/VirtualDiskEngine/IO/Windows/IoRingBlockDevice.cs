using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using Microsoft.Win32.SafeHandles;

namespace DataWarehouse.SDK.VirtualDiskEngine.IO.Windows;

/// <summary>
/// Windows 11+ block device using the IoRing API for submission-queue / completion-queue
/// (SQE/CQE) based block I/O, similar to Linux io_uring.
/// </summary>
/// <remarks>
/// <para>
/// IoRing provides ~20% throughput improvement over IOCP on Windows 11 by reducing
/// kernel transitions: multiple I/O operations are submitted in a single syscall via
/// <see cref="IoRingNativeMethods.SubmitIoRing"/>, and completions are polled from
/// the CQ without additional syscalls.
/// </para>
/// <para>
/// The write API (<c>BuildIoRingWriteFile</c>) was added in later Windows 11 builds.
/// If writes are not supported, this implementation falls back to <see cref="RandomAccess.WriteAsync"/>
/// while still using IoRing for reads.
/// </para>
/// <para>
/// Thread safety: All IoRing operations are serialized via a lock because the IoRing
/// handle is not thread-safe. Concurrent callers queue behind the lock, but the
/// batched submission model ensures high throughput despite serialization.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
[SdkCompatibility("6.0.0", Notes = "VOPT-80: IoRing-based block device for Windows 11+")]
public sealed class IoRingBlockDevice : IBatchBlockDevice
{
    private readonly SafeFileHandle _fileHandle;
    private readonly nint _rawFileHandle; // Cached via DangerousAddRef for safe IoRing P/Invoke usage
    private readonly int _blockSize;
    private readonly long _blockCount;
    private readonly int _ringSize;
    private readonly nint _ioRingHandle;
    private readonly bool _writeSupported;
    private readonly SemaphoreSlim _ringLock = new(1, 1);
    private bool _handleRefAdded;

    // Pre-allocated pinned buffers for zero-copy I/O
    private readonly nint[] _bufferPointers;
    private readonly GCHandle[] _pinnedHandles;
    private readonly SemaphoreSlim _bufferSemaphore;
    private readonly System.Collections.Concurrent.ConcurrentQueue<int> _bufferPool = new();

    private volatile int _disposed;
    private uint _nextUserData;

    /// <inheritdoc />
    public int BlockSize => _blockSize;

    /// <inheritdoc />
    public long BlockCount => _blockCount;

    /// <inheritdoc />
    public int MaxBatchSize => _ringSize;

    /// <summary>
    /// Creates a new IoRing-backed block device.
    /// </summary>
    /// <param name="fileHandle">
    /// An already-opened file handle (should be opened with
    /// <c>FILE_FLAG_NO_BUFFERING | FILE_FLAG_OVERLAPPED</c> for optimal performance).
    /// The caller retains ownership; dispose the handle after disposing this device.
    /// </param>
    /// <param name="blockSize">Size of each block in bytes.</param>
    /// <param name="blockCount">Total number of blocks.</param>
    /// <param name="ringSize">Number of SQ/CQ entries (default 64).</param>
    /// <exception cref="PlatformNotSupportedException">
    /// Thrown when the IoRing API is not available on the current Windows version.
    /// Use <see cref="IsSupported"/> to check before constructing.
    /// </exception>
    public IoRingBlockDevice(
        SafeFileHandle fileHandle,
        int blockSize,
        long blockCount,
        int ringSize = 64)
    {
        ArgumentNullException.ThrowIfNull(fileHandle);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 512);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(blockCount, 0L);
        ArgumentOutOfRangeException.ThrowIfLessThan(ringSize, 1);

        if (!IoRingNativeMethods.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "IoRing API is not available on this Windows version. " +
                "Use WindowsOverlappedBlockDevice for IOCP-based I/O on Windows 10+.");
        }

        _fileHandle = fileHandle;
        _blockSize = blockSize;
        _blockCount = blockCount;
        _ringSize = ringSize;
        _writeSupported = IoRingNativeMethods.IsWriteSupported;

        // Cache the raw handle via DangerousAddRef to avoid repeated DangerousGetHandle calls.
        // This adds a reference count so the handle won't be released while we hold it.
        // The single DangerousGetHandle call here is guarded by DangerousAddRef (ensures handle
        // validity) and DangerousRelease in DisposeAsync -- this is the recommended pattern for
        // P/Invoke-heavy code that needs the raw OS handle value.
        _fileHandle.DangerousAddRef(ref _handleRefAdded);
#pragma warning disable S3869 // SafeHandle.DangerousGetHandle -- guarded by DangerousAddRef/DangerousRelease lifecycle
        _rawFileHandle = _fileHandle.DangerousGetHandle();
#pragma warning restore S3869

        // Create the IoRing
        var flags = new IoRingNativeMethods.IoringCreateFlags
        {
            Required = IoRingNativeMethods.IoringCreateFlagsNone,
            Advisory = IoRingNativeMethods.IoringCreateFlagsNone
        };

        int hr = IoRingNativeMethods.CreateIoRing(
            IoRingNativeMethods.IoringVersion3,
            flags,
            (uint)ringSize,
            (uint)ringSize,
            out _ioRingHandle);

        if (hr != IoRingNativeMethods.SOk || _ioRingHandle == nint.Zero)
        {
            throw new PlatformNotSupportedException(
                $"CreateIoRing failed with HRESULT 0x{hr:X8}. " +
                "Use WindowsOverlappedBlockDevice for IOCP-based I/O on Windows 10+.");
        }

        // Pre-allocate pinned buffers for I/O operations
        _bufferPointers = new nint[ringSize];
        _pinnedHandles = new GCHandle[ringSize];
        _bufferSemaphore = new SemaphoreSlim(ringSize, ringSize);

        for (int i = 0; i < ringSize; i++)
        {
            byte[] buffer = GC.AllocateArray<byte>(blockSize, pinned: true);
            _pinnedHandles[i] = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            _bufferPointers[i] = _pinnedHandles[i].AddrOfPinnedObject();
            _bufferPool.Enqueue(i);
        }
    }

    /// <summary>
    /// Gets whether the IoRing API is available on the current platform.
    /// </summary>
    public static bool IsSupported => IoRingNativeMethods.IsSupported;

    /// <summary>
    /// Gets whether IoRing write operations are supported on the current platform.
    /// When <c>false</c>, writes fall back to <see cref="RandomAccess.WriteAsync"/>.
    /// </summary>
    public bool WriteSupported => _writeSupported;

    /// <inheritdoc />
    public async Task ReadBlockAsync(long blockNumber, Memory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ValidateBlockNumber(blockNumber);

        if (buffer.Length < _blockSize)
        {
            throw new ArgumentException(
                $"Buffer must be at least {_blockSize} bytes, but was {buffer.Length} bytes.",
                nameof(buffer));
        }

        int bufIndex = await AcquireBufferAsync(ct).ConfigureAwait(false);
        try
        {
            uint userData = Interlocked.Increment(ref _nextUserData);

            await _ringLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var handleRef = IoRingNativeMethods.IoringHandleRef.FromHandle(
                    _rawFileHandle);
                var bufferRef = IoRingNativeMethods.IoringBufferRef.FromAddress(
                    _bufferPointers[bufIndex]);

                int hr = IoRingNativeMethods.BuildIoRingReadFile(
                    _ioRingHandle,
                    handleRef,
                    bufferRef,
                    (uint)_blockSize,
                    (ulong)(blockNumber * _blockSize),
                    userData,
                    0);

                if (hr != IoRingNativeMethods.SOk)
                {
                    throw new IOException($"BuildIoRingReadFile failed with HRESULT 0x{hr:X8}.");
                }

                hr = IoRingNativeMethods.SubmitIoRing(
                    _ioRingHandle, 1, OverlappedNativeMethods.Infinite, out _);

                if (hr != IoRingNativeMethods.SOk)
                {
                    throw new IOException($"SubmitIoRing failed with HRESULT 0x{hr:X8}.");
                }

                // Pop the completion
                hr = IoRingNativeMethods.PopIoRingCompletion(_ioRingHandle, out var cqe);
                if (hr != IoRingNativeMethods.SOk)
                {
                    throw new IOException($"PopIoRingCompletion failed with HRESULT 0x{hr:X8}.");
                }

                if (cqe.ResultCode < 0)
                {
                    throw new IOException(
                        $"IoRing read failed at block {blockNumber} with result 0x{cqe.ResultCode:X8}.");
                }
            }
            finally
            {
                _ringLock.Release();
            }

            // Copy from pinned buffer to caller's buffer
            unsafe
            {
                new Span<byte>((void*)_bufferPointers[bufIndex], _blockSize)
                    .CopyTo(buffer.Span);
            }
        }
        finally
        {
            ReleaseBuffer(bufIndex);
        }
    }

    /// <inheritdoc />
    public async Task WriteBlockAsync(long blockNumber, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ValidateBlockNumber(blockNumber);

        if (data.Length != _blockSize)
        {
            throw new ArgumentException(
                $"Data must be exactly {_blockSize} bytes, but was {data.Length} bytes.",
                nameof(data));
        }

        if (!_writeSupported)
        {
            // Fallback: use RandomAccess for writes on early Windows 11 builds
            long offset = blockNumber * _blockSize;
            await RandomAccess.WriteAsync(_fileHandle, data, offset, ct).ConfigureAwait(false);
            return;
        }

        int bufIndex = await AcquireBufferAsync(ct).ConfigureAwait(false);
        try
        {
            // Copy data into the pinned buffer
            unsafe
            {
                data.Span.CopyTo(new Span<byte>((void*)_bufferPointers[bufIndex], _blockSize));
            }

            uint userData = Interlocked.Increment(ref _nextUserData);

            await _ringLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var handleRef = IoRingNativeMethods.IoringHandleRef.FromHandle(
                    _rawFileHandle);
                var bufferRef = IoRingNativeMethods.IoringBufferRef.FromAddress(
                    _bufferPointers[bufIndex]);

                int hr = IoRingNativeMethods.BuildIoRingWriteFile(
                    _ioRingHandle,
                    handleRef,
                    bufferRef,
                    (uint)_blockSize,
                    (ulong)(blockNumber * _blockSize),
                    userData,
                    0);

                if (hr != IoRingNativeMethods.SOk)
                {
                    throw new IOException($"BuildIoRingWriteFile failed with HRESULT 0x{hr:X8}.");
                }

                hr = IoRingNativeMethods.SubmitIoRing(
                    _ioRingHandle, 1, OverlappedNativeMethods.Infinite, out _);

                if (hr != IoRingNativeMethods.SOk)
                {
                    throw new IOException($"SubmitIoRing failed with HRESULT 0x{hr:X8}.");
                }

                hr = IoRingNativeMethods.PopIoRingCompletion(_ioRingHandle, out var cqe);
                if (hr != IoRingNativeMethods.SOk)
                {
                    throw new IOException($"PopIoRingCompletion failed with HRESULT 0x{hr:X8}.");
                }

                if (cqe.ResultCode < 0)
                {
                    throw new IOException(
                        $"IoRing write failed at block {blockNumber} with result 0x{cqe.ResultCode:X8}.");
                }
            }
            finally
            {
                _ringLock.Release();
            }
        }
        finally
        {
            ReleaseBuffer(bufIndex);
        }
    }

    /// <inheritdoc />
    public async Task<int> ReadBatchAsync(IReadOnlyList<BlockRange> requests, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(requests);

        if (requests.Count > _ringSize)
        {
            throw new ArgumentException(
                $"Batch size {requests.Count} exceeds MaxBatchSize {_ringSize}.",
                nameof(requests));
        }

        if (requests.Count == 0) return 0;

        // For multi-block ranges, flatten to individual block operations
        var flatOps = FlattenReadRequests(requests);

        // Acquire buffers for all operations
        int[] bufIndices = new int[flatOps.Count];
        for (int i = 0; i < flatOps.Count; i++)
        {
            bufIndices[i] = await AcquireBufferAsync(ct).ConfigureAwait(false);
        }

        int completedRequests = 0;
        try
        {
            await _ringLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Build all SQEs
                for (int i = 0; i < flatOps.Count; i++)
                {
                    var (blockNum, _, _) = flatOps[i];
                    uint userData = Interlocked.Increment(ref _nextUserData);

                    var handleRef = IoRingNativeMethods.IoringHandleRef.FromHandle(
                        _rawFileHandle);
                    var bufferRef = IoRingNativeMethods.IoringBufferRef.FromAddress(
                        _bufferPointers[bufIndices[i]]);

                    int hr = IoRingNativeMethods.BuildIoRingReadFile(
                        _ioRingHandle,
                        handleRef,
                        bufferRef,
                        (uint)_blockSize,
                        (ulong)(blockNum * _blockSize),
                        userData,
                        0);

                    if (hr != IoRingNativeMethods.SOk)
                    {
                        throw new IOException($"BuildIoRingReadFile failed with HRESULT 0x{hr:X8} at op {i}.");
                    }
                }

                // Submit all and wait for all completions
                int submitHr = IoRingNativeMethods.SubmitIoRing(
                    _ioRingHandle,
                    (uint)flatOps.Count,
                    OverlappedNativeMethods.Infinite,
                    out uint submitted);

                if (submitHr != IoRingNativeMethods.SOk)
                {
                    throw new IOException($"SubmitIoRing batch failed with HRESULT 0x{submitHr:X8}.");
                }

                // Pop all completions
                bool allSucceeded = true;
                for (int i = 0; i < flatOps.Count; i++)
                {
                    int popHr = IoRingNativeMethods.PopIoRingCompletion(_ioRingHandle, out var cqe);
                    if (popHr != IoRingNativeMethods.SOk || cqe.ResultCode < 0)
                    {
                        allSucceeded = false;
                        break;
                    }
                }

                if (!allSucceeded)
                {
                    return 0; // Partial completion tracking is complex with flattened ops
                }
            }
            finally
            {
                _ringLock.Release();
            }

            // Copy results from pinned buffers to caller buffers
            int flatIdx = 0;
            for (int reqIdx = 0; reqIdx < requests.Count; reqIdx++)
            {
                var req = requests[reqIdx];
                for (int blk = 0; blk < req.BlockCount; blk++)
                {
                    unsafe
                    {
                        int bufferOffset = blk * _blockSize;
                        new Span<byte>((void*)_bufferPointers[bufIndices[flatIdx]], _blockSize)
                            .CopyTo(req.Buffer.Span.Slice(bufferOffset, _blockSize));
                    }
                    flatIdx++;
                }
                completedRequests++;
            }
        }
        finally
        {
            for (int i = 0; i < bufIndices.Length; i++)
            {
                ReleaseBuffer(bufIndices[i]);
            }
        }

        return completedRequests;
    }

    /// <inheritdoc />
    public async Task<int> WriteBatchAsync(IReadOnlyList<WriteRequest> requests, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(requests);

        if (requests.Count > _ringSize)
        {
            throw new ArgumentException(
                $"Batch size {requests.Count} exceeds MaxBatchSize {_ringSize}.",
                nameof(requests));
        }

        if (requests.Count == 0) return 0;

        if (!_writeSupported)
        {
            // Fallback: use RandomAccess for all writes
            return await WriteBatchFallbackAsync(requests, ct).ConfigureAwait(false);
        }

        var flatOps = FlattenWriteRequests(requests);

        // Acquire buffers and copy data
        int[] bufIndices = new int[flatOps.Count];
        for (int i = 0; i < flatOps.Count; i++)
        {
            bufIndices[i] = await AcquireBufferAsync(ct).ConfigureAwait(false);

            // Copy source data into pinned buffer
            var (_, dataSlice, _) = flatOps[i];
            unsafe
            {
                dataSlice.Span.CopyTo(
                    new Span<byte>((void*)_bufferPointers[bufIndices[i]], _blockSize));
            }
        }

        int completedRequests = 0;
        try
        {
            await _ringLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                for (int i = 0; i < flatOps.Count; i++)
                {
                    var (blockNum, _, _) = flatOps[i];
                    uint userData = Interlocked.Increment(ref _nextUserData);

                    var handleRef = IoRingNativeMethods.IoringHandleRef.FromHandle(
                        _rawFileHandle);
                    var bufferRef = IoRingNativeMethods.IoringBufferRef.FromAddress(
                        _bufferPointers[bufIndices[i]]);

                    int hr = IoRingNativeMethods.BuildIoRingWriteFile(
                        _ioRingHandle,
                        handleRef,
                        bufferRef,
                        (uint)_blockSize,
                        (ulong)(blockNum * _blockSize),
                        userData,
                        0);

                    if (hr != IoRingNativeMethods.SOk)
                    {
                        throw new IOException($"BuildIoRingWriteFile failed with HRESULT 0x{hr:X8} at op {i}.");
                    }
                }

                int submitHr = IoRingNativeMethods.SubmitIoRing(
                    _ioRingHandle,
                    (uint)flatOps.Count,
                    OverlappedNativeMethods.Infinite,
                    out _);

                if (submitHr != IoRingNativeMethods.SOk)
                {
                    throw new IOException($"SubmitIoRing write batch failed with HRESULT 0x{submitHr:X8}.");
                }

                bool allSucceeded = true;
                for (int i = 0; i < flatOps.Count; i++)
                {
                    int popHr = IoRingNativeMethods.PopIoRingCompletion(_ioRingHandle, out var cqe);
                    if (popHr != IoRingNativeMethods.SOk || cqe.ResultCode < 0)
                    {
                        allSucceeded = false;
                        break;
                    }
                }

                completedRequests = allSucceeded ? requests.Count : 0;
            }
            finally
            {
                _ringLock.Release();
            }
        }
        finally
        {
            for (int i = 0; i < bufIndices.Length; i++)
            {
                ReleaseBuffer(bufIndices[i]);
            }
        }

        return completedRequests;
    }

    /// <inheritdoc />
    public Task FlushAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        // Flush via the file handle since IoRing doesn't have a dedicated flush SQE
        if (!OverlappedNativeMethods.FlushFileBuffers(_fileHandle))
        {
            int error = Marshal.GetLastPInvokeError();
            throw new IOException($"FlushFileBuffers failed with Win32 error {error}.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        // Close the IoRing handle
        if (_ioRingHandle != nint.Zero)
        {
            IoRingNativeMethods.CloseIoRing(_ioRingHandle);
        }

        // Release pinned buffers
        for (int i = 0; i < _pinnedHandles.Length; i++)
        {
            if (_pinnedHandles[i].IsAllocated)
            {
                _pinnedHandles[i].Free();
            }
        }

        _ringLock.Dispose();
        _bufferSemaphore.Dispose();

        // Release the reference added via DangerousAddRef
        if (_handleRefAdded)
        {
            _fileHandle.DangerousRelease();
        }

        // Note: _fileHandle is NOT disposed here -- caller owns it
        return ValueTask.CompletedTask;
    }

    // ── Private helpers ─────────────────────────────────────────────────

    private void ValidateBlockNumber(long blockNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(blockNumber, _blockCount);
    }

    private async Task<int> AcquireBufferAsync(CancellationToken ct)
    {
        await _bufferSemaphore.WaitAsync(ct).ConfigureAwait(false);
        if (_bufferPool.TryDequeue(out int index))
        {
            return index;
        }
        _bufferSemaphore.Release();
        throw new InvalidOperationException("Buffer pool exhausted despite semaphore.");
    }

    private void ReleaseBuffer(int index)
    {
        _bufferPool.Enqueue(index);
        _bufferSemaphore.Release();
    }

    /// <summary>
    /// Flattens multi-block read requests into individual block operations.
    /// Returns list of (blockNumber, bufferSliceNotUsedHere, requestIndex).
    /// </summary>
    private List<(long BlockNumber, Memory<byte> BufferSlice, int RequestIndex)> FlattenReadRequests(
        IReadOnlyList<BlockRange> requests)
    {
        var ops = new List<(long, Memory<byte>, int)>();
        for (int i = 0; i < requests.Count; i++)
        {
            var req = requests[i];
            for (int blk = 0; blk < req.BlockCount; blk++)
            {
                long blockNum = req.BlockNumber + blk;
                ValidateBlockNumber(blockNum);
                ops.Add((blockNum, req.Buffer.Slice(blk * _blockSize, _blockSize), i));
            }
        }
        return ops;
    }

    /// <summary>
    /// Flattens multi-block write requests into individual block operations.
    /// </summary>
    private List<(long BlockNumber, ReadOnlyMemory<byte> DataSlice, int RequestIndex)> FlattenWriteRequests(
        IReadOnlyList<WriteRequest> requests)
    {
        var ops = new List<(long, ReadOnlyMemory<byte>, int)>();
        for (int i = 0; i < requests.Count; i++)
        {
            var req = requests[i];
            for (int blk = 0; blk < req.BlockCount; blk++)
            {
                long blockNum = req.BlockNumber + blk;
                ValidateBlockNumber(blockNum);
                ops.Add((blockNum, req.Data.Slice(blk * _blockSize, _blockSize), i));
            }
        }
        return ops;
    }

    /// <summary>
    /// Fallback write batch using <see cref="RandomAccess.WriteAsync"/> when IoRing writes
    /// are not available (early Windows 11 builds).
    /// </summary>
    private async Task<int> WriteBatchFallbackAsync(
        IReadOnlyList<WriteRequest> requests, CancellationToken ct)
    {
        int completed = 0;
        var tasks = new Task[requests.Count];

        for (int i = 0; i < requests.Count; i++)
        {
            int index = i;
            var req = requests[i];
            tasks[i] = Task.Run(async () =>
            {
                for (int blk = 0; blk < req.BlockCount; blk++)
                {
                    long blockNum = req.BlockNumber + blk;
                    ValidateBlockNumber(blockNum);
                    long offset = blockNum * _blockSize;
                    var slice = req.Data.Slice(blk * _blockSize, _blockSize);
                    await RandomAccess.WriteAsync(_fileHandle, slice, offset, ct).ConfigureAwait(false);
                }
            }, ct);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // All tasks completed (Task.WhenAll throws on any failure)
        completed = requests.Count;
        return completed;
    }
}
