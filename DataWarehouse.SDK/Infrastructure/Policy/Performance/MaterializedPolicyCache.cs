using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy.Performance
{
    /// <summary>
    /// Immutable, point-in-time snapshot of pre-computed effective policies for all registered features.
    /// <para>
    /// Each snapshot is created by the PolicyMaterializationEngine at VDE open time (PERF-01)
    /// or on policy change notification (PERF-06). Once published, the snapshot is immutable and safe
    /// to read from any thread without synchronization. Lookup is O(1) via dictionary key "featureId:path".
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 76: Performance Optimization (PERF-01, PERF-06)")]
    public sealed class MaterializedPolicyCacheSnapshot
    {
        /// <summary>
        /// Monotonically increasing version number for this snapshot.
        /// </summary>
        public long Version { get; }

        /// <summary>
        /// UTC timestamp at which this snapshot was materialized.
        /// </summary>
        public DateTimeOffset MaterializedAt { get; }

        /// <summary>
        /// Immutable dictionary of pre-computed effective policies keyed by composite key "featureId:path".
        /// </summary>
        public IReadOnlyDictionary<string, IEffectivePolicy> EffectivePolicies { get; }

        /// <summary>
        /// Initializes a new <see cref="MaterializedPolicyCacheSnapshot"/>.
        /// </summary>
        /// <param name="version">Monotonically increasing version number.</param>
        /// <param name="materializedAt">UTC timestamp at which this snapshot was built.</param>
        /// <param name="effectivePolicies">Pre-computed effective policies keyed by "featureId:path".</param>
        public MaterializedPolicyCacheSnapshot(
            long version,
            DateTimeOffset materializedAt,
            IReadOnlyDictionary<string, IEffectivePolicy> effectivePolicies)
        {
            Version = version;
            MaterializedAt = materializedAt;
            EffectivePolicies = effectivePolicies ?? throw new ArgumentNullException(nameof(effectivePolicies));
        }

        /// <summary>
        /// Attempts to retrieve the pre-computed effective policy for a given feature and path.
        /// </summary>
        /// <param name="featureId">The feature identifier (e.g., "compression", "encryption").</param>
        /// <param name="path">The VDE path (e.g., "/myVde/container1").</param>
        /// <returns>The pre-computed <see cref="IEffectivePolicy"/> if found; otherwise <c>null</c>.</returns>
        public IEffectivePolicy? TryGetEffective(string featureId, string path)
        {
            var key = $"{featureId}:{path}";
            EffectivePolicies.TryGetValue(key, out var policy);
            return policy;
        }

        /// <summary>
        /// Returns <c>true</c> if a pre-computed effective policy exists for the given feature and path.
        /// O(1) dictionary key lookup.
        /// </summary>
        /// <param name="featureId">The feature identifier.</param>
        /// <param name="path">The VDE path.</param>
        /// <returns><c>true</c> if the key exists in the snapshot; otherwise <c>false</c>.</returns>
        public bool HasPrecomputed(string featureId, string path)
        {
            var key = $"{featureId}:{path}";
            return EffectivePolicies.ContainsKey(key);
        }
    }

    /// <summary>
    /// Double-buffered cache holding pre-computed effective policies for zero cold-start policy lookups (PERF-01).
    /// <para>
    /// Maintains two snapshots (current + previous) using the same <see cref="Interlocked.Exchange{T}"/>
    /// double-buffer pattern as <see cref="VersionedPolicyCache"/>. When new policies are materialized,
    /// the entire snapshot is atomically swapped without blocking readers. The previous snapshot remains
    /// valid for any in-flight operations still referencing it.
    /// </para>
    /// <para>
    /// Thread-safe via immutable snapshots and Interlocked operations (no locks).
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 76: Performance Optimization (PERF-01, PERF-06)")]
    public sealed class MaterializedPolicyCache
    {
        private volatile MaterializedPolicyCacheSnapshot _current;
        private volatile MaterializedPolicyCacheSnapshot _previous;
        private long _version;

        /// <summary>
        /// Initializes a new <see cref="MaterializedPolicyCache"/> with an empty snapshot at version 0.
        /// </summary>
        public MaterializedPolicyCache()
        {
            var emptyPolicies = ImmutableDictionary<string, IEffectivePolicy>.Empty;
            var initialSnapshot = new MaterializedPolicyCacheSnapshot(0, DateTimeOffset.UtcNow, emptyPolicies);
            _current = initialSnapshot;
            _previous = initialSnapshot;
            _version = 0;
        }

        /// <summary>
        /// Returns the current immutable snapshot. Callers should capture the returned reference
        /// at the start of an operation and use it throughout for consistent reads.
        /// </summary>
        /// <returns>The current <see cref="MaterializedPolicyCacheSnapshot"/>.</returns>
        public MaterializedPolicyCacheSnapshot GetSnapshot() => _current;

        /// <summary>
        /// Returns the previous snapshot, which may still be in use by in-flight operations
        /// that started before the most recent publish.
        /// </summary>
        /// <returns>The previous <see cref="MaterializedPolicyCacheSnapshot"/>.</returns>
        public MaterializedPolicyCacheSnapshot GetPreviousSnapshot() => _previous;

        /// <summary>
        /// Gets the current version number of the cache (monotonically increasing).
        /// </summary>
        public long CurrentVersion => Interlocked.Read(ref _version);

        /// <summary>
        /// Publishes a new set of pre-computed effective policies into the cache via atomic double-buffer swap.
        /// <para>
        /// The current snapshot becomes the previous, and the new snapshot (built from the provided
        /// dictionary) becomes current. Readers on the old snapshot are unaffected.
        /// </para>
        /// </summary>
        /// <param name="effectivePolicies">
        /// Pre-computed effective policies keyed by "featureId:path". Converted to
        /// <see cref="ImmutableDictionary{TKey,TValue}"/> if not already immutable.
        /// </param>
        public void Publish(IReadOnlyDictionary<string, IEffectivePolicy> effectivePolicies)
        {
            if (effectivePolicies is null)
                throw new ArgumentNullException(nameof(effectivePolicies));

            var immutable = effectivePolicies is ImmutableDictionary<string, IEffectivePolicy> alreadyImmutable
                ? alreadyImmutable
                : effectivePolicies.ToImmutableDictionary(StringComparer.Ordinal);

            var newVersion = Interlocked.Increment(ref _version);
            var newSnapshot = new MaterializedPolicyCacheSnapshot(newVersion, DateTimeOffset.UtcNow, immutable);

            // Atomic double-buffer swap: current becomes previous, new becomes current
            var oldCurrent = Interlocked.Exchange(ref _current, newSnapshot);
            Interlocked.Exchange(ref _previous, oldCurrent);
        }

        /// <summary>
        /// Returns <c>true</c> if the current snapshot was materialized before the given policy change time,
        /// indicating that the cache is stale and should be re-materialized (PERF-06).
        /// </summary>
        /// <param name="policyLastChanged">The timestamp of the most recent policy change.</param>
        /// <returns><c>true</c> if the cache is stale; otherwise <c>false</c>.</returns>
        public bool IsStale(DateTimeOffset policyLastChanged)
        {
            return _current.MaterializedAt < policyLastChanged;
        }
    }
}
