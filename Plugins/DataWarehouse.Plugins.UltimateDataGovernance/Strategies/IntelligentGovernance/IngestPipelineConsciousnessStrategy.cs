using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Consciousness;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataGovernance.Strategies.IntelligentGovernance;

/// <summary>
/// Thread-safe store for consciousness scores, supporting retrieval by grade, action,
/// threshold, and aggregate statistics computation.
/// </summary>
/// <remarks>
/// Scores are stored in a ConcurrentDictionary keyed by object ID. The store also
/// adds consciousness metadata tags to the object's metadata dictionary for pipeline
/// integration (consciousness:score, consciousness:grade, consciousness:action).
/// </remarks>
public sealed class ConsciousnessScoreStore
{
    private readonly BoundedDictionary<string, ConsciousnessScore> _scores = new BoundedDictionary<string, ConsciousnessScore>(1000);

    /// <summary>
    /// Gets the number of scores currently stored.
    /// </summary>
    public int Count => _scores.Count;

    /// <summary>
    /// Stores a consciousness score, replacing any existing score for the same object.
    /// </summary>
    /// <param name="score">The consciousness score to store.</param>
    public void StoreScore(ConsciousnessScore score)
    {
        ArgumentNullException.ThrowIfNull(score);
        _scores[score.ObjectId] = score;
    }

    /// <summary>
    /// Gets the consciousness score for a specific object.
    /// </summary>
    /// <param name="objectId">The object identifier to look up.</param>
    /// <returns>The consciousness score, or null if not found.</returns>
    public ConsciousnessScore? GetScore(string objectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        return _scores.TryGetValue(objectId, out var score) ? score : null;
    }

    /// <summary>
    /// Gets all scores with a specific consciousness grade.
    /// </summary>
    /// <param name="grade">The grade to filter by.</param>
    /// <returns>A read-only list of matching scores.</returns>
    public IReadOnlyList<ConsciousnessScore> GetScoresByGrade(ConsciousnessGrade grade)
    {
        return _scores.Values.Where(s => s.Grade == grade).ToList().AsReadOnly();
    }

    /// <summary>
    /// Gets all scores with a specific recommended action.
    /// </summary>
    /// <param name="action">The action to filter by.</param>
    /// <returns>A read-only list of matching scores.</returns>
    public IReadOnlyList<ConsciousnessScore> GetScoresByAction(ConsciousnessAction action)
    {
        return _scores.Values.Where(s => s.RecommendedAction == action).ToList().AsReadOnly();
    }

    /// <summary>
    /// Gets all scores below a specified threshold.
    /// </summary>
    /// <param name="threshold">The composite score threshold (exclusive upper bound).</param>
    /// <returns>A read-only list of scores below the threshold.</returns>
    public IReadOnlyList<ConsciousnessScore> GetScoresBelow(double threshold)
    {
        return _scores.Values.Where(s => s.CompositeScore < threshold).ToList().AsReadOnly();
    }

    /// <summary>
    /// Gets all scores above a specified threshold.
    /// </summary>
    /// <param name="threshold">The composite score threshold (exclusive lower bound).</param>
    /// <returns>A read-only list of scores above the threshold.</returns>
    public IReadOnlyList<ConsciousnessScore> GetScoresAbove(double threshold)
    {
        return _scores.Values.Where(s => s.CompositeScore > threshold).ToList().AsReadOnly();
    }

    /// <summary>
    /// Gets all stored consciousness scores.
    /// </summary>
    /// <returns>A read-only list of all scores.</returns>
    public IReadOnlyList<ConsciousnessScore> GetAllScores()
    {
        return _scores.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// Removes the consciousness score for a specific object.
    /// </summary>
    /// <param name="objectId">The object identifier to remove.</param>
    /// <returns>True if the score was found and removed; false otherwise.</returns>
    public bool RemoveScore(string objectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        return _scores.TryRemove(objectId, out _);
    }

    /// <summary>
    /// Computes aggregate statistics across all stored consciousness scores.
    /// </summary>
    /// <returns>A <see cref="ConsciousnessStatistics"/> record with aggregate metrics.</returns>
    public ConsciousnessStatistics GetStatistics()
    {
        var allScores = _scores.Values.ToList();

        if (allScores.Count == 0)
        {
            return new ConsciousnessStatistics(
                TotalScored: 0,
                AverageScore: 0.0,
                MedianScore: 0.0,
                ByGrade: new Dictionary<ConsciousnessGrade, int>(),
                ByAction: new Dictionary<ConsciousnessAction, int>(),
                DarkDataCount: 0,
                ComputedAt: DateTime.UtcNow);
        }

        double average = allScores.Average(s => s.CompositeScore);

        // Compute median
        var sorted = allScores.OrderBy(s => s.CompositeScore).ToList();
        double median;
        int mid = sorted.Count / 2;
        if (sorted.Count % 2 == 0)
            median = (sorted[mid - 1].CompositeScore + sorted[mid].CompositeScore) / 2.0;
        else
            median = sorted[mid].CompositeScore;

        // Count by grade
        var byGrade = new Dictionary<ConsciousnessGrade, int>();
        foreach (var grade in Enum.GetValues<ConsciousnessGrade>())
        {
            int count = allScores.Count(s => s.Grade == grade);
            if (count > 0)
                byGrade[grade] = count;
        }

        // Count by action
        var byAction = new Dictionary<ConsciousnessAction, int>();
        foreach (var action in Enum.GetValues<ConsciousnessAction>())
        {
            int count = allScores.Count(s => s.RecommendedAction == action);
            if (count > 0)
                byAction[action] = count;
        }

        // Dark data: composite score < 25
        int darkDataCount = allScores.Count(s => s.CompositeScore < 25.0);

        return new ConsciousnessStatistics(
            TotalScored: allScores.Count,
            AverageScore: average,
            MedianScore: median,
            ByGrade: byGrade,
            ByAction: byAction,
            DarkDataCount: darkDataCount,
            ComputedAt: DateTime.UtcNow);
    }
}

/// <summary>
/// Aggregate statistics computed from all stored consciousness scores.
/// </summary>
/// <param name="TotalScored">Total number of objects that have been scored.</param>
/// <param name="AverageScore">Mean composite consciousness score across all objects.</param>
/// <param name="MedianScore">Median composite consciousness score.</param>
/// <param name="ByGrade">Count of objects in each consciousness grade.</param>
/// <param name="ByAction">Count of objects for each recommended action.</param>
/// <param name="DarkDataCount">Number of objects with composite score below 25 (dark data).</param>
/// <param name="ComputedAt">UTC timestamp when these statistics were computed.</param>
public sealed record ConsciousnessStatistics(
    int TotalScored,
    double AverageScore,
    double MedianScore,
    Dictionary<ConsciousnessGrade, int> ByGrade,
    Dictionary<ConsciousnessAction, int> ByAction,
    int DarkDataCount,
    DateTime ComputedAt);

/// <summary>
/// Pipeline integration strategy that automatically scores every data object on ingestion.
/// Hooks into the WRITE PIPELINE step 3 (DATA GOVERNANCE) to score objects as they flow
/// through the ingest pipeline, storing results and publishing message bus events for
/// downstream consumers (auto-archive, auto-purge, dashboards).
/// </summary>
/// <remarks>
/// This strategy is designed to slot into the write pipeline alongside UltimateDataQuality
/// and UltimateDataGovernance. The kernel's pipeline orchestrator calls ScoreOnIngestAsync
/// during ingest, which:
///   1. Scores the object via ConsciousnessScoringEngine
///   2. Stores the result in ConsciousnessScoreStore
///   3. Mutates the metadata dictionary with consciousness tags
///   4. Publishes message bus events for downstream consumers
///   5. Publishes purge/archive recommendations if score falls below thresholds
/// </remarks>
public sealed class IngestPipelineConsciousnessStrategy : ConsciousnessStrategyBase
{
    private readonly ConsciousnessScoringEngine _engine;
    private readonly ConsciousnessScoreStore _store;
    private readonly ConsciousnessScoringConfig _config;

    /// <summary>
    /// Message bus instance for publishing consciousness events.
    /// Nullable for unit test contexts where the bus is not wired.
    /// Shadows <see cref="StrategyBase.MessageBus"/> (protected) to expose it publicly for injection.
    /// </summary>
    public new IMessageBus? MessageBus
    {
        get => base.MessageBus;
        set => base.ConfigureIntelligence(value);
    }

    /// <inheritdoc />
    public override string StrategyId => "consciousness-ingest-pipeline";

    /// <inheritdoc />
    public override string DisplayName => "Ingest Pipeline Consciousness Strategy";

    /// <inheritdoc />
    public override ConsciousnessCategory Category => ConsciousnessCategory.PipelineIntegration;

    /// <inheritdoc />
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true,
        SupportsBatch: true,
        SupportsRealTime: true,
        SupportsStreaming: true,
        SupportsRetroactive: true);

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Pipeline integration strategy that automatically scores every data object on ingestion, " +
        "storing results and publishing message bus events for downstream consumers " +
        "(auto-archive, auto-purge, dashboards).";

    /// <inheritdoc />
    public override string[] Tags => ["consciousness", "pipeline", "ingest", "auto-score", "integration"];

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestPipelineConsciousnessStrategy"/> class.
    /// </summary>
    /// <param name="engine">The consciousness scoring engine to use.</param>
    /// <param name="store">The score store for persisting results.</param>
    /// <param name="config">Optional scoring configuration for thresholds. Uses defaults if null.</param>
    public IngestPipelineConsciousnessStrategy(
        ConsciousnessScoringEngine engine,
        ConsciousnessScoreStore store,
        ConsciousnessScoringConfig? config = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _config = config ?? new ConsciousnessScoringConfig();
    }

    /// <summary>
    /// Scores a data object during the ingest pipeline, storing the result,
    /// annotating metadata, and publishing message bus events.
    /// </summary>
    /// <param name="objectId">Unique identifier of the data object being ingested.</param>
    /// <param name="data">Raw data bytes of the object.</param>
    /// <param name="metadata">
    /// Metadata dictionary for the object. This dictionary is mutated in-place to add
    /// consciousness:score, consciousness:grade, and consciousness:action tags for
    /// pipeline efficiency.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The computed <see cref="ConsciousnessScore"/>.</returns>
    public async Task<ConsciousnessScore> ScoreOnIngestAsync(
        string objectId,
        byte[] data,
        Dictionary<string, object> metadata,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(metadata);
        ct.ThrowIfCancellationRequested();

        IncrementCounter("ingest_score_invocations");

        // Step 1: Score via the consciousness engine
        var score = await _engine.ScoreAsync(objectId, data, metadata, ct).ConfigureAwait(false);

        // Step 2: Store result
        _store.StoreScore(score);

        // Step 3: Mutate metadata with consciousness tags (in-place for pipeline efficiency)
        metadata["consciousness:score"] = score.CompositeScore;
        metadata["consciousness:grade"] = score.Grade.ToString();
        metadata["consciousness:action"] = score.RecommendedAction.ToString();

        // Check for quarantine override from engine metadata
        if (score.Metadata != null
            && score.Metadata.TryGetValue("quarantine_override", out var overrideVal)
            && overrideVal is true)
        {
            metadata["consciousness:action"] = ConsciousnessAction.Quarantine.ToString();
        }

        // Step 4: Publish scoring event via message bus
        await PublishEventAsync("consciousness.score.computed", new Dictionary<string, object>
        {
            ["objectId"] = objectId,
            ["compositeScore"] = score.CompositeScore,
            ["grade"] = score.Grade.ToString(),
            ["action"] = score.RecommendedAction.ToString(),
            ["valueScore"] = score.Value.OverallScore,
            ["liabilityScore"] = score.Liability.OverallScore,
            ["scoredAt"] = score.ScoredAt.ToString("O")
        }, ct).ConfigureAwait(false);

        // Step 5: Publish purge recommendation if score below purge threshold
        if (score.CompositeScore < _config.PurgeThreshold)
        {
            IncrementCounter("purge_recommendations");
            await PublishEventAsync("consciousness.purge.recommended", new Dictionary<string, object>
            {
                ["objectId"] = objectId,
                ["compositeScore"] = score.CompositeScore,
                ["grade"] = score.Grade.ToString(),
                ["reason"] = $"Composite score {score.CompositeScore:F1} below purge threshold {_config.PurgeThreshold:F1}"
            }, ct).ConfigureAwait(false);
        }
        // Step 6: Publish archive recommendation if score below archive threshold
        else if (score.CompositeScore < _config.ArchiveThreshold)
        {
            IncrementCounter("archive_recommendations");
            await PublishEventAsync("consciousness.archive.recommended", new Dictionary<string, object>
            {
                ["objectId"] = objectId,
                ["compositeScore"] = score.CompositeScore,
                ["grade"] = score.Grade.ToString(),
                ["reason"] = $"Composite score {score.CompositeScore:F1} below archive threshold {_config.ArchiveThreshold:F1}"
            }, ct).ConfigureAwait(false);
        }

        return score;
    }

    /// <summary>
    /// Publishes an event to the message bus using the null-conditional pattern.
    /// The message bus may not be wired in unit test contexts.
    /// </summary>
    private async Task PublishEventAsync(
        string topic,
        Dictionary<string, object> payload,
        CancellationToken ct)
    {
        if (MessageBus != null)
        {
            var message = new PluginMessage
            {
                Type = topic,
                Payload = payload,
                Source = StrategyId
            };
            await MessageBus.PublishAsync(topic, message, ct).ConfigureAwait(false);
        }
    }
}
