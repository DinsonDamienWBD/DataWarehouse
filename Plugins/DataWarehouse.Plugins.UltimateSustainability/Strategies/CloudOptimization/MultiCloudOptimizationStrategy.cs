namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.CloudOptimization;

/// <summary>
/// Optimizes workload placement across multiple cloud providers
/// for cost, performance, and sustainability.
/// </summary>
public sealed class MultiCloudOptimizationStrategy : SustainabilityStrategyBase
{
    private readonly Dictionary<string, CloudProvider> _providers = new();
    private readonly Dictionary<string, MultiCloudWorkload> _workloads = new();
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "multi-cloud-optimization";
    /// <inheritdoc/>
    public override string DisplayName => "Multi-Cloud Optimization";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.CloudOptimization;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.Scheduling | SustainabilityCapabilities.ExternalIntegration | SustainabilityCapabilities.CarbonCalculation;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Optimizes workload placement across AWS, Azure, and GCP for cost and carbon efficiency.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "multi-cloud", "aws", "azure", "gcp", "placement", "optimization" };

    /// <summary>Weight for cost in placement decisions (0-1).</summary>
    public double CostWeight { get; set; } = 0.4;
    /// <summary>Weight for carbon in placement decisions (0-1).</summary>
    public double CarbonWeight { get; set; } = 0.4;
    /// <summary>Weight for latency in placement decisions (0-1).</summary>
    public double LatencyWeight { get; set; } = 0.2;

    /// <summary>Registers a cloud provider.</summary>
    public void RegisterProvider(string providerId, string name, double baseCostMultiplier, double carbonIntensity, int avgLatencyMs)
    {
        lock (_lock)
        {
            _providers[providerId] = new CloudProvider
            {
                ProviderId = providerId,
                Name = name,
                BaseCostMultiplier = baseCostMultiplier,
                CarbonIntensity = carbonIntensity,
                AvgLatencyMs = avgLatencyMs,
                IsAvailable = true
            };
        }
    }

    /// <summary>Updates provider metrics.</summary>
    public void UpdateProviderMetrics(string providerId, double currentCostMultiplier, double currentCarbonIntensity, int currentLatencyMs, bool isAvailable)
    {
        lock (_lock)
        {
            if (_providers.TryGetValue(providerId, out var provider))
            {
                provider.BaseCostMultiplier = currentCostMultiplier;
                provider.CarbonIntensity = currentCarbonIntensity;
                provider.AvgLatencyMs = currentLatencyMs;
                provider.IsAvailable = isAvailable;
                provider.LastUpdated = DateTimeOffset.UtcNow;
            }
        }
        RecordSample(0, currentCarbonIntensity);
        EvaluateOptimizations();
    }

    /// <summary>Registers a workload.</summary>
    public void RegisterWorkload(string workloadId, string name, string currentProvider, double baselineCostUsd, bool isPortable)
    {
        lock (_lock)
        {
            _workloads[workloadId] = new MultiCloudWorkload
            {
                WorkloadId = workloadId,
                Name = name,
                CurrentProvider = currentProvider,
                BaselineCostUsd = baselineCostUsd,
                IsPortable = isPortable,
                CreatedAt = DateTimeOffset.UtcNow
            };
        }
    }

    /// <summary>Gets optimal provider for a workload.</summary>
    public PlacementRecommendation GetOptimalPlacement(string workloadId)
    {
        lock (_lock)
        {
            return GetOptimalPlacementCore(workloadId);
        }
    }

    // Finding 4445: extracted lock-free core so GetAllRecommendations can call it
    // under a single lock acquisition rather than re-entering from GetOptimalPlacement.
    private PlacementRecommendation GetOptimalPlacementCore(string workloadId)
    {
        if (!_workloads.TryGetValue(workloadId, out var workload))
            return new PlacementRecommendation { WorkloadId = workloadId, Success = false, Reason = "Workload not found" };

        if (!workload.IsPortable)
            return new PlacementRecommendation { WorkloadId = workloadId, Success = false, Reason = "Workload is not portable" };

        var availableProviders = _providers.Values.Where(p => p.IsAvailable).ToList();
        if (!availableProviders.Any())
            return new PlacementRecommendation { WorkloadId = workloadId, Success = false, Reason = "No providers available" };

        // Normalize and score
        var maxCost = availableProviders.Max(p => p.BaseCostMultiplier);
        var maxCarbon = availableProviders.Max(p => p.CarbonIntensity);
        var maxLatency = availableProviders.Max(p => p.AvgLatencyMs);

        var scored = availableProviders.Select(p =>
        {
            var costScore = maxCost > 0 ? 1 - (p.BaseCostMultiplier / maxCost) : 1;
            var carbonScore = maxCarbon > 0 ? 1 - (p.CarbonIntensity / maxCarbon) : 1;
            var latencyScore = maxLatency > 0 ? 1 - ((double)p.AvgLatencyMs / maxLatency) : 1;
            var totalScore = costScore * CostWeight + carbonScore * CarbonWeight + latencyScore * LatencyWeight;

            return new { Provider = p, Score = totalScore, CostScore = costScore, CarbonScore = carbonScore };
        }).OrderByDescending(x => x.Score).ToList();

        var best = scored.First();
        var current = _providers.TryGetValue(workload.CurrentProvider, out var curr) ? curr : null;
        var currentCost = current != null ? workload.BaselineCostUsd * current.BaseCostMultiplier : workload.BaselineCostUsd;
        var newCost = workload.BaselineCostUsd * best.Provider.BaseCostMultiplier;
        var costSavings = currentCost - newCost;
        var carbonReduction = current != null ? current.CarbonIntensity - best.Provider.CarbonIntensity : 0;

        return new PlacementRecommendation
        {
            WorkloadId = workloadId,
            Success = true,
            CurrentProvider = workload.CurrentProvider,
            RecommendedProvider = best.Provider.ProviderId,
            Score = best.Score,
            EstimatedCostSavingsUsd = costSavings,
            EstimatedCarbonReductionGrams = carbonReduction * 10, // Rough estimate
            ShouldMigrate = best.Provider.ProviderId != workload.CurrentProvider && costSavings > 0,
            Reason = $"Best fit: {best.Provider.Name} (score: {best.Score:F2})"
        };
    }

    /// <summary>Gets all placement recommendations.</summary>
    public IReadOnlyList<PlacementRecommendation> GetAllRecommendations()
    {
        lock (_lock)
        {
            // Finding 4445: use core method to avoid re-entrant lock acquisition.
            return _workloads.Keys
                .Select(GetOptimalPlacementCore)
                .Where(r => r.Success && r.ShouldMigrate)
                .OrderByDescending(r => r.EstimatedCostSavingsUsd)
                .ToList();
        }
    }

    /// <summary>Gets provider comparison.</summary>
    public IReadOnlyList<ProviderComparison> GetProviderComparison()
    {
        lock (_lock)
        {
            return _providers.Values.Select(p => new ProviderComparison
            {
                ProviderId = p.ProviderId,
                Name = p.Name,
                CostMultiplier = p.BaseCostMultiplier,
                CarbonIntensity = p.CarbonIntensity,
                AvgLatencyMs = p.AvgLatencyMs,
                IsAvailable = p.IsAvailable,
                WorkloadCount = _workloads.Values.Count(w => w.CurrentProvider == p.ProviderId)
            }).OrderBy(p => p.CostMultiplier).ToList();
        }
    }

    private void EvaluateOptimizations()
    {
        ClearRecommendations();
        var recs = GetAllRecommendations();

        if (recs.Any())
        {
            var totalSavings = recs.Sum(r => r.EstimatedCostSavingsUsd);
            var totalCarbonReduction = recs.Sum(r => r.EstimatedCarbonReductionGrams);

            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-migrate",
                Type = "MultiCloudMigration",
                Priority = 6,
                Description = $"{recs.Count} workloads can migrate for ${totalSavings:F0}/month savings and {totalCarbonReduction:F0}g CO2e reduction.",
                EstimatedCostSavingsUsd = totalSavings,
                EstimatedCarbonReductionGrams = totalCarbonReduction,
                CanAutoApply = false,
                Action = "migrate-workloads"
            });
        }
    }
}

/// <summary>Cloud provider information.</summary>
public sealed class CloudProvider
{
    public required string ProviderId { get; init; }
    public required string Name { get; init; }
    public double BaseCostMultiplier { get; set; }
    public double CarbonIntensity { get; set; }
    public int AvgLatencyMs { get; set; }
    public bool IsAvailable { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
}

/// <summary>Multi-cloud workload.</summary>
public sealed class MultiCloudWorkload
{
    public required string WorkloadId { get; init; }
    public required string Name { get; init; }
    public required string CurrentProvider { get; init; }
    public required double BaselineCostUsd { get; init; }
    public required bool IsPortable { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Placement recommendation.</summary>
public sealed record PlacementRecommendation
{
    public required string WorkloadId { get; init; }
    public bool Success { get; init; }
    public string? Reason { get; init; }
    public string? CurrentProvider { get; init; }
    public string? RecommendedProvider { get; init; }
    public double Score { get; init; }
    public double EstimatedCostSavingsUsd { get; init; }
    public double EstimatedCarbonReductionGrams { get; init; }
    public bool ShouldMigrate { get; init; }
}

/// <summary>Provider comparison.</summary>
public sealed record ProviderComparison
{
    public required string ProviderId { get; init; }
    public required string Name { get; init; }
    public required double CostMultiplier { get; init; }
    public required double CarbonIntensity { get; init; }
    public required int AvgLatencyMs { get; init; }
    public required bool IsAvailable { get; init; }
    public required int WorkloadCount { get; init; }
}
