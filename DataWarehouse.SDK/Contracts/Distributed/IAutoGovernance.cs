using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Distributed
{
    /// <summary>
    /// Contract for SDK-level policy enforcement (DIST-07).
    /// Provides automatic governance through configurable policies that control
    /// data retention, classification, compliance, access, and data residency.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public interface IAutoGovernance
    {
        /// <summary>
        /// Raised when a governance event occurs.
        /// </summary>
        event Action<GovernanceEvent>? OnGovernanceEvent;

        /// <summary>
        /// Evaluates an operation against active governance policies.
        /// </summary>
        /// <param name="context">The governance context describing the operation.</param>
        /// <param name="ct">Cancellation token for the evaluation.</param>
        /// <returns>The evaluation result with any violations and required actions.</returns>
        Task<PolicyEvaluationResult> EvaluateAsync(GovernanceContext context, CancellationToken ct = default);

        /// <summary>
        /// Gets all currently active governance policies.
        /// </summary>
        /// <param name="ct">Cancellation token for the retrieval.</param>
        /// <returns>A read-only list of active policies.</returns>
        Task<IReadOnlyList<GovernancePolicy>> GetActivePoliciesAsync(CancellationToken ct = default);

        /// <summary>
        /// Registers a new governance policy.
        /// </summary>
        /// <param name="policy">The policy to register.</param>
        /// <param name="ct">Cancellation token for the registration.</param>
        /// <returns>A task representing the registration operation.</returns>
        Task RegisterPolicyAsync(GovernancePolicy policy, CancellationToken ct = default);

        /// <summary>
        /// Deregisters a governance policy.
        /// </summary>
        /// <param name="policyId">The policy identifier to deregister.</param>
        /// <param name="ct">Cancellation token for the deregistration.</param>
        /// <returns>A task representing the deregistration operation.</returns>
        Task DeregisterPolicyAsync(string policyId, CancellationToken ct = default);
    }

    /// <summary>
    /// A governance policy that defines rules for automated enforcement.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record GovernancePolicy
    {
        /// <summary>
        /// Unique identifier for this policy.
        /// </summary>
        public required string PolicyId { get; init; }

        /// <summary>
        /// Human-readable name for this policy.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Description of what this policy enforces.
        /// </summary>
        public required string Description { get; init; }

        /// <summary>
        /// The type of governance this policy addresses.
        /// </summary>
        public required GovernancePolicyType Type { get; init; }

        /// <summary>
        /// The policy expression or rule definition.
        /// </summary>
        public required string Expression { get; init; }

        /// <summary>
        /// The action to take when this policy is violated.
        /// </summary>
        public required GovernanceAction Action { get; init; }

        /// <summary>
        /// Priority of this policy (lower values are evaluated first).
        /// </summary>
        public required int Priority { get; init; }

        /// <summary>
        /// Whether this policy is currently enabled.
        /// </summary>
        public required bool IsEnabled { get; init; }
    }

    /// <summary>
    /// Types of governance policies.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public enum GovernancePolicyType
    {
        /// <summary>Data retention policies (e.g., delete after 90 days).</summary>
        Retention,
        /// <summary>Data classification policies (e.g., PII detection).</summary>
        Classification,
        /// <summary>Compliance policies (e.g., GDPR, HIPAA).</summary>
        Compliance,
        /// <summary>Access control policies.</summary>
        Access,
        /// <summary>Data residency policies (e.g., data must stay in region).</summary>
        DataResidency
    }

    /// <summary>
    /// Actions that can be taken by governance policies.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public enum GovernanceAction
    {
        /// <summary>Allow the operation to proceed.</summary>
        Allow,
        /// <summary>Deny the operation.</summary>
        Deny,
        /// <summary>Allow the operation but record an audit entry.</summary>
        Audit,
        /// <summary>Quarantine the data for review.</summary>
        Quarantine,
        /// <summary>Encrypt the data before allowing the operation.</summary>
        Encrypt,
        /// <summary>Notify administrators about the operation.</summary>
        Notify
    }

    /// <summary>
    /// Context describing an operation to be evaluated against governance policies.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record GovernanceContext
    {
        /// <summary>
        /// The type of operation being performed (e.g., "read", "write", "delete").
        /// </summary>
        public required string OperationType { get; init; }

        /// <summary>
        /// The resource being operated on.
        /// </summary>
        public required string ResourceId { get; init; }

        /// <summary>
        /// The user or system performing the operation, if known.
        /// </summary>
        public string? UserId { get; init; }

        /// <summary>
        /// Additional metadata about the operation.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Result of evaluating an operation against governance policies.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record PolicyEvaluationResult
    {
        /// <summary>
        /// Whether the operation is allowed by all policies.
        /// </summary>
        public required bool IsAllowed { get; init; }

        /// <summary>
        /// List of policy violations found.
        /// </summary>
        public required IReadOnlyList<PolicyViolation> Violations { get; init; }

        /// <summary>
        /// List of actions required by the policies.
        /// </summary>
        public required IReadOnlyList<GovernanceAction> RequiredActions { get; init; }

        /// <summary>
        /// Optional audit message to record.
        /// </summary>
        public string? AuditMessage { get; init; }

        /// <summary>
        /// Creates an allowed result with no violations.
        /// </summary>
        /// <param name="auditMessage">Optional audit message.</param>
        /// <returns>An allowed policy evaluation result.</returns>
        public static PolicyEvaluationResult Allowed(string? auditMessage = null) =>
            new()
            {
                IsAllowed = true,
                Violations = Array.Empty<PolicyViolation>(),
                RequiredActions = Array.Empty<GovernanceAction>(),
                AuditMessage = auditMessage
            };

        /// <summary>
        /// Creates a denied result with violations.
        /// </summary>
        /// <param name="violations">The policy violations.</param>
        /// <param name="requiredActions">The required actions.</param>
        /// <param name="auditMessage">Optional audit message.</param>
        /// <returns>A denied policy evaluation result.</returns>
        public static PolicyEvaluationResult Denied(
            IReadOnlyList<PolicyViolation> violations,
            IReadOnlyList<GovernanceAction> requiredActions,
            string? auditMessage = null) =>
            new()
            {
                IsAllowed = false,
                Violations = violations,
                RequiredActions = requiredActions,
                AuditMessage = auditMessage
            };
    }

    /// <summary>
    /// A specific policy violation.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record PolicyViolation
    {
        /// <summary>
        /// The policy that was violated.
        /// </summary>
        public required string PolicyId { get; init; }

        /// <summary>
        /// The name of the violated policy.
        /// </summary>
        public required string PolicyName { get; init; }

        /// <summary>
        /// Description of the violation.
        /// </summary>
        public required string Description { get; init; }

        /// <summary>
        /// The action mandated by the violated policy.
        /// </summary>
        public required GovernanceAction Action { get; init; }
    }

    /// <summary>
    /// A governance event describing policy-related operations.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record GovernanceEvent
    {
        /// <summary>
        /// The type of governance event.
        /// </summary>
        public required GovernanceEventType EventType { get; init; }

        /// <summary>
        /// The policy involved in the event.
        /// </summary>
        public required string PolicyId { get; init; }

        /// <summary>
        /// The resource involved in the event.
        /// </summary>
        public required string ResourceId { get; init; }

        /// <summary>
        /// When the event occurred.
        /// </summary>
        public required DateTimeOffset Timestamp { get; init; }

        /// <summary>
        /// Optional detail about the event.
        /// </summary>
        public string? Detail { get; init; }
    }

    /// <summary>
    /// Types of governance events.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public enum GovernanceEventType
    {
        /// <summary>A policy was evaluated.</summary>
        PolicyEvaluated,
        /// <summary>A policy was violated.</summary>
        PolicyViolated,
        /// <summary>A new policy was registered.</summary>
        PolicyRegistered,
        /// <summary>A policy was deregistered.</summary>
        PolicyDeregistered
    }
}
