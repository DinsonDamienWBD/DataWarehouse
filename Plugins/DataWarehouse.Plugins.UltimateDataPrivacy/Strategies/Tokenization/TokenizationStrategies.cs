namespace DataWarehouse.Plugins.UltimateDataPrivacy.Strategies.Tokenization;

public sealed class VaultedTokenizationStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "vaulted-tokenization";
    public override string DisplayName => "Vaulted Tokenization";
    public override PrivacyCategory Category => PrivacyCategory.Tokenization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = true, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Vaulted tokenization with secure token vault storage";
    public override string[] Tags => ["tokenization", "vaulted", "secure-storage"];
}

public sealed class VaultlessTokenizationStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "vaultless-tokenization";
    public override string DisplayName => "Vaultless Tokenization";
    public override PrivacyCategory Category => PrivacyCategory.Tokenization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = true, SupportsFormatPreserving = true };
    public override string SemanticDescription => "Vaultless tokenization using cryptographic transformation";
    public override string[] Tags => ["tokenization", "vaultless", "cryptographic"];
}

public sealed class FormatPreservingTokenizationStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "format-preserving-tokenization";
    public override string DisplayName => "Format-Preserving Tokenization";
    public override PrivacyCategory Category => PrivacyCategory.Tokenization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = true, SupportsFormatPreserving = true };
    public override string SemanticDescription => "Tokenization preserving original data format (FPE)";
    public override string[] Tags => ["tokenization", "format-preserving", "fpe"];
}

public sealed class PCICompliantTokenizationStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "pci-compliant-tokenization";
    public override string DisplayName => "PCI-Compliant Tokenization";
    public override PrivacyCategory Category => PrivacyCategory.Tokenization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = true, SupportsFormatPreserving = true };
    public override string SemanticDescription => "PCI-DSS compliant tokenization for payment data";
    public override string[] Tags => ["tokenization", "pci-dss", "payment"];
}

public sealed class DeterministicTokenizationStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "deterministic-tokenization";
    public override string DisplayName => "Deterministic Tokenization";
    public override PrivacyCategory Category => PrivacyCategory.Tokenization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = true, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Deterministic tokenization with consistent token generation";
    public override string[] Tags => ["tokenization", "deterministic", "consistent"];
}

public sealed class RandomTokenizationStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "random-tokenization";
    public override string DisplayName => "Random Tokenization";
    public override PrivacyCategory Category => PrivacyCategory.Tokenization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = true, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Random tokenization with unpredictable tokens";
    public override string[] Tags => ["tokenization", "random", "unpredictable"];
}

public sealed class TokenLifecycleStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "token-lifecycle";
    public override string DisplayName => "Token Lifecycle Management";
    public override PrivacyCategory Category => PrivacyCategory.Tokenization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = true, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Manage token lifecycle including expiration and rotation";
    public override string[] Tags => ["tokenization", "lifecycle", "expiration"];
}

public sealed class HighValueTokenizationStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "high-value-tokenization";
    public override string DisplayName => "High-Value Tokenization";
    public override PrivacyCategory Category => PrivacyCategory.Tokenization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = true, SupportsFormatPreserving = true };
    public override string SemanticDescription => "Enhanced tokenization for high-value sensitive data";
    public override string[] Tags => ["tokenization", "high-value", "enhanced"];
}
