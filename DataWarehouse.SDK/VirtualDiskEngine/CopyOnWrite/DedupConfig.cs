using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.CopyOnWrite;

/// <summary>
/// Configuration for the content-addressable deduplication engine.
/// Controls thresholds, resource limits, and verification policies.
/// </summary>
/// <remarks>
/// All properties have production-safe defaults. Increase <see cref="MaxConcurrentScans"/>
/// and <see cref="MaxBytesPerPass"/> for faster deduplication on well-provisioned hosts;
/// reduce them on resource-constrained edge or embedded systems.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87-30: Content-addressable dedup config (VOPT-46+59)")]
public sealed class DedupConfig
{
    /// <summary>
    /// Minimum extent block count before deduplication is attempted.
    /// Extents smaller than this threshold are skipped.
    /// Default: 4 (skip extents below 16 KB at 4 KB block size).
    /// </summary>
    public int MinExtentBlockCount { get; set; } = 4;

    /// <summary>
    /// Maximum number of concurrent deduplication I/O scans.
    /// Limits parallelism to avoid saturating the block device.
    /// Default: 2.
    /// </summary>
    public int MaxConcurrentScans { get; set; } = 2;

    /// <summary>
    /// Maximum bytes to process per deduplication pass.
    /// Helps bound memory and I/O pressure for large volumes.
    /// Default: 1 GB.
    /// </summary>
    public long MaxBytesPerPass { get; set; } = 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// When <see langword="true"/>, performs a byte-level comparison of candidate extents
    /// before sharing their physical blocks, guarding against BLAKE3 hash collisions.
    /// Slightly slower but guarantees correctness even under theoretical hash collision.
    /// Default: <see langword="true"/>.
    /// </summary>
    public bool VerifyBeforeShare { get; set; } = true;

    /// <summary>
    /// Interval between background deduplication passes.
    /// Default: 30 minutes.
    /// </summary>
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromMinutes(30);
}
