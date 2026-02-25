namespace DataWarehouse.Plugins.UltimateDataGovernance.Strategies.DataOwnership;

public sealed class OwnershipAssignmentStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "ownership-assignment";
    public override string DisplayName => "Ownership Assignment";
    public override GovernanceCategory Category => GovernanceCategory.DataOwnership;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Assign and manage data asset ownership with roles and responsibilities";
    public override string[] Tags => ["ownership", "assignment", "responsibilities"];
}

public sealed class OwnershipTransferStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "ownership-transfer";
    public override string DisplayName => "Ownership Transfer";
    public override GovernanceCategory Category => GovernanceCategory.DataOwnership;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = false, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Transfer ownership with approval workflow and notification";
    public override string[] Tags => ["ownership", "transfer", "workflow"];
}

public sealed class OwnershipHierarchyStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "ownership-hierarchy";
    public override string DisplayName => "Ownership Hierarchy";
    public override GovernanceCategory Category => GovernanceCategory.DataOwnership;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = false, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Define and manage ownership hierarchies and inheritance";
    public override string[] Tags => ["ownership", "hierarchy", "inheritance"];
}

public sealed class OwnershipMetadataStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "ownership-metadata";
    public override string DisplayName => "Ownership Metadata";
    public override GovernanceCategory Category => GovernanceCategory.DataOwnership;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Track ownership metadata including contact, department, and accountability";
    public override string[] Tags => ["ownership", "metadata", "accountability"];
}

public sealed class OwnershipCertificationStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "ownership-certification";
    public override string DisplayName => "Ownership Certification";
    public override GovernanceCategory Category => GovernanceCategory.DataOwnership;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = false, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Periodic ownership certification and recertification workflows";
    public override string[] Tags => ["ownership", "certification", "recertification"];
}

public sealed class OwnershipDelegationStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "ownership-delegation";
    public override string DisplayName => "Ownership Delegation";
    public override GovernanceCategory Category => GovernanceCategory.DataOwnership;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = false, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Delegate ownership responsibilities with time-bound permissions";
    public override string[] Tags => ["ownership", "delegation", "temporary"];
}

public sealed class OwnershipNotificationStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "ownership-notification";
    public override string DisplayName => "Ownership Notifications";
    public override GovernanceCategory Category => GovernanceCategory.DataOwnership;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = false, SupportsVersioning = false };
    public override string SemanticDescription => "Automated notifications for ownership changes and responsibilities";
    public override string[] Tags => ["ownership", "notifications", "alerts"];
}

public sealed class OwnershipReportingStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "ownership-reporting";
    public override string DisplayName => "Ownership Reporting";
    public override GovernanceCategory Category => GovernanceCategory.DataOwnership;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = false, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Generate ownership reports and dashboards";
    public override string[] Tags => ["ownership", "reporting", "dashboards"];
}

public sealed class OwnershipVacancyDetectionStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "ownership-vacancy-detection";
    public override string DisplayName => "Ownership Vacancy Detection";
    public override GovernanceCategory Category => GovernanceCategory.DataOwnership;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Detect and alert on data assets without assigned owners";
    public override string[] Tags => ["ownership", "vacancy", "orphaned-assets"];
}
