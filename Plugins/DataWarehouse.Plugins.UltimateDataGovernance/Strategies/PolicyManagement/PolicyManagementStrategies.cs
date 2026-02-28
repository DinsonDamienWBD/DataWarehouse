using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataGovernance.Strategies.PolicyManagement;

#region Policy Definition Strategy

public sealed class PolicyDefinitionStrategy : DataGovernanceStrategyBase
{
    private readonly BoundedDictionary<string, PolicyDefinition> _policies = new BoundedDictionary<string, PolicyDefinition>(1000);

    public override string StrategyId => "policy-definition";
    public override string DisplayName => "Policy Definition";
    public override GovernanceCategory Category => GovernanceCategory.PolicyManagement;
    public override DataGovernanceCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = true,
        SupportsAudit = true,
        SupportsVersioning = true
    };
    public override string SemanticDescription => "Define and manage governance policies with templates, rules, and metadata";
    public override string[] Tags => ["policy", "definition", "templates", "rules"];

    public PolicyDefinition? GetPolicy(string policyId) => _policies.TryGetValue(policyId, out var policy) ? policy : null;

    public PolicyDefinition DefinePolicy(string policyId, string name, string description, Dictionary<string, object> rules)
    {
        var policy = new PolicyDefinition
        {
            PolicyId = policyId,
            Name = name,
            Description = description,
            Rules = rules,
            CreatedAt = DateTimeOffset.UtcNow,
            Version = 1,
            IsActive = false
        };
        _policies[policyId] = policy;
        return policy;
    }

    public bool UpdatePolicy(string policyId, Dictionary<string, object> rules)
    {
        if (!_policies.TryGetValue(policyId, out var policy)) return false;
        var updated = policy with { Rules = rules, Version = policy.Version + 1, ModifiedAt = DateTimeOffset.UtcNow };
        _policies[policyId] = updated;
        return true;
    }

    public bool DeletePolicy(string policyId) => _policies.TryRemove(policyId, out _);

    public IReadOnlyList<PolicyDefinition> GetAllPolicies() => _policies.Values.ToList().AsReadOnly();
}

public sealed record PolicyDefinition
{
    public required string PolicyId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public Dictionary<string, object> Rules { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
    public int Version { get; init; }
    public bool IsActive { get; init; }
}

#endregion

#region Policy Enforcement Strategy

public sealed class PolicyEnforcementStrategy : DataGovernanceStrategyBase
{
    private readonly BoundedDictionary<string, PolicyDefinition> _activePolicies = new BoundedDictionary<string, PolicyDefinition>(1000);
    private readonly BoundedDictionary<string, List<PolicyViolation>> _violations = new BoundedDictionary<string, List<PolicyViolation>>(1000);

    public override string StrategyId => "policy-enforcement";
    public override string DisplayName => "Policy Enforcement";
    public override GovernanceCategory Category => GovernanceCategory.PolicyManagement;
    public override DataGovernanceCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = true,
        SupportsAudit = true,
        SupportsVersioning = false
    };
    public override string SemanticDescription => "Enforce governance policies with real-time validation and violation tracking";
    public override string[] Tags => ["policy", "enforcement", "validation", "compliance"];

    public void LoadPolicy(PolicyDefinition policy)
    {
        if (policy.IsActive)
        {
            _activePolicies[policy.PolicyId] = policy;
        }
    }

    public PolicyEnforcementResult EvaluatePolicy(string policyId, string resourceId, Dictionary<string, object> context)
    {
        if (!_activePolicies.TryGetValue(policyId, out var policy))
        {
            // Fail-closed: unknown policy ID denies access to prevent typo-based bypasses (finding 2288).
            return new PolicyEnforcementResult
            {
                PolicyId = policyId,
                ResourceId = resourceId,
                IsAllowed = false,
                Reason = "Policy not found or not active — access denied (fail-closed)"
            };
        }

        // Simple rule evaluation: check if context satisfies policy rules
        var violations = new List<string>();
        foreach (var (key, value) in policy.Rules)
        {
            if (context.TryGetValue(key, out var contextValue))
            {
                if (!Equals(value, contextValue))
                {
                    violations.Add($"Rule '{key}' violated: expected '{value}', got '{contextValue}'");
                }
            }
            else
            {
                violations.Add($"Required field '{key}' missing from context");
            }
        }

        var isAllowed = violations.Count == 0;

        if (!isAllowed)
        {
            var violation = new PolicyViolation
            {
                PolicyId = policyId,
                ResourceId = resourceId,
                Timestamp = DateTimeOffset.UtcNow,
                Violations = violations
            };

            _violations.AddOrUpdate(
                resourceId,
                _ => new List<PolicyViolation> { violation },
                (_, list) => { lock (list) { list.Add(violation); } return list; });
        }

        return new PolicyEnforcementResult
        {
            PolicyId = policyId,
            ResourceId = resourceId,
            IsAllowed = isAllowed,
            Reason = isAllowed ? "Policy satisfied" : string.Join("; ", violations)
        };
    }

    public IReadOnlyList<PolicyViolation> GetViolations(string resourceId) =>
        _violations.TryGetValue(resourceId, out var list) ? list.AsReadOnly() : Array.Empty<PolicyViolation>();
}

public sealed record PolicyEnforcementResult
{
    public required string PolicyId { get; init; }
    public required string ResourceId { get; init; }
    public bool IsAllowed { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed record PolicyViolation
{
    public required string PolicyId { get; init; }
    public required string ResourceId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public List<string> Violations { get; init; } = new();
}

#endregion

#region Policy Versioning Strategy

public sealed class PolicyVersioningStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "policy-versioning";
    public override string DisplayName => "Policy Versioning";
    public override GovernanceCategory Category => GovernanceCategory.PolicyManagement;
    public override DataGovernanceCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = false,
        SupportsRealTime = false,
        SupportsAudit = true,
        SupportsVersioning = true
    };
    public override string SemanticDescription => "Version control for governance policies with change tracking and rollback";
    public override string[] Tags => ["policy", "versioning", "change-tracking", "history"];
}

#endregion

#region Policy Lifecycle Strategy

public sealed class PolicyLifecycleStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "policy-lifecycle";
    public override string DisplayName => "Policy Lifecycle Management";
    public override GovernanceCategory Category => GovernanceCategory.PolicyManagement;
    public override DataGovernanceCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = false,
        SupportsAudit = true,
        SupportsVersioning = true
    };
    public override string SemanticDescription => "Manage policy lifecycle from draft to active to deprecated states";
    public override string[] Tags => ["policy", "lifecycle", "workflow", "states"];
}

#endregion

#region Policy Validation Strategy

public sealed class PolicyValidationStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "policy-validation";
    public override string DisplayName => "Policy Validation";
    public override GovernanceCategory Category => GovernanceCategory.PolicyManagement;
    public override DataGovernanceCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = true,
        SupportsAudit = true,
        SupportsVersioning = false
    };
    public override string SemanticDescription => "Validate policy definitions for consistency and conflicts";
    public override string[] Tags => ["policy", "validation", "consistency", "conflicts"];
}

#endregion

#region Policy Template Strategy

public sealed class PolicyTemplateStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "policy-template";
    public override string DisplayName => "Policy Templates";
    public override GovernanceCategory Category => GovernanceCategory.PolicyManagement;
    public override DataGovernanceCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = false,
        SupportsAudit = true,
        SupportsVersioning = true
    };
    public override string SemanticDescription => "Pre-built policy templates for common governance scenarios";
    public override string[] Tags => ["policy", "templates", "best-practices", "reusable"];
}

#endregion

#region Policy Impact Analysis Strategy

public sealed class PolicyImpactAnalysisStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "policy-impact-analysis";
    public override string DisplayName => "Policy Impact Analysis";
    public override GovernanceCategory Category => GovernanceCategory.PolicyManagement;
    public override DataGovernanceCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = false,
        SupportsAudit = true,
        SupportsVersioning = false
    };
    public override string SemanticDescription => "Analyze impact of policy changes on data assets and operations";
    public override string[] Tags => ["policy", "impact-analysis", "risk-assessment", "change-management"];
}

#endregion

#region Policy Conflict Detection Strategy

public sealed class PolicyConflictDetectionStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "policy-conflict-detection";
    public override string DisplayName => "Policy Conflict Detection";
    public override GovernanceCategory Category => GovernanceCategory.PolicyManagement;
    public override DataGovernanceCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = true,
        SupportsAudit = true,
        SupportsVersioning = false
    };
    public override string SemanticDescription => "Detect conflicts between multiple governance policies";
    public override string[] Tags => ["policy", "conflicts", "resolution", "priority"];
}

#endregion

#region Policy Approval Workflow Strategy

public sealed class PolicyApprovalWorkflowStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "policy-approval-workflow";
    public override string DisplayName => "Policy Approval Workflow";
    public override GovernanceCategory Category => GovernanceCategory.PolicyManagement;
    public override DataGovernanceCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = false,
        SupportsRealTime = true,
        SupportsAudit = true,
        SupportsVersioning = true
    };
    public override string SemanticDescription => "Workflow for policy approval with multi-level review and sign-off";
    public override string[] Tags => ["policy", "approval", "workflow", "review"];
}

#endregion

#region Policy Exception Management Strategy

public sealed class PolicyExceptionManagementStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "policy-exception-management";
    public override string DisplayName => "Policy Exception Management";
    public override GovernanceCategory Category => GovernanceCategory.PolicyManagement;
    public override DataGovernanceCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = true,
        SupportsAudit = true,
        SupportsVersioning = true
    };
    public override string SemanticDescription => "Manage exceptions to governance policies with approval and tracking";
    public override string[] Tags => ["policy", "exceptions", "waivers", "approvals"];
}

#endregion
