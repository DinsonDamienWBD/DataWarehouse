using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading;

namespace DataWarehouse.SDK.VirtualDiskEngine.IO.Spdk;

/// <summary>
/// Allocates DMA-safe memory buffers using SPDK's hugepage-backed allocator.
/// Buffers allocated through this class are guaranteed to be physically contiguous
/// and suitable for NVMe DMA transfers at sub-microsecond latency.
/// </summary>
/// <remarks>
/// <para>
/// SPDK manages its own hugepage pool initialized during <c>spdk_env_init</c>.
/// This allocator delegates to <c>spdk_dma_malloc</c>/<c>spdk_dma_free</c> and
/// does not implement additional pooling, as SPDK's internal allocator is already
/// optimized for high-frequency allocation patterns.
/// </para>
/// <para>
/// All returned buffers are zero-initialized to prevent information leakage from
/// previously freed DMA regions.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "VOPT-63: DMA-aligned buffer allocator for SPDK NVMe I/O")]
internal sealed class SpdkDmaAllocator
{
    /// <summary>
    /// Default alignment for DMA buffers (4096 bytes = NVMe Advanced Format sector size).
    /// </summary>
    internal const int DefaultAlignment = 4096;

    /// <summary>
    /// Allocates a DMA-safe buffer of the specified size with the given alignment.
    /// </summary>
    /// <param name="byteCount">Number of bytes to allocate (must be positive).</param>
    /// <param name="alignment">
    /// Alignment in bytes (default 4096). Must be a positive power of 2.
    /// NVMe requires sector-aligned DMA buffers for command submission.
    /// </param>
    /// <returns>
    /// An <see cref="IMemoryOwner{T}"/> wrapping the DMA buffer. The caller must
    /// dispose the returned owner to free the DMA memory.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="byteCount"/> is not positive or
    /// <paramref name="alignment"/> is not a positive power of 2.
    /// </exception>
    /// <exception cref="OutOfMemoryException">
    /// Thrown when SPDK's DMA allocator fails (e.g., hugepage pool exhausted).
    /// </exception>
    internal IMemoryOwner<byte> Allocate(int byteCount, int alignment = DefaultAlignment)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(byteCount, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(alignment, 0);

        if ((alignment & (alignment - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), alignment,
                "Alignment must be a power of 2.");
        }

        IntPtr ptr = SpdkNativeMethods.DmaMalloc((nint)byteCount, (nint)alignment, IntPtr.Zero);
        if (ptr == IntPtr.Zero)
        {
            throw new OutOfMemoryException(
                $"SPDK DMA allocation failed: requested {byteCount} bytes with {alignment}-byte alignment. " +
                "The hugepage pool may be exhausted.");
        }

        // Zero-initialize to prevent information leakage
        unsafe
        {
            NativeMemory.Clear(ptr.ToPointer(), (nuint)byteCount);
        }

        return new SpdkDmaMemoryOwner(ptr, byteCount);
    }

    /// <summary>
    /// Frees a DMA buffer that was allocated directly via <see cref="SpdkNativeMethods.DmaMalloc"/>.
    /// Use this only for buffers not wrapped in <see cref="IMemoryOwner{T}"/>.
    /// </summary>
    /// <param name="buffer">Pointer to the DMA buffer to free.</param>
    internal static void Free(IntPtr buffer)
    {
        if (buffer != IntPtr.Zero)
        {
            SpdkNativeMethods.DmaFree(buffer);
        }
    }

    /// <summary>
    /// An <see cref="IMemoryOwner{T}"/> backed by an SPDK DMA buffer allocated from
    /// the hugepage pool. Disposal returns the buffer to SPDK via <c>spdk_dma_free</c>.
    /// </summary>
    private sealed unsafe class SpdkDmaMemoryOwner : IMemoryOwner<byte>
    {
        private IntPtr _pointer;
        private DmaUnmanagedMemoryManager? _manager;
        private int _disposed;

        /// <summary>Gets the size in bytes of the DMA buffer.</summary>
        internal int ByteCount { get; }

        internal SpdkDmaMemoryOwner(IntPtr pointer, int byteCount)
        {
            _pointer = pointer;
            ByteCount = byteCount;
            _manager = new DmaUnmanagedMemoryManager((byte*)pointer, byteCount);
        }

        /// <inheritdoc/>
        public Memory<byte> Memory
        {
            get
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                return _manager!.Memory;
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            {
                return;
            }

            _manager = null;

            IntPtr ptr = _pointer;
            _pointer = IntPtr.Zero;

            if (ptr != IntPtr.Zero)
            {
                SpdkNativeMethods.DmaFree(ptr);
            }
        }
    }

    /// <summary>
    /// A <see cref="MemoryManager{T}"/> that wraps an unmanaged DMA pointer,
    /// providing <see cref="Memory{T}"/> access to SPDK-allocated memory.
    /// </summary>
    private sealed unsafe class DmaUnmanagedMemoryManager : MemoryManager<byte>
    {
        private readonly byte* _pointer;
        private readonly int _length;

        internal DmaUnmanagedMemoryManager(byte* pointer, int length)
        {
            _pointer = pointer;
            _length = length;
        }

        /// <inheritdoc/>
        public override Span<byte> GetSpan() => new(_pointer, _length);

        /// <inheritdoc/>
        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(elementIndex, _length);

            // DMA memory is always pinned (physically contiguous hugepage allocation)
            return new MemoryHandle(_pointer + elementIndex);
        }

        /// <inheritdoc/>
        public override void Unpin()
        {
            // No-op: DMA memory from hugepages is always pinned
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            // No-op: the owning SpdkDmaMemoryOwner manages the DMA allocation lifecycle
        }
    }
}
