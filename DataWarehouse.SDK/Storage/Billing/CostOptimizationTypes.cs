using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Storage.Billing;

/// <summary>
/// A comprehensive optimization plan containing recommendations across all optimization strategies
/// (spot storage, reserved capacity, tier transitions, and cross-provider arbitrage) with an
/// aggregated savings summary.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Storage cost optimizer")]
public sealed record OptimizationPlan
{
    /// <summary>Unique identifier for this optimization plan.</summary>
    public string PlanId { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Timestamp when the plan was generated.</summary>
    public DateTimeOffset GeneratedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Aggregated savings summary across all recommendation categories.</summary>
    public SavingsSummary Summary { get; init; } = new();

    /// <summary>Recommendations to move reproducible/temporary data to spot storage.</summary>
    public IReadOnlyList<SpotStorageRecommendation> SpotRecommendations { get; init; } = Array.Empty<SpotStorageRecommendation>();

    /// <summary>Recommendations to commit stable workloads to reserved capacity.</summary>
    public IReadOnlyList<ReservedCapacityRecommendation> ReservedRecommendations { get; init; } = Array.Empty<ReservedCapacityRecommendation>();

    /// <summary>Recommendations to transition cold data to cheaper storage tiers.</summary>
    public IReadOnlyList<TierTransitionRecommendation> TierRecommendations { get; init; } = Array.Empty<TierTransitionRecommendation>();

    /// <summary>Recommendations to move data between providers for cost arbitrage.</summary>
    public IReadOnlyList<CrossProviderArbitrageRecommendation> ArbitrageRecommendations { get; init; } = Array.Empty<CrossProviderArbitrageRecommendation>();
}

/// <summary>
/// Aggregated savings summary providing the financial overview of an optimization plan,
/// including current costs, projected costs, break-even analysis, and recommendation counts.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Storage cost optimizer")]
public sealed record SavingsSummary
{
    /// <summary>Current monthly cost across all analyzed providers.</summary>
    public decimal CurrentMonthlyCost { get; init; }

    /// <summary>Projected monthly cost after implementing all recommendations.</summary>
    public decimal ProjectedMonthlyCost { get; init; }

    /// <summary>Total estimated monthly savings from all recommendations.</summary>
    public decimal EstimatedMonthlySavings { get; init; }

    /// <summary>Savings as a percentage of current cost.</summary>
    public double SavingsPercent { get; init; }

    /// <summary>One-time migration/transition cost to implement recommendations.</summary>
    public decimal ImplementationCost { get; init; }

    /// <summary>Days until implementation costs are recovered from savings.</summary>
    public int BreakEvenDays { get; init; }

    /// <summary>Total number of recommendations across all categories.</summary>
    public int TotalRecommendations { get; init; }

    /// <summary>Number of recommendations with confidence score above 0.8.</summary>
    public int HighConfidenceRecommendations { get; init; }
}

/// <summary>
/// Recommendation to move data to spot storage for significant savings,
/// accounting for interruption risk and data reproducibility requirements.
/// Spot storage typically offers 60-90% savings for interruptible workloads.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Storage cost optimizer")]
public sealed record SpotStorageRecommendation
{
    /// <summary>Cloud provider offering the spot storage.</summary>
    public CloudProvider Provider { get; init; }

    /// <summary>Region where spot storage is available.</summary>
    public string Region { get; init; } = "";

    /// <summary>Storage class for the spot offering.</summary>
    public string StorageClass { get; init; } = "";

    /// <summary>Amount of data eligible for spot storage (GB).</summary>
    public long DataSizeGB { get; init; }

    /// <summary>Current on-demand cost per GB per month.</summary>
    public decimal CurrentCostPerGBMonth { get; init; }

    /// <summary>Spot price per GB per month.</summary>
    public decimal SpotCostPerGBMonth { get; init; }

    /// <summary>Estimated monthly savings from switching to spot.</summary>
    public decimal MonthlySavings { get; init; }

    /// <summary>Probability of spot storage interruption (0.0 to 1.0).</summary>
    public double InterruptionRisk { get; init; }

    /// <summary>Whether the data must be reproducible (spot storage may be reclaimed).</summary>
    public bool RequiresReproducibleData { get; init; } = true;

    /// <summary>Confidence score for this recommendation (0.0 to 1.0).</summary>
    public double ConfidenceScore { get; init; }
}

/// <summary>
/// Recommendation to commit to reserved capacity for stable workloads,
/// offering 20-60% savings in exchange for a term commitment.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Storage cost optimizer")]
public sealed record ReservedCapacityRecommendation
{
    /// <summary>Cloud provider for the reserved capacity.</summary>
    public CloudProvider Provider { get; init; }

    /// <summary>Region for the reserved capacity.</summary>
    public string Region { get; init; } = "";

    /// <summary>Storage class being reserved.</summary>
    public string StorageClass { get; init; } = "";

    /// <summary>Amount of capacity to commit (GB).</summary>
    public long CommitGB { get; init; }

    /// <summary>Duration of the commitment in months.</summary>
    public int TermMonths { get; init; }

    /// <summary>Current on-demand cost per GB per month.</summary>
    public decimal OnDemandCostPerGBMonth { get; init; }

    /// <summary>Reserved cost per GB per month.</summary>
    public decimal ReservedCostPerGBMonth { get; init; }

    /// <summary>Estimated monthly savings from reserved pricing.</summary>
    public decimal MonthlySavings { get; init; }

    /// <summary>Months until the commitment pays for itself.</summary>
    public int BreakEvenMonths { get; init; }

    /// <summary>Probability that storage will be utilized for the full term (0.0 to 1.0).</summary>
    public double UtilizationConfidence { get; init; }
}

/// <summary>
/// Recommendation to transition data to a cheaper storage tier based on access pattern analysis.
/// Cold/archive tiers are typically 75% cheaper than standard tiers for infrequently accessed data.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Storage cost optimizer")]
public sealed record TierTransitionRecommendation
{
    /// <summary>Pattern matching the affected object keys.</summary>
    public string ObjectKeyPattern { get; init; } = "";

    /// <summary>Number of objects affected by this transition.</summary>
    public long AffectedObjectCount { get; init; }

    /// <summary>Total size of affected objects (GB).</summary>
    public long AffectedSizeGB { get; init; }

    /// <summary>Current storage tier name.</summary>
    public string CurrentTier { get; init; } = "";

    /// <summary>Recommended storage tier name.</summary>
    public string RecommendedTier { get; init; } = "";

    /// <summary>Average number of accesses per day for affected objects.</summary>
    public double AccessFrequencyPerDay { get; init; }

    /// <summary>Days since the most recent access to affected objects.</summary>
    public int DaysSinceLastAccess { get; init; }

    /// <summary>Current cost per GB per month in the current tier.</summary>
    public decimal CurrentCostPerGBMonth { get; init; }

    /// <summary>Cost per GB per month in the recommended tier.</summary>
    public decimal RecommendedCostPerGBMonth { get; init; }

    /// <summary>One-time cost to transition data between tiers.</summary>
    public decimal TransitionCost { get; init; }

    /// <summary>Estimated monthly savings after transition.</summary>
    public decimal MonthlySavings { get; init; }

    /// <summary>Days until transition cost is recovered from savings.</summary>
    public int BreakEvenDays { get; init; }
}

/// <summary>
/// Recommendation to move data between cloud providers when price differences
/// exceed egress costs, enabling cost arbitrage across the multi-cloud estate.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Storage cost optimizer")]
public sealed record CrossProviderArbitrageRecommendation
{
    /// <summary>Current (more expensive) cloud provider.</summary>
    public CloudProvider SourceProvider { get; init; }

    /// <summary>Target (cheaper) cloud provider.</summary>
    public CloudProvider TargetProvider { get; init; }

    /// <summary>Category of data being considered for migration.</summary>
    public string DataCategory { get; init; } = "";

    /// <summary>Amount of data to migrate (GB).</summary>
    public long DataSizeGB { get; init; }

    /// <summary>Cost per GB per month at the source provider.</summary>
    public decimal SourceCostPerGBMonth { get; init; }

    /// <summary>Cost per GB per month at the target provider.</summary>
    public decimal TargetCostPerGBMonth { get; init; }

    /// <summary>One-time egress cost to transfer data out of source provider.</summary>
    public decimal EgressCost { get; init; }

    /// <summary>Estimated monthly savings after migration.</summary>
    public decimal MonthlySavings { get; init; }

    /// <summary>Days until egress cost is recovered from monthly savings.</summary>
    public int BreakEvenDays { get; init; }

    /// <summary>Estimated latency impact of moving data to another provider (ms).</summary>
    public double LatencyImpactMs { get; init; }

    /// <summary>Overall risk assessment for this arbitrage opportunity.</summary>
    public string RiskAssessment { get; init; } = "";
}
