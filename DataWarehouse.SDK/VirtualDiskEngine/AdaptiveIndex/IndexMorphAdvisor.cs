using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Audit entry recording a morph decision for VDE audit log integration.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-06 MorphAuditEntry")]
public sealed record MorphAuditEntry
{
    /// <summary>When the morph decision was made.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>The level before the recommended transition.</summary>
    public MorphLevel FromLevel { get; init; }

    /// <summary>The recommended target level.</summary>
    public MorphLevel ToLevel { get; init; }

    /// <summary>Metrics snapshot at the time of the decision.</summary>
    public MorphMetricsSnapshot Metrics { get; init; } = null!;

    /// <summary>Human-readable reason for the recommendation.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Whether this was an automatic revert due to latency regression.</summary>
    public bool WasAutoReverted { get; init; }
}

/// <summary>
/// Result of a periodic evaluation by the advisor.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-06 MorphAdvisoryResult")]
public sealed record MorphAdvisoryResult
{
    /// <summary>The recommended target level, or null if no change is recommended.</summary>
    public MorphLevel? RecommendedLevel { get; init; }

    /// <summary>The metrics snapshot used for the decision.</summary>
    public MorphMetricsSnapshot Snapshot { get; init; } = null!;

    /// <summary>Reason for the recommendation.</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Autonomous morph decision engine that recommends index level transitions based on real-time
/// metrics (object count, R/W ratio, latency, key entropy, insert rate). Integrates with
/// <see cref="IndexMorphPolicy"/> for admin override constraints and self-tunes thresholds
/// to prevent oscillation after latency regressions.
/// </summary>
/// <remarks>
/// <para>
/// The advisor does NOT execute morph transitions; it only recommends them. The caller
/// (typically <see cref="AdaptiveIndexEngine"/>) decides whether to act on the recommendation.
/// </para>
/// <para>
/// Self-tuning: After each morph, the advisor records pre/post P99 latency. If the post-morph
/// P99 exceeds pre-morph by 50%, the advisor auto-reverts and increases the triggering threshold
/// by 2x. After 3 reverts for the same transition, it is permanently disabled.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-06 IndexMorphAdvisor")]
public sealed class IndexMorphAdvisor
{
    private readonly MorphMetricsCollector _metrics;
    private readonly IndexMorphPolicy _policy;
    private readonly Action<MorphAuditEntry>? _auditLogger;
    private readonly object _lock = new();

    // Self-tuning state
    private readonly Dictionary<(MorphLevel From, MorphLevel To), int> _revertCount = new();
    private readonly Dictionary<MorphLevel, long> _adjustedThresholds = new();

    // Cooldown tracking
    private DateTimeOffset _lastMorphTime = DateTimeOffset.MinValue;

    // Latency regression tracking
    private double _preMorphP99;
    private MorphLevel _lastFromLevel;
    private MorphLevel _lastToLevel;
    private bool _hasPendingMorphCheck;

    // Default object count thresholds per level
    private static readonly (MorphLevel Level, long MaxCount)[] DefaultThresholds =
    {
        (MorphLevel.DirectPointer, 1L),
        (MorphLevel.SortedArray, 10_000L),
        (MorphLevel.AdaptiveRadixTree, 1_000_000L),
        (MorphLevel.BeTree, 100_000_000L),
        (MorphLevel.LearnedIndex, 10_000_000_000L),
        (MorphLevel.BeTreeForest, 1_000_000_000_000L),
    };

    /// <summary>
    /// Initializes a new <see cref="IndexMorphAdvisor"/>.
    /// </summary>
    /// <param name="metrics">Metrics collector providing real-time workload data.</param>
    /// <param name="policy">Admin policy constraining morph behavior.</param>
    /// <param name="auditLogger">Optional callback for VDE audit log integration.</param>
    public IndexMorphAdvisor(
        MorphMetricsCollector metrics,
        IndexMorphPolicy policy,
        Action<MorphAuditEntry>? auditLogger = null)
    {
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _auditLogger = auditLogger;
    }

    /// <summary>
    /// Recommends a morph level transition based on the given metrics snapshot.
    /// Returns null if no change is recommended (same level, cooldown active, or policy blocked).
    /// </summary>
    /// <param name="snapshot">The current metrics snapshot.</param>
    /// <returns>The recommended target level, or null if no change is needed.</returns>
    public MorphLevel? Recommend(MorphMetricsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_lock)
        {
            var current = snapshot.CurrentLevel;

            // 1. Check policy override: if ForcedLevel set, return it
            var forcedOverride = _policy.GetOverride();
            if (forcedOverride.HasValue)
            {
                var forced = forcedOverride.Value;
                if (forced != current)
                {
                    LogAudit(current, forced, snapshot, $"Policy forced level override to {forced}", wasAutoReverted: false);
                    return forced;
                }
                return null;
            }

            // 2. Compute recommended level based on object count thresholds
            var recommended = RecommendByObjectCount(snapshot.ObjectCount);

            // 3. Apply workload modifiers
            string reason;
            (recommended, reason) = ApplyModifiers(snapshot, current, recommended);

            // 4. Check policy constraints
            if (recommended != current && !_policy.IsAllowed(current, recommended))
            {
                LogAudit(current, recommended, snapshot, $"Policy blocked transition {current}->{recommended}", wasAutoReverted: false);
                return null;
            }

            // 5. Check cooldown
            if (recommended != current)
            {
                var elapsed = DateTimeOffset.UtcNow - _lastMorphTime;
                if (elapsed < _policy.MorphCooldown)
                {
                    return null;
                }
            }

            // 6. Return recommendation if different from current
            if (recommended != current)
            {
                LogAudit(current, recommended, snapshot, reason, wasAutoReverted: false);
                return recommended;
            }

            return null;
        }
    }

    /// <summary>
    /// Called after a morph is executed to enable latency regression tracking.
    /// Records pre-morph P99 for comparison after the new level stabilizes.
    /// </summary>
    /// <param name="from">The previous morph level.</param>
    /// <param name="to">The new morph level.</param>
    /// <param name="preMorphP99LatencyMs">The P99 latency before the morph.</param>
    public void RecordMorphExecuted(MorphLevel from, MorphLevel to, double preMorphP99LatencyMs)
    {
        lock (_lock)
        {
            _lastMorphTime = DateTimeOffset.UtcNow;
            _preMorphP99 = preMorphP99LatencyMs;
            _lastFromLevel = from;
            _lastToLevel = to;
            _hasPendingMorphCheck = true;
        }
    }

    /// <summary>
    /// Checks for latency regression after a morph and auto-reverts if P99 increased by 50%+.
    /// Should be called some time after <see cref="RecordMorphExecuted"/> to allow the new level to stabilize.
    /// </summary>
    /// <param name="currentP99LatencyMs">The current P99 latency after the morph.</param>
    /// <returns>The level to revert to, or null if no revert is needed.</returns>
    public MorphLevel? CheckLatencyRegression(double currentP99LatencyMs)
    {
        lock (_lock)
        {
            if (!_hasPendingMorphCheck) return null;
            _hasPendingMorphCheck = false;

            // Check if post-morph P99 > pre-morph P99 * 1.5 (50% regression)
            if (_preMorphP99 > 0 && currentP99LatencyMs > _preMorphP99 * 1.5)
            {
                var transition = (_lastFromLevel, _lastToLevel);

                // Increment revert count
                _revertCount.TryGetValue(transition, out int count);
                count++;
                _revertCount[transition] = count;

                // Increase the threshold that triggered the morph by 2x
                AdjustThreshold(_lastToLevel, multiplier: 2.0);

                // If reverted 3 times for same transition, permanently disable it
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
                // Cat 2 (finding 754): snapshot the reference inside the lock to avoid the
                // ??= TOCTOU race when an external caller concurrently assigns DisabledLevels.
=======
                // Cat 2 (finding 754): snapshot the reference inside the lock and use a local variable
                // so that a concurrent external assignment to DisabledLevels cannot race with our write.
>>>>>>> Stashed changes
=======
                // Cat 2 (finding 754): snapshot the reference inside the lock and use a local variable
                // so that a concurrent external assignment to DisabledLevels cannot race with our write.
>>>>>>> Stashed changes
=======
                // Cat 2 (finding 754): snapshot the reference inside the lock and use a local variable
                // so that a concurrent external assignment to DisabledLevels cannot race with our write.
>>>>>>> Stashed changes
=======
                // Cat 2 (finding 754): snapshot the reference inside the lock and use a local variable
                // so that a concurrent external assignment to DisabledLevels cannot race with our write.
>>>>>>> Stashed changes
=======
                // Cat 2 (finding 754): snapshot the reference inside the lock and use a local variable
                // so that a concurrent external assignment to DisabledLevels cannot race with our write.
>>>>>>> Stashed changes
                if (count >= 3)
                {
                    if (_policy.DisabledLevels is null)
                        _policy.DisabledLevels = new Dictionary<MorphLevel, bool>();
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
<<<<<<< Updated upstream
=======
                    // Work on the captured reference to avoid TOCTOU across the ??= boundary
>>>>>>> Stashed changes
=======
                    // Work on the captured reference to avoid TOCTOU across the ??= boundary
>>>>>>> Stashed changes
=======
                    // Work on the captured reference to avoid TOCTOU across the ??= boundary
>>>>>>> Stashed changes
=======
                    // Work on the captured reference to avoid TOCTOU across the ??= boundary
>>>>>>> Stashed changes
=======
                    // Work on the captured reference to avoid TOCTOU across the ??= boundary
>>>>>>> Stashed changes
                    var disabledLevels = _policy.DisabledLevels;
                    disabledLevels[_lastToLevel] = true;
                }

                var snapshot = _metrics.TakeSnapshot(_lastToLevel);
                LogAudit(
                    _lastToLevel, _lastFromLevel, snapshot,
                    $"Auto-revert: P99 regression {_preMorphP99:F1}ms -> {currentP99LatencyMs:F1}ms " +
                    $"(revert #{count} for {_lastFromLevel}->{_lastToLevel})" +
                    (count >= 3 ? $"; transition permanently disabled" : string.Empty),
                    wasAutoReverted: true);

                _lastMorphTime = DateTimeOffset.UtcNow;
                return _lastFromLevel;
            }

            return null;
        }
    }

    /// <summary>
    /// Periodic evaluation called by the adaptive index engine on a timer (default every 30 seconds).
    /// Takes a metrics snapshot, runs Recommend, and returns an advisory result.
    /// Does NOT execute the morph (caller decides).
    /// </summary>
    /// <param name="currentLevel">The current morph level.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Advisory result with optional recommendation.</returns>
    public Task<MorphAdvisoryResult> EvaluateAsync(MorphLevel currentLevel, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var snapshot = _metrics.TakeSnapshot(currentLevel);
        var recommended = Recommend(snapshot);

        var result = new MorphAdvisoryResult
        {
            RecommendedLevel = recommended,
            Snapshot = snapshot,
            Reason = recommended.HasValue
                ? $"Recommend {currentLevel} -> {recommended.Value}"
                : "No change recommended"
        };

        return Task.FromResult(result);
    }

    /// <summary>
    /// Gets the number of times a specific transition has been auto-reverted.
    /// </summary>
    public int GetRevertCount(MorphLevel from, MorphLevel to)
    {
        lock (_lock)
        {
            return _revertCount.TryGetValue((from, to), out int count) ? count : 0;
        }
    }

    /// <summary>
    /// Gets the adjusted threshold for a specific level, or the default if not adjusted.
    /// </summary>
    public long GetEffectiveThreshold(MorphLevel level)
    {
        lock (_lock)
        {
            if (_adjustedThresholds.TryGetValue(level, out long adjusted))
            {
                return adjusted;
            }

            foreach (var (l, maxCount) in DefaultThresholds)
            {
                if (l == level) return maxCount;
            }

            return long.MaxValue;
        }
    }

    private MorphLevel RecommendByObjectCount(long objectCount)
    {
        // Walk thresholds from lowest to highest
        foreach (var (level, maxCount) in DefaultThresholds)
        {
            long effectiveMax = _adjustedThresholds.TryGetValue(level, out long adj) ? adj : maxCount;
            if (objectCount <= effectiveMax)
            {
                return level;
            }
        }

        // Beyond all thresholds: distributed routing
        return MorphLevel.DistributedRouting;
    }

    private (MorphLevel Recommended, string Reason) ApplyModifiers(
        MorphMetricsSnapshot snapshot,
        MorphLevel current,
        MorphLevel countBased)
    {
        var recommended = countBased;
        string reason = $"Object count {snapshot.ObjectCount} -> Level {(byte)countBased} ({countBased})";

        // Modifier: If P99 latency > 10ms and current level > recommended, suggest downgrade
        if (snapshot.P99LatencyMs > 10.0 && current > recommended)
        {
            recommended = countBased; // Already a downgrade from current
            reason = $"P99 latency {snapshot.P99LatencyMs:F1}ms > 10ms, recommend downgrade from {current} to {recommended}";
        }

        // Modifier: If insert rate > 100K/s and current/recommended uses ALEX (LearnedIndex), recommend BeTree
        if (snapshot.InsertRate > 100_000 && recommended == MorphLevel.LearnedIndex)
        {
            recommended = MorphLevel.BeTree;
            reason = $"Insert rate {snapshot.InsertRate:F0}/s > 100K with ALEX, recommend BeTree for write performance";
        }

        // Modifier: ALEX beneficial check for large datasets
        if (snapshot.ObjectCount > 100_000_000L && snapshot.ObjectCount <= 10_000_000_000L)
        {
            if (snapshot.ReadWriteRatio > 0.7 && snapshot.KeyEntropy < 3.0)
            {
                recommended = MorphLevel.LearnedIndex;
                reason = $"R/W ratio {snapshot.ReadWriteRatio:F2} > 0.7 and key entropy {snapshot.KeyEntropy:F2} < 3.0, ALEX beneficial";
            }
        }

        // Modifier: Read-heavy at BeTree level, consider ALEX
        if (snapshot.ReadWriteRatio > 0.9 && current == MorphLevel.BeTree &&
            snapshot.ObjectCount > 1_000_000L)
        {
            recommended = MorphLevel.LearnedIndex;
            reason = $"R/W ratio {snapshot.ReadWriteRatio:F2} > 0.9 at BeTree, ALEX helps read-heavy workloads";
        }

        // Check if transition is permanently disabled
        if (recommended != current)
        {
            var transition = (current, recommended);
            if (_revertCount.TryGetValue(transition, out int reverts) && reverts >= 3)
            {
                recommended = current; // Stay at current level
                reason = $"Transition {current}->{recommended} permanently disabled after {reverts} reverts";
            }
        }

        return (recommended, reason);
    }

    private void AdjustThreshold(MorphLevel level, double multiplier)
    {
        long current = GetEffectiveThreshold(level);
        long adjusted = (long)(current * multiplier);
        // Prevent overflow
        if (adjusted < current) adjusted = long.MaxValue;
        _adjustedThresholds[level] = adjusted;
    }

    private void LogAudit(MorphLevel from, MorphLevel to, MorphMetricsSnapshot metrics, string reason, bool wasAutoReverted)
    {
        _auditLogger?.Invoke(new MorphAuditEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            FromLevel = from,
            ToLevel = to,
            Metrics = metrics,
            Reason = reason,
            WasAutoReverted = wasAutoReverted
        });
    }
}
