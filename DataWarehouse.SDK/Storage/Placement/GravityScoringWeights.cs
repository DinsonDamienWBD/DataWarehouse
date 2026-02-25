using System.Linq;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Storage.Placement;

/// <summary>
/// Configurable weights for each dimension of the gravity scoring model.
/// All weights are 0.0-1.0. Final composite score is the weighted sum normalized to [0,1].
/// Higher composite score = higher gravity (harder to move).
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Gravity-aware placement")]
public sealed record GravityScoringWeights
{
    /// <summary>
    /// Weight for access frequency dimension. Hot data has high gravity.
    /// Default 0.30 — access patterns are the strongest gravity signal.
    /// </summary>
    public double AccessFrequency { get; init; } = 0.30;

    /// <summary>
    /// Weight for colocation dimension. Data near its consumers has high gravity.
    /// Default 0.20 — moving colocated data causes cascading latency increases.
    /// </summary>
    public double Colocation { get; init; } = 0.20;

    /// <summary>
    /// Weight for egress cost dimension. High-egress-cost locations increase gravity.
    /// Default 0.15 — moving data out of expensive locations incurs direct cost.
    /// </summary>
    public double EgressCost { get; init; } = 0.15;

    /// <summary>
    /// Weight for latency dimension. Low-latency placements increase gravity.
    /// Default 0.15 — data already in optimal latency position shouldn't move.
    /// </summary>
    public double Latency { get; init; } = 0.15;

    /// <summary>
    /// Weight for compliance dimension. Compliance-constrained data has maximum gravity.
    /// Default 0.20 — regulatory requirements override all cost optimization.
    /// </summary>
    public double Compliance { get; init; } = 0.20;

    /// <summary>
    /// Default scoring weights optimized for balanced cost/performance.
    /// </summary>
    public static GravityScoringWeights Default => new();

    /// <summary>
    /// Cost-optimized weights: prioritize egress cost and access frequency.
    /// </summary>
    public static GravityScoringWeights CostOptimized => new()
    {
        AccessFrequency = 0.20,
        Colocation = 0.10,
        EgressCost = 0.35,
        Latency = 0.10,
        Compliance = 0.25
    };

    /// <summary>
    /// Performance-optimized weights: prioritize latency and colocation.
    /// </summary>
    public static GravityScoringWeights PerformanceOptimized => new()
    {
        AccessFrequency = 0.35,
        Colocation = 0.30,
        EgressCost = 0.05,
        Latency = 0.25,
        Compliance = 0.05
    };

    /// <summary>
    /// Compliance-first weights: regulatory requirements dominate.
    /// </summary>
    public static GravityScoringWeights ComplianceFirst => new()
    {
        AccessFrequency = 0.10,
        Colocation = 0.10,
        EgressCost = 0.10,
        Latency = 0.10,
        Compliance = 0.60
    };

    /// <summary>
    /// Validates that weights are within range and sum to a positive value.
    /// </summary>
    public bool IsValid()
    {
        var weights = new[] { AccessFrequency, Colocation, EgressCost, Latency, Compliance };
        return weights.All(w => w >= 0.0 && w <= 1.0) && weights.Sum() > 0;
    }

    /// <summary>
    /// Normalize weights so they sum to 1.0.
    /// Returns <see cref="Default"/> if all weights are zero.
    /// </summary>
    public GravityScoringWeights Normalize()
    {
        double sum = AccessFrequency + Colocation + EgressCost + Latency + Compliance;
        if (sum == 0) return Default;
        return new GravityScoringWeights
        {
            AccessFrequency = AccessFrequency / sum,
            Colocation = Colocation / sum,
            EgressCost = EgressCost / sum,
            Latency = Latency / sum,
            Compliance = Compliance / sum
        };
    }
}
