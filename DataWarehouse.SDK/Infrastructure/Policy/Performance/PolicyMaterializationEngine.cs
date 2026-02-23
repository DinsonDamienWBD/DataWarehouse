using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy.Performance
{
    /// <summary>
    /// Engine that pre-computes effective policies for all registered features at VDE open time (PERF-01)
    /// and re-materializes only when policy changes are signaled (PERF-06).
    /// <para>
    /// At VDE open, <see cref="MaterializeAsync"/> resolves every feature via <see cref="IPolicyEngine.ResolveAsync"/>
    /// and publishes the results into a <see cref="MaterializedPolicyCache"/> double-buffered snapshot.
    /// Subsequent reads via <see cref="TryGetCached"/> return in O(1) with zero cold-start penalty.
    /// </para>
    /// <para>
    /// Re-materialization is guarded by a <see cref="SemaphoreSlim"/>(1,1) to prevent thundering herd
    /// when multiple threads detect staleness simultaneously. The Interlocked-guarded last-change-time
    /// field ensures that <see cref="RematerializeIfStaleAsync"/> is a no-op when no policy has changed.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 76: Performance Optimization (PERF-01, PERF-06)")]
    public sealed class PolicyMaterializationEngine
    {
        private readonly IPolicyEngine _engine;
        private readonly MaterializedPolicyCache _cache;
        private readonly IReadOnlyList<string>? _featureIds;
        private long _lastPolicyChangeTimeTicks = DateTimeOffset.MinValue.UtcTicks;
        private readonly SemaphoreSlim _materializationLock = new(1, 1);

        /// <summary>
        /// Initializes a new <see cref="PolicyMaterializationEngine"/>.
        /// </summary>
        /// <param name="engine">The policy resolution engine used to compute effective policies for each feature.</param>
        /// <param name="cache">The double-buffered cache into which materialized snapshots are published.</param>
        /// <param name="featureIds">
        /// Optional fixed set of feature IDs to materialize. If <c>null</c>, the engine reads feature IDs
        /// from the active operational profile's <c>FeaturePolicies.Keys</c>.
        /// </param>
        public PolicyMaterializationEngine(
            IPolicyEngine engine,
            MaterializedPolicyCache cache,
            IReadOnlyList<string>? featureIds = null)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _featureIds = featureIds;
        }

        /// <summary>
        /// Pre-computes effective policies for all registered features at the given VDE path
        /// and publishes them into the double-buffered cache. Call this at VDE open time (PERF-01).
        /// <para>
        /// Serialized via <see cref="SemaphoreSlim"/>(1,1) to prevent redundant concurrent materializations.
        /// </para>
        /// </summary>
        /// <param name="vdePath">The VDE path to resolve policies against (e.g., "/myVde").</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        public async Task MaterializeAsync(string vdePath, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            await _materializationLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var featureIds = await GetFeatureIdsAsync(ct).ConfigureAwait(false);
                var context = new PolicyResolutionContext { Path = vdePath };
                var results = new Dictionary<string, IEffectivePolicy>(StringComparer.Ordinal);

                foreach (var featureId in featureIds)
                {
                    ct.ThrowIfCancellationRequested();
                    var effective = await _engine.ResolveAsync(featureId, context, ct).ConfigureAwait(false);
                    var key = $"{featureId}:{vdePath}";
                    results[key] = effective;
                }

                _cache.Publish(results);
                Interlocked.Exchange(ref _lastPolicyChangeTimeTicks, DateTimeOffset.UtcNow.UtcTicks);
            }
            finally
            {
                _materializationLock.Release();
            }
        }

        /// <summary>
        /// Pre-computes effective policies for all registered features across multiple paths
        /// (e.g., VDE + containers + objects if known at open time) and publishes a single
        /// combined snapshot into the double-buffered cache.
        /// </summary>
        /// <param name="paths">The set of VDE paths to resolve policies for.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        public async Task MaterializeMultiPathAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
        {
            if (paths is null) throw new ArgumentNullException(nameof(paths));
            ct.ThrowIfCancellationRequested();

            await _materializationLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var featureIds = await GetFeatureIdsAsync(ct).ConfigureAwait(false);
                var results = new Dictionary<string, IEffectivePolicy>(StringComparer.Ordinal);

                foreach (var path in paths)
                {
                    var context = new PolicyResolutionContext { Path = path };

                    foreach (var featureId in featureIds)
                    {
                        ct.ThrowIfCancellationRequested();
                        var effective = await _engine.ResolveAsync(featureId, context, ct).ConfigureAwait(false);
                        var key = $"{featureId}:{path}";
                        results[key] = effective;
                    }
                }

                _cache.Publish(results);
                Interlocked.Exchange(ref _lastPolicyChangeTimeTicks, DateTimeOffset.UtcNow.UtcTicks);
            }
            finally
            {
                _materializationLock.Release();
            }
        }

        /// <summary>
        /// Re-materializes the cache only if a policy change has been signaled since the last materialization (PERF-06).
        /// <para>
        /// If <see cref="NotifyPolicyChanged"/> has not been called since the last materialization,
        /// this method returns immediately without any work. This ensures that the hot path (no policy changes)
        /// pays zero cost for staleness checks.
        /// </para>
        /// </summary>
        /// <param name="vdePath">The VDE path to resolve policies against.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        public async Task RematerializeIfStaleAsync(string vdePath, CancellationToken ct = default)
        {
            var lastChangeTicks = Interlocked.Read(ref _lastPolicyChangeTimeTicks);
            var lastChangeTime = new DateTimeOffset(lastChangeTicks, TimeSpan.Zero);
            if (!_cache.IsStale(lastChangeTime))
            {
                return;
            }

            await MaterializeAsync(vdePath, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Signals that a policy change has occurred, marking the cache as stale (PERF-06).
        /// <para>
        /// Called by policy change event handlers. The next call to <see cref="RematerializeIfStaleAsync"/>
        /// will trigger re-materialization. Direct reads via <see cref="TryGetCached"/> continue
        /// to return the previous snapshot until re-materialization completes.
        /// </para>
        /// </summary>
        public void NotifyPolicyChanged()
        {
            Interlocked.Exchange(ref _lastPolicyChangeTimeTicks, DateTimeOffset.UtcNow.UtcTicks);
        }

        /// <summary>
        /// Convenience method that returns a pre-computed effective policy from the current cache snapshot.
        /// Returns <c>null</c> if the feature/path combination was not pre-computed, allowing the caller
        /// to fall back to live resolution via <see cref="IPolicyEngine.ResolveAsync"/>.
        /// </summary>
        /// <param name="featureId">The feature identifier.</param>
        /// <param name="path">The VDE path.</param>
        /// <returns>The pre-computed <see cref="IEffectivePolicy"/> if found; otherwise <c>null</c>.</returns>
        public IEffectivePolicy? TryGetCached(string featureId, string path)
        {
            return _cache.GetSnapshot().TryGetEffective(featureId, path);
        }

        /// <summary>
        /// Resolves the set of feature IDs to materialize, either from the explicit list
        /// provided at construction or from the active operational profile.
        /// </summary>
        private async Task<IReadOnlyCollection<string>> GetFeatureIdsAsync(CancellationToken ct)
        {
            if (_featureIds is not null)
            {
                return _featureIds;
            }

            var profile = await _engine.GetActiveProfileAsync(ct).ConfigureAwait(false);
            return profile.FeaturePolicies.Keys;
        }
    }
}
