using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Observability;

/// <summary>
/// Well-known MLOG metric ID constants as defined in the DWVD v2.0 spec (VOPT-68).
/// These IDs correspond to spec table entries 0x0001-0x0041.
/// </summary>
/// <remarks>
/// Int64 metric IDs (ReadOps, WriteOps, ReadBytes, WriteBytes, ReadLatencyP50,
/// ReadLatencyP99, WriteLatencyP50, WriteLatencyP99, FreeBlocks, ReplicationLag,
/// DirtyBlocks, TamperEvents, ErrorCount) are stored as raw int64 LE.
///
/// Float64 metric IDs (FragmentationRatio, InodeUtilization, CacheHitRatio,
/// CompressionRatio) are stored as float64 LE (IEEE 754 double-precision).
/// Use <see cref="IsFloat64Metric"/> to determine encoding.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: MLOG observability metric IDs (VOPT-68)")]
public static class WellKnownMetricId
{
    // ── I/O Counters ──────────────────────────────────────────────────────

    /// <summary>Total read operations (int64).</summary>
    public const ushort ReadOps = 0x0001;

    /// <summary>Total write operations (int64).</summary>
    public const ushort WriteOps = 0x0002;

    /// <summary>Total bytes read (int64).</summary>
    public const ushort ReadBytes = 0x0003;

    /// <summary>Total bytes written (int64).</summary>
    public const ushort WriteBytes = 0x0004;

    // ── Latency Percentiles ───────────────────────────────────────────────

    /// <summary>Read latency P50 in microseconds (int64).</summary>
    public const ushort ReadLatencyP50 = 0x0005;

    /// <summary>Read latency P99 in microseconds (int64).</summary>
    public const ushort ReadLatencyP99 = 0x0006;

    /// <summary>Write latency P50 in microseconds (int64).</summary>
    public const ushort WriteLatencyP50 = 0x0007;

    /// <summary>Write latency P99 in microseconds (int64).</summary>
    public const ushort WriteLatencyP99 = 0x0008;

    // ── Space & Fragmentation ─────────────────────────────────────────────

    /// <summary>Free block count (int64).</summary>
    public const ushort FreeBlocks = 0x0010;

    /// <summary>Fragmentation ratio 0.0-1.0 (float64).</summary>
    public const ushort FragmentationRatio = 0x0011;

    /// <summary>Inode utilization ratio 0.0-1.0 (float64).</summary>
    public const ushort InodeUtilization = 0x0012;

    // ── Cache ─────────────────────────────────────────────────────────────

    /// <summary>Cache hit ratio 0.0-1.0 (float64).</summary>
    public const ushort CacheHitRatio = 0x0020;

    /// <summary>Compression ratio (compressed/original) 0.0-1.0 (float64).</summary>
    public const ushort CompressionRatio = 0x0021;

    // ── Replication ───────────────────────────────────────────────────────

    /// <summary>Replication lag in milliseconds (int64).</summary>
    public const ushort ReplicationLag = 0x0030;

    /// <summary>Dirty block count pending flush (int64).</summary>
    public const ushort DirtyBlocks = 0x0031;

    // ── Security / Integrity ──────────────────────────────────────────────

    /// <summary>Detected tamper events count (int64).</summary>
    public const ushort TamperEvents = 0x0040;

    /// <summary>Cumulative error count (int64).</summary>
    public const ushort ErrorCount = 0x0041;

    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> if the metric value is stored as a float64 (IEEE 754 double)
    /// rather than a raw int64. Float64 metrics are: FragmentationRatio (0x0011),
    /// InodeUtilization (0x0012), CacheHitRatio (0x0020), CompressionRatio (0x0021).
    /// </summary>
    /// <param name="id">The metric ID to classify.</param>
    public static bool IsFloat64Metric(ushort id) =>
        id == FragmentationRatio
        || id == InodeUtilization
        || id == CacheHitRatio
        || id == CompressionRatio;
}
