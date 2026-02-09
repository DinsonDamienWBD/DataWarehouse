namespace DataWarehouse.Plugins.UltimateDataGovernance.Strategies.RetentionManagement;

public sealed class RetentionPolicyDefinitionStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "retention-policy-definition";
    public override string DisplayName => "Retention Policy Definition";
    public override GovernanceCategory Category => GovernanceCategory.RetentionManagement;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = false, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Define retention policies with rules and durations";
    public override string[] Tags => ["retention", "policy", "definition"];
}

public sealed class AutomatedArchivalStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "automated-archival";
    public override string DisplayName => "Automated Archival";
    public override GovernanceCategory Category => GovernanceCategory.RetentionManagement;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = false, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Automatically archive data based on retention policies";
    public override string[] Tags => ["retention", "archival", "automation"];
}

public sealed class AutomatedDeletionStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "automated-deletion";
    public override string DisplayName => "Automated Deletion";
    public override GovernanceCategory Category => GovernanceCategory.RetentionManagement;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = false, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Automatically delete data based on retention policies";
    public override string[] Tags => ["retention", "deletion", "automation"];
}

public sealed class LegalHoldStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "legal-hold";
    public override string DisplayName => "Legal Hold";
    public override GovernanceCategory Category => GovernanceCategory.RetentionManagement;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = false, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Place legal holds on data to prevent deletion or modification";
    public override string[] Tags => ["retention", "legal-hold", "preservation"];
}

public sealed class RetentionComplianceStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "retention-compliance";
    public override string DisplayName => "Retention Compliance";
    public override GovernanceCategory Category => GovernanceCategory.RetentionManagement;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Verify compliance with retention policies and regulations";
    public override string[] Tags => ["retention", "compliance", "verification"];
}

public sealed class RetentionReportingStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "retention-reporting";
    public override string DisplayName => "Retention Reporting";
    public override GovernanceCategory Category => GovernanceCategory.RetentionManagement;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = false, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Generate reports on retention status and compliance";
    public override string[] Tags => ["retention", "reporting", "analytics"];
}

public sealed class DataDispositionStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "data-disposition";
    public override string DisplayName => "Data Disposition";
    public override GovernanceCategory Category => GovernanceCategory.RetentionManagement;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = false, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Manage secure data disposition and destruction";
    public override string[] Tags => ["retention", "disposition", "destruction"];
}

public sealed class RetentionExceptionStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "retention-exception";
    public override string DisplayName => "Retention Exceptions";
    public override GovernanceCategory Category => GovernanceCategory.RetentionManagement;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = false, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Manage exceptions to retention policies with approval workflows";
    public override string[] Tags => ["retention", "exceptions", "approvals"];
}
