using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Tags;

/// <summary>
/// Default implementation of <see cref="ITagPolicyEngine"/> that evaluates tag policies
/// against object tag collections using an in-memory policy store.
/// Publishes violations to the message bus when severity is Warning or above.
/// </summary>
/// <remarks>
/// Registers two built-in policies:
/// <list type="bullet">
/// <item><description><c>system.require-classification</c> (disabled): requires all objects to have a <c>compliance:data-classification</c> tag.</description></item>
/// <item><description><c>system.tag-count-limit</c> (enabled): limits objects to a maximum of 1000 tags.</description></item>
/// </list>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Default tag policy engine")]
public sealed class DefaultTagPolicyEngine : ITagPolicyEngine
{
    private readonly BoundedDictionary<string, TagPolicy> _policies = new BoundedDictionary<string, TagPolicy>(1000);
    private readonly IMessageBus? _messageBus;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultTagPolicyEngine"/> class.
    /// </summary>
    /// <param name="messageBus">
    /// Optional message bus for publishing policy violation events.
    /// If null, violations are still collected in results but not published.
    /// </param>
    public DefaultTagPolicyEngine(IMessageBus? messageBus = null)
    {
        _messageBus = messageBus;
        RegisterBuiltInPolicies();
    }

    /// <inheritdoc />
    public Task AddPolicyAsync(TagPolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(policy.PolicyId))
            throw new ArgumentException("PolicyId must not be empty.", nameof(policy));

        ct.ThrowIfCancellationRequested();
        _policies[policy.PolicyId] = policy;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemovePolicyAsync(string policyId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
        ct.ThrowIfCancellationRequested();
        _policies.TryRemove(policyId, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<TagPolicy?> GetPolicyAsync(string policyId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
        ct.ThrowIfCancellationRequested();
        _policies.TryGetValue(policyId, out var policy);
        return Task.FromResult<TagPolicy?>(policy);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TagPolicy> ListPoliciesAsync(
        PolicyScope? scope = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var kvp in _policies)
        {
            ct.ThrowIfCancellationRequested();
            if (scope is null || kvp.Value.Scope == scope.Value)
            {
                yield return kvp.Value;
            }
        }

        await Task.CompletedTask; // Ensure async signature is valid
    }

    /// <inheritdoc />
    public async Task<PolicyEvaluationResult> EvaluateAsync(
        string objectKey, TagCollection tags, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentNullException.ThrowIfNull(tags);
        ct.ThrowIfCancellationRequested();

        var sw = Stopwatch.StartNew();
        var violations = new List<PolicyViolation>();
        int policiesEvaluated = 0;

        foreach (var kvp in _policies)
        {
            ct.ThrowIfCancellationRequested();
            var policy = kvp.Value;

            if (!policy.Enabled)
                continue;

            if (!IsPolicyApplicable(policy, objectKey))
                continue;

            policiesEvaluated++;
            EvaluatePolicy(policy, objectKey, tags, violations);
        }

        sw.Stop();

        // Publish violations to message bus if any are Warning or above
        if (_messageBus is not null && violations.Any(v => v.Severity >= PolicySeverity.Warning))
        {
            await PublishViolationsAsync(objectKey, violations, ct).ConfigureAwait(false);
        }

        bool isCompliant = !violations.Any(v =>
            v.Severity == PolicySeverity.Error || v.Severity == PolicySeverity.Critical);

        return new PolicyEvaluationResult(isCompliant, violations, policiesEvaluated, sw.Elapsed);
    }

    /// <inheritdoc />
    public async Task<bool> IsCompliantAsync(
        string objectKey, TagCollection tags, CancellationToken ct = default)
    {
        var result = await EvaluateAsync(objectKey, tags, ct).ConfigureAwait(false);
        return result.IsCompliant;
    }

    /// <summary>
    /// Determines whether a policy applies to the given object key based on its scope.
    /// </summary>
    private static bool IsPolicyApplicable(TagPolicy policy, string objectKey)
    {
        return policy.Scope switch
        {
            PolicyScope.Global => true,
            PolicyScope.Namespace => !string.IsNullOrEmpty(policy.ScopeFilter) &&
                objectKey.StartsWith(policy.ScopeFilter + ":", StringComparison.OrdinalIgnoreCase),
            PolicyScope.ObjectPrefix => !string.IsNullOrEmpty(policy.ScopeFilter) &&
                objectKey.StartsWith(policy.ScopeFilter, StringComparison.OrdinalIgnoreCase),
            PolicyScope.Custom => true, // Custom scoping handled by condition predicates
            _ => false
        };
    }

    /// <summary>
    /// Evaluates all conditions in a policy against the object's tags.
    /// All conditions use AND semantics: every failed condition produces a violation.
    /// </summary>
    private static void EvaluatePolicy(
        TagPolicy policy, string objectKey, TagCollection tags, List<PolicyViolation> violations)
    {
        foreach (var condition in policy.Conditions)
        {
            bool satisfied = EvaluateCondition(condition, tags);
            if (!satisfied)
            {
                violations.Add(new PolicyViolation(
                    PolicyId: policy.PolicyId,
                    PolicyDisplayName: policy.DisplayName,
                    ObjectKey: objectKey,
                    FailedCondition: condition,
                    Severity: policy.Severity,
                    Message: BuildViolationMessage(policy, condition, objectKey),
                    DetectedAt: DateTimeOffset.UtcNow));
            }
        }
    }

    /// <summary>
    /// Evaluates a single condition against a tag collection.
    /// Returns true if the condition is satisfied, false if violated.
    /// </summary>
    private static bool EvaluateCondition(PolicyCondition condition, TagCollection tags)
    {
        return condition.Type switch
        {
            PolicyConditionType.RequireTag =>
                condition.TagKey is not null && tags.ContainsKey(condition.TagKey),

            PolicyConditionType.ForbidTag =>
                condition.TagKey is null || !tags.ContainsKey(condition.TagKey),

            PolicyConditionType.RequireValue =>
                condition.TagKey is not null &&
                tags[condition.TagKey] is { } tag &&
                condition.ExpectedValue is not null &&
                tag.Value.Equals(condition.ExpectedValue),

            PolicyConditionType.RequireSource =>
                condition.TagKey is not null &&
                tags[condition.TagKey] is { } sourceTag &&
                condition.RequiredSource.HasValue &&
                sourceTag.Source.Source == condition.RequiredSource.Value,

            PolicyConditionType.RequireAcl =>
                condition.TagKey is not null &&
                tags[condition.TagKey] is { } aclTag &&
                aclTag.Acl.Entries.Count > 0,

            PolicyConditionType.TagCountLimit =>
                condition.MaxTagCount.HasValue && tags.Count <= condition.MaxTagCount.Value,

            PolicyConditionType.CustomPredicate =>
                condition.Predicate is not null && condition.Predicate(tags),

            _ => true
        };
    }

    /// <summary>
    /// Builds a human-readable violation message for a failed condition.
    /// </summary>
    private static string BuildViolationMessage(TagPolicy policy, PolicyCondition condition, string objectKey)
    {
        var conditionDesc = !string.IsNullOrEmpty(condition.Description)
            ? condition.Description
            : condition.Type.ToString();

        return $"Policy '{policy.DisplayName}' violated on object '{objectKey}': {conditionDesc}";
    }

    /// <summary>
    /// Publishes policy violations to the message bus on the <see cref="TagTopics.TagPolicyViolation"/> topic.
    /// </summary>
    private async Task PublishViolationsAsync(
        string objectKey, List<PolicyViolation> violations, CancellationToken ct)
    {
        if (_messageBus is null) return;

        var message = new PluginMessage
        {
            Type = TagTopics.TagPolicyViolation,
            SourcePluginId = "sdk.tags.policy-engine",
            Source = "TagPolicyEngine",
            Description = $"Tag policy violations detected on object '{objectKey}'",
            CorrelationId = Guid.NewGuid().ToString("N"),
            Payload = new Dictionary<string, object>
            {
                ["objectKey"] = objectKey,
                ["violationCount"] = violations.Count,
                ["violations"] = violations.Select(v => new Dictionary<string, object>
                {
                    ["policyId"] = v.PolicyId,
                    ["policyName"] = v.PolicyDisplayName,
                    ["severity"] = v.Severity.ToString(),
                    ["message"] = v.Message,
                    ["conditionType"] = v.FailedCondition.Type.ToString(),
                    ["detectedAt"] = v.DetectedAt.ToString("O")
                }).ToList(),
                ["hasBlockingViolations"] = violations.Any(v =>
                    v.Severity == PolicySeverity.Error || v.Severity == PolicySeverity.Critical),
                ["hasCriticalViolations"] = violations.Any(v =>
                    v.Severity == PolicySeverity.Critical)
            }
        };

        try
        {
            await _messageBus.PublishAsync(TagTopics.TagPolicyViolation, message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DefaultTagPolicyEngine.PublishViolationsAsync] {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Registers the built-in system policies.
    /// </summary>
    private void RegisterBuiltInPolicies()
    {
        // Built-in: Require data classification tag (disabled by default)
        _policies["system.require-classification"] = new TagPolicy(
            PolicyId: "system.require-classification",
            DisplayName: "Require Data Classification",
            Description: "All objects must have a compliance:data-classification tag for regulatory compliance.",
            Scope: PolicyScope.Global,
            ScopeFilter: null,
            Conditions: new[]
            {
                new PolicyCondition(
                    Type: PolicyConditionType.RequireTag,
                    TagKey: new TagKey("compliance", "data-classification"),
                    Description: "Object must have a compliance:data-classification tag")
            },
            Severity: PolicySeverity.Error,
            Enabled: false,
            CreatedBy: "system");

        // Built-in: Tag count limit (enabled by default)
        _policies["system.tag-count-limit"] = new TagPolicy(
            PolicyId: "system.tag-count-limit",
            DisplayName: "Tag Count Limit",
            Description: "No object may have more than 1000 tags to prevent unbounded tag accumulation.",
            Scope: PolicyScope.Global,
            ScopeFilter: null,
            Conditions: new[]
            {
                new PolicyCondition(
                    Type: PolicyConditionType.TagCountLimit,
                    MaxTagCount: 1000,
                    Description: "Object must not exceed 1000 tags")
            },
            Severity: PolicySeverity.Error,
            Enabled: true,
            CreatedBy: "system");
    }
}
