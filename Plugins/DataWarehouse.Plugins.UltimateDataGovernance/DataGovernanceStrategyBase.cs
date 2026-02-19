using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateDataGovernance;

/// <summary>
/// Governance category types corresponding to T123 sub-tasks.
/// </summary>
public enum GovernanceCategory
{
    /// <summary>123.1: Policy Management</summary>
    PolicyManagement,
    /// <summary>123.2: Data Ownership</summary>
    DataOwnership,
    /// <summary>123.3: Data Stewardship</summary>
    DataStewardship,
    /// <summary>123.4: Data Classification</summary>
    DataClassification,
    /// <summary>123.5: Lineage Tracking</summary>
    LineageTracking,
    /// <summary>123.6: Retention Management</summary>
    RetentionManagement,
    /// <summary>123.7: Regulatory Compliance</summary>
    RegulatoryCompliance,
    /// <summary>123.8: Audit and Reporting</summary>
    AuditReporting
}

/// <summary>
/// Capabilities of a data governance strategy.
/// </summary>
public sealed record DataGovernanceCapabilities
{
    public bool SupportsAsync { get; init; }
    public bool SupportsBatch { get; init; }
    public bool SupportsRealTime { get; init; }
    public bool SupportsAudit { get; init; }
    public bool SupportsVersioning { get; init; }
}

/// <summary>
/// Interface for data governance strategies.
/// </summary>
public interface IDataGovernanceStrategy
{
    string StrategyId { get; }
    string DisplayName { get; }
    GovernanceCategory Category { get; }
    DataGovernanceCapabilities Capabilities { get; }
    string SemanticDescription { get; }
    string[] Tags { get; }
}

/// <summary>
/// Base class for data governance strategies.
/// Provides production infrastructure: lifecycle management, health checks, counters, graceful shutdown.
/// </summary>
public abstract class DataGovernanceStrategyBase : IDataGovernanceStrategy
{
    private readonly ConcurrentDictionary<string, long> _counters = new();
    private bool _initialized;
    private bool _disposed;
    private DateTime? _healthCacheExpiry;
    private HealthStatus? _cachedHealth;

    public abstract string StrategyId { get; }
    public abstract string DisplayName { get; }
    public abstract GovernanceCategory Category { get; }
    public abstract DataGovernanceCapabilities Capabilities { get; }
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

    /// <summary>Gets a cached health status, refreshing every 60 seconds.</summary>
    public HealthStatus GetHealth()
    {
        if (_cachedHealth.HasValue && _healthCacheExpiry.HasValue && DateTime.UtcNow < _healthCacheExpiry.Value)
            return _cachedHealth.Value;

        _cachedHealth = _initialized ? HealthStatus.Healthy : HealthStatus.NotInitialized;
        _healthCacheExpiry = DateTime.UtcNow.AddSeconds(60);
        return _cachedHealth.Value;
    }

    /// <summary>Increments a named counter. Thread-safe.</summary>
    protected void IncrementCounter(string name)
    {
        _counters.AddOrUpdate(name, 1, (_, current) => Interlocked.Increment(ref current));
    }

    /// <summary>Gets all counter values.</summary>
    public IReadOnlyDictionary<string, long> GetCounters() => new Dictionary<string, long>(_counters);
}

/// <summary>Health status for governance strategies.</summary>
public enum HealthStatus
{
    /// <summary>Strategy is healthy and operational.</summary>
    Healthy,
    /// <summary>Strategy has not been initialized.</summary>
    NotInitialized,
    /// <summary>Strategy is degraded.</summary>
    Degraded,
    /// <summary>Strategy is unhealthy.</summary>
    Unhealthy
}

/// <summary>
/// Registry for data governance strategies.
/// </summary>
public sealed class DataGovernanceStrategyRegistry
{
    private readonly ConcurrentDictionary<string, IDataGovernanceStrategy> _strategies = new();

    public int Count => _strategies.Count;

    public int AutoDiscover(System.Reflection.Assembly assembly)
    {
        var strategyType = typeof(IDataGovernanceStrategy);
        var count = 0;

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || !strategyType.IsAssignableFrom(type))
                continue;

            try
            {
                if (Activator.CreateInstance(type) is IDataGovernanceStrategy strategy)
                {
                    _strategies[strategy.StrategyId] = strategy;
                    count++;
                }
            }
            catch { /* Non-critical operation */ }
        }

        return count;
    }

    public IDataGovernanceStrategy? Get(string strategyId) =>
        _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;

    public IReadOnlyList<IDataGovernanceStrategy> GetAll() =>
        _strategies.Values.ToList().AsReadOnly();

    public IReadOnlyList<IDataGovernanceStrategy> GetByCategory(GovernanceCategory category) =>
        _strategies.Values.Where(s => s.Category == category).ToList().AsReadOnly();
}
