using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.VirtualDiskEngine.Container;

/// <summary>
/// Static helper for computing DWVD container layout.
/// Determines where each region (superblocks, bitmap, inode table, WAL, data) starts within the container.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: Virtual Disk Engine (VDE-01/VDE-04)")]
public static class ContainerFormat
{
    /// <summary>
    /// Computes the block layout for a container with the specified parameters.
    /// </summary>
    /// <param name="blockSize">Size of each block in bytes.</param>
    /// <param name="totalBlocks">Total number of blocks in the container.</param>
    /// <returns>Layout information describing where each region is located.</returns>
    public static ContainerLayout ComputeLayout(int blockSize, long totalBlocks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, VdeConstants.MinBlockSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockSize, VdeConstants.MaxBlockSize);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(totalBlocks, 0L);

        // Block 0: Primary superblock
        // Block 1: Mirror superblock
        long currentBlock = 2;

        // Bitmap region: 1 bit per data block, rounded up to block boundaries
        // We'll compute data blocks first as an estimate, then iterate to converge
        long bitmapStartBlock = currentBlock;
        long estimatedDataBlocks = totalBlocks - currentBlock - 256; // Reserve space for other regions
        long bitmapBlockCount = ComputeBitmapBlockCount(estimatedDataBlocks, blockSize);
        currentBlock += bitmapBlockCount;

        // Inode table: Fixed size, 1024 blocks initially (expandable later)
        long inodeTableBlock = currentBlock;
        long inodeTableBlockCount = 1024;
        currentBlock += inodeTableBlockCount;

        // WAL: 1% of total blocks, minimum 256 blocks
        long walBlockCount = Math.Max(256, totalBlocks / 100);
        long walStartBlock = currentBlock;
        currentBlock += walBlockCount;

        // Checksum table: 8 bytes per data block
        // We'll compute this after we know the final data block count
        long checksumTableBlock = currentBlock;
        long estimatedChecksumBlocks = (estimatedDataBlocks * 8 + blockSize - 1) / blockSize;
        currentBlock += estimatedChecksumBlocks;

        // B-Tree root: Reserve 1 block initially
        long bTreeRootBlock = currentBlock;
        currentBlock += 1;

        // Data region: everything that remains
        long dataStartBlock = currentBlock;
        long actualDataBlocks = totalBlocks - dataStartBlock;

        if (actualDataBlocks <= 0)
        {
            throw new ArgumentException($"Container is too small: {totalBlocks} blocks is insufficient for the required metadata regions.");
        }

        // Recompute bitmap and checksum table based on actual data block count
        bitmapBlockCount = ComputeBitmapBlockCount(actualDataBlocks, blockSize);
        long checksumBlockCount = (actualDataBlocks * 8 + blockSize - 1) / blockSize;

        return new ContainerLayout
        {
            BlockSize = blockSize,
            TotalBlocks = totalBlocks,
            PrimarySuperblockBlock = 0,
            MirrorSuperblockBlock = 1,
            BitmapStartBlock = bitmapStartBlock,
            BitmapBlockCount = bitmapBlockCount,
            InodeTableBlock = inodeTableBlock,
            InodeTableBlockCount = inodeTableBlockCount,
            WalStartBlock = walStartBlock,
            WalBlockCount = walBlockCount,
            ChecksumTableBlock = checksumTableBlock,
            ChecksumTableBlockCount = checksumBlockCount,
            BTreeRootBlock = bTreeRootBlock,
            DataStartBlock = dataStartBlock,
            DataBlockCount = actualDataBlocks
        };
    }

    /// <summary>
    /// Computes the number of blocks required to store a bitmap for the given number of data blocks.
    /// </summary>
    /// <param name="dataBlocks">Number of data blocks to track.</param>
    /// <param name="blockSize">Size of each block in bytes.</param>
    /// <returns>Number of blocks required for the bitmap.</returns>
    private static long ComputeBitmapBlockCount(long dataBlocks, int blockSize)
    {
        // Each block can store blockSize * 8 bits (one bit per data block)
        long bitsPerBlock = blockSize * 8L;
        return (dataBlocks + bitsPerBlock - 1) / bitsPerBlock;
    }
}

/// <summary>
/// Describes the block layout of a DWVD container.
/// All offsets are in block numbers.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: Virtual Disk Engine (VDE-01/VDE-04)")]
public record ContainerLayout
{
    /// <summary>Block size in bytes.</summary>
    public required int BlockSize { get; init; }

    /// <summary>Total number of blocks in the container.</summary>
    public required long TotalBlocks { get; init; }

    /// <summary>Block number of the primary superblock (always 0).</summary>
    public required long PrimarySuperblockBlock { get; init; }

    /// <summary>Block number of the mirror superblock (always 1).</summary>
    public required long MirrorSuperblockBlock { get; init; }

    /// <summary>Block number where the free space bitmap starts.</summary>
    public required long BitmapStartBlock { get; init; }

    /// <summary>Number of blocks occupied by the bitmap.</summary>
    public required long BitmapBlockCount { get; init; }

    /// <summary>Block number where the inode table starts.</summary>
    public required long InodeTableBlock { get; init; }

    /// <summary>Number of blocks reserved for the inode table.</summary>
    public required long InodeTableBlockCount { get; init; }

    /// <summary>Block number where the Write-Ahead Log starts.</summary>
    public required long WalStartBlock { get; init; }

    /// <summary>Number of blocks reserved for the WAL.</summary>
    public required long WalBlockCount { get; init; }

    /// <summary>Block number of the checksum table.</summary>
    public required long ChecksumTableBlock { get; init; }

    /// <summary>Number of blocks occupied by the checksum table.</summary>
    public required long ChecksumTableBlockCount { get; init; }

    /// <summary>Block number of the B-Tree root node.</summary>
    public required long BTreeRootBlock { get; init; }

    /// <summary>Block number where the data region starts.</summary>
    public required long DataStartBlock { get; init; }

    /// <summary>Number of blocks available for data storage.</summary>
    public required long DataBlockCount { get; init; }
}
