using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Metadata;

/// <summary>
/// Configuration for cold analytics scans of VDE metadata regions.
/// Controls which metadata regions are scanned and how many inodes are processed per batch.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-45 metadata-only cold analytics")]
public sealed class ColdAnalyticsConfig
{
    /// <summary>
    /// Number of inodes to process per read batch.
    /// A 4 KiB block holds 8 x 512-byte inodes; multiples of 8 are most efficient.
    /// Default: 256 inodes (32 blocks).
    /// </summary>
    public int BatchSize { get; set; } = 256;

    /// <summary>
    /// Whether to scan the inode table for file/directory statistics.
    /// Default: true.
    /// </summary>
    public bool ScanInodes { get; set; } = true;

    /// <summary>
    /// Whether to scan TRLR (Universal Block Trailer) blocks for integrity statistics.
    /// Default: true.
    /// </summary>
    public bool ScanTrailers { get; set; } = true;

    /// <summary>
    /// Whether to scan inline tag areas within inode blocks for tag analytics.
    /// Default: true.
    /// </summary>
    public bool ScanTags { get; set; } = true;

    /// <summary>
    /// Maximum number of inodes to process during a single scan pass.
    /// Set to a value less than <see cref="long.MaxValue"/> for incremental scanning.
    /// Default: <see cref="long.MaxValue"/> (scan all inodes).
    /// </summary>
    public long MaxInodesPerScan { get; set; } = long.MaxValue;
}
