using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Storage.Billing;
using Xunit;

namespace DataWarehouse.Tests.Storage.ZeroGravity;

/// <summary>
/// Tests for storage cost optimizer.
/// Verifies: empty provider handling, spot recommendations, tier recommendations,
/// arbitrage recommendations, savings summary calculation, and break-even computation.
/// </summary>
public sealed class CostOptimizerTests
{
    #region Helpers

    private sealed class TestBillingProvider : IBillingProvider
    {
        private readonly BillingReport _report;
        private readonly IReadOnlyList<SpotPricing> _spotPricing;
        private readonly IReadOnlyList<ReservedCapacity> _reservedCapacity;

        public TestBillingProvider(
            CloudProvider provider,
            BillingReport? report = null,
            IReadOnlyList<SpotPricing>? spotPricing = null,
            IReadOnlyList<ReservedCapacity>? reservedCapacity = null)
        {
            Provider = provider;
            _report = report ?? new BillingReport(
                ProviderId: provider.ToString(),
                Provider: provider,
                PeriodStart: DateTimeOffset.UtcNow.AddDays(-30),
                PeriodEnd: DateTimeOffset.UtcNow,
                TotalCost: 0m,
                Currency: "USD",
                Breakdown: Array.Empty<CostBreakdown>());
            _spotPricing = spotPricing ?? Array.Empty<SpotPricing>();
            _reservedCapacity = reservedCapacity ?? Array.Empty<ReservedCapacity>();
        }

        public CloudProvider Provider { get; }

        public Task<BillingReport> GetBillingReportAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
            => Task.FromResult(_report);

        public Task<IReadOnlyList<SpotPricing>> GetSpotPricingAsync(string? region = null, CancellationToken ct = default)
            => Task.FromResult(_spotPricing);

        public Task<IReadOnlyList<ReservedCapacity>> GetReservedCapacityAsync(CancellationToken ct = default)
            => Task.FromResult(_reservedCapacity);

        public Task<CostForecast> ForecastCostAsync(int days, CancellationToken ct = default)
            => Task.FromResult(new CostForecast(Provider, days, 0m, 50.0, Array.Empty<CostRecommendation>()));

        public Task<bool> ValidateCredentialsAsync(CancellationToken ct = default)
            => Task.FromResult(true);
    }

    #endregion

    #region Empty Providers

    [Fact]
    public async Task GeneratePlan_NoProviders_EmptyRecommendations()
    {
        var optimizer = new StorageCostOptimizer(Array.Empty<IBillingProvider>());

        var plan = await optimizer.GenerateOptimizationPlanAsync();

        Assert.Empty(plan.SpotRecommendations);
        Assert.Empty(plan.ReservedRecommendations);
        Assert.Empty(plan.TierRecommendations);
        Assert.Empty(plan.ArbitrageRecommendations);
        Assert.Equal(0, plan.Summary.TotalRecommendations);
    }

    #endregion

    #region Spot Recommendations

    [Fact]
    public async Task SpotRecommendations_HighSavings_Included()
    {
        var spotPricing = new List<SpotPricing>
        {
            new SpotPricing(
                Provider: CloudProvider.AWS,
                Region: "us-east-1",
                StorageClass: "Standard",
                CurrentPricePerGBMonth: 0.023m,
                SpotPricePerGBMonth: 0.010m,
                SavingsPercent: 56.5,
                AvailableCapacityGB: 10000,
                InterruptionProbability: 0.05)
        };

        var provider = new TestBillingProvider(CloudProvider.AWS, spotPricing: spotPricing);
        var optimizer = new StorageCostOptimizer(
            new[] { provider },
            new StorageCostOptimizerOptions { MinMonthlySavings = 10m });

        var plan = await optimizer.GenerateOptimizationPlanAsync();

        Assert.NotEmpty(plan.SpotRecommendations);
        var rec = plan.SpotRecommendations[0];
        Assert.True(rec.MonthlySavings > 0);
        Assert.True(rec.ConfidenceScore > 0.8); // Low interruption risk = high confidence
    }

    [Fact]
    public async Task SpotRecommendations_HighRisk_Excluded()
    {
        var spotPricing = new List<SpotPricing>
        {
            new SpotPricing(
                Provider: CloudProvider.AWS,
                Region: "us-east-1",
                StorageClass: "Standard",
                CurrentPricePerGBMonth: 0.023m,
                SpotPricePerGBMonth: 0.005m,
                SavingsPercent: 78.0,
                AvailableCapacityGB: 5000,
                InterruptionProbability: 0.50) // 50% interruption risk - too high
        };

        var provider = new TestBillingProvider(CloudProvider.AWS, spotPricing: spotPricing);
        var optimizer = new StorageCostOptimizer(
            new[] { provider },
            new StorageCostOptimizerOptions { MaxSpotInterruptionRisk = 0.10 });

        var plan = await optimizer.GenerateOptimizationPlanAsync();

        Assert.Empty(plan.SpotRecommendations);
    }

    #endregion

    #region Tier Recommendations

    [Fact]
    public async Task TierRecommendations_LowAccess_SuggestsCold()
    {
        var report = new BillingReport(
            ProviderId: "aws-1",
            Provider: CloudProvider.AWS,
            PeriodStart: DateTimeOffset.UtcNow.AddDays(-30),
            PeriodEnd: DateTimeOffset.UtcNow,
            TotalCost: 500m,
            Currency: "USD",
            Breakdown: new[]
            {
                new CostBreakdown(CostCategory.Storage, "S3-Standard", 400m, "GB-Month", 10000, "us-east-1"),
                new CostBreakdown(CostCategory.Operations, "S3-Ops", 5m, "Operations", 10, "us-east-1") // Very low ops = cold
            });

        var provider = new TestBillingProvider(CloudProvider.AWS, report: report);
        var optimizer = new StorageCostOptimizer(
            new[] { provider },
            new StorageCostOptimizerOptions { ColdTierAccessThresholdPerDay = 1.0, MinMonthlySavings = 10m });

        var plan = await optimizer.GenerateOptimizationPlanAsync();

        Assert.NotEmpty(plan.TierRecommendations);
        var rec = plan.TierRecommendations[0];
        Assert.Equal("Cold/Archive", rec.RecommendedTier);
        Assert.True(rec.MonthlySavings > 0);
    }

    #endregion

    #region Arbitrage Recommendations

    [Fact]
    public async Task ArbitrageRecommendations_SignificantDiff_Included()
    {
        var awsReport = new BillingReport(
            ProviderId: "aws-1",
            Provider: CloudProvider.AWS,
            PeriodStart: DateTimeOffset.UtcNow.AddDays(-30),
            PeriodEnd: DateTimeOffset.UtcNow,
            TotalCost: 2000m,
            Currency: "USD",
            Breakdown: new[]
            {
                new CostBreakdown(CostCategory.Storage, "S3", 1800m, "GB-Month", 5000, "us-east-1"),
                new CostBreakdown(CostCategory.Operations, "S3-Ops", 200m, "Operations", 50000, "us-east-1")
            });

        var gcpReport = new BillingReport(
            ProviderId: "gcp-1",
            Provider: CloudProvider.GCP,
            PeriodStart: DateTimeOffset.UtcNow.AddDays(-30),
            PeriodEnd: DateTimeOffset.UtcNow,
            TotalCost: 500m, // 75% cheaper
            Currency: "USD",
            Breakdown: new[]
            {
                new CostBreakdown(CostCategory.Storage, "GCS", 400m, "GB-Month", 5000, "us-central1"),
                new CostBreakdown(CostCategory.Operations, "GCS-Ops", 100m, "Operations", 50000, "us-central1")
            });

        var awsProvider = new TestBillingProvider(CloudProvider.AWS, report: awsReport);
        var gcpProvider = new TestBillingProvider(CloudProvider.GCP, report: gcpReport);
        var optimizer = new StorageCostOptimizer(
            new IBillingProvider[] { awsProvider, gcpProvider },
            new StorageCostOptimizerOptions { MinMonthlySavings = 10m });

        var plan = await optimizer.GenerateOptimizationPlanAsync();

        Assert.NotEmpty(plan.ArbitrageRecommendations);
        var rec = plan.ArbitrageRecommendations[0];
        Assert.True(rec.MonthlySavings > 0);
        Assert.True(rec.BreakEvenDays > 0);
    }

    #endregion

    #region Summary Calculations

    [Fact]
    public async Task SavingsSummary_CalculatedCorrectly()
    {
        var spotPricing = new List<SpotPricing>
        {
            new SpotPricing(
                Provider: CloudProvider.AWS,
                Region: "us-east-1",
                StorageClass: "Standard",
                CurrentPricePerGBMonth: 0.023m,
                SpotPricePerGBMonth: 0.010m,
                SavingsPercent: 56.5,
                AvailableCapacityGB: 10000,
                InterruptionProbability: 0.05)
        };

        var report = new BillingReport(
            ProviderId: "aws-1",
            Provider: CloudProvider.AWS,
            PeriodStart: DateTimeOffset.UtcNow.AddDays(-30),
            PeriodEnd: DateTimeOffset.UtcNow,
            TotalCost: 1000m,
            Currency: "USD",
            Breakdown: new[]
            {
                new CostBreakdown(CostCategory.Storage, "S3", 800m, "GB-Month", 10000, "us-east-1"),
                new CostBreakdown(CostCategory.Operations, "S3-Ops", 5m, "Operations", 10, "us-east-1")
            });

        var provider = new TestBillingProvider(CloudProvider.AWS, report: report, spotPricing: spotPricing);
        var optimizer = new StorageCostOptimizer(
            new[] { provider },
            new StorageCostOptimizerOptions { MinMonthlySavings = 10m });

        var plan = await optimizer.GenerateOptimizationPlanAsync();

        // Summary savings should be total of all category savings
        decimal totalCategorySavings =
            plan.SpotRecommendations.Sum(r => r.MonthlySavings) +
            plan.ReservedRecommendations.Sum(r => r.MonthlySavings) +
            plan.TierRecommendations.Sum(r => r.MonthlySavings) +
            plan.ArbitrageRecommendations.Sum(r => r.MonthlySavings);

        Assert.Equal(totalCategorySavings, plan.Summary.EstimatedMonthlySavings);
    }

    [Fact]
    public async Task BreakEven_ComputedFromImplementationCost()
    {
        var report = new BillingReport(
            ProviderId: "aws-1",
            Provider: CloudProvider.AWS,
            PeriodStart: DateTimeOffset.UtcNow.AddDays(-30),
            PeriodEnd: DateTimeOffset.UtcNow,
            TotalCost: 500m,
            Currency: "USD",
            Breakdown: new[]
            {
                new CostBreakdown(CostCategory.Storage, "S3", 400m, "GB-Month", 10000, "us-east-1"),
                new CostBreakdown(CostCategory.Operations, "S3-Ops", 5m, "Operations", 10, "us-east-1")
            });

        var provider = new TestBillingProvider(CloudProvider.AWS, report: report);
        var optimizer = new StorageCostOptimizer(
            new[] { provider },
            new StorageCostOptimizerOptions { MinMonthlySavings = 10m });

        var plan = await optimizer.GenerateOptimizationPlanAsync();

        // Break-even should be 0 if no implementation cost, or calculated if cost exists
        if (plan.Summary.ImplementationCost == 0m)
        {
            Assert.Equal(0, plan.Summary.BreakEvenDays);
        }
        else if (plan.Summary.EstimatedMonthlySavings > 0m)
        {
            decimal dailySavings = plan.Summary.EstimatedMonthlySavings / 30m;
            int expectedDays = (int)Math.Ceiling(plan.Summary.ImplementationCost / dailySavings);
            Assert.Equal(expectedDays, plan.Summary.BreakEvenDays);
        }
    }

    #endregion
}
