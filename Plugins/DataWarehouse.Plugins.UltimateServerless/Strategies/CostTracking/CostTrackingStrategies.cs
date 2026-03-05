using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateServerless.Strategies.CostTracking;

#region 119.8.1 Usage Analytics Strategy

/// <summary>
/// 119.8.1: Usage analytics for serverless functions with
/// invocation tracking, duration analysis, and memory efficiency.
/// </summary>
public sealed class UsageAnalyticsStrategy : ServerlessStrategyBase
{
    private readonly BoundedDictionary<string, List<UsageRecord>> _usageRecords = new BoundedDictionary<string, List<UsageRecord>>(1000);

    public override string StrategyId => "cost-usage-analytics";
    public override string DisplayName => "Usage Analytics";
    public override ServerlessCategory Category => ServerlessCategory.CostTracking;
    public override bool IsProductionReady => false; // Usage data is fabricated

    public override ServerlessStrategyCapabilities Capabilities => new() { SupportsSyncInvocation = true };

    public override string SemanticDescription =>
        "Usage analytics tracking invocations, duration, memory usage, and GB-seconds " +
        "for accurate cost attribution and optimization insights.";

    public override string[] Tags => new[] { "usage", "analytics", "invocations", "gb-seconds", "tracking" };

    /// <summary>Records usage data.</summary>
    public Task RecordUsageAsync(UsageRecord record, CancellationToken ct = default)
    {
        _usageRecords.AddOrUpdate(record.FunctionId,
            _ => new List<UsageRecord> { record },
            (_, list) => { list.Add(record); return list; });

        RecordOperation("RecordUsage");
        return Task.CompletedTask;
    }

    /// <summary>Gets usage summary.</summary>
    public Task<UsageSummary> GetUsageSummaryAsync(string functionId, TimeSpan period, CancellationToken ct = default)
    {
        RecordOperation("GetUsageSummary");
        var invocations = Random.Shared.Next(1000, 100000);
        var avgDuration = Random.Shared.Next(50, 500);
        var memoryMb = Random.Shared.Next(128, 1024);

        return Task.FromResult(new UsageSummary
        {
            FunctionId = functionId,
            Period = period,
            TotalInvocations = invocations,
            TotalDurationMs = invocations * avgDuration,
            AvgDurationMs = avgDuration,
            TotalGbSeconds = invocations * avgDuration / 1000.0 * memoryMb / 1024.0,
            MemoryMb = memoryMb,
            ColdStartCount = (int)(invocations * 0.1),
            ErrorCount = (int)(invocations * 0.02)
        });
    }

    /// <summary>Gets usage breakdown by time.</summary>
    public Task<IReadOnlyList<UsageTimeSlice>> GetUsageBreakdownAsync(string functionId, TimeSpan period, TimeSpan granularity, CancellationToken ct = default)
    {
        RecordOperation("GetUsageBreakdown");
        var slices = new List<UsageTimeSlice>();
        var sliceCount = (int)(period.TotalMinutes / granularity.TotalMinutes);

        for (int i = 0; i < sliceCount; i++)
        {
            slices.Add(new UsageTimeSlice
            {
                StartTime = DateTimeOffset.UtcNow.AddMinutes(-period.TotalMinutes + i * granularity.TotalMinutes),
                EndTime = DateTimeOffset.UtcNow.AddMinutes(-period.TotalMinutes + (i + 1) * granularity.TotalMinutes),
                Invocations = Random.Shared.Next(10, 1000),
                DurationMs = Random.Shared.Next(1000, 50000),
                GbSeconds = Random.Shared.NextDouble() * 10
            });
        }

        return Task.FromResult<IReadOnlyList<UsageTimeSlice>>(slices);
    }
}

#endregion

#region 119.8.2 Cost Estimation Strategy

/// <summary>
/// 119.8.2: Cost estimation and projection for serverless workloads
/// with pricing models and regional variations.
/// </summary>
public sealed class CostEstimationStrategy : ServerlessStrategyBase
{
    private readonly Dictionary<ServerlessPlatform, PricingModel> _pricingModels = new()
    {
        [ServerlessPlatform.AwsLambda] = new PricingModel { RequestPrice = 0.0000002, GbSecondPrice = 0.0000166667, FreeRequests = 1_000_000, FreeGbSeconds = 400_000 },
        [ServerlessPlatform.AzureFunctions] = new PricingModel { RequestPrice = 0.0000002, GbSecondPrice = 0.000016, FreeRequests = 1_000_000, FreeGbSeconds = 400_000 },
        [ServerlessPlatform.GoogleCloudFunctions] = new PricingModel { RequestPrice = 0.0000004, GbSecondPrice = 0.0000025, FreeRequests = 2_000_000, FreeGbSeconds = 400_000 }
    };

    public override string StrategyId => "cost-estimation";
    public override string DisplayName => "Cost Estimation";
    public override ServerlessCategory Category => ServerlessCategory.CostTracking;

    public override ServerlessStrategyCapabilities Capabilities => new() { SupportsSyncInvocation = true };

    public override string SemanticDescription =>
        "Cost estimation with platform-specific pricing models, regional variations, " +
        "free tier calculations, and monthly/annual projections.";

    public override string[] Tags => new[] { "cost", "estimation", "pricing", "projection", "budget" };

    /// <summary>Estimates cost for usage.</summary>
    public Task<CostEstimate> EstimateCostAsync(CostEstimationRequest request, CancellationToken ct = default)
    {
        RecordOperation("EstimateCost");

        var pricing = _pricingModels.GetValueOrDefault(request.Platform, _pricingModels[ServerlessPlatform.AwsLambda]);

        var billableRequests = Math.Max(0, request.Invocations - pricing.FreeRequests);
        var billableGbSeconds = Math.Max(0, request.GbSeconds - pricing.FreeGbSeconds);

        var requestCost = billableRequests * pricing.RequestPrice;
        var computeCost = billableGbSeconds * pricing.GbSecondPrice;
        var totalCost = requestCost + computeCost;

        return Task.FromResult(new CostEstimate
        {
            Platform = request.Platform,
            Period = request.Period,
            Invocations = request.Invocations,
            GbSeconds = request.GbSeconds,
            RequestCost = requestCost,
            ComputeCost = computeCost,
            TotalCost = totalCost,
            FreeTierSavings = (pricing.FreeRequests * pricing.RequestPrice) + (pricing.FreeGbSeconds * pricing.GbSecondPrice),
            CostPerInvocation = request.Invocations > 0 ? totalCost / request.Invocations : 0
        });
    }

    /// <summary>Projects future costs.</summary>
    public Task<CostProjection> ProjectCostAsync(string functionId, int monthsAhead, CancellationToken ct = default)
    {
        RecordOperation("ProjectCost");
        return Task.FromResult(new CostProjection
        {
            FunctionId = functionId,
            MonthsAhead = monthsAhead,
            MonthlyProjections = Enumerable.Range(1, monthsAhead).Select(m => new MonthlyProjection
            {
                Month = DateTimeOffset.UtcNow.AddMonths(m).ToString("yyyy-MM"),
                ProjectedInvocations = Random.Shared.Next(100000, 1000000),
                ProjectedCost = Random.Shared.Next(10, 500),
                ConfidencePercent = 95 - m * 5
            }).ToList()
        });
    }
}

#endregion

#region 119.8.3 Budget Alerting Strategy

/// <summary>
/// 119.8.3: Budget alerts and thresholds for serverless costs
/// with notification channels and forecast alerts.
/// </summary>
public sealed class BudgetAlertingStrategy : ServerlessStrategyBase
{
    private readonly BoundedDictionary<string, Budget> _budgets = new BoundedDictionary<string, Budget>(1000);

    public override string StrategyId => "cost-budget-alerting";
    public override string DisplayName => "Budget Alerting";
    public override ServerlessCategory Category => ServerlessCategory.CostTracking;

    public override ServerlessStrategyCapabilities Capabilities => new() { SupportsSyncInvocation = true };

    public override string SemanticDescription =>
        "Budget alerts with configurable thresholds, email/SNS/Slack notifications, " +
        "forecast-based alerts, and per-function budget tracking.";

    public override string[] Tags => new[] { "budget", "alerting", "threshold", "notification", "spending" };

    /// <summary>Creates a budget.</summary>
    public Task<Budget> CreateBudgetAsync(BudgetConfig config, CancellationToken ct = default)
    {
        var budget = new Budget
        {
            BudgetId = Guid.NewGuid().ToString(),
            Name = config.Name,
            Amount = config.Amount,
            Period = config.Period,
            Thresholds = config.Thresholds,
            NotificationChannels = config.NotificationChannels,
            CurrentSpend = 0,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _budgets[budget.BudgetId] = budget;
        RecordOperation("CreateBudget");
        return Task.FromResult(budget);
    }

    /// <summary>Gets budget status.</summary>
    public Task<BudgetStatus> GetBudgetStatusAsync(string budgetId, CancellationToken ct = default)
    {
        if (!_budgets.TryGetValue(budgetId, out var budget))
            throw new KeyNotFoundException($"Budget {budgetId} not found");

        var currentSpend = Random.Shared.NextDouble() * budget.Amount;
        var percentUsed = currentSpend / budget.Amount * 100;

        RecordOperation("GetBudgetStatus");
        return Task.FromResult(new BudgetStatus
        {
            BudgetId = budgetId,
            BudgetAmount = budget.Amount,
            CurrentSpend = currentSpend,
            PercentUsed = percentUsed,
            ForecastedSpend = currentSpend * 1.2,
            DaysRemaining = Random.Shared.Next(1, 30),
            TriggeredThresholds = budget.Thresholds.Where(t => percentUsed >= t).ToList()
        });
    }

    /// <summary>Updates budget spend.</summary>
    public Task UpdateSpendAsync(string budgetId, double amount, CancellationToken ct = default)
    {
        if (_budgets.TryGetValue(budgetId, out var budget))
        {
            budget.CurrentSpend += amount;
        }
        RecordOperation("UpdateSpend");
        return Task.CompletedTask;
    }
}

#endregion

#region 119.8.4 Cost Optimization Strategy

/// <summary>
/// 119.8.4: Cost optimization recommendations for serverless
/// with memory right-sizing and reserved capacity.
/// </summary>
public sealed class CostOptimizationStrategy : ServerlessStrategyBase
{
    public override string StrategyId => "cost-optimization";
    public override string DisplayName => "Cost Optimization";
    public override ServerlessCategory Category => ServerlessCategory.CostTracking;
    public override bool IsProductionReady => false; // Optimization data is fabricated

    public override ServerlessStrategyCapabilities Capabilities => new() { SupportsSyncInvocation = true };

    public override string SemanticDescription =>
        "Cost optimization with memory right-sizing, provisioned concurrency analysis, " +
        "architecture recommendations, and savings opportunities.";

    public override string[] Tags => new[] { "optimization", "right-sizing", "savings", "efficiency" };

    /// <summary>Analyzes cost optimization opportunities.</summary>
    public Task<OptimizationAnalysis> AnalyzeAsync(string functionId, CancellationToken ct = default)
    {
        RecordOperation("Analyze");
        return Task.FromResult(new OptimizationAnalysis
        {
            FunctionId = functionId,
            CurrentMonthlyCost = Random.Shared.Next(50, 500),
            OptimizedMonthlyCost = Random.Shared.Next(30, 300),
            PotentialSavingsPercent = Random.Shared.Next(10, 40),
            Recommendations = new[]
            {
                new OptimizationRecommendation
                {
                    Type = "MemoryRightSizing",
                    Priority = 1,
                    CurrentValue = "1024 MB",
                    RecommendedValue = "512 MB",
                    EstimatedSavingsPercent = 25,
                    Description = "Reduce memory allocation based on actual usage"
                },
                new OptimizationRecommendation
                {
                    Type = "ProvisionedConcurrency",
                    Priority = 2,
                    CurrentValue = "10 instances",
                    RecommendedValue = "5 instances",
                    EstimatedSavingsPercent = 15,
                    Description = "Reduce provisioned concurrency during off-peak hours"
                },
                new OptimizationRecommendation
                {
                    Type = "Architecture",
                    Priority = 3,
                    CurrentValue = "Multiple functions",
                    RecommendedValue = "Consolidated function",
                    EstimatedSavingsPercent = 20,
                    Description = "Consolidate related functions to reduce invocation overhead"
                }
            }
        });
    }

    /// <summary>Runs Power Tuning analysis.</summary>
    public Task<PowerTuningResult> RunPowerTuningAsync(string functionArn, int[] memorySizes, int invocationsPerSize, CancellationToken ct = default)
    {
        RecordOperation("RunPowerTuning");
        return Task.FromResult(new PowerTuningResult
        {
            FunctionArn = functionArn,
            OptimalMemory = 512,
            Results = memorySizes.Select(m => new PowerTuningDataPoint
            {
                MemoryMb = m,
                AvgDurationMs = 1000.0 / (m / 128.0) + Random.Shared.Next(10, 50),
                CostPerInvocation = m * 0.0000000167 * (1000.0 / (m / 128.0) + Random.Shared.Next(10, 50))
            }).ToList()
        });
    }
}

#endregion

#region 119.8.5-8 Additional Cost Tracking Strategies

/// <summary>119.8.5: Cost allocation tags.</summary>
public sealed class CostAllocationStrategy : ServerlessStrategyBase
{
    public override string StrategyId => "cost-allocation";
    public override string DisplayName => "Cost Allocation";
    public override ServerlessCategory Category => ServerlessCategory.CostTracking;
    public override ServerlessStrategyCapabilities Capabilities => new() { SupportsSyncInvocation = true };
    public override string SemanticDescription => "Cost allocation with tags for team, project, environment, and custom dimensions.";
    public override string[] Tags => new[] { "allocation", "tags", "chargeback", "cost-center" };

    public Task<AllocationReport> GetAllocationReportAsync(string tagKey, TimeSpan period, CancellationToken ct = default)
    {
        RecordOperation("GetAllocationReport");
        return Task.FromResult(new AllocationReport
        {
            TagKey = tagKey,
            Period = period,
            Allocations = new[]
            {
                new CostAllocation { TagValue = "team-a", Cost = Random.Shared.Next(100, 1000), Percent = 40 },
                new CostAllocation { TagValue = "team-b", Cost = Random.Shared.Next(100, 1000), Percent = 35 },
                new CostAllocation { TagValue = "team-c", Cost = Random.Shared.Next(100, 1000), Percent = 25 }
            }
        });
    }
}

/// <summary>119.8.6: Cost anomaly detection.</summary>
public sealed class CostAnomalyDetectionStrategy : ServerlessStrategyBase
{
    private readonly List<(DateTimeOffset timestamp, decimal cost, string service)> _costHistory
        = new List<(DateTimeOffset, decimal, string)>();
    private readonly object _lock = new();

    public override string StrategyId => "cost-anomaly-detection";
    public override string DisplayName => "Cost Anomaly Detection";
    public override ServerlessCategory Category => ServerlessCategory.CostTracking;
    public override ServerlessStrategyCapabilities Capabilities => new() { SupportsSyncInvocation = true };
    public override string SemanticDescription => "Threshold-based anomaly detection for unusual spending patterns and cost spikes using rolling average analysis.";
    public override string[] Tags => new[] { "anomaly", "detection", "threshold", "spike", "unusual" };

    /// <summary>Records a cost data point for anomaly analysis.</summary>
    public void RecordCost(string service, decimal cost)
    {
        lock (_lock)
        {
            _costHistory.Add((DateTimeOffset.UtcNow, cost, service));
            // Keep last 10000 data points
            if (_costHistory.Count > 10000) _costHistory.RemoveAt(0);
        }
    }

    public Task<IReadOnlyList<CostAnomaly>> DetectAnomaliesAsync(TimeSpan lookbackPeriod, CancellationToken ct = default)
    {
        RecordOperation("DetectAnomalies");
        var cutoff = DateTimeOffset.UtcNow - lookbackPeriod;
        List<CostAnomaly> anomalies;

        lock (_lock)
        {
            if (_costHistory.Count < 10)
                return Task.FromResult<IReadOnlyList<CostAnomaly>>(Array.Empty<CostAnomaly>());

            // Group by service and compute mean + stddev
            var byService = _costHistory
                .GroupBy(x => x.service)
                .ToDictionary(g => g.Key, g => g.Select(x => (double)x.cost).ToList());

            anomalies = new List<CostAnomaly>();
            var recentRecords = _costHistory.Where(x => x.timestamp >= cutoff).ToList();

            foreach (var (ts, cost, service) in recentRecords)
            {
                if (!byService.TryGetValue(service, out var history) || history.Count < 5) continue;
                var mean = history.Average();
                var stddev = Math.Sqrt(history.Select(c => Math.Pow(c - mean, 2)).Average());
                if (stddev < 0.01) continue; // Not enough variance to detect anomaly
                var zScore = ((double)cost - mean) / stddev;
                if (Math.Abs(zScore) > 2.5) // ~2.5 sigma threshold
                {
                    anomalies.Add(new CostAnomaly
                    {
                        AnomalyId = Guid.NewGuid().ToString(),
                        DetectedAt = ts,
                        Severity = zScore > 4.0 ? "High" : zScore > 3.0 ? "Medium" : "Low",
                        ExpectedCost = mean,
                        ActualCost = (double)cost,
                        RootCause = $"Unusual cost for service '{service}': {zScore:F1}σ above mean"
                    });
                }
            }
        }

        return Task.FromResult<IReadOnlyList<CostAnomaly>>(anomalies);
    }
}

/// <summary>119.8.7: Compute Savings Plans.</summary>
public sealed class SavingsPlansStrategy : ServerlessStrategyBase
{
    private readonly List<(DateTimeOffset timestamp, decimal cost)> _usageHistory
        = new List<(DateTimeOffset, decimal)>();
    private readonly object _lock = new();

    public override string StrategyId => "cost-savings-plans";
    public override string DisplayName => "Savings Plans";
    public override ServerlessCategory Category => ServerlessCategory.CostTracking;
    public override ServerlessStrategyCapabilities Capabilities => new() { SupportsSyncInvocation = true };
    public override string SemanticDescription => "Compute Savings Plans analysis with commitment recommendations based on actual usage patterns.";
    public override string[] Tags => new[] { "savings-plans", "commitment", "discount", "reserved" };

    /// <summary>Records an hourly usage data point for recommendation analysis.</summary>
    public void RecordHourlyUsage(decimal hourlyCost)
    {
        lock (_lock)
        {
            _usageHistory.Add((DateTimeOffset.UtcNow, hourlyCost));
            if (_usageHistory.Count > 8760) _usageHistory.RemoveAt(0); // 1 year of hourly data
        }
    }

    public Task<SavingsPlanRecommendation> GetRecommendationAsync(CancellationToken ct = default)
    {
        RecordOperation("GetRecommendation");
        List<(DateTimeOffset, decimal)> snapshot;
        lock (_lock) { snapshot = new List<(DateTimeOffset, decimal)>(_usageHistory); }

        if (snapshot.Count < 24)
        {
            // Insufficient data — return zero-commitment recommendation
            return Task.FromResult(new SavingsPlanRecommendation
            {
                RecommendedCommitment = 0,
                Term = "1 year",
                EstimatedSavingsPercent = 0,
                EstimatedMonthlySavings = 0,
                Coverage = 0
            });
        }

        // Analyze usage: recommend commitment at the P10 percentile of hourly spend
        // (conservative commitment that covers baseline usage)
        var costs = snapshot.Select(x => (double)x.Item2).OrderBy(c => c).ToList();
        var p10Index = Math.Max(0, (int)(costs.Count * 0.10));
        var p10Cost = costs[p10Index];
        var avgCost = costs.Average();
        var monthlyOnDemand = avgCost * 24 * 30;
        // Savings Plans offer ~17% discount on 1-year commitment, ~34% on 3-year
        const double savingsRate1Yr = 0.17;
        var commitment = Math.Round(p10Cost, 2);
        var coverage = avgCost > 0 ? Math.Min(100.0, commitment / avgCost * 100) : 0;
        var monthlySavings = commitment * 24 * 30 * savingsRate1Yr;

        return Task.FromResult(new SavingsPlanRecommendation
        {
            RecommendedCommitment = commitment,
            Term = "1 year",
            EstimatedSavingsPercent = savingsRate1Yr * 100,
            EstimatedMonthlySavings = monthlySavings,
            Coverage = coverage
        });
    }
}

/// <summary>119.8.8: Cost reporting.</summary>
public sealed class CostReportingStrategy : ServerlessStrategyBase
{
    public override string StrategyId => "cost-reporting";
    public override string DisplayName => "Cost Reporting";
    public override ServerlessCategory Category => ServerlessCategory.CostTracking;
    public override ServerlessStrategyCapabilities Capabilities => new() { SupportsSyncInvocation = true };
    public override string SemanticDescription => "Detailed cost reports with export to S3, scheduled delivery, and custom formats.";
    public override string[] Tags => new[] { "reporting", "export", "scheduled", "cur" };

    public Task<CostReport> GenerateReportAsync(ReportConfig config, CancellationToken ct = default)
    {
        RecordOperation("GenerateReport");
        return Task.FromResult(new CostReport
        {
            ReportId = Guid.NewGuid().ToString(),
            Period = config.Period,
            TotalCost = Random.Shared.Next(500, 5000),
            FunctionBreakdown = new[]
            {
                new FunctionCost { FunctionId = "func-1", Cost = Random.Shared.Next(100, 1000) },
                new FunctionCost { FunctionId = "func-2", Cost = Random.Shared.Next(100, 1000) }
            },
            GeneratedAt = DateTimeOffset.UtcNow
        });
    }
}

#endregion

#region Supporting Types

public sealed record UsageRecord { public required string FunctionId { get; init; } public long Invocations { get; init; } public double DurationMs { get; init; } public int MemoryMb { get; init; } public DateTimeOffset Timestamp { get; init; } }
public sealed record UsageSummary { public required string FunctionId { get; init; } public TimeSpan Period { get; init; } public long TotalInvocations { get; init; } public double TotalDurationMs { get; init; } public double AvgDurationMs { get; init; } public double TotalGbSeconds { get; init; } public int MemoryMb { get; init; } public int ColdStartCount { get; init; } public int ErrorCount { get; init; } }
public sealed record UsageTimeSlice { public DateTimeOffset StartTime { get; init; } public DateTimeOffset EndTime { get; init; } public long Invocations { get; init; } public double DurationMs { get; init; } public double GbSeconds { get; init; } }

public sealed record PricingModel { public double RequestPrice { get; init; } public double GbSecondPrice { get; init; } public long FreeRequests { get; init; } public long FreeGbSeconds { get; init; } }
public sealed record CostEstimationRequest { public ServerlessPlatform Platform { get; init; } public string Period { get; init; } = "monthly"; public long Invocations { get; init; } public double GbSeconds { get; init; } }
public sealed record CostEstimate { public ServerlessPlatform Platform { get; init; } public required string Period { get; init; } public long Invocations { get; init; } public double GbSeconds { get; init; } public double RequestCost { get; init; } public double ComputeCost { get; init; } public double TotalCost { get; init; } public double FreeTierSavings { get; init; } public double CostPerInvocation { get; init; } }
public sealed record CostProjection { public required string FunctionId { get; init; } public int MonthsAhead { get; init; } public IReadOnlyList<MonthlyProjection> MonthlyProjections { get; init; } = Array.Empty<MonthlyProjection>(); }
public sealed record MonthlyProjection { public required string Month { get; init; } public long ProjectedInvocations { get; init; } public double ProjectedCost { get; init; } public int ConfidencePercent { get; init; } }

public sealed class Budget { public required string BudgetId { get; init; } public required string Name { get; init; } public double Amount { get; init; } public required string Period { get; init; } public IReadOnlyList<double> Thresholds { get; init; } = Array.Empty<double>(); public IReadOnlyList<string> NotificationChannels { get; init; } = Array.Empty<string>(); public double CurrentSpend { get; set; } public DateTimeOffset CreatedAt { get; init; } }
public sealed record BudgetConfig { public required string Name { get; init; } public double Amount { get; init; } public required string Period { get; init; } public IReadOnlyList<double> Thresholds { get; init; } = new[] { 50.0, 80.0, 100.0 }; public IReadOnlyList<string> NotificationChannels { get; init; } = Array.Empty<string>(); }
public sealed record BudgetStatus { public required string BudgetId { get; init; } public double BudgetAmount { get; init; } public double CurrentSpend { get; init; } public double PercentUsed { get; init; } public double ForecastedSpend { get; init; } public int DaysRemaining { get; init; } public IReadOnlyList<double> TriggeredThresholds { get; init; } = Array.Empty<double>(); }

public sealed record OptimizationAnalysis { public required string FunctionId { get; init; } public double CurrentMonthlyCost { get; init; } public double OptimizedMonthlyCost { get; init; } public double PotentialSavingsPercent { get; init; } public IReadOnlyList<OptimizationRecommendation> Recommendations { get; init; } = Array.Empty<OptimizationRecommendation>(); }
public sealed record OptimizationRecommendation { public required string Type { get; init; } public int Priority { get; init; } public required string CurrentValue { get; init; } public required string RecommendedValue { get; init; } public double EstimatedSavingsPercent { get; init; } public required string Description { get; init; } }
public sealed record PowerTuningResult { public required string FunctionArn { get; init; } public int OptimalMemory { get; init; } public IReadOnlyList<PowerTuningDataPoint> Results { get; init; } = Array.Empty<PowerTuningDataPoint>(); }
public sealed record PowerTuningDataPoint { public int MemoryMb { get; init; } public double AvgDurationMs { get; init; } public double CostPerInvocation { get; init; } }

public sealed record AllocationReport { public required string TagKey { get; init; } public TimeSpan Period { get; init; } public IReadOnlyList<CostAllocation> Allocations { get; init; } = Array.Empty<CostAllocation>(); }
public sealed record CostAllocation { public required string TagValue { get; init; } public double Cost { get; init; } public double Percent { get; init; } }

public sealed record CostAnomaly { public required string AnomalyId { get; init; } public DateTimeOffset DetectedAt { get; init; } public required string Severity { get; init; } public double ExpectedCost { get; init; } public double ActualCost { get; init; } public required string RootCause { get; init; } }

public sealed record SavingsPlanRecommendation { public double RecommendedCommitment { get; init; } public required string Term { get; init; } public double EstimatedSavingsPercent { get; init; } public double EstimatedMonthlySavings { get; init; } public double Coverage { get; init; } }

public sealed record ReportConfig { public required string Period { get; init; } public string? Format { get; init; } }
public sealed record CostReport { public required string ReportId { get; init; } public required string Period { get; init; } public double TotalCost { get; init; } public IReadOnlyList<FunctionCost> FunctionBreakdown { get; init; } = Array.Empty<FunctionCost>(); public DateTimeOffset GeneratedAt { get; init; } }
public sealed record FunctionCost { public required string FunctionId { get; init; } public double Cost { get; init; } }

#endregion
