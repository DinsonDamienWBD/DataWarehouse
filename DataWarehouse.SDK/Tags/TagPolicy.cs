using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags;

/// <summary>
/// Severity levels for tag policy violations.
/// Determines the operational impact when a policy condition is not met.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag policy types")]
public enum PolicySeverity
{
    /// <summary>Logged only; no operational impact.</summary>
    Info = 0,

    /// <summary>Logged and event emitted; operation is allowed to proceed.</summary>
    Warning = 1,

    /// <summary>Event emitted; operation is blocked.</summary>
    Error = 2,

    /// <summary>Event emitted; operation is blocked and an alert is triggered.</summary>
    Critical = 3
}

/// <summary>
/// Defines the scope of objects a tag policy applies to.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag policy types")]
public enum PolicyScope
{
    /// <summary>Policy applies to all objects in the data warehouse.</summary>
    Global = 0,

    /// <summary>Policy applies to objects in a specific tag namespace.</summary>
    Namespace = 1,

    /// <summary>Policy applies to objects whose key starts with a given prefix.</summary>
    ObjectPrefix = 2,

    /// <summary>Policy uses a custom predicate function for scope matching.</summary>
    Custom = 3
}

/// <summary>
/// Types of conditions that can be evaluated as part of a tag policy.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag policy types")]
public enum PolicyConditionType
{
    /// <summary>Requires a tag with the specified key to exist.</summary>
    RequireTag,

    /// <summary>Forbids a tag with the specified key from existing.</summary>
    ForbidTag,

    /// <summary>Requires a tag to exist with a specific value.</summary>
    RequireValue,

    /// <summary>Requires a tag to exist with a specific source.</summary>
    RequireSource,

    /// <summary>Requires a tag to exist with at least one ACL entry.</summary>
    RequireAcl,

    /// <summary>Limits the total number of tags on an object.</summary>
    TagCountLimit,

    /// <summary>Uses a custom predicate function for evaluation.</summary>
    CustomPredicate
}

/// <summary>
/// A single condition within a tag policy that must be satisfied.
/// All conditions in a policy are evaluated with AND semantics.
/// </summary>
/// <param name="Type">The type of condition to evaluate.</param>
/// <param name="TagKey">The tag key to check (for RequireTag, ForbidTag, RequireValue, RequireSource).</param>
/// <param name="ExpectedValue">The expected value (for RequireValue conditions).</param>
/// <param name="RequiredSource">The required tag source (for RequireSource conditions).</param>
/// <param name="MaxTagCount">Maximum allowed tag count (for TagCountLimit conditions).</param>
/// <param name="Predicate">Custom predicate function (for CustomPredicate conditions).</param>
/// <param name="Description">Human-readable description of this condition.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag policy types")]
public sealed record PolicyCondition(
    PolicyConditionType Type,
    TagKey? TagKey = null,
    TagValue? ExpectedValue = null,
    TagSource? RequiredSource = null,
    int? MaxTagCount = null,
    Func<TagCollection, bool>? Predicate = null,
    string Description = "");

/// <summary>
/// Defines a tag policy that enforces mandatory tag rules across the data warehouse.
/// Policies contain one or more conditions (AND semantics) and are scoped to control
/// which objects they apply to. Compliance passports and sovereignty mesh depend on
/// enforceable rules like "all PII objects MUST have data-classification tag".
/// </summary>
/// <param name="PolicyId">Unique identifier for this policy.</param>
/// <param name="DisplayName">Human-readable display name.</param>
/// <param name="Description">Optional longer description of the policy's purpose.</param>
/// <param name="Scope">The scope of objects this policy applies to.</param>
/// <param name="ScopeFilter">Scope-specific filter (namespace name, object prefix, etc.).</param>
/// <param name="Conditions">All conditions that must pass (AND semantics).</param>
/// <param name="Severity">The severity level when this policy is violated.</param>
/// <param name="Enabled">Whether this policy is currently active. Defaults to true.</param>
/// <param name="CreatedUtc">UTC timestamp of when this policy was created.</param>
/// <param name="CreatedBy">Optional identifier of who or what created this policy.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag policy types")]
public sealed record TagPolicy(
    string PolicyId,
    string DisplayName,
    string? Description,
    PolicyScope Scope,
    string? ScopeFilter,
    IReadOnlyList<PolicyCondition> Conditions,
    PolicySeverity Severity,
    bool Enabled = true,
    DateTimeOffset CreatedUtc = default,
    string? CreatedBy = null)
{
    /// <summary>UTC timestamp of when this policy was created.</summary>
    public DateTimeOffset CreatedUtc { get; init; } = CreatedUtc == default ? DateTimeOffset.UtcNow : CreatedUtc;
}

/// <summary>
/// Represents a single policy violation detected during evaluation.
/// Contains sufficient context to identify the object, policy, and condition that failed.
/// </summary>
/// <param name="PolicyId">The ID of the violated policy.</param>
/// <param name="PolicyDisplayName">The display name of the violated policy.</param>
/// <param name="ObjectKey">The storage key of the object that violated the policy.</param>
/// <param name="FailedCondition">The specific condition that was not met.</param>
/// <param name="Severity">The severity of this violation (inherited from the policy).</param>
/// <param name="Message">Human-readable description of the violation.</param>
/// <param name="DetectedAt">UTC timestamp of when this violation was detected.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag policy types")]
public sealed record PolicyViolation(
    string PolicyId,
    string PolicyDisplayName,
    string ObjectKey,
    PolicyCondition FailedCondition,
    PolicySeverity Severity,
    string Message,
    DateTimeOffset DetectedAt);

/// <summary>
/// The result of evaluating all applicable policies against an object's tags.
/// Contains compliance status, all violations found, and evaluation metrics.
/// </summary>
/// <param name="IsCompliant">True if no Error or Critical violations were found.</param>
/// <param name="Violations">All violations detected, including Info and Warning severity.</param>
/// <param name="PoliciesEvaluated">The number of policies that were evaluated.</param>
/// <param name="EvaluationDuration">How long the evaluation took.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag policy types")]
public sealed record PolicyEvaluationResult(
    bool IsCompliant,
    IReadOnlyList<PolicyViolation> Violations,
    int PoliciesEvaluated,
    TimeSpan EvaluationDuration);
