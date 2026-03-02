using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers;

namespace DataWarehouse.SDK.VirtualDiskEngine.IO;

/// <summary>
/// Provides DI-injectable memory allocation with alignment guarantees.
/// Implementations may allocate fresh buffers or return pooled buffers.
/// </summary>
/// <remarks>
/// <para>
/// Direct I/O (O_DIRECT, FILE_FLAG_NO_BUFFERING) and DMA transfers require
/// buffers aligned to the physical sector size (typically 4096 bytes).
/// This interface enables callers to obtain aligned buffers without coupling
/// to a specific allocation strategy.
/// </para>
/// <para>
/// Dispose the allocator to release any pooled resources. Each returned
/// <see cref="IMemoryOwner{T}"/> must still be disposed independently.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "VOPT-82: DI-injectable aligned memory allocator interface")]
public interface IMemoryAllocator : IDisposable
{
    /// <summary>
    /// Gets the alignment guarantee of allocated buffers in bytes (e.g., 4096).
    /// All buffers returned by <see cref="Allocate"/> are guaranteed to start at
    /// an address that is a multiple of this value.
    /// </summary>
    int Alignment { get; }

    /// <summary>
    /// Allocates a buffer of at least <paramref name="byteCount"/> bytes with
    /// the alignment specified by <see cref="Alignment"/>.
    /// </summary>
    /// <param name="byteCount">Minimum number of bytes to allocate (must be > 0).</param>
    /// <returns>An <see cref="IMemoryOwner{T}"/> wrapping the aligned buffer. Caller must dispose.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="byteCount"/> is not positive.</exception>
    /// <exception cref="OutOfMemoryException">Thrown when native memory allocation fails.</exception>
    IMemoryOwner<byte> Allocate(int byteCount);
}
