namespace DataWarehouse.Plugins.UltimateDataGovernance.Strategies.DataStewardship;

public sealed class StewardRoleDefinitionStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "steward-role-definition";
    public override string DisplayName => "Steward Role Definition";
    public override GovernanceCategory Category => GovernanceCategory.DataStewardship;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = false, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Define data steward roles with responsibilities and permissions";
    public override string[] Tags => ["stewardship", "roles", "responsibilities"];
}

public sealed class StewardWorkflowStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "steward-workflow";
    public override string DisplayName => "Steward Workflows";
    public override GovernanceCategory Category => GovernanceCategory.DataStewardship;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = false, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Workflows for data stewardship tasks and approvals";
    public override string[] Tags => ["stewardship", "workflow", "approvals"];
}

public sealed class StewardCertificationStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "steward-certification";
    public override string DisplayName => "Steward Certification";
    public override GovernanceCategory Category => GovernanceCategory.DataStewardship;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = false, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Certification programs for data stewards with training tracking";
    public override string[] Tags => ["stewardship", "certification", "training"];
}

public sealed class StewardQualityMetricsStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "steward-quality-metrics";
    public override string DisplayName => "Stewardship Quality Metrics";
    public override GovernanceCategory Category => GovernanceCategory.DataStewardship;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Track data quality metrics and stewardship effectiveness";
    public override string[] Tags => ["stewardship", "quality", "metrics"];
}

public sealed class StewardCollaborationStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "steward-collaboration";
    public override string DisplayName => "Steward Collaboration";
    public override GovernanceCategory Category => GovernanceCategory.DataStewardship;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = false, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Collaboration tools for data stewards across domains";
    public override string[] Tags => ["stewardship", "collaboration", "communication"];
}

public sealed class StewardTaskManagementStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "steward-task-management";
    public override string DisplayName => "Steward Task Management";
    public override GovernanceCategory Category => GovernanceCategory.DataStewardship;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Task management for data stewardship activities";
    public override string[] Tags => ["stewardship", "tasks", "management"];
}

public sealed class StewardEscalationStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "steward-escalation";
    public override string DisplayName => "Stewardship Escalation";
    public override GovernanceCategory Category => GovernanceCategory.DataStewardship;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = false, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Escalation paths for data stewardship issues";
    public override string[] Tags => ["stewardship", "escalation", "resolution"];
}

public sealed class StewardReportingStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "steward-reporting";
    public override string DisplayName => "Stewardship Reporting";
    public override GovernanceCategory Category => GovernanceCategory.DataStewardship;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = false, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Generate stewardship activity and effectiveness reports";
    public override string[] Tags => ["stewardship", "reporting", "analytics"];
}
