using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataQuality;

/// <summary>
/// Defines the category of data quality strategy.
/// </summary>
public enum DataQualityCategory
{
    /// <summary>
    /// Validation strategies for rule-based data validation.
    /// </summary>
    Validation,

    /// <summary>
    /// Profiling strategies for data analysis and statistics.
    /// </summary>
    Profiling,

    /// <summary>
    /// Cleansing strategies for data cleanup and correction.
    /// </summary>
    Cleansing,

    /// <summary>
    /// Duplicate detection strategies for identifying redundant records.
    /// </summary>
    DuplicateDetection,

    /// <summary>
    /// Standardization strategies for data format normalization.
    /// </summary>
    Standardization,

    /// <summary>
    /// Scoring strategies for quality assessment and metrics.
    /// </summary>
    Scoring,

    /// <summary>
    /// Monitoring strategies for real-time quality tracking.
    /// </summary>
    Monitoring,

    /// <summary>
    /// Reporting strategies for quality dashboards and reports.
    /// </summary>
    Reporting
}

/// <summary>
/// Defines the capabilities of a data quality strategy.
/// </summary>
public sealed record DataQualityCapabilities
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
    /// Whether the strategy supports streaming operations.
    /// </summary>
    public required bool SupportsStreaming { get; init; }

    /// <summary>
    /// Whether the strategy supports distributed operations.
    /// </summary>
    public required bool SupportsDistributed { get; init; }

    /// <summary>
    /// Whether the strategy supports incremental processing.
    /// </summary>
    public required bool SupportsIncremental { get; init; }

    /// <summary>
    /// Maximum throughput in records per second (0 = unlimited).
    /// </summary>
    public long MaxThroughput { get; init; } = 0;

    /// <summary>
    /// Typical latency in milliseconds for operations.
    /// </summary>
    public double TypicalLatencyMs { get; init; } = 1.0;
}

/// <summary>
/// Statistics tracking for data quality operations.
/// </summary>
public sealed class DataQualityStatistics
{
    /// <summary>
    /// Total number of records processed.
    /// </summary>
    public long TotalRecordsProcessed { get; set; }

    /// <summary>
    /// Total number of records passing validation.
    /// </summary>
    public long RecordsPassed { get; set; }

    /// <summary>
    /// Total number of records failing validation.
    /// </summary>
    public long RecordsFailed { get; set; }

    /// <summary>
    /// Total number of records corrected.
    /// </summary>
    public long RecordsCorrected { get; set; }

    /// <summary>
    /// Total number of duplicates found.
    /// </summary>
    public long DuplicatesFound { get; set; }

    /// <summary>
    /// Total number of duplicates resolved.
    /// </summary>
    public long DuplicatesResolved { get; set; }

    /// <summary>
    /// Average quality score (0-100).
    /// </summary>
    public double AverageQualityScore { get; set; }

    /// <summary>
    /// Total time spent on operations in milliseconds.
    /// </summary>
    public double TotalTimeMs { get; set; }

    /// <summary>
    /// Total number of failures.
    /// </summary>
    public long TotalFailures { get; set; }

    /// <summary>
    /// Gets the pass rate.
    /// </summary>
    public double PassRate =>
        TotalRecordsProcessed > 0 ? (double)RecordsPassed / TotalRecordsProcessed : 0;

    /// <summary>
    /// Gets the correction rate.
    /// </summary>
    public double CorrectionRate =>
        RecordsFailed > 0 ? (double)RecordsCorrected / RecordsFailed : 0;

    /// <summary>
    /// Gets the average processing time per record in milliseconds.
    /// </summary>
    public double AverageProcessingTimeMs =>
        TotalRecordsProcessed > 0 ? TotalTimeMs / TotalRecordsProcessed : 0;
}

/// <summary>
/// Represents a data quality issue found during processing.
/// </summary>
public sealed class DataQualityIssue
{
    /// <summary>
    /// Unique identifier for the issue.
    /// </summary>
    public string IssueId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Issue severity level.
    /// </summary>
    public required IssueSeverity Severity { get; init; }

    /// <summary>
    /// Issue category.
    /// </summary>
    public required IssueCategory Category { get; init; }

    /// <summary>
    /// Field or column name where issue was found.
    /// </summary>
    public string? FieldName { get; init; }

    /// <summary>
    /// Record or row identifier.
    /// </summary>
    public string? RecordId { get; init; }

    /// <summary>
    /// Description of the issue.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Original value that caused the issue.
    /// </summary>
    public object? OriginalValue { get; init; }

    /// <summary>
    /// Suggested corrected value.
    /// </summary>
    public object? SuggestedValue { get; init; }

    /// <summary>
    /// Rule ID that triggered the issue.
    /// </summary>
    public string? RuleId { get; init; }

    /// <summary>
    /// Timestamp when the issue was detected.
    /// </summary>
    public DateTime DetectedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Whether the issue was auto-corrected.
    /// </summary>
    public bool AutoCorrected { get; set; }

    /// <summary>
    /// Additional context.
    /// </summary>
    public Dictionary<string, object>? Context { get; init; }
}

/// <summary>
/// Issue severity levels.
/// </summary>
public enum IssueSeverity
{
    /// <summary>
    /// Informational issue - no action required.
    /// </summary>
    Info,

    /// <summary>
    /// Warning - data may have minor issues.
    /// </summary>
    Warning,

    /// <summary>
    /// Error - data has significant issues.
    /// </summary>
    Error,

    /// <summary>
    /// Critical - data is severely compromised.
    /// </summary>
    Critical
}

/// <summary>
/// Issue category types.
/// </summary>
public enum IssueCategory
{
    /// <summary>
    /// Missing or null values.
    /// </summary>
    MissingValue,

    /// <summary>
    /// Invalid format or structure.
    /// </summary>
    InvalidFormat,

    /// <summary>
    /// Value out of expected range.
    /// </summary>
    OutOfRange,

    /// <summary>
    /// Duplicate data detected.
    /// </summary>
    Duplicate,

    /// <summary>
    /// Referential integrity violation.
    /// </summary>
    ReferentialIntegrity,

    /// <summary>
    /// Business rule violation.
    /// </summary>
    BusinessRule,

    /// <summary>
    /// Data type mismatch.
    /// </summary>
    TypeMismatch,

    /// <summary>
    /// Encoding or character set issue.
    /// </summary>
    Encoding,

    /// <summary>
    /// Consistency issue across related fields.
    /// </summary>
    Consistency,

    /// <summary>
    /// Timeliness issue - data is stale.
    /// </summary>
    Timeliness,

    /// <summary>
    /// Accuracy issue - data is incorrect.
    /// </summary>
    Accuracy,

    /// <summary>
    /// Completeness issue - data is incomplete.
    /// </summary>
    Completeness
}

/// <summary>
/// Represents a data record for quality processing.
/// </summary>
public sealed class DataRecord
{
    /// <summary>
    /// Unique identifier for the record.
    /// </summary>
    public required string RecordId { get; init; }

    /// <summary>
    /// Source identifier (table, file, etc.).
    /// </summary>
    public string? SourceId { get; init; }

    /// <summary>
    /// Record fields with their values.
    /// </summary>
    public required Dictionary<string, object?> Fields { get; init; }

    /// <summary>
    /// Record metadata.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Timestamp when the record was created.
    /// </summary>
    public DateTime? CreatedAt { get; init; }

    /// <summary>
    /// Timestamp when the record was last modified.
    /// </summary>
    public DateTime? ModifiedAt { get; init; }

    /// <summary>
    /// Gets a field value by name.
    /// </summary>
    public T? GetField<T>(string fieldName)
    {
        if (Fields.TryGetValue(fieldName, out var value) && value is T typedValue)
        {
            return typedValue;
        }
        return default;
    }

    /// <summary>
    /// Gets a field value as string.
    /// </summary>
    public string? GetFieldAsString(string fieldName)
    {
        return Fields.TryGetValue(fieldName, out var value) ? value?.ToString() : null;
    }
}

/// <summary>
/// Interface for data quality strategies.
/// </summary>
public interface IDataQualityStrategy
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
    DataQualityCategory Category { get; }

    /// <summary>
    /// Capabilities of this strategy.
    /// </summary>
    DataQualityCapabilities Capabilities { get; }

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
    DataQualityStatistics GetStatistics();

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
/// Abstract base class for data quality strategies.
/// Provides common functionality including statistics tracking and validation via StrategyBase.
/// </summary>
public abstract class DataQualityStrategyBase : StrategyBase, IDataQualityStrategy
{
    private readonly DataQualityStatistics _statistics = new();
    private readonly object _statsLock = new();

    /// <inheritdoc/>
    public abstract override string StrategyId { get; }

    /// <inheritdoc/>
    public abstract string DisplayName { get; }

    /// <inheritdoc/>
    public override string Name => DisplayName;

    /// <inheritdoc/>
    public abstract DataQualityCategory Category { get; }

    /// <inheritdoc/>
    public abstract DataQualityCapabilities Capabilities { get; }

    /// <inheritdoc/>
    public abstract string SemanticDescription { get; }

    /// <inheritdoc/>
    public abstract string[] Tags { get; }

    /// <inheritdoc/>
    public DataQualityStatistics GetStatistics()
    {
        lock (_statsLock)
        {
            return new DataQualityStatistics
            {
                TotalRecordsProcessed = _statistics.TotalRecordsProcessed,
                RecordsPassed = _statistics.RecordsPassed,
                RecordsFailed = _statistics.RecordsFailed,
                RecordsCorrected = _statistics.RecordsCorrected,
                DuplicatesFound = _statistics.DuplicatesFound,
                DuplicatesResolved = _statistics.DuplicatesResolved,
                AverageQualityScore = _statistics.AverageQualityScore,
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
            _statistics.TotalRecordsProcessed = 0;
            _statistics.RecordsPassed = 0;
            _statistics.RecordsFailed = 0;
            _statistics.RecordsCorrected = 0;
            _statistics.DuplicatesFound = 0;
            _statistics.DuplicatesResolved = 0;
            _statistics.AverageQualityScore = 0;
            _statistics.TotalTimeMs = 0;
            _statistics.TotalFailures = 0;
        }
    }

    /// <inheritdoc/>
    protected override async Task InitializeAsyncCore(CancellationToken ct)
    {
        await InitializeCoreAsync(ct);
        IncrementCounter("initialized");
    }

    /// <summary>
    /// Disposes the data quality strategy asynchronously, calling domain-specific cleanup.
    /// Note: this returns Task rather than ValueTask to match domain base patterns.
    /// Use the IAsyncDisposable.DisposeAsync() path from StrategyBase for ValueTask disposal.
    /// </summary>
    public virtual new async Task DisposeAsync()
    {
        if (!IsInitialized) return;
        await DisposeCoreAsync();
        await ShutdownAsync();
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
    /// Records a processed record.
    /// </summary>
    protected void RecordProcessed(bool passed, double timeMs)
    {
        lock (_statsLock)
        {
            _statistics.TotalRecordsProcessed++;
            if (passed)
                _statistics.RecordsPassed++;
            else
                _statistics.RecordsFailed++;
            _statistics.TotalTimeMs += timeMs;
        }
    }

    /// <summary>
    /// Records a corrected record.
    /// </summary>
    protected void RecordCorrected()
    {
        lock (_statsLock)
        {
            _statistics.RecordsCorrected++;
        }
    }

    /// <summary>
    /// Records a duplicate found.
    /// </summary>
    protected void RecordDuplicateFound()
    {
        lock (_statsLock)
        {
            _statistics.DuplicatesFound++;
        }
    }

    /// <summary>
    /// Records a duplicate resolved.
    /// </summary>
    protected void RecordDuplicateResolved()
    {
        lock (_statsLock)
        {
            _statistics.DuplicatesResolved++;
        }
    }

    /// <summary>
    /// Updates the average quality score.
    /// </summary>
    protected void UpdateQualityScore(double newScore)
    {
        lock (_statsLock)
        {
            var totalProcessed = _statistics.TotalRecordsProcessed;
            if (totalProcessed == 0)
            {
                _statistics.AverageQualityScore = newScore;
            }
            else
            {
                // Running average
                _statistics.AverageQualityScore =
                    (_statistics.AverageQualityScore * (totalProcessed - 1) + newScore) / totalProcessed;
            }
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

    /// <summary>Gets cached health status, refreshing every 60 seconds.</summary>
    public bool IsHealthy()
    {
        var result = GetCachedHealthAsync(ct =>
            Task.FromResult(new StrategyHealthCheckResult(IsInitialized)),
            TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        return result.IsHealthy;
    }

    /// <summary>Gets all counter values.</summary>
    public IReadOnlyDictionary<string, long> GetCounters() => GetAllCounters();
}

/// <summary>
/// Thread-safe registry for data quality strategies.
/// </summary>
public sealed class DataQualityStrategyRegistry
{
    private readonly BoundedDictionary<string, IDataQualityStrategy> _strategies =
        new(1000);

    /// <summary>
    /// Registers a strategy.
    /// </summary>
    public void Register(IDataQualityStrategy strategy)
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
    public IDataQualityStrategy? Get(string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        return _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;
    }

    /// <summary>
    /// Gets all registered strategies.
    /// </summary>
    public IReadOnlyCollection<IDataQualityStrategy> GetAll()
    {
        return _strategies.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// Gets strategies by category.
    /// </summary>
    public IReadOnlyCollection<IDataQualityStrategy> GetByCategory(DataQualityCategory category)
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
        var strategyType = typeof(IDataQualityStrategy);
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
                        if (Activator.CreateInstance(type) is IDataQualityStrategy strategy)
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
