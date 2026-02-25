using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.ChaosVaccination
{
    /// <summary>
    /// Defines the type of fault to inject during a chaos experiment.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public enum FaultType
    {
        /// <summary>Simulates a network partition between components.</summary>
        NetworkPartition,

        /// <summary>Simulates disk I/O failure or disk unavailability.</summary>
        DiskFailure,

        /// <summary>Simulates an abrupt node crash.</summary>
        NodeCrash,

        /// <summary>Introduces artificial latency into operations.</summary>
        LatencySpike,

        /// <summary>Simulates high memory pressure conditions.</summary>
        MemoryPressure,

        /// <summary>Simulates CPU saturation scenarios.</summary>
        CpuSaturation,

        /// <summary>Exhausts the connection pool to test pool recovery.</summary>
        ConnectionPoolExhaustion,

        /// <summary>Simulates DNS resolution failures.</summary>
        DnsFailure,

        /// <summary>Simulates certificate expiry or TLS handshake failure.</summary>
        CertificateExpiry,

        /// <summary>Simulates a partition in the message bus infrastructure.</summary>
        MessageBusPartition,

        /// <summary>Simulates an abrupt plugin crash.</summary>
        PluginCrash,

        /// <summary>Simulates gradual kernel-level degradation.</summary>
        KernelDegradation,

        /// <summary>Simulates data corruption in storage layers.</summary>
        StorageCorruption,

        /// <summary>Simulates replication lag between nodes.</summary>
        ReplicationLag,

        /// <summary>User-defined custom fault type.</summary>
        Custom
    }

    /// <summary>
    /// Severity level of a fault injection.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public enum FaultSeverity
    {
        /// <summary>Minimal impact, localized effect.</summary>
        Low,

        /// <summary>Moderate impact, may affect dependent components.</summary>
        Medium,

        /// <summary>Significant impact, affects multiple components.</summary>
        High,

        /// <summary>Severe impact, affects entire subsystem.</summary>
        Critical,

        /// <summary>Total failure scenario, system-wide impact.</summary>
        Catastrophic
    }

    /// <summary>
    /// Defines the maximum allowed blast radius for a chaos experiment.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public enum BlastRadiusLevel
    {
        /// <summary>Confined to a single strategy within a plugin.</summary>
        SingleStrategy,

        /// <summary>Confined to a single plugin.</summary>
        SinglePlugin,

        /// <summary>Affects all plugins in a category.</summary>
        PluginCategory,

        /// <summary>Affects a single node.</summary>
        SingleNode,

        /// <summary>Affects a group of nodes.</summary>
        NodeGroup,

        /// <summary>Affects the entire cluster.</summary>
        Cluster
    }

    /// <summary>
    /// Status of a chaos experiment lifecycle.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public enum ExperimentStatus
    {
        /// <summary>Experiment is defined but not yet started.</summary>
        Pending,

        /// <summary>Experiment is currently executing.</summary>
        Running,

        /// <summary>Experiment completed successfully.</summary>
        Completed,

        /// <summary>Experiment failed during execution.</summary>
        Failed,

        /// <summary>Experiment was manually or automatically aborted.</summary>
        Aborted,

        /// <summary>Experiment result is under quarantine for review.</summary>
        Quarantined
    }

    /// <summary>
    /// Defines a chaos experiment to be executed against the system.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public record ChaosExperiment
    {
        /// <summary>
        /// Unique identifier for the experiment.
        /// </summary>
        public required string Id { get; init; }

        /// <summary>
        /// Human-readable name for the experiment.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Detailed description of what this experiment tests.
        /// </summary>
        public required string Description { get; init; }

        /// <summary>
        /// The type of fault to inject.
        /// </summary>
        public required FaultType FaultType { get; init; }

        /// <summary>
        /// The severity level of the injected fault.
        /// </summary>
        public required FaultSeverity Severity { get; init; }

        /// <summary>
        /// Maximum allowed blast radius for this experiment.
        /// </summary>
        public required BlastRadiusLevel MaxBlastRadius { get; init; }

        /// <summary>
        /// Optional list of specific plugin IDs to target. Null means experiment determines targets.
        /// </summary>
        public string[]? TargetPluginIds { get; init; }

        /// <summary>
        /// Optional list of specific node IDs to target. Null means experiment determines targets.
        /// </summary>
        public string[]? TargetNodeIds { get; init; }

        /// <summary>
        /// Maximum allowed duration for the experiment.
        /// </summary>
        public required TimeSpan DurationLimit { get; init; }

        /// <summary>
        /// Additional fault-specific parameters.
        /// </summary>
        public IReadOnlyDictionary<string, object> Parameters { get; init; } = new Dictionary<string, object>();

        /// <summary>
        /// Tags for categorization and filtering.
        /// </summary>
        public string[] Tags { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Safety checks that must pass before the experiment executes.
        /// </summary>
        public SafetyCheck[] SafetyChecks { get; init; } = Array.Empty<SafetyCheck>();
    }

    /// <summary>
    /// Result of a completed (or aborted) chaos experiment.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public record ChaosExperimentResult
    {
        /// <summary>
        /// The ID of the experiment that produced this result.
        /// </summary>
        public required string ExperimentId { get; init; }

        /// <summary>
        /// The final status of the experiment.
        /// </summary>
        public required ExperimentStatus Status { get; init; }

        /// <summary>
        /// When the experiment started.
        /// </summary>
        public required DateTimeOffset StartedAt { get; init; }

        /// <summary>
        /// When the experiment completed. Null if still running or aborted before completion.
        /// </summary>
        public DateTimeOffset? CompletedAt { get; init; }

        /// <summary>
        /// The actual blast radius observed during the experiment.
        /// </summary>
        public required BlastRadiusLevel ActualBlastRadius { get; init; }

        /// <summary>
        /// IDs of plugins that were affected by the experiment.
        /// </summary>
        public string[] AffectedPlugins { get; init; } = Array.Empty<string>();

        /// <summary>
        /// IDs of nodes that were affected by the experiment.
        /// </summary>
        public string[] AffectedNodes { get; init; } = Array.Empty<string>();

        /// <summary>
        /// The fault signature derived from this experiment, if one was generated.
        /// </summary>
        public FaultSignature? FaultSignature { get; init; }

        /// <summary>
        /// Time in milliseconds for the system to recover after fault injection. Null if not measured.
        /// </summary>
        public long? RecoveryTimeMs { get; init; }

        /// <summary>
        /// Quantitative metrics collected during the experiment.
        /// </summary>
        public IReadOnlyDictionary<string, double> Metrics { get; init; } = new Dictionary<string, double>();

        /// <summary>
        /// Human-readable observations recorded during the experiment.
        /// </summary>
        public string[] Observations { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Represents a unique signature for a type of fault, used by the immune response system
    /// to recognize previously encountered faults.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public record FaultSignature
    {
        /// <summary>
        /// Unique hash identifying this fault signature.
        /// </summary>
        public required string Hash { get; init; }

        /// <summary>
        /// The type of fault this signature represents.
        /// </summary>
        public required FaultType FaultType { get; init; }

        /// <summary>
        /// A pattern description used to match future faults against this signature.
        /// </summary>
        public required string Pattern { get; init; }

        /// <summary>
        /// Components affected by this fault pattern.
        /// </summary>
        public string[] AffectedComponents { get; init; } = Array.Empty<string>();

        /// <summary>
        /// The severity of this fault pattern.
        /// </summary>
        public required FaultSeverity Severity { get; init; }

        /// <summary>
        /// When this fault pattern was first observed.
        /// </summary>
        public required DateTimeOffset FirstObserved { get; init; }

        /// <summary>
        /// How many times this fault pattern has been observed.
        /// </summary>
        public required int ObservationCount { get; init; }
    }

    /// <summary>
    /// A safety check that must pass before a chaos experiment can execute.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public record SafetyCheck
    {
        /// <summary>
        /// Name of the safety check.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Description of what this check validates.
        /// </summary>
        public required string Description { get; init; }

        /// <summary>
        /// The check action to execute. Returns true if the check passes.
        /// </summary>
        public required Func<CancellationToken, Task<bool>> CheckAction { get; init; }

        /// <summary>
        /// Whether the experiment must be aborted if this check fails.
        /// </summary>
        public bool IsRequired { get; init; } = true;
    }

    /// <summary>
    /// Global configuration options for the chaos vaccination system.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public record ChaosVaccinationOptions
    {
        /// <summary>
        /// Whether chaos vaccination is enabled. Default: false (opt-in).
        /// </summary>
        public bool Enabled { get; init; } = false;

        /// <summary>
        /// Maximum number of experiments that can run concurrently. Default: 1.
        /// </summary>
        public int MaxConcurrentExperiments { get; init; } = 1;

        /// <summary>
        /// The global blast radius limit that no experiment can exceed. Default: SinglePlugin.
        /// </summary>
        public BlastRadiusLevel GlobalBlastRadiusLimit { get; init; } = BlastRadiusLevel.SinglePlugin;

        /// <summary>
        /// When enabled, experiments are constrained to the safest possible execution path. Default: true.
        /// </summary>
        public bool SafeMode { get; init; } = true;

        /// <summary>
        /// Whether experiments with Critical or Catastrophic severity require manual approval. Default: true.
        /// </summary>
        public bool RequireApprovalForCritical { get; init; } = true;

        /// <summary>
        /// Whether to automatically quarantine failed experiment results for review. Default: true.
        /// </summary>
        public bool QuarantineOnFailure { get; init; } = true;
    }
}
