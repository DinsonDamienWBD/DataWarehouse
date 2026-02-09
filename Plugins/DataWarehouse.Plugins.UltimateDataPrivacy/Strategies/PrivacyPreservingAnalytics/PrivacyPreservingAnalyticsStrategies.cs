namespace DataWarehouse.Plugins.UltimateDataPrivacy.Strategies.PrivacyPreservingAnalytics;

public sealed class SecureAggregationStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "secure-aggregation";
    public override string DisplayName => "Secure Aggregation";
    public override PrivacyCategory Category => PrivacyCategory.PrivacyPreservingAnalytics;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Secure multi-party aggregation without revealing individual values";
    public override string[] Tags => ["analytics", "aggregation", "secure"];
}

public sealed class HomomorphicEncryptionAnalyticsStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "homomorphic-encryption-analytics";
    public override string DisplayName => "Homomorphic Encryption Analytics";
    public override PrivacyCategory Category => PrivacyCategory.PrivacyPreservingAnalytics;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Perform analytics on encrypted data using homomorphic encryption";
    public override string[] Tags => ["analytics", "homomorphic", "encryption"];
}

public sealed class FederatedLearningStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "federated-learning";
    public override string DisplayName => "Federated Learning";
    public override PrivacyCategory Category => PrivacyCategory.PrivacyPreservingAnalytics;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Train ML models without centralizing sensitive data";
    public override string[] Tags => ["analytics", "federated-learning", "machine-learning"];
}

public sealed class SecureMultiPartyComputationStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "secure-multi-party-computation";
    public override string DisplayName => "Secure Multi-Party Computation";
    public override PrivacyCategory Category => PrivacyCategory.PrivacyPreservingAnalytics;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Collaborative computation without sharing raw data (MPC)";
    public override string[] Tags => ["analytics", "mpc", "secure-computation"];
}

public sealed class SyntheticDataAnalyticsStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "synthetic-data-analytics";
    public override string DisplayName => "Synthetic Data Analytics";
    public override PrivacyCategory Category => PrivacyCategory.PrivacyPreservingAnalytics;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Generate and analyze synthetic data preserving statistical properties";
    public override string[] Tags => ["analytics", "synthetic-data", "generation"];
}

public sealed class PrivateInformationRetrievalStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "private-information-retrieval";
    public override string DisplayName => "Private Information Retrieval";
    public override PrivacyCategory Category => PrivacyCategory.PrivacyPreservingAnalytics;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = false, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Query databases without revealing query content (PIR)";
    public override string[] Tags => ["analytics", "pir", "private-retrieval"];
}

public sealed class PrivateSetIntersectionStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "private-set-intersection";
    public override string DisplayName => "Private Set Intersection";
    public override PrivacyCategory Category => PrivacyCategory.PrivacyPreservingAnalytics;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Compute set intersection without revealing non-intersecting elements (PSI)";
    public override string[] Tags => ["analytics", "psi", "set-intersection"];
}

public sealed class ConfidentialComputingStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "confidential-computing";
    public override string DisplayName => "Confidential Computing";
    public override PrivacyCategory Category => PrivacyCategory.PrivacyPreservingAnalytics;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Perform analytics in trusted execution environments (TEE)";
    public override string[] Tags => ["analytics", "confidential-computing", "tee"];
}
