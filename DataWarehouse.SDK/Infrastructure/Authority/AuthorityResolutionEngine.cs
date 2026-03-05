using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Authority
{
    /// <summary>
    /// Core engine for resolving competing authority decisions based on strict priority ordering.
    /// Enforces the authority chain: Quorum (0) > AiEmergency (1) > Admin (2) > SystemDefaults (3).
    /// Lower-authority levels cannot override decisions made at a higher authority level.
    /// <para>
    /// Thread-safe: uses <see cref="ConcurrentDictionary{TKey,TValue}"/> for decision storage
    /// and lock-free reads for configuration.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Authority Chain (AUTH-09)")]
    public sealed class AuthorityResolutionEngine : IAuthorityResolver
    {
        private readonly AuthorityConfiguration _config;
        private readonly ConcurrentDictionary<string, List<AuthorityDecision>> _decisionHistory = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _historyLock = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="AuthorityResolutionEngine"/>.
        /// </summary>
        /// <param name="config">
        /// Configuration controlling the authority chain, enforcement mode, and retention period.
        /// If null, defaults to <see cref="AuthorityConfiguration"/> with the standard four-level chain.
        /// </param>
        public AuthorityResolutionEngine(AuthorityConfiguration? config = null)
        {
            _config = config ?? new AuthorityConfiguration();
        }

        /// <inheritdoc />
        public Task<AuthorityResolution> ResolveAsync(
            string action,
            IReadOnlyList<AuthorityDecision> decisions,
            AuthorityChain? chain = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(action))
                throw new ArgumentException("Action must not be null or empty.", nameof(action));
            if (decisions is null || decisions.Count == 0)
                throw new ArgumentException("At least one decision must be provided.", nameof(decisions));

            var effectiveChain = chain ?? _config.Chain;

            // Sort by AuthorityPriority ascending: lowest number = highest authority = winner
            var sorted = decisions.OrderBy(d => d.AuthorityPriority).ThenBy(d => d.Timestamp).ToList();

            var winner = sorted[0];
            var overridden = sorted.Count > 1
                ? sorted.GetRange(1, sorted.Count - 1)
                : (IReadOnlyList<AuthorityDecision>)Array.Empty<AuthorityDecision>();

            var resolution = new AuthorityResolution
            {
                WinningDecision = winner,
                OverriddenDecisions = overridden,
                Chain = effectiveChain,
                ResolvedAt = DateTimeOffset.UtcNow
            };

            return Task.FromResult(resolution);
        }

        /// <inheritdoc />
        public Task<bool> CanOverrideAsync(
            AuthorityDecision proposed,
            AuthorityDecision existing,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (proposed is null)
                throw new ArgumentNullException(nameof(proposed));
            if (existing is null)
                throw new ArgumentNullException(nameof(existing));

            // Lower priority number = higher authority.
            // Override allowed ONLY if proposed has strictly higher authority (lower number).
            // Equal priority cannot override (strict enforcement).
            var canOverride = proposed.AuthorityPriority < existing.AuthorityPriority;

            return Task.FromResult(canOverride);
        }

        /// <inheritdoc />
        public Task<AuthorityDecision> RecordDecisionAsync(
            string authorityLevelName,
            string action,
            string? actorId = null,
            string? reason = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(authorityLevelName))
                throw new ArgumentException("Authority level name must not be null or empty.", nameof(authorityLevelName));
            if (string.IsNullOrEmpty(action))
                throw new ArgumentException("Action must not be null or empty.", nameof(action));

            // Validate the authority level name exists in the configured chain
            var matchedLevel = FindAuthorityLevel(authorityLevelName);
            if (matchedLevel is null)
            {
                throw new ArgumentException(
                    $"Authority level '{authorityLevelName}' does not exist in the configured chain. " +
                    $"Valid levels: {string.Join(", ", _config.Chain.Levels.Select(l => l.Name))}",
                    nameof(authorityLevelName));
            }

            var decision = new AuthorityDecision
            {
                DecisionId = Guid.NewGuid().ToString("D"),
                AuthorityLevelName = matchedLevel.Name,
                AuthorityPriority = matchedLevel.Priority,
                Action = action,
                Timestamp = DateTimeOffset.UtcNow,
                ActorId = actorId,
                DecisionReason = reason,
                IsActive = true
            };

            // Store the decision under the action key
            lock (_historyLock)
            {
                var list = _decisionHistory.GetOrAdd(action, _ => new List<AuthorityDecision>());
                list.Add(decision);
            }

            return Task.FromResult(decision);
        }

        /// <summary>
        /// Returns all recorded decisions for a given action, ordered newest first.
        /// </summary>
        /// <param name="action">The action to retrieve decision history for.</param>
        /// <returns>
        /// A read-only list of decisions for the action, ordered by timestamp descending.
        /// Returns an empty list if no decisions have been recorded for the action.
        /// </returns>
        public IReadOnlyList<AuthorityDecision> GetDecisionHistory(string action)
        {
            if (string.IsNullOrEmpty(action))
                return Array.Empty<AuthorityDecision>();

            // Acquire lock BEFORE TryGetValue to prevent TOCTOU: the list reference could be
            // replaced and the list itself mutated between TryGetValue and the lock below.
            lock (_historyLock)
            {
                if (!_decisionHistory.TryGetValue(action, out var list))
                    return Array.Empty<AuthorityDecision>();

                return list.OrderByDescending(d => d.Timestamp).ToList().AsReadOnly();
            }
        }

        /// <summary>
        /// Removes all decisions older than the configured <see cref="AuthorityConfiguration.DecisionRetentionPeriod"/>.
        /// This is a maintenance operation that should be called periodically to prevent unbounded memory growth.
        /// </summary>
        public void PurgeExpiredDecisions()
        {
            var cutoff = DateTimeOffset.UtcNow - _config.DecisionRetentionPeriod;

            lock (_historyLock)
            {
                foreach (var kvp in _decisionHistory)
                {
                    kvp.Value.RemoveAll(d => d.Timestamp < cutoff);
                }

                // Remove empty action entries to avoid accumulating empty lists
                var emptyKeys = _decisionHistory.Where(kvp => kvp.Value.Count == 0).Select(kvp => kvp.Key).ToList();
                foreach (var key in emptyKeys)
                {
                    _decisionHistory.TryRemove(key, out _);
                }
            }
        }

        /// <summary>
        /// Finds an authority level by name in the configured chain. Case-insensitive comparison.
        /// </summary>
        private AuthorityLevel? FindAuthorityLevel(string name)
        {
            for (var i = 0; i < _config.Chain.Levels.Length; i++)
            {
                if (string.Equals(_config.Chain.Levels[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return _config.Chain.Levels[i];
                }
            }

            return null;
        }
    }
}
