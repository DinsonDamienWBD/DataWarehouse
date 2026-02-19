using DataWarehouse.SDK.Storage.Fabric;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using StorageCapabilities = DataWarehouse.SDK.Contracts.Storage.StorageCapabilities;
using StorageHealthInfo = DataWarehouse.SDK.Contracts.Storage.StorageHealthInfo;
using StorageTier = DataWarehouse.SDK.Contracts.Storage.StorageTier;
using IStorageStrategy = DataWarehouse.SDK.Contracts.Storage.IStorageStrategy;

namespace DataWarehouse.Plugins.UniversalFabric.Placement;

/// <summary>
/// Orchestrates placement rule evaluation and multi-factor scoring to select the optimal
/// storage backend for new objects. Thread-safe for concurrent store operations.
/// </summary>
/// <remarks>
/// <para>
/// The placement optimizer works in phases:
/// 1. Retrieve all backends from <see cref="IBackendRegistry"/>
/// 2. Filter out read-only backends (cannot store new objects)
/// 3. Apply Require rules (hard filter -- only matching backends pass)
/// 4. Apply Exclude rules (remove disqualified backends)
/// 5. Apply condition-based rules using <see cref="PlacementContext"/>
/// 6. Score remaining candidates using <see cref="PlacementScorer"/>
/// 7. Apply preferred backend boost if specified in hints
/// 8. Return the highest-scoring backend with score breakdown
/// </para>
/// <para>
/// Rules are evaluated in priority order (lower Priority value = evaluated first).
/// If no candidates remain after filtering, a <see cref="PlacementFailedException"/> is thrown.
/// </para>
/// </remarks>
public class PlacementOptimizer
{
    private readonly IBackendRegistry _registry;
    private readonly PlacementScorer _scorer;
    private readonly List<PlacementRule> _rules = new();
    private readonly ReaderWriterLockSlim _rulesLock = new();

    /// <summary>
    /// Score boost applied to the preferred backend when it appears among candidates.
    /// </summary>
    private const double PreferredBackendBoost = 0.2;

    /// <summary>
    /// Initializes a new instance of <see cref="PlacementOptimizer"/>.
    /// </summary>
    /// <param name="registry">The backend registry to query for available backends.</param>
    /// <param name="scorer">Optional custom scorer. If null, a default scorer is used.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="registry"/> is null.</exception>
    public PlacementOptimizer(IBackendRegistry registry, PlacementScorer? scorer = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _scorer = scorer ?? new PlacementScorer();
    }

    /// <summary>
    /// Gets the current placement rules, ordered by priority.
    /// </summary>
    public IReadOnlyList<PlacementRule> Rules
    {
        get
        {
            _rulesLock.EnterReadLock();
            try
            {
                return _rules.OrderBy(r => r.Priority).ToList().AsReadOnly();
            }
            finally
            {
                _rulesLock.ExitReadLock();
            }
        }
    }

    /// <summary>
    /// Adds a placement rule. If a rule with the same name already exists, it is replaced.
    /// </summary>
    /// <param name="rule">The rule to add.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="rule"/> is null.</exception>
    public void AddRule(PlacementRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        _rulesLock.EnterWriteLock();
        try
        {
            _rules.RemoveAll(r => string.Equals(r.Name, rule.Name, StringComparison.OrdinalIgnoreCase));
            _rules.Add(rule);
        }
        finally
        {
            _rulesLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Removes a placement rule by name.
    /// </summary>
    /// <param name="ruleName">The name of the rule to remove.</param>
    /// <returns>True if the rule was found and removed; false otherwise.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="ruleName"/> is null or empty.</exception>
    public bool RemoveRule(string ruleName)
    {
        if (string.IsNullOrEmpty(ruleName))
            throw new ArgumentNullException(nameof(ruleName));

        _rulesLock.EnterWriteLock();
        try
        {
            return _rules.RemoveAll(r => string.Equals(r.Name, ruleName, StringComparison.OrdinalIgnoreCase)) > 0;
        }
        finally
        {
            _rulesLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Selects the best backend for placing a new object based on placement hints,
    /// rules, and multi-factor scoring.
    /// </summary>
    /// <param name="hints">Placement hints describing desired backend characteristics.</param>
    /// <param name="context">Optional context about the object being placed (content type, size, metadata).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A placement result containing the selected backend, score, and alternatives.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="hints"/> is null.</exception>
    /// <exception cref="PlacementFailedException">Thrown when no backend matches the placement criteria.</exception>
    public async Task<PlacementResult> SelectBackendAsync(
        StoragePlacementHints hints,
        PlacementContext? context = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hints);
        ct.ThrowIfCancellationRequested();

        // If explicit backend is requested, use it directly
        if (!string.IsNullOrEmpty(hints.PreferredBackendId))
        {
            var preferred = _registry.GetById(hints.PreferredBackendId);
            if (preferred is not null)
            {
                var health = await GetHealthSafeAsync(preferred, ct);
                var breakdown = _scorer.ScoreWithBreakdown(preferred, hints, health);
                return new PlacementResult
                {
                    Backend = preferred,
                    Score = breakdown.TotalScore,
                    ScoreBreakdown = breakdown.ToDictionary(),
                    Alternatives = null
                };
            }
        }

        // Get all backends and filter
        var allBackends = _registry.All;
        if (allBackends.Count == 0)
            throw new PlacementFailedException("No backends registered in the fabric.", hints);

        // Filter out read-only backends (cannot store new objects)
        var candidates = allBackends.Where(b => !b.IsReadOnly).ToList();
        if (candidates.Count == 0)
            throw new PlacementFailedException("All registered backends are read-only.", hints);

        // Get current rules snapshot
        List<PlacementRule> rules;
        _rulesLock.EnterReadLock();
        try
        {
            rules = _rules.OrderBy(r => r.Priority).ToList();
        }
        finally
        {
            _rulesLock.ExitReadLock();
        }

        // Apply rules
        candidates = ApplyRules(candidates, rules, context);

        if (candidates.Count == 0)
            throw new PlacementFailedException(
                "No backends match placement rules and hints.", hints);

        // Score all candidates
        var scoredCandidates = new List<(BackendDescriptor Backend, double Score, IReadOnlyDictionary<string, double> Breakdown)>();

        foreach (var backend in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var health = await GetHealthSafeAsync(backend, ct);
            var breakdown = _scorer.ScoreWithBreakdown(backend, hints, health);
            var score = breakdown.TotalScore;

            // Boost preferred backend if it's among candidates
            if (!string.IsNullOrEmpty(hints.PreferredBackendId) &&
                string.Equals(backend.BackendId, hints.PreferredBackendId, StringComparison.OrdinalIgnoreCase))
            {
                score = Math.Min(1.0, score + PreferredBackendBoost);
            }

            scoredCandidates.Add((backend, score, breakdown.ToDictionary()));
        }

        // Sort by score descending
        scoredCandidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        var winner = scoredCandidates[0];
        var alternatives = scoredCandidates.Count > 1
            ? scoredCandidates.Skip(1).Select(s => s.Backend).ToList().AsReadOnly()
            : null;

        return new PlacementResult
        {
            Backend = winner.Backend,
            Score = winner.Score,
            ScoreBreakdown = winner.Breakdown,
            Alternatives = alternatives
        };
    }

    /// <summary>
    /// Selects multiple backends for placement, useful for replication scenarios.
    /// Returns the top N backends ordered by descending score.
    /// </summary>
    /// <param name="hints">Placement hints describing desired backend characteristics.</param>
    /// <param name="count">The number of backends to select.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An ordered list of placement results (best first).</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="hints"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="count"/> is less than 1.</exception>
    /// <exception cref="PlacementFailedException">Thrown when not enough backends match the placement criteria.</exception>
    public async Task<IReadOnlyList<PlacementResult>> SelectBackendsAsync(
        StoragePlacementHints hints,
        int count,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hints);
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count), "Count must be at least 1.");

        ct.ThrowIfCancellationRequested();

        // Get all backends and filter
        var allBackends = _registry.All;
        if (allBackends.Count == 0)
            throw new PlacementFailedException("No backends registered in the fabric.", hints);

        var candidates = allBackends.Where(b => !b.IsReadOnly).ToList();
        if (candidates.Count == 0)
            throw new PlacementFailedException("All registered backends are read-only.", hints);

        // Get current rules snapshot
        List<PlacementRule> rules;
        _rulesLock.EnterReadLock();
        try
        {
            rules = _rules.OrderBy(r => r.Priority).ToList();
        }
        finally
        {
            _rulesLock.ExitReadLock();
        }

        // Apply rules
        candidates = ApplyRules(candidates, rules, context: null);

        if (candidates.Count < count)
            throw new PlacementFailedException(
                $"Only {candidates.Count} backends match placement criteria, but {count} were requested.", hints);

        // Score all candidates
        var scoredCandidates = new List<(BackendDescriptor Backend, double Score, IReadOnlyDictionary<string, double> Breakdown)>();

        foreach (var backend in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var health = await GetHealthSafeAsync(backend, ct);
            var breakdown = _scorer.ScoreWithBreakdown(backend, hints, health);
            scoredCandidates.Add((backend, breakdown.TotalScore, breakdown.ToDictionary()));
        }

        // Sort by score descending and take top N
        scoredCandidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        return scoredCandidates.Take(count).Select((s, i) =>
        {
            var remaining = scoredCandidates.Skip(count).Select(r => r.Backend).ToList();
            return new PlacementResult
            {
                Backend = s.Backend,
                Score = s.Score,
                ScoreBreakdown = s.Breakdown,
                Alternatives = i == 0 && remaining.Count > 0 ? remaining.AsReadOnly() : null
            };
        }).ToList().AsReadOnly();
    }

    /// <summary>
    /// Applies placement rules to filter the candidate list.
    /// Rules are applied in priority order (lowest priority value first).
    /// </summary>
    private static List<BackendDescriptor> ApplyRules(
        List<BackendDescriptor> candidates,
        List<PlacementRule> rules,
        PlacementContext? context)
    {
        foreach (var rule in rules)
        {
            if (candidates.Count == 0)
                break;

            // Check if the rule's condition matches the current context
            if (rule.Condition is not null && !rule.Condition.Matches(context))
                continue;

            switch (rule.Type)
            {
                case PlacementRuleType.Require:
                    candidates = candidates.Where(b => MatchesBackend(b, rule)).ToList();
                    break;

                case PlacementRuleType.Exclude:
                    candidates = candidates.Where(b => !MatchesBackendForExclusion(b, rule)).ToList();
                    break;

                case PlacementRuleType.Include:
                    // Include rules don't filter -- they're informational for scoring
                    break;

                case PlacementRuleType.Prefer:
                    // Prefer rules don't filter -- they're handled in scoring phase
                    break;
            }
        }

        return candidates;
    }

    /// <summary>
    /// Checks if a backend matches a rule's requirements (for Require rules).
    /// A backend matches if it satisfies all specified criteria.
    /// </summary>
    private static bool MatchesBackend(BackendDescriptor backend, PlacementRule rule)
    {
        // Target backend ID -- exact match required
        if (rule.TargetBackendId is not null)
        {
            if (!string.Equals(backend.BackendId, rule.TargetBackendId, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Required tags -- all must be present
        if (rule.RequiredTags is not null && rule.RequiredTags.Count > 0)
        {
            foreach (var tag in rule.RequiredTags)
            {
                if (!backend.Tags.Contains(tag))
                    return false;
            }
        }

        // Required tier -- must match exactly
        if (rule.RequiredTier is not null)
        {
            if (backend.Tier != rule.RequiredTier.Value)
                return false;
        }

        // Required region -- must match
        if (rule.RequiredRegion is not null)
        {
            if (!string.Equals(backend.Region, rule.RequiredRegion, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if a backend matches exclusion criteria. A backend is excluded if it has
    /// any excluded tag or matches the target backend ID for exclusion.
    /// </summary>
    private static bool MatchesBackendForExclusion(BackendDescriptor backend, PlacementRule rule)
    {
        // Excluded tags -- any match means excluded
        if (rule.ExcludedTags is not null && rule.ExcludedTags.Count > 0)
        {
            foreach (var tag in rule.ExcludedTags)
            {
                if (backend.Tags.Contains(tag))
                    return true;
            }
        }

        // Target backend ID exclusion
        if (rule.TargetBackendId is not null)
        {
            if (string.Equals(backend.BackendId, rule.TargetBackendId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Required tags used as exclusion filter: exclude if backend has the required tags
        if (rule.RequiredTags is not null && rule.RequiredTags.Count > 0)
        {
            var allPresent = true;
            foreach (var tag in rule.RequiredTags)
            {
                if (!backend.Tags.Contains(tag))
                {
                    allPresent = false;
                    break;
                }
            }
            if (allPresent)
                return true;
        }

        // Required tier exclusion
        if (rule.RequiredTier is not null && backend.Tier == rule.RequiredTier.Value)
            return true;

        // Required region exclusion
        if (rule.RequiredRegion is not null &&
            string.Equals(backend.Region, rule.RequiredRegion, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Safely retrieves health information for a backend, returning null if unavailable.
    /// </summary>
    private async Task<StorageHealthInfo?> GetHealthSafeAsync(BackendDescriptor backend, CancellationToken ct)
    {
        try
        {
            var strategy = _registry.GetStrategy(backend.BackendId);
            if (strategy is null)
                return null;

            return await strategy.GetHealthAsync(ct);
        }
        catch
        {
            // Health check failure -- treat as unknown health
            return null;
        }
    }
}

/// <summary>
/// Result of a placement decision, containing the selected backend, its score,
/// a detailed score breakdown, and alternative backends for fallback.
/// </summary>
public record PlacementResult
{
    /// <summary>
    /// Gets the selected backend descriptor.
    /// </summary>
    public required BackendDescriptor Backend { get; init; }

    /// <summary>
    /// Gets the overall placement score (0.0 to 1.0). Higher is better.
    /// </summary>
    public required double Score { get; init; }

    /// <summary>
    /// Gets the per-factor score breakdown for observability and debugging.
    /// Keys are factor names (tier, tags, region, capacity, priority, health, capability, total).
    /// </summary>
    public required IReadOnlyDictionary<string, double> ScoreBreakdown { get; init; }

    /// <summary>
    /// Gets alternative backends that were candidates but scored lower, ordered by score descending.
    /// Null if no alternatives are available.
    /// </summary>
    public IReadOnlyList<BackendDescriptor>? Alternatives { get; init; }
}
