using System.Collections.Concurrent;
using System.Reflection;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.DataFormat;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataFormat;

/// <summary>
/// Ultimate Data Format Plugin - Comprehensive data format parsing, serialization, and conversion solution (T110).
///
/// Implements 10+ format strategies across categories:
/// - Text Formats (JSON, XML, CSV, YAML, TOML)
/// - Binary Formats (Protobuf, MessagePack)
/// - Schema Formats (Avro, Thrift)
///
/// Features:
/// - Strategy pattern for extensibility
/// - Auto-discovery of strategies via reflection
/// - Unified API for format detection, parsing, serialization, conversion
/// - Intelligence-aware for AI-enhanced format recommendations
/// - Schema extraction for schema-aware formats
/// - Format validation against schema
/// - Bidirectional conversion between formats
/// - Production-ready error handling
/// </summary>
public sealed class UltimateDataFormatPlugin : IntelligenceAwarePluginBase, IDisposable
{
    private readonly ConcurrentDictionary<string, IDataFormatStrategy> _registry = new();
    private readonly ConcurrentDictionary<string, long> _usageStats = new();
    private readonly ConcurrentDictionary<DomainFamily, List<string>> _domainIndex = new();
    private bool _disposed;

    // Configuration
    private volatile bool _auditEnabled = true;
    private volatile bool _autoOptimizationEnabled = true;

    // Statistics
    private long _totalOperations;
    private long _totalBytesProcessed;
    private long _totalFailures;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.dataformat.ultimate";

    /// <inheritdoc/>
    public override string Name => "Ultimate Data Format";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.OrchestrationProvider;

    /// <summary>
    /// Semantic description of this plugin for AI discovery.
    /// </summary>
    public string SemanticDescription =>
        "Ultimate data format plugin providing 10+ strategies including text formats (JSON, XML, CSV, YAML, TOML), " +
        "binary formats (Protobuf, MessagePack), and schema formats (Avro, Thrift). " +
        "Supports format detection, parsing, serialization, bidirectional conversion, schema extraction, and validation. " +
        "Handles structured, hierarchical, and binary data with production-ready error handling.";

    /// <summary>
    /// Semantic tags for AI discovery and categorization.
    /// </summary>
    public string[] SemanticTags => [
        "data-format", "json", "xml", "csv", "yaml", "toml", "protobuf", "msgpack",
        "avro", "thrift", "parsing", "serialization", "conversion", "schema"
    ];

    /// <summary>
    /// Gets the number of registered strategies.
    /// </summary>
    public int StrategyCount => _registry.Count;

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

    public UltimateDataFormatPlugin()
    {
        DiscoverAndRegisterStrategies();
        BuildDomainIndex();
    }

    /// <summary>
    /// Discovers and registers all format strategies in this assembly.
    /// </summary>
    private void DiscoverAndRegisterStrategies()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var strategyTypes = assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(IDataFormatStrategy).IsAssignableFrom(t));

        foreach (var type in strategyTypes)
        {
            try
            {
                if (Activator.CreateInstance(type) is IDataFormatStrategy strategy)
                {
                    _registry[strategy.StrategyId] = strategy;

                    // Configure Intelligence integration if available
                    if (strategy is DataFormatStrategyBase baseStrategy && MessageBus != null)
                    {
                        baseStrategy.ConfigureIntelligence(MessageBus);
                    }
                }
            }
            catch
            {
                // Skip strategies that fail to instantiate
            }
        }
    }

    /// <summary>
    /// Builds domain family index for fast lookup.
    /// </summary>
    private void BuildDomainIndex()
    {
        foreach (var strategy in _registry.Values)
        {
            var domain = strategy.FormatInfo.DomainFamily;
            if (!_domainIndex.ContainsKey(domain))
            {
                _domainIndex[domain] = new List<string>();
            }
            _domainIndex[domain].Add(strategy.StrategyId);
        }
    }

    /// <summary>
    /// Gets a format strategy by ID.
    /// </summary>
    /// <param name="strategyId">Strategy ID (e.g., "json", "xml").</param>
    /// <returns>Strategy instance, or null if not found.</returns>
    public IDataFormatStrategy? GetStrategy(string strategyId)
    {
        return _registry.TryGetValue(strategyId, out var strategy) ? strategy : null;
    }

    /// <summary>
    /// Gets all strategies for a specific domain family.
    /// </summary>
    /// <param name="domain">Domain family (e.g., General, Analytics, Healthcare).</param>
    /// <returns>Collection of strategies for the domain.</returns>
    public IEnumerable<IDataFormatStrategy> GetStrategiesByDomain(DomainFamily domain)
    {
        if (_domainIndex.TryGetValue(domain, out var strategyIds))
        {
            return strategyIds.Select(id => _registry[id]);
        }
        return Enumerable.Empty<IDataFormatStrategy>();
    }

    /// <summary>
    /// Gets all registered strategies.
    /// </summary>
    /// <returns>Collection of all strategies.</returns>
    public IEnumerable<IDataFormatStrategy> GetAllStrategies()
    {
        return _registry.Values;
    }

    /// <summary>
    /// Detects the format of a data stream by trying all registered strategies.
    /// </summary>
    /// <param name="stream">Stream to analyze (must support seeking).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Detected format strategy, or null if no match.</returns>
    public async Task<IDataFormatStrategy?> DetectFormat(Stream stream, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        Interlocked.Increment(ref _totalOperations);

        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        if (!stream.CanSeek)
            throw new ArgumentException("Stream must support seeking for format detection.", nameof(stream));

        // Try each strategy until one matches
        foreach (var strategy in _registry.Values)
        {
            try
            {
                if (await strategy.DetectFormatAsync(stream, ct))
                {
                    RecordUsage(strategy.StrategyId);
                    return strategy;
                }
            }
            catch
            {
                // Continue to next strategy
            }
        }

        return null;
    }

    /// <summary>
    /// Parses data from a stream using auto-detected format.
    /// </summary>
    /// <param name="input">Input stream.</param>
    /// <param name="context">Parse context with options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Parse result.</returns>
    public async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        Interlocked.Increment(ref _totalOperations);

        var strategy = await DetectFormat(input, ct);
        if (strategy == null)
        {
            Interlocked.Increment(ref _totalFailures);
            return DataFormatResult.Fail("Unable to detect format");
        }

        try
        {
            var result = await strategy.ParseAsync(input, context, ct);
            if (result.Success)
            {
                Interlocked.Add(ref _totalBytesProcessed, result.BytesProcessed);
            }
            else
            {
                Interlocked.Increment(ref _totalFailures);
            }
            return result;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _totalFailures);
            return DataFormatResult.Fail($"Parse failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Serializes data using a specific format strategy.
    /// </summary>
    /// <param name="strategyId">Format strategy ID.</param>
    /// <param name="data">Data to serialize.</param>
    /// <param name="output">Output stream.</param>
    /// <param name="context">Serialization context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Serialization result.</returns>
    public async Task<DataFormatResult> SerializeAsync(
        string strategyId,
        object data,
        Stream output,
        DataFormatContext context,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        Interlocked.Increment(ref _totalOperations);

        var strategy = GetStrategy(strategyId);
        if (strategy == null)
        {
            Interlocked.Increment(ref _totalFailures);
            return DataFormatResult.Fail($"Strategy '{strategyId}' not found");
        }

        try
        {
            var result = await strategy.SerializeAsync(data, output, context, ct);
            if (result.Success)
            {
                Interlocked.Add(ref _totalBytesProcessed, result.BytesProcessed);
                RecordUsage(strategyId);
            }
            else
            {
                Interlocked.Increment(ref _totalFailures);
            }
            return result;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _totalFailures);
            return DataFormatResult.Fail($"Serialization failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Converts data from one format to another.
    /// </summary>
    /// <param name="input">Input stream.</param>
    /// <param name="sourceStrategyId">Source format strategy ID.</param>
    /// <param name="targetStrategyId">Target format strategy ID.</param>
    /// <param name="output">Output stream.</param>
    /// <param name="context">Conversion context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Conversion result.</returns>
    public async Task<DataFormatResult> ConvertAsync(
        Stream input,
        string sourceStrategyId,
        string targetStrategyId,
        Stream output,
        DataFormatContext context,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        Interlocked.Increment(ref _totalOperations);

        var sourceStrategy = GetStrategy(sourceStrategyId);
        var targetStrategy = GetStrategy(targetStrategyId);

        if (sourceStrategy == null)
        {
            Interlocked.Increment(ref _totalFailures);
            return DataFormatResult.Fail($"Source strategy '{sourceStrategyId}' not found");
        }

        if (targetStrategy == null)
        {
            Interlocked.Increment(ref _totalFailures);
            return DataFormatResult.Fail($"Target strategy '{targetStrategyId}' not found");
        }

        try
        {
            var result = await sourceStrategy.ConvertToAsync(input, targetStrategy, output, context, ct);
            if (result.Success)
            {
                Interlocked.Add(ref _totalBytesProcessed, result.BytesProcessed);
                RecordUsage(sourceStrategyId);
                RecordUsage(targetStrategyId);
            }
            else
            {
                Interlocked.Increment(ref _totalFailures);
            }
            return result;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _totalFailures);
            return DataFormatResult.Fail($"Conversion failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Records usage statistics for a strategy.
    /// </summary>
    private void RecordUsage(string strategyId)
    {
        _usageStats.AddOrUpdate(strategyId, 1, (_, count) => count + 1);
    }

    /// <summary>
    /// Gets plugin statistics.
    /// </summary>
    public DataFormatStatistics GetStatistics() => new()
    {
        TotalOperations = Interlocked.Read(ref _totalOperations),
        TotalBytesProcessed = Interlocked.Read(ref _totalBytesProcessed),
        TotalFailures = Interlocked.Read(ref _totalFailures),
        RegisteredStrategies = _registry.Count,
        UsageByStrategy = _usageStats.ToDictionary(k => k.Key, v => v.Value)
    };

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(UltimateDataFormatPlugin));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var strategy in _registry.Values)
        {
            if (strategy is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}

/// <summary>
/// Statistics for the UltimateDataFormat plugin.
/// </summary>
public sealed record DataFormatStatistics
{
    public long TotalOperations { get; init; }
    public long TotalBytesProcessed { get; init; }
    public long TotalFailures { get; init; }
    public int RegisteredStrategies { get; init; }
    public Dictionary<string, long> UsageByStrategy { get; init; } = new();
}
