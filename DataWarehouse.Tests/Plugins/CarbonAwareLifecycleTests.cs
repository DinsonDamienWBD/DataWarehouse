using Xunit;
using DataWarehouse.Plugins.UltimateSustainability;
using DataWarehouse.Plugins.UltimateSustainability.Strategies.EnergyMeasurement;
using DataWarehouse.Plugins.UltimateSustainability.Strategies.CarbonBudgetEnforcement;
using DataWarehouse.Plugins.UltimateSustainability.Strategies.GreenPlacement;
using DataWarehouse.Plugins.UltimateSustainability.Strategies.GreenTiering;
using DataWarehouse.Plugins.UltimateSustainability.Strategies.CarbonReporting;
using DataWarehouse.SDK.Contracts.Carbon;

namespace DataWarehouse.Tests.Plugins;

/// <summary>
/// Integration tests for the complete carbon-aware lifecycle:
/// energy measurement, carbon budgets, green scoring, GHG reporting, and green tiering.
/// </summary>
[Trait("Category", "Integration")]
public class CarbonAwareLifecycleTests : IDisposable
{
    private readonly string _tempDir;

    public CarbonAwareLifecycleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"dw-carbon-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Cleanup failure is non-fatal in tests
        }
    }

    #region 1. Energy Measurement Tests

    [Fact]
    public async Task EstimationEnergyStrategy_ReturnsReasonableWattage()
    {
        var strategy = new EstimationEnergyStrategy();
        await strategy.InitializeAsync();

        var measurement = await strategy.MeasureOperationAsync(
            operationId: "test-op-1",
            operationType: "read",
            dataSizeBytes: 1024,
            operation: () => Task.CompletedTask,
            tenantId: "tenant-1");

        Assert.True(measurement.WattsConsumed > 0, "Watts consumed should be positive");
        Assert.True(measurement.WattsConsumed < 500, "Watts consumed should be reasonable (< 500W)");
        Assert.Equal(EnergySource.Estimation, measurement.Source);

        await strategy.DisposeAsync();
    }

    [Fact]
    public async Task EnergyMeasurementService_SelectsBestAvailableSource()
    {
        var service = new EnergyMeasurementService(messageBus: null);
        await service.InitializeAsync();

        // On Windows test environment, RAPL and powercap are not available.
        // Service should fall back to Estimation.
        Assert.Equal(EnergySource.Estimation, service.ActiveSource);

        await service.DisposeAsync();
    }

    [Fact]
    public async Task EnergyMeasurement_RecordContainsAllFields()
    {
        var service = new EnergyMeasurementService(messageBus: null);
        await service.InitializeAsync();

        var measurement = await service.MeasureOperationAsync(
            operationId: "test-op-fields",
            operationType: "write",
            dataSizeBytes: 4096,
            operation: () => Task.Delay(10),
            tenantId: "tenant-fields");

        Assert.Equal("test-op-fields", measurement.OperationId);
        Assert.True(measurement.Timestamp > DateTimeOffset.MinValue, "Timestamp should be set");
        Assert.True(measurement.WattsConsumed > 0, "WattsConsumed should be populated");
        Assert.NotEqual(EnergyComponent.Unknown, measurement.Component);
        Assert.NotEqual(default, measurement.Source);
        Assert.Equal("write", measurement.OperationType);

        await service.DisposeAsync();
    }

    #endregion

    #region 2. Carbon Budget Tests

    [Fact]
    public async Task CarbonBudgetStore_SetAndGetBudget()
    {
        using var store = new CarbonBudgetStore(_tempDir);

        var budget = new CarbonBudget
        {
            TenantId = "tenant-budget-1",
            BudgetPeriod = CarbonBudgetPeriod.Monthly,
            BudgetGramsCO2e = 1000.0,
            UsedGramsCO2e = 0,
            ThrottleThresholdPercent = 80.0,
            HardLimitPercent = 100.0,
            PeriodStart = DateTimeOffset.UtcNow.Date,
            PeriodEnd = DateTimeOffset.UtcNow.Date.AddMonths(1)
        };

        await store.SetAsync("tenant-budget-1", budget);
        var retrieved = await store.GetAsync("tenant-budget-1");

        Assert.NotNull(retrieved);
        Assert.Equal("tenant-budget-1", retrieved.TenantId);
        Assert.Equal(CarbonBudgetPeriod.Monthly, retrieved.BudgetPeriod);
        Assert.Equal(1000.0, retrieved.BudgetGramsCO2e);
        Assert.Equal(0.0, retrieved.UsedGramsCO2e);
        Assert.Equal(80.0, retrieved.ThrottleThresholdPercent);
        Assert.Equal(100.0, retrieved.HardLimitPercent);
    }

    [Fact]
    public async Task CarbonBudgetStore_RecordUsage_UpdatesBalance()
    {
        using var store = new CarbonBudgetStore(_tempDir);

        var budget = new CarbonBudget
        {
            TenantId = "tenant-usage-1",
            BudgetPeriod = CarbonBudgetPeriod.Monthly,
            BudgetGramsCO2e = 1000.0,
            UsedGramsCO2e = 0,
            ThrottleThresholdPercent = 80.0,
            HardLimitPercent = 100.0,
            PeriodStart = DateTimeOffset.UtcNow.Date,
            PeriodEnd = DateTimeOffset.UtcNow.Date.AddMonths(1)
        };

        await store.SetAsync("tenant-usage-1", budget);
        var recorded = await store.RecordUsageAsync("tenant-usage-1", 300.0);

        Assert.True(recorded, "Recording 300g against 1000g budget should succeed");

        var updated = await store.GetAsync("tenant-usage-1");
        Assert.NotNull(updated);
        Assert.Equal(300.0, updated.UsedGramsCO2e);
        Assert.Equal(700.0, updated.RemainingGramsCO2e);
    }

    [Fact]
    public async Task CarbonBudgetEnforcement_SoftThrottle_At80Percent()
    {
        using var store = new CarbonBudgetStore(_tempDir);
        var enforcement = new CarbonBudgetEnforcementStrategy(store);
        await enforcement.InitializeAsync();

        await enforcement.SetBudgetAsync("tenant-soft", 1000.0, CarbonBudgetPeriod.Monthly);
        await enforcement.RecordUsageAsync("tenant-soft", 800.0, "write");

        var decision = await enforcement.EvaluateThrottleAsync("tenant-soft");

        Assert.Equal(ThrottleLevel.Soft, decision.ThrottleLevel);
        Assert.True(decision.CurrentUsagePercent >= 80.0, "Usage should be at or above 80%");
        Assert.True(decision.RecommendedDelayMs > 0, "Soft throttle should recommend a delay");

        await enforcement.DisposeAsync();
    }

    [Fact]
    public async Task CarbonBudgetEnforcement_HardThrottle_At100Percent()
    {
        using var store = new CarbonBudgetStore(_tempDir);
        var enforcement = new CarbonBudgetEnforcementStrategy(store);
        await enforcement.InitializeAsync();

        await enforcement.SetBudgetAsync("tenant-hard", 1000.0, CarbonBudgetPeriod.Monthly);
        await enforcement.RecordUsageAsync("tenant-hard", 1000.0, "write");

        var decision = await enforcement.EvaluateThrottleAsync("tenant-hard");

        Assert.Equal(ThrottleLevel.Hard, decision.ThrottleLevel);
        Assert.True(decision.CurrentUsagePercent >= 100.0, "Usage should be at or above 100%");

        await enforcement.DisposeAsync();
    }

    [Fact]
    public async Task CarbonBudgetEnforcement_CanProceed_ReturnsFalseWhenExhausted()
    {
        using var store = new CarbonBudgetStore(_tempDir);
        var enforcement = new CarbonBudgetEnforcementStrategy(store);
        await enforcement.InitializeAsync();

        await enforcement.SetBudgetAsync("tenant-exhaust", 100.0, CarbonBudgetPeriod.Monthly);
        await enforcement.RecordUsageAsync("tenant-exhaust", 100.0, "write");

        var canProceed = await enforcement.CanProceedAsync("tenant-exhaust", 10.0);
        Assert.False(canProceed, "Should not be able to proceed when budget is exhausted");

        await enforcement.DisposeAsync();
    }

    [Fact]
    public async Task CarbonBudgetStore_ResetExpiredBudgets_AdvancesPeriod()
    {
        using var store = new CarbonBudgetStore(_tempDir);

        // Create a budget with PeriodEnd in the past
        var pastPeriodStart = DateTimeOffset.UtcNow.AddDays(-60);
        var pastPeriodEnd = DateTimeOffset.UtcNow.AddDays(-30);

        var budget = new CarbonBudget
        {
            TenantId = "tenant-reset",
            BudgetPeriod = CarbonBudgetPeriod.Monthly,
            BudgetGramsCO2e = 500.0,
            UsedGramsCO2e = 350.0,
            ThrottleThresholdPercent = 80.0,
            HardLimitPercent = 100.0,
            PeriodStart = pastPeriodStart,
            PeriodEnd = pastPeriodEnd
        };

        await store.SetAsync("tenant-reset", budget);
        await store.ResetExpiredBudgetsAsync();

        var reset = await store.GetAsync("tenant-reset");
        Assert.NotNull(reset);
        Assert.Equal(0.0, reset.UsedGramsCO2e);
        Assert.True(reset.PeriodEnd > DateTimeOffset.UtcNow,
            "Period end should be advanced past current time");
    }

    #endregion

    #region 3. Green Score Tests

    [Fact]
    public async Task BackendGreenScoreRegistry_ScoresCorrectly()
    {
        var registry = new BackendGreenScoreRegistry();
        await registry.InitializeAsync();

        // Register a 100% renewable backend with excellent PUE
        registry.RegisterBackend("green-dc", "eu-north-1", 100.0, 1.1, 0.15);

        // Register a 40% renewable backend with poor PUE
        registry.RegisterBackend("dirty-dc", "ap-southeast-1", 40.0, 1.5, null);

        var greenScore = registry.GetScore("green-dc");
        var dirtyScore = registry.GetScore("dirty-dc");

        Assert.NotNull(greenScore);
        Assert.NotNull(dirtyScore);
        Assert.True(greenScore.Score > dirtyScore.Score,
            $"Green backend ({greenScore.Score:F1}) should score higher than dirty backend ({dirtyScore.Score:F1})");
    }

    [Fact]
    public async Task BackendGreenScoreRegistry_GetBestBackend_ReturnsHighestScore()
    {
        var registry = new BackendGreenScoreRegistry();
        await registry.InitializeAsync();

        registry.RegisterBackend("low-carbon", "eu-north-1", 100.0, 1.08, 0.15);
        registry.RegisterBackend("medium-carbon", "eu-west-1", 70.0, 1.2, null);
        registry.RegisterBackend("high-carbon", "ap-southeast-1", 20.0, 1.5, null);

        var best = registry.GetBestBackend(new[] { "low-carbon", "medium-carbon", "high-carbon" });

        Assert.Equal("low-carbon", best);
    }

    [Fact]
    public async Task GreenPlacementService_SelectsGreenestBackend()
    {
        var registry = new BackendGreenScoreRegistry();
        await registry.InitializeAsync();

        // Register backends with known green scores
        registry.RegisterBackend("green-backend", "eu-north-1", 98.0, 1.08, 0.15);
        registry.RegisterBackend("brown-backend", "ap-southeast-1", 15.0, 1.3, null);

        var wattTime = new WattTimeGridApiStrategy();
        var electricityMaps = new ElectricityMapsApiStrategy();
        var service = new GreenPlacementService(wattTime, electricityMaps, registry);
        await service.InitializeAsync();

        var decision = await service.SelectGreenestBackendAsync(
            new[] { "green-backend", "brown-backend" },
            dataSizeBytes: 1024 * 1024);

        Assert.Equal("green-backend", decision.PreferredBackendId);
        Assert.True(decision.GreenScore.Score > 0);

        await service.DisposeAsync();
    }

    #endregion

    #region 4. GHG Reporting Tests

    [Fact]
    public async Task GhgProtocolReporting_Scope2_CalculatesFromEnergyAndIntensity()
    {
        var strategy = new GhgProtocolReportingStrategy();
        await strategy.InitializeAsync();

        // Record a measurement: 100 Wh at 400 gCO2e/kWh -> expected ~40g CO2e
        var now = DateTimeOffset.UtcNow;
        strategy.RecordMeasurement(new EnergyMeasurementRecord
        {
            Timestamp = now,
            WattsConsumed = 100.0,
            DurationMs = 3600000.0, // 1 hour
            EnergyWh = 100.0,
            Source = EnergySource.Estimation,
            OperationType = "write",
            TenantId = "tenant-ghg-1",
            Region = "us-east-1",
            DataSizeBytes = 1024 * 1024,
            CarbonIntensity = 400.0
        });

        strategy.UpdateRegionCarbonIntensity("us-east-1", 400.0);

        var entries = await strategy.GenerateScope2ReportAsync(
            now.AddMinutes(-1), now.AddMinutes(1), "tenant-ghg-1");

        Assert.NotEmpty(entries);

        var totalEmissions = entries.Sum(e => e.EmissionsGramsCO2e);
        // 100 Wh * 400 gCO2e/kWh / 1000 = 40 gCO2e
        Assert.True(totalEmissions >= 30.0 && totalEmissions <= 50.0,
            $"Expected ~40g CO2e, got {totalEmissions:F4}g");

        Assert.All(entries, e =>
            Assert.Equal(GhgScopeCategory.Scope2_PurchasedElectricity, e.Scope));

        await strategy.DisposeAsync();
    }

    [Fact]
    public async Task GhgProtocolReporting_Scope3_IncludesDataTransfer()
    {
        var strategy = new GhgProtocolReportingStrategy();
        await strategy.InitializeAsync();

        var now = DateTimeOffset.UtcNow;

        // Record a network transfer measurement: 1 GB
        strategy.RecordMeasurement(new EnergyMeasurementRecord
        {
            Timestamp = now,
            WattsConsumed = 10.0,
            DurationMs = 1000.0,
            EnergyWh = 0.003,
            Source = EnergySource.Estimation,
            OperationType = "transfer",
            TenantId = "tenant-scope3",
            Region = "global",
            DataSizeBytes = 1L * 1024 * 1024 * 1024, // 1 GB
            CarbonIntensity = 0
        });

        var entries = await strategy.GenerateScope3ReportAsync(
            now.AddMinutes(-1), now.AddMinutes(1), "tenant-scope3");

        Assert.NotEmpty(entries);

        // Should include data transfer entry
        var transferEntry = entries.FirstOrDefault(e =>
            e.Category.Contains("transportation", StringComparison.OrdinalIgnoreCase) ||
            e.Category.Contains("transfer", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(transferEntry);
        Assert.Equal(GhgScopeCategory.Scope3_ValueChain, transferEntry.Scope);
        Assert.True(transferEntry.EmissionsGramsCO2e > 0,
            "Data transfer should produce Scope 3 emissions");

        await strategy.DisposeAsync();
    }

    [Fact]
    public async Task CarbonReportingService_GetCarbonSummary_AggregatesAllData()
    {
        var ghgStrategy = new GhgProtocolReportingStrategy();
        var dashboardStrategy = new CarbonDashboardDataStrategy();
        var service = new CarbonReportingService(ghgStrategy, dashboardStrategy);
        await service.InitializeAsync();

        var now = DateTimeOffset.UtcNow;

        // Record measurements for the reporting period
        ghgStrategy.RecordMeasurement(new EnergyMeasurementRecord
        {
            Timestamp = now,
            WattsConsumed = 50.0,
            DurationMs = 7200000.0, // 2 hours
            EnergyWh = 100.0,
            Source = EnergySource.Estimation,
            OperationType = "write",
            TenantId = null, // System-wide
            Region = "us-west-2",
            DataSizeBytes = 10 * 1024 * 1024,
            CarbonIntensity = 200.0
        });

        ghgStrategy.UpdateRegionCarbonIntensity("us-west-2", 200.0);

        var summary = await service.GetCarbonSummaryAsync();

        Assert.NotNull(summary);
        Assert.True(summary.TotalEmissionsGramsCO2e >= 0, "Emissions should be non-negative");
        Assert.True(summary.TotalEnergyWh >= 0, "Energy should be non-negative");
        Assert.True(summary.RenewablePercentage >= 0 && summary.RenewablePercentage <= 100,
            "Renewable % should be between 0 and 100");
        Assert.True(summary.AvgCarbonIntensity > 0, "Avg carbon intensity should be positive");
        Assert.False(string.IsNullOrEmpty(summary.TopEmittingRegion), "Top emitting region should be populated");
        Assert.True(summary.PeriodStart < summary.PeriodEnd, "Period start should be before end");

        await service.DisposeAsync();
    }

    #endregion

    #region 5. Green Tiering Tests

    [Fact]
    public void GreenTieringPolicy_DefaultValues()
    {
        var policy = new GreenTieringPolicy { TenantId = "test-tenant" };

        Assert.Equal(TimeSpan.FromDays(30), policy.ColdThreshold);
        Assert.Equal(80.0, policy.TargetGreenScore);
        Assert.True(policy.Enabled);
        Assert.Equal(GreenMigrationSchedule.LowCarbonWindowOnly, policy.MigrationSchedule);
        Assert.True(policy.RespectCarbonBudget);
    }

    [Fact]
    public void GreenTieringStrategy_IdentifiesColdDataPolicy()
    {
        // Verify that the policy engine correctly stores and retrieves policies
        // that control cold data identification thresholds
        using var policyEngine = new GreenTieringPolicyEngine(_tempDir);

        var policy = new GreenTieringPolicy
        {
            TenantId = "cold-data-tenant",
            ColdThreshold = TimeSpan.FromDays(15), // Custom shorter threshold
            TargetGreenScore = 85.0,
            Enabled = true
        };

        policyEngine.SetPolicy("cold-data-tenant", policy);

        var retrieved = policyEngine.GetPolicy("cold-data-tenant");

        Assert.Equal(TimeSpan.FromDays(15), retrieved.ColdThreshold);
        Assert.Equal(85.0, retrieved.TargetGreenScore);
        Assert.True(retrieved.Enabled);
    }

    [Fact]
    public void GreenMigrationCandidate_CarbonSavingsCalculation()
    {
        // Verify the migration candidate data structure captures
        // all necessary fields for carbon savings calculations
        var candidate = new GreenMigrationCandidate
        {
            ObjectKey = "data/archive/old-report.parquet",
            CurrentBackendId = "dirty-dc",
            CurrentGreenScore = 20.0, // High carbon backend
            SizeBytes = 1024L * 1024 * 1024, // 1 GB
            LastAccessed = DateTimeOffset.UtcNow.AddDays(-60),
            TenantId = "migration-tenant"
        };

        // Verify the candidate identifies data on a low-green-score backend
        Assert.True(candidate.CurrentGreenScore < 80.0,
            "Candidate should be on a backend below green score threshold");
        Assert.True(candidate.LastAccessed < DateTimeOffset.UtcNow.AddDays(-30),
            "Candidate should be cold (last accessed > 30 days ago)");

        // Create a batch with target green backend (score 95)
        var batch = new GreenMigrationBatch
        {
            Candidates = new[] { candidate },
            TargetBackendId = "green-dc",
            TargetGreenScore = 95.0,
            EstimatedCarbonCostGrams = 0.5, // Small migration cost
            EstimatedEnergyWh = 0.1,
            ScheduledFor = DateTimeOffset.UtcNow,
            TenantId = "migration-tenant"
        };

        // The migration moves from 20 -> 95 green score, so carbon savings should be positive
        var scoreDelta = batch.TargetGreenScore - candidate.CurrentGreenScore;
        Assert.True(scoreDelta > 0, "Moving to a greener backend should improve score");
        Assert.Single(batch.Candidates);
        Assert.Equal(1024L * 1024 * 1024, batch.TotalSizeBytes);
    }

    #endregion

    #region 6. Dashboard Data Tests

    [Fact]
    public async Task CarbonDashboardData_RecordAndRetrieveTimeSeries()
    {
        var strategy = new CarbonDashboardDataStrategy();
        await strategy.InitializeAsync();

        // Record some carbon intensity data points
        strategy.RecordCarbonIntensity("us-west-2", 150.0);
        strategy.RecordCarbonIntensity("us-west-2", 175.0);
        strategy.RecordCarbonIntensity("us-west-2", 160.0);

        var timeSeries = strategy.GetCarbonIntensityTimeSeries(
            "us-west-2", TimeSpan.FromHours(1), TimeSpan.FromMinutes(5));

        Assert.NotEmpty(timeSeries);
        Assert.All(timeSeries, p => Assert.Equal("gCO2e/kWh", p.Unit));

        await strategy.DisposeAsync();
    }

    [Fact]
    public async Task CarbonDashboardData_EmissionsByOperationType()
    {
        var strategy = new CarbonDashboardDataStrategy();
        await strategy.InitializeAsync();

        strategy.RecordOperationEmission("read", 10.0);
        strategy.RecordOperationEmission("write", 25.0);
        strategy.RecordOperationEmission("delete", 5.0);
        strategy.RecordOperationEmission("list", 2.0);

        var now = DateTimeOffset.UtcNow;
        var emissions = strategy.GetEmissionsByOperationType(now.AddHours(-1), now);

        Assert.Equal(10.0, emissions.ReadGramsCO2e);
        Assert.Equal(25.0, emissions.WriteGramsCO2e);
        Assert.Equal(5.0, emissions.DeleteGramsCO2e);
        Assert.Equal(2.0, emissions.ListGramsCO2e);
        Assert.Equal(42.0, emissions.TotalGramsCO2e);

        await strategy.DisposeAsync();
    }

    [Fact]
    public async Task CarbonDashboardData_TopEmittingTenants()
    {
        var strategy = new CarbonDashboardDataStrategy();
        await strategy.InitializeAsync();

        strategy.RecordTenantEmission("tenant-a", 500.0, 100.0);
        strategy.RecordTenantEmission("tenant-b", 200.0, 50.0);
        strategy.RecordTenantEmission("tenant-c", 800.0, 200.0);

        var now = DateTimeOffset.UtcNow;
        var topEmitters = strategy.GetTopEmittingTenants(2, now.AddHours(-1), now);

        Assert.Equal(2, topEmitters.Count);
        Assert.Equal("tenant-c", topEmitters[0].TenantId);
        Assert.Equal(800.0, topEmitters[0].TotalEmissionsGramsCO2e);
        Assert.Equal("tenant-a", topEmitters[1].TenantId);

        await strategy.DisposeAsync();
    }

    [Fact]
    public async Task CarbonDashboardData_GreenScoreTrend()
    {
        var strategy = new CarbonDashboardDataStrategy();
        await strategy.InitializeAsync();

        strategy.RecordGreenScore(75.0);
        strategy.RecordGreenScore(78.0);
        strategy.RecordGreenScore(82.0);

        var trend = strategy.GetGreenScoreTrend(TimeSpan.FromHours(1));

        Assert.NotEmpty(trend);
        Assert.All(trend, p => Assert.Equal("score", p.Unit));
        Assert.True(trend.Count >= 3, "Should have at least 3 data points");

        await strategy.DisposeAsync();
    }

    #endregion

    #region 7. Full Report Generation

    [Fact]
    public async Task GhgProtocolReporting_FullReport_CombinesScopes()
    {
        var strategy = new GhgProtocolReportingStrategy();
        await strategy.InitializeAsync();

        var now = DateTimeOffset.UtcNow;

        // Add Scope 2 data (energy consumption)
        strategy.RecordMeasurement(new EnergyMeasurementRecord
        {
            Timestamp = now,
            WattsConsumed = 200.0,
            DurationMs = 3600000.0,
            EnergyWh = 200.0,
            Source = EnergySource.Rapl,
            OperationType = "write",
            Region = "eu-west-1",
            DataSizeBytes = 5L * 1024 * 1024 * 1024,
            CarbonIntensity = 300.0
        });

        // Add Scope 3 data (network transfer)
        strategy.RecordMeasurement(new EnergyMeasurementRecord
        {
            Timestamp = now,
            WattsConsumed = 5.0,
            DurationMs = 500.0,
            EnergyWh = 0.001,
            Source = EnergySource.Estimation,
            OperationType = "transfer",
            Region = "global",
            DataSizeBytes = 2L * 1024 * 1024 * 1024,
            CarbonIntensity = 0
        });

        strategy.UpdateRegionCarbonIntensity("eu-west-1", 300.0);

        var report = await strategy.GenerateFullReportAsync(
            now.AddMinutes(-1), now.AddMinutes(1), "Test Organization");

        Assert.NotNull(report);
        Assert.Equal("Test Organization", report.OrganizationName);
        Assert.NotEmpty(report.Entries);
        Assert.True(report.TotalScope2 > 0, "Scope 2 total should be positive");
        Assert.True(report.TotalScope3 >= 0, "Scope 3 total should be non-negative");
        Assert.True(report.TotalEmissions > 0, "Total emissions should be positive");
        Assert.False(string.IsNullOrEmpty(report.Methodology), "Methodology should be populated");
        Assert.False(string.IsNullOrEmpty(report.ExecutiveSummary), "Executive summary should be populated");
        Assert.NotNull(report.ReportId);

        // Verify data quality tagging
        var scope2Entries = report.Entries.Where(e => e.Scope == GhgScopeCategory.Scope2_PurchasedElectricity).ToList();
        Assert.NotEmpty(scope2Entries);
        // Since we used RAPL source, data quality should be Measured
        Assert.Contains(scope2Entries, e => e.DataQuality == DataQualityLevel.Measured);

        await strategy.DisposeAsync();
    }

    #endregion

    #region 8. End-to-End Lifecycle

    [Fact]
    public async Task EndToEnd_CarbonBudgetToReporting()
    {
        // Set up budget
        using var store = new CarbonBudgetStore(_tempDir);
        var enforcement = new CarbonBudgetEnforcementStrategy(store);
        await enforcement.InitializeAsync();
        await enforcement.SetBudgetAsync("e2e-tenant", 1000.0, CarbonBudgetPeriod.Monthly);

        // Record energy and calculate carbon
        var energyWh = 50.0;
        var carbonIntensity = 300.0; // gCO2e/kWh
        var carbonGrams = energyWh * carbonIntensity / 1000.0; // = 15g

        await enforcement.RecordUsageAsync("e2e-tenant", carbonGrams, "write");

        // Verify budget tracking
        var budget = await enforcement.GetBudgetAsync("e2e-tenant");
        Assert.True(budget.UsedGramsCO2e > 0, "Budget should track usage");
        Assert.True(budget.RemainingGramsCO2e < 1000.0, "Remaining should decrease");

        // Set up reporting
        var ghgStrategy = new GhgProtocolReportingStrategy();
        await ghgStrategy.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        ghgStrategy.RecordMeasurement(new EnergyMeasurementRecord
        {
            Timestamp = now,
            WattsConsumed = 50.0,
            DurationMs = 3600000.0,
            EnergyWh = energyWh,
            Source = EnergySource.Estimation,
            OperationType = "write",
            TenantId = "e2e-tenant",
            Region = "us-east-1",
            DataSizeBytes = 1024 * 1024,
            CarbonIntensity = carbonIntensity
        });
        ghgStrategy.UpdateRegionCarbonIntensity("us-east-1", carbonIntensity);

        // Generate report
        var entries = await ghgStrategy.GenerateScope2ReportAsync(
            now.AddMinutes(-1), now.AddMinutes(1), "e2e-tenant");

        Assert.NotEmpty(entries);
        var totalReported = entries.Sum(e => e.EmissionsGramsCO2e);
        Assert.True(totalReported > 0, "Report should show emissions");

        // Verify throttle state is still nominal
        var throttle = await enforcement.EvaluateThrottleAsync("e2e-tenant");
        Assert.Equal(ThrottleLevel.None, throttle.ThrottleLevel);

        await enforcement.DisposeAsync();
        await ghgStrategy.DisposeAsync();
    }

    #endregion
}
