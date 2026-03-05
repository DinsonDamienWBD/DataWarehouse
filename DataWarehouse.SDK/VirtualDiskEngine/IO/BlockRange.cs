using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.VirtualDiskEngine.IO;

/// <summary>
/// Represents a contiguous range of blocks to read, with its destination buffer.
/// Used in scatter-gather batch read operations via <see cref="IBatchBlockDevice"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "VOPT-79: Batched scatter-gather I/O support")]
public readonly struct BlockRange
{
    /// <summary>
    /// Gets the starting block index (0-based).
    /// </summary>
    public long BlockNumber { get; }

    /// <summary>
    /// Gets the number of contiguous blocks in this range.
    /// </summary>
    public int BlockCount { get; }

    /// <summary>
    /// Gets the destination buffer for the read data.
    /// Must be at least <see cref="BlockCount"/> * blockSize bytes.
    /// </summary>
    public Memory<byte> Buffer { get; }

    /// <summary>
    /// Creates a new block range for a batch read operation.
    /// </summary>
    /// <param name="blockNumber">Starting block index (must be >= 0).</param>
    /// <param name="blockCount">Number of contiguous blocks (must be > 0).</param>
    /// <param name="buffer">Destination buffer for the read data.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="blockNumber"/> is negative or <paramref name="blockCount"/> is not positive.
    /// </exception>
    public BlockRange(long blockNumber, int blockCount, Memory<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(blockCount, 0);

        BlockNumber = blockNumber;
        BlockCount = blockCount;
        Buffer = buffer;
    }
}
