using System.Buffers;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.ModuleManagement;

/// <summary>
/// A contiguous range of free blocks found in the allocation bitmap.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Online module addition free block range")]
public readonly record struct FreeBlockRange(long StartBlock, long BlockCount);

/// <summary>
/// Scans the VDE allocation bitmap to find contiguous runs of free blocks.
/// Each bit in the bitmap represents one block: bit=0 means free, bit=1 means allocated.
/// Thread-safe: uses no shared mutable state (reads stream with explicit position).
/// All reads use <see cref="ArrayPool{T}.Shared"/> for buffer management.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Online module addition free space scanner (OMA-01)")]
public sealed class FreeSpaceScanner
{
    private readonly Stream _vdeStream;
    private readonly int _blockSize;
    private readonly long _bitmapStartBlock;
    private readonly long _bitmapBlockCount;

    /// <summary>
    /// Creates a new FreeSpaceScanner for the given VDE stream.
    /// </summary>
    /// <param name="vdeStream">The VDE stream to read bitmap data from.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="bitmapStartBlock">First block number of the allocation bitmap region.</param>
    /// <param name="bitmapBlockCount">Number of blocks in the allocation bitmap region.</param>
    public FreeSpaceScanner(Stream vdeStream, int blockSize, long bitmapStartBlock, long bitmapBlockCount)
    {
        _vdeStream = vdeStream ?? throw new ArgumentNullException(nameof(vdeStream));
        if (blockSize < FormatConstants.MinBlockSize || blockSize > FormatConstants.MaxBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be between {FormatConstants.MinBlockSize} and {FormatConstants.MaxBlockSize}.");
        _blockSize = blockSize;
        _bitmapStartBlock = bitmapStartBlock;
        _bitmapBlockCount = bitmapBlockCount;
    }

    /// <summary>
    /// Finds the first contiguous run of at least <paramref name="requiredBlocks"/> free blocks
    /// in the allocation bitmap.
    /// </summary>
    /// <param name="requiredBlocks">Minimum number of contiguous free blocks needed.</param>
    /// <returns>The free block range if found, or null if no sufficient contiguous run exists.</returns>
    public FreeBlockRange? FindContiguousFreeBlocks(long requiredBlocks)
    {
        if (requiredBlocks <= 0)
            throw new ArgumentOutOfRangeException(nameof(requiredBlocks), "Required blocks must be positive.");

        var bitmapBytes = ReadBitmapBytes();
        try
        {
            long runStart = -1;
            long runLength = 0;
            long totalBits = bitmapBytes.Length * 8L;

            for (long bitIndex = 0; bitIndex < totalBits; bitIndex++)
            {
                int byteIndex = (int)(bitIndex >> 3);
                int bitOffset = (int)(bitIndex & 7);
                bool isAllocated = (bitmapBytes[byteIndex] & (1 << bitOffset)) != 0;

                if (!isAllocated)
                {
                    if (runLength == 0)
                        runStart = bitIndex;
                    runLength++;

                    if (runLength >= requiredBlocks)
                        return new FreeBlockRange(runStart, runLength);
                }
                else
                {
                    runLength = 0;
                }
            }

            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bitmapBytes);
        }
    }

    /// <summary>
    /// Finds all contiguous free block ranges with at least <paramref name="minimumBlocks"/> blocks.
    /// Useful for diagnostic and reporting purposes.
    /// </summary>
    /// <param name="minimumBlocks">Minimum number of contiguous free blocks per range (default 1).</param>
    /// <returns>All free block ranges meeting the minimum size requirement.</returns>
    public IReadOnlyList<FreeBlockRange> FindAllFreeRanges(long minimumBlocks = 1)
    {
        if (minimumBlocks < 1)
            throw new ArgumentOutOfRangeException(nameof(minimumBlocks), "Minimum blocks must be at least 1.");

        var bitmapBytes = ReadBitmapBytes();
        try
        {
            var ranges = new List<FreeBlockRange>();
            long runStart = -1;
            long runLength = 0;
            long totalBits = bitmapBytes.Length * 8L;

            for (long bitIndex = 0; bitIndex < totalBits; bitIndex++)
            {
                int byteIndex = (int)(bitIndex >> 3);
                int bitOffset = (int)(bitIndex & 7);
                bool isAllocated = (bitmapBytes[byteIndex] & (1 << bitOffset)) != 0;

                if (!isAllocated)
                {
                    if (runLength == 0)
                        runStart = bitIndex;
                    runLength++;
                }
                else
                {
                    if (runLength >= minimumBlocks)
                        ranges.Add(new FreeBlockRange(runStart, runLength));
                    runLength = 0;
                }
            }

            // Handle trailing run
            if (runLength >= minimumBlocks)
                ranges.Add(new FreeBlockRange(runStart, runLength));

            return ranges;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bitmapBytes);
        }
    }

    /// <summary>
    /// Returns the total count of free (unallocated) blocks in the allocation bitmap.
    /// </summary>
    /// <returns>Number of free blocks.</returns>
    public long GetTotalFreeBlocks()
    {
        var bitmapBytes = ReadBitmapBytes();
        try
        {
            long freeCount = 0;
            long totalBits = bitmapBytes.Length * 8L;

            for (long bitIndex = 0; bitIndex < totalBits; bitIndex++)
            {
                int byteIndex = (int)(bitIndex >> 3);
                int bitOffset = (int)(bitIndex & 7);
                bool isAllocated = (bitmapBytes[byteIndex] & (1 << bitOffset)) != 0;

                if (!isAllocated)
                    freeCount++;
            }

            return freeCount;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bitmapBytes);
        }
    }

    /// <summary>
    /// Reads the raw bitmap bytes from the VDE stream. Returns a rented buffer
    /// that the caller must return to <see cref="ArrayPool{T}.Shared"/>.
    /// The usable length is the payload size (excluding block trailers).
    /// </summary>
    private byte[] ReadBitmapBytes()
    {
        int payloadPerBlock = _blockSize - FormatConstants.UniversalBlockTrailerSize;
        int totalPayload = checked((int)(_bitmapBlockCount * payloadPerBlock));
        var buffer = ArrayPool<byte>.Shared.Rent(totalPayload);

        // Clear rented buffer to avoid stale data beyond actual read
        Array.Clear(buffer, 0, buffer.Length);

        var readBuffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            int payloadOffset = 0;
            for (long blockIdx = 0; blockIdx < _bitmapBlockCount; blockIdx++)
            {
                long byteOffset = (_bitmapStartBlock + blockIdx) * _blockSize;
                _vdeStream.Seek(byteOffset, SeekOrigin.Begin);

                int totalRead = 0;
                while (totalRead < _blockSize)
                {
                    int bytesRead = _vdeStream.Read(readBuffer, totalRead, _blockSize - totalRead);
                    if (bytesRead == 0)
                        throw new InvalidDataException(
                            $"Unexpected end of stream reading bitmap block {_bitmapStartBlock + blockIdx}.");
                    totalRead += bytesRead;
                }

                // Copy payload (excluding trailer) into the output buffer
                Array.Copy(readBuffer, 0, buffer, payloadOffset, payloadPerBlock);
                payloadOffset += payloadPerBlock;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }

        return buffer;
    }
}
