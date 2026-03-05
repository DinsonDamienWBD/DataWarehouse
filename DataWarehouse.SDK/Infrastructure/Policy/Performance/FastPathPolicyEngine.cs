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
    /// Three-tier fast-path policy engine implementing <see cref="IPolicyEngine"/> with deployment-tier
    /// routing for minimal-overhead policy resolution (PERF-04, PERF-05).
    /// <para>
    /// Routing is determined by two orthogonal classifications:
    /// <list type="number">
    ///   <item><description><see cref="CheckTiming"/>: when to evaluate (ConnectTime, SessionCached,
    ///   PerOperation, Deferred, Periodic). ConnectTime/SessionCached use delegate cache; Deferred/Periodic
    ///   use materialized snapshot; PerOperation routes by deployment tier.</description></item>
    ///   <item><description><see cref="DeploymentTier"/>: which fast path to take for PerOperation checks.
    ///   VdeOnly (0ns): compiled delegate only. ContainerStop (~20ns): bloom filter + cache.
    ///   FullCascade (~200ns): full cascade resolution.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Thread safety: <see cref="_currentTier"/> is volatile for safe cross-thread reads after
    /// <see cref="SetDeploymentTier"/>. All other shared state (caches, optimizer) is internally
    /// thread-safe via their own synchronization mechanisms.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 76: Performance Optimization (PERF-04, PERF-05)")]
    public sealed class FastPathPolicyEngine : IPolicyEngine
    {
        private readonly PolicyResolutionEngine _resolutionEngine;
        private readonly MaterializedPolicyCache _materializedCache;
        private readonly PolicyDelegateCache _delegateCache;
        private readonly PolicySkipOptimizer? _skipOptimizer;
        private readonly PolicyMaterializationEngine? _materializationEngine;
        private readonly PolicySimulator _simulator;
        private volatile DeploymentTier _currentTier = DeploymentTier.VdeOnly;

        /// <summary>
        /// Initializes a new <see cref="FastPathPolicyEngine"/> wiring the three-tier fast path.
        /// </summary>
        /// <param name="resolutionEngine">The full cascade engine used as fallback for FullCascade tier.</param>
        /// <param name="materializedCache">Pre-computed policies for zero cold-start lookups (Plan 01).</param>
        /// <param name="delegateCache">Compiled delegates for direct invocation on hot paths (Plan 03).</param>
        /// <param name="skipOptimizer">
        /// Optional bloom filter optimizer for ContainerStop tier pre-checks (Plan 02).
        /// When null, ContainerStop tier falls through to full resolution.
        /// </param>
        /// <param name="materializationEngine">
        /// Optional materialization engine for re-materialization on policy changes (Plan 01).
        /// When null, <see cref="SetActiveProfileAsync"/> does not trigger re-materialization.
        /// </param>
        public FastPathPolicyEngine(
            PolicyResolutionEngine resolutionEngine,
            MaterializedPolicyCache materializedCache,
            PolicyDelegateCache delegateCache,
            PolicySkipOptimizer? skipOptimizer = null,
            PolicyMaterializationEngine? materializationEngine = null)
        {
            _resolutionEngine = resolutionEngine ?? throw new ArgumentNullException(nameof(resolutionEngine));
            _materializedCache = materializedCache ?? throw new ArgumentNullException(nameof(materializedCache));
            _delegateCache = delegateCache ?? throw new ArgumentNullException(nameof(delegateCache));
            _skipOptimizer = skipOptimizer;
            _materializationEngine = materializationEngine;
            _simulator = new PolicySimulator(_resolutionEngine);
        }

        /// <summary>
        /// Gets the current deployment tier classification. Set at VDE open time via
        /// <see cref="InitializeForVdeAsync"/> or explicitly via <see cref="SetDeploymentTier"/>.
        /// </summary>
        public DeploymentTier CurrentTier => _currentTier;

        /// <inheritdoc />
        /// <remarks>
        /// Routes by <see cref="CheckTiming"/> first, then by <see cref="DeploymentTier"/> for PerOperation:
        /// <list type="bullet">
        ///   <item><description>ConnectTime: delegate cache (already resolved at connect time).</description></item>
        ///   <item><description>SessionCached: delegate cache (cached for session, invalidated on policy change).</description></item>
        ///   <item><description>PerOperation + VdeOnly: delegate cache only (0ns overhead).</description></item>
        ///   <item><description>PerOperation + ContainerStop: bloom filter pre-check + delegate cache (~20ns).</description></item>
        ///   <item><description>PerOperation + FullCascade: full cascade resolution (~200ns).</description></item>
        ///   <item><description>Deferred: materialized cache snapshot (used async after operation).</description></item>
        ///   <item><description>Periodic: materialized cache snapshot (evaluated on timer).</description></item>
        /// </list>
        /// </remarks>
        public async Task<IEffectivePolicy> ResolveAsync(
            string featureId,
            PolicyResolutionContext context,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(featureId))
                throw new ArgumentException("Feature ID must not be null or empty.", nameof(featureId));
            if (context is null)
                throw new ArgumentNullException(nameof(context));

            var timing = CheckClassificationTable.GetTiming(featureId);

            return timing switch
            {
                CheckTiming.ConnectTime => _delegateCache.GetOrCompile(featureId, context.Path),
                CheckTiming.SessionCached => _delegateCache.GetOrCompile(featureId, context.Path),
                CheckTiming.PerOperation => await ResolvePerOperationAsync(featureId, context, ct).ConfigureAwait(false),
                CheckTiming.Deferred => ResolveFromSnapshot(featureId, context),
                CheckTiming.Periodic => ResolveFromSnapshot(featureId, context),
                _ => await _resolutionEngine.ResolveAsync(featureId, context, ct).ConfigureAwait(false)
            };
        }

        /// <inheritdoc />
        public async Task<IReadOnlyDictionary<string, IEffectivePolicy>> ResolveAllAsync(
            PolicyResolutionContext context,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (context is null) throw new ArgumentNullException(nameof(context));

            var profile = await _resolutionEngine.GetActiveProfileAsync(ct).ConfigureAwait(false);
            var result = new Dictionary<string, IEffectivePolicy>(StringComparer.Ordinal);

            foreach (var featureId in profile.FeaturePolicies.Keys)
            {
                ct.ThrowIfCancellationRequested();
                result[featureId] = await ResolveAsync(featureId, context, ct).ConfigureAwait(false);
            }

            return result;
        }

        /// <inheritdoc />
        public Task<OperationalProfile> GetActiveProfileAsync(CancellationToken ct = default)
        {
            return _resolutionEngine.GetActiveProfileAsync(ct);
        }

        /// <inheritdoc />
        public async Task SetActiveProfileAsync(OperationalProfile profile, CancellationToken ct = default)
        {
            await _resolutionEngine.SetActiveProfileAsync(profile, ct).ConfigureAwait(false);

            // PERF-06: Trigger re-materialization so caches reflect the new profile
            _materializationEngine?.NotifyPolicyChanged();
        }

        /// <inheritdoc />
        /// <remarks>
        /// Delegates to <see cref="PolicySimulator.SimulateAsync"/> which uses the underlying
        /// resolution engine's SimulateAsync (PERF-07). The live engine, store, and caches are
        /// never modified.
        /// </remarks>
        public async Task<IEffectivePolicy> SimulateAsync(
            string featureId,
            PolicyResolutionContext context,
            FeaturePolicy hypotheticalPolicy,
            CancellationToken ct = default)
        {
            var result = await _simulator.SimulateAsync(featureId, context, hypotheticalPolicy, ct)
                .ConfigureAwait(false);
            return result.HypotheticalPolicy;
        }

        /// <summary>
        /// Sets the current deployment tier, controlling which fast path is used for
        /// <see cref="CheckTiming.PerOperation"/> checks. Called after tier classification at VDE open.
        /// </summary>
        /// <param name="tier">The deployment tier to set.</param>
        public void SetDeploymentTier(DeploymentTier tier)
        {
            _currentTier = tier;
        }

        /// <summary>
        /// Initializes the fast-path engine for a specific VDE: materializes policies, classifies
        /// the deployment tier, and warms up the delegate cache.
        /// <para>
        /// Call this at VDE open time. After initialization, all subsequent <see cref="ResolveAsync"/>
        /// calls benefit from the classified fast path.
        /// </para>
        /// </summary>
        /// <param name="vdePath">The VDE path to initialize for.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        public async Task InitializeForVdeAsync(string vdePath, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(vdePath))
                throw new ArgumentException("VDE path must not be null or empty.", nameof(vdePath));

            // Step 1: Materialize policies for this VDE path
            if (_materializationEngine is not null)
            {
                await _materializationEngine.MaterializeAsync(vdePath, ct).ConfigureAwait(false);
            }

            // Step 2: Classify deployment tier
            if (_skipOptimizer is not null)
            {
                // O(1) bloom filter classification
                var bloomFilter = GetBloomFilterFromOptimizer();
                if (bloomFilter is not null)
                {
                    _currentTier = DeploymentTierClassifier.ClassifyFromBloomFilter(bloomFilter, vdePath);
                }
                else
                {
                    _currentTier = DeploymentTier.VdeOnly;
                }
            }
            else
            {
                _currentTier = DeploymentTier.VdeOnly;
            }

            // Step 3: Warm up delegate cache for all classified features
            var allFeatures = new List<string>();
            foreach (CheckTiming timing in Enum.GetValues<CheckTiming>())
            {
                var features = CheckClassificationTable.GetFeaturesByTiming(timing);
                allFeatures.AddRange(features);
            }

            _delegateCache.WarmUp(allFeatures, vdePath);
        }

        /// <summary>
        /// Resolves a PerOperation feature by routing through the current deployment tier.
        /// </summary>
        private async Task<IEffectivePolicy> ResolvePerOperationAsync(
            string featureId,
            PolicyResolutionContext context,
            CancellationToken ct)
        {
            var tier = _currentTier;

            switch (tier)
            {
                case DeploymentTier.VdeOnly:
                    // 0ns path: direct delegate invocation, no store access
                    return _delegateCache.GetOrCompile(featureId, context.Path);

                case DeploymentTier.ContainerStop:
                    // ~20ns path: bloom filter pre-check, then delegate cache
                    if (_skipOptimizer is not null)
                    {
                        // Check if override exists below container level
                        bool hasBlockOverride = await _skipOptimizer
                            .HasOverrideOptimizedAsync(PolicyLevel.Block, context.Path, ct)
                            .ConfigureAwait(false);
                        bool hasChunkOverride = !hasBlockOverride && await _skipOptimizer
                            .HasOverrideOptimizedAsync(PolicyLevel.Chunk, context.Path, ct)
                            .ConfigureAwait(false);
                        bool hasObjectOverride = !hasBlockOverride && !hasChunkOverride && await _skipOptimizer
                            .HasOverrideOptimizedAsync(PolicyLevel.Object, context.Path, ct)
                            .ConfigureAwait(false);

                        if (!hasBlockOverride && !hasChunkOverride && !hasObjectOverride)
                        {
                            // No override below container -- safe to use delegate cache
                            return _delegateCache.GetOrCompile(featureId, context.Path);
                        }
                    }

                    // Override detected or no optimizer -- fall through to full resolution
                    return await _resolutionEngine.ResolveAsync(featureId, context, ct).ConfigureAwait(false);

                case DeploymentTier.FullCascade:
                default:
                    // ~200ns path: full cascade resolution
                    return await _resolutionEngine.ResolveAsync(featureId, context, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Resolves from the materialized cache snapshot for Deferred and Periodic checks.
        /// Falls back to a default policy if the feature/path is not in the snapshot.
        /// </summary>
        private IEffectivePolicy ResolveFromSnapshot(string featureId, PolicyResolutionContext context)
        {
            var snapshot = _materializedCache.GetSnapshot();
            var cached = snapshot.TryGetEffective(featureId, context.Path);
            if (cached is not null)
            {
                return cached;
            }

            // Fall back to delegate cache (will compile from snapshot or return default)
            return _delegateCache.GetOrCompile(featureId, context.Path);
        }

        /// <summary>
        /// Extracts the bloom filter from the skip optimizer for deployment tier classification.
        /// Returns null when no skip optimizer is registered.
        /// </summary>
        private BloomFilterSkipIndex? GetBloomFilterFromOptimizer()
            => _skipOptimizer?.BloomFilter;
    }
}
