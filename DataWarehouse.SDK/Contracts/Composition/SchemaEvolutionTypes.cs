using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Contracts.Composition
{
    /// <summary>
    /// Represents a proposed schema evolution detected by the self-evolving schema engine.
    /// Immutable record for API contract safety (AD-05).
    /// </summary>
    [SdkCompatibility("3.0.0")]
    public record SchemaEvolutionProposal
    {
        /// <summary>
        /// Unique identifier for this proposal (GUID).
        /// </summary>
        public required string ProposalId { get; init; }

        /// <summary>
        /// The schema that would be evolved.
        /// </summary>
        public required string SchemaId { get; init; }

        /// <summary>
        /// List of detected field changes in this proposal.
        /// </summary>
        public required IReadOnlyList<FieldChange> DetectedChanges { get; init; }

        /// <summary>
        /// Percentage of ingested records that contain the new pattern.
        /// Threshold for triggering automatic proposals.
        /// </summary>
        public required double ChangePercentage { get; init; }

        /// <summary>
        /// Timestamp when the pattern was detected.
        /// </summary>
        public required DateTimeOffset DetectedAt { get; init; }

        /// <summary>
        /// Current decision status for this proposal.
        /// Defaults to Pending.
        /// </summary>
        public SchemaEvolutionDecision Decision { get; init; } = SchemaEvolutionDecision.Pending;

        /// <summary>
        /// Username who approved/rejected this proposal.
        /// Null for auto-approved proposals.
        /// </summary>
        public string? ApprovedBy { get; init; }

        /// <summary>
        /// Timestamp when the decision was made.
        /// Null if still pending.
        /// </summary>
        public DateTimeOffset? DecidedAt { get; init; }
    }

    /// <summary>
    /// Represents a single field change detected in a schema evolution proposal.
    /// </summary>
    [SdkCompatibility("3.0.0")]
    public record FieldChange
    {
        /// <summary>
        /// Name of the field being changed.
        /// </summary>
        public required string FieldName { get; init; }

        /// <summary>
        /// Type of change being proposed.
        /// </summary>
        public required FieldChangeType ChangeType { get; init; }

        /// <summary>
        /// Original data type before the change.
        /// Null for Added fields.
        /// </summary>
        public string? OldType { get; init; }

        /// <summary>
        /// New data type after the change.
        /// Null for Removed fields.
        /// </summary>
        public string? NewType { get; init; }

        /// <summary>
        /// Default value to use for the field.
        /// Required for Added fields to maintain forward compatibility.
        /// </summary>
        public object? DefaultValue { get; init; }
    }

    /// <summary>
    /// Types of field changes that can occur in schema evolution.
    /// </summary>
    [SdkCompatibility("3.0.0")]
    public enum FieldChangeType
    {
        /// <summary>New field added to the schema.</summary>
        Added,

        /// <summary>Field removed from the schema.</summary>
        Removed,

        /// <summary>Field type widened (e.g., int to long).</summary>
        TypeWidened,

        /// <summary>Field type changed incompatibly.</summary>
        TypeChanged,

        /// <summary>Default value changed for a field.</summary>
        DefaultChanged
    }

    /// <summary>
    /// Decision status for a schema evolution proposal.
    /// </summary>
    [SdkCompatibility("3.0.0")]
    public enum SchemaEvolutionDecision
    {
        /// <summary>Proposal is pending review.</summary>
        Pending,

        /// <summary>Proposal was automatically approved by forward-compatibility check.</summary>
        AutoApproved,

        /// <summary>Proposal was manually approved by a user.</summary>
        ManuallyApproved,

        /// <summary>Proposal was rejected.</summary>
        Rejected
    }

    /// <summary>
    /// Configuration for the schema evolution engine.
    /// </summary>
    [SdkCompatibility("3.0.0")]
    public record SchemaEvolutionEngineConfig
    {
        /// <summary>
        /// Percentage threshold for triggering schema evolution proposals.
        /// Default: 10% of records must contain new pattern.
        /// </summary>
        public double ChangeThresholdPercent { get; init; } = 10.0;

        /// <summary>
        /// Whether to automatically approve forward-compatible schema changes.
        /// Default: false (requires manual approval).
        /// </summary>
        public bool AutoApproveForwardCompatible { get; init; } = false;

        /// <summary>
        /// Interval for pattern detection checks.
        /// Default: 5 minutes.
        /// </summary>
        public TimeSpan DetectionInterval { get; init; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Maximum number of pending proposals to retain in memory.
        /// Default: 100. Bounded per Phase 23 memory safety.
        /// </summary>
        public int MaxPendingProposals { get; init; } = 100;

        /// <summary>
        /// Maximum number of field changes allowed in a single proposal.
        /// Default: 50. Prevents unbounded memory growth.
        /// </summary>
        public int MaxFieldChangesPerProposal { get; init; } = 50;
    }
}
