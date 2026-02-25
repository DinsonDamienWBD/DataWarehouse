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
    /// Isolated sandbox environment that replays full workloads against hypothetical policies
    /// and produces a <see cref="SimulationImpactReport"/> without modifying the live engine or store (PADV-02).
    /// <para>
    /// The sandbox clones the live <see cref="IPolicyStore"/> into an isolated <see cref="InMemoryPolicyStore"/>,
    /// applies hypothetical policy changes to the clone, creates a sandboxed <see cref="PolicyResolutionEngine"/>,
    /// and resolves all features in both environments to produce a before/after impact analysis.
    /// </para>
    /// <para>
    /// Key invariant: the sandbox NEVER modifies the live engine or store. All hypothetical changes
    /// are applied exclusively to the internal sandbox store and engine.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 76: Performance Optimization (PADV-02)")]
    public sealed class PolicySimulationSandbox : IDisposable
    {
        /// <summary>
        /// Feature IDs classified as security-sensitive. Intensity decreases on these features
        /// are classified as <see cref="ImpactSeverity.Critical"/>.
        /// </summary>
        private static readonly HashSet<string> SecurityFeatureIds = new(StringComparer.Ordinal)
        {
            "encryption",
            "access_control",
            "auth_model",
            "key_management",
            "fips_mode"
        };

        private readonly IPolicyEngine _liveEngine;
        private readonly IPolicyStore _liveStore;
        private InMemoryPolicyStore? _sandboxStore;
        private PolicyResolutionEngine? _sandboxEngine;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private bool _disposed;

        /// <summary>
        /// Initializes a new <see cref="PolicySimulationSandbox"/>.
        /// </summary>
        /// <param name="liveEngine">The live policy engine to read current state from (read-only).</param>
        /// <param name="liveStore">The live policy store to clone from (read-only).</param>
        public PolicySimulationSandbox(IPolicyEngine liveEngine, IPolicyStore liveStore)
        {
            _liveEngine = liveEngine ?? throw new ArgumentNullException(nameof(liveEngine));
            _liveStore = liveStore ?? throw new ArgumentNullException(nameof(liveStore));
        }

        /// <summary>
        /// Simulates a set of hypothetical policy changes against the full feature workload for a VDE path,
        /// producing a comprehensive <see cref="SimulationImpactReport"/> with latency, throughput,
        /// storage, and compliance projections.
        /// <para>
        /// The live engine and store are never modified. All hypothetical changes are applied to an
        /// isolated in-memory clone.
        /// </para>
        /// </summary>
        /// <param name="vdePath">The VDE path to simulate against.</param>
        /// <param name="hypotheticalPolicies">The hypothetical policies to apply in the sandbox.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>A complete impact report comparing current vs hypothetical policy state.</returns>
        public async Task<SimulationImpactReport> SimulateWorkloadAsync(
            string vdePath,
            IReadOnlyList<FeaturePolicy> hypotheticalPolicies,
            CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (string.IsNullOrEmpty(vdePath))
                throw new ArgumentException("VDE path must not be null or empty.", nameof(vdePath));
            if (hypotheticalPolicies is null)
                throw new ArgumentNullException(nameof(hypotheticalPolicies));

            // Initialize sandbox with cloned store
            await InitializeSandboxAsync(ct).ConfigureAwait(false);

            // Apply hypothetical policies to sandbox store
            foreach (var policy in hypotheticalPolicies)
            {
                ct.ThrowIfCancellationRequested();
                await _sandboxStore!.SetAsync(policy.FeatureId, policy.Level, vdePath, policy, ct)
                    .ConfigureAwait(false);
            }

            // Build resolution context for this VDE path
            var context = new PolicyResolutionContext { Path = vdePath };

            // Get active profile to know which features to evaluate
            var profile = await _liveEngine.GetActiveProfileAsync(ct).ConfigureAwait(false);

            // Collect all known feature IDs (from profile + hypothetical policies + classification table)
            var allFeatureIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in profile.FeaturePolicies.Keys)
                allFeatureIds.Add(key);
            foreach (var policy in hypotheticalPolicies)
                allFeatureIds.Add(policy.FeatureId);

            // Resolve current and hypothetical for each feature
            var featureImpacts = new List<FeatureImpact>();
            var currentIntensities = new List<int>();
            var projectedIntensities = new List<int>();

            foreach (var featureId in allFeatureIds)
            {
                ct.ThrowIfCancellationRequested();

                var currentPolicy = await _liveEngine.ResolveAsync(featureId, context, ct).ConfigureAwait(false);
                var hypotheticalPolicy = await _sandboxEngine!.ResolveAsync(featureId, context, ct).ConfigureAwait(false);

                var timing = CheckClassificationTable.GetTiming(featureId);

                var impact = new FeatureImpact
                {
                    FeatureId = featureId,
                    Timing = timing,
                    CurrentPolicy = currentPolicy,
                    HypotheticalPolicy = hypotheticalPolicy,
                    Severity = ImpactSeverity.None // Placeholder; classified below
                };

                impact = impact with { Severity = ClassifySeverity(impact) };
                featureImpacts.Add(impact);

                currentIntensities.Add(currentPolicy.EffectiveIntensity);
                projectedIntensities.Add(hypotheticalPolicy.EffectiveIntensity);
            }

            // Compute latency projection
            var currentTier = await ClassifyTierAsync(_liveStore, vdePath, ct).ConfigureAwait(false);
            var projectedTier = await ClassifyTierAsync(_sandboxStore!, vdePath, ct).ConfigureAwait(false);
            var latency = new LatencyProjection
            {
                CurrentTier = currentTier,
                ProjectedTier = projectedTier,
                CurrentEstimatedNs = EstimateLatencyNs(currentTier),
                ProjectedEstimatedNs = EstimateLatencyNs(projectedTier)
            };

            // Compute throughput projection
            var throughput = new ThroughputProjection
            {
                CurrentIntensityAverage = currentIntensities.Count > 0
                    ? (int)currentIntensities.Average()
                    : 0,
                ProjectedIntensityAverage = projectedIntensities.Count > 0
                    ? (int)projectedIntensities.Average()
                    : 0
            };

            // Compute storage projection (compression and replication features)
            var compressionImpact = featureImpacts.Find(f =>
                string.Equals(f.FeatureId, "compression", StringComparison.Ordinal));
            var replicationImpact = featureImpacts.Find(f =>
                string.Equals(f.FeatureId, "replication", StringComparison.Ordinal));

            var storage = new StorageProjection
            {
                CurrentCompressionIntensity = compressionImpact?.CurrentPolicy.EffectiveIntensity ?? 50,
                ProjectedCompressionIntensity = compressionImpact?.HypotheticalPolicy.EffectiveIntensity ?? 50,
                CurrentReplicationIntensity = replicationImpact?.CurrentPolicy.EffectiveIntensity ?? 50,
                ProjectedReplicationIntensity = replicationImpact?.HypotheticalPolicy.EffectiveIntensity ?? 50
            };

            // Compute compliance projection
            var compliance = BuildComplianceProjection(featureImpacts);

            return new SimulationImpactReport
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                VdePath = vdePath,
                FeatureImpacts = featureImpacts,
                Latency = latency,
                Throughput = throughput,
                Storage = storage,
                Compliance = compliance
            };
        }

        /// <summary>
        /// Simulates switching to an entirely different operational profile and produces an impact report.
        /// Extracts all <see cref="FeaturePolicy"/> entries from the profile and delegates to
        /// <see cref="SimulateWorkloadAsync"/>.
        /// </summary>
        /// <param name="vdePath">The VDE path to simulate against.</param>
        /// <param name="hypotheticalProfile">The hypothetical operational profile to evaluate.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>A complete impact report comparing current vs hypothetical profile state.</returns>
        public Task<SimulationImpactReport> SimulateProfileAsync(
            string vdePath,
            OperationalProfile hypotheticalProfile,
            CancellationToken ct = default)
        {
            if (hypotheticalProfile is null) throw new ArgumentNullException(nameof(hypotheticalProfile));

            var policies = hypotheticalProfile.FeaturePolicies.Values.ToList();
            return SimulateWorkloadAsync(vdePath, policies, ct);
        }

        /// <summary>
        /// Classifies the impact severity of a feature change based on security implications,
        /// cascade strategy changes, intensity deltas, and AI autonomy changes.
        /// </summary>
        /// <param name="impact">The feature impact to classify.</param>
        /// <returns>The computed <see cref="ImpactSeverity"/>.</returns>
        internal static ImpactSeverity ClassifySeverity(FeatureImpact impact)
        {
            // No change at all
            if (impact.IntensityDelta == 0 && !impact.AiAutonomyChanged && !impact.CascadeChanged)
                return ImpactSeverity.None;

            // Security features with intensity decrease are Critical
            if (SecurityFeatureIds.Contains(impact.FeatureId) && impact.IntensityDelta < 0)
                return ImpactSeverity.Critical;

            // Cascade strategy changed is High
            if (impact.CascadeChanged)
                return ImpactSeverity.High;

            // Intensity delta > 20 or AI autonomy changed is Medium
            if (Math.Abs(impact.IntensityDelta) > 20 || impact.AiAutonomyChanged)
                return ImpactSeverity.Medium;

            // Otherwise minor change is Low
            return ImpactSeverity.Low;
        }

        /// <summary>
        /// Estimates per-operation latency in nanoseconds based on deployment tier.
        /// </summary>
        /// <param name="tier">The deployment tier to estimate latency for.</param>
        /// <returns>Estimated latency in nanoseconds.</returns>
        private static double EstimateLatencyNs(DeploymentTier tier) => tier switch
        {
            DeploymentTier.VdeOnly => 0.0,
            DeploymentTier.ContainerStop => 20.0,
            DeploymentTier.FullCascade => 200.0,
            _ => 200.0 // Unknown tier: conservative estimate
        };

        /// <summary>
        /// Resets the sandbox state, clearing the cloned store and engine.
        /// The sandbox will be re-initialized on the next simulation call.
        /// </summary>
        public void Reset()
        {
            _sandboxStore?.Clear();
            _sandboxStore = null;
            _sandboxEngine = null;
        }

        /// <summary>
        /// Initializes the sandbox by cloning the live store into an isolated <see cref="InMemoryPolicyStore"/>
        /// and creating a sandboxed <see cref="PolicyResolutionEngine"/>. Thread-safe via semaphore.
        /// </summary>
        private async Task InitializeSandboxAsync(CancellationToken ct)
        {
            if (_sandboxStore is not null && _sandboxEngine is not null)
                return;

            await _initLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_sandboxStore is not null && _sandboxEngine is not null)
                    return;

                // Create fresh sandbox store
                _sandboxStore = new InMemoryPolicyStore();

                // Clone active profile
                var profile = await _liveEngine.GetActiveProfileAsync(ct).ConfigureAwait(false);

                // Clone per-feature overrides from live store into sandbox
                foreach (var featureId in profile.FeaturePolicies.Keys)
                {
                    ct.ThrowIfCancellationRequested();
                    var overrides = await _liveStore.ListOverridesAsync(featureId, ct).ConfigureAwait(false);
                    foreach (var (level, path, policy) in overrides)
                    {
                        await _sandboxStore.SetAsync(featureId, level, path, policy, ct).ConfigureAwait(false);
                    }
                }

                // Create sandboxed engine with isolated store and in-memory persistence
                var sandboxPersistence = new InMemoryPolicyPersistence();
                _sandboxEngine = new PolicyResolutionEngine(_sandboxStore, sandboxPersistence, profile);
            }
            finally
            {
                _initLock.Release();
            }
        }

        /// <summary>
        /// Classifies the deployment tier for a VDE path by checking the given store for overrides.
        /// </summary>
        private static Task<DeploymentTier> ClassifyTierAsync(IPolicyStore store, string vdePath, CancellationToken ct)
        {
            return DeploymentTierClassifier.ClassifyAsync(store, vdePath, ct);
        }

        /// <summary>
        /// Builds a <see cref="ComplianceProjection"/> from the list of feature impacts by
        /// identifying security-relevant features and tracking upgrades/downgrades.
        /// </summary>
        private static ComplianceProjection BuildComplianceProjection(List<FeatureImpact> impacts)
        {
            var currentSecurityFeatures = new List<string>();
            var projectedSecurityFeatures = new List<string>();
            var downgrades = new List<string>();
            var upgrades = new List<string>();

            foreach (var impact in impacts)
            {
                if (!SecurityFeatureIds.Contains(impact.FeatureId))
                    continue;

                currentSecurityFeatures.Add($"{impact.FeatureId}:{impact.CurrentPolicy.EffectiveIntensity}");
                projectedSecurityFeatures.Add($"{impact.FeatureId}:{impact.HypotheticalPolicy.EffectiveIntensity}");

                if (impact.IntensityDelta < 0)
                {
                    downgrades.Add(impact.FeatureId);
                }
                else if (impact.IntensityDelta > 0)
                {
                    upgrades.Add(impact.FeatureId);
                }
            }

            return new ComplianceProjection
            {
                CurrentSecurityFeatures = currentSecurityFeatures,
                ProjectedSecurityFeatures = projectedSecurityFeatures,
                Downgrades = downgrades,
                Upgrades = upgrades
            };
        }

        /// <summary>
        /// Disposes the sandbox, releasing the initialization semaphore.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _sandboxStore = null;
            _sandboxEngine = null;
            _initLock.Dispose();
        }
    }
}
