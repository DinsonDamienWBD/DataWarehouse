using DataWarehouse.SDK.Contracts;
using System.Buffers;

namespace DataWarehouse.SDK.VirtualDiskEngine.IO;

/// <summary>
/// Extends <see cref="IBatchBlockDevice"/> with O_DIRECT semantics and aligned buffer allocation.
/// Direct I/O bypasses the OS page cache, providing predictable latency and avoiding
/// double-buffering for workloads that manage their own caching (e.g., VDE block cache).
/// </summary>
/// <remarks>
/// <para>
/// Direct I/O requires all buffers to be aligned to the device's alignment requirement
/// (typically 512 or 4096 bytes). Use <see cref="GetAlignedBuffer"/> to allocate
/// DMA-compatible buffers that satisfy this constraint.
/// </para>
/// <para>
/// On Windows, direct I/O is achieved via FILE_FLAG_NO_BUFFERING. On Linux, via O_DIRECT
/// (typically through io_uring). On macOS, via F_NOCACHE. The <see cref="IsDirectIo"/>
/// property indicates whether the implementation has successfully enabled direct I/O
/// on the current platform.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "VOPT-79: Direct I/O interface for OS page cache bypass")]
public interface IDirectBlockDevice : IBatchBlockDevice
{
    /// <summary>
    /// Gets whether this device has direct I/O enabled, bypassing the OS page cache.
    /// Returns <c>true</c> when the underlying file handle was opened with direct I/O flags
    /// (e.g., FILE_FLAG_NO_BUFFERING on Windows, O_DIRECT on Linux).
    /// </summary>
    bool IsDirectIo { get; }

    /// <summary>
    /// Gets the alignment requirement in bytes for direct I/O buffers.
    /// Typically 512 bytes (legacy sector size) or 4096 bytes (Advanced Format sector size).
    /// All buffers passed to read/write operations must be aligned to this boundary.
    /// </summary>
    int AlignmentRequirement { get; }

    /// <summary>
    /// Allocates a buffer aligned to <see cref="AlignmentRequirement"/> for use with direct I/O.
    /// The returned buffer is suitable for DMA transfers and page-cache bypass operations.
    /// </summary>
    /// <param name="byteCount">The number of bytes to allocate. Should be a multiple of <see cref="AlignmentRequirement"/>.</param>
    /// <returns>An <see cref="IMemoryOwner{T}"/> wrapping the aligned buffer. Caller must dispose.</returns>
    IMemoryOwner<byte> GetAlignedBuffer(int byteCount);
}
