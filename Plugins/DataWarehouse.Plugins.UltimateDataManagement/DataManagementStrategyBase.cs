using System.Diagnostics;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement;

/// <summary>
/// Defines the category of data management strategy.
/// </summary>
public enum DataManagementCategory
{
    /// <summary>
    /// Caching strategies for in-memory or distributed caching.
    /// </summary>
    Caching,

    /// <summary>
    /// Indexing strategies for full-text, metadata, or semantic search.
    /// </summary>
    Indexing,

    /// <summary>
    /// Write destination strategies for fan-out orchestration.
    /// </summary>
    WriteDestination,

    /// <summary>
    /// Data transformation strategies.
    /// </summary>
    Transformation,

    /// <summary>
    /// Metadata management strategies.
    /// </summary>
    Metadata,

    /// <summary>
    /// Data lifecycle management strategies.
    /// </summary>
    Lifecycle,

    /// <summary>
    /// Data versioning strategies for version control and history.
    /// </summary>
    Versioning,

    /// <summary>
    /// Data branching strategies for Git-for-Data functionality.
    /// </summary>
    Branching,

    /// <summary>
    /// Event sourcing strategies for event-driven architectures.
    /// </summary>
    EventSourcing,

    /// <summary>
    /// Data fabric strategies for distributed data architecture (topology, virtualization, mesh, semantic layer).
    /// Merged from UltimateDataFabric plugin (T137).
    /// </summary>
    Fabric
}

/// <summary>
/// Defines the capabilities of a data management strategy.
/// </summary>
public sealed record DataManagementCapabilities
{
    /// <summary>
    /// Whether the strategy supports async operations.
    /// </summary>
    public required bool SupportsAsync { get; init; }

    /// <summary>
    /// Whether the strategy supports batch operations.
    /// </summary>
    public required bool SupportsBatch { get; init; }

    /// <summary>
    /// Whether the strategy supports distributed operations.
    /// </summary>
    public required bool SupportsDistributed { get; init; }

    /// <summary>
    /// Whether the strategy supports transactional operations.
    /// </summary>
    public required bool SupportsTransactions { get; init; }

    /// <summary>
    /// Whether the strategy supports TTL (time-to-live) for data.
    /// </summary>
    public required bool SupportsTTL { get; init; }

    /// <summary>
    /// Maximum throughput in operations per second (0 = unlimited).
    /// </summary>
    public long MaxThroughput { get; init; } = 0;

    /// <summary>
    /// Typical latency in milliseconds for operations.
    /// </summary>
    public double TypicalLatencyMs { get; init; } = 1.0;
}

/// <summary>
/// Statistics tracking for data management operations.
/// </summary>
public sealed class DataManagementStatistics
{
    /// <summary>
    /// Total number of read operations.
    /// </summary>
    public long TotalReads { get; set; }

    /// <summary>
    /// Total number of write operations.
    /// </summary>
    public long TotalWrites { get; set; }

    /// <summary>
    /// Total number of delete operations.
    /// </summary>
    public long TotalDeletes { get; set; }

    /// <summary>
    /// Total number of cache hits.
    /// </summary>
    public long CacheHits { get; set; }

    /// <summary>
    /// Total number of cache misses.
    /// </summary>
    public long CacheMisses { get; set; }

    /// <summary>
    /// Total bytes read.
    /// </summary>
    public long TotalBytesRead { get; set; }

    /// <summary>
    /// Total bytes written.
    /// </summary>
    public long TotalBytesWritten { get; set; }

    /// <summary>
    /// Total time spent on operations in milliseconds.
    /// </summary>
    public double TotalTimeMs { get; set; }

    /// <summary>
    /// Total number of failures.
    /// </summary>
    public long TotalFailures { get; set; }

    /// <summary>
    /// Gets the cache hit ratio.
    /// </summary>
    public double CacheHitRatio =>
        (CacheHits + CacheMisses) > 0 ? (double)CacheHits / (CacheHits + CacheMisses) : 0;

    /// <summary>
    /// Gets the average latency per operation in milliseconds.
    /// </summary>
    public double AverageLatencyMs
    {
        get
        {
            var total = TotalReads + TotalWrites + TotalDeletes;
            return total > 0 ? TotalTimeMs / total : 0;
        }
    }
}

/// <summary>
/// Interface for data management strategies.
/// </summary>
public interface IDataManagementStrategy
{
    /// <summary>
    /// Unique identifier for this strategy.
    /// </summary>
    string StrategyId { get; }

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Category of this strategy.
    /// </summary>
    DataManagementCategory Category { get; }

    /// <summary>
    /// Capabilities of this strategy.
    /// </summary>
    DataManagementCapabilities Capabilities { get; }

    /// <summary>
    /// Semantic description for AI discovery.
    /// </summary>
    string SemanticDescription { get; }

    /// <summary>
    /// Tags for categorization and discovery.
    /// </summary>
    string[] Tags { get; }

    /// <summary>
    /// Gets statistics for this strategy.
    /// </summary>
    DataManagementStatistics GetStatistics();

    /// <summary>
    /// Resets the statistics.
    /// </summary>
    void ResetStatistics();

    /// <summary>
    /// Initializes the strategy.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Disposes of the strategy resources.
    /// </summary>
    Task DisposeAsync();
}

/// <summary>
/// Abstract base class for data management strategies.
/// Provides common functionality including statistics tracking and validation.
/// </summary>
public abstract class DataManagementStrategyBase : StrategyBase, IDataManagementStrategy
{
    private readonly DataManagementStatistics _statistics = new();
    private readonly object _statsLock = new();
    private new bool _initialized;

    /// <inheritdoc/>
    public abstract override string StrategyId { get; }

    /// <inheritdoc/>
    public abstract string DisplayName { get; }

    /// <summary>
    /// Bridges StrategyBase.Name to the domain-specific DisplayName property.
    /// </summary>
    public override string Name => DisplayName;

    /// <inheritdoc/>
    public abstract DataManagementCategory Category { get; }

    /// <inheritdoc/>
    public abstract DataManagementCapabilities Capabilities { get; }

    /// <inheritdoc/>
    public abstract string SemanticDescription { get; }

    /// <inheritdoc/>
    public abstract string[] Tags { get; }

    /// <summary>
    /// Gets whether the strategy has been initialized.
    /// </summary>
    protected new bool IsInitialized => _initialized;

    /// <inheritdoc/>
    public DataManagementStatistics GetStatistics()
    {
        lock (_statsLock)
        {
            return new DataManagementStatistics
            {
                TotalReads = _statistics.TotalReads,
                TotalWrites = _statistics.TotalWrites,
                TotalDeletes = _statistics.TotalDeletes,
                CacheHits = _statistics.CacheHits,
                CacheMisses = _statistics.CacheMisses,
                TotalBytesRead = _statistics.TotalBytesRead,
                TotalBytesWritten = _statistics.TotalBytesWritten,
                TotalTimeMs = _statistics.TotalTimeMs,
                TotalFailures = _statistics.TotalFailures
            };
        }
    }

    /// <inheritdoc/>
    public void ResetStatistics()
    {
        lock (_statsLock)
        {
            _statistics.TotalReads = 0;
            _statistics.TotalWrites = 0;
            _statistics.TotalDeletes = 0;
            _statistics.CacheHits = 0;
            _statistics.CacheMisses = 0;
            _statistics.TotalBytesRead = 0;
            _statistics.TotalBytesWritten = 0;
            _statistics.TotalTimeMs = 0;
            _statistics.TotalFailures = 0;
        }
    }

    /// <inheritdoc/>
    public new virtual async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await InitializeCoreAsync(ct);
        _initialized = true;
    }

    /// <inheritdoc/>
    public new virtual async Task DisposeAsync()
    {
        if (!_initialized) return;
        await DisposeCoreAsync();
        _initialized = false;
    }

    /// <summary>
    /// Core initialization logic. Override in derived classes.
    /// </summary>
    protected virtual Task InitializeCoreAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Core disposal logic. Override in derived classes.
    /// </summary>
    protected virtual Task DisposeCoreAsync() => Task.CompletedTask;

    /// <summary>
    /// Records a read operation.
    /// </summary>
    protected void RecordRead(long bytesRead, double timeMs, bool hit = false, bool miss = false)
    {
        lock (_statsLock)
        {
            _statistics.TotalReads++;
            _statistics.TotalBytesRead += bytesRead;
            _statistics.TotalTimeMs += timeMs;
            if (hit) _statistics.CacheHits++;
            if (miss) _statistics.CacheMisses++;
        }
    }

    /// <summary>
    /// Records a write operation.
    /// </summary>
    protected void RecordWrite(long bytesWritten, double timeMs)
    {
        lock (_statsLock)
        {
            _statistics.TotalWrites++;
            _statistics.TotalBytesWritten += bytesWritten;
            _statistics.TotalTimeMs += timeMs;
        }
    }

    /// <summary>
    /// Records a delete operation.
    /// </summary>
    protected void RecordDelete(double timeMs)
    {
        lock (_statsLock)
        {
            _statistics.TotalDeletes++;
            _statistics.TotalTimeMs += timeMs;
        }
    }

    /// <summary>
    /// Records a failure.
    /// </summary>
    protected void RecordFailure()
    {
        lock (_statsLock)
        {
            _statistics.TotalFailures++;
        }
    }

    /// <summary>
    /// Throws if the strategy has not been initialized.
    /// </summary>
    protected new void ThrowIfNotInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException($"Strategy '{StrategyId}' has not been initialized.");
    }
}

/// <summary>
/// Thread-safe registry for data management strategies.
/// </summary>
public sealed class DataManagementStrategyRegistry
{
    private readonly BoundedDictionary<string, IDataManagementStrategy> _strategies =
        new(1000);

    /// <summary>
    /// Registers a strategy.
    /// </summary>
    public void Register(IDataManagementStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _strategies[strategy.StrategyId] = strategy;
    }

    /// <summary>
    /// Unregisters a strategy by ID.
    /// </summary>
    public bool Unregister(string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        return _strategies.TryRemove(strategyId, out _);
    }

    /// <summary>
    /// Gets a strategy by ID.
    /// </summary>
    public IDataManagementStrategy? Get(string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        return _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;
    }

    /// <summary>
    /// Gets all registered strategies.
    /// </summary>
    public IReadOnlyCollection<IDataManagementStrategy> GetAll()
    {
        return _strategies.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// Gets strategies by category.
    /// </summary>
    public IReadOnlyCollection<IDataManagementStrategy> GetByCategory(DataManagementCategory category)
    {
        return _strategies.Values
            .Where(s => s.Category == category)
            .OrderBy(s => s.DisplayName)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Gets the count of registered strategies.
    /// </summary>
    public int Count => _strategies.Count;

    /// <summary>
    /// Auto-discovers and registers strategies from assemblies.
    /// </summary>
    public int AutoDiscover(params System.Reflection.Assembly[] assemblies)
    {
        var strategyType = typeof(IDataManagementStrategy);
        int discovered = 0;

        foreach (var assembly in assemblies)
        {
            try
            {
                var types = assembly.GetTypes()
                    .Where(t => !t.IsAbstract && !t.IsInterface && strategyType.IsAssignableFrom(t));

                foreach (var type in types)
                {
                    try
                    {
                        if (Activator.CreateInstance(type) is IDataManagementStrategy strategy)
                        {
                            Register(strategy);
                            discovered++;
                        }
                    }
                    catch
                    {
                        // Skip types that cannot be instantiated
                    }
                }
            }
            catch
            {
                // Skip assemblies that cannot be scanned
            }
        }

        return discovered;
    }
}
