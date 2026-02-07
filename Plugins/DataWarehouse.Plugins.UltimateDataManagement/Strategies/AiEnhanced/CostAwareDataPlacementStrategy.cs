using System.Collections.Concurrent;
using System.Diagnostics;
using DataWarehouse.SDK.Contracts.IntelligenceAware;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.AiEnhanced;

/// <summary>
/// Storage pricing tier.
/// </summary>
public sealed class StoragePricing
{
    /// <summary>
    /// Tier identifier.
    /// </summary>
    public required string TierId { get; init; }

    /// <summary>
    /// Display name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Storage cost per GB per month.
    /// </summary>
    public required decimal StorageCostPerGBMonth { get; init; }

    /// <summary>
    /// Read operation cost per 10,000 operations.
    /// </summary>
    public decimal ReadCostPer10K { get; init; }

    /// <summary>
    /// Write operation cost per 10,000 operations.
    /// </summary>
    public decimal WriteCostPer10K { get; init; }

    /// <summary>
    /// Data retrieval cost per GB.
    /// </summary>
    public decimal RetrievalCostPerGB { get; init; }

    /// <summary>
    /// Minimum storage duration in days.
    /// </summary>
    public int MinStorageDays { get; init; }

    /// <summary>
    /// Early deletion penalty per GB.
    /// </summary>
    public decimal EarlyDeletionCostPerGB { get; init; }

    /// <summary>
    /// Typical access latency in milliseconds.
    /// </summary>
    public double TypicalLatencyMs { get; init; }
}

/// <summary>
/// Cost analysis result for a data object.
/// </summary>
public sealed class CostAnalysis
{
    /// <summary>
    /// Object identifier.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// Current tier.
    /// </summary>
    public required string CurrentTier { get; init; }

    /// <summary>
    /// Current monthly cost.
    /// </summary>
    public decimal CurrentMonthlyCost { get; init; }

    /// <summary>
    /// Recommended tier.
    /// </summary>
    public string? RecommendedTier { get; init; }

    /// <summary>
    /// Projected monthly cost if moved to recommended tier.
    /// </summary>
    public decimal? ProjectedMonthlyCost { get; init; }

    /// <summary>
    /// Monthly savings if recommendation is followed.
    /// </summary>
    public decimal MonthlySavings { get; init; }

    /// <summary>
    /// Annual savings if recommendation is followed.
    /// </summary>
    public decimal AnnualSavings { get; init; }

    /// <summary>
    /// Migration cost to recommended tier.
    /// </summary>
    public decimal MigrationCost { get; init; }

    /// <summary>
    /// Months until migration pays off.
    /// </summary>
    public double? PaybackMonths { get; init; }

    /// <summary>
    /// Confidence in the recommendation (0.0-1.0).
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Whether AI was used.
    /// </summary>
    public bool UsedAi { get; init; }
}

/// <summary>
/// Budget constraint for cost optimization.
/// </summary>
public sealed class BudgetConstraint
{
    /// <summary>
    /// Monthly budget in currency units.
    /// </summary>
    public required decimal MonthlyBudget { get; init; }

    /// <summary>
    /// Current monthly spend.
    /// </summary>
    public decimal CurrentSpend { get; init; }

    /// <summary>
    /// Hard limit (cannot exceed).
    /// </summary>
    public bool IsHardLimit { get; init; }

    /// <summary>
    /// Warning threshold percentage (0-1).
    /// </summary>
    public decimal WarningThreshold { get; init; } = 0.8m;

    /// <summary>
    /// Alert threshold percentage (0-1).
    /// </summary>
    public decimal AlertThreshold { get; init; } = 0.95m;
}

/// <summary>
/// Cost-aware data placement strategy that optimizes for cost
/// while respecting performance requirements.
/// </summary>
/// <remarks>
/// Features:
/// - Real-time cost modeling
/// - Cost vs performance trade-off analysis
/// - Budget constraint enforcement
/// - Migration ROI calculation
/// - AI-powered access pattern prediction for cost forecasting
/// </remarks>
public sealed class CostAwareDataPlacementStrategy : AiEnhancedStrategyBase
{
    private readonly ConcurrentDictionary<string, StoragePricing> _tiers = new();
    private readonly ConcurrentDictionary<string, (string Tier, long SizeBytes, long ReadOps, long WriteOps, DateTime Since)> _objectMetrics = new();
    private readonly ConcurrentDictionary<string, CostAnalysis> _analysisCache = new();
    private BudgetConstraint? _budget;

    /// <summary>
    /// Initializes a new CostAwareDataPlacementStrategy.
    /// </summary>
    public CostAwareDataPlacementStrategy()
    {
        InitializeDefaultTiers();
    }

    /// <inheritdoc/>
    public override string StrategyId => "ai.cost-aware";

    /// <inheritdoc/>
    public override string DisplayName => "Cost-Aware Data Placement";

    /// <inheritdoc/>
    public override AiEnhancedCategory AiCategory => AiEnhancedCategory.CostOptimization;

    /// <inheritdoc/>
    public override IntelligenceCapabilities RequiredCapabilities =>
        IntelligenceCapabilities.AccessPatternPrediction | IntelligenceCapabilities.TieringRecommendation;

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 5_000,
        TypicalLatencyMs = 10.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Cost-aware data placement strategy using AI to analyze access patterns, " +
        "model storage costs, and optimize placement for cost efficiency while respecting performance SLOs.";

    /// <inheritdoc/>
    public override string[] Tags => ["ai", "cost", "optimization", "placement", "budget", "tiering"];

    /// <summary>
    /// Registers a storage pricing tier.
    /// </summary>
    public void RegisterTier(StoragePricing tier)
    {
        ArgumentNullException.ThrowIfNull(tier);
        ArgumentException.ThrowIfNullOrWhiteSpace(tier.TierId);

        _tiers[tier.TierId] = tier;
    }

    /// <summary>
    /// Gets all registered tiers.
    /// </summary>
    public IReadOnlyList<StoragePricing> GetTiers() => _tiers.Values.OrderBy(t => t.StorageCostPerGBMonth).ToList();

    /// <summary>
    /// Sets the budget constraint.
    /// </summary>
    public void SetBudget(BudgetConstraint budget)
    {
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    }

    /// <summary>
    /// Gets the current budget status.
    /// </summary>
    public (decimal Budget, decimal CurrentSpend, double UtilizationPercent, string Status)? GetBudgetStatus()
    {
        if (_budget == null)
            return null;

        var utilization = _budget.CurrentSpend / _budget.MonthlyBudget;
        var status = utilization switch
        {
            >= 1.0m when _budget.IsHardLimit => "EXCEEDED",
            >= 1.0m => "OVER_BUDGET",
            _ when utilization >= _budget.AlertThreshold => "ALERT",
            _ when utilization >= _budget.WarningThreshold => "WARNING",
            _ => "OK"
        };

        return (_budget.MonthlyBudget, _budget.CurrentSpend, (double)utilization * 100, status);
    }

    /// <summary>
    /// Records object metrics for cost analysis.
    /// </summary>
    public void RecordObjectMetrics(string objectId, string currentTier, long sizeBytes, long readOps = 0, long writeOps = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        if (_objectMetrics.TryGetValue(objectId, out var existing))
        {
            _objectMetrics[objectId] = (currentTier, sizeBytes, existing.ReadOps + readOps, existing.WriteOps + writeOps, existing.Since);
        }
        else
        {
            _objectMetrics[objectId] = (currentTier, sizeBytes, readOps, writeOps, DateTime.UtcNow);
        }

        // Invalidate cache
        _analysisCache.TryRemove(objectId, out _);
    }

    /// <summary>
    /// Analyzes cost for a specific object.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="forceRefresh">Force new analysis.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Cost analysis result.</returns>
    public async Task<CostAnalysis?> AnalyzeCostAsync(
        string objectId,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        // Check cache
        if (!forceRefresh && _analysisCache.TryGetValue(objectId, out var cached))
        {
            return cached;
        }

        // Get metrics
        if (!_objectMetrics.TryGetValue(objectId, out var metrics))
        {
            return null;
        }

        var sw = Stopwatch.StartNew();
        CostAnalysis analysis;

        if (IsAiAvailable)
        {
            analysis = await AnalyzeWithAiAsync(objectId, metrics, ct) ?? AnalyzeWithFallback(objectId, metrics);
            RecordAiOperation(analysis.UsedAi, false, analysis.Confidence, sw.Elapsed.TotalMilliseconds);
        }
        else
        {
            analysis = AnalyzeWithFallback(objectId, metrics);
            RecordAiOperation(false, false, 0, sw.Elapsed.TotalMilliseconds);
        }

        _analysisCache[objectId] = analysis;
        return analysis;
    }

    /// <summary>
    /// Gets all cost optimization recommendations.
    /// </summary>
    /// <param name="minSavings">Minimum monthly savings threshold.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of recommendations.</returns>
    public async Task<IReadOnlyList<CostAnalysis>> GetOptimizationRecommendationsAsync(
        decimal minSavings = 0.01m,
        CancellationToken ct = default)
    {
        var recommendations = new List<CostAnalysis>();

        foreach (var objectId in _objectMetrics.Keys)
        {
            var analysis = await AnalyzeCostAsync(objectId, false, ct);
            if (analysis != null && analysis.MonthlySavings >= minSavings)
            {
                recommendations.Add(analysis);
            }
        }

        return recommendations
            .OrderByDescending(r => r.AnnualSavings)
            .ToList();
    }

    /// <summary>
    /// Gets total potential savings.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Total monthly and annual savings.</returns>
    public async Task<(decimal MonthlySavings, decimal AnnualSavings)> GetTotalPotentialSavingsAsync(CancellationToken ct = default)
    {
        var recommendations = await GetOptimizationRecommendationsAsync(0, ct);
        var monthly = recommendations.Sum(r => r.MonthlySavings);
        var annual = recommendations.Sum(r => r.AnnualSavings);
        return (monthly, annual);
    }

    /// <summary>
    /// Gets current total monthly cost.
    /// </summary>
    public decimal GetCurrentMonthlyCost()
    {
        decimal total = 0;

        foreach (var (objectId, metrics) in _objectMetrics)
        {
            if (_tiers.TryGetValue(metrics.Tier, out var tier))
            {
                total += CalculateMonthlyCost(metrics.SizeBytes, metrics.ReadOps, metrics.WriteOps, tier, DateTime.UtcNow - metrics.Since);
            }
        }

        return total;
    }

    /// <summary>
    /// Checks if a placement would exceed budget.
    /// </summary>
    public bool WouldExceedBudget(string tierId, long sizeBytes, long estimatedMonthlyReadOps, long estimatedMonthlyWriteOps)
    {
        if (_budget == null || !_budget.IsHardLimit)
            return false;

        if (!_tiers.TryGetValue(tierId, out var tier))
            return false;

        var additionalCost = CalculateMonthlyCost(sizeBytes, estimatedMonthlyReadOps, estimatedMonthlyWriteOps, tier, TimeSpan.FromDays(30));
        return _budget.CurrentSpend + additionalCost > _budget.MonthlyBudget;
    }

    private async Task<CostAnalysis?> AnalyzeWithAiAsync(
        string objectId,
        (string Tier, long SizeBytes, long ReadOps, long WriteOps, DateTime Since) metrics,
        CancellationToken ct)
    {
        var daysSinceTracking = (DateTime.UtcNow - metrics.Since).TotalDays;
        if (daysSinceTracking < 1)
            daysSinceTracking = 1;

        var dailyReads = metrics.ReadOps / daysSinceTracking;
        var dailyWrites = metrics.WriteOps / daysSinceTracking;

        var inputData = new Dictionary<string, object>
        {
            ["objectId"] = objectId,
            ["currentTier"] = metrics.Tier,
            ["sizeGB"] = metrics.SizeBytes / (1024.0 * 1024 * 1024),
            ["dailyReadOps"] = dailyReads,
            ["dailyWriteOps"] = dailyWrites,
            ["trackedDays"] = daysSinceTracking,
            ["availableTiers"] = _tiers.Keys.ToArray()
        };

        var prediction = await RequestPredictionAsync("cost_optimization", inputData, DefaultContext, ct);

        if (prediction.HasValue)
        {
            var recommendedTier = prediction.Value.Prediction?.ToString();
            if (recommendedTier != null && _tiers.ContainsKey(recommendedTier))
            {
                return BuildCostAnalysis(objectId, metrics, recommendedTier, prediction.Value.Confidence, true);
            }
        }

        return null;
    }

    private CostAnalysis AnalyzeWithFallback(
        string objectId,
        (string Tier, long SizeBytes, long ReadOps, long WriteOps, DateTime Since) metrics)
    {
        var daysSinceTracking = Math.Max(1, (DateTime.UtcNow - metrics.Since).TotalDays);
        var monthlyReadOps = (long)(metrics.ReadOps / daysSinceTracking * 30);
        var monthlyWriteOps = (long)(metrics.WriteOps / daysSinceTracking * 30);

        // Find cheapest tier based on usage pattern
        string? bestTier = null;
        decimal bestCost = decimal.MaxValue;

        foreach (var tier in _tiers.Values)
        {
            var cost = CalculateMonthlyCost(metrics.SizeBytes, monthlyReadOps, monthlyWriteOps, tier, TimeSpan.FromDays(30));

            if (cost < bestCost)
            {
                bestCost = cost;
                bestTier = tier.TierId;
            }
        }

        return BuildCostAnalysis(objectId, metrics, bestTier, 0.5, false);
    }

    private CostAnalysis BuildCostAnalysis(
        string objectId,
        (string Tier, long SizeBytes, long ReadOps, long WriteOps, DateTime Since) metrics,
        string? recommendedTier,
        double confidence,
        bool usedAi)
    {
        var daysSinceTracking = Math.Max(1, (DateTime.UtcNow - metrics.Since).TotalDays);
        var monthlyReadOps = (long)(metrics.ReadOps / daysSinceTracking * 30);
        var monthlyWriteOps = (long)(metrics.WriteOps / daysSinceTracking * 30);

        var currentCost = 0m;
        if (_tiers.TryGetValue(metrics.Tier, out var currentTier))
        {
            currentCost = CalculateMonthlyCost(metrics.SizeBytes, monthlyReadOps, monthlyWriteOps, currentTier, TimeSpan.FromDays(30));
        }

        var projectedCost = currentCost;
        var migrationCost = 0m;

        if (recommendedTier != null && recommendedTier != metrics.Tier && _tiers.TryGetValue(recommendedTier, out var recTier))
        {
            projectedCost = CalculateMonthlyCost(metrics.SizeBytes, monthlyReadOps, monthlyWriteOps, recTier, TimeSpan.FromDays(30));
            migrationCost = CalculateMigrationCost(metrics.SizeBytes, currentTier, recTier);
        }

        var monthlySavings = currentCost - projectedCost;
        var annualSavings = monthlySavings * 12;
        var paybackMonths = migrationCost > 0 && monthlySavings > 0
            ? (double)(migrationCost / monthlySavings)
            : (double?)null;

        return new CostAnalysis
        {
            ObjectId = objectId,
            CurrentTier = metrics.Tier,
            CurrentMonthlyCost = currentCost,
            RecommendedTier = recommendedTier != metrics.Tier ? recommendedTier : null,
            ProjectedMonthlyCost = recommendedTier != metrics.Tier ? projectedCost : null,
            MonthlySavings = Math.Max(0, monthlySavings),
            AnnualSavings = Math.Max(0, annualSavings),
            MigrationCost = migrationCost,
            PaybackMonths = paybackMonths,
            Confidence = confidence,
            UsedAi = usedAi
        };
    }

    private static decimal CalculateMonthlyCost(long sizeBytes, long monthlyReadOps, long monthlyWriteOps, StoragePricing tier, TimeSpan duration)
    {
        var sizeGB = sizeBytes / (1024m * 1024 * 1024);
        var storageCost = sizeGB * tier.StorageCostPerGBMonth;
        var readCost = (monthlyReadOps / 10000m) * tier.ReadCostPer10K;
        var writeCost = (monthlyWriteOps / 10000m) * tier.WriteCostPer10K;

        return storageCost + readCost + writeCost;
    }

    private static decimal CalculateMigrationCost(long sizeBytes, StoragePricing? from, StoragePricing to)
    {
        var sizeGB = sizeBytes / (1024m * 1024 * 1024);

        // Retrieval cost from source
        var retrievalCost = from != null ? sizeGB * from.RetrievalCostPerGB : 0;

        // Write cost to destination
        var writeCost = to.WriteCostPer10K / 10000m; // Assuming 1 write operation per object

        return retrievalCost + writeCost;
    }

    private void InitializeDefaultTiers()
    {
        RegisterTier(new StoragePricing
        {
            TierId = "hot",
            Name = "Hot Storage",
            StorageCostPerGBMonth = 0.023m,
            ReadCostPer10K = 0.004m,
            WriteCostPer10K = 0.005m,
            RetrievalCostPerGB = 0m,
            MinStorageDays = 0,
            EarlyDeletionCostPerGB = 0m,
            TypicalLatencyMs = 1
        });

        RegisterTier(new StoragePricing
        {
            TierId = "cool",
            Name = "Cool Storage",
            StorageCostPerGBMonth = 0.01m,
            ReadCostPer10K = 0.01m,
            WriteCostPer10K = 0.01m,
            RetrievalCostPerGB = 0.01m,
            MinStorageDays = 30,
            EarlyDeletionCostPerGB = 0.01m,
            TypicalLatencyMs = 10
        });

        RegisterTier(new StoragePricing
        {
            TierId = "archive",
            Name = "Archive Storage",
            StorageCostPerGBMonth = 0.002m,
            ReadCostPer10K = 0.10m,
            WriteCostPer10K = 0.10m,
            RetrievalCostPerGB = 0.02m,
            MinStorageDays = 180,
            EarlyDeletionCostPerGB = 0.02m,
            TypicalLatencyMs = 3600000 // Hours for archive retrieval
        });
    }
}
