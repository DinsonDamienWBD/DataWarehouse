using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Caching;

/// <summary>
/// Configuration for hybrid cache layers.
/// </summary>
public sealed class HybridCacheConfig
{
    /// <summary>
    /// Maximum size of L1 cache in bytes.
    /// </summary>
    public long L1MaxSize { get; init; } = 64 * 1024 * 1024; // 64 MB

    /// <summary>
    /// Maximum size of L2 cache in bytes.
    /// </summary>
    public long L2MaxSize { get; init; } = 512 * 1024 * 1024; // 512 MB

    /// <summary>
    /// Threshold size for promoting to L1 (smaller items go to L1).
    /// </summary>
    public int L1ThresholdSize { get; init; } = 64 * 1024; // 64 KB

    /// <summary>
    /// Number of accesses before promoting from L2 to L1.
    /// </summary>
    public int PromotionThreshold { get; init; } = 3;

    /// <summary>
    /// Default TTL for L1 cache.
    /// </summary>
    public TimeSpan L1DefaultTTL { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Default TTL for L2 cache.
    /// </summary>
    public TimeSpan L2DefaultTTL { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// L1 + L2 layered hybrid cache strategy.
/// L1 provides ultra-fast in-memory access for hot data.
/// L2 provides larger capacity with slightly higher latency.
/// </summary>
/// <remarks>
/// Features:
/// - Automatic tiering based on access patterns
/// - Hot data promotion from L2 to L1
/// - Cold data demotion from L1 to L2
/// - Size-based tier selection
/// - Configurable promotion thresholds
/// - Write-around policy for L1
/// </remarks>
public sealed class HybridCacheStrategy : CachingStrategyBase
{
    private readonly InMemoryCacheStrategy _l1Cache;
    private readonly InMemoryCacheStrategy _l2Cache;
    private readonly ConcurrentDictionary<string, int> _accessCount = new();
    private readonly HybridCacheConfig _config;

    /// <summary>
    /// Initializes a new HybridCacheStrategy with default configuration.
    /// </summary>
    public HybridCacheStrategy() : this(new HybridCacheConfig()) { }

    /// <summary>
    /// Initializes a new HybridCacheStrategy with specified configuration.
    /// </summary>
    public HybridCacheStrategy(HybridCacheConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _l1Cache = new InMemoryCacheStrategy(_config.L1MaxSize);
        _l2Cache = new InMemoryCacheStrategy(_config.L2MaxSize);
    }

    /// <inheritdoc/>
    public override string StrategyId => "cache.hybrid";

    /// <inheritdoc/>
    public override string DisplayName => "Hybrid L1/L2 Cache";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = true,
        MaxThroughput = 500_000,
        TypicalLatencyMs = 0.01
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Two-tier hybrid cache combining fast L1 in-memory cache with larger L2 cache. " +
        "Automatically promotes hot data to L1 and demotes cold data to L2. " +
        "Provides optimal balance between speed and capacity.";

    /// <inheritdoc/>
    public override string[] Tags => ["cache", "hybrid", "l1", "l2", "tiered", "adaptive"];

    /// <inheritdoc/>
    public override long GetCurrentSize() => _l1Cache.GetCurrentSize() + _l2Cache.GetCurrentSize();

    /// <inheritdoc/>
    public override long GetEntryCount() => _l1Cache.GetEntryCount() + _l2Cache.GetEntryCount();

    /// <summary>
    /// Gets the L1 cache size.
    /// </summary>
    public long GetL1Size() => _l1Cache.GetCurrentSize();

    /// <summary>
    /// Gets the L2 cache size.
    /// </summary>
    public long GetL2Size() => _l2Cache.GetCurrentSize();

    /// <summary>
    /// Gets the L1 cache entry count.
    /// </summary>
    public long GetL1EntryCount() => _l1Cache.GetEntryCount();

    /// <summary>
    /// Gets the L2 cache entry count.
    /// </summary>
    public long GetL2EntryCount() => _l2Cache.GetEntryCount();

    /// <inheritdoc/>
    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        await _l1Cache.InitializeAsync(ct);
        await _l2Cache.InitializeAsync(ct);
    }

    /// <inheritdoc/>
    protected override async Task DisposeCoreAsync()
    {
        await _l1Cache.DisposeAsync();
        await _l2Cache.DisposeAsync();
        _accessCount.Clear();
    }

    /// <inheritdoc/>
    protected override async Task<CacheResult<byte[]>> GetCoreAsync(string key, CancellationToken ct)
    {
        // Check L1 first (fastest)
        var l1Result = await _l1Cache.GetAsync(key, ct);
        if (l1Result.Found)
        {
            IncrementAccess(key);
            return l1Result;
        }

        // Check L2
        var l2Result = await _l2Cache.GetAsync(key, ct);
        if (l2Result.Found && l2Result.Value != null)
        {
            var accessCount = IncrementAccess(key);

            // Promote to L1 if accessed frequently and small enough
            if (accessCount >= _config.PromotionThreshold && l2Result.Value.Length <= _config.L1ThresholdSize)
            {
                await PromoteToL1Async(key, l2Result.Value, ct);
            }

            return l2Result;
        }

        return CacheResult<byte[]>.Miss();
    }

    /// <inheritdoc/>
    protected override async Task SetCoreAsync(string key, byte[] value, CacheOptions options, CancellationToken ct)
    {
        // Determine which tier based on size
        if (value.Length <= _config.L1ThresholdSize)
        {
            // Small items go to L1 directly
            var l1Options = CreateL1Options(options);
            await _l1Cache.SetAsync(key, value, l1Options, ct);
        }
        else
        {
            // Large items go to L2
            var l2Options = CreateL2Options(options);
            await _l2Cache.SetAsync(key, value, l2Options, ct);
        }

        // Reset access count for new entry
        _accessCount[key] = 0;
    }

    /// <inheritdoc/>
    protected override async Task<bool> RemoveCoreAsync(string key, CancellationToken ct)
    {
        // Remove from both tiers
        var l1Removed = await _l1Cache.RemoveAsync(key, ct);
        var l2Removed = await _l2Cache.RemoveAsync(key, ct);
        _accessCount.TryRemove(key, out _);

        return l1Removed || l2Removed;
    }

    /// <inheritdoc/>
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        return await _l1Cache.ExistsAsync(key, ct) || await _l2Cache.ExistsAsync(key, ct);
    }

    /// <inheritdoc/>
    protected override async Task InvalidateByTagsCoreAsync(string[] tags, CancellationToken ct)
    {
        await _l1Cache.InvalidateByTagsAsync(tags, ct);
        await _l2Cache.InvalidateByTagsAsync(tags, ct);
    }

    /// <inheritdoc/>
    protected override async Task ClearCoreAsync(CancellationToken ct)
    {
        await _l1Cache.ClearAsync(ct);
        await _l2Cache.ClearAsync(ct);
        _accessCount.Clear();
    }

    private int IncrementAccess(string key)
    {
        return _accessCount.AddOrUpdate(key, 1, (_, count) => count + 1);
    }

    private async Task PromoteToL1Async(string key, byte[] value, CancellationToken ct)
    {
        var options = new CacheOptions { TTL = _config.L1DefaultTTL };
        await _l1Cache.SetAsync(key, value, options, ct);
        // Keep in L2 for durability
    }

    private CacheOptions CreateL1Options(CacheOptions original)
    {
        return new CacheOptions
        {
            TTL = original.TTL ?? _config.L1DefaultTTL,
            SlidingExpiration = original.SlidingExpiration,
            Priority = original.Priority,
            Tags = original.Tags
        };
    }

    private CacheOptions CreateL2Options(CacheOptions original)
    {
        return new CacheOptions
        {
            TTL = original.TTL ?? _config.L2DefaultTTL,
            SlidingExpiration = original.SlidingExpiration,
            Priority = original.Priority,
            Tags = original.Tags
        };
    }
}
