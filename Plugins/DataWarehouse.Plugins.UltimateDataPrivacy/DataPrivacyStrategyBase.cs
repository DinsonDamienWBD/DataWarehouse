using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataPrivacy;

/// <summary>Privacy category types corresponding to T124 sub-tasks.</summary>
public enum PrivacyCategory
{
    /// <summary>124.1: Anonymization</summary>
    Anonymization,
    /// <summary>124.2: Pseudonymization</summary>
    Pseudonymization,
    /// <summary>124.3: Tokenization</summary>
    Tokenization,
    /// <summary>124.4: Masking</summary>
    Masking,
    /// <summary>124.5: Differential Privacy</summary>
    DifferentialPrivacy,
    /// <summary>124.6: Privacy Compliance</summary>
    PrivacyCompliance,
    /// <summary>124.7: Privacy-Preserving Analytics</summary>
    PrivacyPreservingAnalytics,
    /// <summary>124.8: Privacy Metrics</summary>
    PrivacyMetrics,
    /// <summary>124.9: Data Classification (PII detection, sensitivity labeling)</summary>
    DataClassification
}

/// <summary>Capabilities of a data privacy strategy.</summary>
public sealed record DataPrivacyCapabilities
{
    public bool SupportsAsync { get; init; }
    public bool SupportsBatch { get; init; }
    public bool SupportsReversible { get; init; }
    public bool SupportsFormatPreserving { get; init; }
}

/// <summary>Interface for data privacy strategies.</summary>
public interface IDataPrivacyStrategy
{
    string StrategyId { get; }
    string DisplayName { get; }
    PrivacyCategory Category { get; }
    DataPrivacyCapabilities Capabilities { get; }
    string SemanticDescription { get; }
    string[] Tags { get; }
}

/// <summary>
/// Base class for data privacy strategies.
/// Provides production infrastructure via StrategyBase: lifecycle management, health checks, counters, graceful shutdown.
/// </summary>
public abstract class DataPrivacyStrategyBase : StrategyBase, IDataPrivacyStrategy
{
    public abstract override string StrategyId { get; }
    public abstract string DisplayName { get; }
    public override string Name => DisplayName;
    public abstract PrivacyCategory Category { get; }
    public abstract DataPrivacyCapabilities Capabilities { get; }
    public abstract string SemanticDescription { get; }
    public abstract string[] Tags { get; }

    /// <summary>Initializes the strategy. Idempotent.</summary>
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("initialized");
        await Task.CompletedTask;
    }

    /// <summary>Shuts down the strategy gracefully.</summary>
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("shutdown");
        await Task.CompletedTask;
    }

    /// <summary>Gets cached health status synchronously.</summary>
    // P2-2515: Avoid sync-over-async deadlock — derive health from initialization state directly
    // without invoking GetCachedHealthAsync. The health check factory only reads IsInitialized,
    // so we can compute the result inline.
    public bool IsHealthy() => IsInitialized;

    /// <summary>Gets all counter values.</summary>
    public IReadOnlyDictionary<string, long> GetCounters() => GetAllCounters();
}

/// <summary>Registry for data privacy strategies.</summary>
public sealed class DataPrivacyStrategyRegistry
{
    private readonly BoundedDictionary<string, IDataPrivacyStrategy> _strategies = new BoundedDictionary<string, IDataPrivacyStrategy>(1000);

    public int Count => _strategies.Count;

    public int AutoDiscover(System.Reflection.Assembly assembly)
    {
        var strategyType = typeof(IDataPrivacyStrategy);
        var count = 0;

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || !strategyType.IsAssignableFrom(type))
                continue;

            try
            {
                if (Activator.CreateInstance(type) is IDataPrivacyStrategy strategy)
                {
                    _strategies[strategy.StrategyId] = strategy;
                    count++;
                }
            }
            catch { /* Non-critical operation */ }
        }

        return count;
    }

    public IDataPrivacyStrategy? Get(string strategyId) =>
        _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;

    public IReadOnlyList<IDataPrivacyStrategy> GetAll() =>
        _strategies.Values.ToList().AsReadOnly();

    public IReadOnlyList<IDataPrivacyStrategy> GetByCategory(PrivacyCategory category) =>
        _strategies.Values.Where(s => s.Category == category).ToList().AsReadOnly();
}
