using System.Collections.Concurrent;
using System.Reflection;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.UltimateDataIntegration;

/// <summary>
/// Ultimate Data Integration Plugin - Comprehensive data integration solution (T126).
///
/// Implements 70+ data integration strategies across categories:
/// - 126.1: ETL Pipelines (Classic ETL, Streaming ETL, Micro-batch ETL)
/// - 126.2: ELT Patterns (Cloud-native ELT, dbt-style transformations)
/// - 126.3: Data Transformation (Type conversion, aggregation, cleansing)
/// - 126.4: Data Mapping (Schema mapping, field mapping, semantic mapping)
/// - 126.5: Schema Evolution (Forward/backward compatibility, migration)
/// - 126.6: CDC - Change Data Capture (Log-based, trigger-based, timestamp-based)
/// - 126.7: Batch vs Streaming Integration (Lambda, Kappa, unified architectures)
/// - 126.8: Integration Monitoring (Health checks, SLA tracking, alerts)
///
/// Features:
/// - Strategy pattern for extensibility
/// - Auto-discovery of strategies via reflection
/// - Unified API for data integration operations
/// - Intelligence-aware for AI-enhanced recommendations
/// - Multi-tenant support
/// - Production-ready fault tolerance
/// - Horizontal scalability
/// - Performance metrics and monitoring
/// </summary>
public sealed class UltimateDataIntegrationPlugin : IntelligenceAwarePluginBase, IDisposable
{
    private readonly DataIntegrationStrategyRegistry _registry;
    private readonly ConcurrentDictionary<string, long> _usageStats = new();
    private readonly ConcurrentDictionary<string, IntegrationPolicy> _policies = new();
    private bool _disposed;

    // Configuration
    private volatile bool _auditEnabled = true;
    private volatile bool _autoOptimizationEnabled = true;

    // Statistics - accessed via GetStatistics(), incremented by RecordOperation/RecordFailure
    private long _totalOperations;
    private long _totalRecordsProcessed;
    private long _totalFailures;

    /// <summary>
    /// Records an operation and updates statistics.
    /// </summary>
    public void RecordOperation(int recordCount = 1)
    {
        Interlocked.Increment(ref _totalOperations);
        Interlocked.Add(ref _totalRecordsProcessed, recordCount);
    }

    /// <summary>
    /// Records a failure.
    /// </summary>
    public void RecordFailure()
    {
        Interlocked.Increment(ref _totalFailures);
    }

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.integration.ultimate";

    /// <inheritdoc/>
    public override string Name => "Ultimate Data Integration";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.OrchestrationProvider;

    /// <summary>
    /// Semantic description of this plugin for AI discovery.
    /// </summary>
    public string SemanticDescription =>
        "Ultimate data integration plugin providing 70+ strategies including ETL/ELT pipelines, " +
        "data transformation, schema mapping, schema evolution, change data capture (CDC), " +
        "batch/streaming integration patterns, and comprehensive monitoring. " +
        "Supports exactly-once semantics, schema versioning, and production-ready deployment.";

    /// <summary>
    /// Semantic tags for AI discovery and categorization.
    /// </summary>
    public string[] SemanticTags => [
        "integration", "etl", "elt", "transformation", "mapping",
        "schema-evolution", "cdc", "batch", "streaming", "monitoring"
    ];

    /// <summary>
    /// Gets the data integration strategy registry.
    /// </summary>
    public DataIntegrationStrategyRegistry Registry => _registry;

    /// <summary>
    /// Gets or sets whether audit logging is enabled.
    /// </summary>
    public bool AuditEnabled
    {
        get => _auditEnabled;
        set => _auditEnabled = value;
    }

    /// <summary>
    /// Gets or sets whether automatic optimization is enabled.
    /// </summary>
    public bool AutoOptimizationEnabled
    {
        get => _autoOptimizationEnabled;
        set => _autoOptimizationEnabled = value;
    }

    public UltimateDataIntegrationPlugin()
    {
        _registry = new DataIntegrationStrategyRegistry();
        DiscoverAndRegisterStrategies();
    }

    private void DiscoverAndRegisterStrategies()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var strategyTypes = assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(IDataIntegrationStrategy).IsAssignableFrom(t));

        foreach (var type in strategyTypes)
        {
            try
            {
                if (Activator.CreateInstance(type) is IDataIntegrationStrategy strategy)
                {
                    _registry.Register(strategy);
                }
            }
            catch
            {
                // Skip strategies that fail to instantiate
            }
        }
    }

    /// <summary>
    /// Gets a data integration strategy by ID.
    /// </summary>
    public IDataIntegrationStrategy? GetStrategy(string strategyId) =>
        _registry.GetStrategy(strategyId);

    /// <summary>
    /// Gets all strategies of a specific category.
    /// </summary>
    public IEnumerable<IDataIntegrationStrategy> GetStrategiesByCategory(IntegrationCategory category) =>
        _registry.GetByCategory(category);

    /// <summary>
    /// Gets plugin statistics.
    /// </summary>
    public IntegrationStatistics GetStatistics() => new()
    {
        TotalOperations = Interlocked.Read(ref _totalOperations),
        TotalRecordsProcessed = Interlocked.Read(ref _totalRecordsProcessed),
        TotalFailures = Interlocked.Read(ref _totalFailures),
        RegisteredStrategies = _registry.Count,
        ActivePolicies = _policies.Count,
        UsageByStrategy = _usageStats.ToDictionary(k => k.Key, v => v.Value)
    };

    private new void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(UltimateDataIntegrationPlugin));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var strategy in _registry.GetAllStrategies())
            {
            if (strategy is IDisposable disposable)
            {
            disposable.Dispose();
            }
            }
        }
        base.Dispose(disposing);
    }
}

#region Core Interfaces and Types

/// <summary>
/// Base interface for all data integration strategies.
/// </summary>
public interface IDataIntegrationStrategy
{
    /// <summary>Gets the unique strategy identifier.</summary>
    string StrategyId { get; }

    /// <summary>Gets the display name.</summary>
    string DisplayName { get; }

    /// <summary>Gets the integration category.</summary>
    IntegrationCategory Category { get; }

    /// <summary>Gets the capabilities.</summary>
    DataIntegrationCapabilities Capabilities { get; }

    /// <summary>Gets the semantic description for AI discovery.</summary>
    string SemanticDescription { get; }

    /// <summary>Gets tags for categorization.</summary>
    string[] Tags { get; }
}

/// <summary>
/// Data integration strategy categories corresponding to T126 sub-tasks.
/// </summary>
public enum IntegrationCategory
{
    /// <summary>126.1: ETL Pipelines</summary>
    EtlPipelines,

    /// <summary>126.2: ELT Patterns</summary>
    EltPatterns,

    /// <summary>126.3: Data Transformation</summary>
    DataTransformation,

    /// <summary>126.4: Data Mapping</summary>
    DataMapping,

    /// <summary>126.5: Schema Evolution</summary>
    SchemaEvolution,

    /// <summary>126.6: CDC (Change Data Capture)</summary>
    ChangeDataCapture,

    /// <summary>126.7: Batch vs Streaming Integration</summary>
    BatchStreamingIntegration,

    /// <summary>126.8: Integration Monitoring</summary>
    IntegrationMonitoring
}

/// <summary>
/// Capabilities of a data integration strategy.
/// </summary>
public sealed record DataIntegrationCapabilities
{
    public bool SupportsAsync { get; init; }
    public bool SupportsBatch { get; init; }
    public bool SupportsStreaming { get; init; }
    public bool SupportsExactlyOnce { get; init; }
    public bool SupportsSchemaEvolution { get; init; }
    public bool SupportsIncremental { get; init; }
    public bool SupportsParallel { get; init; }
    public bool SupportsDistributed { get; init; }
    public long MaxThroughputRecordsPerSec { get; init; }
    public double TypicalLatencyMs { get; init; }
}

/// <summary>
/// Integration policy definition.
/// </summary>
public sealed record IntegrationPolicy
{
    public required string PolicyId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public IntegrationCategory TargetCategory { get; init; }
    public Dictionary<string, object>? Settings { get; init; }
    public bool IsEnabled { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Plugin statistics.
/// </summary>
public sealed record IntegrationStatistics
{
    public long TotalOperations { get; init; }
    public long TotalRecordsProcessed { get; init; }
    public long TotalFailures { get; init; }
    public int RegisteredStrategies { get; init; }
    public int ActivePolicies { get; init; }
    public Dictionary<string, long> UsageByStrategy { get; init; } = new();
}

#endregion

#region Strategy Registry

/// <summary>
/// Registry for data integration strategies.
/// </summary>
public sealed class DataIntegrationStrategyRegistry
{
    private readonly ConcurrentDictionary<string, IDataIntegrationStrategy> _strategies = new();

    public int Count => _strategies.Count;

    public void Register(IDataIntegrationStrategy strategy)
    {
        _strategies[strategy.StrategyId] = strategy;
    }

    public IDataIntegrationStrategy? GetStrategy(string strategyId) =>
        _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;

    public IEnumerable<IDataIntegrationStrategy> GetByCategory(IntegrationCategory category) =>
        _strategies.Values.Where(s => s.Category == category);

    public IEnumerable<IDataIntegrationStrategy> GetAllStrategies() => _strategies.Values;
}

#endregion

#region Base Strategy Class

/// <summary>
/// Base class for data integration strategies.
/// </summary>
public abstract class DataIntegrationStrategyBase : IDataIntegrationStrategy
{
    // Metrics - accessed via GetMetrics()
    private long _totalReads = 0;
    private long _totalWrites = 0;
    private long _totalFailures = 0;

    public abstract string StrategyId { get; }
    public abstract string DisplayName { get; }
    public abstract IntegrationCategory Category { get; }
    public abstract DataIntegrationCapabilities Capabilities { get; }
    public abstract string SemanticDescription { get; }
    public abstract string[] Tags { get; }

    protected void RecordRead()
    {
        Interlocked.Increment(ref _totalReads);
    }

    protected void RecordOperation(string operationName)
    {
        Interlocked.Increment(ref _totalWrites);
    }

    protected void RecordFailure()
    {
        Interlocked.Increment(ref _totalFailures);
    }

    /// <summary>
    /// Gets metrics for this strategy.
    /// </summary>
    public StrategyMetrics GetMetrics() => new()
    {
        TotalReads = Interlocked.Read(ref _totalReads),
        TotalWrites = Interlocked.Read(ref _totalWrites),
        TotalFailures = Interlocked.Read(ref _totalFailures)
    };
}

public sealed record StrategyMetrics
{
    public long TotalReads { get; init; }
    public long TotalWrites { get; init; }
    public long TotalFailures { get; init; }
}

#endregion
