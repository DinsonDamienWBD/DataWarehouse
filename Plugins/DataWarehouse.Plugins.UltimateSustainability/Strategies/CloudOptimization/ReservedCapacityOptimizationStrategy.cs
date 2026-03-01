namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.CloudOptimization;

/// <summary>
/// Optimizes cloud reserved capacity (RIs, Savings Plans) for cost and sustainability.
/// Analyzes usage patterns to recommend optimal commitment levels.
/// </summary>
public sealed class ReservedCapacityOptimizationStrategy : SustainabilityStrategyBase
{
    private readonly Dictionary<string, ReservedCapacity> _reservations = new();
    // Finding 4451: Queue for O(1) dequeue when capping usage history (vs O(n) List.RemoveAt(0)).
    private readonly Queue<UsageRecord> _usageHistory = new();
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "reserved-capacity-optimization";
    /// <inheritdoc/>
    public override string DisplayName => "Reserved Capacity Optimization";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.CloudOptimization;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.PredictiveAnalytics | SustainabilityCapabilities.Reporting;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Optimizes cloud reserved capacity and savings plans for cost reduction and sustainability.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "reserved", "ri", "savings-plan", "cost", "commitment", "cloud" };

    /// <summary>Minimum coverage target (%).</summary>
    public double MinCoveragePercent { get; set; } = 70;
    /// <summary>Maximum commitment period (months).</summary>
    public int MaxCommitmentMonths { get; set; } = 36;

    /// <summary>Registers a reserved capacity.</summary>
    public void RegisterReservation(string reservationId, string instanceType, string region, int quantity,
        DateTimeOffset startDate, int termMonths, double monthlyCost, double savingsPercent)
    {
        lock (_lock)
        {
            _reservations[reservationId] = new ReservedCapacity
            {
                ReservationId = reservationId,
                InstanceType = instanceType,
                Region = region,
                Quantity = quantity,
                StartDate = startDate,
                EndDate = startDate.AddMonths(termMonths),
                TermMonths = termMonths,
                MonthlyCostUsd = monthlyCost,
                SavingsPercent = savingsPercent
            };
        }
    }

    /// <summary>Records hourly usage.</summary>
    public void RecordUsage(string instanceType, string region, int onDemandCount, int reservedCount)
    {
        lock (_lock)
        {
            _usageHistory.Add(new UsageRecord
            {
                Timestamp = DateTimeOffset.UtcNow,
                InstanceType = instanceType,
                Region = region,
                OnDemandCount = onDemandCount,
                ReservedCount = reservedCount,
                TotalCount = onDemandCount + reservedCount,
                CoveragePercent = onDemandCount + reservedCount > 0
                    ? (double)reservedCount / (onDemandCount + reservedCount) * 100
                    : 0
            });

            if (_usageHistory.Count > 10000) _usageHistory.Dequeue(); // O(1) vs O(n) RemoveAt(0)
        }
        RecordSample(onDemandCount + reservedCount, 0);
        EvaluateOptimizations();
    }

    /// <summary>Gets coverage analysis.</summary>
    public CoverageAnalysis GetCoverageAnalysis(TimeSpan? period = null)
    {
        var since = period.HasValue ? DateTimeOffset.UtcNow - period.Value : DateTimeOffset.UtcNow.AddDays(-30);

        lock (_lock)
        {
            var records = _usageHistory.Where(r => r.Timestamp >= since).ToList();
            if (!records.Any())
                return new CoverageAnalysis { HasData = false };

            var byType = records.GroupBy(r => $"{r.InstanceType}:{r.Region}")
                .Select(g => new TypeCoverage
                {
                    InstanceType = g.First().InstanceType,
                    Region = g.First().Region,
                    AvgCoverage = g.Average(r => r.CoveragePercent),
                    MaxOnDemand = g.Max(r => r.OnDemandCount),
                    AvgTotal = g.Average(r => r.TotalCount)
                }).ToList();

            return new CoverageAnalysis
            {
                HasData = true,
                OverallCoverage = records.Average(r => r.CoveragePercent),
                TotalOnDemandHours = records.Sum(r => r.OnDemandCount),
                TotalReservedHours = records.Sum(r => r.ReservedCount),
                ByInstanceType = byType
            };
        }
    }

    /// <summary>Gets purchase recommendations.</summary>
    public IReadOnlyList<ReservationRecommendation> GetPurchaseRecommendations()
    {
        var recommendations = new List<ReservationRecommendation>();
        var analysis = GetCoverageAnalysis(TimeSpan.FromDays(30));

        if (!analysis.HasData) return recommendations;

        foreach (var type in analysis.ByInstanceType.Where(t => t.AvgCoverage < MinCoveragePercent))
        {
            var targetCoverage = Math.Min(MinCoveragePercent + 10, 90);
            var neededQuantity = (int)Math.Ceiling(type.AvgTotal * (targetCoverage - type.AvgCoverage) / 100);

            if (neededQuantity > 0)
            {
                var estimatedMonthlySavings = neededQuantity * 100 * 0.3; // Rough estimate
                recommendations.Add(new ReservationRecommendation
                {
                    InstanceType = type.InstanceType,
                    Region = type.Region,
                    RecommendedQuantity = neededQuantity,
                    CurrentCoverage = type.AvgCoverage,
                    TargetCoverage = targetCoverage,
                    EstimatedMonthlySavingsUsd = estimatedMonthlySavings,
                    RecommendedTerm = 12,
                    Reason = $"Coverage at {type.AvgCoverage:F0}% below target {MinCoveragePercent}%"
                });
            }
        }

        return recommendations.OrderByDescending(r => r.EstimatedMonthlySavingsUsd).ToList();
    }

    /// <summary>Gets expiring reservations.</summary>
    public IReadOnlyList<ReservedCapacity> GetExpiringReservations(int withinDays = 30)
    {
        var threshold = DateTimeOffset.UtcNow.AddDays(withinDays);
        lock (_lock)
        {
            return _reservations.Values
                .Where(r => r.EndDate <= threshold)
                .OrderBy(r => r.EndDate)
                .ToList();
        }
    }

    private void EvaluateOptimizations()
    {
        ClearRecommendations();
        var analysis = GetCoverageAnalysis(TimeSpan.FromDays(7));

        if (analysis.HasData && analysis.OverallCoverage < MinCoveragePercent)
        {
            var recs = GetPurchaseRecommendations();
            var totalSavings = recs.Sum(r => r.EstimatedMonthlySavingsUsd);

            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-coverage",
                Type = "LowCoverage",
                Priority = 6,
                Description = $"RI coverage at {analysis.OverallCoverage:F0}%. Purchase recommendations available for ${totalSavings:F0}/month savings.",
                EstimatedCostSavingsUsd = totalSavings,
                CanAutoApply = false
            });
        }

        var expiring = GetExpiringReservations(30);
        if (expiring.Any())
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-expiring",
                Type = "ExpiringReservations",
                Priority = 7,
                Description = $"{expiring.Count} reservations expiring within 30 days. Review for renewal.",
                CanAutoApply = false
            });
        }
    }
}

/// <summary>Reserved capacity information.</summary>
public sealed record ReservedCapacity
{
    public required string ReservationId { get; init; }
    public required string InstanceType { get; init; }
    public required string Region { get; init; }
    public required int Quantity { get; init; }
    public required DateTimeOffset StartDate { get; init; }
    public required DateTimeOffset EndDate { get; init; }
    public required int TermMonths { get; init; }
    public required double MonthlyCostUsd { get; init; }
    public required double SavingsPercent { get; init; }
}

/// <summary>Usage record.</summary>
public sealed record UsageRecord
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string InstanceType { get; init; }
    public required string Region { get; init; }
    public required int OnDemandCount { get; init; }
    public required int ReservedCount { get; init; }
    public required int TotalCount { get; init; }
    public required double CoveragePercent { get; init; }
}

/// <summary>Coverage analysis.</summary>
public sealed record CoverageAnalysis
{
    public bool HasData { get; init; }
    public double OverallCoverage { get; init; }
    public long TotalOnDemandHours { get; init; }
    public long TotalReservedHours { get; init; }
    public List<TypeCoverage> ByInstanceType { get; init; } = new();
}

/// <summary>Coverage by instance type.</summary>
public sealed record TypeCoverage
{
    public required string InstanceType { get; init; }
    public required string Region { get; init; }
    public required double AvgCoverage { get; init; }
    public required int MaxOnDemand { get; init; }
    public required double AvgTotal { get; init; }
}

/// <summary>Reservation purchase recommendation.</summary>
public sealed record ReservationRecommendation
{
    public required string InstanceType { get; init; }
    public required string Region { get; init; }
    public required int RecommendedQuantity { get; init; }
    public required double CurrentCoverage { get; init; }
    public required double TargetCoverage { get; init; }
    public required double EstimatedMonthlySavingsUsd { get; init; }
    public required int RecommendedTerm { get; init; }
    public required string Reason { get; init; }
}
