using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Integrity;

/// <summary>
/// Configuration for the <see cref="ExtentIntegrityCache"/>.
/// Controls the maximum number of cached entries, TTL, memory trade-offs,
/// and eviction batch size.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Extent integrity cache configuration (VOPT-52)")]
public sealed class IntegrityCacheConfig
{
    /// <summary>
    /// Maximum number of cached extent verification results.
    /// When the cache is full the oldest entries are evicted in batches.
    /// Default: 65,536 entries.
    /// </summary>
    public int MaxEntries { get; set; } = 65_536;

    /// <summary>
    /// Maximum lifetime of a cache entry regardless of whether the generation
    /// number is still current. Protects against stale data accumulating after
    /// very long idle periods.
    /// Default: 30 minutes.
    /// </summary>
    public TimeSpan EntryTtl { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// When <see langword="true"/>, also caches the per-block pass/fail results
    /// inside each entry. This uses more memory but enables finer-grained
    /// diagnostics without re-reading the block device.
    /// Default: <see langword="false"/>.
    /// </summary>
    public bool TrackBlockLevelResults { get; set; } = false;

    /// <summary>
    /// Number of LRU candidates dequeued and removed from the cache in a
    /// single eviction sweep when <see cref="MaxEntries"/> is exceeded.
    /// Default: 256.
    /// </summary>
    public int EvictionBatchSize { get; set; } = 256;
}
