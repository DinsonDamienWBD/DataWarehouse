namespace DataWarehouse.Plugins.UltimateDataPrivacy.Strategies.PrivacyMetrics;

public sealed class ReIdentificationRiskStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "re-identification-risk";
    public override string DisplayName => "Re-identification Risk Assessment";
    public override PrivacyCategory Category => PrivacyCategory.PrivacyMetrics;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Assess risk of re-identifying anonymized data";
    public override string[] Tags => ["metrics", "re-identification", "risk-assessment"];
}

public sealed class PrivacyScoringStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "privacy-scoring";
    public override string DisplayName => "Privacy Scoring";
    public override PrivacyCategory Category => PrivacyCategory.PrivacyMetrics;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Calculate overall privacy scores and metrics";
    public override string[] Tags => ["metrics", "scoring", "assessment"];
}

public sealed class DataUtilityMeasurementStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "data-utility-measurement";
    public override string DisplayName => "Data Utility Measurement";
    public override PrivacyCategory Category => PrivacyCategory.PrivacyMetrics;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Measure data utility after privacy transformations";
    public override string[] Tags => ["metrics", "utility", "measurement"];
}

public sealed class PrivacyUtilityTradeoffStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "privacy-utility-tradeoff";
    public override string DisplayName => "Privacy-Utility Tradeoff Analysis";
    public override PrivacyCategory Category => PrivacyCategory.PrivacyMetrics;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Analyze tradeoffs between privacy and data utility";
    public override string[] Tags => ["metrics", "tradeoff", "analysis"];
}

public sealed class SensitivityAnalysisStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "sensitivity-analysis";
    public override string DisplayName => "Sensitivity Analysis";
    public override PrivacyCategory Category => PrivacyCategory.PrivacyMetrics;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Analyze sensitivity of data attributes to privacy attacks";
    public override string[] Tags => ["metrics", "sensitivity", "vulnerability"];
}

public sealed class LinkageRiskAssessmentStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "linkage-risk-assessment";
    public override string DisplayName => "Linkage Risk Assessment";
    public override PrivacyCategory Category => PrivacyCategory.PrivacyMetrics;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Assess risk of linking anonymized data to external sources";
    public override string[] Tags => ["metrics", "linkage", "risk"];
}

public sealed class PrivacyBudgetConsumptionStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "privacy-budget-consumption";
    public override string DisplayName => "Privacy Budget Consumption";
    public override PrivacyCategory Category => PrivacyCategory.PrivacyMetrics;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = false, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Track and report privacy budget consumption over time";
    public override string[] Tags => ["metrics", "budget", "consumption"];
}

public sealed class AnonymityMeasurementStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "anonymity-measurement";
    public override string DisplayName => "Anonymity Measurement";
    public override PrivacyCategory Category => PrivacyCategory.PrivacyMetrics;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Measure degree of anonymity in transformed data";
    public override string[] Tags => ["metrics", "anonymity", "measurement"];
}
