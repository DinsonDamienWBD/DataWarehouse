namespace DataWarehouse.Plugins.UltimateDataGovernance.Strategies.PolicyManagement;

#region Policy Definition Strategy

public sealed class PolicyDefinitionStrategy : DataGovernanceStrategyBase
{
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
}

#endregion

#region Policy Enforcement Strategy

public sealed class PolicyEnforcementStrategy : DataGovernanceStrategyBase
{
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
