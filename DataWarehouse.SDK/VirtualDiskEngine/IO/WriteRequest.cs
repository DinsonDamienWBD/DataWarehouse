using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.VirtualDiskEngine.IO;

/// <summary>
/// Represents a contiguous range of blocks to write, with its source data.
/// Used in scatter-gather batch write operations via <see cref="IBatchBlockDevice"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "VOPT-79: Batched scatter-gather I/O support")]
public readonly struct WriteRequest
{
    /// <summary>
    /// Gets the starting block index (0-based).
    /// </summary>
    public long BlockNumber { get; }

    /// <summary>
    /// Gets the number of contiguous blocks in this request.
    /// </summary>
    public int BlockCount { get; }

    /// <summary>
    /// Gets the source data to write.
    /// Must be exactly <see cref="BlockCount"/> * blockSize bytes.
    /// </summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>
    /// Creates a new write request for a batch write operation.
    /// </summary>
    /// <param name="blockNumber">Starting block index (must be >= 0).</param>
    /// <param name="blockCount">Number of contiguous blocks (must be > 0).</param>
    /// <param name="data">Source data to write.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="blockNumber"/> is negative or <paramref name="blockCount"/> is not positive.
    /// </exception>
    public WriteRequest(long blockNumber, int blockCount, ReadOnlyMemory<byte> data)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(blockCount, 0);

        BlockNumber = blockNumber;
        BlockCount = blockCount;
        Data = data;
    }
}
