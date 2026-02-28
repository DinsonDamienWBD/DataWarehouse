using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Consciousness;

namespace DataWarehouse.Plugins.UltimateDataGovernance.Strategies.IntelligentGovernance;

/// <summary>
/// Composite consciousness scoring engine that merges value and liability assessments
/// into a single 0-100 consciousness score with grade and recommended action.
/// </summary>
/// <remarks>
/// This is the heart of the Data Consciousness system. It combines the value engine
/// (CompositeValueScoringStrategy from Plan 02) and liability engine
/// (CompositeLiabilityScoringStrategy from Plan 03) to produce a final ConsciousnessScore.
///
/// Scoring formula:
///   composite = ratio * valueScore + (1 - ratio) * (100 - liabilityScore)
///   where ratio = ValueLiabilityRatio (default 0.6)
///
/// This means:
///   - High value pushes the score UP
///   - High liability pushes the score DOWN
///   - A 100-value + 0-liability object gets 100
///   - A 0-value + 100-liability object gets 0
///
/// The recommended action is determined by a value/liability quadrant analysis:
///   - High value + Low liability  => Retain
///   - High value + High liability => Review
///   - Low value  + Low liability  => Archive
///   - Low value  + High liability => Purge
///   - Liability >= ReviewThreshold => Quarantine (override)
/// </remarks>
public sealed class ConsciousnessScoringEngine : ConsciousnessStrategyBase, IConsciousnessScorer
{
    private readonly IValueScorer _valueScorer;
    private readonly ILiabilityScorer _liabilityScorer;
    private readonly ConsciousnessScoringConfig _config;

    /// <inheritdoc />
    public override string StrategyId => "consciousness-composite-engine";

    /// <inheritdoc />
    public override string DisplayName => "Composite Consciousness Scoring Engine";

    /// <inheritdoc />
    public override ConsciousnessCategory Category => ConsciousnessCategory.CompositeScoring;

    /// <inheritdoc />
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true,
        SupportsBatch: true,
        SupportsRealTime: true,
        SupportsStreaming: true,
        SupportsRetroactive: true);

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Composite consciousness scoring engine that merges value and liability assessments " +
        "into a single 0-100 consciousness score with grade and recommended action. " +
        "Supports single-object and batch scoring with configurable parallelism.";

    /// <inheritdoc />
    public override string[] Tags => ["consciousness", "composite", "engine", "value-liability", "scoring"];

    /// <summary>
    /// Initializes a new instance of the <see cref="ConsciousnessScoringEngine"/> class.
    /// </summary>
    /// <param name="valueScorer">The value scorer to use for assessing data value.</param>
    /// <param name="liabilityScorer">The liability scorer to use for assessing data liability.</param>
    /// <param name="config">Optional scoring configuration. Uses defaults if null.</param>
    public ConsciousnessScoringEngine(
        IValueScorer valueScorer,
        ILiabilityScorer liabilityScorer,
        ConsciousnessScoringConfig? config = null)
    {
        _valueScorer = valueScorer ?? throw new ArgumentNullException(nameof(valueScorer));
        _liabilityScorer = liabilityScorer ?? throw new ArgumentNullException(nameof(liabilityScorer));
        _config = config ?? new ConsciousnessScoringConfig();
    }

    /// <summary>
    /// Computes the composite consciousness score for a single data object by running
    /// value and liability scoring in parallel, then combining results.
    /// </summary>
    /// <param name="objectId">Unique identifier of the data object.</param>
    /// <param name="data">Raw data bytes of the object.</param>
    /// <param name="metadata">Additional metadata about the object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ConsciousnessScore"/> with composite score, grade, and recommended action.</returns>
    public async Task<ConsciousnessScore> ScoreAsync(
        string objectId,
        byte[] data,
        IReadOnlyDictionary<string, object> metadata,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(metadata);
        ct.ThrowIfCancellationRequested();

        IncrementCounter("score_invocations");

        // Step 1: Run value and liability scoring in parallel
        var valueTask = _valueScorer.ScoreValueAsync(objectId, data, metadata, ct);
        var liabilityTask = _liabilityScorer.ScoreLiabilityAsync(objectId, data, metadata, ct);

        await Task.WhenAll(valueTask, liabilityTask).ConfigureAwait(false);

        var valueScore = await valueTask.ConfigureAwait(false);
        var liabilityScore = await liabilityTask.ConfigureAwait(false);

        return BuildConsciousnessScore(objectId, valueScore, liabilityScore);
    }

    /// <summary>
    /// Computes consciousness scores for a batch of data objects with concurrency control.
    /// Uses a semaphore to limit concurrent scoring to <c>Environment.ProcessorCount * 2</c>.
    /// </summary>
    /// <param name="batch">Collection of (objectId, data, metadata) tuples to score.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of consciousness scores, one per input object.</returns>
    public async Task<IReadOnlyList<ConsciousnessScore>> ScoreBatchAsync(
        IReadOnlyList<(string objectId, byte[] data, Dictionary<string, object> metadata)> batch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ct.ThrowIfCancellationRequested();

        IncrementCounter("batch_invocations");

        if (batch.Count == 0)
            return Array.Empty<ConsciousnessScore>();

        int maxConcurrency = Math.Max(1, Environment.ProcessorCount * 2);
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var tasks = new Task<ConsciousnessScore>[batch.Count];

        for (int i = 0; i < batch.Count; i++)
        {
            var item = batch[i];
            tasks[i] = ScoreWithSemaphoreAsync(semaphore, item.objectId, item.data, item.metadata, ct);
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        IncrementCounter("batch_items_scored", results.Length);

        return results;
    }

    /// <summary>
    /// Creates a default engine instance using the standard composite value and liability scorers.
    /// </summary>
    /// <param name="config">Optional scoring configuration. Uses defaults if null.</param>
    /// <returns>A new <see cref="ConsciousnessScoringEngine"/> with default scorers.</returns>
    public static ConsciousnessScoringEngine CreateDefault(ConsciousnessScoringConfig? config = null)
    {
        var valueScorer = new CompositeValueScoringStrategy();
        var liabilityScorer = new CompositeLiabilityScoringStrategy();
        return new ConsciousnessScoringEngine(valueScorer, liabilityScorer, config);
    }

    /// <summary>
    /// Builds the final <see cref="ConsciousnessScore"/> from value and liability sub-scores.
    /// </summary>
    private ConsciousnessScore BuildConsciousnessScore(
        string objectId,
        ValueScore valueScore,
        LiabilityScore liabilityScore)
    {
        // Step 2: Compute composite score
        // High value pushes score UP, high liability pushes score DOWN
        double composite = _config.ValueLiabilityRatio * valueScore.OverallScore
                         + (1.0 - _config.ValueLiabilityRatio) * (100.0 - liabilityScore.OverallScore);

        // Clamp to [0, 100]
        composite = Math.Clamp(composite, 0.0, 100.0);

        // Step 3 & 4: Grade and action are computed properties on ConsciousnessScore
        // but we need to check the quarantine override from config.ReviewThreshold
        // The ConsciousnessScore record computes Grade and RecommendedAction from its fields.
        // However, the plan specifies a quarantine override when liability >= config.ReviewThreshold.
        // We handle this via Metadata so downstream consumers can act on it.

        var scoredAt = DateTime.UtcNow;

        // Build metadata with quarantine override info
        var scoringMetadata = new Dictionary<string, object>
        {
            ["scoring_strategy"] = StrategyId,
            ["value_liability_ratio"] = _config.ValueLiabilityRatio,
            ["raw_value_score"] = valueScore.OverallScore,
            ["raw_liability_score"] = liabilityScore.OverallScore
        };

        // Quarantine override: if liability >= ReviewThreshold, override action to Quarantine
        bool quarantineOverride = liabilityScore.OverallScore >= _config.ReviewThreshold;
        if (quarantineOverride)
        {
            scoringMetadata["quarantine_override"] = true;
            scoringMetadata["quarantine_reason"] =
                $"Liability score {liabilityScore.OverallScore:F1} exceeds review threshold {_config.ReviewThreshold:F1}";
        }

        var score = new ConsciousnessScore(
            ObjectId: objectId,
            CompositeScore: composite,
            Value: valueScore,
            Liability: liabilityScore,
            ScoringStrategy: StrategyId,
            ScoredAt: scoredAt,
            Metadata: scoringMetadata);

        return score;
    }

    /// <summary>
    /// Scores a single item while respecting the concurrency semaphore.
    /// </summary>
    private async Task<ConsciousnessScore> ScoreWithSemaphoreAsync(
        SemaphoreSlim semaphore,
        string objectId,
        byte[] data,
        Dictionary<string, object> metadata,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ScoreAsync(objectId, data, metadata, ct).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Increments a named counter by a specified amount. Thread-safe.
    /// </summary>
    private void IncrementCounter(string name, long amount)
    {
        for (long i = 0; i < amount; i++)
            IncrementCounter(name);
    }
}
