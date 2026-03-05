using System;
using System.Collections.Concurrent;
using System.Threading;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Preamble;

/// <summary>
/// Pool-based DMA-safe memory allocator for SPDK I/O buffers in the preamble boot path.
/// Pre-allocates a set of physically contiguous, hugepage-backed buffers via
/// <see cref="SpdkNativeBindings.DmaZmalloc"/> and provides lock-free rent/return
/// semantics for zero-allocation I/O operations.
/// </summary>
/// <remarks>
/// <para>
/// Each buffer is aligned to the configured block size and sized to hold exactly one
/// block. When the pool is exhausted, <see cref="Rent"/> transparently allocates a new
/// DMA buffer (pool growth), ensuring the I/O path never blocks on allocation.
/// </para>
/// <para>
/// This allocator is specific to the preamble bare-metal boot scenario where SPDK takes
/// direct NVMe ownership via vfio-pci. For hosted environments, the general-purpose
/// SPDK device in <c>IO.Spdk</c> manages its own DMA memory.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 Wave 6: VOPT-63 preamble DMA buffer pool")]
internal sealed class SpdkDmaAllocator : IDisposable
{
    private readonly int _blockSize;
    private readonly ConcurrentStack<IntPtr> _pool;
    private int _totalAllocated;
    private volatile bool _disposed;

    /// <summary>
    /// Initialises a new <see cref="SpdkDmaAllocator"/> with a pre-warmed buffer pool.
    /// </summary>
    /// <param name="blockSize">
    /// Size of each DMA buffer in bytes. Must be a positive power of two and match
    /// the NVMe namespace sector size or the VDE block size.
    /// </param>
    /// <param name="poolSize">
    /// Number of DMA buffers to pre-allocate. A typical value is 32-128 for
    /// concurrent I/O depth in the preamble boot path.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="blockSize"/> is not positive or <paramref name="poolSize"/> is negative.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The preamble SPDK library is not available on this platform.
    /// </exception>
    /// <exception cref="OutOfMemoryException">
    /// SPDK failed to allocate a DMA buffer (hugepage pool exhausted).
    /// </exception>
    public SpdkDmaAllocator(int blockSize, int poolSize)
    {
        if (blockSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize,
                "Block size must be a positive value.");

        if (poolSize < 0)
            throw new ArgumentOutOfRangeException(nameof(poolSize), poolSize,
                "Pool size must not be negative.");

        if (!SpdkNativeBindings.IsAvailable)
            throw new InvalidOperationException(
                "The preamble SPDK library (libspdk_nvme) is not available on this platform. " +
                "SPDK DMA allocation requires a bare-metal Linux environment with hugepages and vfio-pci.");

        _blockSize = blockSize;
        _pool = new ConcurrentStack<IntPtr>();

        // Pre-warm the pool with the requested number of DMA buffers.
        for (int i = 0; i < poolSize; i++)
        {
            IntPtr buffer = AllocateBuffer();
            _pool.Push(buffer);
        }
    }

    /// <summary>
    /// Gets the block size in bytes that each buffer in this allocator is sized for.
    /// </summary>
    public int BlockSize => _blockSize;

    /// <summary>
    /// Gets the total number of DMA buffers allocated by this instance (including
    /// those currently rented and those available in the pool).
    /// </summary>
    public int TotalAllocated => Volatile.Read(ref _totalAllocated);

    /// <summary>
    /// Gets the approximate number of buffers currently available in the pool.
    /// </summary>
    public int PoolAvailable => _pool.Count;

    /// <summary>
    /// Rents a DMA-safe buffer from the pool. If the pool is empty, a new buffer
    /// is allocated transparently (pool growth).
    /// </summary>
    /// <returns>
    /// A pointer to a physically contiguous, hugepage-backed buffer of
    /// <see cref="BlockSize"/> bytes suitable for NVMe DMA transfers.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The allocator has been disposed.</exception>
    /// <exception cref="OutOfMemoryException">SPDK failed to allocate a DMA buffer.</exception>
    public IntPtr Rent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pool.TryPop(out IntPtr buffer))
            return buffer;

        // Pool exhausted: allocate a new buffer (growth).
        return AllocateBuffer();
    }

    /// <summary>
    /// Returns a previously rented DMA buffer to the pool for reuse.
    /// </summary>
    /// <param name="buffer">
    /// Buffer pointer previously obtained from <see cref="Rent"/>.
    /// Must not be <see cref="IntPtr.Zero"/>.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="buffer"/> is zero.</exception>
    public void Return(IntPtr buffer)
    {
        if (buffer == IntPtr.Zero)
            throw new ArgumentException("Cannot return a null buffer.", nameof(buffer));

        if (_disposed)
        {
            // If already disposed, free immediately rather than returning to pool.
            SpdkNativeBindings.DmaFree(buffer);
            return;
        }

        _pool.Push(buffer);
    }

    /// <summary>
    /// Releases all DMA buffers, both pooled and any that were returned after disposal.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        while (_pool.TryPop(out IntPtr buffer))
        {
            SpdkNativeBindings.DmaFree(buffer);
        }
    }

    private IntPtr AllocateBuffer()
    {
        IntPtr buffer = SpdkNativeBindings.DmaZmalloc(
            (nuint)_blockSize,
            (nuint)_blockSize,
            IntPtr.Zero);

        if (buffer == IntPtr.Zero)
            throw new OutOfMemoryException(
                $"SPDK DMA allocation failed for {_blockSize} bytes. " +
                "Verify hugepage availability and SPDK environment initialisation.");

        Interlocked.Increment(ref _totalAllocated);
        return buffer;
    }
}
