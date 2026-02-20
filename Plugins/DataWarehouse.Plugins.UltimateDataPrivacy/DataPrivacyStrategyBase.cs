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
    PrivacyMetrics
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
/// Provides production infrastructure: lifecycle management, health checks, counters, graceful shutdown.
/// </summary>
public abstract class DataPrivacyStrategyBase : IDataPrivacyStrategy
{
    private readonly BoundedDictionary<string, long> _counters = new BoundedDictionary<string, long>(1000);
    private bool _initialized;
    private DateTime? _healthCacheExpiry;
    private bool? _cachedHealthy;

    public abstract string StrategyId { get; }
    public abstract string DisplayName { get; }
    public abstract PrivacyCategory Category { get; }
    public abstract DataPrivacyCapabilities Capabilities { get; }
    public abstract string SemanticDescription { get; }
    public abstract string[] Tags { get; }

    /// <summary>Gets whether this strategy has been initialized.</summary>
    public bool IsInitialized => _initialized;

    /// <summary>Initializes the strategy. Idempotent.</summary>
    public virtual Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return Task.CompletedTask;
        _initialized = true;
        IncrementCounter("initialized");
        return Task.CompletedTask;
    }

    /// <summary>Shuts down the strategy gracefully.</summary>
    public virtual Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized) return Task.CompletedTask;
        _initialized = false;
        IncrementCounter("shutdown");
        return Task.CompletedTask;
    }

    /// <summary>Gets cached health status, refreshing every 60 seconds.</summary>
    public bool IsHealthy()
    {
        if (_cachedHealthy.HasValue && _healthCacheExpiry.HasValue && DateTime.UtcNow < _healthCacheExpiry.Value)
            return _cachedHealthy.Value;

        _cachedHealthy = _initialized;
        _healthCacheExpiry = DateTime.UtcNow.AddSeconds(60);
        return _cachedHealthy.Value;
    }

    /// <summary>Increments a named counter. Thread-safe.</summary>
    protected void IncrementCounter(string name)
    {
        _counters.AddOrUpdate(name, 1, (_, current) => Interlocked.Increment(ref current));
    }

    /// <summary>Gets all counter values.</summary>
    public IReadOnlyDictionary<string, long> GetCounters() => new Dictionary<string, long>(_counters);
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
