using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace DataWarehouse.SDK.VirtualDiskEngine.IO;

/// <summary>
/// Pooled aligned memory allocator that recycles fixed-size aligned buffers.
/// Returns buffers to the pool on dispose instead of freeing, reducing allocation
/// overhead for workloads that repeatedly allocate and free same-sized buffers.
/// </summary>
/// <remarks>
/// <para>
/// All buffers returned by <see cref="Allocate"/> are exactly <c>bufferSize</c> bytes.
/// Requests for any other size will throw <see cref="ArgumentException"/>.
/// This constraint enables the pool to recycle buffers without size tracking.
/// </para>
/// <para>
/// Thread-safe via <see cref="ConcurrentBag{T}"/>. When the pool is exhausted,
/// new buffers are allocated on demand. When the pool exceeds <c>maxPooled</c>
/// capacity, returned buffers are freed instead of pooled.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "VOPT-82: Pooled aligned memory allocator for reusable DMA buffers")]
public sealed class AlignedMemoryPool : IMemoryAllocator
{
    private readonly int _bufferSize;
    private readonly int _maxPooled;
    private readonly ConcurrentBag<AlignedMemoryOwner> _pool = new();
    private int _totalAllocated;
    private volatile bool _disposed;

    /// <summary>
    /// Creates a new aligned memory pool with the specified buffer size and alignment.
    /// </summary>
    /// <param name="bufferSize">
    /// Fixed buffer size in bytes. All <see cref="Allocate"/> calls must request exactly this size.
    /// </param>
    /// <param name="alignment">
    /// Alignment in bytes (must be a positive power of 2). Default is 4096.
    /// </param>
    /// <param name="maxPooled">
    /// Maximum number of buffers to keep in the pool. Excess buffers are freed on return.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when parameters are invalid.
    /// </exception>
    public AlignedMemoryPool(int bufferSize, int alignment = 4096, int maxPooled = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bufferSize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(alignment, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxPooled, 0);

        if ((alignment & (alignment - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), alignment,
                "Alignment must be a power of 2.");
        }

        _bufferSize = bufferSize;
        Alignment = alignment;
        _maxPooled = maxPooled;
    }

    /// <inheritdoc/>
    public int Alignment { get; }

    /// <summary>
    /// Gets the current number of buffers available in the pool.
    /// </summary>
    public int PooledCount => _pool.Count;

    /// <summary>
    /// Gets the total number of buffers created since pool construction (diagnostic).
    /// </summary>
    public int TotalAllocated => Volatile.Read(ref _totalAllocated);

    /// <summary>
    /// Allocates a fixed-size aligned buffer from the pool, or creates a new one if the pool is empty.
    /// </summary>
    /// <param name="byteCount">Must equal the pool's <c>bufferSize</c>.</param>
    /// <returns>A pooled buffer wrapped in an <see cref="IMemoryOwner{T}"/>.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="byteCount"/> does not match the pool's fixed buffer size.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Thrown when the pool has been disposed.</exception>
    public IMemoryOwner<byte> Allocate(int byteCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (byteCount != _bufferSize)
        {
            throw new ArgumentException(
                $"Pool only supports fixed-size buffers of {_bufferSize} bytes, but {byteCount} was requested.",
                nameof(byteCount));
        }

        if (_pool.TryTake(out var owner))
        {
            return new PooledBufferOwner(this, owner);
        }

        // Pool empty: allocate a new buffer
        var newOwner = new AlignedMemoryOwner(_bufferSize, Alignment);
        Interlocked.Increment(ref _totalAllocated);
        return new PooledBufferOwner(this, newOwner);
    }

    /// <summary>
    /// Disposes the pool and frees all pooled buffers via <see cref="NativeMemory.AlignedFree"/>.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        while (_pool.TryTake(out var owner))
        {
            owner.Dispose();
        }
    }

    /// <summary>
    /// Returns a buffer to the pool if capacity allows, otherwise disposes it.
    /// </summary>
    private void Return(AlignedMemoryOwner owner)
    {
        if (_disposed || _pool.Count >= _maxPooled)
        {
            owner.Dispose();
            return;
        }

        _pool.Add(owner);
    }

    /// <summary>
    /// Wraps an <see cref="AlignedMemoryOwner"/> so that disposal returns the buffer
    /// to the pool instead of freeing native memory.
    /// </summary>
    private sealed class PooledBufferOwner : IMemoryOwner<byte>
    {
        private AlignedMemoryPool? _pool;
        private AlignedMemoryOwner? _inner;

        public PooledBufferOwner(AlignedMemoryPool pool, AlignedMemoryOwner inner)
        {
            _pool = pool;
            _inner = inner;
        }

        public Memory<byte> Memory
        {
            get
            {
                var inner = _inner;
                ObjectDisposedException.ThrowIf(inner is null, this);
                return inner.Memory;
            }
        }

        public void Dispose()
        {
            var pool = Interlocked.Exchange(ref _pool, null);
            var inner = Interlocked.Exchange(ref _inner, null);

            if (inner is null)
            {
                return;
            }

            if (pool is not null)
            {
                pool.Return(inner);
            }
            else
            {
                inner.Dispose();
            }
        }
    }
}
