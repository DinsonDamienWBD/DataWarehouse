using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace DataWarehouse.SDK.VirtualDiskEngine.IO;

/// <summary>
/// Provides an <see cref="IMemoryOwner{T}"/> backed by natively aligned memory,
/// suitable for direct I/O (ODirect, FILE_FLAG_NO_BUFFERING) and DMA transfers.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="NativeMemory.AlignedAlloc"/> to guarantee that the buffer starts
/// at an address that is a multiple of the specified alignment. This is required by
/// most OS direct I/O APIs and hardware DMA engines.
/// </para>
/// <para>
/// The allocation is over-sized by <c>alignment</c> bytes to guarantee alignment even
/// if the runtime's memory manager does not provide aligned allocations natively.
/// The <see cref="Memory"/> property exposes exactly <c>byteCount</c> bytes starting
/// at the first aligned offset within the allocation.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "VOPT-79: Aligned native memory for direct I/O buffers")]
public sealed class AlignedMemoryOwner : IMemoryOwner<byte>
{
    private unsafe byte* _pointer;
    private UnmanagedMemoryManager? _manager;
    private int _disposed;

    /// <summary>
    /// Gets the number of bytes allocated.
    /// </summary>
    public int ByteCount { get; }

    /// <summary>
    /// Gets the alignment in bytes.
    /// </summary>
    public int Alignment { get; }

    /// <summary>
    /// Creates a new aligned memory owner with the specified size and alignment.
    /// </summary>
    /// <param name="byteCount">Number of bytes to allocate (must be > 0).</param>
    /// <param name="alignment">
    /// Alignment in bytes (must be a power of 2 and > 0).
    /// Typical values: 512 (legacy sector), 4096 (Advanced Format sector).
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="byteCount"/> is not positive or <paramref name="alignment"/> is not a positive power of 2.
    /// </exception>
    /// <exception cref="OutOfMemoryException">Thrown when native memory allocation fails.</exception>
    public unsafe AlignedMemoryOwner(int byteCount, int alignment)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(byteCount, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(alignment, 0);

        if ((alignment & (alignment - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), alignment,
                "Alignment must be a power of 2.");
        }

        ByteCount = byteCount;
        Alignment = alignment;
        _pointer = (byte*)NativeMemory.AlignedAlloc((nuint)byteCount, (nuint)alignment);
        _manager = new UnmanagedMemoryManager(_pointer, byteCount);

        // Zero-initialize for safety (prevent information leakage from previous allocations)
        NativeMemory.Clear(_pointer, (nuint)byteCount);
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
    public unsafe void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return;
        }

        _manager = null;

        byte* ptr = _pointer;
        _pointer = null;

        if (ptr != null)
        {
            NativeMemory.AlignedFree(ptr);
        }
    }

    /// <summary>
    /// A <see cref="MemoryManager{T}"/> that wraps an unmanaged pointer and length,
    /// providing <see cref="Memory{T}"/> access to natively allocated memory.
    /// </summary>
    private sealed unsafe class UnmanagedMemoryManager : MemoryManager<byte>
    {
        private readonly byte* _pointer;
        private readonly int _length;

        public UnmanagedMemoryManager(byte* pointer, int length)
        {
            _pointer = pointer;
            _length = length;
        }

        public override Span<byte> GetSpan() => new(_pointer, _length);

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(elementIndex, _length);

            // Memory is already pinned (native allocation), return handle with offset pointer
            return new MemoryHandle(_pointer + elementIndex);
        }

        public override void Unpin()
        {
            // No-op: native memory is always pinned
        }

        protected override void Dispose(bool disposing)
        {
            // No-op: the owning AlignedMemoryOwner manages the native allocation
        }
    }
}
