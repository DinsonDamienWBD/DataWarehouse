using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy.Performance
{
    /// <summary>
    /// What-if analysis engine that compares current policy state against hypothetical changes
    /// without modifying the live engine, store, or cache (PERF-07).
    /// <para>
    /// Uses <see cref="IPolicyEngine.SimulateAsync"/> which already creates a hypothetical
    /// resolution chain without persisting. The simulator adds comparison logic on top:
    /// intensity delta, AI autonomy changes, cascade changes, and per-parameter diffs.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 76: Performance Optimization (PERF-07)")]
    public sealed class PolicySimulator
    {
        private readonly IPolicyEngine _engine;

        /// <summary>
        /// Initializes a new <see cref="PolicySimulator"/> backed by the given policy engine.
        /// </summary>
        /// <param name="engine">The real resolution engine (never modified by simulation).</param>
        public PolicySimulator(IPolicyEngine engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        /// <summary>
        /// Simulates a single feature policy change and returns a comparison of current vs hypothetical state.
        /// <para>
        /// Neither the live engine, store, nor cache is modified. The engine's existing
        /// <see cref="IPolicyEngine.SimulateAsync"/> handles hypothetical resolution chain construction.
        /// </para>
        /// </summary>
        /// <param name="featureId">The feature to simulate changes for.</param>
        /// <param name="context">The resolution context (path, user, hardware, security).</param>
        /// <param name="hypotheticalPolicy">The hypothetical policy to evaluate without persisting.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>A <see cref="PolicySimulationResult"/> comparing current and hypothetical state.</returns>
        public async Task<PolicySimulationResult> SimulateAsync(
            string featureId,
            PolicyResolutionContext context,
            FeaturePolicy hypotheticalPolicy,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(featureId))
                throw new ArgumentException("Feature ID must not be null or empty.", nameof(featureId));
            if (context is null) throw new ArgumentNullException(nameof(context));
            if (hypotheticalPolicy is null) throw new ArgumentNullException(nameof(hypotheticalPolicy));

            // Get current effective policy (live state)
            var currentPolicy = await _engine.ResolveAsync(featureId, context, ct).ConfigureAwait(false);

            // Get hypothetical effective policy (without persisting)
            var hypotheticalEffective = await _engine.SimulateAsync(featureId, context, hypotheticalPolicy, ct)
                .ConfigureAwait(false);

            // Compute changed parameters
            var changedParams = ComputeChangedParameters(currentPolicy.MergedParameters, hypotheticalEffective.MergedParameters);

            return new PolicySimulationResult
            {
                CurrentPolicy = currentPolicy,
                HypotheticalPolicy = hypotheticalEffective,
                ChangedParameters = changedParams,
                SimulatedAt = DateTimeOffset.UtcNow
            };
        }

        /// <summary>
        /// Simulates switching to an entirely different operational profile and returns per-feature
        /// comparison results for all features affected by the change.
        /// <para>
        /// Iterates all features in both the current and hypothetical profiles, resolving current
        /// state and simulating hypothetical state for each. Only features present in at least
        /// one profile are compared.
        /// </para>
        /// </summary>
        /// <param name="hypotheticalProfile">The hypothetical profile to evaluate.</param>
        /// <param name="context">The resolution context (path, user, hardware, security).</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>An <see cref="AggregateSimulationResult"/> with per-feature comparisons.</returns>
        public async Task<AggregateSimulationResult> SimulateProfileChangeAsync(
            OperationalProfile hypotheticalProfile,
            PolicyResolutionContext context,
            CancellationToken ct = default)
        {
            if (hypotheticalProfile is null) throw new ArgumentNullException(nameof(hypotheticalProfile));
            if (context is null) throw new ArgumentNullException(nameof(context));

            var currentProfile = await _engine.GetActiveProfileAsync(ct).ConfigureAwait(false);

            // Collect all unique feature IDs from both profiles
            var allFeatureIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in currentProfile.FeaturePolicies.Keys)
                allFeatureIds.Add(key);
            foreach (var key in hypotheticalProfile.FeaturePolicies.Keys)
                allFeatureIds.Add(key);

            var featureResults = new Dictionary<string, PolicySimulationResult>(StringComparer.Ordinal);

            foreach (var featureId in allFeatureIds)
            {
                ct.ThrowIfCancellationRequested();

                // Get current effective policy
                var currentPolicy = await _engine.ResolveAsync(featureId, context, ct).ConfigureAwait(false);

                // Get hypothetical: use hypothetical profile's policy if it exists, otherwise simulate removal
                IEffectivePolicy hypotheticalEffective;
                if (hypotheticalProfile.FeaturePolicies.TryGetValue(featureId, out var hypotheticalFeaturePolicy))
                {
                    hypotheticalEffective = await _engine.SimulateAsync(featureId, context, hypotheticalFeaturePolicy, ct)
                        .ConfigureAwait(false);
                }
                else
                {
                    // Feature not in hypothetical profile -- effective policy would be system default
                    hypotheticalEffective = currentPolicy;
                }

                var changedParams = ComputeChangedParameters(
                    currentPolicy.MergedParameters,
                    hypotheticalEffective.MergedParameters);

                featureResults[featureId] = new PolicySimulationResult
                {
                    CurrentPolicy = currentPolicy,
                    HypotheticalPolicy = hypotheticalEffective,
                    ChangedParameters = changedParams,
                    SimulatedAt = DateTimeOffset.UtcNow
                };
            }

            return new AggregateSimulationResult
            {
                FeatureResults = featureResults,
                SimulatedAt = DateTimeOffset.UtcNow
            };
        }

        /// <summary>
        /// Computes which parameter keys differ between current and hypothetical merged parameters.
        /// </summary>
        private static IReadOnlyList<string> ComputeChangedParameters(
            IReadOnlyDictionary<string, string> current,
            IReadOnlyDictionary<string, string> hypothetical)
        {
            var changed = new List<string>();

            // Keys in current that are missing or different in hypothetical
            foreach (var kvp in current)
            {
                if (!hypothetical.TryGetValue(kvp.Key, out var hypotheticalValue)
                    || !string.Equals(kvp.Value, hypotheticalValue, StringComparison.Ordinal))
                {
                    changed.Add(kvp.Key);
                }
            }

            // Keys in hypothetical that are missing from current
            foreach (var key in hypothetical.Keys)
            {
                if (!current.ContainsKey(key))
                {
                    changed.Add(key);
                }
            }

            changed.Sort(StringComparer.Ordinal);
            return changed;
        }
    }

    /// <summary>
    /// Result of a single-feature what-if simulation comparing current policy state against a hypothetical change.
    /// Immutable record capturing intensity delta, AI autonomy changes, cascade changes, and parameter diffs.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 76: Performance Optimization (PERF-07)")]
    public sealed record PolicySimulationResult
    {
        /// <summary>
        /// The current effective policy (live state before any hypothetical change).
        /// </summary>
        public required IEffectivePolicy CurrentPolicy { get; init; }

        /// <summary>
        /// The hypothetical effective policy (what the policy would be after the simulated change).
        /// </summary>
        public required IEffectivePolicy HypotheticalPolicy { get; init; }

        /// <summary>
        /// The difference in intensity between hypothetical and current policies.
        /// Positive means the hypothetical is more intense; negative means less intense.
        /// </summary>
        public int IntensityDelta => HypotheticalPolicy.EffectiveIntensity - CurrentPolicy.EffectiveIntensity;

        /// <summary>
        /// Returns <c>true</c> if the AI autonomy level would change under the hypothetical policy.
        /// </summary>
        public bool AiAutonomyChanged => HypotheticalPolicy.EffectiveAiAutonomy != CurrentPolicy.EffectiveAiAutonomy;

        /// <summary>
        /// Returns <c>true</c> if the cascade strategy would change under the hypothetical policy.
        /// </summary>
        public bool CascadeChanged => HypotheticalPolicy.AppliedCascade != CurrentPolicy.AppliedCascade;

        /// <summary>
        /// Parameter keys that differ between current and hypothetical merged parameters.
        /// Sorted alphabetically for deterministic output.
        /// </summary>
        public IReadOnlyList<string> ChangedParameters { get; init; } = Array.Empty<string>();

        /// <summary>
        /// UTC timestamp at which this simulation was performed.
        /// </summary>
        public DateTimeOffset SimulatedAt { get; init; }
    }

    /// <summary>
    /// Aggregate result of a profile-wide what-if simulation, containing per-feature comparison results.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 76: Performance Optimization (PERF-07)")]
    public sealed record AggregateSimulationResult
    {
        /// <summary>
        /// Per-feature simulation results keyed by feature identifier.
        /// </summary>
        public required IReadOnlyDictionary<string, PolicySimulationResult> FeatureResults { get; init; }

        /// <summary>
        /// Number of features where at least one property (intensity, AI autonomy, cascade, or parameters) would change.
        /// </summary>
        public int FeaturesAffected => FeatureResults.Count(r =>
            r.Value.IntensityDelta != 0 || r.Value.AiAutonomyChanged || r.Value.CascadeChanged);

        /// <summary>
        /// UTC timestamp at which this aggregate simulation was performed.
        /// </summary>
        public DateTimeOffset SimulatedAt { get; init; }
    }
}
