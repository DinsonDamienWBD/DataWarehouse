namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.ResourceEfficiency;

/// <summary>
/// Optimizes caching for energy efficiency by balancing cache hit rates
/// with memory/storage power consumption.
/// </summary>
public sealed class CacheOptimizationStrategy : SustainabilityStrategyBase
{
    private readonly Dictionary<string, CacheInstance> _caches = new();
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "cache-optimization";
    /// <inheritdoc/>
    public override string DisplayName => "Cache Optimization";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.ResourceEfficiency;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.RealTimeMonitoring | SustainabilityCapabilities.ActiveControl;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Optimizes cache sizes and policies for energy efficiency while maintaining performance.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "cache", "memory", "redis", "memcached", "efficiency", "hit-rate" };

    /// <summary>Target cache hit rate (%).</summary>
    public double TargetHitRatePercent { get; set; } = 90;
    /// <summary>Energy cost per GB of cache memory (watts).</summary>
    public double WattsPerGb { get; set; } = 0.3;

    /// <summary>Registers a cache instance.</summary>
    public void RegisterCache(string cacheId, string name, CacheType type, long maxSizeBytes)
    {
        lock (_lock)
        {
            _caches[cacheId] = new CacheInstance
            {
                CacheId = cacheId,
                Name = name,
                Type = type,
                MaxSizeBytes = maxSizeBytes
            };
        }
    }

    /// <summary>Records cache statistics.</summary>
    public void RecordCacheStats(string cacheId, long hits, long misses, long currentSizeBytes, long evictions)
    {
        lock (_lock)
        {
            if (_caches.TryGetValue(cacheId, out var cache))
            {
                cache.TotalHits += hits;
                cache.TotalMisses += misses;
                cache.CurrentSizeBytes = currentSizeBytes;
                cache.TotalEvictions += evictions;
                cache.HitRate = cache.TotalHits + cache.TotalMisses > 0
                    ? (double)cache.TotalHits / (cache.TotalHits + cache.TotalMisses) * 100
                    : 0;
            }
        }
        RecordSample(currentSizeBytes / 1_073_741_824.0 * WattsPerGb, 0);
        EvaluateOptimizations();
    }

    /// <summary>Gets cache recommendations.</summary>
    public IReadOnlyList<CacheRecommendation> GetRecommendations()
    {
        var recommendations = new List<CacheRecommendation>();

        lock (_lock)
        {
            foreach (var cache in _caches.Values)
            {
                var currentGb = cache.CurrentSizeBytes / 1_073_741_824.0;
                var maxGb = cache.MaxSizeBytes / 1_073_741_824.0;
                var utilizationPercent = cache.MaxSizeBytes > 0 ? (double)cache.CurrentSizeBytes / cache.MaxSizeBytes * 100 : 0;

                // Under-utilized cache - can reduce size
                if (utilizationPercent < 50 && cache.HitRate >= TargetHitRatePercent)
                {
                    var targetSize = cache.CurrentSizeBytes * 1.5; // 50% headroom
                    var savingsGb = maxGb - targetSize / 1_073_741_824.0;
                    recommendations.Add(new CacheRecommendation
                    {
                        CacheId = cache.CacheId,
                        Type = CacheRecommendationType.ReduceSize,
                        CurrentSizeBytes = cache.CurrentSizeBytes,
                        RecommendedSizeBytes = (long)targetSize,
                        HitRate = cache.HitRate,
                        EstimatedSavingsWatts = savingsGb * WattsPerGb,
                        Reason = $"Cache {utilizationPercent:F0}% utilized with {cache.HitRate:F0}% hit rate"
                    });
                }
                // Over-utilized cache - needs increase
                else if (utilizationPercent > 95 && cache.HitRate < TargetHitRatePercent && cache.TotalEvictions > 1000)
                {
                    var targetSize = Math.Min(cache.MaxSizeBytes * 2, cache.MaxSizeBytes + 1_073_741_824);
                    recommendations.Add(new CacheRecommendation
                    {
                        CacheId = cache.CacheId,
                        Type = CacheRecommendationType.IncreaseSize,
                        CurrentSizeBytes = cache.CurrentSizeBytes,
                        RecommendedSizeBytes = targetSize,
                        HitRate = cache.HitRate,
                        EstimatedSavingsWatts = 0,
                        Reason = $"Low hit rate {cache.HitRate:F0}% with high evictions"
                    });
                }
                // Poor hit rate despite available space
                else if (cache.HitRate < TargetHitRatePercent * 0.7 && utilizationPercent < 80)
                {
                    recommendations.Add(new CacheRecommendation
                    {
                        CacheId = cache.CacheId,
                        Type = CacheRecommendationType.ReviewPolicy,
                        CurrentSizeBytes = cache.CurrentSizeBytes,
                        RecommendedSizeBytes = cache.CurrentSizeBytes,
                        HitRate = cache.HitRate,
                        EstimatedSavingsWatts = 0,
                        Reason = $"Poor hit rate {cache.HitRate:F0}% - review eviction policy"
                    });
                }
            }
        }

        return recommendations.OrderByDescending(r => r.EstimatedSavingsWatts).ToList();
    }

    /// <summary>Gets total cache power consumption.</summary>
    public double GetTotalPowerWatts()
    {
        lock (_lock)
        {
            return _caches.Values.Sum(c => c.CurrentSizeBytes / 1_073_741_824.0 * WattsPerGb);
        }
    }

    private void EvaluateOptimizations()
    {
        ClearRecommendations();
        var recs = GetRecommendations();

        if (recs.Any(r => r.Type == CacheRecommendationType.ReduceSize))
        {
            var totalSavings = recs.Where(r => r.Type == CacheRecommendationType.ReduceSize).Sum(r => r.EstimatedSavingsWatts);
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-reduce",
                Type = "CacheReduction",
                Priority = 6,
                Description = $"Reduce cache sizes to save {totalSavings:F1}W",
                EstimatedEnergySavingsWh = totalSavings * 24,
                CanAutoApply = true,
                Action = "reduce-cache-sizes"
            });
        }
    }
}

/// <summary>Cache type.</summary>
public enum CacheType { Memory, Redis, Memcached, Disk, CDN }

/// <summary>Cache instance information.</summary>
public sealed class CacheInstance
{
    public required string CacheId { get; init; }
    public required string Name { get; init; }
    public required CacheType Type { get; init; }
    public required long MaxSizeBytes { get; init; }
    public long CurrentSizeBytes { get; set; }
    public long TotalHits { get; set; }
    public long TotalMisses { get; set; }
    public long TotalEvictions { get; set; }
    public double HitRate { get; set; }
}

/// <summary>Cache recommendation type.</summary>
public enum CacheRecommendationType { ReduceSize, IncreaseSize, ReviewPolicy, Disable }

/// <summary>Cache optimization recommendation.</summary>
public sealed record CacheRecommendation
{
    public required string CacheId { get; init; }
    public required CacheRecommendationType Type { get; init; }
    public required long CurrentSizeBytes { get; init; }
    public required long RecommendedSizeBytes { get; init; }
    public required double HitRate { get; init; }
    public required double EstimatedSavingsWatts { get; init; }
    public required string Reason { get; init; }
}
