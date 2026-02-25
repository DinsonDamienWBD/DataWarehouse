namespace DataWarehouse.Plugins.UltimateDataPrivacy.Strategies.DifferentialPrivacy;

public sealed class LaplaceNoiseStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "laplace-noise";
    public override string DisplayName => "Laplace Noise Mechanism";
    public override PrivacyCategory Category => PrivacyCategory.DifferentialPrivacy;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Add Laplace noise for differential privacy guarantees";
    public override string[] Tags => ["differential-privacy", "laplace", "noise"];
}

public sealed class GaussianNoiseStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "gaussian-noise";
    public override string DisplayName => "Gaussian Noise Mechanism";
    public override PrivacyCategory Category => PrivacyCategory.DifferentialPrivacy;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Add Gaussian noise for differential privacy with concentrated guarantees";
    public override string[] Tags => ["differential-privacy", "gaussian", "noise"];
}

public sealed class ExponentialMechanismStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "exponential-mechanism";
    public override string DisplayName => "Exponential Mechanism";
    public override PrivacyCategory Category => PrivacyCategory.DifferentialPrivacy;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Exponential mechanism for selecting from discrete options";
    public override string[] Tags => ["differential-privacy", "exponential", "selection"];
}

public sealed class PrivacyBudgetManagementStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "privacy-budget-management";
    public override string DisplayName => "Privacy Budget Management";
    public override PrivacyCategory Category => PrivacyCategory.DifferentialPrivacy;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = false, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Track and manage privacy budget (epsilon) consumption";
    public override string[] Tags => ["differential-privacy", "budget", "epsilon"];
}

public sealed class CompositionAnalysisStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "composition-analysis";
    public override string DisplayName => "Composition Analysis";
    public override PrivacyCategory Category => PrivacyCategory.DifferentialPrivacy;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = false, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Analyze privacy loss under composition of multiple queries";
    public override string[] Tags => ["differential-privacy", "composition", "analysis"];
}

public sealed class LocalDifferentialPrivacyStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "local-differential-privacy";
    public override string DisplayName => "Local Differential Privacy";
    public override PrivacyCategory Category => PrivacyCategory.DifferentialPrivacy;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Local differential privacy with client-side noise addition";
    public override string[] Tags => ["differential-privacy", "local", "client-side"];
}

public sealed class GlobalDifferentialPrivacyStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "global-differential-privacy";
    public override string DisplayName => "Global Differential Privacy";
    public override PrivacyCategory Category => PrivacyCategory.DifferentialPrivacy;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Global differential privacy with centralized noise addition";
    public override string[] Tags => ["differential-privacy", "global", "centralized"];
}

public sealed class ApproximateDPStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "approximate-dp";
    public override string DisplayName => "Approximate Differential Privacy";
    public override PrivacyCategory Category => PrivacyCategory.DifferentialPrivacy;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Approximate (epsilon, delta)-differential privacy";
    public override string[] Tags => ["differential-privacy", "approximate", "delta"];
}
