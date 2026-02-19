using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Storage.Billing;

/// <summary>
/// Cross-provider storage cost optimizer.
/// Analyzes billing data from all configured providers and generates actionable optimization plans.
///
/// Optimization strategies:
/// <list type="number">
///   <item><description>SPOT: Move reproducible/temporary data to spot storage for 60-90% savings</description></item>
///   <item><description>RESERVED: Commit stable workloads to reserved capacity for 20-60% savings</description></item>
///   <item><description>TIER: Transition cold data to cheaper tiers (e.g., S3 Standard to Glacier)</description></item>
///   <item><description>ARBITRAGE: Move data between providers when price differences exceed egress cost</description></item>
/// </list>
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Storage cost optimizer")]
public sealed class StorageCostOptimizer
{
    private readonly IReadOnlyList<IBillingProvider> _providers;
    private readonly StorageCostOptimizerOptions _options;

    /// <summary>
    /// Creates a new cost optimizer that analyzes billing across the given providers.
    /// </summary>
    /// <param name="providers">Billing providers to analyze (at least one required).</param>
    /// <param name="options">Optimization thresholds and configuration. Uses defaults if null.</param>
    public StorageCostOptimizer(
        IReadOnlyList<IBillingProvider> providers,
        StorageCostOptimizerOptions? options = null)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _options = options ?? new StorageCostOptimizerOptions();
    }

    /// <summary>
    /// Generates a comprehensive optimization plan by analyzing billing data from all
    /// configured providers. Fetches billing reports, spot pricing, and reserved capacity
    /// in parallel, then generates recommendations across all four strategy categories.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An optimization plan with categorized recommendations and savings summary.</returns>
    public async Task<OptimizationPlan> GenerateOptimizationPlanAsync(CancellationToken ct = default)
    {
        // Gather billing data from all providers in parallel
        var billingTasks = _providers.Select(p =>
            p.GetBillingReportAsync(
                DateTimeOffset.UtcNow.AddDays(-30),
                DateTimeOffset.UtcNow, ct));
        var billingReports = await Task.WhenAll(billingTasks).ConfigureAwait(false);

        var spotTasks = _providers.Select(p => p.GetSpotPricingAsync(ct: ct));
        var spotPricing = (await Task.WhenAll(spotTasks).ConfigureAwait(false))
            .SelectMany(s => s).ToList();

        var reservedTasks = _providers.Select(p => p.GetReservedCapacityAsync(ct));
        var reservedCapacity = (await Task.WhenAll(reservedTasks).ConfigureAwait(false))
            .SelectMany(r => r).ToList();

        // Generate recommendations across all four strategies
        var spotRecs = GenerateSpotRecommendations(billingReports, spotPricing);
        var reservedRecs = GenerateReservedRecommendations(billingReports, reservedCapacity);
        var tierRecs = GenerateTierRecommendations(billingReports);
        var arbitrageRecs = GenerateArbitrageRecommendations(billingReports, spotPricing);

        // Calculate aggregated savings summary
        decimal currentMonthlyCost = billingReports.Sum(r => r.TotalCost);
        decimal spotSavings = spotRecs.Sum(r => r.MonthlySavings);
        decimal reservedSavings = reservedRecs.Sum(r => r.MonthlySavings);
        decimal tierSavings = tierRecs.Sum(r => r.MonthlySavings);
        decimal arbitrageSavings = arbitrageRecs.Sum(r => r.MonthlySavings);
        decimal totalSavings = spotSavings + reservedSavings + tierSavings + arbitrageSavings;
        decimal implementationCost = tierRecs.Sum(r => r.TransitionCost) + arbitrageRecs.Sum(r => r.EgressCost);

        return new OptimizationPlan
        {
            Summary = new SavingsSummary
            {
                CurrentMonthlyCost = currentMonthlyCost,
                ProjectedMonthlyCost = currentMonthlyCost - totalSavings,
                EstimatedMonthlySavings = totalSavings,
                SavingsPercent = currentMonthlyCost > 0
                    ? (double)(totalSavings / currentMonthlyCost * 100)
                    : 0,
                ImplementationCost = implementationCost,
                BreakEvenDays = totalSavings > 0
                    ? (int)Math.Ceiling(implementationCost / (totalSavings / 30m))
                    : 0,
                TotalRecommendations = spotRecs.Count + reservedRecs.Count
                    + tierRecs.Count + arbitrageRecs.Count,
                HighConfidenceRecommendations =
                    spotRecs.Count(r => r.ConfidenceScore > 0.8)
                    + reservedRecs.Count(r => r.UtilizationConfidence > 0.8)
            },
            SpotRecommendations = spotRecs,
            ReservedRecommendations = reservedRecs,
            TierRecommendations = tierRecs,
            ArbitrageRecommendations = arbitrageRecs
        };
    }

    /// <summary>
    /// Identifies spot storage opportunities by comparing on-demand pricing with
    /// spot pricing, filtering by interruption risk tolerance and minimum savings thresholds.
    /// </summary>
    private IReadOnlyList<SpotStorageRecommendation> GenerateSpotRecommendations(
        BillingReport[] reports, IReadOnlyList<SpotPricing> spotPricing)
    {
        var recs = new List<SpotStorageRecommendation>();
        foreach (var spot in spotPricing.Where(s => s.SavingsPercent > _options.MinSpotSavingsPercent))
        {
            if (spot.InterruptionProbability > _options.MaxSpotInterruptionRisk)
                continue;

            decimal monthlySavings = (spot.CurrentPricePerGBMonth - spot.SpotPricePerGBMonth)
                * spot.AvailableCapacityGB;

            if (monthlySavings < _options.MinMonthlySavings)
                continue;

            recs.Add(new SpotStorageRecommendation
            {
                Provider = spot.Provider,
                Region = spot.Region,
                StorageClass = spot.StorageClass,
                DataSizeGB = spot.AvailableCapacityGB,
                CurrentCostPerGBMonth = spot.CurrentPricePerGBMonth,
                SpotCostPerGBMonth = spot.SpotPricePerGBMonth,
                MonthlySavings = monthlySavings,
                InterruptionRisk = spot.InterruptionProbability,
                ConfidenceScore = 1.0 - spot.InterruptionProbability
            });
        }
        return recs.OrderByDescending(r => r.MonthlySavings).ToList();
    }

    /// <summary>
    /// Analyzes stable storage usage patterns from billing reports to identify
    /// reserved capacity opportunities. Skips regions with existing reservations.
    /// Estimates a typical 35% discount for 12-month commitments.
    /// </summary>
    private IReadOnlyList<ReservedCapacityRecommendation> GenerateReservedRecommendations(
        BillingReport[] reports, IReadOnlyList<ReservedCapacity> existing)
    {
        var recs = new List<ReservedCapacityRecommendation>();

        foreach (var report in reports)
        {
            var storageCosts = report.Breakdown
                .Where(b => b.Category == CostCategory.Storage)
                .ToList();

            foreach (var cost in storageCosts)
            {
                // Skip regions that already have reserved capacity
                bool alreadyReserved = existing.Any(r =>
                    r.Provider == report.Provider && r.Region == cost.Region);
                if (alreadyReserved) continue;

                // Estimate stable GB from billing quantity
                decimal estimatedGB = cost.Quantity > 0 ? (decimal)cost.Quantity : 0;
                if (estimatedGB < _options.MinReservedCapacityGB) continue;

                // Calculate 1-year reserved savings (typical: 35% discount)
                decimal onDemandMonthly = cost.Amount;
                decimal reservedMonthly = onDemandMonthly * 0.65m;
                decimal monthlySavings = onDemandMonthly - reservedMonthly;

                if (monthlySavings < _options.MinMonthlySavings) continue;

                recs.Add(new ReservedCapacityRecommendation
                {
                    Provider = report.Provider,
                    Region = cost.Region ?? "unknown",
                    StorageClass = cost.ServiceName,
                    CommitGB = (long)estimatedGB,
                    TermMonths = 12,
                    OnDemandCostPerGBMonth = estimatedGB > 0 ? onDemandMonthly / estimatedGB : 0,
                    ReservedCostPerGBMonth = estimatedGB > 0 ? reservedMonthly / estimatedGB : 0,
                    MonthlySavings = monthlySavings,
                    BreakEvenMonths = 0, // reserved pricing saves immediately vs on-demand
                    UtilizationConfidence = 0.85 // 30 days of stable usage indicates high confidence
                });
            }
        }
        return recs.OrderByDescending(r => r.MonthlySavings).ToList();
    }

    /// <summary>
    /// Analyzes access patterns in billing data to identify data suitable for tier transitions.
    /// High storage cost combined with low operation counts indicates cold data that
    /// should be moved to archive-class storage (typically 75% cheaper).
    /// </summary>
    private IReadOnlyList<TierTransitionRecommendation> GenerateTierRecommendations(
        BillingReport[] reports)
    {
        var recs = new List<TierTransitionRecommendation>();

        foreach (var report in reports)
        {
            var storageCosts = report.Breakdown.Where(b => b.Category == CostCategory.Storage);
            var operationCosts = report.Breakdown.Where(b => b.Category == CostCategory.Operations).ToList();

            foreach (var storage in storageCosts)
            {
                // Low operation count relative to storage = candidate for cold tier
                var relatedOps = operationCosts.FirstOrDefault(o => o.Region == storage.Region);
                double opsPerDay = relatedOps != null ? relatedOps.Quantity / 30.0 : 0;

                if (opsPerDay >= _options.ColdTierAccessThresholdPerDay)
                    continue;

                if (storage.Amount <= _options.MinMonthlySavings)
                    continue;

                decimal coldTierCost = storage.Amount * 0.25m; // cold tier typically 75% cheaper
                decimal transitionCost = storage.Amount * 0.05m; // one-time transition fee estimate
                decimal monthlySavings = storage.Amount - coldTierCost;
                long sizeGB = (long)Math.Max(1, storage.Quantity);

                recs.Add(new TierTransitionRecommendation
                {
                    ObjectKeyPattern = $"{storage.Region}/{storage.ServiceName}/*",
                    AffectedSizeGB = sizeGB,
                    CurrentTier = "Standard",
                    RecommendedTier = "Cold/Archive",
                    AccessFrequencyPerDay = opsPerDay,
                    CurrentCostPerGBMonth = sizeGB > 0 ? storage.Amount / (decimal)sizeGB : 0,
                    RecommendedCostPerGBMonth = sizeGB > 0 ? coldTierCost / (decimal)sizeGB : 0,
                    TransitionCost = transitionCost,
                    MonthlySavings = monthlySavings,
                    BreakEvenDays = monthlySavings > 0
                        ? (int)Math.Ceiling(transitionCost / (monthlySavings / 30m))
                        : int.MaxValue
                });
            }
        }
        return recs.OrderByDescending(r => r.MonthlySavings).ToList();
    }

    /// <summary>
    /// Compares storage costs across providers to identify cross-provider arbitrage
    /// opportunities. Requires at least 2 providers to compare. Factors in egress costs
    /// and requires a minimum 3-month break-even period.
    /// </summary>
    private IReadOnlyList<CrossProviderArbitrageRecommendation> GenerateArbitrageRecommendations(
        BillingReport[] reports, IReadOnlyList<SpotPricing> spotPricing)
    {
        var recs = new List<CrossProviderArbitrageRecommendation>();
        if (reports.Length < 2) return recs; // need at least 2 providers for arbitrage

        // Compare storage costs pairwise across providers
        for (int i = 0; i < reports.Length; i++)
        {
            for (int j = i + 1; j < reports.Length; j++)
            {
                var expensive = reports[i].TotalCost > reports[j].TotalCost ? reports[i] : reports[j];
                var cheaper = reports[i].TotalCost > reports[j].TotalCost ? reports[j] : reports[i];

                decimal priceDiff = expensive.TotalCost - cheaper.TotalCost;
                if (priceDiff < _options.MinMonthlySavings) continue;

                // Estimate egress cost (typical $0.09/GB across major cloud providers)
                decimal totalGB = expensive.Breakdown
                    .Where(b => b.Category == CostCategory.Storage)
                    .Sum(b => (decimal)b.Quantity);
                decimal egressCost = totalGB * 0.09m;

                // Require savings to cover egress within 3 months minimum
                if (priceDiff <= egressCost / 3) continue;

                recs.Add(new CrossProviderArbitrageRecommendation
                {
                    SourceProvider = expensive.Provider,
                    TargetProvider = cheaper.Provider,
                    DataCategory = "Storage",
                    DataSizeGB = (long)totalGB,
                    SourceCostPerGBMonth = totalGB > 0 ? expensive.TotalCost / totalGB : 0,
                    TargetCostPerGBMonth = totalGB > 0 ? cheaper.TotalCost / totalGB : 0,
                    EgressCost = egressCost,
                    MonthlySavings = priceDiff,
                    BreakEvenDays = priceDiff > 0
                        ? (int)Math.Ceiling(egressCost / (priceDiff / 30m))
                        : int.MaxValue,
                    RiskAssessment = priceDiff > expensive.TotalCost * 0.3m ? "Low" : "Medium"
                });
            }
        }
        return recs.OrderByDescending(r => r.MonthlySavings).ToList();
    }
}

/// <summary>
/// Configuration for cost optimizer thresholds controlling which recommendations
/// are generated based on minimum savings, risk tolerance, and capacity requirements.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Storage cost optimizer")]
public sealed record StorageCostOptimizerOptions
{
    /// <summary>Minimum spot savings percentage to recommend (default 20%).</summary>
    public double MinSpotSavingsPercent { get; init; } = 20.0;

    /// <summary>Maximum acceptable spot interruption risk (default 10%).</summary>
    public double MaxSpotInterruptionRisk { get; init; } = 0.10;

    /// <summary>Minimum GB for reserved capacity recommendation (default 100 GB).</summary>
    public long MinReservedCapacityGB { get; init; } = 100;

    /// <summary>Minimum monthly savings to generate a recommendation (default $10).</summary>
    public decimal MinMonthlySavings { get; init; } = 10.00m;

    /// <summary>Access frequency threshold below which data is considered cold (per day, default 1.0).</summary>
    public double ColdTierAccessThresholdPerDay { get; init; } = 1.0;
}
