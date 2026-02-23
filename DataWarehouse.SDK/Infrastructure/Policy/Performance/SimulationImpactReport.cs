using System;
using System.Collections.Generic;
using System.Linq;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy.Performance
{
    /// <summary>
    /// Severity classification for the impact of a hypothetical policy change on a feature.
    /// Used by <see cref="SimulationImpactReport"/> to prioritize operator attention.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 76: Performance Optimization (PADV-02)")]
    public enum ImpactSeverity
    {
        /// <summary>No change detected between current and hypothetical policies.</summary>
        None = 0,

        /// <summary>Minor parameter change within the same tier (e.g., intensity +/-5 points).</summary>
        Low = 1,

        /// <summary>Intensity change greater than 20 points or AI autonomy level changed.</summary>
        Medium = 2,

        /// <summary>Cascade strategy changed or deployment tier would change.</summary>
        High = 3,

        /// <summary>Security downgrade detected (lower encryption, relaxed access control, reduced key management).</summary>
        Critical = 4
    }

    /// <summary>
    /// Impact assessment for a single feature comparing current vs hypothetical policy state.
    /// Part of the <see cref="SimulationImpactReport"/> produced by the policy simulation sandbox.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 76: Performance Optimization (PADV-02)")]
    public sealed record FeatureImpact
    {
        /// <summary>
        /// The feature identifier being assessed (e.g., "encryption", "compression").
        /// </summary>
        public required string FeatureId { get; init; }

        /// <summary>
        /// The check timing classification for this feature from <see cref="CheckClassificationTable"/>.
        /// Determines when this feature check is evaluated during the VDE lifecycle.
        /// </summary>
        public required CheckTiming Timing { get; init; }

        /// <summary>
        /// The current effective policy resolved from the live engine.
        /// </summary>
        public required IEffectivePolicy CurrentPolicy { get; init; }

        /// <summary>
        /// The hypothetical effective policy resolved from the sandbox engine with proposed changes.
        /// </summary>
        public required IEffectivePolicy HypotheticalPolicy { get; init; }

        /// <summary>
        /// The difference in intensity between hypothetical and current policies.
        /// Positive means hypothetical is more intense; negative means less intense.
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
        /// The computed impact severity for this feature change, assigned by
        /// the sandbox's severity classification logic.
        /// </summary>
        public required ImpactSeverity Severity { get; init; }
    }

    /// <summary>
    /// Projects the latency impact of a hypothetical policy change based on deployment tier changes.
    /// Latency estimates are based on the three-tier fast-path classification (PERF-04).
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 76: Performance Optimization (PADV-02)")]
    public sealed record LatencyProjection
    {
        /// <summary>
        /// The current deployment tier classification for the VDE.
        /// </summary>
        public required DeploymentTier CurrentTier { get; init; }

        /// <summary>
        /// The projected deployment tier after applying hypothetical policies.
        /// May change if overrides are added or removed at different hierarchy levels.
        /// </summary>
        public required DeploymentTier ProjectedTier { get; init; }

        /// <summary>
        /// Estimated per-operation latency in nanoseconds under the current tier.
        /// VdeOnly=0ns, ContainerStop=20ns, FullCascade=200ns.
        /// </summary>
        public required double CurrentEstimatedNs { get; init; }

        /// <summary>
        /// Estimated per-operation latency in nanoseconds under the projected tier.
        /// </summary>
        public required double ProjectedEstimatedNs { get; init; }

        /// <summary>
        /// The absolute change in estimated latency (projected minus current).
        /// Positive means slower; negative means faster.
        /// </summary>
        public double LatencyDeltaNs => ProjectedEstimatedNs - CurrentEstimatedNs;

        /// <summary>
        /// Human-readable summary of the latency impact direction.
        /// </summary>
        public string LatencyImpact => LatencyDeltaNs switch
        {
            0 => "No change",
            < 0 => "Improved",
            _ => "Degraded"
        };
    }

    /// <summary>
    /// Projects the throughput impact of a hypothetical policy change based on average intensity changes.
    /// Higher average intensity generally means more processing overhead per operation.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 76: Performance Optimization (PADV-02)")]
    public sealed record ThroughputProjection
    {
        /// <summary>
        /// Average intensity across all features under the current policy configuration.
        /// </summary>
        public required int CurrentIntensityAverage { get; init; }

        /// <summary>
        /// Average intensity across all features under the hypothetical policy configuration.
        /// </summary>
        public required int ProjectedIntensityAverage { get; init; }

        /// <summary>
        /// Human-readable summary of the throughput impact.
        /// A difference of more than 10 points in either direction is considered significant.
        /// </summary>
        public string ThroughputImpact => ProjectedIntensityAverage switch
        {
            var p when p > CurrentIntensityAverage + 10 => "Higher overhead (more processing)",
            var p when p < CurrentIntensityAverage - 10 => "Lower overhead (less processing)",
            _ => "Minimal change"
        };
    }

    /// <summary>
    /// Projects the storage impact of a hypothetical policy change based on compression and replication
    /// intensity changes. Lower compression or higher replication increases storage usage.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 76: Performance Optimization (PADV-02)")]
    public sealed record StorageProjection
    {
        /// <summary>
        /// Current compression feature intensity (higher = more compression = less storage).
        /// </summary>
        public required int CurrentCompressionIntensity { get; init; }

        /// <summary>
        /// Projected compression feature intensity under hypothetical policies.
        /// </summary>
        public required int ProjectedCompressionIntensity { get; init; }

        /// <summary>
        /// Current replication feature intensity (higher = more replicas = more storage).
        /// </summary>
        public required int CurrentReplicationIntensity { get; init; }

        /// <summary>
        /// Projected replication feature intensity under hypothetical policies.
        /// </summary>
        public required int ProjectedReplicationIntensity { get; init; }

        /// <summary>
        /// Human-readable summary of the storage impact.
        /// Decreased compression or increased replication leads to higher storage usage.
        /// </summary>
        public string StorageImpact
        {
            get
            {
                var compressionDecreased = ProjectedCompressionIntensity < CurrentCompressionIntensity;
                var replicationIncreased = ProjectedReplicationIntensity > CurrentReplicationIntensity;

                if (compressionDecreased && replicationIncreased)
                    return "Increased storage usage (less compression + more replication)";
                if (compressionDecreased)
                    return "Increased storage usage (less compression)";
                if (replicationIncreased)
                    return "Increased storage usage (more replication)";

                var compressionIncreased = ProjectedCompressionIntensity > CurrentCompressionIntensity;
                var replicationDecreased = ProjectedReplicationIntensity < CurrentReplicationIntensity;

                if (compressionIncreased && replicationDecreased)
                    return "Decreased storage usage (more compression + less replication)";
                if (compressionIncreased)
                    return "Decreased storage usage (more compression)";
                if (replicationDecreased)
                    return "Decreased storage usage (less replication)";

                return "No significant storage impact";
            }
        }
    }

    /// <summary>
    /// Projects the compliance impact of a hypothetical policy change by tracking which
    /// security-relevant features have been upgraded or downgraded.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 76: Performance Optimization (PADV-02)")]
    public sealed record ComplianceProjection
    {
        /// <summary>
        /// Security-relevant features at their current intensity levels (e.g., "encryption:70", "auth_model:80").
        /// </summary>
        public required IReadOnlyList<string> CurrentSecurityFeatures { get; init; }

        /// <summary>
        /// Security-relevant features at their projected intensity levels under hypothetical policies.
        /// </summary>
        public required IReadOnlyList<string> ProjectedSecurityFeatures { get; init; }

        /// <summary>
        /// Features where the security posture has decreased (intensity lowered or feature removed).
        /// </summary>
        public required IReadOnlyList<string> Downgrades { get; init; }

        /// <summary>
        /// Features where the security posture has improved (intensity raised or feature added).
        /// </summary>
        public required IReadOnlyList<string> Upgrades { get; init; }

        /// <summary>
        /// Returns <c>true</c> if any security-relevant feature has been downgraded.
        /// </summary>
        public bool HasSecurityDowngrade => Downgrades.Count > 0;
    }

    /// <summary>
    /// Comprehensive impact report produced by the policy simulation sandbox comparing
    /// current policy state against hypothetical changes across latency, throughput, storage,
    /// and compliance dimensions (PADV-02).
    /// <para>
    /// The report is immutable after creation except for the approval workflow properties
    /// (<see cref="IsApproved"/> and <see cref="ApprovalReason"/>), which are set by the operator
    /// after reviewing the impact analysis.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 76: Performance Optimization (PADV-02)")]
    public sealed class SimulationImpactReport
    {
        /// <summary>
        /// UTC timestamp at which this impact report was generated.
        /// </summary>
        public required DateTimeOffset GeneratedAt { get; init; }

        /// <summary>
        /// The VDE path that was simulated.
        /// </summary>
        public required string VdePath { get; init; }

        /// <summary>
        /// Per-feature impact assessments comparing current vs hypothetical policies.
        /// </summary>
        public required IReadOnlyList<FeatureImpact> FeatureImpacts { get; init; }

        /// <summary>
        /// Projected latency impact based on deployment tier changes.
        /// </summary>
        public required LatencyProjection Latency { get; init; }

        /// <summary>
        /// Projected throughput impact based on average intensity changes.
        /// </summary>
        public required ThroughputProjection Throughput { get; init; }

        /// <summary>
        /// Projected storage impact based on compression and replication changes.
        /// </summary>
        public required StorageProjection Storage { get; init; }

        /// <summary>
        /// Projected compliance impact based on security feature changes.
        /// </summary>
        public required ComplianceProjection Compliance { get; init; }

        /// <summary>
        /// Number of features where at least one property (intensity, AI autonomy, or cascade) changed.
        /// </summary>
        public int FeaturesAffected => FeatureImpacts.Count(f =>
            f.IntensityDelta != 0 || f.AiAutonomyChanged || f.CascadeChanged);

        /// <summary>
        /// The highest severity across all feature impacts. Returns <see cref="ImpactSeverity.None"/>
        /// if no features were affected.
        /// </summary>
        public ImpactSeverity OverallSeverity => FeatureImpacts.Count > 0
            ? FeatureImpacts.Max(f => f.Severity)
            : ImpactSeverity.None;

        /// <summary>
        /// Mutable approval flag set by the operator after reviewing the impact report.
        /// Must be set to <c>true</c> before the hypothetical changes can be applied to the live engine.
        /// </summary>
        public bool IsApproved { get; set; }

        /// <summary>
        /// Mutable approval notes provided by the operator when approving or rejecting the changes.
        /// </summary>
        public string? ApprovalReason { get; set; }
    }
}
