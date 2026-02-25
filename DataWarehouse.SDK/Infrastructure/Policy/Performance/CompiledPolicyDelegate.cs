using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy.Performance
{
    /// <summary>
    /// Typed delegate for policy checks, providing strong typing in the fast-path wiring
    /// as an alternative to <see cref="Func{T}"/>.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 76: Performance Optimization (PERF-03)")]
    public delegate IEffectivePolicy PolicyCheckDelegate(string featureId, string path);

    /// <summary>
    /// Captures a pre-resolved <see cref="IEffectivePolicy"/> as a direct <see cref="Func{TResult}"/>
    /// delegate for zero-overhead hot-path policy checks (PERF-03).
    /// <para>
    /// The "JIT compilation" is closure capture of the pre-resolved IEffectivePolicy into a direct
    /// <c>Func&lt;IEffectivePolicy&gt;</c>. This eliminates all resolution overhead: the delegate is a
    /// pointer to a method that returns a captured object. This is the standard high-performance .NET
    /// pattern for eliminating virtual dispatch and algorithm overhead on hot paths.
    /// </para>
    /// <para>
    /// Each compiled delegate is versioned against the <see cref="MaterializedPolicyCacheSnapshot"/>
    /// it was compiled from. When the materialized cache publishes a new version, all delegates
    /// become stale and must be recompiled (PERF-06: change-only recompilation).
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 76: Performance Optimization (PERF-03)")]
    public sealed class CompiledPolicyDelegate
    {
        private readonly Func<IEffectivePolicy> _compiledCheck;

        /// <summary>
        /// Gets the feature identifier this delegate is compiled for.
        /// </summary>
        public string FeatureId { get; }

        /// <summary>
        /// Gets the VDE path this delegate is compiled for.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Gets the materialized cache version this delegate was compiled from.
        /// Used for staleness detection via <see cref="IsStale"/>.
        /// </summary>
        public long SourceVersion { get; }

        /// <summary>
        /// Gets the UTC timestamp at which this delegate was compiled.
        /// </summary>
        public DateTimeOffset CompiledAt { get; }

        private CompiledPolicyDelegate(
            string featureId,
            string path,
            long sourceVersion,
            DateTimeOffset compiledAt,
            Func<IEffectivePolicy> compiledCheck)
        {
            FeatureId = featureId ?? throw new ArgumentNullException(nameof(featureId));
            Path = path ?? throw new ArgumentNullException(nameof(path));
            SourceVersion = sourceVersion;
            CompiledAt = compiledAt;
            _compiledCheck = compiledCheck ?? throw new ArgumentNullException(nameof(compiledCheck));
        }

        /// <summary>
        /// Invokes the compiled delegate, returning the pre-resolved effective policy
        /// via a single direct delegate call with zero resolution overhead.
        /// <para>
        /// This is the hot path: a single delegate call returning the captured pre-resolved policy.
        /// No async, no cascade resolution, no dictionary lookup beyond the initial delegate cache hit.
        /// </para>
        /// </summary>
        /// <returns>The pre-resolved <see cref="IEffectivePolicy"/>.</returns>
        public IEffectivePolicy Invoke() => _compiledCheck();

        /// <summary>
        /// Compiles a delegate from a <see cref="MaterializedPolicyCacheSnapshot"/> by looking up
        /// the pre-computed effective policy for the given feature and path.
        /// <para>
        /// If the snapshot contains a pre-computed policy, it is captured in a closure for direct return.
        /// If not found, a default <see cref="EffectivePolicy"/> is compiled (intensity 50, SuggestExplain,
        /// Inherit cascade, VDE level, empty chain, empty params).
        /// </para>
        /// </summary>
        /// <param name="featureId">The feature identifier to compile for.</param>
        /// <param name="path">The VDE path to compile for.</param>
        /// <param name="snapshot">The materialized snapshot to read the effective policy from.</param>
        /// <returns>A new <see cref="CompiledPolicyDelegate"/> capturing the resolved policy.</returns>
        public static CompiledPolicyDelegate CompileFromSnapshot(
            string featureId,
            string path,
            MaterializedPolicyCacheSnapshot snapshot)
        {
            if (featureId is null) throw new ArgumentNullException(nameof(featureId));
            if (path is null) throw new ArgumentNullException(nameof(path));
            if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));

            var effective = snapshot.TryGetEffective(featureId, path);
            var now = DateTimeOffset.UtcNow;

            if (effective is not null)
            {
                // Closure capture: the delegate returns the pre-resolved policy directly
                var capturedPolicy = effective;
                return new CompiledPolicyDelegate(
                    featureId,
                    path,
                    snapshot.Version,
                    now,
                    () => capturedPolicy);
            }

            // No pre-computed policy found: compile a default
            var defaultPolicy = new EffectivePolicy(
                featureId: featureId,
                effectiveIntensity: 50,
                effectiveAiAutonomy: AiAutonomyLevel.SuggestExplain,
                appliedCascade: CascadeStrategy.Inherit,
                decidedAtLevel: PolicyLevel.VDE,
                resolutionChain: Array.Empty<FeaturePolicy>(),
                mergedParameters: new Dictionary<string, string>(),
                snapshotTimestamp: snapshot.MaterializedAt);

            var capturedDefault = (IEffectivePolicy)defaultPolicy;
            return new CompiledPolicyDelegate(
                featureId,
                path,
                snapshot.Version,
                now,
                () => capturedDefault);
        }

        /// <summary>
        /// Compiles a delegate from an already-resolved <see cref="IEffectivePolicy"/>.
        /// Used when the caller already has the resolved policy and just wants delegate wrapping
        /// for consistent hot-path invocation.
        /// </summary>
        /// <param name="featureId">The feature identifier.</param>
        /// <param name="path">The VDE path.</param>
        /// <param name="effective">The pre-resolved effective policy to capture.</param>
        /// <param name="version">The materialized cache version to associate with this delegate.</param>
        /// <returns>A new <see cref="CompiledPolicyDelegate"/> capturing the provided policy.</returns>
        public static CompiledPolicyDelegate CompileFromEffective(
            string featureId,
            string path,
            IEffectivePolicy effective,
            long version)
        {
            if (featureId is null) throw new ArgumentNullException(nameof(featureId));
            if (path is null) throw new ArgumentNullException(nameof(path));
            if (effective is null) throw new ArgumentNullException(nameof(effective));

            return new CompiledPolicyDelegate(
                featureId,
                path,
                version,
                DateTimeOffset.UtcNow,
                () => effective);
        }

        /// <summary>
        /// Returns <c>true</c> if this delegate is stale relative to the given cache version,
        /// meaning the materialized cache has been updated since this delegate was compiled
        /// and the delegate must be recompiled to reflect the new policy state.
        /// </summary>
        /// <param name="currentVersion">The current materialized cache version.</param>
        /// <returns><c>true</c> if <see cref="SourceVersion"/> differs from <paramref name="currentVersion"/>.</returns>
        public bool IsStale(long currentVersion) => SourceVersion != currentVersion;
    }
}
