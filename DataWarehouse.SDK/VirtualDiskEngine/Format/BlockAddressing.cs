using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Static utility class for VDE v2.0 block-to-byte address calculations.
/// All methods use checked arithmetic to detect overflow.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 block addressing utilities (VDE2-03)")]
public static class BlockAddressing
{
    /// <summary>
    /// Converts a block number to its byte offset within the VDE file.
    /// </summary>
    /// <param name="blockNumber">The block number (zero-based).</param>
    /// <param name="blockSize">The block size in bytes.</param>
    /// <returns>The byte offset of the block.</returns>
    public static long BlockToByteOffset(long blockNumber, int blockSize)
    {
        if (blockNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(blockNumber), "Block number must be non-negative.");
        if (!IsValidBlockSize(blockSize))
            throw new ArgumentOutOfRangeException(nameof(blockSize), $"Block size must be a power of 2 between {FormatConstants.MinBlockSize} and {FormatConstants.MaxBlockSize}.");

        return checked(blockNumber * blockSize);
    }

    /// <summary>
    /// Converts a byte offset to its block number within the VDE file.
    /// </summary>
    /// <param name="byteOffset">The byte offset (must be block-aligned).</param>
    /// <param name="blockSize">The block size in bytes.</param>
    /// <returns>The block number.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="byteOffset"/> is not aligned to <paramref name="blockSize"/>.</exception>
    public static long ByteOffsetToBlock(long byteOffset, int blockSize)
    {
        if (byteOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(byteOffset), "Byte offset must be non-negative.");
        if (!IsValidBlockSize(blockSize))
            throw new ArgumentOutOfRangeException(nameof(blockSize), $"Block size must be a power of 2 between {FormatConstants.MinBlockSize} and {FormatConstants.MaxBlockSize}.");
        // P2-812: Enforce block alignment — silently truncating a non-aligned offset would return
        // the wrong block number and corrupt callers that rely on the documented contract.
        if ((byteOffset & (blockSize - 1)) != 0)
            throw new ArgumentException($"Byte offset {byteOffset} is not aligned to block size {blockSize}.", nameof(byteOffset));

        return byteOffset / blockSize;
    }

    /// <summary>
    /// Returns the usable payload size per block (block size minus Universal Block Trailer).
    /// </summary>
    /// <param name="blockSize">The block size in bytes.</param>
    /// <returns>The number of usable payload bytes per block.</returns>
    public static int UsablePayloadSize(int blockSize)
    {
        if (!IsValidBlockSize(blockSize))
            throw new ArgumentOutOfRangeException(nameof(blockSize), $"Block size must be a power of 2 between {FormatConstants.MinBlockSize} and {FormatConstants.MaxBlockSize}.");

        return blockSize - FormatConstants.UniversalBlockTrailerSize;
    }

    /// <summary>
    /// Calculates the total file size for a given number of blocks.
    /// </summary>
    /// <param name="totalBlocks">The total number of blocks.</param>
    /// <param name="blockSize">The block size in bytes.</param>
    /// <returns>The total file size in bytes.</returns>
    public static long CalculateTotalFileSize(long totalBlocks, int blockSize)
    {
        if (totalBlocks < 0)
            throw new ArgumentOutOfRangeException(nameof(totalBlocks), "Total blocks must be non-negative.");
        if (!IsValidBlockSize(blockSize))
            throw new ArgumentOutOfRangeException(nameof(blockSize), $"Block size must be a power of 2 between {FormatConstants.MinBlockSize} and {FormatConstants.MaxBlockSize}.");

        return checked(totalBlocks * blockSize);
    }

    /// <summary>
    /// Calculates the number of blocks that fit in a given file size.
    /// </summary>
    /// <param name="fileSize">The file size in bytes.</param>
    /// <param name="blockSize">The block size in bytes.</param>
    /// <returns>The number of whole blocks.</returns>
    public static long CalculateBlockCount(long fileSize, int blockSize)
    {
        if (fileSize < 0)
            throw new ArgumentOutOfRangeException(nameof(fileSize), "File size must be non-negative.");
        if (!IsValidBlockSize(blockSize))
            throw new ArgumentOutOfRangeException(nameof(blockSize), $"Block size must be a power of 2 between {FormatConstants.MinBlockSize} and {FormatConstants.MaxBlockSize}.");

        return fileSize / blockSize;
    }

    /// <summary>
    /// Checks whether a block size is valid: must be a power of 2 between
    /// <see cref="FormatConstants.MinBlockSize"/> and <see cref="FormatConstants.MaxBlockSize"/>.
    /// </summary>
    /// <param name="blockSize">The block size to validate.</param>
    /// <returns>True if the block size is valid.</returns>
    public static bool IsValidBlockSize(int blockSize)
    {
        return blockSize >= FormatConstants.MinBlockSize
            && blockSize <= FormatConstants.MaxBlockSize
            && (blockSize & (blockSize - 1)) == 0;
    }

    /// <summary>
    /// Returns the byte size of a superblock group (4 blocks).
    /// </summary>
    /// <param name="blockSize">The block size in bytes.</param>
    /// <returns>The superblock group size in bytes.</returns>
    public static int SuperblockGroupByteSize(int blockSize)
    {
        if (!IsValidBlockSize(blockSize))
            throw new ArgumentOutOfRangeException(nameof(blockSize), $"Block size must be a power of 2 between {FormatConstants.MinBlockSize} and {FormatConstants.MaxBlockSize}.");

        return checked(FormatConstants.SuperblockGroupBlocks * blockSize);
    }

    /// <summary>
    /// Returns the byte offset of the superblock mirror group (starts at block 4).
    /// </summary>
    /// <param name="blockSize">The block size in bytes.</param>
    /// <returns>The byte offset of the mirror group.</returns>
    public static long MirrorGroupStartOffset(int blockSize)
    {
        if (!IsValidBlockSize(blockSize))
            throw new ArgumentOutOfRangeException(nameof(blockSize), $"Block size must be a power of 2 between {FormatConstants.MinBlockSize} and {FormatConstants.MaxBlockSize}.");

        return checked((long)FormatConstants.SuperblockMirrorStartBlock * blockSize);
    }

    /// <summary>
    /// Returns the byte offset of the Region Directory (starts at block 8).
    /// </summary>
    /// <param name="blockSize">The block size in bytes.</param>
    /// <returns>The byte offset of the region directory.</returns>
    public static long RegionDirectoryStartOffset(int blockSize)
    {
        if (!IsValidBlockSize(blockSize))
            throw new ArgumentOutOfRangeException(nameof(blockSize), $"Block size must be a power of 2 between {FormatConstants.MinBlockSize} and {FormatConstants.MaxBlockSize}.");

        return checked(FormatConstants.RegionDirectoryStartBlock * blockSize);
    }

    /// <summary>
    /// Returns the byte size of the Region Directory (2 blocks).
    /// </summary>
    /// <param name="blockSize">The block size in bytes.</param>
    /// <returns>The region directory size in bytes.</returns>
    public static long RegionDirectoryByteSize(int blockSize)
    {
        if (!IsValidBlockSize(blockSize))
            throw new ArgumentOutOfRangeException(nameof(blockSize), $"Block size must be a power of 2 between {FormatConstants.MinBlockSize} and {FormatConstants.MaxBlockSize}.");

        return checked((long)FormatConstants.RegionDirectoryBlocks * blockSize);
    }

    /// <summary>
    /// Returns the minimum block number where data regions can begin.
    /// After: SB group (0-3) + SB mirror (4-7) + Region Dir (8-9) +
    /// Policy Vault header (10-11) + Encryption header (12-13) = block 14.
    /// </summary>
    /// <returns>The first data region block number (14).</returns>
    public static long FirstDataRegionMinBlock() => 14;
}
