using DataWarehouse.SDK.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Integrity;

/// <summary>
/// Interface for computing and verifying per-block checksums.
/// Provides integrity guarantees for block-level data storage.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: Virtual Disk Engine (VDE-04 Checksumming)")]
public interface IBlockChecksummer
{
    /// <summary>
    /// Computes a checksum for a block of data using XxHash3.
    /// </summary>
    /// <param name="blockData">Block data to checksum.</param>
    /// <returns>64-bit XxHash3 checksum.</returns>
    ulong ComputeChecksum(ReadOnlySpan<byte> blockData);

    /// <summary>
    /// Verifies that a block's computed checksum matches the expected value.
    /// </summary>
    /// <param name="blockData">Block data to verify.</param>
    /// <param name="expectedChecksum">Expected checksum value.</param>
    /// <returns>True if checksums match, false otherwise.</returns>
    bool VerifyChecksum(ReadOnlySpan<byte> blockData, ulong expectedChecksum);

    /// <summary>
    /// Stores a checksum for a block in the checksum table.
    /// </summary>
    /// <param name="blockNumber">Block number.</param>
    /// <param name="checksum">Checksum value to store.</param>
    /// <param name="ct">Cancellation token.</param>
    Task StoreChecksumAsync(long blockNumber, ulong checksum, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the stored checksum for a block from the checksum table.
    /// </summary>
    /// <param name="blockNumber">Block number.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Stored checksum value.</returns>
    Task<ulong> GetStoredChecksumAsync(long blockNumber, CancellationToken ct = default);

    /// <summary>
    /// Verifies a block against its stored checksum.
    /// </summary>
    /// <param name="blockNumber">Block number.</param>
    /// <param name="blockData">Block data to verify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if block data matches stored checksum, false otherwise.</returns>
    Task<bool> VerifyBlockAsync(long blockNumber, ReadOnlyMemory<byte> blockData, CancellationToken ct = default);

    /// <summary>
    /// Flushes dirty checksum entries to disk.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>
    /// Invalidates the verified-block cache entry for a block (called after write).
    /// </summary>
    /// <param name="blockNumber">Block number.</param>
    void InvalidateCacheEntry(long blockNumber);
}
