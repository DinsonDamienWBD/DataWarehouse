using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Edge.Memory;

/// <summary>
/// Configuration for bounded memory runtime.
/// </summary>
/// <remarks>
/// Controls memory ceiling, ArrayPool sizing, and GC pressure thresholds for resource-constrained
/// edge devices (64MB-256MB typical). Enables DataWarehouse to run predictably on embedded systems.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Memory settings (EDGE-06)")]
public sealed record MemorySettings
{
    /// <summary>
    /// Maximum memory usage in bytes. Default: 128MB.
    /// When exceeded, operations are rejected with OutOfMemoryException.
    /// </summary>
    public long MemoryCeiling { get; init; } = 128 * 1024 * 1024; // 128MB default

    /// <summary>
    /// Maximum array size for ArrayPool&lt;byte&gt;. Default: 1MB.
    /// Arrays larger than this are allocated directly (not pooled).
    /// </summary>
    public int ArrayPoolMaxArraySize { get; init; } = 1024 * 1024; // 1MB

    /// <summary>
    /// GC pressure threshold as ratio of ceiling (0.0-1.0). Default: 0.85 (85%).
    /// When memory usage exceeds this threshold, proactive Gen1 GC is triggered.
    /// </summary>
    public double GcPressureThreshold { get; init; } = 0.85; // 85% of ceiling

    /// <summary>
    /// Whether bounded memory mode is enabled. Default: false (opt-in).
    /// When disabled, memory tracking is bypassed for zero overhead.
    /// </summary>
    public bool Enabled { get; init; } = false;
}
