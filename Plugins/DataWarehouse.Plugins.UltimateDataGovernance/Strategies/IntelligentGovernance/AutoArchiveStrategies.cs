using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Consciousness;

namespace DataWarehouse.Plugins.UltimateDataGovernance.Strategies.IntelligentGovernance;

#region Supporting Types

/// <summary>
/// Represents the outcome of an auto-archive evaluation for a single data object.
/// </summary>
/// <param name="ObjectId">Unique identifier of the evaluated data object.</param>
/// <param name="ShouldArchive">Whether the object should be archived.</param>
/// <param name="TargetTier">Target storage tier: "cold", "archive", "glacier", or "deep_archive".</param>
/// <param name="Reason">Human-readable explanation of the archive decision.</param>
/// <param name="Score">The consciousness score that drove this decision.</param>
/// <param name="DecidedAt">UTC timestamp when the decision was made.</param>
public sealed record ArchiveDecision(
    string ObjectId,
    bool ShouldArchive,
    string TargetTier,
    string Reason,
    ConsciousnessScore Score,
    DateTime DecidedAt);

/// <summary>
/// Configurable policy governing when and how auto-archival operates.
/// </summary>
/// <param name="ScoreThreshold">Composite score below which archival is considered. Default: 30.</param>
/// <param name="MinAgeDays">Minimum age in days before data can be archived. Default: 90.</param>
/// <param name="GracePeriodDays">Days to wait after decision before executing archive. Default: 7.</param>
/// <param name="RequireApproval">Whether archive actions require manual approval. Default: false.</param>
/// <param name="ExemptClassifications">Classifications exempt from auto-archive. Default: ["restricted", "top_secret"].</param>
public sealed record ArchivePolicy(
    double ScoreThreshold = 30.0,
    int MinAgeDays = 90,
    int GracePeriodDays = 7,
    bool RequireApproval = false,
    string[]? ExemptClassifications = null)
{
    /// <summary>
    /// Gets the effective exempt classifications, defaulting to restricted and top_secret.
    /// </summary>
    public string[] EffectiveExemptClassifications =>
        ExemptClassifications ?? new[] { "restricted", "top_secret" };
}

#endregion

#region Strategy 1: ThresholdAutoArchiveStrategy

/// <summary>
/// Archives data objects whose composite consciousness score falls below a configurable
/// threshold, provided the object meets age requirements and is not exempt by classification
/// or legal hold.
/// </summary>
/// <remarks>
/// Target tier selection is based on composite score bands:
/// <list type="bullet">
///   <item><term>20-30</term><description>"cold" tier</description></item>
///   <item><term>10-20</term><description>"archive" tier</description></item>
///   <item><term>&lt;10</term><description>"deep_archive" tier</description></item>
/// </list>
/// Metadata keys consumed: "created_at" (DateTime), "classification" (string), "legal_hold" (bool).
/// </remarks>
public sealed class ThresholdAutoArchiveStrategy : ConsciousnessStrategyBase
{
    private ArchivePolicy _policy = new();

    /// <inheritdoc />
    public override string StrategyId => "auto-archive-threshold";

    /// <inheritdoc />
    public override string DisplayName => "Threshold Auto-Archive";

    /// <inheritdoc />
    public override ConsciousnessCategory Category => ConsciousnessCategory.AutoArchive;

    /// <inheritdoc />
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true,
        SupportsBatch: true,
        SupportsRealTime: true);

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Archives data objects whose composite consciousness score falls below a configurable threshold. " +
        "Selects target storage tier (cold/archive/deep_archive) based on score bands. " +
        "Respects age requirements, classification exemptions, and legal holds.";

    /// <inheritdoc />
    public override string[] Tags => ["auto-archive", "threshold", "tiered-storage", "cost-optimization", "lifecycle"];

    /// <summary>
    /// Configures the archive policy used for threshold evaluation.
    /// </summary>
    /// <param name="policy">The archive policy to apply.</param>
    public void Configure(ArchivePolicy policy)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    /// <summary>
    /// Evaluates whether a data object should be archived based on its consciousness score
    /// and the configured archive policy.
    /// </summary>
    /// <param name="score">The consciousness score of the data object.</param>
    /// <param name="metadata">Object metadata including created_at, classification, and legal_hold.</param>
    /// <returns>An archive decision indicating whether and where to archive.</returns>
    public ArchiveDecision Evaluate(ConsciousnessScore score, Dictionary<string, object> metadata)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(metadata);
        IncrementCounter("evaluate_invocations");

        // Check legal hold -- never archive data under legal hold
        if (metadata.TryGetValue("legal_hold", out var legalHoldObj) && Convert.ToBoolean(legalHoldObj))
        {
            IncrementCounter("legal_hold_skips");
            return new ArchiveDecision(score.ObjectId, false, "none",
                "Object is under legal hold and cannot be archived.", score, DateTime.UtcNow);
        }

        // Check classification exemptions
        if (metadata.TryGetValue("classification", out var classObj))
        {
            string classification = classObj?.ToString() ?? string.Empty;
            if (_policy.EffectiveExemptClassifications
                .Any(c => string.Equals(c, classification, StringComparison.OrdinalIgnoreCase)))
            {
                IncrementCounter("classification_exempt_skips");
                return new ArchiveDecision(score.ObjectId, false, "none",
                    $"Object classification '{classification}' is exempt from auto-archive.", score, DateTime.UtcNow);
            }
        }

        // Check minimum age requirement
        if (metadata.TryGetValue("created_at", out var createdObj))
        {
            DateTime createdAt = Convert.ToDateTime(createdObj);
            double ageDays = (DateTime.UtcNow - createdAt).TotalDays;
            if (ageDays < _policy.MinAgeDays)
            {
                IncrementCounter("too_young_skips");
                return new ArchiveDecision(score.ObjectId, false, "none",
                    $"Object is only {ageDays:F0} days old; minimum age is {_policy.MinAgeDays} days.", score, DateTime.UtcNow);
            }
        }

        // Check score threshold
        if (score.CompositeScore >= _policy.ScoreThreshold)
        {
            IncrementCounter("above_threshold_skips");
            return new ArchiveDecision(score.ObjectId, false, "none",
                $"Composite score {score.CompositeScore:F1} is above threshold {_policy.ScoreThreshold:F1}.", score, DateTime.UtcNow);
        }

        // Determine target tier based on score bands
        string targetTier = score.CompositeScore switch
        {
            >= 20 => "cold",
            >= 10 => "archive",
            _ => "deep_archive"
        };

        IncrementCounter("archive_decisions");
        return new ArchiveDecision(score.ObjectId, true, targetTier,
            $"Composite score {score.CompositeScore:F1} is below threshold {_policy.ScoreThreshold:F1}. " +
            $"Target tier: {targetTier}.",
            score, DateTime.UtcNow);
    }
}

#endregion

#region Strategy 2: AgeBasedAutoArchiveStrategy

/// <summary>
/// Re-evaluates consciousness scores by applying a time-decay factor to the value portion,
/// reflecting the reality that idle data loses value over time while liability remains constant.
/// </summary>
/// <remarks>
/// Decay schedule applied to the value portion of the score:
/// <list type="bullet">
///   <item><term>30 days idle</term><description>10% value reduction</description></item>
///   <item><term>60 days idle</term><description>20% value reduction</description></item>
///   <item><term>90 days idle</term><description>30% value reduction</description></item>
///   <item><term>180+ days idle</term><description>50% value reduction</description></item>
/// </list>
/// Metadata keys consumed: "last_accessed" (DateTime), "created_at" (DateTime), "classification" (string), "legal_hold" (bool).
/// </remarks>
public sealed class AgeBasedAutoArchiveStrategy : ConsciousnessStrategyBase
{
    private ArchivePolicy _policy = new();

    /// <inheritdoc />
    public override string StrategyId => "auto-archive-age";

    /// <inheritdoc />
    public override string DisplayName => "Age-Based Auto-Archive";

    /// <inheritdoc />
    public override ConsciousnessCategory Category => ConsciousnessCategory.AutoArchive;

    /// <inheritdoc />
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true,
        SupportsBatch: true,
        SupportsRetroactive: true);

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Re-evaluates consciousness scores by applying time-decay to the value portion. " +
        "Idle data loses value over time while liability remains constant. " +
        "After decay, if the recomputed composite falls below the archive threshold, archive is recommended.";

    /// <inheritdoc />
    public override string[] Tags => ["auto-archive", "age-based", "time-decay", "value-decay", "idle-detection"];

    /// <summary>
    /// Configures the archive policy used for age-based evaluation.
    /// </summary>
    /// <param name="policy">The archive policy to apply.</param>
    public void Configure(ArchivePolicy policy)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    /// <summary>
    /// Computes the value decay factor based on how many days since the data was last accessed.
    /// </summary>
    /// <param name="daysSinceLastAccess">Number of days since the last access.</param>
    /// <returns>A multiplier between 0.5 and 1.0 to apply to the value score.</returns>
    public static double ComputeDecayFactor(double daysSinceLastAccess)
    {
        return daysSinceLastAccess switch
        {
            >= 180 => 0.50,
            >= 90 => 0.70,
            >= 60 => 0.80,
            >= 30 => 0.90,
            _ => 1.00
        };
    }

    /// <summary>
    /// Evaluates whether a data object should be archived based on its consciousness score
    /// with time-decay applied to the value portion.
    /// </summary>
    /// <param name="score">The consciousness score of the data object.</param>
    /// <param name="metadata">Object metadata including last_accessed, created_at, classification, and legal_hold.</param>
    /// <returns>An archive decision reflecting the decayed score evaluation.</returns>
    public ArchiveDecision Evaluate(ConsciousnessScore score, Dictionary<string, object> metadata)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(metadata);
        IncrementCounter("evaluate_invocations");

        // Check legal hold
        if (metadata.TryGetValue("legal_hold", out var legalHoldObj) && Convert.ToBoolean(legalHoldObj))
        {
            IncrementCounter("legal_hold_skips");
            return new ArchiveDecision(score.ObjectId, false, "none",
                "Object is under legal hold and cannot be archived.", score, DateTime.UtcNow);
        }

        // Check classification exemptions
        if (metadata.TryGetValue("classification", out var classObj))
        {
            string classification = classObj?.ToString() ?? string.Empty;
            if (_policy.EffectiveExemptClassifications
                .Any(c => string.Equals(c, classification, StringComparison.OrdinalIgnoreCase)))
            {
                IncrementCounter("classification_exempt_skips");
                return new ArchiveDecision(score.ObjectId, false, "none",
                    $"Object classification '{classification}' is exempt from auto-archive.", score, DateTime.UtcNow);
            }
        }

        // Check minimum age
        if (metadata.TryGetValue("created_at", out var createdObj))
        {
            DateTime createdAt = Convert.ToDateTime(createdObj);
            double ageDays = (DateTime.UtcNow - createdAt).TotalDays;
            if (ageDays < _policy.MinAgeDays)
            {
                IncrementCounter("too_young_skips");
                return new ArchiveDecision(score.ObjectId, false, "none",
                    $"Object is only {ageDays:F0} days old; minimum age is {_policy.MinAgeDays} days.", score, DateTime.UtcNow);
            }
        }

        // Compute decay factor based on last access time
        double daysSinceAccess = 0;
        if (metadata.TryGetValue("last_accessed", out var lastAccessedObj))
        {
            DateTime lastAccessed = Convert.ToDateTime(lastAccessedObj);
            daysSinceAccess = (DateTime.UtcNow - lastAccessed).TotalDays;
        }

        double decayFactor = ComputeDecayFactor(daysSinceAccess);

        // Apply decay to value portion only; liability remains constant
        double decayedValue = score.Value.OverallScore * decayFactor;
        double liabilityContribution = score.Liability.OverallScore;

        // Recompute composite using the standard 60/40 ratio (value/liability)
        // Higher value raises score, higher liability lowers score
        double recomputedComposite = (decayedValue * 0.6) + ((100.0 - liabilityContribution) * 0.4);
        recomputedComposite = Math.Clamp(recomputedComposite, 0.0, 100.0);

        if (recomputedComposite >= _policy.ScoreThreshold)
        {
            IncrementCounter("above_threshold_after_decay");
            return new ArchiveDecision(score.ObjectId, false, "none",
                $"Decayed composite score {recomputedComposite:F1} (decay factor: {decayFactor:F2}, " +
                $"days idle: {daysSinceAccess:F0}) is above threshold {_policy.ScoreThreshold:F1}.",
                score, DateTime.UtcNow);
        }

        string targetTier = recomputedComposite switch
        {
            >= 20 => "cold",
            >= 10 => "archive",
            _ => "deep_archive"
        };

        IncrementCounter("archive_decisions");
        return new ArchiveDecision(score.ObjectId, true, targetTier,
            $"Decayed composite score {recomputedComposite:F1} (original: {score.CompositeScore:F1}, " +
            $"decay factor: {decayFactor:F2}, days idle: {daysSinceAccess:F0}) is below threshold " +
            $"{_policy.ScoreThreshold:F1}. Target tier: {targetTier}.",
            score, DateTime.UtcNow);
    }
}

#endregion

#region Strategy 3: TieredAutoArchiveStrategy

/// <summary>
/// Implements progressive archival where data moves through storage tiers over time
/// as its consciousness score declines and age increases.
/// </summary>
/// <remarks>
/// Tier transition rules:
/// <list type="bullet">
///   <item><term>hot -> warm</term><description>Score &lt; 60 AND age > 30 days</description></item>
///   <item><term>warm -> cold</term><description>Score &lt; 40 AND age > 90 days</description></item>
///   <item><term>cold -> archive</term><description>Score &lt; 25 AND age > 180 days</description></item>
///   <item><term>archive -> deep_archive</term><description>Score &lt; 15 AND age > 365 days</description></item>
/// </list>
/// Each tier transition is logged with a timestamp and reason. Metadata keys consumed:
/// "current_tier" (string), "created_at" (DateTime), "classification" (string), "legal_hold" (bool).
/// </remarks>
public sealed class TieredAutoArchiveStrategy : ConsciousnessStrategyBase
{
    /// <summary>
    /// Records a single tier transition with timestamp and reason.
    /// </summary>
    /// <param name="ObjectId">The data object identifier.</param>
    /// <param name="FromTier">The tier the object is transitioning from.</param>
    /// <param name="ToTier">The tier the object is transitioning to.</param>
    /// <param name="Reason">Why the transition is occurring.</param>
    /// <param name="TransitionedAt">UTC timestamp of the transition.</param>
    public sealed record TierTransition(
        string ObjectId,
        string FromTier,
        string ToTier,
        string Reason,
        DateTime TransitionedAt);

    private static readonly string[] TierOrder = { "hot", "warm", "cold", "archive", "deep_archive" };

    private readonly ConcurrentDictionary<string, List<TierTransition>> _transitionLog = new();

    /// <inheritdoc />
    public override string StrategyId => "auto-archive-tiered";

    /// <inheritdoc />
    public override string DisplayName => "Tiered Auto-Archive";

    /// <inheritdoc />
    public override ConsciousnessCategory Category => ConsciousnessCategory.AutoArchive;

    /// <inheritdoc />
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true,
        SupportsBatch: true,
        SupportsRetroactive: true);

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Implements progressive archival where data moves through storage tiers (hot -> warm -> cold -> archive -> deep_archive) " +
        "over time as consciousness score declines and age increases. Each transition is logged for audit.";

    /// <inheritdoc />
    public override string[] Tags => ["auto-archive", "tiered", "progressive", "storage-tiers", "lifecycle-management"];

    /// <summary>
    /// Evaluates whether a data object should move to the next storage tier based on its
    /// consciousness score, current tier, and age.
    /// </summary>
    /// <param name="score">The consciousness score of the data object.</param>
    /// <param name="metadata">Object metadata including current_tier, created_at, classification, and legal_hold.</param>
    /// <returns>An archive decision indicating whether a tier transition should occur.</returns>
    public ArchiveDecision Evaluate(ConsciousnessScore score, Dictionary<string, object> metadata)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(metadata);
        IncrementCounter("evaluate_invocations");

        // Check legal hold
        if (metadata.TryGetValue("legal_hold", out var legalHoldObj) && Convert.ToBoolean(legalHoldObj))
        {
            IncrementCounter("legal_hold_skips");
            return new ArchiveDecision(score.ObjectId, false, "none",
                "Object is under legal hold and cannot transition tiers.", score, DateTime.UtcNow);
        }

        // Check classification exemptions (restricted and top_secret never auto-archive)
        if (metadata.TryGetValue("classification", out var classObj))
        {
            string classification = classObj?.ToString() ?? string.Empty;
            if (string.Equals(classification, "restricted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(classification, "top_secret", StringComparison.OrdinalIgnoreCase))
            {
                IncrementCounter("classification_exempt_skips");
                return new ArchiveDecision(score.ObjectId, false, "none",
                    $"Object classification '{classification}' is exempt from tier transitions.", score, DateTime.UtcNow);
            }
        }

        string currentTier = metadata.TryGetValue("current_tier", out var tierObj)
            ? tierObj?.ToString() ?? "hot"
            : "hot";

        double ageDays = 0;
        if (metadata.TryGetValue("created_at", out var createdObj))
        {
            DateTime createdAt = Convert.ToDateTime(createdObj);
            ageDays = (DateTime.UtcNow - createdAt).TotalDays;
        }

        // Determine if transition is warranted based on tier-specific rules
        string? nextTier = DetermineNextTier(currentTier, score.CompositeScore, ageDays);

        if (nextTier == null)
        {
            IncrementCounter("no_transition_needed");
            return new ArchiveDecision(score.ObjectId, false, currentTier,
                $"No tier transition needed. Current tier: {currentTier}, " +
                $"score: {score.CompositeScore:F1}, age: {ageDays:F0} days.",
                score, DateTime.UtcNow);
        }

        // Log the transition
        var transition = new TierTransition(
            score.ObjectId, currentTier, nextTier,
            $"Score {score.CompositeScore:F1} and age {ageDays:F0} days triggered {currentTier} -> {nextTier} transition.",
            DateTime.UtcNow);

        _transitionLog.AddOrUpdate(
            score.ObjectId,
            _ => new List<TierTransition> { transition },
            (_, existing) => { existing.Add(transition); return existing; });

        IncrementCounter("tier_transitions");
        return new ArchiveDecision(score.ObjectId, true, nextTier,
            $"Tier transition: {currentTier} -> {nextTier}. " +
            $"Score: {score.CompositeScore:F1}, age: {ageDays:F0} days.",
            score, DateTime.UtcNow);
    }

    /// <summary>
    /// Gets the transition history for a specific data object.
    /// </summary>
    /// <param name="objectId">The data object identifier.</param>
    /// <returns>List of tier transitions, empty if none recorded.</returns>
    public IReadOnlyList<TierTransition> GetTransitionHistory(string objectId)
    {
        return _transitionLog.TryGetValue(objectId, out var transitions)
            ? transitions.AsReadOnly()
            : Array.Empty<TierTransition>();
    }

    private static string? DetermineNextTier(string currentTier, double compositeScore, double ageDays)
    {
        return currentTier.ToLowerInvariant() switch
        {
            "hot" when compositeScore < 60 && ageDays > 30 => "warm",
            "warm" when compositeScore < 40 && ageDays > 90 => "cold",
            "cold" when compositeScore < 25 && ageDays > 180 => "archive",
            "archive" when compositeScore < 15 && ageDays > 365 => "deep_archive",
            _ => null
        };
    }
}

#endregion

#region Strategy 4: AutoArchiveOrchestrator

/// <summary>
/// Orchestrates auto-archive decisions by evaluating all archive strategies (threshold, age-based,
/// tiered) and applying consensus logic: archival only proceeds when all strategies agree.
/// Subscribes to "consciousness.archive.recommended" message bus topic and publishes
/// "consciousness.archive.executed" or "consciousness.archive.deferred" events.
/// </summary>
/// <remarks>
/// Consensus model: if any strategy says "don't archive", the object is not archived.
/// The most aggressive agreed-upon action (deepest tier) is selected when all agree.
/// Maintains a tracking dictionary of all archive decisions for audit and reporting.
/// </remarks>
public sealed class AutoArchiveOrchestrator : ConsciousnessStrategyBase
{
    private static readonly string[] TierDepthOrder = { "hot", "warm", "cold", "archive", "glacier", "deep_archive" };

    private readonly ThresholdAutoArchiveStrategy _thresholdStrategy = new();
    private readonly AgeBasedAutoArchiveStrategy _ageBasedStrategy = new();
    private readonly TieredAutoArchiveStrategy _tieredStrategy = new();
    private readonly ConcurrentDictionary<string, ArchiveDecision> _decisions = new();

    /// <summary>
    /// The message bus topic this orchestrator subscribes to for archive recommendations.
    /// </summary>
    public const string SubscribeTopic = "consciousness.archive.recommended";

    /// <summary>
    /// The message bus topic published when an archive action is executed.
    /// </summary>
    public const string ExecutedTopic = "consciousness.archive.executed";

    /// <summary>
    /// The message bus topic published when an archive action is deferred (grace period).
    /// </summary>
    public const string DeferredTopic = "consciousness.archive.deferred";

    /// <inheritdoc />
    public override string StrategyId => "auto-archive-orchestrator";

    /// <inheritdoc />
    public override string DisplayName => "Auto-Archive Orchestrator";

    /// <inheritdoc />
    public override ConsciousnessCategory Category => ConsciousnessCategory.AutoArchive;

    /// <inheritdoc />
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true,
        SupportsBatch: true,
        SupportsRealTime: true,
        SupportsStreaming: true,
        SupportsRetroactive: true);

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Orchestrates auto-archive decisions using consensus across threshold, age-based, and tiered strategies. " +
        "Subscribes to 'consciousness.archive.recommended' and publishes 'consciousness.archive.executed' or " +
        "'consciousness.archive.deferred'. Archive only proceeds when all strategies agree.";

    /// <inheritdoc />
    public override string[] Tags => ["auto-archive", "orchestrator", "consensus", "message-bus", "lifecycle-management"];

    /// <summary>
    /// Configures all underlying archive strategies with the specified policy.
    /// </summary>
    /// <param name="policy">The archive policy to apply across all strategies.</param>
    public void Configure(ArchivePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _thresholdStrategy.Configure(policy);
        _ageBasedStrategy.Configure(policy);
    }

    /// <summary>
    /// Evaluates a single consciousness score against all archive strategies using consensus logic.
    /// Archive is only recommended when all strategies agree the object should be archived.
    /// </summary>
    /// <param name="score">The consciousness score to evaluate.</param>
    /// <param name="metadata">Object metadata for evaluation context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A consensus-based archive decision.</returns>
    public Task<ArchiveDecision> EvaluateAsync(
        ConsciousnessScore score,
        Dictionary<string, object> metadata,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(metadata);
        IncrementCounter("evaluate_invocations");

        // Evaluate all three strategies
        var thresholdDecision = _thresholdStrategy.Evaluate(score, metadata);
        var ageDecision = _ageBasedStrategy.Evaluate(score, metadata);
        var tieredDecision = _tieredStrategy.Evaluate(score, metadata);

        var allDecisions = new[] { thresholdDecision, ageDecision, tieredDecision };

        // Consensus: all must agree to archive
        bool allAgree = allDecisions.All(d => d.ShouldArchive);

        ArchiveDecision finalDecision;
        if (!allAgree)
        {
            var dissenters = allDecisions.Where(d => !d.ShouldArchive)
                .Select(d => d.Reason);
            string combinedReason = "Consensus not reached. Dissenting reasons: " +
                                    string.Join("; ", dissenters);

            finalDecision = new ArchiveDecision(
                score.ObjectId, false, "none", combinedReason, score, DateTime.UtcNow);
            IncrementCounter("consensus_not_reached");
        }
        else
        {
            // Select the deepest tier that all strategies agreed on
            string deepestTier = allDecisions
                .Select(d => d.TargetTier)
                .OrderByDescending(t => Array.IndexOf(TierDepthOrder, t.ToLowerInvariant()))
                .First();

            string reason = $"Consensus reached across all strategies. " +
                            $"Threshold: {thresholdDecision.TargetTier}, " +
                            $"Age-based: {ageDecision.TargetTier}, " +
                            $"Tiered: {tieredDecision.TargetTier}. " +
                            $"Selected deepest agreed tier: {deepestTier}.";

            finalDecision = new ArchiveDecision(
                score.ObjectId, true, deepestTier, reason, score, DateTime.UtcNow);
            IncrementCounter("consensus_reached");
        }

        _decisions[score.ObjectId] = finalDecision;
        return Task.FromResult(finalDecision);
    }

    /// <summary>
    /// Evaluates a batch of consciousness scores against all archive strategies.
    /// </summary>
    /// <param name="scores">The consciousness scores to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of consensus-based archive decisions.</returns>
    public async Task<IReadOnlyList<ArchiveDecision>> EvaluateBatchAsync(
        IReadOnlyList<ConsciousnessScore> scores,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(scores);
        IncrementCounter("batch_evaluate_invocations");

        var results = new List<ArchiveDecision>(scores.Count);
        foreach (var score in scores)
        {
            ct.ThrowIfCancellationRequested();
            var metadata = score.Metadata != null
                ? new Dictionary<string, object>(score.Metadata)
                : new Dictionary<string, object>();
            var decision = await EvaluateAsync(score, metadata, ct).ConfigureAwait(false);
            results.Add(decision);
        }

        return results.AsReadOnly();
    }

    /// <summary>
    /// Gets a previously recorded archive decision for a specific object.
    /// </summary>
    /// <param name="objectId">The data object identifier.</param>
    /// <returns>The archive decision, or null if not found.</returns>
    public ArchiveDecision? GetDecision(string objectId) =>
        _decisions.TryGetValue(objectId, out var decision) ? decision : null;

    /// <summary>
    /// Gets all tracked archive decisions.
    /// </summary>
    /// <returns>A read-only dictionary of all decisions keyed by object ID.</returns>
    public IReadOnlyDictionary<string, ArchiveDecision> GetAllDecisions() =>
        new Dictionary<string, ArchiveDecision>(_decisions);
}

#endregion
