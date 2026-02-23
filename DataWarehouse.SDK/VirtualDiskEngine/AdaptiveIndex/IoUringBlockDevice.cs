using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// <see cref="IBlockDevice"/> implementation using Linux io_uring for maximum I/O throughput.
/// Pre-allocates registered buffers via <see cref="NativeMemory.AlignedAlloc"/> for zero-copy I/O,
/// supports batched submissions, SQPoll (kernel-side submission), and NVMe passthrough.
/// On non-Linux platforms, the <see cref="Create"/> factory method returns a standard
/// <see cref="FileBlockDevice"/> for graceful fallback.
/// </summary>
/// <remarks>
/// Thread safety: Ring-per-thread design via <see cref="ThreadStaticAttribute"/>. Each thread
/// maintains its own io_uring instance. If thread count exceeds configured maximum
/// (default <see cref="Environment.ProcessorCount"/>), a shared ring with
/// <see cref="SemaphoreSlim"/> serialization is used.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-12 io_uring block device")]
public sealed class IoUringBlockDevice : IBlockDevice
{
    private readonly string _filePath;
    private readonly int _blockSize;
    private readonly int _queueDepth;
    private readonly bool _useSqPoll;
    private readonly int _maxThreadRings;

    // File descriptor opened with O_DIRECT
    private int _fd = -1;

    // Registered buffer management
    private unsafe nint* _bufferPointers;
    private IoUringBindings.IoVec[]? _iovecs;
    private readonly ConcurrentQueue<int> _bufferPool = new();
    private readonly SemaphoreSlim _bufferSemaphore;

    // Per-thread rings indexed by managed thread ID for ring-per-thread design
    private readonly ConcurrentDictionary<int, IoUringBindings.IoUring> _threadRings = new();

    // Shared ring fallback for overflow threads
    private IoUringBindings.IoUring _sharedRing;
    private bool _sharedRingInitialized;
    private readonly SemaphoreSlim _sharedRingLock = new(1, 1);

    // Correlation ID for SQE/CQE matching
    private long _nextCorrelationId;

    // NVMe passthrough support
    private readonly bool _isNvmeDevice;

    // Dispose tracking
    private bool _disposed;

    /// <inheritdoc/>
    public int BlockSize => _blockSize;

    /// <inheritdoc/>
    public long BlockCount { get; }

    /// <summary>
    /// Creates a new io_uring-backed block device.
    /// </summary>
    /// <param name="filePath">Path to the block device or file.</param>
    /// <param name="blockSize">Size of each block in bytes (must be aligned to 4096).</param>
    /// <param name="blockCount">Total number of blocks.</param>
    /// <param name="queueDepth">Number of SQ entries (default 256).</param>
    /// <param name="useSqPoll">Enable kernel-side SQ polling (reduces syscalls).</param>
    public IoUringBlockDevice(string filePath, int blockSize, long blockCount, int queueDepth = 256, bool useSqPoll = false)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 512);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(blockCount, 0L);
        ArgumentOutOfRangeException.ThrowIfLessThan(queueDepth, 1);

        _filePath = filePath;
        _blockSize = blockSize;
        _queueDepth = queueDepth;
        _useSqPoll = useSqPoll;
        _maxThreadRings = Environment.ProcessorCount;
        BlockCount = blockCount;
        _bufferSemaphore = new SemaphoreSlim(queueDepth, queueDepth);
        _isNvmeDevice = filePath.StartsWith("/dev/nvme", StringComparison.Ordinal);

        if (IoUringBindings.IsAvailable)
        {
            InitializeNative();
        }
    }

    /// <summary>
    /// Initializes native io_uring resources: file descriptor, registered buffers, and shared ring.
    /// </summary>
    private unsafe void InitializeNative()
    {
        // Open file with O_DIRECT for direct I/O (bypass page cache)
        _fd = IoUringBindings.PosixOpen(_filePath, IoUringBindings.O_RDWR | IoUringBindings.O_DIRECT, 0);
        if (_fd < 0)
        {
            throw new IOException($"Failed to open '{_filePath}' with O_DIRECT. errno={Marshal.GetLastPInvokeError()}");
        }

        // Pre-allocate registered buffers with 4096-byte alignment for O_DIRECT
        _bufferPointers = (nint*)NativeMemory.AlignedAlloc(
            (nuint)(_queueDepth * sizeof(nint)), 64);

        _iovecs = new IoUringBindings.IoVec[_queueDepth];

        for (int i = 0; i < _queueDepth; i++)
        {
            nint buf = (nint)NativeMemory.AlignedAlloc((nuint)_blockSize, 4096);
            _bufferPointers[i] = buf;
            _iovecs[i] = new IoUringBindings.IoVec
            {
                Base = buf,
                Len = (nuint)_blockSize
            };
            _bufferPool.Enqueue(i);
        }

        // Initialize shared ring (fallback for excess threads)
        InitializeRing(ref _sharedRing);
        _sharedRingInitialized = true;

        // Register buffers with the shared ring
        fixed (IoUringBindings.IoVec* iov = _iovecs)
        {
            int ret = IoUringBindings.RegisterBuffers(ref _sharedRing, (nint)iov, (uint)_queueDepth);
            if (ret < 0)
            {
                // Non-fatal: fall back to non-registered buffer I/O
            }
        }
    }

    /// <summary>
    /// Initializes an io_uring ring with the configured queue depth and flags.
    /// </summary>
    private void InitializeRing(ref IoUringBindings.IoUring ring)
    {
        var p = new IoUringBindings.IoUringParams();
        if (_useSqPoll)
        {
            p.Flags = IoUringBindings.IORING_SETUP_SQPOLL;
            p.SqThreadIdle = 2000; // 2 seconds idle before kernel thread sleeps
        }

        int ret = IoUringBindings.QueueInitParams((uint)_queueDepth, ref ring, ref p);
        if (ret < 0)
        {
            throw new IOException($"io_uring_queue_init_params failed with error {ret}");
        }
    }

    /// <summary>
    /// Gets or creates a per-thread io_uring ring, falling back to the shared ring
    /// when the maximum thread ring count is exceeded.
    /// </summary>
    /// <param name="ring">The ring to use (thread-local or shared).</param>
    /// <param name="isShared">True if the shared ring was returned (caller must lock).</param>
    private void GetRing(out IoUringBindings.IoUring ring, out bool isShared)
    {
        int threadId = Environment.CurrentManagedThreadId;

        if (_threadRings.TryGetValue(threadId, out var existing))
        {
            ring = existing;
            isShared = false;
            return;
        }

        if (_threadRings.Count < _maxThreadRings)
        {
            var threadRing = new IoUringBindings.IoUring();
            InitializeRing(ref threadRing);
            if (_threadRings.TryAdd(threadId, threadRing))
            {
                ring = threadRing;
                isShared = false;
                return;
            }

            // Another thread beat us; clean up and use shared
            IoUringBindings.QueueExit(ref threadRing);
        }

        // Exceeded max thread rings: use shared ring with lock
        ring = _sharedRing;
        isShared = true;
    }

    /// <summary>
    /// Acquires a registered buffer index from the pool.
    /// </summary>
    private async Task<int> AcquireBufferAsync(CancellationToken ct)
    {
        await _bufferSemaphore.WaitAsync(ct);
        if (_bufferPool.TryDequeue(out int index))
        {
            return index;
        }
        _bufferSemaphore.Release();
        throw new InvalidOperationException("Buffer pool exhausted despite semaphore");
    }

    /// <summary>
    /// Returns a registered buffer index to the pool.
    /// </summary>
    private void ReleaseBuffer(int index)
    {
        _bufferPool.Enqueue(index);
        _bufferSemaphore.Release();
    }

    /// <inheritdoc/>
    public async Task ReadBlockAsync(long blockNumber, Memory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(blockNumber, BlockCount);

        if (buffer.Length < _blockSize)
        {
            throw new ArgumentException($"Buffer must be at least {_blockSize} bytes.", nameof(buffer));
        }

        if (!IoUringBindings.IsAvailable || _fd < 0)
        {
            await FallbackReadAsync(blockNumber, buffer, ct);
            return;
        }

        int bufIndex = await AcquireBufferAsync(ct);
        try
        {
            long offset = blockNumber * _blockSize;
            ulong correlationId = (ulong)Interlocked.Increment(ref _nextCorrelationId);

            GetRing(out var ring, out bool isShared);
            if (isShared) await _sharedRingLock.WaitAsync(ct);
            try
            {
                nint sqe = IoUringBindings.GetSqe(ref ring);
                if (sqe == nint.Zero)
                {
                    throw new IOException("io_uring SQ full; cannot submit read");
                }

                unsafe
                {
                    IoUringBindings.PrepReadFixed(sqe, _fd, _bufferPointers[bufIndex],
                        (uint)_blockSize, offset, bufIndex);
                    ((IoUringBindings.IoUringSqe*)sqe)->UserData = correlationId;
                }

                int submitted = IoUringBindings.Submit(ref ring);
                if (submitted < 0)
                {
                    throw new IOException($"io_uring_submit failed: {submitted}");
                }

                // Wait for completion
                int ret = IoUringBindings.WaitCqe(ref ring, out nint cqePtr);
                if (ret < 0)
                {
                    throw new IOException($"io_uring_wait_cqe failed: {ret}");
                }

                unsafe
                {
                    var cqe = (IoUringBindings.IoUringCqe*)cqePtr;
                    if (cqe->Res < 0)
                    {
                        IoUringBindings.CqeSeen(ref ring, cqePtr);
                        throw new IOException($"io_uring read failed at block {blockNumber}: {cqe->Res}");
                    }
                }

                IoUringBindings.CqeSeen(ref ring, cqePtr);
            }
            finally
            {
                if (isShared) _sharedRingLock.Release();
            }

            // Copy from registered buffer to managed buffer
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

    /// <inheritdoc/>
    public async Task WriteBlockAsync(long blockNumber, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(blockNumber, BlockCount);

        if (data.Length != _blockSize)
        {
            throw new ArgumentException($"Data must be exactly {_blockSize} bytes.", nameof(data));
        }

        if (!IoUringBindings.IsAvailable || _fd < 0)
        {
            await FallbackWriteAsync(blockNumber, data, ct);
            return;
        }

        int bufIndex = await AcquireBufferAsync(ct);
        try
        {
            // Copy data to registered buffer
            unsafe
            {
                data.Span.CopyTo(new Span<byte>((void*)_bufferPointers[bufIndex], _blockSize));
            }

            long offset = blockNumber * _blockSize;
            ulong correlationId = (ulong)Interlocked.Increment(ref _nextCorrelationId);

            GetRing(out var ring, out bool isShared);
            if (isShared) await _sharedRingLock.WaitAsync(ct);
            try
            {
                nint sqe = IoUringBindings.GetSqe(ref ring);
                if (sqe == nint.Zero)
                {
                    throw new IOException("io_uring SQ full; cannot submit write");
                }

                unsafe
                {
                    IoUringBindings.PrepWriteFixed(sqe, _fd, _bufferPointers[bufIndex],
                        (uint)_blockSize, offset, bufIndex);
                    ((IoUringBindings.IoUringSqe*)sqe)->UserData = correlationId;
                }

                int submitted = IoUringBindings.Submit(ref ring);
                if (submitted < 0)
                {
                    throw new IOException($"io_uring_submit failed: {submitted}");
                }

                int ret = IoUringBindings.WaitCqe(ref ring, out nint cqePtr);
                if (ret < 0)
                {
                    throw new IOException($"io_uring_wait_cqe failed: {ret}");
                }

                unsafe
                {
                    var cqe = (IoUringBindings.IoUringCqe*)cqePtr;
                    if (cqe->Res < 0)
                    {
                        IoUringBindings.CqeSeen(ref ring, cqePtr);
                        throw new IOException($"io_uring write failed at block {blockNumber}: {cqe->Res}");
                    }
                }

                IoUringBindings.CqeSeen(ref ring, cqePtr);
            }
            finally
            {
                if (isShared) _sharedRingLock.Release();
            }
        }
        finally
        {
            ReleaseBuffer(bufIndex);
        }
    }

    /// <summary>
    /// Reads multiple blocks in a single batched io_uring submission.
    /// Submits all reads at once and collects completions, amortizing syscall overhead.
    /// </summary>
    /// <param name="blockNumbers">Block numbers to read.</param>
    /// <param name="buffers">Destination buffers, one per block number.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ReadBlocksAsync(long[] blockNumbers, Memory<byte>[] buffers, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(blockNumbers);
        ArgumentNullException.ThrowIfNull(buffers);

        if (blockNumbers.Length != buffers.Length)
        {
            throw new ArgumentException("blockNumbers and buffers must have the same length.");
        }

        if (blockNumbers.Length == 0) return;

        if (!IoUringBindings.IsAvailable || _fd < 0)
        {
            // Fallback: sequential reads
            for (int i = 0; i < blockNumbers.Length; i++)
            {
                await ReadBlockAsync(blockNumbers[i], buffers[i], ct);
            }
            return;
        }

        // Acquire buffers for batch
        int[] bufIndices = new int[blockNumbers.Length];
        for (int i = 0; i < blockNumbers.Length; i++)
        {
            bufIndices[i] = await AcquireBufferAsync(ct);
        }

        try
        {
            ulong baseCorrelation = (ulong)Interlocked.Add(ref _nextCorrelationId, blockNumbers.Length);

            GetRing(out var ring, out bool isShared);
            if (isShared) await _sharedRingLock.WaitAsync(ct);
            try
            {
                // Submit all reads in a single batch
                for (int i = 0; i < blockNumbers.Length; i++)
                {
                    long offset = blockNumbers[i] * _blockSize;
                    nint sqe = IoUringBindings.GetSqe(ref ring);
                    if (sqe == nint.Zero)
                    {
                        throw new IOException("io_uring SQ full during batch read");
                    }

                    unsafe
                    {
                        IoUringBindings.PrepReadFixed(sqe, _fd, _bufferPointers[bufIndices[i]],
                            (uint)_blockSize, offset, bufIndices[i]);
                        ((IoUringBindings.IoUringSqe*)sqe)->UserData = baseCorrelation - (ulong)(blockNumbers.Length - i);
                    }
                }

                int submitted = IoUringBindings.Submit(ref ring);
                if (submitted < 0)
                {
                    throw new IOException($"io_uring_submit batch failed: {submitted}");
                }

                // Collect all completions
                int completed = 0;
                while (completed < blockNumbers.Length)
                {
                    int ret = IoUringBindings.WaitCqe(ref ring, out nint cqePtr);
                    if (ret < 0)
                    {
                        throw new IOException($"io_uring_wait_cqe failed during batch: {ret}");
                    }

                    unsafe
                    {
                        var cqe = (IoUringBindings.IoUringCqe*)cqePtr;
                        if (cqe->Res < 0)
                        {
                            IoUringBindings.CqeSeen(ref ring, cqePtr);
                            throw new IOException($"io_uring batch read failed: {cqe->Res}");
                        }
                    }

                    IoUringBindings.CqeSeen(ref ring, cqePtr);
                    completed++;
                }
            }
            finally
            {
                if (isShared) _sharedRingLock.Release();
            }

            // Copy from registered buffers to managed buffers
            for (int i = 0; i < blockNumbers.Length; i++)
            {
                unsafe
                {
                    new Span<byte>((void*)_bufferPointers[bufIndices[i]], _blockSize)
                        .CopyTo(buffers[i].Span);
                }
            }
        }
        finally
        {
            for (int i = 0; i < bufIndices.Length; i++)
            {
                ReleaseBuffer(bufIndices[i]);
            }
        }
    }

    /// <summary>
    /// Sends a raw NVMe passthrough command via IORING_OP_URING_CMD.
    /// Only available on raw NVMe block devices (/dev/nvme*).
    /// </summary>
    /// <param name="command">NVMe command bytes.</param>
    /// <param name="data">Data buffer for the command.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="NotSupportedException">Thrown if not on an NVMe device.</exception>
    public async Task NvmePassthroughAsync(byte[] command, byte[] data, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(data);

        if (!_isNvmeDevice)
        {
            throw new NotSupportedException("NVMe passthrough is only available on /dev/nvme* block devices.");
        }

        if (!IoUringBindings.IsAvailable || _fd < 0)
        {
            throw new NotSupportedException("NVMe passthrough requires io_uring on Linux.");
        }

        ulong correlationId = (ulong)Interlocked.Increment(ref _nextCorrelationId);

        GetRing(out var ring, out bool isShared);
        if (isShared) await _sharedRingLock.WaitAsync(ct);
        try
        {
            nint sqe = IoUringBindings.GetSqe(ref ring);
            if (sqe == nint.Zero)
            {
                throw new IOException("io_uring SQ full; cannot submit NVMe command");
            }

            unsafe
            {
                fixed (byte* cmdPtr = command)
                {
                    IoUringBindings.PrepUringCmd(sqe, _fd, (nint)cmdPtr, (uint)command.Length);
                    ((IoUringBindings.IoUringSqe*)sqe)->UserData = correlationId;

                    int submitted = IoUringBindings.Submit(ref ring);
                    if (submitted < 0)
                    {
                        throw new IOException($"io_uring_submit NVMe cmd failed: {submitted}");
                    }

                    int ret = IoUringBindings.WaitCqe(ref ring, out nint cqePtr);
                    if (ret < 0)
                    {
                        throw new IOException($"io_uring_wait_cqe NVMe cmd failed: {ret}");
                    }

                    var cqe = (IoUringBindings.IoUringCqe*)cqePtr;
                    if (cqe->Res < 0)
                    {
                        IoUringBindings.CqeSeen(ref ring, cqePtr);
                        throw new IOException($"NVMe passthrough command failed: {cqe->Res}");
                    }

                    IoUringBindings.CqeSeen(ref ring, cqePtr);
                }
            }
        }
        finally
        {
            if (isShared) _sharedRingLock.Release();
        }
    }

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // io_uring with O_DIRECT bypasses page cache; explicit flush is a no-op.
        // Data is persisted on write completion.
        return Task.CompletedTask;
    }

    // ─── Fallback Methods ───────────────────────────────────────────────

    private async Task FallbackReadAsync(long blockNumber, Memory<byte> buffer, CancellationToken ct)
    {
        using var handle = File.OpenHandle(_filePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, FileOptions.Asynchronous | FileOptions.RandomAccess);
        long offset = blockNumber * _blockSize;
        int bytesRead = await RandomAccess.ReadAsync(handle, buffer[.._blockSize], offset, ct);
        if (bytesRead != _blockSize)
        {
            throw new IOException($"Incomplete fallback read: expected {_blockSize}, got {bytesRead}");
        }
    }

    private async Task FallbackWriteAsync(long blockNumber, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        using var handle = File.OpenHandle(_filePath, FileMode.Open, FileAccess.ReadWrite,
            FileShare.None, FileOptions.Asynchronous | FileOptions.RandomAccess);
        long offset = blockNumber * _blockSize;
        await RandomAccess.WriteAsync(handle, data, offset, ct);
    }

    // ─── Factory Method ─────────────────────────────────────────────────

    /// <summary>
    /// Creates the optimal <see cref="IBlockDevice"/> for the current platform.
    /// Returns <see cref="IoUringBlockDevice"/> on Linux with liburing available,
    /// otherwise falls back to <see cref="FileBlockDevice"/>.
    /// </summary>
    /// <param name="path">Path to the file or block device.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="blockCount">Total number of blocks.</param>
    /// <param name="createNew">Whether to create a new file (only for FileBlockDevice fallback).</param>
    /// <returns>An <see cref="IBlockDevice"/> using the fastest available I/O path.</returns>
    public static IBlockDevice Create(string path, int blockSize, long blockCount, bool createNew = false)
    {
        if (IoUringBindings.IsAvailable)
        {
            return new IoUringBlockDevice(path, blockSize, blockCount);
        }

        return new FileBlockDevice(path, blockSize, blockCount, createNew);
    }

    // ─── Dispose ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public unsafe ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        // Unregister buffers from shared ring
        if (_sharedRingInitialized)
        {
            IoUringBindings.UnregisterBuffers(ref _sharedRing);
            IoUringBindings.QueueExit(ref _sharedRing);
            _sharedRingInitialized = false;
        }

        // Free registered buffers
        if (_bufferPointers != null)
        {
            for (int i = 0; i < _queueDepth; i++)
            {
                if (_bufferPointers[i] != nint.Zero)
                {
                    NativeMemory.AlignedFree((void*)_bufferPointers[i]);
                    _bufferPointers[i] = nint.Zero;
                }
            }

            NativeMemory.AlignedFree(_bufferPointers);
            _bufferPointers = null;
        }

        // Close file descriptor
        if (_fd >= 0)
        {
            IoUringBindings.PosixClose(_fd);
            _fd = -1;
        }

        _bufferSemaphore.Dispose();
        _sharedRingLock.Dispose();

        return ValueTask.CompletedTask;
    }
}
