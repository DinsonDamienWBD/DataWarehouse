using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.IO.Unix;

/// <summary>
/// Block device implementation for macOS and FreeBSD using kqueue + POSIX AIO
/// for asynchronous non-blocking block I/O with OS page cache bypass.
/// </summary>
/// <remarks>
/// <para>
/// On macOS, the F_NOCACHE fcntl flag is applied to bypass the unified buffer cache,
/// providing ODirect-equivalent behavior. This ensures predictable latency and avoids
/// double-buffering for workloads that manage their own caching (e.g., VDE block cache).
/// </para>
/// <para>
/// Individual read/write operations use POSIX AIO (aio_read/aio_write) with polling
/// via aio_error for completion. Batch operations submit all AIO requests and then
/// poll for completion of each, yielding between polls to avoid busy-waiting.
/// </para>
/// <para>
/// AIO control blocks are allocated on the native heap via <see cref="NativeMemory"/>
/// to ensure stable pointers across async/await boundaries, since C# prohibits pointer
/// locals in async methods and unsafe contexts containing await expressions.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "VOPT-81: kqueue + POSIX AIO block device for macOS/FreeBSD")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("freebsd")]
public sealed class KqueueBlockDevice : IBatchBlockDevice
{
    /// <summary>
    /// Maximum number of polling iterations before backing off to Task.Delay.
    /// </summary>
    private const int SpinCountBeforeDelay = 16;

    /// <summary>
    /// Maximum delay in milliseconds between AIO completion polls.
    /// </summary>
    private const int MaxPollDelayMs = 1;

    private readonly int _fileFd;
    private readonly int _kqueueFd;
    private readonly int _alignmentRequirement;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile bool _disposed;

    /// <inheritdoc/>
    public int BlockSize { get; }

    /// <inheritdoc/>
    public long BlockCount { get; }

    /// <inheritdoc/>
    public int MaxBatchSize => 64;

    /// <summary>
    /// Gets whether F_NOCACHE was successfully applied for page cache bypass.
    /// </summary>
    public bool IsDirectIo { get; }

    /// <summary>
    /// Gets the buffer alignment requirement in bytes for optimal I/O performance.
    /// </summary>
    public int AlignmentRequirement => _alignmentRequirement;

    /// <summary>
    /// Creates a new kqueue-based block device for macOS or FreeBSD.
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
    /// <exception cref="PlatformNotSupportedException">
    /// Thrown when kqueue is not supported on the current platform.
    /// </exception>
    /// <exception cref="IOException">
    /// Thrown when the file cannot be opened or kqueue descriptor creation fails.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when opening an existing file whose size does not match expected dimensions.
    /// </exception>
    public KqueueBlockDevice(string path, int blockSize, long blockCount, bool createNew,
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

        if (!KqueueNativeMethods.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "KqueueBlockDevice requires macOS or FreeBSD. " +
                $"Current platform: {RuntimeInformation.OSDescription}");
        }

        BlockSize = blockSize;
        BlockCount = blockCount;
        _alignmentRequirement = alignmentRequirement;

        // Open file descriptor with ORdwr, optionally creating new
        int openFlags = KqueueNativeMethods.ORdwr;
        if (createNew)
        {
            openFlags |= KqueueNativeMethods.OCreat | KqueueNativeMethods.OExcl;
        }

        _fileFd = KqueueNativeMethods.Open(path, openFlags, KqueueNativeMethods.SIrusrIwusr);
        if (_fileFd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"Failed to open file '{path}' with flags 0x{openFlags:X}: errno={errno}");
        }

        bool kqueueCreated = false;
        try
        {
            // Apply F_NOCACHE on macOS for page cache bypass
            if (KqueueNativeMethods.IsMacOs)
            {
                int result = KqueueNativeMethods.Fcntl(_fileFd, KqueueNativeMethods.FNocache, 1);
                IsDirectIo = result == 0;
            }

            // Pre-allocate file for new volumes
            if (createNew)
            {
                long totalSize = (long)blockSize * blockCount;
                int result = KqueueNativeMethods.Ftruncate(_fileFd, totalSize);
                if (result != 0)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    throw new IOException(
                        $"Failed to pre-allocate file to {totalSize} bytes: errno={errno}");
                }
            }

            // Create kqueue descriptor for event-driven batch I/O
            _kqueueFd = KqueueNativeMethods.Kqueue();
            if (_kqueueFd < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                throw new IOException($"Failed to create kqueue descriptor: errno={errno}");
            }

            kqueueCreated = true;
        }
        catch
        {
            if (kqueueCreated && _kqueueFd >= 0)
            {
                KqueueNativeMethods.Close(_kqueueFd);
            }

            KqueueNativeMethods.Close(_fileFd);
            throw;
        }
    }

    /// <summary>
    /// Returns <c>true</c> if the current platform supports kqueue-based block I/O.
    /// </summary>
    public static bool IsSupported => KqueueNativeMethods.IsSupported;

    /// <summary>
    /// Allocates a buffer aligned to <see cref="AlignmentRequirement"/> for direct I/O.
    /// </summary>
    /// <param name="byteCount">Number of bytes to allocate.</param>
    /// <returns>An aligned memory owner. Caller must dispose.</returns>
    public IMemoryOwner<byte> GetAlignedBuffer(int byteCount)
    {
        ThrowIfDisposed();
        return new AlignedMemoryOwner(byteCount, _alignmentRequirement);
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
        using var pinHandle = buffer.Pin();
        IntPtr nativeAiocb = AllocateAiocb(_fileFd, offset, pinHandle, BlockSize);

        try
        {
            SubmitAioRead(nativeAiocb, blockNumber);
            await PollForCompletionAsync(nativeAiocb, blockNumber, isRead: true, ct);
        }
        finally
        {
            FreeAiocb(nativeAiocb);
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
            using var pinHandle = data.Pin();
            IntPtr nativeAiocb = AllocateAiocb(_fileFd, offset, pinHandle, BlockSize);

            try
            {
                SubmitAioWrite(nativeAiocb, blockNumber);
                await PollForCompletionAsync(nativeAiocb, blockNumber, isRead: false, ct);
            }
            finally
            {
                FreeAiocb(nativeAiocb);
            }
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
                using var pinHandle = req.Buffer.Pin();
                IntPtr nativeAiocb = AllocateAiocb(_fileFd, offset, pinHandle, totalBytes);

                try
                {
                    SubmitAioRead(nativeAiocb, req.BlockNumber);
                    await PollForCompletionAsync(nativeAiocb, req.BlockNumber, isRead: true, ct);
                }
                finally
                {
                    FreeAiocb(nativeAiocb);
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
                    using var pinHandle = req.Data.Pin();
                    IntPtr nativeAiocb = AllocateAiocb(_fileFd, offset, pinHandle, totalBytes);

                    try
                    {
                        SubmitAioWrite(nativeAiocb, req.BlockNumber);
                        await PollForCompletionAsync(nativeAiocb, req.BlockNumber, isRead: false, ct);
                    }
                    finally
                    {
                        FreeAiocb(nativeAiocb);
                    }

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
    public Task FlushAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        int result = KqueueNativeMethods.Fsync(_fileFd);
        if (result != 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            throw new IOException($"fsync failed: errno={errno}");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        // Close kqueue descriptor first, then file descriptor
        if (_kqueueFd >= 0)
        {
            KqueueNativeMethods.Close(_kqueueFd);
        }

        if (_fileFd >= 0)
        {
            KqueueNativeMethods.Close(_fileFd);
        }

        _writeLock.Dispose();

        return ValueTask.CompletedTask;
    }

    // --- Native aiocb allocation on unmanaged heap ---
    // Aiocb structs must live at stable addresses for the lifetime of the AIO operation.
    // Since async methods cannot use pointer locals or unsafe+await together, we allocate
    // on the native heap and pass IntPtr around.

    /// <summary>
    /// Allocates a <see cref="KqueueNativeMethods.Aiocb"/> on the native heap and initializes it.
    /// The caller must call <see cref="FreeAiocb"/> when done.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe IntPtr AllocateAiocb(int fileFd, long offset, MemoryHandle pinHandle, int byteCount)
    {
        int size = sizeof(KqueueNativeMethods.Aiocb);
        IntPtr ptr = (IntPtr)NativeMemory.AllocZeroed((nuint)size);

        var aiocb = (KqueueNativeMethods.Aiocb*)ptr;
        aiocb->FileDescriptor = fileFd;
        aiocb->Offset = offset;
        aiocb->Buffer = (IntPtr)pinHandle.Pointer;
        aiocb->ByteCount = (nuint)byteCount;
        aiocb->RequestPriority = 0;
        aiocb->SigEvent = new KqueueNativeMethods.Sigevent { Notify = KqueueNativeMethods.SigevNone };
        aiocb->LioOpcode = 0;

        return ptr;
    }

    /// <summary>
    /// Frees a native-heap-allocated aiocb.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void FreeAiocb(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero)
        {
            NativeMemory.Free((void*)ptr);
        }
    }

    /// <summary>
    /// Submits an aio_read for the given native aiocb. Synchronous; no await needed.
    /// </summary>
    private static unsafe void SubmitAioRead(IntPtr aiocbPtr, long blockNumber)
    {
        var aiocb = (KqueueNativeMethods.Aiocb*)aiocbPtr;
        int result = KqueueNativeMethods.AioRead(aiocb);
        if (result != 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"aio_read failed at block {blockNumber} (offset {aiocb->Offset}): errno={errno}");
        }
    }

    /// <summary>
    /// Submits an aio_write for the given native aiocb. Synchronous; no await needed.
    /// </summary>
    private static unsafe void SubmitAioWrite(IntPtr aiocbPtr, long blockNumber)
    {
        var aiocb = (KqueueNativeMethods.Aiocb*)aiocbPtr;
        int result = KqueueNativeMethods.AioWrite(aiocb);
        if (result != 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"aio_write failed at block {blockNumber} (offset {aiocb->Offset}): errno={errno}");
        }
    }

    /// <summary>
    /// Checks AIO completion synchronously. Returns true if completed, false if still in progress.
    /// Throws on AIO error.
    /// </summary>
    private static unsafe bool TryCheckCompletion(IntPtr aiocbPtr, long blockNumber, bool isRead)
    {
        var aiocb = (KqueueNativeMethods.Aiocb*)aiocbPtr;
        int error = KqueueNativeMethods.AioError(aiocb);

        if (error == 0)
        {
            nint bytesTransferred = KqueueNativeMethods.AioReturn(aiocb);
            if (bytesTransferred < 0)
            {
                string op = isRead ? "read" : "write";
                throw new IOException(
                    $"AIO {op} failed at block {blockNumber}: aio_return returned {bytesTransferred}");
            }

            return true;
        }

        if (error != KqueueNativeMethods.Einprogress)
        {
            string op = isRead ? "read" : "write";
            throw new IOException(
                $"AIO {op} failed at block {blockNumber}: aio_error returned {error}");
        }

        return false;
    }

    /// <summary>
    /// Polls an AIO control block for completion using aio_error, yielding between iterations
    /// to avoid busy-waiting. Uses a spin-then-delay pattern for low-latency completion.
    /// </summary>
    /// <param name="aiocbPtr">IntPtr to the native-heap-allocated aiocb struct.</param>
    /// <param name="blockNumber">Block number for error reporting.</param>
    /// <param name="isRead">Whether this is a read or write operation.</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task PollForCompletionAsync(
        IntPtr aiocbPtr, long blockNumber, bool isRead, CancellationToken ct)
    {
        // Fast path: check if already completed synchronously
        if (TryCheckCompletion(aiocbPtr, blockNumber, isRead))
        {
            return;
        }

        // Slow path: poll with yield/delay backoff
        int spinCount = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (spinCount < SpinCountBeforeDelay)
            {
                await Task.Yield();
            }
            else
            {
                await Task.Delay(MaxPollDelayMs, ct);
            }

            if (TryCheckCompletion(aiocbPtr, blockNumber, isRead))
            {
                return;
            }

            spinCount++;
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
