using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Tiering;

/// <summary>
/// Hybrid tiering strategy that combines multiple tiering strategies using a composite pattern.
/// Supports strategy weighting, conflict resolution, and configurable aggregation modes.
/// </summary>
/// <remarks>
/// Features:
/// - Composite strategy pattern for combining multiple strategies
/// - Configurable strategy weights for influence control
/// - Multiple conflict resolution modes (voting, weighted, priority-based)
/// - Strategy enablement and exclusion rules
/// - Unified recommendation with confidence aggregation
/// </remarks>
public sealed class HybridTieringStrategy : TieringStrategyBase
{
    private readonly BoundedDictionary<string, StrategyRegistration> _strategies = new BoundedDictionary<string, StrategyRegistration>(1000);
    private ConflictResolutionMode _conflictMode = ConflictResolutionMode.WeightedVoting;

    /// <summary>
    /// Registration information for a strategy.
    /// </summary>
    private sealed class StrategyRegistration
    {
        public required ITieringStrategy Strategy { get; init; }
        public double Weight { get; set; } = 1.0;
        public int Priority { get; set; }
        public bool IsEnabled { get; set; } = true;
        public HashSet<string> ExcludedClassifications { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Modes for resolving conflicts between strategy recommendations.
    /// </summary>
    public enum ConflictResolutionMode
    {
        /// <summary>
        /// Simple majority voting - most common recommendation wins.
        /// </summary>
        MajorityVoting,

        /// <summary>
        /// Weighted voting based on strategy weights and confidence.
        /// </summary>
        WeightedVoting,

        /// <summary>
        /// Highest priority strategy wins.
        /// </summary>
        PriorityBased,

        /// <summary>
        /// Most conservative (coldest) tier recommendation wins.
        /// </summary>
        MostConservative,

        /// <summary>
        /// Most aggressive (hottest) tier recommendation wins.
        /// </summary>
        MostAggressive,

        /// <summary>
        /// Highest confidence recommendation wins.
        /// </summary>
        HighestConfidence,

        /// <summary>
        /// Unanimous agreement required, otherwise no change.
        /// </summary>
        Unanimous
    }

    /// <summary>
    /// Result of strategy aggregation.
    /// </summary>
    public sealed class AggregatedRecommendation
    {
        /// <summary>
        /// The final recommended tier.
        /// </summary>
        public StorageTier RecommendedTier { get; init; }

        /// <summary>
        /// Aggregated confidence level.
        /// </summary>
        public double AggregatedConfidence { get; init; }

        /// <summary>
        /// Individual strategy recommendations.
        /// </summary>
        public IReadOnlyDictionary<string, TierRecommendation> StrategyRecommendations { get; init; } =
            new Dictionary<string, TierRecommendation>();

        /// <summary>
        /// Number of strategies that agreed with the final recommendation.
        /// </summary>
        public int AgreementCount { get; init; }

        /// <summary>
        /// Total number of strategies evaluated.
        /// </summary>
        public int TotalStrategies { get; init; }

        /// <summary>
        /// Agreement ratio (0-1).
        /// </summary>
        public double AgreementRatio => TotalStrategies > 0 ? AgreementCount / (double)TotalStrategies : 0;

        /// <summary>
        /// Conflict resolution mode used.
        /// </summary>
        public ConflictResolutionMode ResolutionMode { get; init; }

        /// <summary>
        /// Explanation of how the recommendation was determined.
        /// </summary>
        public string Reasoning { get; init; } = string.Empty;
    }

    /// <inheritdoc/>
    public override string StrategyId => "tiering.hybrid";

    /// <inheritdoc/>
    public override string DisplayName => "Hybrid Tiering";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 10000,
        TypicalLatencyMs = 5.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Hybrid tiering that combines multiple strategies using configurable weights " +
        "and conflict resolution. Supports voting, priority-based, and confidence-based " +
        "aggregation for optimal tier recommendations.";

    /// <inheritdoc/>
    public override string[] Tags => ["tiering", "hybrid", "composite", "multi-strategy", "aggregation"];

    /// <summary>
    /// Gets or sets the conflict resolution mode.
    /// </summary>
    public ConflictResolutionMode ConflictMode
    {
        get => _conflictMode;
        set => _conflictMode = value;
    }

    /// <summary>
    /// Registers a strategy with the hybrid tiering system.
    /// </summary>
    /// <param name="strategy">The strategy to register.</param>
    /// <param name="weight">Weight for voting (default 1.0).</param>
    /// <param name="priority">Priority for priority-based resolution (higher = more important).</param>
    public void RegisterStrategy(ITieringStrategy strategy, double weight = 1.0, int priority = 0)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        _strategies[strategy.StrategyId] = new StrategyRegistration
        {
            Strategy = strategy,
            Weight = Math.Max(0, weight),
            Priority = priority,
            IsEnabled = true
        };
    }

    /// <summary>
    /// Unregisters a strategy.
    /// </summary>
    /// <param name="strategyId">The strategy identifier.</param>
    /// <returns>True if the strategy was removed.</returns>
    public bool UnregisterStrategy(string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        return _strategies.TryRemove(strategyId, out _);
    }

    /// <summary>
    /// Gets all registered strategies.
    /// </summary>
    public IReadOnlyList<(string StrategyId, double Weight, int Priority, bool IsEnabled)> GetRegisteredStrategies()
    {
        return _strategies.Select(kvp => (kvp.Key, kvp.Value.Weight, kvp.Value.Priority, kvp.Value.IsEnabled)).ToList();
    }

    /// <summary>
    /// Sets the weight for a strategy.
    /// </summary>
    /// <param name="strategyId">The strategy identifier.</param>
    /// <param name="weight">The new weight.</param>
    /// <returns>True if the strategy was found and updated.</returns>
    public bool SetStrategyWeight(string strategyId, double weight)
    {
        if (_strategies.TryGetValue(strategyId, out var registration))
        {
            registration.Weight = Math.Max(0, weight);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Sets the priority for a strategy.
    /// </summary>
    /// <param name="strategyId">The strategy identifier.</param>
    /// <param name="priority">The new priority.</param>
    /// <returns>True if the strategy was found and updated.</returns>
    public bool SetStrategyPriority(string strategyId, int priority)
    {
        if (_strategies.TryGetValue(strategyId, out var registration))
        {
            registration.Priority = priority;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Enables or disables a strategy.
    /// </summary>
    /// <param name="strategyId">The strategy identifier.</param>
    /// <param name="enabled">Whether to enable the strategy.</param>
    /// <returns>True if the strategy was found and updated.</returns>
    public bool SetStrategyEnabled(string strategyId, bool enabled)
    {
        if (_strategies.TryGetValue(strategyId, out var registration))
        {
            registration.IsEnabled = enabled;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Excludes a classification from a specific strategy.
    /// </summary>
    /// <param name="strategyId">The strategy identifier.</param>
    /// <param name="classification">The classification to exclude.</param>
    public bool ExcludeClassificationFromStrategy(string strategyId, string classification)
    {
        if (_strategies.TryGetValue(strategyId, out var registration))
        {
            registration.ExcludedClassifications.Add(classification);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Evaluates all strategies and returns aggregated recommendation.
    /// </summary>
    /// <param name="data">The data object to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aggregated recommendation with details from all strategies.</returns>
    public async Task<AggregatedRecommendation> EvaluateAllAsync(DataObject data, CancellationToken ct = default)
    {
        var recommendations = new Dictionary<string, TierRecommendation>();
        var weightedVotes = new Dictionary<StorageTier, double>();

        foreach (var (strategyId, registration) in _strategies)
        {
            if (!registration.IsEnabled)
                continue;

            if (!string.IsNullOrEmpty(data.Classification) &&
                registration.ExcludedClassifications.Contains(data.Classification))
                continue;

            try
            {
                var recommendation = await registration.Strategy.EvaluateAsync(data, ct);
                recommendations[strategyId] = recommendation;

                // Accumulate weighted votes
                var vote = recommendation.RecommendedTier;
                var voteWeight = registration.Weight * recommendation.Confidence;

                weightedVotes.TryGetValue(vote, out var existing);
                weightedVotes[vote] = existing + voteWeight;
            }
            catch
            {
                // Strategy evaluation failed - skip
            }
        }

        if (recommendations.Count == 0)
        {
            return new AggregatedRecommendation
            {
                RecommendedTier = data.CurrentTier,
                AggregatedConfidence = 0,
                StrategyRecommendations = recommendations,
                AgreementCount = 0,
                TotalStrategies = 0,
                ResolutionMode = _conflictMode,
                Reasoning = "No strategies evaluated successfully"
            };
        }

        // Resolve conflicts
        var (finalTier, confidence, reasoning) = ResolveConflicts(data, recommendations, weightedVotes);

        var agreementCount = recommendations.Values.Count(r => r.RecommendedTier == finalTier);

        return new AggregatedRecommendation
        {
            RecommendedTier = finalTier,
            AggregatedConfidence = confidence,
            StrategyRecommendations = recommendations,
            AgreementCount = agreementCount,
            TotalStrategies = recommendations.Count,
            ResolutionMode = _conflictMode,
            Reasoning = reasoning
        };
    }

    /// <inheritdoc/>
    protected override async Task<TierRecommendation> EvaluateCoreAsync(DataObject data, CancellationToken ct)
    {
        var aggregated = await EvaluateAllAsync(data, ct);

        if (aggregated.RecommendedTier == data.CurrentTier)
        {
            return NoChange(data, aggregated.Reasoning);
        }

        var detailedReason = $"{aggregated.Reasoning} (Agreement: {aggregated.AgreementCount}/{aggregated.TotalStrategies})";

        if (aggregated.RecommendedTier < data.CurrentTier)
        {
            return Promote(data, aggregated.RecommendedTier, detailedReason,
                aggregated.AggregatedConfidence, aggregated.AgreementRatio);
        }
        else
        {
            return Demote(data, aggregated.RecommendedTier, detailedReason,
                aggregated.AggregatedConfidence, aggregated.AgreementRatio,
                EstimateMonthlySavings(data.SizeBytes, data.CurrentTier, aggregated.RecommendedTier));
        }
    }

    private (StorageTier tier, double confidence, string reasoning) ResolveConflicts(
        DataObject data,
        Dictionary<string, TierRecommendation> recommendations,
        Dictionary<StorageTier, double> weightedVotes)
    {
        return _conflictMode switch
        {
            ConflictResolutionMode.MajorityVoting => ResolveMajorityVoting(recommendations),
            ConflictResolutionMode.WeightedVoting => ResolveWeightedVoting(weightedVotes),
            ConflictResolutionMode.PriorityBased => ResolvePriorityBased(recommendations),
            ConflictResolutionMode.MostConservative => ResolveMostConservative(recommendations),
            ConflictResolutionMode.MostAggressive => ResolveMostAggressive(recommendations),
            ConflictResolutionMode.HighestConfidence => ResolveHighestConfidence(recommendations),
            ConflictResolutionMode.Unanimous => ResolveUnanimous(data, recommendations),
            _ => ResolveWeightedVoting(weightedVotes)
        };
    }

    private static (StorageTier tier, double confidence, string reasoning) ResolveMajorityVoting(
        Dictionary<string, TierRecommendation> recommendations)
    {
        var votes = recommendations.Values
            .GroupBy(r => r.RecommendedTier)
            .OrderByDescending(g => g.Count())
            .First();

        var winner = votes.Key;
        var confidence = votes.Average(r => r.Confidence);
        var voteCount = votes.Count();

        return (winner, confidence,
            $"Majority voting: {winner} received {voteCount}/{recommendations.Count} votes");
    }

    private static (StorageTier tier, double confidence, string reasoning) ResolveWeightedVoting(
        Dictionary<StorageTier, double> weightedVotes)
    {
        if (weightedVotes.Count == 0)
            return (StorageTier.Hot, 0, "No votes recorded");

        var winner = weightedVotes.OrderByDescending(kvp => kvp.Value).First();
        var totalWeight = weightedVotes.Values.Sum();
        var confidence = totalWeight > 0 ? winner.Value / totalWeight : 0;

        return (winner.Key, confidence,
            $"Weighted voting: {winner.Key} with {winner.Value:F2}/{totalWeight:F2} weighted votes");
    }

    private (StorageTier tier, double confidence, string reasoning) ResolvePriorityBased(
        Dictionary<string, TierRecommendation> recommendations)
    {
        var highestPriority = _strategies
            .Where(kvp => recommendations.ContainsKey(kvp.Key))
            .OrderByDescending(kvp => kvp.Value.Priority)
            .First();

        var recommendation = recommendations[highestPriority.Key];

        return (recommendation.RecommendedTier, recommendation.Confidence,
            $"Priority-based: Using {highestPriority.Key} (priority {highestPriority.Value.Priority})");
    }

    private static (StorageTier tier, double confidence, string reasoning) ResolveMostConservative(
        Dictionary<string, TierRecommendation> recommendations)
    {
        var mostConservative = recommendations.Values
            .OrderByDescending(r => r.RecommendedTier)
            .First();

        var agreeing = recommendations.Values.Count(r => r.RecommendedTier == mostConservative.RecommendedTier);

        return (mostConservative.RecommendedTier, mostConservative.Confidence * (agreeing / (double)recommendations.Count),
            $"Most conservative: {mostConservative.RecommendedTier} (coldest recommendation)");
    }

    private static (StorageTier tier, double confidence, string reasoning) ResolveMostAggressive(
        Dictionary<string, TierRecommendation> recommendations)
    {
        var mostAggressive = recommendations.Values
            .OrderBy(r => r.RecommendedTier)
            .First();

        var agreeing = recommendations.Values.Count(r => r.RecommendedTier == mostAggressive.RecommendedTier);

        return (mostAggressive.RecommendedTier, mostAggressive.Confidence * (agreeing / (double)recommendations.Count),
            $"Most aggressive: {mostAggressive.RecommendedTier} (hottest recommendation)");
    }

    private static (StorageTier tier, double confidence, string reasoning) ResolveHighestConfidence(
        Dictionary<string, TierRecommendation> recommendations)
    {
        var highestConfidence = recommendations
            .OrderByDescending(kvp => kvp.Value.Confidence)
            .First();

        return (highestConfidence.Value.RecommendedTier, highestConfidence.Value.Confidence,
            $"Highest confidence: {highestConfidence.Key} recommends {highestConfidence.Value.RecommendedTier} " +
            $"with {highestConfidence.Value.Confidence:P0} confidence");
    }

    private static (StorageTier tier, double confidence, string reasoning) ResolveUnanimous(
        DataObject data,
        Dictionary<string, TierRecommendation> recommendations)
    {
        var distinctTiers = recommendations.Values.Select(r => r.RecommendedTier).Distinct().ToList();

        if (distinctTiers.Count == 1)
        {
            var tier = distinctTiers[0];
            var avgConfidence = recommendations.Values.Average(r => r.Confidence);

            return (tier, avgConfidence,
                $"Unanimous agreement: All {recommendations.Count} strategies recommend {tier}");
        }

        return (data.CurrentTier, 0.5,
            $"No unanimous agreement: {distinctTiers.Count} different recommendations. Keeping current tier.");
    }
}
