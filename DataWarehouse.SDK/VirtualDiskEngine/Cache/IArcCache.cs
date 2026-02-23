using DataWarehouse.SDK.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Cache;

/// <summary>
/// Statistics for an ARC cache instance.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: 3-tier ARC cache statistics (VOPT-03)")]
public readonly struct ArcCacheStats
{
    /// <summary>Total cache hits.</summary>
    public long Hits { get; init; }

    /// <summary>Total cache misses.</summary>
    public long Misses { get; init; }

    /// <summary>Total evictions performed.</summary>
    public long Evictions { get; init; }

    /// <summary>Current size of T1 (recent) list.</summary>
    public long T1Size { get; init; }

    /// <summary>Current size of T2 (frequent) list.</summary>
    public long T2Size { get; init; }

    /// <summary>Current size of B1 (ghost recent) list.</summary>
    public long B1Size { get; init; }

    /// <summary>Current size of B2 (ghost frequent) list.</summary>
    public long B2Size { get; init; }

    /// <summary>Hit ratio (Hits / (Hits + Misses)), or 0 if no requests.</summary>
    public double HitRatio
    {
        get
        {
            long total = Hits + Misses;
            return total == 0 ? 0.0 : (double)Hits / total;
        }
    }
}

/// <summary>
/// Interface for Adaptive Replacement Cache tiers.
/// Provides block-level caching with Get/Put/Evict/Stats operations.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: ARC cache interface (VOPT-03/04/05)")]
public interface IArcCache
{
    /// <summary>
    /// Gets a cached block by block number, or null if not cached.
    /// </summary>
    ValueTask<byte[]?> GetAsync(long blockNumber, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates a block in the cache.
    /// </summary>
    ValueTask PutAsync(long blockNumber, ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>
    /// Removes a specific block from the cache.
    /// </summary>
    void Evict(long blockNumber);

    /// <summary>
    /// Flushes the entire cache.
    /// </summary>
    void Clear();

    /// <summary>
    /// Returns current cache statistics including hit/miss/eviction counts.
    /// </summary>
    ArcCacheStats GetStats();

    /// <summary>
    /// Maximum number of entries this cache can hold.
    /// </summary>
    long Capacity { get; }

    /// <summary>
    /// Current number of entries in the cache.
    /// </summary>
    long Count { get; }
}
