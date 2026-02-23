using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy
{
    /// <summary>
    /// Concrete, immutable implementation of <see cref="IEffectivePolicy"/> representing a resolved
    /// policy snapshot for a given feature and context after cascade resolution.
    /// <para>
    /// Instances are created by the PolicyResolutionEngine and are thread-safe
    /// since all properties are read-only after construction. The <see cref="SnapshotTimestamp"/>
    /// guarantees that operations in flight use a consistent view (CASC-06 contract).
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 70: Cascade engine (CASC-01)")]
    public sealed class EffectivePolicy : IEffectivePolicy
    {
        /// <summary>
        /// Initializes a new instance of <see cref="EffectivePolicy"/> with all resolved values.
        /// </summary>
        /// <param name="featureId">Unique identifier of the feature this policy applies to.</param>
        /// <param name="effectiveIntensity">Resolved intensity level (0-100) after cascade resolution.</param>
        /// <param name="effectiveAiAutonomy">Resolved AI autonomy level after cascade resolution.</param>
        /// <param name="appliedCascade">The cascade strategy that produced this result.</param>
        /// <param name="decidedAtLevel">The hierarchy level at which the final decision was made.</param>
        /// <param name="resolutionChain">Full chain of policies consulted, ordered most-specific first.</param>
        /// <param name="mergedParameters">Custom parameters merged from the resolution chain.</param>
        /// <param name="snapshotTimestamp">Timestamp at which this snapshot was taken.</param>
        public EffectivePolicy(
            string featureId,
            int effectiveIntensity,
            AiAutonomyLevel effectiveAiAutonomy,
            CascadeStrategy appliedCascade,
            PolicyLevel decidedAtLevel,
            IReadOnlyList<FeaturePolicy> resolutionChain,
            IReadOnlyDictionary<string, string> mergedParameters,
            DateTimeOffset snapshotTimestamp)
        {
            FeatureId = featureId ?? throw new ArgumentNullException(nameof(featureId));
            EffectiveIntensity = effectiveIntensity;
            EffectiveAiAutonomy = effectiveAiAutonomy;
            AppliedCascade = appliedCascade;
            DecidedAtLevel = decidedAtLevel;
            ResolutionChain = resolutionChain ?? throw new ArgumentNullException(nameof(resolutionChain));
            MergedParameters = mergedParameters ?? throw new ArgumentNullException(nameof(mergedParameters));
            SnapshotTimestamp = snapshotTimestamp;
        }

        /// <inheritdoc />
        public string FeatureId { get; }

        /// <inheritdoc />
        public int EffectiveIntensity { get; }

        /// <inheritdoc />
        public AiAutonomyLevel EffectiveAiAutonomy { get; }

        /// <inheritdoc />
        public CascadeStrategy AppliedCascade { get; }

        /// <inheritdoc />
        public PolicyLevel DecidedAtLevel { get; }

        /// <inheritdoc />
        public IReadOnlyList<FeaturePolicy> ResolutionChain { get; }

        /// <inheritdoc />
        public IReadOnlyDictionary<string, string> MergedParameters { get; }

        /// <inheritdoc />
        public DateTimeOffset SnapshotTimestamp { get; }
    }
}
