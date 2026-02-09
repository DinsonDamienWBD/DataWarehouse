namespace DataWarehouse.Plugins.UltimateDataPrivacy.Strategies.PrivacyCompliance;

public sealed class GDPRRightToErasureStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "gdpr-right-to-erasure";
    public override string DisplayName => "GDPR Right to Erasure";
    public override PrivacyCategory Category => PrivacyCategory.PrivacyCompliance;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = false, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Implement GDPR right to erasure (right to be forgotten)";
    public override string[] Tags => ["privacy-compliance", "gdpr", "erasure"];
}

public sealed class GDPRRightToAccessStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "gdpr-right-to-access";
    public override string DisplayName => "GDPR Right to Access";
    public override PrivacyCategory Category => PrivacyCategory.PrivacyCompliance;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = false, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Implement GDPR right to access personal data";
    public override string[] Tags => ["privacy-compliance", "gdpr", "access"];
}

public sealed class GDPRDataPortabilityStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "gdpr-data-portability";
    public override string DisplayName => "GDPR Data Portability";
    public override PrivacyCategory Category => PrivacyCategory.PrivacyCompliance;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Implement GDPR right to data portability";
    public override string[] Tags => ["privacy-compliance", "gdpr", "portability"];
}

public sealed class CCPAOptOutStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "ccpa-opt-out";
    public override string DisplayName => "CCPA Opt-Out";
    public override PrivacyCategory Category => PrivacyCategory.PrivacyCompliance;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = false, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Implement CCPA opt-out of data sale";
    public override string[] Tags => ["privacy-compliance", "ccpa", "opt-out"];
}

public sealed class ConsentCollectionStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "consent-collection";
    public override string DisplayName => "Consent Collection";
    public override PrivacyCategory Category => PrivacyCategory.PrivacyCompliance;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = false, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Collect and manage user consent for data processing";
    public override string[] Tags => ["privacy-compliance", "consent", "collection"];
}

public sealed class ConsentWithdrawalStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "consent-withdrawal";
    public override string DisplayName => "Consent Withdrawal";
    public override PrivacyCategory Category => PrivacyCategory.PrivacyCompliance;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = false, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Process consent withdrawal requests";
    public override string[] Tags => ["privacy-compliance", "consent", "withdrawal"];
}

public sealed class DataProcessingRecordStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "data-processing-record";
    public override string DisplayName => "Data Processing Records";
    public override PrivacyCategory Category => PrivacyCategory.PrivacyCompliance;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Maintain records of data processing activities";
    public override string[] Tags => ["privacy-compliance", "records", "processing"];
}

public sealed class PrivacyImpactAssessmentStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "privacy-impact-assessment";
    public override string DisplayName => "Privacy Impact Assessment";
    public override PrivacyCategory Category => PrivacyCategory.PrivacyCompliance;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = false, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Conduct privacy impact assessments (PIA/DPIA)";
    public override string[] Tags => ["privacy-compliance", "impact-assessment", "pia"];
}
