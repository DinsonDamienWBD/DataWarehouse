namespace DataWarehouse.Plugins.UltimateDataPrivacy.Strategies.Pseudonymization;

public sealed class DeterministicPseudonymizationStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "deterministic-pseudonymization";
    public override string DisplayName => "Deterministic Pseudonymization";
    public override PrivacyCategory Category => PrivacyCategory.Pseudonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = true, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Deterministic pseudonymization with consistent mapping";
    public override string[] Tags => ["pseudonymization", "deterministic", "consistent"];
}

public sealed class FormatPreservingPseudonymizationStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "format-preserving-pseudonymization";
    public override string DisplayName => "Format-Preserving Pseudonymization";
    public override PrivacyCategory Category => PrivacyCategory.Pseudonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = true, SupportsFormatPreserving = true };
    public override string SemanticDescription => "Pseudonymization preserving original data format and structure";
    public override string[] Tags => ["pseudonymization", "format-preserving", "structure"];
}

public sealed class ReversiblePseudonymizationStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "reversible-pseudonymization";
    public override string DisplayName => "Reversible Pseudonymization";
    public override PrivacyCategory Category => PrivacyCategory.Pseudonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = true, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Reversible pseudonymization with secure key management";
    public override string[] Tags => ["pseudonymization", "reversible", "key-management"];
}

public sealed class HashBasedPseudonymizationStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "hash-based-pseudonymization";
    public override string DisplayName => "Hash-Based Pseudonymization";
    public override PrivacyCategory Category => PrivacyCategory.Pseudonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Hash-based pseudonymization using cryptographic hashing";
    public override string[] Tags => ["pseudonymization", "hashing", "cryptographic"];
}

public sealed class EncryptionBasedPseudonymizationStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "encryption-based-pseudonymization";
    public override string DisplayName => "Encryption-Based Pseudonymization";
    public override PrivacyCategory Category => PrivacyCategory.Pseudonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = true, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Pseudonymization using symmetric or asymmetric encryption";
    public override string[] Tags => ["pseudonymization", "encryption", "reversible"];
}

public sealed class PseudonymMappingStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "pseudonym-mapping";
    public override string DisplayName => "Pseudonym Mapping";
    public override PrivacyCategory Category => PrivacyCategory.Pseudonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = true, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Maintain secure mapping between identifiers and pseudonyms";
    public override string[] Tags => ["pseudonymization", "mapping", "lookup"];
}

public sealed class KeyedPseudonymizationStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "keyed-pseudonymization";
    public override string DisplayName => "Keyed Pseudonymization";
    public override PrivacyCategory Category => PrivacyCategory.Pseudonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = true, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Pseudonymization with cryptographic keys and key rotation";
    public override string[] Tags => ["pseudonymization", "keyed", "key-rotation"];
}

public sealed class CrossDatasetPseudonymizationStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "cross-dataset-pseudonymization";
    public override string DisplayName => "Cross-Dataset Pseudonymization";
    public override PrivacyCategory Category => PrivacyCategory.Pseudonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = true, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Consistent pseudonymization across multiple datasets";
    public override string[] Tags => ["pseudonymization", "cross-dataset", "consistency"];
}
