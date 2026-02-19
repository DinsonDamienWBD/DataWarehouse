using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Consciousness;

namespace DataWarehouse.Plugins.UltimateDataCatalog.Strategies.AssetDiscovery;

#region Supporting Types

/// <summary>
/// Result of a retroactive scoring or rescoring operation, containing summary statistics
/// including score change distribution and grade distribution.
/// </summary>
/// <param name="TotalRescored">Total number of objects that were scored or rescored.</param>
/// <param name="ScoreChanged">Number of objects whose score actually changed.</param>
/// <param name="ScoreImproved">Number of objects whose score increased.</param>
/// <param name="ScoreDegraded">Number of objects whose score decreased.</param>
/// <param name="GradeDistribution">Distribution of objects across consciousness grades after rescoring.</param>
/// <param name="Duration">Wall-clock time the operation took to complete.</param>
public sealed record RescoringResult(
    int TotalRescored,
    int ScoreChanged,
    int ScoreImproved,
    int ScoreDegraded,
    Dictionary<ConsciousnessGrade, int> GradeDistribution,
    TimeSpan Duration);

/// <summary>
/// Configuration for retroactive scoring operations, controlling batch size, change thresholds,
/// and decay recalculation parameters.
/// </summary>
/// <param name="BatchSize">Number of objects to process in a single batch. Default: 500.</param>
/// <param name="MinScoreChangeTrigger">Minimum score change (absolute) required to persist an update. Default: 5.0.</param>
/// <param name="RecalculateDecay">Whether to include time-based decay in recalculations. Default: true.</param>
/// <param name="DecayRecalcIntervalDays">Minimum number of days between decay recalculations for an object. Default: 30.</param>
public sealed record RescoringConfig(
    int BatchSize = 500,
    double MinScoreChangeTrigger = 5.0,
    bool RecalculateDecay = true,
    int DecayRecalcIntervalDays = 30);

#endregion

#region Strategy 1: RetroactiveBatchScoringStrategy

/// <summary>
/// Scores large volumes of existing data objects that have never been scored by the consciousness
/// scoring system. Processes objects in configurable batches with progress tracking and concurrency control.
/// </summary>
/// <remarks>
/// Uses <see cref="SemaphoreSlim"/> to limit concurrent scoring operations to
/// <c>Environment.ProcessorCount * 2</c>, preventing scorer overload during batch processing.
/// Progress is reported via an optional callback after each object is scored.
/// </remarks>
public sealed class RetroactiveBatchScoringStrategy : ConsciousnessStrategyBase
{
    /// <inheritdoc />
    public override string StrategyId => "retroactive-batch-scoring";

    /// <inheritdoc />
    public override string DisplayName => "Retroactive Batch Scorer";

    /// <inheritdoc />
    public override ConsciousnessCategory Category => ConsciousnessCategory.DarkDataDiscovery;

    /// <inheritdoc />
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true,
        SupportsBatch: true,
        SupportsRetroactive: true);

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Scores large volumes of existing data objects that have never been scored. Processes in configurable " +
        "batches with progress tracking and concurrency-controlled parallel scoring.";

    /// <inheritdoc />
    public override string[] Tags => ["retroactive", "batch", "scoring", "bulk", "progress", "concurrency"];

    /// <summary>
    /// Scores a batch of objects that have never been consciousness-scored.
    /// </summary>
    /// <param name="objects">Collection of (objectId, data, metadata) tuples to score.</param>
    /// <param name="scorer">The consciousness scorer to use.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="progressCallback">Optional callback invoked after each object is scored: (scored, total).</param>
    /// <returns>A rescoring result with summary statistics and grade distribution.</returns>
    public async Task<RescoringResult> ScoreBatchAsync(
        IReadOnlyList<(string objectId, byte[] data, Dictionary<string, object> metadata)> objects,
        IConsciousnessScorer scorer,
        CancellationToken ct = default,
        Action<int, int>? progressCallback = null)
    {
        IncrementCounter("batch_score_invocations");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var maxConcurrency = Math.Max(1, Environment.ProcessorCount * 2);
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var totalScored = 0;
        var gradeDistribution = new ConcurrentDictionary<ConsciousnessGrade, int>();

        // Initialize grade distribution
        foreach (var grade in Enum.GetValues<ConsciousnessGrade>())
        {
            gradeDistribution[grade] = 0;
        }

        var tasks = new List<Task>();

        foreach (var (objectId, data, metadata) in objects)
        {
            ct.ThrowIfCancellationRequested();

            await semaphore.WaitAsync(ct).ConfigureAwait(false);

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var score = await scorer.ScoreAsync(objectId, data, metadata, ct).ConfigureAwait(false);
                    gradeDistribution.AddOrUpdate(score.Grade, 1, (_, count) => count + 1);

                    var scored = Interlocked.Increment(ref totalScored);
                    IncrementCounter("objects_scored");

                    progressCallback?.Invoke(scored, objects.Count);
                }
                catch (Exception)
                {
                    IncrementCounter("scoring_failures");
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        stopwatch.Stop();

        return new RescoringResult(
            TotalRescored: totalScored,
            ScoreChanged: totalScored, // All are new scores
            ScoreImproved: totalScored, // All go from unscored to scored
            ScoreDegraded: 0,
            GradeDistribution: new Dictionary<ConsciousnessGrade, int>(gradeDistribution),
            Duration: stopwatch.Elapsed);
    }
}

#endregion

#region Strategy 2: IncrementalRescoringStrategy

/// <summary>
/// Re-scores objects whose metadata has changed since their last consciousness scoring. Uses metadata
/// hash comparison to efficiently determine which objects need expensive full rescoring.
/// </summary>
/// <remarks>
/// Tracks "consciousness:scored_at" metadata timestamp and computes metadata hashes to determine
/// if rescoring is necessary. Only rescores when: metadata changed (hash comparison), access patterns
/// changed, lineage changed, or classification changed.
/// </remarks>
public sealed class IncrementalRescoringStrategy : ConsciousnessStrategyBase
{
    /// <inheritdoc />
    public override string StrategyId => "retroactive-incremental-rescore";

    /// <inheritdoc />
    public override string DisplayName => "Incremental Rescorer";

    /// <inheritdoc />
    public override ConsciousnessCategory Category => ConsciousnessCategory.DarkDataDiscovery;

    /// <inheritdoc />
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true,
        SupportsBatch: true,
        SupportsRetroactive: true);

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Re-scores objects whose metadata has changed since last scoring. Uses metadata hash comparison " +
        "to efficiently determine which objects need expensive full rescoring.";

    /// <inheritdoc />
    public override string[] Tags => ["retroactive", "incremental", "rescore", "metadata-hash", "change-detection"];

    /// <summary>
    /// Keys in metadata that trigger rescoring when changed.
    /// </summary>
    private static readonly string[] RescoringTriggerKeys = new[]
    {
        "classification", "access_count", "last_accessed", "access_trend",
        "lineage_source", "upstream_object_ids", "owner", "owner_principal_id",
        "governance_zone", "retention_policy", "pii_detected", "phi_detected", "pci_detected"
    };

    /// <summary>
    /// Re-scores objects whose metadata has changed since they were last scored.
    /// </summary>
    /// <param name="objects">Collection of (objectId, currentMetadata, previousMetadata) tuples.</param>
    /// <param name="scorer">The consciousness scorer to use.</param>
    /// <param name="config">Rescoring configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A rescoring result with statistics on which scores changed.</returns>
    public async Task<RescoringResult> RescoreChangedAsync(
        IReadOnlyList<(string objectId, Dictionary<string, object> currentMetadata, Dictionary<string, object> previousMetadata)> objects,
        IConsciousnessScorer scorer,
        RescoringConfig? config = null,
        CancellationToken ct = default)
    {
        IncrementCounter("incremental_rescore_invocations");
        config ??= new RescoringConfig();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var totalRescored = 0;
        var scoreChanged = 0;
        var scoreImproved = 0;
        var scoreDegraded = 0;
        var gradeDistribution = new Dictionary<ConsciousnessGrade, int>();

        foreach (var grade in Enum.GetValues<ConsciousnessGrade>())
        {
            gradeDistribution[grade] = 0;
        }

        foreach (var (objectId, currentMetadata, previousMetadata) in objects)
        {
            ct.ThrowIfCancellationRequested();

            // Check if metadata actually changed using hash comparison
            if (!HasMetadataChanged(currentMetadata, previousMetadata))
            {
                IncrementCounter("rescore_skipped_unchanged");
                continue;
            }

            try
            {
                // Get old score if available
                var oldCompositeScore = previousMetadata.TryGetValue("consciousness:score", out var oldScoreObj) &&
                    oldScoreObj is double oldScore ? oldScore : 0.0;

                // Rescore with current metadata
                var data = Array.Empty<byte>(); // Rescoring uses metadata only
                var newScore = await scorer.ScoreAsync(objectId, data, currentMetadata, ct).ConfigureAwait(false);

                totalRescored++;
                gradeDistribution[newScore.Grade]++;

                var scoreDelta = newScore.CompositeScore - oldCompositeScore;
                if (Math.Abs(scoreDelta) >= config.MinScoreChangeTrigger)
                {
                    scoreChanged++;
                    if (scoreDelta > 0) scoreImproved++;
                    else scoreDegraded++;
                }

                IncrementCounter("objects_rescored");
            }
            catch (Exception)
            {
                IncrementCounter("rescoring_failures");
            }
        }

        stopwatch.Stop();

        return new RescoringResult(
            TotalRescored: totalRescored,
            ScoreChanged: scoreChanged,
            ScoreImproved: scoreImproved,
            ScoreDegraded: scoreDegraded,
            GradeDistribution: gradeDistribution,
            Duration: stopwatch.Elapsed);
    }

    /// <summary>
    /// Determines whether relevant metadata keys have changed between two metadata snapshots.
    /// Uses hash comparison of trigger keys for efficiency.
    /// </summary>
    private static bool HasMetadataChanged(
        Dictionary<string, object> currentMetadata,
        Dictionary<string, object> previousMetadata)
    {
        var currentHash = ComputeMetadataHash(currentMetadata);
        var previousHash = ComputeMetadataHash(previousMetadata);
        return !string.Equals(currentHash, previousHash, StringComparison.Ordinal);
    }

    /// <summary>
    /// Computes a hash of the scoring-relevant metadata keys for efficient change detection.
    /// </summary>
    private static string ComputeMetadataHash(Dictionary<string, object> metadata)
    {
        var sb = new StringBuilder();
        foreach (var key in RescoringTriggerKeys)
        {
            if (metadata.TryGetValue(key, out var value))
            {
                sb.Append(key).Append('=').Append(value?.ToString() ?? "null").Append(';');
            }
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}

#endregion

#region Strategy 3: ScoreDecayRecalculationStrategy

/// <summary>
/// Periodically recalculates consciousness scores to account for time-based decay. Freshness scores
/// decay as data ages, and access frequency scores decay as historical access becomes less relevant.
/// Publishes events when objects cross grade boundaries due to decay.
/// </summary>
/// <remarks>
/// Decay is applied to the existing composite score based on:
/// <list type="bullet">
///   <item><term>Age decay</term><description>Score decays as time since last scoring increases.</description></item>
///   <item><term>Access decay</term><description>Objects not accessed recently lose value score.</description></item>
///   <item><term>Freshness decay</term><description>Data that hasn't been updated loses freshness score.</description></item>
/// </list>
/// Only rescores if the projected decay would change the score by more than <see cref="RescoringConfig.MinScoreChangeTrigger"/>.
/// </remarks>
public sealed class ScoreDecayRecalculationStrategy : ConsciousnessStrategyBase
{
    private readonly ConcurrentBag<(string ObjectId, ConsciousnessGrade OldGrade, ConsciousnessGrade NewGrade, DateTime DecayedAt)> _gradeBoundaryEvents = new();

    /// <inheritdoc />
    public override string StrategyId => "retroactive-decay-recalc";

    /// <inheritdoc />
    public override string DisplayName => "Score Decay Recalculator";

    /// <inheritdoc />
    public override ConsciousnessCategory Category => ConsciousnessCategory.DarkDataDiscovery;

    /// <inheritdoc />
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true,
        SupportsBatch: true,
        SupportsRetroactive: true);

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Periodically recalculates consciousness scores accounting for time-based decay of freshness, " +
        "access frequency, and data age. Publishes events when objects cross grade boundaries.";

    /// <inheritdoc />
    public override string[] Tags => ["retroactive", "decay", "recalculation", "freshness", "time-based", "grade-boundary"];

    /// <summary>
    /// Gets all grade boundary crossing events recorded during decay recalculation.
    /// These represent objects whose consciousness grade changed (e.g., Aware to Dormant) due to score decay.
    /// </summary>
    public IReadOnlyCollection<(string ObjectId, ConsciousnessGrade OldGrade, ConsciousnessGrade NewGrade, DateTime DecayedAt)> GradeBoundaryEvents =>
        _gradeBoundaryEvents.ToArray();

    /// <summary>
    /// Recalculates scores for existing consciousness scores, applying time-based decay factors.
    /// Only rescores objects where decay would change the score by more than the configured threshold.
    /// </summary>
    /// <param name="existingScores">Existing consciousness scores to evaluate for decay.</param>
    /// <param name="scorer">The consciousness scorer to produce updated scores.</param>
    /// <param name="config">Rescoring configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A rescoring result with statistics on decay-driven score changes.</returns>
    public async Task<RescoringResult> RecalculateDecayAsync(
        IReadOnlyList<ConsciousnessScore> existingScores,
        IConsciousnessScorer scorer,
        RescoringConfig? config = null,
        CancellationToken ct = default)
    {
        IncrementCounter("decay_recalc_invocations");
        config ??= new RescoringConfig();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var totalRescored = 0;
        var scoreChanged = 0;
        var scoreImproved = 0;
        var scoreDegraded = 0;
        var gradeDistribution = new Dictionary<ConsciousnessGrade, int>();

        foreach (var grade in Enum.GetValues<ConsciousnessGrade>())
        {
            gradeDistribution[grade] = 0;
        }

        foreach (var existingScore in existingScores)
        {
            ct.ThrowIfCancellationRequested();

            // Check if enough time has passed since last scoring
            var daysSinceScored = (DateTime.UtcNow - existingScore.ScoredAt).TotalDays;
            if (daysSinceScored < config.DecayRecalcIntervalDays)
            {
                IncrementCounter("decay_skipped_too_recent");
                continue;
            }

            // Estimate decay impact before expensive rescore
            var estimatedDecay = EstimateDecay(existingScore, daysSinceScored);
            if (Math.Abs(estimatedDecay) < config.MinScoreChangeTrigger)
            {
                IncrementCounter("decay_skipped_below_threshold");
                continue;
            }

            try
            {
                // Rescore with updated metadata including decay context
                var metadata = new Dictionary<string, object>(existingScore.Metadata ?? new Dictionary<string, object>());
                metadata["consciousness:previous_score"] = existingScore.CompositeScore;
                metadata["consciousness:scored_at"] = existingScore.ScoredAt;
                metadata["consciousness:days_since_scored"] = daysSinceScored;
                metadata["consciousness:decay_triggered"] = true;

                var data = Array.Empty<byte>();
                var newScore = await scorer.ScoreAsync(existingScore.ObjectId, data, metadata, ct).ConfigureAwait(false);

                totalRescored++;
                gradeDistribution[newScore.Grade]++;

                var oldGrade = existingScore.Grade;
                var newGrade = newScore.Grade;
                var scoreDelta = newScore.CompositeScore - existingScore.CompositeScore;

                if (Math.Abs(scoreDelta) >= config.MinScoreChangeTrigger)
                {
                    scoreChanged++;
                    if (scoreDelta > 0) scoreImproved++;
                    else scoreDegraded++;
                }

                // Record grade boundary crossing events
                if (oldGrade != newGrade)
                {
                    _gradeBoundaryEvents.Add((existingScore.ObjectId, oldGrade, newGrade, DateTime.UtcNow));
                    IncrementCounter("grade_boundary_crossings");
                }

                IncrementCounter("objects_decay_rescored");
            }
            catch (Exception)
            {
                IncrementCounter("decay_rescoring_failures");
            }
        }

        stopwatch.Stop();

        return new RescoringResult(
            TotalRescored: totalRescored,
            ScoreChanged: scoreChanged,
            ScoreImproved: scoreImproved,
            ScoreDegraded: scoreDegraded,
            GradeDistribution: gradeDistribution,
            Duration: stopwatch.Elapsed);
    }

    /// <summary>
    /// Estimates the score decay for a given score based on time elapsed since last scoring.
    /// Uses a logarithmic decay curve to model diminishing impact over time.
    /// </summary>
    /// <param name="score">The existing consciousness score.</param>
    /// <param name="daysSinceScored">Number of days since the score was last computed.</param>
    /// <returns>The estimated score change (negative values indicate decay).</returns>
    private static double EstimateDecay(ConsciousnessScore score, double daysSinceScored)
    {
        // Logarithmic decay: faster initial decay, slower over time
        // Max decay of ~30 points after a year, ~15 after 6 months
        var decayFactor = Math.Log(1 + daysSinceScored / 30.0) * 5.0;

        // Higher-scored objects decay faster (more to lose)
        var scoreWeight = score.CompositeScore / 100.0;

        return -(decayFactor * scoreWeight);
    }
}

#endregion
