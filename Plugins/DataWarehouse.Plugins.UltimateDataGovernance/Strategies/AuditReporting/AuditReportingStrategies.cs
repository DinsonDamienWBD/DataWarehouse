namespace DataWarehouse.Plugins.UltimateDataGovernance.Strategies.AuditReporting;

public sealed class AuditTrailCaptureStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "audit-trail-capture";
    public override string DisplayName => "Audit Trail Capture";
    public override GovernanceCategory Category => GovernanceCategory.AuditReporting;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Capture comprehensive audit trails for all governance activities";
    public override string[] Tags => ["audit", "trail", "capture"];
}

public sealed class AuditReportGenerationStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "audit-report-generation";
    public override string DisplayName => "Audit Report Generation";
    public override GovernanceCategory Category => GovernanceCategory.AuditReporting;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = false, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Generate comprehensive audit reports and summaries";
    public override string[] Tags => ["audit", "reporting", "generation"];
}

public sealed class GovernanceDashboardStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "governance-dashboard";
    public override string DisplayName => "Governance Dashboard";
    public override GovernanceCategory Category => GovernanceCategory.AuditReporting;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = false, SupportsRealTime = true, SupportsAudit = false, SupportsVersioning = false };
    public override string SemanticDescription => "Real-time governance metrics and KPI dashboards";
    public override string[] Tags => ["audit", "dashboard", "metrics"];
}

public sealed class ComplianceMetricsStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "compliance-metrics";
    public override string DisplayName => "Compliance Metrics";
    public override GovernanceCategory Category => GovernanceCategory.AuditReporting;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Track and report compliance metrics and trends";
    public override string[] Tags => ["audit", "compliance", "metrics"];
}

public sealed class ViolationTrackingStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "violation-tracking";
    public override string DisplayName => "Violation Tracking";
    public override GovernanceCategory Category => GovernanceCategory.AuditReporting;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Track and report policy violations and remediation";
    public override string[] Tags => ["audit", "violations", "tracking"];
}

public sealed class AuditLogSearchStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "audit-log-search";
    public override string DisplayName => "Audit Log Search";
    public override GovernanceCategory Category => GovernanceCategory.AuditReporting;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = false, SupportsVersioning = false };
    public override string SemanticDescription => "Search and query audit logs with advanced filtering";
    public override string[] Tags => ["audit", "search", "query"];
}

public sealed class AuditRetentionStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "audit-retention";
    public override string DisplayName => "Audit Retention";
    public override GovernanceCategory Category => GovernanceCategory.AuditReporting;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = false, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Manage retention and archival of audit logs";
    public override string[] Tags => ["audit", "retention", "archival"];
}

public sealed class AuditAlertingStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "audit-alerting";
    public override string DisplayName => "Audit Alerting";
    public override GovernanceCategory Category => GovernanceCategory.AuditReporting;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = false, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Real-time alerts for critical governance events";
    public override string[] Tags => ["audit", "alerting", "notifications"];
}

public sealed class ExecutiveReportingStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "executive-reporting";
    public override string DisplayName => "Executive Reporting";
    public override GovernanceCategory Category => GovernanceCategory.AuditReporting;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = false, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Executive-level governance summary reports";
    public override string[] Tags => ["audit", "executive", "summary"];
}
