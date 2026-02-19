using System.Collections.Concurrent;
using System.Reflection;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Plugins.UltimateMultiCloud;

/// <summary>
/// Characteristics of a multi-cloud strategy.
/// </summary>
public sealed class MultiCloudCharacteristics
{
    /// <summary>Strategy name.</summary>
    public required string StrategyName { get; init; }

    /// <summary>Description.</summary>
    public required string Description { get; init; }

    /// <summary>Category.</summary>
    public required string Category { get; init; }

    /// <summary>Supports cross-cloud replication.</summary>
    public bool SupportsCrossCloudReplication { get; init; }

    /// <summary>Supports automatic failover.</summary>
    public bool SupportsAutomaticFailover { get; init; }

    /// <summary>Supports cost optimization.</summary>
    public bool SupportsCostOptimization { get; init; }

    /// <summary>Supports hybrid cloud.</summary>
    public bool SupportsHybridCloud { get; init; }

    /// <summary>Supports data sovereignty.</summary>
    public bool SupportsDataSovereignty { get; init; }

    /// <summary>Typical latency overhead in milliseconds.</summary>
    public double TypicalLatencyOverheadMs { get; init; } = 5.0;

    /// <summary>Memory footprint descriptor.</summary>
    public string MemoryFootprint { get; init; } = "Low";
}

/// <summary>
/// Multi-cloud operation result.
/// </summary>
public sealed class MultiCloudResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? SourceProvider { get; init; }
    public string? TargetProvider { get; init; }
    public TimeSpan Duration { get; init; }
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Multi-cloud strategy statistics.
/// </summary>
public sealed class MultiCloudStrategyStatistics
{
    public long TotalExecutions { get; set; }
    public long SuccessfulExecutions { get; set; }
    public long FailedExecutions { get; set; }
    public string? CurrentState { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public DateTimeOffset? LastFailure { get; set; }
}

/// <summary>
/// Interface for multi-cloud strategies.
/// </summary>
public interface IMultiCloudStrategy
{
    /// <summary>Strategy ID.</summary>
    string StrategyId { get; }

    /// <summary>Strategy name.</summary>
    string StrategyName { get; }

    /// <summary>Category.</summary>
    string Category { get; }

    /// <summary>Characteristics.</summary>
    MultiCloudCharacteristics Characteristics { get; }

    /// <summary>Gets statistics.</summary>
    MultiCloudStrategyStatistics GetStatistics();

    /// <summary>Resets state.</summary>
    void Reset();
}

/// <summary>
/// Interface for strategy registry.
/// </summary>
public interface IMultiCloudStrategyRegistry
{
    IReadOnlyList<IMultiCloudStrategy> GetAllStrategies();
    IReadOnlyList<IMultiCloudStrategy> GetStrategiesByCategory(string category);
    IMultiCloudStrategy? GetStrategy(string strategyId);
}

/// <summary>
/// Base class for multi-cloud strategies.
/// </summary>
public abstract class MultiCloudStrategyBase : IMultiCloudStrategy
{
    private long _totalExecutions;
    private long _successfulExecutions;
    private long _failedExecutions;
    private DateTimeOffset? _lastSuccess;
    private DateTimeOffset? _lastFailure;
    private readonly ConcurrentDictionary<string, long> _counters = new();
    private bool _initialized;
    private DateTime? _healthCacheExpiry;
    private bool? _cachedHealthy;
    protected IMessageBus? MessageBus { get; private set; }

    /// <inheritdoc/>
    public abstract string StrategyId { get; }

    /// <inheritdoc/>
    public abstract string StrategyName { get; }

    /// <inheritdoc/>
    public abstract string Category { get; }

    /// <inheritdoc/>
    public abstract MultiCloudCharacteristics Characteristics { get; }

    /// <summary>
    /// Configures intelligence integration.
    /// </summary>
    public void ConfigureIntelligence(IMessageBus messageBus)
    {
        MessageBus = messageBus;
    }

    /// <inheritdoc/>
    public MultiCloudStrategyStatistics GetStatistics()
    {
        return new MultiCloudStrategyStatistics
        {
            TotalExecutions = Interlocked.Read(ref _totalExecutions),
            SuccessfulExecutions = Interlocked.Read(ref _successfulExecutions),
            FailedExecutions = Interlocked.Read(ref _failedExecutions),
            CurrentState = GetCurrentState(),
            LastSuccess = _lastSuccess,
            LastFailure = _lastFailure
        };
    }

    /// <inheritdoc/>
    public virtual void Reset()
    {
        Interlocked.Exchange(ref _totalExecutions, 0);
        Interlocked.Exchange(ref _successfulExecutions, 0);
        Interlocked.Exchange(ref _failedExecutions, 0);
        _lastSuccess = null;
        _lastFailure = null;
    }

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

    /// <summary>
    /// Gets current state description.
    /// </summary>
    protected virtual string? GetCurrentState() => null;

    /// <summary>
    /// Records a successful execution.
    /// </summary>
    protected void RecordSuccess()
    {
        Interlocked.Increment(ref _totalExecutions);
        Interlocked.Increment(ref _successfulExecutions);
        _lastSuccess = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Records a failed execution.
    /// </summary>
    protected void RecordFailure()
    {
        Interlocked.Increment(ref _totalExecutions);
        Interlocked.Increment(ref _failedExecutions);
        _lastFailure = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Strategy registry implementation.
/// </summary>
public sealed class MultiCloudStrategyRegistry : IMultiCloudStrategyRegistry
{
    private readonly ConcurrentDictionary<string, IMultiCloudStrategy> _strategies = new();

    /// <summary>
    /// Discovers and registers strategies from an assembly.
    /// </summary>
    public void DiscoverStrategies(Assembly assembly)
    {
        var strategyTypes = assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && typeof(MultiCloudStrategyBase).IsAssignableFrom(t));

        foreach (var type in strategyTypes)
        {
            try
            {
                if (Activator.CreateInstance(type) is MultiCloudStrategyBase strategy)
                {
                    _strategies[strategy.StrategyId] = strategy;
                }
            }
            catch
            {
                // Skip strategies that fail to instantiate
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<IMultiCloudStrategy> GetAllStrategies()
    {
        return _strategies.Values.ToList();
    }

    /// <inheritdoc/>
    public IReadOnlyList<IMultiCloudStrategy> GetStrategiesByCategory(string category)
    {
        return _strategies.Values
            .Where(s => s.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <inheritdoc/>
    public IMultiCloudStrategy? GetStrategy(string strategyId)
    {
        return _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;
    }
}
