using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies;

/// <summary>
/// Carbon-aware scheduling strategy using carbon intensity data (WattTime API pattern).
/// Routes workloads to regions with lower carbon intensity.
/// </summary>
public sealed class CarbonAwareSchedulingStrategy : SustainabilityStrategyBase
{
    private readonly BoundedDictionary<string, CarbonIntensityData> _intensityData = new BoundedDictionary<string, CarbonIntensityData>(1000);
    private readonly BoundedDictionary<string, List<CarbonSchedulingDecision>> _decisions = new BoundedDictionary<string, List<CarbonSchedulingDecision>>(1000);
    private readonly BoundedDictionary<string, SchedulingCarbonBudget> _budgets = new BoundedDictionary<string, SchedulingCarbonBudget>(1000);

    public override string StrategyId => "carbon-aware-scheduling";
    public override string DisplayName => "Carbon-Aware Scheduling";
    public override SustainabilityCategory Category => SustainabilityCategory.CarbonAwareness;
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.Scheduling | SustainabilityCapabilities.CarbonCalculation |
        SustainabilityCapabilities.ExternalIntegration | SustainabilityCapabilities.Reporting;
    public override string SemanticDescription =>
        "Carbon-aware scheduling that routes workloads to regions with lowest carbon intensity " +
        "using real-time grid carbon data with budget tracking.";
    public override string[] Tags => new[] { "carbon", "scheduling", "regions", "emissions" };

    /// <summary>
    /// Updates carbon intensity data for a region (from WattTime or similar API).
    /// </summary>
    public void UpdateCarbonIntensity(string region, double gCo2PerKwh, string gridOperator, DateTimeOffset validUntil)
    {
        _intensityData[region] = new CarbonIntensityData
        {
            Region = region,
            GCo2PerKwh = gCo2PerKwh,
            GridOperator = gridOperator,
            UpdatedAt = DateTimeOffset.UtcNow,
            ValidUntil = validUntil
        };
    }

    /// <summary>
    /// Recommends the best region for workload placement based on carbon intensity.
    /// </summary>
    public CarbonSchedulingRecommendation RecommendRegion(string workloadId, string[] candidateRegions,
        double estimatedKwh, bool preferRenewable = true)
    {
        var validRegions = candidateRegions
            .Where(r => _intensityData.ContainsKey(r))
            .Select(r => _intensityData[r])
            .Where(d => d.ValidUntil > DateTimeOffset.UtcNow)
            .OrderBy(d => d.GCo2PerKwh)
            .ToList();

        if (validRegions.Count == 0)
            return new CarbonSchedulingRecommendation { WorkloadId = workloadId, HasRecommendation = false };

        var best = validRegions.First();
        var worst = validRegions.Last();
        var estimatedEmissions = best.GCo2PerKwh * estimatedKwh;
        var worstEmissions = worst.GCo2PerKwh * estimatedKwh;

        var decision = new CarbonSchedulingDecision
        {
            WorkloadId = workloadId,
            SelectedRegion = best.Region,
            CarbonIntensity = best.GCo2PerKwh,
            EstimatedEmissionsGrams = estimatedEmissions,
            AvoidedEmissionsGrams = worstEmissions - estimatedEmissions,
            DecidedAt = DateTimeOffset.UtcNow
        };

        _decisions.AddOrUpdate(
            workloadId,
            _ => new List<CarbonSchedulingDecision> { decision },
            (_, list) => { lock (list) { list.Add(decision); } return list; });

        return new CarbonSchedulingRecommendation
        {
            WorkloadId = workloadId,
            HasRecommendation = true,
            RecommendedRegion = best.Region,
            CarbonIntensity = best.GCo2PerKwh,
            EstimatedEmissionsGrams = estimatedEmissions,
            CarbonSavingsGrams = worstEmissions - estimatedEmissions,
            RegionRankings = validRegions.Select((r, i) => new RegionCarbonRanking
            {
                Rank = i + 1,
                Region = r.Region,
                GCo2PerKwh = r.GCo2PerKwh,
                EstimatedEmissionsGrams = r.GCo2PerKwh * estimatedKwh
            }).ToList()
        };
    }

    /// <summary>
    /// Initializes a carbon budget for a project/workload group.
    /// </summary>
    public SchedulingCarbonBudget InitializeBudget(string budgetId, double totalBudgetGrams, TimeSpan period)
    {
        var budget = new SchedulingCarbonBudget
        {
            BudgetId = budgetId,
            TotalBudgetGrams = totalBudgetGrams,
            ConsumedGrams = 0,
            RemainingGrams = totalBudgetGrams,
            Period = period,
            StartedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.Add(period)
        };
        _budgets[budgetId] = budget;
        return budget;
    }

    /// <summary>
    /// Consumes carbon budget.
    /// </summary>
    public CarbonBudgetResult ConsumeBudget(string budgetId, double emissionsGrams)
    {
        if (!_budgets.TryGetValue(budgetId, out var budget))
            return new CarbonBudgetResult { Allowed = false, Reason = "Budget not found" };

        if (budget.ConsumedGrams + emissionsGrams > budget.TotalBudgetGrams)
            return new CarbonBudgetResult
            {
                Allowed = false,
                Reason = $"Would exceed budget: {budget.ConsumedGrams + emissionsGrams:F1}g > {budget.TotalBudgetGrams:F1}g"
            };

        var updated = budget with
        {
            ConsumedGrams = budget.ConsumedGrams + emissionsGrams,
            RemainingGrams = budget.TotalBudgetGrams - budget.ConsumedGrams - emissionsGrams
        };
        _budgets[budgetId] = updated;

        return new CarbonBudgetResult
        {
            Allowed = true,
            ConsumedGrams = updated.ConsumedGrams,
            RemainingGrams = updated.RemainingGrams,
            BudgetUtilization = updated.ConsumedGrams / updated.TotalBudgetGrams
        };
    }

    /// <summary>
    /// Gets current carbon intensity for all tracked regions.
    /// </summary>
    public IReadOnlyList<CarbonIntensityData> GetCurrentIntensity() =>
        _intensityData.Values.OrderBy(d => d.GCo2PerKwh).ToList().AsReadOnly();
}

/// <summary>
/// Energy consumption tracking per operation with GHG Protocol reporting.
/// </summary>
public sealed class EnergyTrackingStrategy : SustainabilityStrategyBase
{
    private readonly BoundedDictionary<string, List<TrackingEnergyMeasurement>> _measurements = new BoundedDictionary<string, List<TrackingEnergyMeasurement>>(1000);
    private readonly BoundedDictionary<string, GhgReport> _ghgRecords = new BoundedDictionary<string, GhgReport>(1000);

    public override string StrategyId => "energy-tracking";
    public override string DisplayName => "Energy Consumption Tracking";
    public override SustainabilityCategory Category => SustainabilityCategory.EnergyOptimization;
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.RealTimeMonitoring | SustainabilityCapabilities.CarbonCalculation |
        SustainabilityCapabilities.Reporting;
    public override string SemanticDescription =>
        "Energy consumption tracking per operation with GHG Protocol Scope 1/2/3 reporting.";
    public override string[] Tags => new[] { "energy", "tracking", "ghg", "emissions", "reporting" };

    /// <summary>
    /// Records energy consumption for an operation.
    /// </summary>
    public void RecordConsumption(string operationId, string operationType, double kwhConsumed,
        string region, double? carbonIntensity = null)
    {
        var measurement = new TrackingEnergyMeasurement
        {
            OperationId = operationId,
            OperationType = operationType,
            KwhConsumed = kwhConsumed,
            Region = region,
            CarbonIntensityGCo2PerKwh = carbonIntensity ?? 400, // Default global average
            EmissionsGCo2 = kwhConsumed * (carbonIntensity ?? 400),
            Timestamp = DateTimeOffset.UtcNow
        };

        _measurements.AddOrUpdate(
            operationType,
            _ => new List<TrackingEnergyMeasurement> { measurement },
            (_, list) => { lock (list) { list.Add(measurement); if (list.Count > 100000) list.RemoveAt(0); } return list; });
    }

    /// <summary>
    /// Generates GHG Protocol Scope 1/2/3 emissions report.
    /// </summary>
    public GhgReport GenerateGhgReport(DateTimeOffset from, DateTimeOffset to)
    {
        var allMeasurements = _measurements.Values
            .SelectMany(l => { lock (l) { return l.ToList(); } })
            .Where(m => m.Timestamp >= from && m.Timestamp <= to)
            .ToList();

        var totalKwh = allMeasurements.Sum(m => m.KwhConsumed);
        var totalEmissionsG = allMeasurements.Sum(m => m.EmissionsGCo2);

        // Categorize by GHG scope
        var scope1 = 0.0; // Direct emissions (on-premise generators)
        var scope2 = totalEmissionsG; // Indirect from purchased electricity
        var scope3 = totalEmissionsG * 0.15; // Upstream/downstream (estimated)

        var byType = allMeasurements
            .GroupBy(m => m.OperationType)
            .Select(g => new OperationEnergyBreakdown
            {
                OperationType = g.Key,
                TotalKwh = g.Sum(m => m.KwhConsumed),
                TotalEmissionsGCo2 = g.Sum(m => m.EmissionsGCo2),
                OperationCount = g.Count()
            })
            .OrderByDescending(o => o.TotalKwh)
            .ToList();

        var byRegion = allMeasurements
            .GroupBy(m => m.Region)
            .Select(g => new RegionEnergyBreakdown
            {
                Region = g.Key,
                TotalKwh = g.Sum(m => m.KwhConsumed),
                AverageCarbonIntensity = g.Average(m => m.CarbonIntensityGCo2PerKwh),
                TotalEmissionsGCo2 = g.Sum(m => m.EmissionsGCo2)
            })
            .OrderByDescending(r => r.TotalEmissionsGCo2)
            .ToList();

        return new GhgReport
        {
            ReportPeriodFrom = from,
            ReportPeriodTo = to,
            TotalEnergyKwh = totalKwh,
            Scope1EmissionsGCo2 = scope1,
            Scope2EmissionsGCo2 = scope2,
            Scope3EmissionsGCo2 = scope3,
            TotalEmissionsKgCo2 = (scope1 + scope2 + scope3) / 1000,
            ByOperationType = byType,
            ByRegion = byRegion,
            MeasurementCount = allMeasurements.Count,
            GeneratedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Gets energy consumption summary.
    /// </summary>
    public EnergySummary GetSummary()
    {
        var allMeasurements = _measurements.Values
            .SelectMany(l => { lock (l) { return l.ToList(); } })
            .ToList();

        return new EnergySummary
        {
            TotalOperations = allMeasurements.Count,
            TotalKwhConsumed = allMeasurements.Sum(m => m.KwhConsumed),
            TotalEmissionsGCo2 = allMeasurements.Sum(m => m.EmissionsGCo2),
            UniqueOperationTypes = allMeasurements.Select(m => m.OperationType).Distinct().Count(),
            UniqueRegions = allMeasurements.Select(m => m.Region).Distinct().Count()
        };
    }
}

#region Models

public sealed record CarbonIntensityData
{
    public required string Region { get; init; }
    public double GCo2PerKwh { get; init; }
    public required string GridOperator { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset ValidUntil { get; init; }
}

public sealed record CarbonSchedulingRecommendation
{
    public required string WorkloadId { get; init; }
    public bool HasRecommendation { get; init; }
    public string? RecommendedRegion { get; init; }
    public double CarbonIntensity { get; init; }
    public double EstimatedEmissionsGrams { get; init; }
    public double CarbonSavingsGrams { get; init; }
    public List<RegionCarbonRanking> RegionRankings { get; init; } = new();
}

public sealed record RegionCarbonRanking
{
    public int Rank { get; init; }
    public required string Region { get; init; }
    public double GCo2PerKwh { get; init; }
    public double EstimatedEmissionsGrams { get; init; }
}

public sealed record CarbonSchedulingDecision
{
    public required string WorkloadId { get; init; }
    public required string SelectedRegion { get; init; }
    public double CarbonIntensity { get; init; }
    public double EstimatedEmissionsGrams { get; init; }
    public double AvoidedEmissionsGrams { get; init; }
    public DateTimeOffset DecidedAt { get; init; }
}

public sealed record SchedulingCarbonBudget
{
    public required string BudgetId { get; init; }
    public double TotalBudgetGrams { get; init; }
    public double ConsumedGrams { get; init; }
    public double RemainingGrams { get; init; }
    public TimeSpan Period { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed record CarbonBudgetResult
{
    public bool Allowed { get; init; }
    public string? Reason { get; init; }
    public double ConsumedGrams { get; init; }
    public double RemainingGrams { get; init; }
    public double BudgetUtilization { get; init; }
}

public sealed record TrackingEnergyMeasurement
{
    public required string OperationId { get; init; }
    public required string OperationType { get; init; }
    public double KwhConsumed { get; init; }
    public required string Region { get; init; }
    public double CarbonIntensityGCo2PerKwh { get; init; }
    public double EmissionsGCo2 { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

public sealed record GhgReport
{
    public DateTimeOffset ReportPeriodFrom { get; init; }
    public DateTimeOffset ReportPeriodTo { get; init; }
    public double TotalEnergyKwh { get; init; }
    public double Scope1EmissionsGCo2 { get; init; }
    public double Scope2EmissionsGCo2 { get; init; }
    public double Scope3EmissionsGCo2 { get; init; }
    public double TotalEmissionsKgCo2 { get; init; }
    public List<OperationEnergyBreakdown> ByOperationType { get; init; } = new();
    public List<RegionEnergyBreakdown> ByRegion { get; init; } = new();
    public int MeasurementCount { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
}

public sealed record OperationEnergyBreakdown
{
    public required string OperationType { get; init; }
    public double TotalKwh { get; init; }
    public double TotalEmissionsGCo2 { get; init; }
    public int OperationCount { get; init; }
}

public sealed record RegionEnergyBreakdown
{
    public required string Region { get; init; }
    public double TotalKwh { get; init; }
    public double AverageCarbonIntensity { get; init; }
    public double TotalEmissionsGCo2 { get; init; }
}

public sealed record EnergySummary
{
    public int TotalOperations { get; init; }
    public double TotalKwhConsumed { get; init; }
    public double TotalEmissionsGCo2 { get; init; }
    public int UniqueOperationTypes { get; init; }
    public int UniqueRegions { get; init; }
}

#endregion
