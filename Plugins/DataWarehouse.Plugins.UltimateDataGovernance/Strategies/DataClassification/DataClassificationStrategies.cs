namespace DataWarehouse.Plugins.UltimateDataGovernance.Strategies.DataClassification;

public sealed class SensitivityClassificationStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "sensitivity-classification";
    public override string DisplayName => "Sensitivity Classification";
    public override GovernanceCategory Category => GovernanceCategory.DataClassification;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Classify data by sensitivity level (public, internal, confidential, restricted)";
    public override string[] Tags => ["classification", "sensitivity", "confidentiality"];
}

public sealed class AutomatedClassificationStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "automated-classification";
    public override string DisplayName => "Automated Classification";
    public override GovernanceCategory Category => GovernanceCategory.DataClassification;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "AI-powered automated data classification using ML models";
    public override string[] Tags => ["classification", "automation", "machine-learning"];
}

public sealed class ManualClassificationStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "manual-classification";
    public override string DisplayName => "Manual Classification";
    public override GovernanceCategory Category => GovernanceCategory.DataClassification;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = false, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "User-driven manual classification with approval workflows";
    public override string[] Tags => ["classification", "manual", "user-driven"];
}

public sealed class ClassificationTaggingStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "classification-tagging";
    public override string DisplayName => "Classification Tagging";
    public override GovernanceCategory Category => GovernanceCategory.DataClassification;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Tag data assets with classification labels and metadata";
    public override string[] Tags => ["classification", "tagging", "labels"];
}

public sealed class ClassificationInheritanceStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "classification-inheritance";
    public override string DisplayName => "Classification Inheritance";
    public override GovernanceCategory Category => GovernanceCategory.DataClassification;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = false, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Inherit classifications from parent assets and containers";
    public override string[] Tags => ["classification", "inheritance", "hierarchy"];
}

public sealed class ClassificationReviewStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "classification-review";
    public override string DisplayName => "Classification Review";
    public override GovernanceCategory Category => GovernanceCategory.DataClassification;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = false, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Periodic review and recertification of data classifications";
    public override string[] Tags => ["classification", "review", "recertification"];
}

public sealed class ClassificationPolicyStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "classification-policy";
    public override string DisplayName => "Classification Policies";
    public override GovernanceCategory Category => GovernanceCategory.DataClassification;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Define and enforce classification policies and rules";
    public override string[] Tags => ["classification", "policy", "rules"];
}

public sealed class ClassificationReportingStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "classification-reporting";
    public override string DisplayName => "Classification Reporting";
    public override GovernanceCategory Category => GovernanceCategory.DataClassification;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = false, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Generate classification coverage and distribution reports";
    public override string[] Tags => ["classification", "reporting", "analytics"];
}

public sealed class PIIDetectionStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "pii-detection";
    public override string DisplayName => "PII Detection";
    public override GovernanceCategory Category => GovernanceCategory.DataClassification;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Detect and classify personally identifiable information (PII)";
    public override string[] Tags => ["classification", "pii", "detection"];
}

public sealed class PHIDetectionStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "phi-detection";
    public override string DisplayName => "PHI Detection";
    public override GovernanceCategory Category => GovernanceCategory.DataClassification;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Detect and classify protected health information (PHI)";
    public override string[] Tags => ["classification", "phi", "healthcare"];
}

public sealed class PCIDetectionStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "pci-detection";
    public override string DisplayName => "PCI Detection";
    public override GovernanceCategory Category => GovernanceCategory.DataClassification;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Detect and classify payment card industry (PCI) data";
    public override string[] Tags => ["classification", "pci", "payment-data"];
}
