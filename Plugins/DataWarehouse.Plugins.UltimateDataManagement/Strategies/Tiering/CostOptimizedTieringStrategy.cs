namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Tiering;

/// <summary>
/// Cost-optimized tiering strategy that minimizes total storage costs by balancing
/// storage costs against access costs for each tier.
/// </summary>
/// <remarks>
/// Features:
/// - Cost models per storage tier
/// - Access cost vs storage cost trade-offs
/// - Total cost of ownership (TCO) optimization
/// - Predictive cost modeling
/// - Cost-benefit analysis for tier movements
/// </remarks>
public sealed class CostOptimizedTieringStrategy : TieringStrategyBase
{
    private CostModel _costModel = CostModel.Default;
    private readonly TimeSpan _analysisWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// Cost model for storage tiers.
    /// </summary>
    public sealed class CostModel
    {
        /// <summary>
        /// Storage cost per GB per month for each tier.
        /// </summary>
        public Dictionary<StorageTier, decimal> StorageCostPerGbMonth { get; init; } = new();

        /// <summary>
        /// Read access cost per 1000 operations for each tier.
        /// </summary>
        public Dictionary<StorageTier, decimal> ReadCostPer1000Ops { get; init; } = new();

        /// <summary>
        /// Write access cost per 1000 operations for each tier.
        /// </summary>
        public Dictionary<StorageTier, decimal> WriteCostPer1000Ops { get; init; } = new();

        /// <summary>
        /// Data retrieval cost per GB for each tier.
        /// </summary>
        public Dictionary<StorageTier, decimal> RetrievalCostPerGb { get; init; } = new();

        /// <summary>
        /// Early deletion penalty period (days) for each tier.
        /// </summary>
        public Dictionary<StorageTier, int> MinRetentionDays { get; init; } = new();

        /// <summary>
        /// Cost per GB for tier transition.
        /// </summary>
        public decimal TransitionCostPerGb { get; init; } = 0.01m;

        /// <summary>
        /// Gets the default cost model based on typical cloud storage pricing.
        /// </summary>
        public static CostModel Default => new()
        {
            StorageCostPerGbMonth = new Dictionary<StorageTier, decimal>
            {
                [StorageTier.Hot] = 0.023m,
                [StorageTier.Warm] = 0.0125m,
                [StorageTier.Cold] = 0.004m,
                [StorageTier.Archive] = 0.00099m,
                [StorageTier.Glacier] = 0.0004m
            },
            ReadCostPer1000Ops = new Dictionary<StorageTier, decimal>
            {
                [StorageTier.Hot] = 0.004m,
                [StorageTier.Warm] = 0.01m,
                [StorageTier.Cold] = 0.01m,
                [StorageTier.Archive] = 0.05m,
                [StorageTier.Glacier] = 0.10m
            },
            WriteCostPer1000Ops = new Dictionary<StorageTier, decimal>
            {
                [StorageTier.Hot] = 0.005m,
                [StorageTier.Warm] = 0.01m,
                [StorageTier.Cold] = 0.01m,
                [StorageTier.Archive] = 0.05m,
                [StorageTier.Glacier] = 0.10m
            },
            RetrievalCostPerGb = new Dictionary<StorageTier, decimal>
            {
                [StorageTier.Hot] = 0.0m,
                [StorageTier.Warm] = 0.01m,
                [StorageTier.Cold] = 0.02m,
                [StorageTier.Archive] = 0.03m,
                [StorageTier.Glacier] = 0.05m
            },
            MinRetentionDays = new Dictionary<StorageTier, int>
            {
                [StorageTier.Hot] = 0,
                [StorageTier.Warm] = 30,
                [StorageTier.Cold] = 90,
                [StorageTier.Archive] = 180,
                [StorageTier.Glacier] = 365
            },
            TransitionCostPerGb = 0.01m
        };

        /// <summary>
        /// Gets AWS S3-like pricing model.
        /// </summary>
        public static CostModel AwsS3 => new()
        {
            StorageCostPerGbMonth = new Dictionary<StorageTier, decimal>
            {
                [StorageTier.Hot] = 0.023m,
                [StorageTier.Warm] = 0.0125m,
                [StorageTier.Cold] = 0.004m,
                [StorageTier.Archive] = 0.00099m,
                [StorageTier.Glacier] = 0.00036m
            },
            ReadCostPer1000Ops = new Dictionary<StorageTier, decimal>
            {
                [StorageTier.Hot] = 0.0004m,
                [StorageTier.Warm] = 0.001m,
                [StorageTier.Cold] = 0.001m,
                [StorageTier.Archive] = 0.05m,
                [StorageTier.Glacier] = 0.10m
            },
            WriteCostPer1000Ops = new Dictionary<StorageTier, decimal>
            {
                [StorageTier.Hot] = 0.005m,
                [StorageTier.Warm] = 0.01m,
                [StorageTier.Cold] = 0.01m,
                [StorageTier.Archive] = 0.05m,
                [StorageTier.Glacier] = 0.10m
            },
            RetrievalCostPerGb = new Dictionary<StorageTier, decimal>
            {
                [StorageTier.Hot] = 0.0m,
                [StorageTier.Warm] = 0.01m,
                [StorageTier.Cold] = 0.01m,
                [StorageTier.Archive] = 0.02m,
                [StorageTier.Glacier] = 0.03m
            },
            MinRetentionDays = new Dictionary<StorageTier, int>
            {
                [StorageTier.Hot] = 0,
                [StorageTier.Warm] = 30,
                [StorageTier.Cold] = 90,
                [StorageTier.Archive] = 180,
                [StorageTier.Glacier] = 365
            },
            TransitionCostPerGb = 0.01m
        };
    }

    /// <summary>
    /// Result of a cost analysis.
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
        public StorageTier CurrentTier { get; init; }

        /// <summary>
        /// Monthly cost in current tier.
        /// </summary>
        public decimal CurrentMonthlyCost { get; init; }

        /// <summary>
        /// Optimal tier based on cost analysis.
        /// </summary>
        public StorageTier OptimalTier { get; init; }

        /// <summary>
        /// Monthly cost in optimal tier.
        /// </summary>
        public decimal OptimalMonthlyCost { get; init; }

        /// <summary>
        /// Cost to transition to optimal tier.
        /// </summary>
        public decimal TransitionCost { get; init; }

        /// <summary>
        /// Months until break-even after transition.
        /// </summary>
        public double BreakEvenMonths { get; init; }

        /// <summary>
        /// Monthly savings after transition.
        /// </summary>
        public decimal MonthlySavings => CurrentMonthlyCost - OptimalMonthlyCost;

        /// <summary>
        /// Annual savings after transition.
        /// </summary>
        public decimal AnnualSavings => MonthlySavings * 12 - TransitionCost;

        /// <summary>
        /// Whether the transition is recommended.
        /// </summary>
        public bool IsTransitionRecommended { get; init; }

        /// <summary>
        /// Cost breakdown by component.
        /// </summary>
        public Dictionary<string, decimal> CostBreakdown { get; init; } = new();
    }

    /// <inheritdoc/>
    public override string StrategyId => "tiering.cost-optimized";

    /// <inheritdoc/>
    public override string DisplayName => "Cost-Optimized Tiering";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 50000,
        TypicalLatencyMs = 1.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Cost-optimized tiering that minimizes total storage costs by analyzing " +
        "storage costs, access costs, retrieval costs, and transition costs. " +
        "Provides TCO optimization with break-even analysis.";

    /// <inheritdoc/>
    public override string[] Tags => ["tiering", "cost", "optimization", "tco", "savings"];

    /// <summary>
    /// Gets or sets the cost model.
    /// </summary>
    public CostModel CurrentCostModel
    {
        get => _costModel;
        set => _costModel = value ?? CostModel.Default;
    }

    /// <summary>
    /// Analyzes the cost for an object across all tiers.
    /// </summary>
    /// <param name="data">The data object to analyze.</param>
    /// <returns>Cost analysis result.</returns>
    public CostAnalysis AnalyzeCosts(DataObject data)
    {
        var sizeGb = data.SizeBytes / (1024.0m * 1024m * 1024m);
        var monthlyReads = data.AccessesLast30Days;
        var monthlyWrites = 0; // Assume mostly read workload

        // Calculate cost for current tier
        var currentCost = CalculateMonthlyCost(data.CurrentTier, sizeGb, monthlyReads, monthlyWrites);

        // Find optimal tier
        var optimalTier = data.CurrentTier;
        var optimalCost = currentCost;

        foreach (StorageTier tier in Enum.GetValues<StorageTier>())
        {
            var tierCost = CalculateMonthlyCost(tier, sizeGb, monthlyReads, monthlyWrites);

            // Check minimum retention requirement
            if (tier > data.CurrentTier && data.Age.TotalDays < _costModel.MinRetentionDays.GetValueOrDefault(data.CurrentTier, 0))
            {
                continue; // Skip - would incur early deletion penalty
            }

            if (tierCost < optimalCost)
            {
                optimalCost = tierCost;
                optimalTier = tier;
            }
        }

        var transitionCost = optimalTier != data.CurrentTier
            ? sizeGb * _costModel.TransitionCostPerGb
            : 0m;

        var monthlySavings = currentCost - optimalCost;
        var breakEvenMonths = monthlySavings > 0 ? (double)(transitionCost / monthlySavings) : double.PositiveInfinity;

        return new CostAnalysis
        {
            ObjectId = data.ObjectId,
            CurrentTier = data.CurrentTier,
            CurrentMonthlyCost = currentCost,
            OptimalTier = optimalTier,
            OptimalMonthlyCost = optimalCost,
            TransitionCost = transitionCost,
            BreakEvenMonths = breakEvenMonths,
            IsTransitionRecommended = optimalTier != data.CurrentTier && breakEvenMonths <= 3,
            CostBreakdown = new Dictionary<string, decimal>
            {
                ["storage"] = sizeGb * _costModel.StorageCostPerGbMonth.GetValueOrDefault(data.CurrentTier, 0),
                ["reads"] = (monthlyReads / 1000m) * _costModel.ReadCostPer1000Ops.GetValueOrDefault(data.CurrentTier, 0),
                ["retrieval"] = sizeGb * (monthlyReads / 30m) * _costModel.RetrievalCostPerGb.GetValueOrDefault(data.CurrentTier, 0)
            }
        };
    }

    /// <inheritdoc/>
    protected override Task<TierRecommendation> EvaluateCoreAsync(DataObject data, CancellationToken ct)
    {
        var analysis = AnalyzeCosts(data);

        if (!analysis.IsTransitionRecommended)
        {
            return Task.FromResult(NoChange(data,
                $"Current tier ({data.CurrentTier}) is cost-optimal. Monthly cost: ${analysis.CurrentMonthlyCost:F4}"));
        }

        var reason = BuildReason(analysis);
        var confidence = CalculateConfidence(analysis);

        if (analysis.OptimalTier > data.CurrentTier)
        {
            return Task.FromResult(Demote(data, analysis.OptimalTier, reason, confidence,
                CalculatePriority(analysis), analysis.MonthlySavings));
        }
        else
        {
            return Task.FromResult(Promote(data, analysis.OptimalTier, reason, confidence,
                CalculatePriority(analysis)));
        }
    }

    private decimal CalculateMonthlyCost(StorageTier tier, decimal sizeGb, int monthlyReads, int monthlyWrites)
    {
        var storageCost = sizeGb * _costModel.StorageCostPerGbMonth.GetValueOrDefault(tier, 0.023m);
        var readCost = (monthlyReads / 1000m) * _costModel.ReadCostPer1000Ops.GetValueOrDefault(tier, 0.004m);
        var writeCost = (monthlyWrites / 1000m) * _costModel.WriteCostPer1000Ops.GetValueOrDefault(tier, 0.005m);
        var retrievalCost = sizeGb * (monthlyReads / 30m) * _costModel.RetrievalCostPerGb.GetValueOrDefault(tier, 0m);

        return storageCost + readCost + writeCost + retrievalCost;
    }

    private static string BuildReason(CostAnalysis analysis)
    {
        var savingsPercent = analysis.CurrentMonthlyCost > 0
            ? (analysis.MonthlySavings / analysis.CurrentMonthlyCost) * 100
            : 0;

        return $"Cost optimization: {analysis.CurrentTier} -> {analysis.OptimalTier}. " +
               $"Monthly savings: ${analysis.MonthlySavings:F4} ({savingsPercent:F1}%). " +
               $"Annual savings: ${analysis.AnnualSavings:F2}. " +
               $"Break-even: {analysis.BreakEvenMonths:F1} months.";
    }

    private static double CalculateConfidence(CostAnalysis analysis)
    {
        // Higher confidence for larger savings percentages
        if (analysis.CurrentMonthlyCost == 0)
            return 0.5;

        var savingsRatio = (double)(analysis.MonthlySavings / analysis.CurrentMonthlyCost);
        return Math.Min(1.0, 0.6 + savingsRatio * 0.4);
    }

    private static double CalculatePriority(CostAnalysis analysis)
    {
        // Higher priority for larger absolute savings
        var annualSavingsNormalized = Math.Min(1.0, (double)analysis.AnnualSavings / 100);
        var breakEvenFactor = analysis.BreakEvenMonths < 1 ? 1.0 :
            analysis.BreakEvenMonths < 3 ? 0.8 :
            analysis.BreakEvenMonths < 6 ? 0.5 : 0.3;

        return annualSavingsNormalized * breakEvenFactor;
    }
}
