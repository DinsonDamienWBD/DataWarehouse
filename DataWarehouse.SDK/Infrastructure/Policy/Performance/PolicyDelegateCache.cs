using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy.Performance
{
    /// <summary>
    /// Thread-safe cache of <see cref="CompiledPolicyDelegate"/> instances keyed by "featureId:path",
    /// with version-gated invalidation tied to the <see cref="MaterializedPolicyCache"/> (PERF-03, PERF-06).
    /// <para>
    /// On the hot path, <see cref="GetOrCompile"/> performs an O(1) dictionary lookup. If the delegate
    /// exists and its source version matches the current materialized cache version, it is invoked directly.
    /// If the entire cache version has changed (policy update), all delegates are invalidated and recompiled
    /// on demand. This ensures recompilation happens exactly once per policy change, not on every operation.
    /// </para>
    /// <para>
    /// Statistics (hits, misses, recompiles) are tracked via <see cref="Interlocked"/> counters for
    /// lock-free telemetry collection.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 76: Performance Optimization (PERF-03, PERF-06)")]
    public sealed class PolicyDelegateCache
    {
        private readonly MaterializedPolicyCache _materializedCache;
        private readonly ConcurrentDictionary<string, CompiledPolicyDelegate> _delegates = new(StringComparer.Ordinal);
        private long _compiledForVersion = -1;
        private long _hitCount;
        private long _missCount;
        private long _recompileCount;

        /// <summary>
        /// Initializes a new <see cref="PolicyDelegateCache"/> backed by the given materialized cache.
        /// </summary>
        /// <param name="materializedCache">The source of pre-computed effective policies and version tracking.</param>
        public PolicyDelegateCache(MaterializedPolicyCache materializedCache)
        {
            _materializedCache = materializedCache ?? throw new ArgumentNullException(nameof(materializedCache));
        }

        /// <summary>
        /// Returns the effective policy for the given feature and path via a compiled delegate.
        /// <para>
        /// Hot path: O(1) dictionary lookup + direct delegate invocation. If the materialized cache
        /// version has changed since delegates were last compiled, <see cref="InvalidateAll"/> is called
        /// first (PERF-06: change-only recompilation). Individual stale delegates are also detected and
        /// recompiled on access.
        /// </para>
        /// </summary>
        /// <param name="featureId">The feature identifier (e.g., "compression", "encryption").</param>
        /// <param name="path">The VDE path (e.g., "/myVde/container1").</param>
        /// <returns>The resolved <see cref="IEffectivePolicy"/> via direct delegate invocation.</returns>
        public IEffectivePolicy GetOrCompile(string featureId, string path)
        {
            if (featureId is null) throw new ArgumentNullException(nameof(featureId));
            if (path is null) throw new ArgumentNullException(nameof(path));

            var currentVersion = _materializedCache.CurrentVersion;

            // Check if entire cache needs invalidation due to version change
            if (currentVersion != Interlocked.Read(ref _compiledForVersion))
            {
                InvalidateAll();
            }

            var key = $"{featureId}:{path}";

            if (_delegates.TryGetValue(key, out var existing) && !existing.IsStale(currentVersion))
            {
                // Cache hit: delegate exists and is current
                Interlocked.Increment(ref _hitCount);
                return existing.Invoke();
            }

            // Cache miss or stale: compile new delegate from current snapshot
            var snapshot = _materializedCache.GetSnapshot();
            var compiled = CompiledPolicyDelegate.CompileFromSnapshot(featureId, path, snapshot);
            _delegates[key] = compiled;
            Interlocked.Increment(ref _missCount);
            return compiled.Invoke();
        }

        /// <summary>
        /// Invalidates all cached delegates and updates the compiled-for version to match the
        /// current materialized cache version. Called when the materialized cache publishes a
        /// new snapshot (PERF-06: only on policy change, not on every operation).
        /// </summary>
        public void InvalidateAll()
        {
            // Update version BEFORE clearing to prevent concurrent GetOrCompile from
            // trapping a stale delegate as if it belongs to the new version
            Interlocked.Exchange(ref _compiledForVersion, _materializedCache.CurrentVersion);
            _delegates.Clear();
            Interlocked.Increment(ref _recompileCount);
        }

        /// <summary>
        /// Invalidates a single cached delegate for the given feature and path.
        /// Used for targeted invalidation when only one policy changed, avoiding
        /// full cache rebuild.
        /// </summary>
        /// <param name="featureId">The feature identifier.</param>
        /// <param name="path">The VDE path.</param>
        public void Invalidate(string featureId, string path)
        {
            if (featureId is null) throw new ArgumentNullException(nameof(featureId));
            if (path is null) throw new ArgumentNullException(nameof(path));

            var key = $"{featureId}:{path}";
            _delegates.TryRemove(key, out _);
        }

        /// <summary>
        /// Pre-compiles delegates for all given features at the specified path, eliminating
        /// even the first-call miss after VDE open or policy materialization.
        /// <para>
        /// Gets the current snapshot once and iterates all feature IDs, compiling and storing
        /// a delegate for each. This ensures the first <see cref="GetOrCompile"/> call for these
        /// features is a cache hit.
        /// </para>
        /// </summary>
        /// <param name="featureIds">The list of feature identifiers to pre-compile delegates for.</param>
        /// <param name="path">The VDE path to compile against.</param>
        public void WarmUp(IReadOnlyList<string> featureIds, string path)
        {
            if (featureIds is null) throw new ArgumentNullException(nameof(featureIds));
            if (path is null) throw new ArgumentNullException(nameof(path));

            var snapshot = _materializedCache.GetSnapshot();

            for (var i = 0; i < featureIds.Count; i++)
            {
                var featureId = featureIds[i];
                if (featureId is null) continue;

                var key = $"{featureId}:{path}";
                var compiled = CompiledPolicyDelegate.CompileFromSnapshot(featureId, path, snapshot);
                _delegates[key] = compiled;
            }

            // Update compiled-for version to match the snapshot used for warm-up
            Interlocked.Exchange(ref _compiledForVersion, snapshot.Version);
        }

        /// <summary>
        /// Returns telemetry counters and the current number of cached delegates.
        /// </summary>
        /// <returns>
        /// A tuple containing:
        /// <list type="bullet">
        /// <item><description><c>Hits</c>: Number of cache hits (delegate found and current).</description></item>
        /// <item><description><c>Misses</c>: Number of cache misses (delegate compiled on demand).</description></item>
        /// <item><description><c>Recompiles</c>: Number of full cache invalidations triggered by version change.</description></item>
        /// <item><description><c>CachedDelegates</c>: Current number of delegates in the cache.</description></item>
        /// </list>
        /// </returns>
        public (long Hits, long Misses, long Recompiles, int CachedDelegates) GetStatistics()
        {
            return (
                Interlocked.Read(ref _hitCount),
                Interlocked.Read(ref _missCount),
                Interlocked.Read(ref _recompileCount),
                _delegates.Count);
        }
    }
}
