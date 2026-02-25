namespace DataWarehouse.Plugins.UltimateDataGovernance.Strategies.RegulatoryCompliance;

public sealed class GDPRComplianceStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "gdpr-compliance";
    public override string DisplayName => "GDPR Compliance";
    public override GovernanceCategory Category => GovernanceCategory.RegulatoryCompliance;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Ensure compliance with EU General Data Protection Regulation (GDPR)";
    public override string[] Tags => ["compliance", "gdpr", "privacy"];
}

public sealed class CCPAComplianceStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "ccpa-compliance";
    public override string DisplayName => "CCPA Compliance";
    public override GovernanceCategory Category => GovernanceCategory.RegulatoryCompliance;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Ensure compliance with California Consumer Privacy Act (CCPA)";
    public override string[] Tags => ["compliance", "ccpa", "privacy"];
}

public sealed class HIPAAComplianceStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "hipaa-compliance";
    public override string DisplayName => "HIPAA Compliance";
    public override GovernanceCategory Category => GovernanceCategory.RegulatoryCompliance;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Ensure compliance with Health Insurance Portability and Accountability Act (HIPAA)";
    public override string[] Tags => ["compliance", "hipaa", "healthcare"];
}

public sealed class SOXComplianceStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "sox-compliance";
    public override string DisplayName => "SOX Compliance";
    public override GovernanceCategory Category => GovernanceCategory.RegulatoryCompliance;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Ensure compliance with Sarbanes-Oxley Act (SOX)";
    public override string[] Tags => ["compliance", "sox", "financial"];
}

public sealed class PCIDSSComplianceStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "pci-dss-compliance";
    public override string DisplayName => "PCI-DSS Compliance";
    public override GovernanceCategory Category => GovernanceCategory.RegulatoryCompliance;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Ensure compliance with Payment Card Industry Data Security Standard (PCI-DSS)";
    public override string[] Tags => ["compliance", "pci-dss", "payment"];
}

public sealed class ComplianceFrameworkStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "compliance-framework";
    public override string DisplayName => "Compliance Framework";
    public override GovernanceCategory Category => GovernanceCategory.RegulatoryCompliance;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = false, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Unified compliance framework supporting multiple regulations";
    public override string[] Tags => ["compliance", "framework", "multi-regulation"];
}

public sealed class ComplianceMonitoringStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "compliance-monitoring";
    public override string DisplayName => "Compliance Monitoring";
    public override GovernanceCategory Category => GovernanceCategory.RegulatoryCompliance;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Continuous monitoring of compliance status and violations";
    public override string[] Tags => ["compliance", "monitoring", "real-time"];
}

public sealed class ComplianceReportingStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "compliance-reporting";
    public override string DisplayName => "Compliance Reporting";
    public override GovernanceCategory Category => GovernanceCategory.RegulatoryCompliance;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = false, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Generate compliance reports and attestations";
    public override string[] Tags => ["compliance", "reporting", "attestation"];
}

public sealed class DataSubjectRightsStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "data-subject-rights";
    public override string DisplayName => "Data Subject Rights";
    public override GovernanceCategory Category => GovernanceCategory.RegulatoryCompliance;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = false, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Manage data subject rights requests (access, deletion, portability)";
    public override string[] Tags => ["compliance", "data-subject-rights", "privacy"];
}

public sealed class ConsentManagementStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "consent-management";
    public override string DisplayName => "Consent Management";
    public override GovernanceCategory Category => GovernanceCategory.RegulatoryCompliance;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Track and manage user consent for data processing";
    public override string[] Tags => ["compliance", "consent", "privacy"];
}
