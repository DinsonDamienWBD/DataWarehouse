namespace DataWarehouse.Plugins.UltimateDataPrivacy.Strategies.Anonymization;

public sealed class KAnonymityStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "k-anonymity";
    public override string DisplayName => "K-Anonymity";
    public override PrivacyCategory Category => PrivacyCategory.Anonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "K-anonymity anonymization ensuring each record is indistinguishable from k-1 others";
    public override string[] Tags => ["anonymization", "k-anonymity", "privacy-protection"];
}

public sealed class LDiversityStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "l-diversity";
    public override string DisplayName => "L-Diversity";
    public override PrivacyCategory Category => PrivacyCategory.Anonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "L-diversity anonymization ensuring sensitive attributes have diverse values";
    public override string[] Tags => ["anonymization", "l-diversity", "attribute-diversity"];
}

public sealed class TClosenessStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "t-closeness";
    public override string DisplayName => "T-Closeness";
    public override PrivacyCategory Category => PrivacyCategory.Anonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "T-closeness anonymization ensuring attribute distribution is close to global distribution";
    public override string[] Tags => ["anonymization", "t-closeness", "distribution"];
}

public sealed class DataSuppressionStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "data-suppression";
    public override string DisplayName => "Data Suppression";
    public override PrivacyCategory Category => PrivacyCategory.Anonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Suppress sensitive data by removing or redacting fields";
    public override string[] Tags => ["anonymization", "suppression", "redaction"];
}

public sealed class GeneralizationStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "generalization";
    public override string DisplayName => "Data Generalization";
    public override PrivacyCategory Category => PrivacyCategory.Anonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Generalize data by replacing specific values with broader categories";
    public override string[] Tags => ["anonymization", "generalization", "abstraction"];
}

public sealed class DataSwappingStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "data-swapping";
    public override string DisplayName => "Data Swapping";
    public override PrivacyCategory Category => PrivacyCategory.Anonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = true };
    public override string SemanticDescription => "Swap values between records to preserve statistics while anonymizing";
    public override string[] Tags => ["anonymization", "swapping", "permutation"];
}

public sealed class DataPerturbationStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "data-perturbation";
    public override string DisplayName => "Data Perturbation";
    public override PrivacyCategory Category => PrivacyCategory.Anonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Add controlled noise to data values for anonymization";
    public override string[] Tags => ["anonymization", "perturbation", "noise"];
}

public sealed class TopBottomCodingStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "top-bottom-coding";
    public override string DisplayName => "Top/Bottom Coding";
    public override PrivacyCategory Category => PrivacyCategory.Anonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Replace extreme values with threshold values to prevent identification";
    public override string[] Tags => ["anonymization", "coding", "outliers"];
}

public sealed class SyntheticDataGenerationStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "synthetic-data-generation";
    public override string DisplayName => "Synthetic Data Generation";
    public override PrivacyCategory Category => PrivacyCategory.Anonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Generate synthetic data with similar statistical properties";
    public override string[] Tags => ["anonymization", "synthetic", "generation"];
}
