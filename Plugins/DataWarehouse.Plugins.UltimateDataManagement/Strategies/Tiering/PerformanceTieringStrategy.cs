using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Tiering;

/// <summary>
/// Performance-based tiering strategy that maximizes data access performance
/// by ensuring frequently accessed data is on the fastest storage tiers.
/// </summary>
/// <remarks>
/// Features:
/// - Latency requirements per object class
/// - Performance SLA enforcement
/// - Automatic promotion of frequently accessed data
/// - Performance monitoring and tracking
/// - Adaptive tier selection based on access patterns
/// </remarks>
public sealed class PerformanceTieringStrategy : TieringStrategyBase
{
    private readonly ConcurrentDictionary<string, PerformanceRequirements> _slaRequirements = new();
    private readonly ConcurrentDictionary<string, PerformanceMetrics> _objectMetrics = new();
    private PerformanceConfig _config = PerformanceConfig.Default;

    /// <summary>
    /// Performance requirements for a classification or object class.
    /// </summary>
    public sealed class PerformanceRequirements
    {
        /// <summary>
        /// Classification or object class name.
        /// </summary>
        public required string Classification { get; init; }

        /// <summary>
        /// Maximum acceptable latency in milliseconds.
        /// </summary>
        public double MaxLatencyMs { get; init; }

        /// <summary>
        /// Minimum required throughput in MB/s.
        /// </summary>
        public double MinThroughputMbps { get; init; }

        /// <summary>
        /// Target availability percentage (0-100).
        /// </summary>
        public double TargetAvailability { get; init; } = 99.9;

        /// <summary>
        /// Priority level (higher = more important).
        /// </summary>
        public int Priority { get; init; }

        /// <summary>
        /// Whether to allow demotion if SLA is not currently violated.
        /// </summary>
        public bool AllowDemotion { get; init; } = true;
    }

    /// <summary>
    /// Performance metrics for an object.
    /// </summary>
    private sealed class PerformanceMetrics
    {
        public required string ObjectId { get; init; }
        public double AverageLatencyMs { get; set; }
        public double P99LatencyMs { get; set; }
        public double ThroughputMbps { get; set; }
        public int AccessCount { get; set; }
        public int SlaViolations { get; set; }
        public DateTime LastUpdated { get; set; }
        public List<double> RecentLatencies { get; } = new();
    }

    /// <summary>
    /// Configuration for performance-based tiering.
    /// </summary>
    public sealed class PerformanceConfig
    {
        /// <summary>
        /// Expected latency per tier in milliseconds.
        /// </summary>
        public Dictionary<StorageTier, double> TierLatencyMs { get; init; } = new();

        /// <summary>
        /// Expected throughput per tier in MB/s.
        /// </summary>
        public Dictionary<StorageTier, double> TierThroughputMbps { get; init; } = new();

        /// <summary>
        /// Number of recent accesses to track for performance metrics.
        /// </summary>
        public int MetricsWindowSize { get; init; } = 100;

        /// <summary>
        /// Promotion threshold - accesses per day to consider for promotion.
        /// </summary>
        public int PromotionAccessThreshold { get; init; } = 5;

        /// <summary>
        /// SLA violation threshold before forced promotion.
        /// </summary>
        public int SlaViolationThreshold { get; init; } = 3;

        /// <summary>
        /// Gets the default configuration.
        /// </summary>
        public static PerformanceConfig Default => new()
        {
            TierLatencyMs = new Dictionary<StorageTier, double>
            {
                [StorageTier.Hot] = 1.0,
                [StorageTier.Warm] = 10.0,
                [StorageTier.Cold] = 100.0,
                [StorageTier.Archive] = 1000.0,
                [StorageTier.Glacier] = 3600000.0 // Hours for glacier retrieval
            },
            TierThroughputMbps = new Dictionary<StorageTier, double>
            {
                [StorageTier.Hot] = 1000.0,
                [StorageTier.Warm] = 500.0,
                [StorageTier.Cold] = 100.0,
                [StorageTier.Archive] = 50.0,
                [StorageTier.Glacier] = 10.0
            },
            MetricsWindowSize = 100,
            PromotionAccessThreshold = 5,
            SlaViolationThreshold = 3
        };
    }

    /// <inheritdoc/>
    public override string StrategyId => "tiering.performance";

    /// <inheritdoc/>
    public override string DisplayName => "Performance-Based Tiering";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 50000,
        TypicalLatencyMs = 0.5
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Performance-based tiering that maximizes data access performance. " +
        "Enforces latency SLAs, automatically promotes frequently accessed data, " +
        "and provides adaptive tier selection based on access patterns.";

    /// <inheritdoc/>
    public override string[] Tags => ["tiering", "performance", "sla", "latency", "promotion"];

    /// <summary>
    /// Gets or sets the performance configuration.
    /// </summary>
    public PerformanceConfig Config
    {
        get => _config;
        set => _config = value ?? PerformanceConfig.Default;
    }

    /// <summary>
    /// Sets performance requirements for a classification.
    /// </summary>
    /// <param name="requirements">The performance requirements.</param>
    public void SetPerformanceRequirements(PerformanceRequirements requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        _slaRequirements[requirements.Classification] = requirements;
    }

    /// <summary>
    /// Removes performance requirements for a classification.
    /// </summary>
    /// <param name="classification">The classification name.</param>
    public bool RemovePerformanceRequirements(string classification)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(classification);
        return _slaRequirements.TryRemove(classification, out _);
    }

    /// <summary>
    /// Gets all configured performance requirements.
    /// </summary>
    public IReadOnlyList<PerformanceRequirements> GetPerformanceRequirements()
    {
        return _slaRequirements.Values.ToList();
    }

    /// <summary>
    /// Records a performance measurement for an object.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    /// <param name="latencyMs">The measured latency in milliseconds.</param>
    /// <param name="bytesTransferred">Bytes transferred (for throughput calculation).</param>
    public void RecordPerformance(string objectId, double latencyMs, long bytesTransferred = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        var metrics = _objectMetrics.GetOrAdd(objectId, id => new PerformanceMetrics { ObjectId = id });

        lock (metrics)
        {
            metrics.RecentLatencies.Add(latencyMs);

            // Keep only recent measurements
            while (metrics.RecentLatencies.Count > _config.MetricsWindowSize)
            {
                metrics.RecentLatencies.RemoveAt(0);
            }

            // Update aggregates
            metrics.AverageLatencyMs = metrics.RecentLatencies.Average();
            metrics.P99LatencyMs = CalculatePercentile(metrics.RecentLatencies, 99);
            metrics.AccessCount++;

            if (bytesTransferred > 0 && latencyMs > 0)
            {
                var throughput = (bytesTransferred / (1024.0 * 1024.0)) / (latencyMs / 1000.0);
                metrics.ThroughputMbps = (metrics.ThroughputMbps * (metrics.AccessCount - 1) + throughput) / metrics.AccessCount;
            }

            metrics.LastUpdated = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Reports an SLA violation for an object.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    public void ReportSlaViolation(string objectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        var metrics = _objectMetrics.GetOrAdd(objectId, id => new PerformanceMetrics { ObjectId = id });

        lock (metrics)
        {
            metrics.SlaViolations++;
            metrics.LastUpdated = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Gets the performance metrics for an object.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    /// <returns>Performance metrics or null if not tracked.</returns>
    public (double AvgLatencyMs, double P99LatencyMs, double ThroughputMbps, int Violations)? GetMetrics(string objectId)
    {
        if (_objectMetrics.TryGetValue(objectId, out var metrics))
        {
            lock (metrics)
            {
                return (metrics.AverageLatencyMs, metrics.P99LatencyMs, metrics.ThroughputMbps, metrics.SlaViolations);
            }
        }

        return null;
    }

    /// <inheritdoc/>
    protected override Task<TierRecommendation> EvaluateCoreAsync(DataObject data, CancellationToken ct)
    {
        // Get performance requirements for this object's classification
        PerformanceRequirements? requirements = null;
        if (!string.IsNullOrEmpty(data.Classification))
        {
            _slaRequirements.TryGetValue(data.Classification, out requirements);
        }

        // Get or create metrics
        var metrics = _objectMetrics.GetOrAdd(data.ObjectId, id => new PerformanceMetrics
        {
            ObjectId = id,
            AverageLatencyMs = _config.TierLatencyMs.GetValueOrDefault(data.CurrentTier, 10.0),
            ThroughputMbps = _config.TierThroughputMbps.GetValueOrDefault(data.CurrentTier, 100.0)
        });

        // Check for SLA violations requiring immediate promotion
        if (requirements != null && metrics.SlaViolations >= _config.SlaViolationThreshold)
        {
            var targetTier = DetermineTargetTierForSla(requirements);

            if (targetTier < data.CurrentTier)
            {
                // Reset violations after promotion
                lock (metrics)
                {
                    metrics.SlaViolations = 0;
                }

                return Task.FromResult(Promote(data, targetTier,
                    $"SLA violations ({metrics.SlaViolations}) exceeded threshold. " +
                    $"Required latency: {requirements.MaxLatencyMs}ms, current tier latency: " +
                    $"{_config.TierLatencyMs.GetValueOrDefault(data.CurrentTier, 10)}ms",
                    0.95, 1.0));
            }
        }

        // Check for high access frequency requiring promotion
        if (data.AccessesLast24Hours >= _config.PromotionAccessThreshold && data.CurrentTier != StorageTier.Hot)
        {
            return Task.FromResult(Promote(data, StorageTier.Hot,
                $"High access frequency ({data.AccessesLast24Hours}/day) requires Hot tier for performance",
                0.85, 0.8));
        }

        // Check if current tier meets SLA requirements
        if (requirements != null)
        {
            var currentTierLatency = _config.TierLatencyMs.GetValueOrDefault(data.CurrentTier, 10.0);
            var currentTierThroughput = _config.TierThroughputMbps.GetValueOrDefault(data.CurrentTier, 100.0);

            if (currentTierLatency > requirements.MaxLatencyMs || currentTierThroughput < requirements.MinThroughputMbps)
            {
                var targetTier = DetermineTargetTierForSla(requirements);

                if (targetTier < data.CurrentTier)
                {
                    return Task.FromResult(Promote(data, targetTier,
                        $"Current tier ({data.CurrentTier}) does not meet SLA requirements. " +
                        $"Required: {requirements.MaxLatencyMs}ms latency, {requirements.MinThroughputMbps}MB/s throughput",
                        0.9, 0.9));
                }
            }

            // Check if we can demote while still meeting SLA
            if (requirements.AllowDemotion && data.AccessesLast7Days < _config.PromotionAccessThreshold)
            {
                var demotionTier = DetermineDemotionTierWithSla(data.CurrentTier, requirements);

                if (demotionTier > data.CurrentTier)
                {
                    return Task.FromResult(Demote(data, demotionTier,
                        $"Low access frequency ({data.AccessesLast7Days}/week) allows demotion while maintaining SLA",
                        0.75, 0.5, EstimateMonthlySavings(data.SizeBytes, data.CurrentTier, demotionTier)));
                }
            }
        }
        else
        {
            // No SLA - use access-based tiering
            var recommendedTier = DetermineRecommendedTierByAccess(data);

            if (recommendedTier != data.CurrentTier)
            {
                if (recommendedTier < data.CurrentTier)
                {
                    return Task.FromResult(Promote(data, recommendedTier,
                        $"Access pattern suggests promotion for better performance",
                        0.7, 0.6));
                }
                else
                {
                    return Task.FromResult(Demote(data, recommendedTier,
                        $"Low access frequency allows demotion",
                        0.7, 0.4, EstimateMonthlySavings(data.SizeBytes, data.CurrentTier, recommendedTier)));
                }
            }
        }

        return Task.FromResult(NoChange(data,
            $"Current tier ({data.CurrentTier}) meets performance requirements"));
    }

    private StorageTier DetermineTargetTierForSla(PerformanceRequirements requirements)
    {
        foreach (StorageTier tier in Enum.GetValues<StorageTier>())
        {
            var tierLatency = _config.TierLatencyMs.GetValueOrDefault(tier, double.MaxValue);
            var tierThroughput = _config.TierThroughputMbps.GetValueOrDefault(tier, 0);

            if (tierLatency <= requirements.MaxLatencyMs && tierThroughput >= requirements.MinThroughputMbps)
            {
                return tier;
            }
        }

        return StorageTier.Hot; // Default to Hot if no tier meets requirements
    }

    private StorageTier DetermineDemotionTierWithSla(StorageTier currentTier, PerformanceRequirements requirements)
    {
        var demotionTier = currentTier;

        foreach (StorageTier tier in Enum.GetValues<StorageTier>())
        {
            if (tier <= currentTier)
                continue;

            var tierLatency = _config.TierLatencyMs.GetValueOrDefault(tier, double.MaxValue);
            var tierThroughput = _config.TierThroughputMbps.GetValueOrDefault(tier, 0);

            if (tierLatency <= requirements.MaxLatencyMs && tierThroughput >= requirements.MinThroughputMbps)
            {
                demotionTier = tier;
            }
            else
            {
                break; // Stop at first tier that doesn't meet SLA
            }
        }

        return demotionTier;
    }

    private StorageTier DetermineRecommendedTierByAccess(DataObject data)
    {
        if (data.AccessesLast24Hours >= 10)
            return StorageTier.Hot;

        if (data.AccessesLast7Days >= 7)
            return StorageTier.Warm;

        if (data.AccessesLast30Days >= 4)
            return StorageTier.Cold;

        return StorageTier.Archive;
    }

    private static double CalculatePercentile(List<double> values, int percentile)
    {
        if (values.Count == 0)
            return 0;

        var sorted = values.OrderBy(v => v).ToList();
        var index = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
        return sorted[Math.Max(0, Math.Min(index, sorted.Count - 1))];
    }
}
