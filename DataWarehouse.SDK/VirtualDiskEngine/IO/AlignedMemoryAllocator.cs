using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers;

namespace DataWarehouse.SDK.VirtualDiskEngine.IO;

/// <summary>
/// Stateless aligned memory allocator that creates a new <see cref="AlignedMemoryOwner"/>
/// for each allocation. Each buffer is independently allocated and freed via
/// <see cref="System.Runtime.InteropServices.NativeMemory.AlignedAlloc"/>.
/// </summary>
/// <remarks>
/// <para>
/// This allocator is thread-safe because it is stateless: each call to <see cref="Allocate"/>
/// creates a new independent allocation. For workloads that allocate and free many same-sized
/// buffers, use <see cref="AlignedMemoryPool"/> instead to amortize allocation cost.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "VOPT-82: Stateless aligned memory allocator via NativeMemory")]
public sealed class AlignedMemoryAllocator : IMemoryAllocator
{
    /// <summary>
    /// Creates a new aligned memory allocator with the specified alignment.
    /// </summary>
    /// <param name="alignment">
    /// Alignment in bytes (must be a positive power of 2). Default is 4096 (Advanced Format sector size).
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="alignment"/> is not a positive power of 2.
    /// </exception>
    public AlignedMemoryAllocator(int alignment = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(alignment, 0);

        if ((alignment & (alignment - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), alignment,
                "Alignment must be a power of 2.");
        }

        Alignment = alignment;
    }

    /// <inheritdoc/>
    public int Alignment { get; }

    /// <inheritdoc/>
    public IMemoryOwner<byte> Allocate(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(byteCount, 0);
        return new AlignedMemoryOwner(byteCount, Alignment);
    }

    /// <summary>
    /// No-op: each <see cref="IMemoryOwner{T}"/> manages its own native allocation independently.
    /// </summary>
    public void Dispose()
    {
        // Stateless allocator: nothing to dispose.
        // Each AlignedMemoryOwner is independently allocated and freed.
    }
}
