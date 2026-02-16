using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.VirtualDiskEngine;

/// <summary>
/// Configuration options for the Virtual Disk Engine.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: VDE engine facade (VDE-07)")]
public sealed record VdeOptions
{
    /// <summary>
    /// Path to the container file (.dwvd).
    /// </summary>
    public required string ContainerPath { get; init; }

    /// <summary>
    /// Block size in bytes. Must be a power of 2 between 512 and 65536.
    /// Default: 4096 (4 KB).
    /// </summary>
    public int BlockSize { get; init; } = 4096;

    /// <summary>
    /// Total number of blocks in the container.
    /// Default: 1,048,576 (4 GB with 4K blocks).
    /// </summary>
    public long TotalBlocks { get; init; } = 1_048_576;

    /// <summary>
    /// Percentage of total blocks reserved for the WAL.
    /// Must be at least 1% or 256 blocks, whichever is larger.
    /// Default: 1%.
    /// </summary>
    public int WalSizePercent { get; init; } = 1;

    /// <summary>
    /// Maximum number of inodes to cache in memory.
    /// Default: 10,000.
    /// </summary>
    public int MaxCachedInodes { get; init; } = 10_000;

    /// <summary>
    /// Maximum number of B-Tree nodes to cache in memory.
    /// Default: 1,000.
    /// </summary>
    public int MaxCachedBTreeNodes { get; init; } = 1_000;

    /// <summary>
    /// Maximum number of checksum blocks to cache in memory.
    /// Default: 256.
    /// </summary>
    public int MaxCachedChecksumBlocks { get; init; } = 256;

    /// <summary>
    /// WAL utilization percentage that triggers an automatic checkpoint.
    /// Default: 75%.
    /// </summary>
    public int CheckpointWalUtilizationPercent { get; init; } = 75;

    /// <summary>
    /// Whether to enable checksum verification on block reads.
    /// Default: true.
    /// </summary>
    public bool EnableChecksumVerification { get; init; } = true;

    /// <summary>
    /// Whether to automatically create the container if it doesn't exist.
    /// Default: true.
    /// </summary>
    public bool AutoCreateContainer { get; init; } = true;

    /// <summary>
    /// Validates the configuration options.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown if any option is invalid.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ContainerPath))
        {
            throw new ArgumentException("ContainerPath cannot be null or empty.", nameof(ContainerPath));
        }

        if (BlockSize < 512 || BlockSize > 65536)
        {
            throw new ArgumentException($"BlockSize must be between 512 and 65536. Got: {BlockSize}", nameof(BlockSize));
        }

        if (!IsPowerOfTwo(BlockSize))
        {
            throw new ArgumentException($"BlockSize must be a power of 2. Got: {BlockSize}", nameof(BlockSize));
        }

        if (TotalBlocks <= 0)
        {
            throw new ArgumentException($"TotalBlocks must be positive. Got: {TotalBlocks}", nameof(TotalBlocks));
        }

        if (WalSizePercent < 1 || WalSizePercent > 50)
        {
            throw new ArgumentException($"WalSizePercent must be between 1 and 50. Got: {WalSizePercent}", nameof(WalSizePercent));
        }

        if (MaxCachedInodes < 0)
        {
            throw new ArgumentException($"MaxCachedInodes cannot be negative. Got: {MaxCachedInodes}", nameof(MaxCachedInodes));
        }

        if (MaxCachedBTreeNodes < 0)
        {
            throw new ArgumentException($"MaxCachedBTreeNodes cannot be negative. Got: {MaxCachedBTreeNodes}", nameof(MaxCachedBTreeNodes));
        }

        if (MaxCachedChecksumBlocks < 0)
        {
            throw new ArgumentException($"MaxCachedChecksumBlocks cannot be negative. Got: {MaxCachedChecksumBlocks}", nameof(MaxCachedChecksumBlocks));
        }

        if (CheckpointWalUtilizationPercent < 1 || CheckpointWalUtilizationPercent > 100)
        {
            throw new ArgumentException($"CheckpointWalUtilizationPercent must be between 1 and 100. Got: {CheckpointWalUtilizationPercent}", nameof(CheckpointWalUtilizationPercent));
        }
    }

    /// <summary>
    /// Checks if a number is a power of 2.
    /// </summary>
    private static bool IsPowerOfTwo(int n)
    {
        return n > 0 && (n & (n - 1)) == 0;
    }
}
