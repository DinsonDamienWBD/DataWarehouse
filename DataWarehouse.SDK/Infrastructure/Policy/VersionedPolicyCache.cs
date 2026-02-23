using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy
{
    /// <summary>
    /// Represents an immutable, point-in-time snapshot of all cached policies at a specific version.
    /// In-flight operations hold a reference to a snapshot and are unaffected by subsequent policy changes.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 70: Versioned policy cache (CASC-06)")]
    public sealed class PolicyCacheSnapshot
    {
        /// <summary>
        /// Monotonically increasing version number for this snapshot.
        /// </summary>
        public long Version { get; }

        /// <summary>
        /// UTC timestamp at which this snapshot was created.
        /// </summary>
        public DateTimeOffset Timestamp { get; }

        /// <summary>
        /// Immutable dictionary of policies keyed by composite key "featureId:level:path".
        /// </summary>
        public IReadOnlyDictionary<string, FeaturePolicy> Policies { get; }

        /// <summary>
        /// Initializes a new <see cref="PolicyCacheSnapshot"/>.
        /// </summary>
        /// <param name="version">The version number for this snapshot.</param>
        /// <param name="timestamp">The UTC timestamp at which this snapshot was created.</param>
        /// <param name="policies">The immutable policy dictionary for this snapshot.</param>
        public PolicyCacheSnapshot(long version, DateTimeOffset timestamp, IReadOnlyDictionary<string, FeaturePolicy> policies)
        {
            Version = version;
            Timestamp = timestamp;
            Policies = policies ?? throw new ArgumentNullException(nameof(policies));
        }

        /// <summary>
        /// Attempts to get a policy from this snapshot.
        /// </summary>
        /// <param name="featureId">Feature identifier.</param>
        /// <param name="level">Hierarchy level.</param>
        /// <param name="path">VDE path.</param>
        /// <returns>The policy if found, or null.</returns>
        public FeaturePolicy? TryGetPolicy(string featureId, PolicyLevel level, string path)
        {
            var key = $"{featureId}:{level}:{path}";
            Policies.TryGetValue(key, out var policy);
            return policy;
        }
    }

    /// <summary>
    /// Double-buffered versioned cache for policy resolution that provides snapshot isolation (CASC-06).
    /// <para>
    /// Maintains two snapshots (current + previous). When policies are modified, the cache increments
    /// a monotonic version number and atomically swaps the buffer via <see cref="Interlocked.Exchange{T}"/>.
    /// The previous snapshot remains valid for any in-flight operations still referencing it.
    /// </para>
    /// <para>
    /// Thread-safe via immutable snapshots and Interlocked operations (no locks).
    /// In-flight operations call <see cref="GetSnapshot"/> at start time and use that snapshot
    /// throughout, even if policies change mid-operation. New operations get the latest snapshot.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 70: Versioned policy cache (CASC-06)")]
    public sealed class VersionedPolicyCache
    {
        private volatile PolicyCacheSnapshot _current;
        private volatile PolicyCacheSnapshot _previous;
        private long _version;

        /// <summary>
        /// Initializes a new <see cref="VersionedPolicyCache"/> with an empty snapshot at version 0.
        /// </summary>
        public VersionedPolicyCache()
        {
            var emptyPolicies = ImmutableDictionary<string, FeaturePolicy>.Empty;
            var initialSnapshot = new PolicyCacheSnapshot(0, DateTimeOffset.UtcNow, emptyPolicies);
            _current = initialSnapshot;
            _previous = initialSnapshot;
            _version = 0;
        }

        /// <summary>
        /// Gets the current version number of the cache.
        /// </summary>
        public long CurrentVersion => Interlocked.Read(ref _version);

        /// <summary>
        /// Returns the current immutable snapshot. In-flight operations should call this once
        /// at the start of resolution and use the returned snapshot throughout.
        /// </summary>
        /// <returns>The current <see cref="PolicyCacheSnapshot"/>.</returns>
        public PolicyCacheSnapshot GetSnapshot() => _current;

        /// <summary>
        /// Returns the previous snapshot, which may still be in use by in-flight operations
        /// that started before the most recent update.
        /// </summary>
        /// <returns>The previous <see cref="PolicyCacheSnapshot"/>.</returns>
        public PolicyCacheSnapshot GetPreviousSnapshot() => _previous;

        /// <summary>
        /// Publishes a new snapshot built from the provided policy store.
        /// The current snapshot becomes the previous, and the new snapshot becomes current.
        /// Uses <see cref="Interlocked.Exchange{T}"/> for lock-free atomic swap.
        /// </summary>
        /// <param name="store">The policy store to snapshot.</param>
        /// <param name="featureIds">The set of feature IDs to include in the snapshot.</param>
        /// <param name="ct">Cancellation token.</param>
        public async System.Threading.Tasks.Task UpdateFromStoreAsync(
            IPolicyStore store,
            IEnumerable<string> featureIds,
            CancellationToken ct = default)
        {
            if (store is null) throw new ArgumentNullException(nameof(store));
            if (featureIds is null) throw new ArgumentNullException(nameof(featureIds));

            var builder = ImmutableDictionary.CreateBuilder<string, FeaturePolicy>(StringComparer.Ordinal);

            foreach (var featureId in featureIds)
            {
                ct.ThrowIfCancellationRequested();
                var overrides = await store.ListOverridesAsync(featureId, ct).ConfigureAwait(false);
                foreach (var (level, path, policy) in overrides)
                {
                    var key = $"{featureId}:{level}:{path}";
                    builder[key] = policy;
                }
            }

            var newVersion = Interlocked.Increment(ref _version);
            var newSnapshot = new PolicyCacheSnapshot(newVersion, DateTimeOffset.UtcNow, builder.ToImmutable());

            // Atomic double-buffer swap: current becomes previous, new becomes current
            var oldCurrent = Interlocked.Exchange(ref _current, newSnapshot);
            Interlocked.Exchange(ref _previous, oldCurrent);
        }

        /// <summary>
        /// Publishes a new snapshot from an already-built immutable dictionary.
        /// Used when the caller has direct access to the policy data without going through the store.
        /// </summary>
        /// <param name="policies">The complete policy dictionary for the new snapshot.</param>
        public void Update(IReadOnlyDictionary<string, FeaturePolicy> policies)
        {
            if (policies is null) throw new ArgumentNullException(nameof(policies));

            var immutable = policies is ImmutableDictionary<string, FeaturePolicy> alreadyImmutable
                ? alreadyImmutable
                : policies.ToImmutableDictionary(StringComparer.Ordinal);

            var newVersion = Interlocked.Increment(ref _version);
            var newSnapshot = new PolicyCacheSnapshot(newVersion, DateTimeOffset.UtcNow, immutable);

            var oldCurrent = Interlocked.Exchange(ref _current, newSnapshot);
            Interlocked.Exchange(ref _previous, oldCurrent);
        }
    }
}
