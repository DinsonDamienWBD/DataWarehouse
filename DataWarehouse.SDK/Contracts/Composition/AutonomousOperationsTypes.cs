using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Contracts.Composition;

/// <summary>
/// Severity level for observability alerts.
/// </summary>
[SdkCompatibility("3.0.0")]
public enum AlertSeverity
{
    /// <summary>Informational alert.</summary>
    Info,

    /// <summary>Warning condition.</summary>
    Warning,

    /// <summary>Critical condition requiring attention.</summary>
    Critical,

    /// <summary>Fatal error condition.</summary>
    Fatal
}

/// <summary>
/// Type of remediation action to execute.
/// </summary>
[SdkCompatibility("3.0.0")]
public enum RemediationActionType
{
    /// <summary>Trigger self-healing storage operations.</summary>
    SelfHeal,

    /// <summary>Trigger automatic data tiering.</summary>
    AutoTier,

    /// <summary>Rebalance workloads based on data gravity.</summary>
    DataGravityRebalance,

    /// <summary>Redistribute replicas across nodes.</summary>
    ReplicaRedistribute,

    /// <summary>Custom remediation action.</summary>
    Custom
}

/// <summary>
/// Outcome of a remediation action attempt.
/// </summary>
[SdkCompatibility("3.0.0")]
public enum RemediationOutcome
{
    /// <summary>Remediation completed successfully.</summary>
    Success,

    /// <summary>Remediation partially succeeded.</summary>
    PartialSuccess,

    /// <summary>Remediation failed.</summary>
    Failed,

    /// <summary>Remediation was skipped (e.g., cooldown active).</summary>
    Skipped,

    /// <summary>Remediation exceeded timeout.</summary>
    TimedOut
}

/// <summary>
/// Defines conditions that trigger a remediation action.
/// </summary>
[SdkCompatibility("3.0.0")]
public sealed record AlertCondition
{
    /// <summary>
    /// Pattern to match against alert topics (e.g., "storage.node.unhealthy", "storage.capacity.*").
    /// </summary>
    public required string AlertPattern { get; init; }

    /// <summary>
    /// Minimum severity level required to trigger this condition.
    /// </summary>
    public AlertSeverity MinSeverity { get; init; } = AlertSeverity.Warning;

    /// <summary>
    /// Optional label matchers for additional filtering (e.g., {"component": "storage"}).
    /// </summary>
    public IReadOnlyDictionary<string, string>? LabelMatchers { get; init; }
}

/// <summary>
/// Defines a remediation action to execute when conditions are met.
/// </summary>
[SdkCompatibility("3.0.0")]
public sealed record RemediationAction
{
    /// <summary>
    /// Type of remediation action.
    /// </summary>
    public required RemediationActionType ActionType { get; init; }

    /// <summary>
    /// Message bus topic to publish the remediation trigger to.
    /// </summary>
    public required string MessageBusTopic { get; init; }

    /// <summary>
    /// Action-specific parameters passed via message bus.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Parameters { get; init; }

    /// <summary>
    /// Maximum time to wait for remediation completion.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Defines a rule mapping alert conditions to remediation actions.
/// </summary>
[SdkCompatibility("3.0.0")]
public sealed record RemediationRule
{
    /// <summary>
    /// Unique identifier for this rule.
    /// </summary>
    public required string RuleId { get; init; }

    /// <summary>
    /// Human-readable name for this rule.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Description of what this rule does.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Condition that triggers this rule.
    /// </summary>
    public required AlertCondition Condition { get; init; }

    /// <summary>
    /// Action to execute when condition is met.
    /// </summary>
    public required RemediationAction Action { get; init; }

    /// <summary>
    /// Whether this rule is currently enabled.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Rule evaluation priority (higher values evaluated first).
    /// </summary>
    public int Priority { get; init; } = 0;

    /// <summary>
    /// Minimum time between re-triggers of this rule.
    /// </summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Maximum retry attempts for failed remediations.
    /// </summary>
    public int MaxRetries { get; init; } = 3;
}

/// <summary>
/// Audit log entry for a remediation action execution.
/// </summary>
[SdkCompatibility("3.0.0")]
public sealed record RemediationLogEntry
{
    /// <summary>
    /// Unique identifier for this log entry.
    /// </summary>
    public required string LogId { get; init; }

    /// <summary>
    /// When the remediation was executed.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// ID of the rule that triggered this remediation.
    /// </summary>
    public required string RuleId { get; init; }

    /// <summary>
    /// Name of the rule that triggered this remediation.
    /// </summary>
    public required string RuleName { get; init; }

    /// <summary>
    /// Alert topic that triggered the rule.
    /// </summary>
    public required string AlertTopic { get; init; }

    /// <summary>
    /// Type of remediation action executed.
    /// </summary>
    public required RemediationActionType ActionType { get; init; }

    /// <summary>
    /// Outcome of the remediation attempt.
    /// </summary>
    public required RemediationOutcome Outcome { get; init; }

    /// <summary>
    /// How long the remediation took to execute.
    /// </summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Error message if the remediation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Additional context about the remediation execution.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Context { get; init; }
}

/// <summary>
/// Configuration for autonomous operations engine.
/// </summary>
[SdkCompatibility("3.0.0")]
public sealed record AutonomousOperationsConfig
{
    /// <summary>
    /// Maximum number of concurrent remediations allowed.
    /// </summary>
    public int MaxConcurrentRemediations { get; init; } = 3;

    /// <summary>
    /// Maximum number of registered rules.
    /// </summary>
    public int MaxRules { get; init; } = 1000;

    /// <summary>
    /// Maximum audit log entries to retain (oldest evicted when exceeded).
    /// </summary>
    public int MaxLogEntries { get; init; } = 100_000;

    /// <summary>
    /// If true, log what would happen without executing actions.
    /// </summary>
    public bool DryRunMode { get; init; } = false;

    /// <summary>
    /// Minimum time between any two remediations (global throttle).
    /// </summary>
    public TimeSpan GlobalCooldown { get; init; } = TimeSpan.FromSeconds(30);
}
