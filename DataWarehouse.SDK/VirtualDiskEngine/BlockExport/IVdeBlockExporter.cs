using System;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.BlockExport;

/// <summary>
/// Capabilities that a VDE block exporter may support.
/// Protocol strategies use these flags to determine which fast-path operations are available.
/// </summary>
[Flags]
[SdkCompatibility("6.0.0", Notes = "Phase 85: VDE-native block export (COMP-01)")]
public enum BlockExportCapabilities
{
    /// <summary>No special capabilities.</summary>
    None = 0,

    /// <summary>Supports zero-copy block reads via memory-mapped file access.</summary>
    ZeroCopy = 1 << 0,

    /// <summary>Supports batch reads of contiguous block ranges into a single buffer.</summary>
    BatchRead = 1 << 1,

    /// <summary>Supports scatter-gather I/O for non-contiguous block reads.</summary>
    ScatterGather = 1 << 2,

    /// <summary>Supports direct memory mapping of VDE file regions.</summary>
    DirectMemoryMap = 1 << 3,
}

/// <summary>
/// Contract for VDE-direct block export. Allows NAS/SAN protocol strategies
/// (SMB, NFS, iSCSI, FC, NVMe-oF) to serve VDE blocks directly from VDE regions,
/// bypassing the full plugin processing pipeline on the hot path.
///
/// This interface provides a zero-copy fast path for block-level I/O that is
/// significantly faster than traversing the plugin layer for each block read.
/// Protocol strategies should check <see cref="GetCapabilities"/> to determine
/// which operations are supported by the underlying implementation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 85: VDE-native block export (COMP-01)")]
public interface IVdeBlockExporter
{
    /// <summary>
    /// Reads a single block from the VDE file without copying to an intermediate buffer.
    /// The returned memory is valid until the exporter is disposed.
    /// </summary>
    /// <param name="blockAddress">Zero-based block address to read.</param>
    /// <returns>
    /// A <see cref="ReadOnlyMemory{T}"/> slice of exactly <c>blockSize</c> bytes
    /// referencing the block data directly from the memory-mapped file.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="blockAddress"/> is negative or exceeds the total block count.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Thrown when the exporter has been disposed.</exception>
    ReadOnlyMemory<byte> ReadBlockZeroCopy(long blockAddress);

    /// <summary>
    /// Reads a contiguous range of blocks directly into a caller-owned buffer.
    /// No intermediate allocation occurs; data is copied directly from the mapped file
    /// into <paramref name="destination"/>.
    /// </summary>
    /// <param name="startBlock">Zero-based address of the first block to read.</param>
    /// <param name="count">Number of contiguous blocks to read.</param>
    /// <param name="destination">
    /// Caller-owned buffer that must be at least <c>count * blockSize</c> bytes.
    /// </param>
    /// <returns>The total number of bytes written to <paramref name="destination"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the block range [startBlock, startBlock+count) exceeds the total block count.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="destination"/> is too small for the requested block count.
    /// </exception>
    ValueTask<int> ReadBlocksAsync(long startBlock, int count, Memory<byte> destination);

    /// <summary>
    /// Resolves which region owns the specified block address by looking up the
    /// block in the VDE region directory.
    /// </summary>
    /// <param name="blockAddress">Zero-based block address to look up.</param>
    /// <param name="region">
    /// When this method returns true, contains the <see cref="RegionPointer"/>
    /// describing the region that owns the block.
    /// </param>
    /// <returns>True if the block belongs to an active region; false otherwise.</returns>
    bool TryGetRegionForBlock(long blockAddress, out RegionPointer region);

    /// <summary>
    /// Reports the capabilities supported by this exporter implementation.
    /// Protocol strategies use this to select the optimal I/O path.
    /// </summary>
    /// <returns>A flags enum indicating supported capabilities.</returns>
    BlockExportCapabilities GetCapabilities();
}
