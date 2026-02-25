# Plugin: UltimateDataQuality
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateDataQuality

### File: Plugins/DataWarehouse.Plugins.UltimateDataQuality/DataQualityStrategyBase.cs
```csharp
public sealed record DataQualityCapabilities
{
}
    public required bool SupportsAsync { get; init; }
    public required bool SupportsBatch { get; init; }
    public required bool SupportsStreaming { get; init; }
    public required bool SupportsDistributed { get; init; }
    public required bool SupportsIncremental { get; init; }
    public long MaxThroughput { get; init; };
    public double TypicalLatencyMs { get; init; };
}
```
```csharp
public sealed class DataQualityStatistics
{
}
    public long TotalRecordsProcessed { get; set; }
    public long RecordsPassed { get; set; }
    public long RecordsFailed { get; set; }
    public long RecordsCorrected { get; set; }
    public long DuplicatesFound { get; set; }
    public long DuplicatesResolved { get; set; }
    public double AverageQualityScore { get; set; }
    public double TotalTimeMs { get; set; }
    public long TotalFailures { get; set; }
    public double PassRate;;
    public double CorrectionRate;;
    public double AverageProcessingTimeMs;;
}
```
```csharp
public sealed class DataQualityIssue
{
}
    public string IssueId { get; init; };
    public required IssueSeverity Severity { get; init; }
    public required IssueCategory Category { get; init; }
    public string? FieldName { get; init; }
    public string? RecordId { get; init; }
    public required string Description { get; init; }
    public object? OriginalValue { get; init; }
    public object? SuggestedValue { get; init; }
    public string? RuleId { get; init; }
    public DateTime DetectedAt { get; init; };
    public bool AutoCorrected { get; set; }
    public Dictionary<string, object>? Context { get; init; }
}
```
```csharp
public sealed class DataRecord
{
}
    public required string RecordId { get; init; }
    public string? SourceId { get; init; }
    public required Dictionary<string, object?> Fields { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
    public DateTime? CreatedAt { get; init; }
    public DateTime? ModifiedAt { get; init; }
    public T? GetField<T>(string fieldName);
    public string? GetFieldAsString(string fieldName);
}
```
```csharp
public interface IDataQualityStrategy
{
}
    string StrategyId { get; }
    string DisplayName { get; }
    DataQualityCategory Category { get; }
    DataQualityCapabilities Capabilities { get; }
    string SemanticDescription { get; }
    string[] Tags { get; }
    DataQualityStatistics GetStatistics();;
    void ResetStatistics();;
    Task InitializeAsync(CancellationToken ct = default);;
    Task DisposeAsync();;
}
```
```csharp
public abstract class DataQualityStrategyBase : StrategyBase, IDataQualityStrategy
{
}
    public abstract override string StrategyId { get; }
    public abstract string DisplayName { get; }
    public override string Name;;
    public abstract DataQualityCategory Category { get; }
    public abstract DataQualityCapabilities Capabilities { get; }
    public abstract string SemanticDescription { get; }
    public abstract string[] Tags { get; }
    public DataQualityStatistics GetStatistics();
    public void ResetStatistics();
    protected override async Task InitializeAsyncCore(CancellationToken ct);
    public virtual new async Task DisposeAsync();
    protected virtual Task InitializeCoreAsync(CancellationToken ct);;
    protected virtual Task DisposeCoreAsync();;
    protected void RecordProcessed(bool passed, double timeMs);
    protected void RecordCorrected();
    protected void RecordDuplicateFound();
    protected void RecordDuplicateResolved();
    protected void UpdateQualityScore(double newScore);
    protected void RecordFailure();
    public bool IsHealthy();
    public IReadOnlyDictionary<string, long> GetCounters();;
}
```
```csharp
public sealed class DataQualityStrategyRegistry
{
}
    public void Register(IDataQualityStrategy strategy);
    public bool Unregister(string strategyId);
    public IDataQualityStrategy? Get(string strategyId);
    public IReadOnlyCollection<IDataQualityStrategy> GetAll();
    public IReadOnlyCollection<IDataQualityStrategy> GetByCategory(DataQualityCategory category);
    public int Count;;
    public int AutoDiscover(params System.Reflection.Assembly[] assemblies);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataQuality/UltimateDataQualityPlugin.cs
```csharp
public sealed class UltimateDataQualityPlugin : DataManagementPluginBase, IDisposable
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string DataManagementDomain;;
    public override PluginCategory Category;;
    public string SemanticDescription;;
    public string[] SemanticTags;;
    public DataQualityStrategyRegistry Registry;;
    public bool AuditEnabled { get => _auditEnabled; set => _auditEnabled = value; }
    public bool AutoCorrectEnabled { get => _autoCorrectEnabled; set => _autoCorrectEnabled = value; }
    public UltimateDataQualityPlugin();
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request);
    protected override List<PluginCapabilityDescriptor> GetCapabilities();
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
{
    get
    {
        var capabilities = new List<RegisteredCapability>
        {
            new()
            {
                CapabilityId = "data-quality",
                DisplayName = "Ultimate Data Quality",
                Description = SemanticDescription,
                Category = SDK.Contracts.CapabilityCategory.DataManagement,
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = SemanticTags
            }
        };
        foreach (var strategy in _registry.GetAll())
        {
            var tags = new List<string>
            {
                "data-quality",
                strategy.Category.ToString().ToLowerInvariant()
            };
            tags.AddRange(strategy.Tags);
            capabilities.Add(new() { CapabilityId = $"data-quality.{strategy.StrategyId}", DisplayName = strategy.DisplayName, Description = strategy.SemanticDescription, Category = SDK.Contracts.CapabilityCategory.DataManagement, SubCategory = strategy.Category.ToString(), PluginId = Id, PluginName = Name, PluginVersion = Version, Tags = tags.ToArray(), Metadata = new Dictionary<string, object> { ["category"] = strategy.Category.ToString(), ["supportsAsync"] = strategy.Capabilities.SupportsAsync, ["supportsBatch"] = strategy.Capabilities.SupportsBatch, ["supportsStreaming"] = strategy.Capabilities.SupportsStreaming, ["supportsDistributed"] = strategy.Capabilities.SupportsDistributed }, SemanticDescription = strategy.SemanticDescription });
        }

        return capabilities.AsReadOnly();
    }
}
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge();
    protected override Dictionary<string, object> GetMetadata();
    public override Task OnMessageAsync(PluginMessage message);
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct);
    protected override async Task OnStartCoreAsync(CancellationToken ct);
    protected override async Task OnBeforeStatePersistAsync(CancellationToken ct);
    protected override void Dispose(bool disposing);
}
```
```csharp
private sealed class QualityPolicyDto
{
}
    public string PolicyId { get; set; };
    public string Name { get; set; };
    public DateTime CreatedAt { get; set; }
}
```
```csharp
private sealed class QualityPolicy
{
}
    public required string PolicyId { get; init; }
    public required string Name { get; init; }
    public DateTime CreatedAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataQuality/Strategies/Cleansing/CleansingStrategies.cs
```csharp
public sealed class CleansingResult
{
}
    public required string RecordId { get; init; }
    public bool WasModified;;
    public required DataRecord OriginalRecord { get; init; }
    public required DataRecord CleansedRecord { get; init; }
    public List<FieldModification> Modifications { get; init; };
    public double ProcessingTimeMs { get; init; }
}
```
```csharp
public sealed class FieldModification
{
}
    public required string FieldName { get; init; }
    public required ModificationType Type { get; init; }
    public object? OriginalValue { get; init; }
    public object? NewValue { get; init; }
    public string? Description { get; init; }
}
```
```csharp
public sealed class CleansingRule
{
}
    public required string RuleId { get; init; }
    public required string FieldPattern { get; init; }
    public required CleansingOperation Operation { get; init; }
    public Dictionary<string, object>? Parameters { get; init; }
    public bool Enabled { get; init; };
    public int Priority { get; init; };
}
```
```csharp
public sealed class BasicCleansingStrategy : DataQualityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataQualityCategory Category;;
    public override DataQualityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void AddRule(CleansingRule rule);;
    public bool RemoveRule(string ruleId);;
    public void RegisterCustomOperation(string operationId, Func<object?, Dictionary<string, object>?, object?> operation);
    public Task<CleansingResult> CleanseAsync(DataRecord record, CancellationToken ct = default);
    public async IAsyncEnumerable<CleansingResult> CleanseBatchAsync(IEnumerable<DataRecord> records, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default);
}
```
```csharp
public sealed class SemanticCleansingStrategy : DataQualityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataQualityCategory Category;;
    public override DataQualityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void RegisterDictionary(string fieldName, Dictionary<string, string> corrections);
    public void RegisterValidValues(string fieldName, IEnumerable<string> validValues);
    public void RegisterAbbreviations(Dictionary<string, string> abbreviations);
    protected override Task InitializeCoreAsync(CancellationToken ct);
    public Task<CleansingResult> CleanseAsync(DataRecord record, CancellationToken ct = default);
}
```
```csharp
public sealed class NullHandlingStrategy : DataQualityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataQualityCategory Category;;
    public override DataQualityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void SetDefault(string fieldName, object defaultValue);
    public void SetMode(string fieldName, NullHandlingMode mode);
    public void SetDefaultMode(NullHandlingMode mode);
    public Task<CleansingResult> HandleNullsAsync(DataRecord record, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataQuality/Strategies/DuplicateDetection/DuplicateDetectionStrategies.cs
```csharp
public sealed class DuplicateGroup
{
}
    public string GroupId { get; init; };
    public List<string> RecordIds { get; init; };
    public string? MasterRecordId { get; set; }
    public double SimilarityScore { get; init; }
    public List<string> MatchingFields { get; init; };
    public required string DetectionMethod { get; init; }
}
```
```csharp
public sealed class DuplicateDetectionResult
{
}
    public int TotalRecords { get; init; }
    public int DuplicateGroupCount;;
    public int TotalDuplicates { get; init; }
    public List<DuplicateGroup> DuplicateGroups { get; init; };
    public List<string> UniqueRecordIds { get; init; };
    public double ProcessingTimeMs { get; init; }
    public double DuplicatePercentage;;
}
```
```csharp
public sealed class DuplicateDetectionConfig
{
}
    public required string[] MatchFields { get; init; }
    public double SimilarityThreshold { get; init; };
    public bool ExactMatchOnly { get; init; }
    public bool IgnoreCase { get; init; };
    public bool TrimWhitespace { get; init; };
    public string[]? MasterSelectionFields { get; init; }
    public MasterSelectionStrategy MasterStrategy { get; init; };
}
```
```csharp
public sealed class DuplicateMergeResult
{
}
    public required DataRecord MergedRecord { get; init; }
    public required List<string> MergedRecordIds { get; init; }
    public List<string> ConsolidatedFields { get; init; };
}
```
```csharp
public sealed class ExactMatchDuplicateStrategy : DataQualityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataQualityCategory Category;;
    public override DataQualityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<DuplicateDetectionResult> DetectAsync(IEnumerable<DataRecord> records, DuplicateDetectionConfig config, CancellationToken ct = default);
}
```
```csharp
public sealed class FuzzyMatchDuplicateStrategy : DataQualityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataQualityCategory Category;;
    public override DataQualityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<DuplicateDetectionResult> DetectAsync(IEnumerable<DataRecord> records, DuplicateDetectionConfig config, CancellationToken ct = default);
}
```
```csharp
public sealed class PhoneticMatchDuplicateStrategy : DataQualityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataQualityCategory Category;;
    public override DataQualityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<DuplicateDetectionResult> DetectAsync(IEnumerable<DataRecord> records, DuplicateDetectionConfig config, CancellationToken ct = default);
}
```
```csharp
public sealed class DuplicateMerger
{
}
    public DuplicateMergeResult Merge(IEnumerable<DataRecord> duplicateRecords, MergeStrategy strategy = MergeStrategy.MostComplete);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataQuality/Strategies/Monitoring/MonitoringStrategies.cs
```csharp
public sealed class QualityMetric
{
}
    public required string Name { get; init; }
    public double Value { get; init; }
    public DateTime Timestamp { get; init; };
    public string? Unit { get; init; }
    public string? Dimension { get; init; }
    public Dictionary<string, string>? Tags { get; init; }
}
```
```csharp
public sealed class QualityThreshold
{
}
    public required string ThresholdId { get; init; }
    public required string MetricName { get; init; }
    public double? MinValue { get; init; }
    public double? MaxValue { get; init; }
    public double WarningThreshold { get; init; };
    public bool Enabled { get; init; };
}
```
```csharp
public sealed class QualityAlert
{
}
    public string AlertId { get; init; };
    public required AlertSeverity Severity { get; init; }
    public required string MetricName { get; init; }
    public double CurrentValue { get; init; }
    public required QualityThreshold Threshold { get; init; }
    public required string Message { get; init; }
    public DateTime RaisedAt { get; init; };
    public bool Acknowledged { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
}
```
```csharp
public sealed class QualityTrend
{
}
    public required string MetricName { get; init; }
    public required TimeSpan Period { get; init; }
    public List<QualityMetric> DataPoints { get; init; };
    public TrendDirection Direction { get; init; }
    public double PercentChange { get; init; }
    public double Average { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double StdDev { get; init; }
}
```
```csharp
public sealed class QualitySla
{
}
    public required string SlaId { get; init; }
    public required string Name { get; init; }
    public double TargetScore { get; init; };
    public double MinScore { get; init; };
    public TimeSpan EvaluationPeriod { get; init; };
    public double RequiredUptime { get; init; };
    public bool Active { get; init; };
}
```
```csharp
public sealed class SlaComplianceResult
{
}
    public required QualitySla Sla { get; init; }
    public bool IsCompliant { get; init; }
    public double CurrentScore { get; init; }
    public double CompliancePercentage { get; init; }
    public int ViolationCount { get; init; }
    public required DateRange EvaluationPeriod { get; init; }
}
```
```csharp
public sealed class DateRange
{
}
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
}
```
```csharp
public sealed class RealTimeMonitoringStrategy : DataQualityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataQualityCategory Category;;
    public override DataQualityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public event EventHandler<QualityAlert>? AlertRaised;
    public void RegisterThreshold(QualityThreshold threshold);
    public void RegisterSla(QualitySla sla);
    public void RecordMetric(QualityMetric metric);
    public void RecordMetrics(IEnumerable<QualityMetric> metrics);
    public QualityMetric? GetCurrentMetric(string metricName);
    public List<QualityMetric> GetMetricHistory(string metricName, TimeSpan? period = null);
    public List<QualityAlert> GetActiveAlerts();
    public bool AcknowledgeAlert(string alertId, string acknowledgedBy);
    public QualityTrend CalculateTrend(string metricName, TimeSpan period);
    public SlaComplianceResult EvaluateSlaCompliance(string slaId, string metricName);
    public Dictionary<string, QualityMetricSummary> GetMetricsSummary();
}
```
```csharp
public sealed class QualityMetricSummary
{
}
    public required string MetricName { get; init; }
    public double CurrentValue { get; init; }
    public double Average { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public int Count { get; init; }
    public DateTime LastUpdated { get; init; }
}
```
```csharp
public sealed class AnomalyDetectionStrategy : DataQualityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataQualityCategory Category;;
    public override DataQualityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void Train(string metricName, IEnumerable<double> values);
    public AnomalyResult DetectAnomaly(string metricName, double value);
}
```
```csharp
internal sealed class AnomalyDetector
{
}
    public double Mean { get; init; }
    public double StdDev { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public int TrainingSamples { get; init; }
}
```
```csharp
public sealed class AnomalyResult
{
}
    public required string MetricName { get; init; }
    public double Value { get; init; }
    public bool IsAnomaly { get; init; }
    public double ZScore { get; init; }
    public AnomalySeverity Severity { get; init; }
    public (double Low, double High) ExpectedRange { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataQuality/Strategies/PredictiveQuality/PredictiveQualityStrategies.cs
```csharp
internal sealed class QualityAnticipatorStrategy : DataQualityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataQualityCategory Category;;
    public override DataQualityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void RecordMetric(string metricName, double value);
    public void SetThreshold(string metricName, double warningStdDevs = 2.0, double criticalStdDevs = 3.0);
    public QualityPrediction? PredictIssues(string metricName);
    public string GetTrend(string metricName, int windowSize = 10);
}
```
```csharp
internal sealed class DataDriftDetectorStrategy : DataQualityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataQualityCategory Category;;
    public override DataQualityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void SetBaseline(string columnName, double[] values);
    public void UpdateCurrent(string columnName, double[] values);
    public DriftReport? DetectDrift(string columnName);
}
```
```csharp
internal sealed class AnomalousDataFlagStrategy : DataQualityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataQualityCategory Category;;
    public override DataQualityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void AddValues(string columnName, double[] values);
    public AnomalyReport? DetectAnomalies(string columnName, double zScoreThreshold = 3.0);
}
```
```csharp
internal sealed class QualityTrendAnalyzerStrategy : DataQualityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataQualityCategory Category;;
    public override DataQualityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void RecordDataPoint(string metricName, double value);
    public TrendAnalysis? AnalyzeTrend(string metricName);
    public bool GetSeasonality(string metricName, int period);
}
```
```csharp
internal sealed class RootCauseAnalyzerStrategy : DataQualityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataQualityCategory Category;;
    public override DataQualityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void RecordEvent(string dimension, string description, double severity, Dictionary<string, string>? attributes = null);
    public RootCauseReport AnalyzeRootCause(string targetDimension, TimeSpan lookbackWindow);
    public Dictionary<(string, string), double> GetCorrelationMatrix(IReadOnlyList<string> dimensions);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataQuality/Strategies/Profiling/ProfilingStrategies.cs
```csharp
public sealed class DataProfile
{
}
    public string ProfileId { get; init; };
    public required string SourceId { get; init; }
    public DateTime GeneratedAt { get; init; };
    public long TotalRecords { get; set; }
    public double ProcessingTimeMs { get; set; }
    public Dictionary<string, FieldProfile> FieldProfiles { get; init; };
    public double OverallQualityScore { get; set; }
    public List<FieldCorrelation> Correlations { get; init; };
    public List<PatternInfo> DetectedPatterns { get; init; };
    public Dictionary<IssueCategory, int> IssueSummary { get; init; };
}
```
```csharp
public sealed class FieldProfile
{
}
    public required string FieldName { get; init; }
    public InferredDataType InferredType { get; set; }
    public long TotalCount { get; set; }
    public long NullCount { get; set; }
    public long DistinctCount { get; set; }
    public double NullPercentage;;
    public double CardinalityRatio;;
    public List<ValueFrequency> TopValues { get; init; };
    public List<ValueFrequency> BottomValues { get; init; };
    public NumericProfile? NumericStats { get; set; }
    public StringProfile? StringStats { get; set; }
    public DateTimeProfile? DateTimeStats { get; set; }
    public string? DetectedPattern { get; set; }
    public double QualityScore { get; set; }
    public List<string> Issues { get; init; };
}
```
```csharp
public sealed class ValueFrequency
{
}
    public required object? Value { get; init; }
    public long Count { get; init; }
    public double Percentage { get; init; }
}
```
```csharp
public sealed class NumericProfile
{
}
    public double Min { get; init; }
    public double Max { get; init; }
    public double Mean { get; init; }
    public double Median { get; init; }
    public double Mode { get; init; }
    public double StdDev { get; init; }
    public double Variance { get; init; }
    public double Skewness { get; init; }
    public double Kurtosis { get; init; }
    public double Q1 { get; init; }
    public double Q3 { get; init; }
    public double IQR { get; init; }
    public double Sum { get; init; }
    public int ZeroCount { get; init; }
    public int NegativeCount { get; init; }
    public int PositiveCount { get; init; }
    public double[] Percentiles { get; init; };
    public Dictionary<string, int> Histogram { get; init; };
}
```
```csharp
public sealed class StringProfile
{
}
    public int MinLength { get; init; }
    public int MaxLength { get; init; }
    public double AvgLength { get; init; }
    public int EmptyCount { get; init; }
    public int WhitespaceOnlyCount { get; init; }
    public Dictionary<string, int> LengthDistribution { get; init; };
    public Dictionary<char, int> CharacterFrequency { get; init; };
    public bool ContainsUppercase { get; init; }
    public bool ContainsLowercase { get; init; }
    public bool ContainsDigits { get; init; }
    public bool ContainsSpecialChars { get; init; }
    public List<string> CommonPrefixes { get; init; };
    public List<string> CommonSuffixes { get; init; };
}
```
```csharp
public sealed class DateTimeProfile
{
}
    public DateTime Min { get; init; }
    public DateTime Max { get; init; }
    public TimeSpan Range { get; init; }
    public DateTime? Mode { get; init; }
    public Dictionary<DayOfWeek, int> DayOfWeekDistribution { get; init; };
    public Dictionary<int, int> MonthDistribution { get; init; };
    public Dictionary<int, int> YearDistribution { get; init; };
    public Dictionary<int, int> HourDistribution { get; init; };
    public int FutureDateCount { get; init; }
    public int WeekendCount { get; init; }
    public string? DetectedFormat { get; init; }
}
```
```csharp
public sealed class FieldCorrelation
{
}
    public required string Field1 { get; init; }
    public required string Field2 { get; init; }
    public double CorrelationCoefficient { get; init; }
    public CorrelationType Type { get; init; }
}
```
```csharp
public sealed class PatternInfo
{
}
    public required string FieldName { get; init; }
    public required string Pattern { get; init; }
    public int MatchCount { get; init; }
    public double MatchPercentage { get; init; }
    public string? Description { get; init; }
}
```
```csharp
public sealed class ColumnProfilingStrategy : DataQualityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataQualityCategory Category;;
    public override DataQualityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public async Task<DataProfile> ProfileAsync(IEnumerable<DataRecord> records, CancellationToken ct = default);
}
```
```csharp
public sealed class PatternDetectionStrategy : DataQualityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataQualityCategory Category;;
    public override DataQualityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<List<PatternInfo>> DetectPatternsAsync(string fieldName, IEnumerable<object?> values, CancellationToken ct = default);
    public string GeneratePatternTemplate(IEnumerable<string> sampleValues);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataQuality/Strategies/Reporting/ReportingStrategies.cs
```csharp
public sealed class ReportConfig
{
}
    public string Title { get; init; };
    public ReportFormat Format { get; init; };
    public bool IncludeFieldDetails { get; init; };
    public bool IncludeTrends { get; init; };
    public bool IncludeRecommendations { get; init; };
    public int MaxIssues { get; init; };
    public TimeSpan? ReportPeriod { get; init; }
}
```
```csharp
public sealed class QualityReport
{
}
    public string ReportId { get; init; };
    public required string Title { get; init; }
    public DateTime GeneratedAt { get; init; };
    public ReportFormat Format { get; init; }
    public required string Content { get; init; }
    public required ReportSummary Summary { get; init; }
}
```
```csharp
public sealed class ReportSummary
{
}
    public double OverallScore { get; init; }
    public int TotalRecords { get; init; }
    public int TotalIssues { get; init; }
    public int CriticalIssues { get; init; }
    public Dictionary<string, double> DimensionScores { get; init; };
    public List<string> TopRecommendations { get; init; };
}
```
```csharp
public sealed class ReportSection
{
}
    public required string Title { get; init; }
    public required string Content { get; init; }
    public int Order { get; init; }
}
```
```csharp
public sealed class ReportGenerationStrategy : DataQualityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataQualityCategory Category;;
    public override DataQualityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<QualityReport> GenerateReportAsync(Scoring.DatasetQualityScore datasetScore, ReportConfig config, CancellationToken ct = default);
}
```
```csharp
public sealed class DashboardDataStrategy : DataQualityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataQualityCategory Category;;
    public override DataQualityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public DashboardData GenerateDashboardData(Scoring.DatasetQualityScore currentScore, List<Scoring.DatasetQualityScore>? historicalScores = null);
}
```
```csharp
public sealed class DashboardData
{
}
    public DateTime GeneratedAt { get; init; }
    public List<KpiData> Kpis { get; init; };
    public List<ChartData> Charts { get; init; };
    public ChartData? TrendChart { get; init; }
}
```
```csharp
public sealed class KpiData
{
}
    public required string Name { get; init; }
    public double Value { get; init; }
    public string? Unit { get; init; }
    public string Trend { get; init; };
    public double? Target { get; init; }
    public bool IsLowerBetter { get; init; }
}
```
```csharp
public sealed class ChartData
{
}
    public required string Title { get; init; }
    public required string Type { get; init; }
    public List<string> Labels { get; init; };
    public List<ChartDataset> Datasets { get; init; };
}
```
```csharp
public sealed class ChartDataset
{
}
    public required string Label { get; init; }
    public List<double> Data { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataQuality/Strategies/Scoring/ScoringStrategies.cs
```csharp
public sealed class RecordQualityScore
{
}
    public required string RecordId { get; init; }
    public double OverallScore { get; init; }
    public QualityGrade Grade;;
    public Dictionary<QualityDimension, double> DimensionScores { get; init; };
    public Dictionary<string, FieldQualityScore> FieldScores { get; init; };
    public List<string> Issues { get; init; };
    public List<string> Recommendations { get; init; };
}
```
```csharp
public sealed class FieldQualityScore
{
}
    public required string FieldName { get; init; }
    public double Score { get; init; }
    public Dictionary<QualityDimension, double> Dimensions { get; init; };
    public List<string> Issues { get; init; };
}
```
```csharp
public sealed class DatasetQualityScore
{
}
    public required string DatasetId { get; init; }
    public double OverallScore { get; init; }
    public QualityGrade Grade;;
    public Dictionary<QualityDimension, double> DimensionScores { get; init; };
    public int TotalRecords { get; init; }
    public Dictionary<QualityGrade, int> RecordsByGrade { get; init; };
    public Dictionary<string, double> FieldScores { get; init; };
    public List<QualityIssue> TopIssues { get; init; };
    public DateTime ScoredAt { get; init; };
}
```
```csharp
public sealed class QualityIssue
{
}
    public required string Description { get; init; }
    public string? FieldName { get; init; }
    public QualityDimension Dimension { get; init; }
    public int AffectedCount { get; init; }
    public double ScoreImpact { get; init; }
}
```
```csharp
public sealed class QualityScoringConfig
{
}
    public Dictionary<QualityDimension, double> DimensionWeights { get; init; };
    public string[] RequiredFields { get; init; };
    public string[] UniqueFields { get; init; };
    public int TimelinessThresholdDays { get; init; };
}
```
```csharp
public sealed class DimensionScoringStrategy : DataQualityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataQualityCategory Category;;
    public override DataQualityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void Configure(QualityScoringConfig config);
    public Task<RecordQualityScore> ScoreRecordAsync(DataRecord record, CancellationToken ct = default);
    public async Task<DatasetQualityScore> ScoreDatasetAsync(IEnumerable<DataRecord> records, CancellationToken ct = default);
}
```
```csharp
public sealed class WeightedScoringStrategy : DataQualityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataQualityCategory Category;;
    public override DataQualityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void SetFieldWeight(string fieldName, double weight);
    public void RegisterFieldScorer(string fieldName, Func<object?, double> scorer);
    public Task<RecordQualityScore> ScoreAsync(DataRecord record, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataQuality/Strategies/Standardization/StandardizationStrategies.cs
```csharp
public sealed class StandardizationResult
{
}
    public required string RecordId { get; init; }
    public bool WasModified;;
    public required DataRecord OriginalRecord { get; init; }
    public required DataRecord StandardizedRecord { get; init; }
    public Dictionary<string, StandardizedValue> StandardizedFields { get; init; };
    public double ProcessingTimeMs { get; init; }
}
```
```csharp
public sealed class StandardizedValue
{
}
    public object? OriginalValue { get; init; }
    public object? StandardizedValue_ { get; init; }
    public required string StandardType { get; init; }
}
```
```csharp
public sealed class StandardFormat
{
}
    public required string FormatId { get; init; }
    public required string FieldPattern { get; init; }
    public required StandardizationType Type { get; init; }
    public Dictionary<string, object>? Parameters { get; init; }
    public int Priority { get; init; };
}
```
```csharp
public sealed class FormatStandardizationStrategy : DataQualityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataQualityCategory Category;;
    public override DataQualityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void AddFormat(StandardFormat format);;
    public void RegisterCustomFormatter(string formatterId, Func<object?, Dictionary<string, object>?, object?> formatter);
    public Task<StandardizationResult> StandardizeAsync(DataRecord record, CancellationToken ct = default);
}
```
```csharp
public sealed class CodeStandardizationStrategy : DataQualityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataQualityCategory Category;;
    public override DataQualityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    protected override Task InitializeCoreAsync(CancellationToken ct);
    public void RegisterCodeMapping(string codeType, Dictionary<string, string> mapping);
    public string StandardizeCode(string codeType, string value);
    public Task<StandardizationResult> StandardizeAsync(DataRecord record, Dictionary<string, string> fieldCodeTypes, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataQuality/Strategies/Validation/ValidationStrategies.cs
```csharp
public sealed class ValidationRule
{
}
    public required string RuleId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required ValidationType Type { get; init; }
    public required string[] TargetFields { get; init; }
    public IssueSeverity Severity { get; init; };
    public Dictionary<string, object>? Parameters { get; init; }
    public string? ErrorMessageTemplate { get; init; }
    public bool StopOnFail { get; init; }
    public bool Enabled { get; init; };
    public int Priority { get; init; };
    public string[]? Tags { get; init; }
}
```
```csharp
public sealed class ValidationResult
{
}
    public required string RecordId { get; init; }
    public bool IsValid;;
    public List<DataQualityIssue> Issues { get; init; };
    public List<string> EvaluatedRules { get; init; };
    public List<string> PassedRules { get; init; };
    public List<string> FailedRules { get; init; };
    public DateTime ValidatedAt { get; init; };
    public double ProcessingTimeMs { get; init; }
}
```
```csharp
public sealed class SchemaValidationStrategy : DataQualityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataQualityCategory Category;;
    public override DataQualityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void RegisterSchema(string schemaId, string schemaJson);
    public async Task<ValidationResult> ValidateAsync(DataRecord record, string schemaId, CancellationToken ct = default);
    protected override Task DisposeCoreAsync();
}
```
```csharp
public sealed class RuleBasedValidationStrategy : DataQualityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataQualityCategory Category;;
    public override DataQualityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void RegisterRule(ValidationRule rule);
    public void RegisterCustomValidator(string validatorId, Func<DataRecord, ValidationRule, DataQualityIssue?> validator);
    public bool RemoveRule(string ruleId);;
    public IEnumerable<ValidationRule> GetRules();;
    public async Task<ValidationResult> ValidateAsync(DataRecord record, CancellationToken ct = default);
    public async IAsyncEnumerable<ValidationResult> ValidateBatchAsync(IEnumerable<DataRecord> records, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default);
}
```
```csharp
public sealed class StatisticalValidationStrategy : DataQualityStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataQualityCategory Category;;
    public override DataQualityCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double OutlierThreshold { get => _outlierThreshold; set => _outlierThreshold = Math.Max(1.0, value); }
    public void Train(IEnumerable<DataRecord> records, string[] numericFields);
    public Task<ValidationResult> ValidateAsync(DataRecord record, CancellationToken ct = default);
    public FieldStatistics? GetFieldStatistics(string fieldName);
}
```
```csharp
public sealed class FieldStatistics
{
}
    public required string FieldName { get; init; }
    public int Count { get; init; }
    public double Mean { get; init; }
    public double StdDev { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double Median { get; init; }
    public double Q1 { get; init; }
    public double Q3 { get; init; }
    public double IQR { get; init; }
}
```
