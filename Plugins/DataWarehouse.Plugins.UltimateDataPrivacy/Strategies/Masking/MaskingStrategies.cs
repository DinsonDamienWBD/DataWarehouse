namespace DataWarehouse.Plugins.UltimateDataPrivacy.Strategies.Masking;

public sealed class StaticDataMaskingStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "static-data-masking";
    public override string DisplayName => "Static Data Masking";
    public override PrivacyCategory Category => PrivacyCategory.Masking;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = true };
    public override string SemanticDescription => "Static masking for non-production environments";
    public override string[] Tags => ["masking", "static", "non-production"];
}

public sealed class DynamicDataMaskingStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "dynamic-data-masking";
    public override string DisplayName => "Dynamic Data Masking";
    public override PrivacyCategory Category => PrivacyCategory.Masking;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = false, SupportsReversible = false, SupportsFormatPreserving = true };
    public override string SemanticDescription => "Real-time dynamic masking based on access policies";
    public override string[] Tags => ["masking", "dynamic", "real-time"];
}

public sealed class DeterministicMaskingStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "deterministic-masking";
    public override string DisplayName => "Deterministic Masking";
    public override PrivacyCategory Category => PrivacyCategory.Masking;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = true };
    public override string SemanticDescription => "Consistent masking with deterministic rules";
    public override string[] Tags => ["masking", "deterministic", "consistent"];
}

public sealed class ConditionalMaskingStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "conditional-masking";
    public override string DisplayName => "Conditional Masking";
    public override PrivacyCategory Category => PrivacyCategory.Masking;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = false, SupportsReversible = false, SupportsFormatPreserving = true };
    public override string SemanticDescription => "Context-aware masking based on conditions and rules";
    public override string[] Tags => ["masking", "conditional", "context-aware"];
}

public sealed class PartialMaskingStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "partial-masking";
    public override string DisplayName => "Partial Masking";
    public override PrivacyCategory Category => PrivacyCategory.Masking;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = true };
    public override string SemanticDescription => "Partial masking revealing only selected characters";
    public override string[] Tags => ["masking", "partial", "selective"];
}

public sealed class RedactionMaskingStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "redaction-masking";
    public override string DisplayName => "Redaction Masking";
    public override PrivacyCategory Category => PrivacyCategory.Masking;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Complete redaction replacing sensitive data with placeholder";
    public override string[] Tags => ["masking", "redaction", "placeholder"];
}

public sealed class SubstitutionMaskingStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "substitution-masking";
    public override string DisplayName => "Substitution Masking";
    public override PrivacyCategory Category => PrivacyCategory.Masking;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = true };
    public override string SemanticDescription => "Substitute sensitive data with realistic fake values";
    public override string[] Tags => ["masking", "substitution", "realistic"];
}

public sealed class ShufflingMaskingStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "shuffling-masking";
    public override string DisplayName => "Shuffling Masking";
    public override PrivacyCategory Category => PrivacyCategory.Masking;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = true };
    public override string SemanticDescription => "Shuffle data values to break original associations";
    public override string[] Tags => ["masking", "shuffling", "permutation"];
}
