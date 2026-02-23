using System.Collections.Concurrent;
using System.Diagnostics;

namespace DataWarehouse.SDK.IO.DeterministicIo;

/// <summary>
/// Exception thrown when the pre-allocated buffer pool is exhausted
/// and no buffer becomes available within the specified timeout.
/// </summary>
public sealed class BufferExhaustedException : InvalidOperationException
{
    /// <summary>
    /// The timeout that was exceeded waiting for a buffer.
    /// </summary>
    public TimeSpan Timeout { get; }

    /// <summary>
    /// Total number of buffers in the pool.
    /// </summary>
    public int TotalBuffers { get; }

    public BufferExhaustedException(TimeSpan timeout, int totalBuffers)
        : base($"Pre-allocated buffer pool exhausted. All {totalBuffers} buffers in use. " +
               $"Timed out after {timeout.TotalMilliseconds:F1}ms. " +
               "This indicates deterministic I/O capacity exceeded — increase PreAllocatedBufferCount or reduce concurrent operations.")
    {
        Timeout = timeout;
        TotalBuffers = totalBuffers;
    }
}

/// <summary>
/// A buffer rented from the <see cref="PreAllocatedBufferPool"/>.
/// Must be disposed to return the buffer to the pool.
/// IMPORTANT: Zero dynamic allocation — this struct references pre-allocated pinned memory.
/// </summary>
public readonly struct PooledBuffer : IDisposable
{
    private readonly PreAllocatedBufferPool? _pool;

    /// <summary>
    /// The memory region for this buffer. Valid only while not disposed.
    /// </summary>
    public Memory<byte> Memory { get; }

    /// <summary>
    /// The slot index within the pool (used for return).
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// Whether this buffer is valid (was successfully rented).
    /// </summary>
    public bool IsValid => _pool is not null;

    internal PooledBuffer(PreAllocatedBufferPool pool, Memory<byte> memory, int index)
    {
        _pool = pool;
        Memory = memory;
        Index = index;
    }

    /// <summary>
    /// Returns the buffer to the pool. The memory is cleared for security.
    /// </summary>
    public void Dispose()
    {
        _pool?.Return(Index);
    }
}

/// <summary>
/// Fixed-size pre-allocated buffer pool with zero dynamic allocation on the hot path.
/// All buffers are pinned at construction time for P/Invoke safety (io_uring, direct I/O).
/// Uses a lock-free <see cref="ConcurrentQueue{T}"/> as the free-list.
/// </summary>
/// <remarks>
/// Design principles for safety-critical environments:
/// <list type="bullet">
///   <item>No <c>new byte[]</c> on hot path</item>
///   <item>No <see cref="System.Buffers.ArrayPool{T}"/> (unpredictable return timing)</item>
///   <item>No <see cref="System.Buffers.MemoryPool{T}"/> (dynamic sizing)</item>
///   <item>Only pre-allocated pinned arrays via <see cref="GC.AllocateArray{T}(int, bool)"/></item>
///   <item>Lock-free rent/return via <see cref="ConcurrentQueue{T}"/></item>
/// </list>
/// </remarks>
public sealed class PreAllocatedBufferPool : IDisposable
{
    private readonly byte[][] _buffers;
    private readonly ConcurrentQueue<int> _freeList;
    private readonly int _bufferSize;
    private volatile bool _disposed;

    /// <summary>
    /// Number of buffers currently available for rent (approximate).
    /// </summary>
    public int Available => _freeList.Count;

    /// <summary>
    /// Total number of buffers in this pool.
    /// </summary>
    public int Total { get; }

    /// <summary>
    /// Size of each buffer in bytes.
    /// </summary>
    public int BufferSize => _bufferSize;

    /// <summary>
    /// Creates a new pre-allocated buffer pool.
    /// All buffers are allocated and pinned immediately.
    /// </summary>
    /// <param name="bufferCount">Number of buffers to allocate.</param>
    /// <param name="bufferSize">Size of each buffer in bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException">If bufferCount or bufferSize is not positive.</exception>
    public PreAllocatedBufferPool(int bufferCount, int bufferSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);

        Total = bufferCount;
        _bufferSize = bufferSize;
        _buffers = new byte[bufferCount][];
        _freeList = new ConcurrentQueue<int>();

        // Allocate ALL buffers upfront — pinned for P/Invoke safety with io_uring
        for (int i = 0; i < bufferCount; i++)
        {
            _buffers[i] = GC.AllocateArray<byte>(bufferSize, pinned: true);
            _freeList.Enqueue(i);
        }
    }

    /// <summary>
    /// Attempts to rent a buffer without waiting. Returns false if pool is exhausted.
    /// Zero allocation on hot path.
    /// </summary>
    /// <param name="buffer">The rented buffer if successful.</param>
    /// <returns>True if a buffer was available; false if pool is exhausted.</returns>
    public bool TryRent(out PooledBuffer buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_freeList.TryDequeue(out int index))
        {
            buffer = new PooledBuffer(this, _buffers[index].AsMemory(), index);
            return true;
        }

        buffer = default;
        return false;
    }

    /// <summary>
    /// Rents a buffer, spinning up to the specified timeout.
    /// Throws <see cref="BufferExhaustedException"/> if no buffer becomes available.
    /// </summary>
    /// <param name="timeout">Maximum time to wait for a buffer.</param>
    /// <returns>A rented buffer. Caller must dispose to return it.</returns>
    /// <exception cref="BufferExhaustedException">Thrown if no buffer is available within timeout.</exception>
    public PooledBuffer Rent(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Fast path: try immediate dequeue
        if (_freeList.TryDequeue(out int index))
        {
            return new PooledBuffer(this, _buffers[index].AsMemory(), index);
        }

        // Spin-wait path for bounded latency
        var sw = Stopwatch.StartNew();
        var spinWait = new SpinWait();

        while (sw.Elapsed < timeout)
        {
            spinWait.SpinOnce();

            if (_freeList.TryDequeue(out index))
            {
                return new PooledBuffer(this, _buffers[index].AsMemory(), index);
            }
        }

        throw new BufferExhaustedException(timeout, Total);
    }

    /// <summary>
    /// Returns a buffer to the pool. Clears buffer bytes for security.
    /// </summary>
    /// <param name="index">The buffer slot index to return.</param>
    internal void Return(int index)
    {
        if (_disposed) return;

        if ((uint)index >= (uint)_buffers.Length)
            return;

        // Clear buffer data for security (prevent data leakage between operations)
        _buffers[index].AsSpan().Clear();

        _freeList.Enqueue(index);
    }

    /// <summary>
    /// Releases all buffers. After disposal, no buffers can be rented.
    /// Pinned arrays will be unpinned by GC after collection.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Clear all buffers for security before releasing
        for (int i = 0; i < _buffers.Length; i++)
        {
            _buffers[i].AsSpan().Clear();
        }

        // Drain free list
        while (_freeList.TryDequeue(out _)) { }
    }
}
