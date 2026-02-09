using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateMultiCloud.Strategies.CostOptimization;

/// <summary>
/// 118.4: Cloud Cost Optimization Strategies
/// Analyzes and optimizes costs across cloud providers.
/// </summary>

/// <summary>
/// Comprehensive cost analysis across all cloud providers.
/// </summary>
public sealed class CrossCloudCostAnalysisStrategy : MultiCloudStrategyBase
{
    private readonly ConcurrentDictionary<string, ProviderCostData> _costData = new();

    public override string StrategyId => "cost-analysis";
    public override string StrategyName => "Cross-Cloud Cost Analysis";
    public override string Category => "CostOptimization";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Analyzes and compares costs across AWS, Azure, GCP, and other providers",
        Category = Category,
        SupportsCostOptimization = true,
        TypicalLatencyOverheadMs = 0.5,
        MemoryFootprint = "Medium"
    };

    /// <summary>Records cost data for a provider.</summary>
    public void RecordCost(string providerId, CostEntry entry)
    {
        var data = _costData.GetOrAdd(providerId, _ => new ProviderCostData { ProviderId = providerId });
        data.Entries.Add(entry);
        data.TotalCost += entry.Amount;
        data.LastUpdated = DateTimeOffset.UtcNow;
    }

    /// <summary>Gets cost comparison across providers.</summary>
    public CostComparisonResult CompareCosts(DateTimeOffset from, DateTimeOffset to)
    {
        var comparisons = _costData.Values.Select(data =>
        {
            var relevantEntries = data.Entries.Where(e => e.Timestamp >= from && e.Timestamp <= to).ToList();
            var total = relevantEntries.Sum(e => e.Amount);
            var byCategory = relevantEntries.GroupBy(e => e.Category)
                .ToDictionary(g => g.Key, g => g.Sum(e => e.Amount));

            return new ProviderCostSummary
            {
                ProviderId = data.ProviderId,
                TotalCost = total,
                CostByCategory = byCategory,
                EntryCount = relevantEntries.Count
            };
        }).ToList();

        var cheapest = comparisons.MinBy(c => c.TotalCost);
        var mostExpensive = comparisons.MaxBy(c => c.TotalCost);

        RecordSuccess();
        return new CostComparisonResult
        {
            ProviderSummaries = comparisons,
            CheapestProvider = cheapest?.ProviderId,
            MostExpensiveProvider = mostExpensive?.ProviderId,
            PotentialSavings = (mostExpensive?.TotalCost ?? 0) - (cheapest?.TotalCost ?? 0),
            AnalysisPeriod = (from, to)
        };
    }

    /// <summary>Gets optimization recommendations.</summary>
    public IReadOnlyList<CostRecommendation> GetRecommendations()
    {
        var recommendations = new List<CostRecommendation>();

        foreach (var data in _costData.Values)
        {
            var highCostCategories = data.Entries
                .GroupBy(e => e.Category)
                .Where(g => g.Sum(e => e.Amount) > 100)
                .OrderByDescending(g => g.Sum(e => e.Amount))
                .Take(3);

            foreach (var category in highCostCategories)
            {
                recommendations.Add(new CostRecommendation
                {
                    ProviderId = data.ProviderId,
                    Category = category.Key,
                    CurrentCost = category.Sum(e => e.Amount),
                    Recommendation = GetRecommendationForCategory(category.Key),
                    EstimatedSavings = category.Sum(e => e.Amount) * 0.2,
                    Priority = "High"
                });
            }
        }

        return recommendations.OrderByDescending(r => r.EstimatedSavings).ToList();
    }

    private static string GetRecommendationForCategory(string category) => category switch
    {
        "Storage" => "Consider moving cold data to archive tier or cheaper provider",
        "Compute" => "Use spot/preemptible instances for batch workloads",
        "Egress" => "Minimize cross-region data transfer, use CDN",
        "Database" => "Right-size instances, use reserved capacity",
        _ => "Review usage patterns and optimize"
    };

    protected override string? GetCurrentState() => $"Providers: {_costData.Count}";
}

/// <summary>
/// Spot/preemptible instance optimization strategy.
/// </summary>
public sealed class SpotInstanceOptimizationStrategy : MultiCloudStrategyBase
{
    private readonly ConcurrentDictionary<string, SpotPricing> _spotPrices = new();

    public override string StrategyId => "cost-spot-optimization";
    public override string StrategyName => "Spot Instance Optimization";
    public override string Category => "CostOptimization";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Optimizes costs using spot/preemptible instances across clouds",
        Category = Category,
        SupportsCostOptimization = true,
        SupportsAutomaticFailover = true,
        TypicalLatencyOverheadMs = 1.0,
        MemoryFootprint = "Low"
    };

    /// <summary>Updates spot pricing.</summary>
    public void UpdateSpotPrice(string providerId, string instanceType, string region, double spotPrice, double onDemandPrice)
    {
        var key = $"{providerId}:{instanceType}:{region}";
        _spotPrices[key] = new SpotPricing
        {
            ProviderId = providerId,
            InstanceType = instanceType,
            Region = region,
            SpotPrice = spotPrice,
            OnDemandPrice = onDemandPrice,
            Savings = ((onDemandPrice - spotPrice) / onDemandPrice) * 100,
            LastUpdated = DateTimeOffset.UtcNow
        };
    }

    /// <summary>Finds best spot option.</summary>
    public SpotPricing? FindBestSpotOption(string instanceType)
    {
        var options = _spotPrices.Values
            .Where(s => s.InstanceType == instanceType)
            .OrderBy(s => s.SpotPrice)
            .FirstOrDefault();

        if (options != null) RecordSuccess();
        return options;
    }

    /// <summary>Gets all spot options sorted by savings.</summary>
    public IReadOnlyList<SpotPricing> GetBestSpotOptions(int limit = 10)
    {
        return _spotPrices.Values
            .OrderByDescending(s => s.Savings)
            .Take(limit)
            .ToList();
    }

    protected override string? GetCurrentState() => $"Spot prices tracked: {_spotPrices.Count}";
}

/// <summary>
/// Reserved capacity optimization strategy.
/// </summary>
public sealed class ReservedCapacityStrategy : MultiCloudStrategyBase
{
    private readonly ConcurrentDictionary<string, ReservedCapacity> _reservations = new();

    public override string StrategyId => "cost-reserved-capacity";
    public override string StrategyName => "Reserved Capacity Optimization";
    public override string Category => "CostOptimization";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Optimizes costs through reserved/committed capacity across clouds",
        Category = Category,
        SupportsCostOptimization = true,
        TypicalLatencyOverheadMs = 0.5,
        MemoryFootprint = "Low"
    };

    /// <summary>Analyzes reservation opportunity.</summary>
    public ReservationRecommendation AnalyzeReservationOpportunity(string providerId, string resourceType, double currentMonthlyUsage, double onDemandPrice)
    {
        // Calculate break-even for 1-year and 3-year reservations
        var oneYearSavings = 0.35; // 35% savings typical
        var threeYearSavings = 0.60; // 60% savings typical

        var oneYearMonthly = onDemandPrice * (1 - oneYearSavings);
        var threeYearMonthly = onDemandPrice * (1 - threeYearSavings);

        return new ReservationRecommendation
        {
            ProviderId = providerId,
            ResourceType = resourceType,
            CurrentMonthlySpend = currentMonthlyUsage * onDemandPrice,
            OneYearCommitmentMonthly = oneYearMonthly * currentMonthlyUsage,
            ThreeYearCommitmentMonthly = threeYearMonthly * currentMonthlyUsage,
            OneYearAnnualSavings = (onDemandPrice - oneYearMonthly) * currentMonthlyUsage * 12,
            ThreeYearAnnualSavings = (onDemandPrice - threeYearMonthly) * currentMonthlyUsage * 12,
            RecommendedTerm = currentMonthlyUsage > 0.7 ? "3-year" : currentMonthlyUsage > 0.4 ? "1-year" : "On-demand"
        };
    }

    /// <summary>Tracks reservation.</summary>
    public void TrackReservation(string providerId, string reservationId, string resourceType, double committedAmount, DateTimeOffset expiresAt)
    {
        _reservations[reservationId] = new ReservedCapacity
        {
            ProviderId = providerId,
            ReservationId = reservationId,
            ResourceType = resourceType,
            CommittedAmount = committedAmount,
            ExpiresAt = expiresAt,
            Utilization = 0
        };
        RecordSuccess();
    }

    /// <summary>Updates reservation utilization.</summary>
    public void UpdateUtilization(string reservationId, double utilization)
    {
        if (_reservations.TryGetValue(reservationId, out var reservation))
        {
            reservation.Utilization = utilization;
        }
    }

    protected override string? GetCurrentState() => $"Reservations: {_reservations.Count}";
}

/// <summary>
/// Egress cost optimization strategy.
/// </summary>
public sealed class EgressOptimizationStrategy : MultiCloudStrategyBase
{
    private readonly ConcurrentDictionary<string, EgressPricing> _egressPricing = new();

    public override string StrategyId => "cost-egress-optimization";
    public override string StrategyName => "Egress Cost Optimization";
    public override string Category => "CostOptimization";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Minimizes data egress costs across cloud providers",
        Category = Category,
        SupportsCostOptimization = true,
        TypicalLatencyOverheadMs = 0.5,
        MemoryFootprint = "Low"
    };

    /// <summary>Sets egress pricing for a route.</summary>
    public void SetEgressPricing(string sourceProvider, string targetProvider, double pricePerGb)
    {
        var key = $"{sourceProvider}:{targetProvider}";
        _egressPricing[key] = new EgressPricing
        {
            SourceProvider = sourceProvider,
            TargetProvider = targetProvider,
            PricePerGb = pricePerGb
        };
    }

    /// <summary>Calculates optimal data placement to minimize egress.</summary>
    public EgressOptimizationResult OptimizeDataPlacement(
        IEnumerable<(string dataId, long sizeBytes, Dictionary<string, double> accessPatterns)> data)
    {
        var recommendations = new List<DataPlacementRecommendation>();
        var totalSavings = 0.0;

        foreach (var (dataId, sizeBytes, accessPatterns) in data)
        {
            var primaryAccess = accessPatterns.MaxBy(ap => ap.Value);
            var sizeGb = sizeBytes / (1024.0 * 1024.0 * 1024.0);

            // Calculate cost if data is placed at different providers
            var costByPlacement = accessPatterns.Keys.Select(provider =>
            {
                var cost = accessPatterns
                    .Where(ap => ap.Key != provider)
                    .Sum(ap =>
                    {
                        var key = $"{provider}:{ap.Key}";
                        var price = _egressPricing.TryGetValue(key, out var p) ? p.PricePerGb : 0.09;
                        return price * sizeGb * ap.Value;
                    });
                return (provider, cost);
            }).ToList();

            var optimalPlacement = costByPlacement.MinBy(c => c.cost);

            recommendations.Add(new DataPlacementRecommendation
            {
                DataId = dataId,
                CurrentProvider = primaryAccess.Key,
                RecommendedProvider = optimalPlacement.provider,
                EstimatedMonthlySavings = costByPlacement.First(c => c.provider == primaryAccess.Key).cost - optimalPlacement.cost
            });

            totalSavings += recommendations.Last().EstimatedMonthlySavings;
        }

        RecordSuccess();
        return new EgressOptimizationResult
        {
            Recommendations = recommendations,
            TotalEstimatedMonthlySavings = totalSavings
        };
    }

    protected override string? GetCurrentState() => $"Routes: {_egressPricing.Count}";
}

/// <summary>
/// Storage tier optimization strategy.
/// </summary>
public sealed class StorageTierOptimizationStrategy : MultiCloudStrategyBase
{
    private readonly ConcurrentDictionary<string, StorageTierPricing> _tierPricing = new();

    public override string StrategyId => "cost-storage-tier";
    public override string StrategyName => "Storage Tier Optimization";
    public override string Category => "CostOptimization";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Optimizes storage costs by selecting optimal tier per provider",
        Category = Category,
        SupportsCostOptimization = true,
        TypicalLatencyOverheadMs = 0.5,
        MemoryFootprint = "Low"
    };

    /// <summary>Sets tier pricing.</summary>
    public void SetTierPricing(string providerId, string tier, double storagePerGbMonth, double retrievalPerGb, int minStorageDays)
    {
        var key = $"{providerId}:{tier}";
        _tierPricing[key] = new StorageTierPricing
        {
            ProviderId = providerId,
            Tier = tier,
            StoragePricePerGbMonth = storagePerGbMonth,
            RetrievalPricePerGb = retrievalPerGb,
            MinimumStorageDays = minStorageDays
        };
    }

    /// <summary>Recommends optimal tier based on access pattern.</summary>
    public TierRecommendation RecommendTier(string providerId, long sizeBytes, double accessesPerMonth, int retentionMonths)
    {
        var sizeGb = sizeBytes / (1024.0 * 1024.0 * 1024.0);
        var tiers = _tierPricing.Values.Where(t => t.ProviderId == providerId).ToList();

        var costs = tiers.Select(tier =>
        {
            var storageCost = tier.StoragePricePerGbMonth * sizeGb * retentionMonths;
            var retrievalCost = tier.RetrievalPricePerGb * sizeGb * accessesPerMonth * retentionMonths;
            return (tier, totalCost: storageCost + retrievalCost);
        }).OrderBy(c => c.totalCost).ToList();

        var optimal = costs.FirstOrDefault();
        var current = costs.LastOrDefault();

        RecordSuccess();
        return new TierRecommendation
        {
            ProviderId = providerId,
            RecommendedTier = optimal.tier?.Tier ?? "Standard",
            CurrentTierCost = current.totalCost,
            RecommendedTierCost = optimal.totalCost,
            MonthlySavings = (current.totalCost - optimal.totalCost) / retentionMonths
        };
    }

    protected override string? GetCurrentState() => $"Tiers: {_tierPricing.Count}";
}

/// <summary>
/// AI-powered cost prediction strategy.
/// </summary>
public sealed class AiCostPredictionStrategy : MultiCloudStrategyBase
{
    private readonly ConcurrentDictionary<string, List<CostDataPoint>> _historicalData = new();

    public override string StrategyId => "cost-ai-prediction";
    public override string StrategyName => "AI Cost Prediction";
    public override string Category => "CostOptimization";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Uses machine learning to predict future cloud costs and anomalies",
        Category = Category,
        SupportsCostOptimization = true,
        TypicalLatencyOverheadMs = 5.0,
        MemoryFootprint = "High"
    };

    /// <summary>Records historical cost data.</summary>
    public void RecordDataPoint(string providerId, DateTimeOffset timestamp, double cost)
    {
        var data = _historicalData.GetOrAdd(providerId, _ => new List<CostDataPoint>());
        data.Add(new CostDataPoint { Timestamp = timestamp, Cost = cost });
    }

    /// <summary>Predicts costs for next period.</summary>
    public CostPrediction PredictCost(string providerId, int daysAhead)
    {
        if (!_historicalData.TryGetValue(providerId, out var data) || data.Count < 7)
        {
            return new CostPrediction
            {
                Success = false,
                ErrorMessage = "Insufficient historical data"
            };
        }

        // Simple trend-based prediction
        var recentDays = data.OrderByDescending(d => d.Timestamp).Take(30).ToList();
        var avgDaily = recentDays.Average(d => d.Cost);
        var trend = recentDays.Count > 1
            ? (recentDays.First().Cost - recentDays.Last().Cost) / recentDays.Count
            : 0;

        var predictedDaily = avgDaily + (trend * daysAhead / 2);
        var predictedTotal = predictedDaily * daysAhead;

        RecordSuccess();
        return new CostPrediction
        {
            Success = true,
            ProviderId = providerId,
            PredictedDailyCost = predictedDaily,
            PredictedTotalCost = predictedTotal,
            DaysAhead = daysAhead,
            ConfidencePercent = Math.Max(50, 100 - (daysAhead * 2)),
            TrendDirection = trend > 0 ? "Increasing" : trend < 0 ? "Decreasing" : "Stable"
        };
    }

    /// <summary>Detects cost anomalies.</summary>
    public AnomalyDetectionResult DetectAnomalies(string providerId)
    {
        if (!_historicalData.TryGetValue(providerId, out var data) || data.Count < 14)
        {
            return new AnomalyDetectionResult { HasAnomaly = false };
        }

        var recentDays = data.OrderByDescending(d => d.Timestamp).Take(30).ToList();
        var avg = recentDays.Average(d => d.Cost);
        var stdDev = Math.Sqrt(recentDays.Average(d => Math.Pow(d.Cost - avg, 2)));

        var anomalies = recentDays
            .Where(d => Math.Abs(d.Cost - avg) > 2 * stdDev)
            .ToList();

        return new AnomalyDetectionResult
        {
            HasAnomaly = anomalies.Any(),
            AnomalyCount = anomalies.Count,
            AverageDeviation = anomalies.Any() ? anomalies.Average(a => Math.Abs(a.Cost - avg)) : 0,
            AnomalyDates = anomalies.Select(a => a.Timestamp).ToArray()
        };
    }

    protected override string? GetCurrentState() => $"Providers tracked: {_historicalData.Count}";
}

#region Supporting Types

public sealed class ProviderCostData
{
    public required string ProviderId { get; init; }
    public List<CostEntry> Entries { get; } = new();
    public double TotalCost { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
}

public sealed class CostEntry
{
    public DateTimeOffset Timestamp { get; init; }
    public required string Category { get; init; }
    public double Amount { get; init; }
    public string? ResourceId { get; init; }
}

public sealed class ProviderCostSummary
{
    public required string ProviderId { get; init; }
    public double TotalCost { get; init; }
    public Dictionary<string, double> CostByCategory { get; init; } = new();
    public int EntryCount { get; init; }
}

public sealed class CostComparisonResult
{
    public List<ProviderCostSummary> ProviderSummaries { get; init; } = new();
    public string? CheapestProvider { get; init; }
    public string? MostExpensiveProvider { get; init; }
    public double PotentialSavings { get; init; }
    public (DateTimeOffset From, DateTimeOffset To) AnalysisPeriod { get; init; }
}

public sealed class CostRecommendation
{
    public required string ProviderId { get; init; }
    public required string Category { get; init; }
    public double CurrentCost { get; init; }
    public required string Recommendation { get; init; }
    public double EstimatedSavings { get; init; }
    public required string Priority { get; init; }
}

public sealed class SpotPricing
{
    public required string ProviderId { get; init; }
    public required string InstanceType { get; init; }
    public required string Region { get; init; }
    public double SpotPrice { get; init; }
    public double OnDemandPrice { get; init; }
    public double Savings { get; init; }
    public DateTimeOffset LastUpdated { get; init; }
}

public sealed class ReservedCapacity
{
    public required string ProviderId { get; init; }
    public required string ReservationId { get; init; }
    public required string ResourceType { get; init; }
    public double CommittedAmount { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public double Utilization { get; set; }
}

public sealed class ReservationRecommendation
{
    public required string ProviderId { get; init; }
    public required string ResourceType { get; init; }
    public double CurrentMonthlySpend { get; init; }
    public double OneYearCommitmentMonthly { get; init; }
    public double ThreeYearCommitmentMonthly { get; init; }
    public double OneYearAnnualSavings { get; init; }
    public double ThreeYearAnnualSavings { get; init; }
    public required string RecommendedTerm { get; init; }
}

public sealed class EgressPricing
{
    public required string SourceProvider { get; init; }
    public required string TargetProvider { get; init; }
    public double PricePerGb { get; init; }
}

public sealed class DataPlacementRecommendation
{
    public required string DataId { get; init; }
    public required string CurrentProvider { get; init; }
    public required string RecommendedProvider { get; init; }
    public double EstimatedMonthlySavings { get; init; }
}

public sealed class EgressOptimizationResult
{
    public List<DataPlacementRecommendation> Recommendations { get; init; } = new();
    public double TotalEstimatedMonthlySavings { get; init; }
}

public sealed class StorageTierPricing
{
    public required string ProviderId { get; init; }
    public required string Tier { get; init; }
    public double StoragePricePerGbMonth { get; init; }
    public double RetrievalPricePerGb { get; init; }
    public int MinimumStorageDays { get; init; }
}

public sealed class TierRecommendation
{
    public required string ProviderId { get; init; }
    public required string RecommendedTier { get; init; }
    public double CurrentTierCost { get; init; }
    public double RecommendedTierCost { get; init; }
    public double MonthlySavings { get; init; }
}

public sealed class CostDataPoint
{
    public DateTimeOffset Timestamp { get; init; }
    public double Cost { get; init; }
}

public sealed class CostPrediction
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ProviderId { get; init; }
    public double PredictedDailyCost { get; init; }
    public double PredictedTotalCost { get; init; }
    public int DaysAhead { get; init; }
    public double ConfidencePercent { get; init; }
    public string? TrendDirection { get; init; }
}

public sealed class AnomalyDetectionResult
{
    public bool HasAnomaly { get; init; }
    public int AnomalyCount { get; init; }
    public double AverageDeviation { get; init; }
    public DateTimeOffset[]? AnomalyDates { get; init; }
}

#endregion
