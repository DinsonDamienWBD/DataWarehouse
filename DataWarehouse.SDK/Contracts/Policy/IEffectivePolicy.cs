using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Contracts.Policy
{
    /// <summary>
    /// Represents a resolved policy snapshot for a given feature and context after cascade resolution.
    /// <para>
    /// An effective policy is the output of <see cref="IPolicyEngine.ResolveAsync"/> and captures the
    /// final resolved values after walking the VDE hierarchy and applying <see cref="CascadeStrategy"/> rules.
    /// The snapshot is immutable and timestamped (CASC-06 contract) so that operations in flight use
    /// a consistent view of policy state even if policies change mid-operation.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 68: Policy Engine interfaces (SDKF-01)")]
    public interface IEffectivePolicy
    {
        /// <summary>
        /// Gets the unique identifier of the feature this policy applies to (e.g., "compression", "encryption").
        /// </summary>
        string FeatureId { get; }

        /// <summary>
        /// Gets the resolved intensity level (0-100) after cascade resolution.
        /// Interpretation is feature-specific (e.g., compression ratio target, replication factor).
        /// </summary>
        int EffectiveIntensity { get; }

        /// <summary>
        /// Gets the resolved AI autonomy level after cascade resolution.
        /// Determines the level of autonomous AI action permitted for this feature.
        /// </summary>
        AiAutonomyLevel EffectiveAiAutonomy { get; }

        /// <summary>
        /// Gets the <see cref="CascadeStrategy"/> that was used to produce this resolved result.
        /// Useful for diagnostics and audit trails.
        /// </summary>
        CascadeStrategy AppliedCascade { get; }

        /// <summary>
        /// Gets the <see cref="PolicyLevel"/> at which the final decision was made.
        /// For example, if a Container-level policy overrode an Object-level policy, this returns
        /// <see cref="PolicyLevel.Container"/>.
        /// </summary>
        PolicyLevel DecidedAtLevel { get; }

        /// <summary>
        /// Gets the full chain of policies consulted during cascade resolution, ordered from
        /// most specific (e.g., Block) to least specific (e.g., VDE). Useful for debugging
        /// and understanding why a particular resolution was produced.
        /// </summary>
        IReadOnlyList<FeaturePolicy> ResolutionChain { get; }

        /// <summary>
        /// Gets custom parameters merged from the resolution chain according to cascade rules.
        /// Keys from more specific levels override keys from less specific levels when using
        /// <see cref="CascadeStrategy.Merge"/> or <see cref="CascadeStrategy.Override"/>.
        /// </summary>
        IReadOnlyDictionary<string, string> MergedParameters { get; }

        /// <summary>
        /// Gets the timestamp at which this policy snapshot was taken.
        /// Operations in flight use this snapshot timestamp to ensure consistent policy application
        /// even if policies are modified concurrently (CASC-06 contract).
        /// </summary>
        DateTimeOffset SnapshotTimestamp { get; }
    }
}
