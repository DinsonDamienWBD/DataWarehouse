using DataWarehouse.SDK.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine;

/// <summary>
/// Abstraction for block-oriented storage devices.
/// Provides random-access read/write operations at block granularity.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: Virtual Disk Engine (VDE-01/VDE-04)")]
public interface IBlockDevice : IAsyncDisposable
{
    /// <summary>
    /// Gets the block size in bytes.
    /// All read/write operations must be aligned to this size.
    /// </summary>
    int BlockSize { get; }

    /// <summary>
    /// Gets the total number of blocks in this device.
    /// </summary>
    long BlockCount { get; }

    /// <summary>
    /// Reads a block from the device.
    /// </summary>
    /// <param name="blockNumber">The block number to read (0-based).</param>
    /// <param name="buffer">Buffer to receive the block data. Must be at least BlockSize bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if blockNumber is out of range.</exception>
    /// <exception cref="ArgumentException">Thrown if buffer is too small.</exception>
    Task ReadBlockAsync(long blockNumber, Memory<byte> buffer, CancellationToken ct = default);

    /// <summary>
    /// Writes a block to the device.
    /// </summary>
    /// <param name="blockNumber">The block number to write (0-based).</param>
    /// <param name="data">Data to write. Must be exactly BlockSize bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if blockNumber is out of range.</exception>
    /// <exception cref="ArgumentException">Thrown if data length is incorrect.</exception>
    Task WriteBlockAsync(long blockNumber, ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>
    /// Flushes any buffered writes to persistent storage.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task FlushAsync(CancellationToken ct = default);
}
