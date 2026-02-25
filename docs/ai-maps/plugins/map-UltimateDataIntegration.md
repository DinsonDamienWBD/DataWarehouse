# Plugin: UltimateDataIntegration
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateDataIntegration

### File: Plugins/DataWarehouse.Plugins.UltimateDataIntegration/UltimateDataIntegrationPlugin.cs
```csharp
public sealed class UltimateDataIntegrationPlugin : OrchestrationPluginBase, IDisposable
{
}
    public void RecordOperation(int recordCount = 1);
    public void RecordFailure();
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string OrchestrationMode;;
    public override PluginCategory Category;;
    public string SemanticDescription;;
    public string[] SemanticTags;;
    public StrategyRegistry<IDataIntegrationStrategy> Registry;;
    public bool AuditEnabled { get => _auditEnabled; set => _auditEnabled = value; }
    public bool AutoOptimizationEnabled { get => _autoOptimizationEnabled; set => _autoOptimizationEnabled = value; }
    public UltimateDataIntegrationPlugin();
    public IDataIntegrationStrategy? GetStrategy(string strategyId);;
    public IEnumerable<IDataIntegrationStrategy> GetStrategiesByCategory(IntegrationCategory category);;
    public IntegrationStatistics GetStatistics();;
    protected override void Dispose(bool disposing);
}
```
```csharp
public interface IDataIntegrationStrategy
{
}
    string StrategyId { get; }
    string DisplayName { get; }
    IntegrationCategory Category { get; }
    DataIntegrationCapabilities Capabilities { get; }
    string SemanticDescription { get; }
    string[] Tags { get; }
}
```
```csharp
public sealed record DataIntegrationCapabilities
{
}
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
```
```csharp
public sealed record IntegrationPolicy
{
}
    public required string PolicyId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public IntegrationCategory TargetCategory { get; init; }
    public Dictionary<string, object>? Settings { get; init; }
    public bool IsEnabled { get; init; };
    public DateTimeOffset CreatedAt { get; init; }
}
```
```csharp
public sealed record IntegrationStatistics
{
}
    public long TotalOperations { get; init; }
    public long TotalRecordsProcessed { get; init; }
    public long TotalFailures { get; init; }
    public int RegisteredStrategies { get; init; }
    public int ActivePolicies { get; init; }
    public Dictionary<string, long> UsageByStrategy { get; init; };
}
```
```csharp
public abstract class DataIntegrationStrategyBase : StrategyBase, IDataIntegrationStrategy
{
}
    public abstract override string StrategyId { get; }
    public abstract string DisplayName { get; }
    public override string Name;;
    public abstract IntegrationCategory Category { get; }
    public abstract DataIntegrationCapabilities Capabilities { get; }
    public abstract string SemanticDescription { get; }
    public abstract string[] Tags { get; }
    protected void RecordRead();
    protected void RecordOperation(string operationName);
    protected void RecordFailure();
    public StrategyMetrics GetMetrics();;
}
```
```csharp
public sealed record StrategyMetrics
{
}
    public long TotalReads { get; init; }
    public long TotalWrites { get; init; }
    public long TotalFailures { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataIntegration/Composition/DataValidationEngine.cs
```csharp
public sealed class DataValidationEngine
{
}
    public void RegisterRuleSet(string datasetId, ValidationRuleSet ruleSet);
    public void RegisterCustomValidator(string name, Func<object?, ValidationResult> validator);
    public Task<BatchValidationResult> ValidateAsync(string datasetId, IReadOnlyList<Dictionary<string, object?>> records, ValidationOptions? options = null, CancellationToken ct = default);
}
```
```csharp
public sealed class DataQualityScoringEngine
{
}
    public Task<DataQualityReport> ScoreAsync(IReadOnlyList<Dictionary<string, object?>> records, DataQualityScoringOptions? options = null, CancellationToken ct = default);
}
```
```csharp
public sealed class ValidationRuleSet
{
}
    public required string DatasetId { get; init; }
    public required List<ValidationRule> Rules { get; init; }
}
```
```csharp
public sealed class ValidationRule
{
}
    public required string FieldName { get; init; }
    public bool IsRequired { get; init; }
    public string? RegexPattern { get; init; }
    public double? MinValue { get; init; }
    public double? MaxValue { get; init; }
    public int MaxLength { get; init; }
    public string? ExpectedType { get; init; }
    public List<string>? AllowedValues { get; init; }
    public string? CustomValidatorName { get; init; }
}
```
```csharp
public sealed class ValidationOptions
{
}
    public int StopAfterErrors { get; init; }
    public bool IncludeWarnings { get; init; };
}
```
```csharp
public sealed class BatchValidationResult
{
}
    public required string DatasetId { get; init; }
    public int TotalRecords { get; init; }
    public int ValidRecords { get; init; }
    public int InvalidRecords { get; init; }
    public required List<RecordValidationError> Errors { get; init; }
    public bool IsValid { get; init; }
    public DateTimeOffset ValidatedAt { get; init; }
}
```
```csharp
public sealed class RecordValidationError
{
}
    public int RowIndex { get; init; }
    public required string FieldName { get; init; }
    public required string RuleType { get; init; }
    public required string Message { get; init; }
    public ValidationSeverity Severity { get; init; };
}
```
```csharp
public sealed class ValidationResult
{
}
    public bool IsValid { get; init; }
    public string? Message { get; init; }
    public ValidationSeverity Severity { get; init; };
}
```
```csharp
public sealed class DataQualityScoringOptions
{
}
    public double CompletenessWeight { get; init; };
    public double ConsistencyWeight { get; init; };
    public double AccuracyWeight { get; init; };
    public double UniquenessWeight { get; init; };
}
```
```csharp
public sealed class DataQualityReport
{
}
    public int TotalRecords { get; init; }
    public double OverallScore { get; init; }
    public double CompletenessScore { get; init; }
    public double ConsistencyScore { get; init; }
    public double AccuracyScore { get; init; }
    public double UniquenessScore { get; init; }
    public int ColumnCount { get; init; }
    public required Dictionary<string, ColumnQualityScore> ColumnScores { get; init; }
    public DateTimeOffset ScoredAt { get; init; }
}
```
```csharp
public sealed class ColumnQualityScore
{
}
    public required string ColumnName { get; init; }
    public double Completeness { get; init; }
    public double Consistency { get; init; }
    public double Uniqueness { get; init; }
    public double Accuracy { get; init; }
    public int NullCount { get; init; }
    public int DistinctCount { get; init; }
    public int TotalCount { get; init; }
    public required string DominantType { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataIntegration/Composition/SchemaEvolutionEngine.cs
```csharp
[SdkCompatibility("3.0.0")]
public sealed class SchemaEvolutionEngine : IDisposable
{
}
    public SchemaEvolutionEngine(IMessageBus messageBus, SchemaEvolutionEngineConfig? config = null, ILogger? logger = null);
    public Task StartAsync(CancellationToken ct = default);
    public Task StopAsync();
    public async Task EvaluateProposalAsync(string proposalId, CancellationToken ct = default);
    public async Task ApproveProposalAsync(string proposalId, string approvedBy, CancellationToken ct = default);
    public async Task RejectProposalAsync(string proposalId, string rejectedBy, CancellationToken ct = default);
    public Task<IReadOnlyList<SchemaEvolutionProposal>> GetPendingProposalsAsync();
    public Task<IReadOnlyList<SchemaEvolutionProposal>> GetProposalHistoryAsync(string schemaId);
    public void Dispose();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataIntegration/Strategies/BatchStreaming/BatchStreamingStrategies.cs
```csharp
public sealed class LambdaArchitectureStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<LambdaPipeline> CreatePipelineAsync(string pipelineId, LambdaConfig config, CancellationToken ct = default);
    public async Task<BatchLayerResult> ProcessBatchAsync(string pipelineId, IReadOnlyList<Dictionary<string, object>> records, CancellationToken ct = default);
    public async IAsyncEnumerable<SpeedLayerResult> ProcessStreamAsync(string pipelineId, IAsyncEnumerable<Dictionary<string, object>> events, [EnumeratorCancellation] CancellationToken ct = default);
    public Task<ServingLayerResult> QueryAsync(string pipelineId, LambdaQuery query, CancellationToken ct = default);
}
```
```csharp
public sealed class KappaArchitectureStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<KappaPipeline> CreatePipelineAsync(string pipelineId, KappaConfig config, CancellationToken ct = default);
    public async IAsyncEnumerable<KappaResult> ProcessEventsAsync(string pipelineId, IAsyncEnumerable<StreamEvent> events, [EnumeratorCancellation] CancellationToken ct = default);
    public Task<ReprocessingJob> StartReprocessingAsync(string pipelineId, DateTime fromTimestamp, DateTime? toTimestamp = null, CancellationToken ct = default);
}
```
```csharp
public sealed class UnifiedBatchStreamingStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<UnifiedPipeline> CreatePipelineAsync(string pipelineId, UnifiedConfig config, CancellationToken ct = default);
    public async Task<UnifiedResult> ProcessAsync(string pipelineId, DataSource source, CancellationToken ct = default);
}
```
```csharp
public sealed class HybridIntegrationStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<HybridPipeline> CreatePipelineAsync(string pipelineId, HybridConfig config, CancellationToken ct = default);
    public Task<ModeSwitch> SwitchModeAsync(string pipelineId, IntegrationMode newMode, CancellationToken ct = default);
    public Task<AutoTuneResult> AutoTuneAsync(string pipelineId, PipelineMetrics metrics, CancellationToken ct = default);
}
```
```csharp
public sealed class RealTimeMaterializedViewStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<MaterializedView> CreateViewAsync(string viewId, string viewDefinition, MaterializedViewConfig? config = null, CancellationToken ct = default);
    public Task<ViewUpdateResult> ApplyUpdatesAsync(string viewId, IReadOnlyList<ViewUpdate> updates, CancellationToken ct = default);
    public Task<ViewQueryResult> QueryViewAsync(string viewId, ViewQuery query, CancellationToken ct = default);
    public Task<RefreshResult> RefreshViewAsync(string viewId, CancellationToken ct = default);
}
```
```csharp
public sealed record LambdaPipeline
{
}
    public required string PipelineId { get; init; }
    public required LambdaConfig Config { get; init; }
    public LambdaStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record LambdaConfig
{
}
    public TimeSpan BatchInterval { get; init; };
    public TimeSpan SpeedViewRetention { get; init; };
}
```
```csharp
public sealed record BatchView
{
}
    public required string PipelineId { get; init; }
    public long TotalRecords { get; set; }
    public int Version { get; set; }
    public DateTime? LastUpdated { get; set; }
}
```
```csharp
public sealed record SpeedView
{
}
    public required string PipelineId { get; init; }
    public long TotalEvents { get; set; }
    public int Version { get; set; }
    public DateTime? LastUpdated { get; set; }
}
```
```csharp
public sealed record BatchLayerResult
{
}
    public required string PipelineId { get; init; }
    public int RecordsProcessed { get; init; }
    public TimeSpan ProcessingTime { get; init; }
    public int ViewVersion { get; init; }
}
```
```csharp
public sealed record SpeedLayerResult
{
}
    public required string PipelineId { get; init; }
    public bool EventProcessed { get; init; }
    public TimeSpan Latency { get; init; }
}
```
```csharp
public sealed record LambdaQuery
{
}
    public string? AggregationType { get; init; }
    public DateTime? FromTime { get; init; }
    public DateTime? ToTime { get; init; }
}
```
```csharp
public sealed record ServingLayerResult
{
}
    public required string PipelineId { get; init; }
    public required Dictionary<string, object> Data { get; init; }
    public DateTime? BatchViewTimestamp { get; init; }
    public DateTime? SpeedViewTimestamp { get; init; }
}
```
```csharp
public sealed record KappaPipeline
{
}
    public required string PipelineId { get; init; }
    public required KappaConfig Config { get; init; }
    public KappaStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record KappaConfig
{
}
    public int Parallelism { get; init; };
    public TimeSpan RetentionPeriod { get; init; };
}
```
```csharp
public sealed record StreamView
{
}
    public required string PipelineId { get; init; }
    public long TotalEvents { get; set; }
    public int Version { get; set; }
    public DateTime? LastUpdated { get; set; }
}
```
```csharp
public sealed record StreamEvent
{
}
    public required string EventId { get; init; }
    public required Dictionary<string, object> Data { get; init; }
    public DateTime Timestamp { get; init; }
}
```
```csharp
public sealed record KappaResult
{
}
    public required string PipelineId { get; init; }
    public required string EventId { get; init; }
    public DateTime ProcessedAt { get; init; }
    public TimeSpan Latency { get; init; }
}
```
```csharp
public sealed record ReprocessingJob
{
}
    public required string JobId { get; init; }
    public required string PipelineId { get; init; }
    public DateTime FromTimestamp { get; init; }
    public DateTime ToTimestamp { get; init; }
    public ReprocessingStatus Status { get; set; }
    public DateTime StartedAt { get; init; }
}
```
```csharp
public sealed record UnifiedPipeline
{
}
    public required string PipelineId { get; init; }
    public required UnifiedConfig Config { get; init; }
    public UnifiedStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record UnifiedConfig
{
}
    public ExecutionMode? ForcedMode { get; init; }
    public TimeSpan MaxLatency { get; init; };
}
```
```csharp
public sealed record DataSource
{
}
    public required string SourceId { get; init; }
    public bool IsStreaming { get; init; }
    public long EstimatedRecords { get; init; }
}
```
```csharp
public sealed record UnifiedResult
{
}
    public required string PipelineId { get; init; }
    public ExecutionMode ExecutionMode { get; init; }
    public int RecordsProcessed { get; init; }
    public TimeSpan ProcessingTime { get; init; }
    public UnifiedExecutionStatus Status { get; init; }
}
```
```csharp
public sealed record HybridPipeline
{
}
    public required string PipelineId { get; init; }
    public required HybridConfig Config { get; init; }
    public IntegrationMode CurrentMode { get; set; }
    public HybridStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastModeSwitch { get; set; }
}
```
```csharp
public sealed record HybridConfig
{
}
    public bool EnableAutoTune { get; init; };
    public TimeSpan LatencyThreshold { get; init; };
    public long VolumeThresholdForStreaming { get; init; };
    public long VolumeThresholdForBatch { get; init; };
}
```
```csharp
public sealed record ModeSwitch
{
}
    public required string PipelineId { get; init; }
    public IntegrationMode PreviousMode { get; init; }
    public IntegrationMode NewMode { get; init; }
    public DateTime SwitchedAt { get; init; }
}
```
```csharp
public sealed record PipelineMetrics
{
}
    public TimeSpan CurrentLatency { get; init; }
    public long DataVolumePerSecond { get; init; }
    public double CpuUtilization { get; init; }
    public double MemoryUtilization { get; init; }
}
```
```csharp
public sealed record AutoTuneResult
{
}
    public required string PipelineId { get; init; }
    public IntegrationMode RecommendedMode { get; init; }
    public bool ModeSwitched { get; init; }
    public required string Reason { get; init; }
}
```
```csharp
public sealed record MaterializedView
{
}
    public required string ViewId { get; init; }
    public required string ViewDefinition { get; init; }
    public required MaterializedViewConfig Config { get; init; }
    public MaterializedViewStatus Status { get; set; }
    public int Version { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastUpdated { get; set; }
    public DateTime? LastRefreshed { get; set; }
    public Dictionary<string, object> Data { get; init; };
}
```
```csharp
public sealed record MaterializedViewConfig
{
}
    public bool EnableIncrementalUpdates { get; init; };
    public TimeSpan RefreshInterval { get; init; };
    public int MaxViewSize { get; init; };
}
```
```csharp
public sealed record ViewUpdate
{
}
    public required string Key { get; init; }
    public UpdateOperation Operation { get; init; }
    public object? Value { get; init; }
    public double? Delta { get; init; }
}
```
```csharp
public sealed record ViewUpdateResult
{
}
    public required string ViewId { get; init; }
    public int UpdatesApplied { get; init; }
    public int NewVersion { get; init; }
    public DateTime UpdatedAt { get; init; }
}
```
```csharp
public sealed record ViewQuery
{
}
    public IReadOnlyList<string>? Keys { get; init; }
    public string? Filter { get; init; }
}
```
```csharp
public sealed record ViewQueryResult
{
}
    public required string ViewId { get; init; }
    public int Version { get; init; }
    public required Dictionary<string, object> Data { get; init; }
    public TimeSpan Freshness { get; init; }
}
```
```csharp
public sealed record RefreshResult
{
}
    public required string ViewId { get; init; }
    public DateTime RefreshedAt { get; init; }
    public RefreshStatus Status { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataIntegration/Strategies/CDC/CdcStrategies.cs
```csharp
public sealed class LogBasedCdcStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<CdcConnector> CreateConnectorAsync(string connectorId, DatabaseType databaseType, string connectionString, CdcConnectorConfig? config = null, CancellationToken ct = default);
    public Task StartCaptureAsync(string connectorId, CancellationToken ct = default);
    public Task StopCaptureAsync(string connectorId, CancellationToken ct = default);
    public async IAsyncEnumerable<CdcEvent> ConsumeEventsAsync(string connectorId, long? fromLsn = null, [EnumeratorCancellation] CancellationToken ct = default);
    public Task<CdcEvent> SimulateChangeAsync(string connectorId, CdcOperationType operation, string tableName, Dictionary<string, object>? before, Dictionary<string, object>? after, CancellationToken ct = default);
    public Task<ConnectorStats> GetStatsAsync(string connectorId);
}
```
```csharp
public sealed class TriggerBasedCdcStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<TriggerConfig> CreateTriggerAsync(string triggerId, string tableName, IReadOnlyList<CdcOperationType> operations, IReadOnlyList<string>? capturedColumns = null, CancellationToken ct = default);
    public Task<ChangeRecord> FireTriggerAsync(string tableName, CdcOperationType operation, Dictionary<string, object>? oldValues, Dictionary<string, object>? newValues, CancellationToken ct = default);
    public Task<List<ChangeRecord>> GetChangesAsync(string tableName, DateTime? since = null, int limit = 1000, CancellationToken ct = default);
}
```
```csharp
public sealed class TimestampBasedCdcStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<TimestampTracker> CreateTrackerAsync(string trackerId, string tableName, string timestampColumn, TimestampTrackerConfig? config = null, CancellationToken ct = default);
    public Task<TimestampPollResult> PollChangesAsync(string trackerId, IReadOnlyList<Dictionary<string, object>> currentData, CancellationToken ct = default);
    public Task SetCheckpointAsync(string trackerId, DateTime checkpoint, CancellationToken ct = default);
}
```
```csharp
public sealed class OutboxPatternCdcStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<OutboxConfig> CreateOutboxAsync(string outboxId, string tableName, OutboxOptions? options = null, CancellationToken ct = default);
    public Task<OutboxEvent> WriteEventAsync(string outboxId, string aggregateType, string aggregateId, string eventType, Dictionary<string, object> payload, CancellationToken ct = default);
    public async IAsyncEnumerable<OutboxEvent> ProcessEventsAsync(string outboxId, int batchSize = 100, [EnumeratorCancellation] CancellationToken ct = default);
    public Task MarkProcessedAsync(string eventId, CancellationToken ct = default);
}
```
```csharp
public sealed class EventSourcingCdcStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<EventStream> CreateStreamAsync(string streamId, string aggregateType, EventStreamConfig? config = null, CancellationToken ct = default);
    public Task<DomainEvent> AppendEventAsync(string streamId, string eventType, Dictionary<string, object> eventData, int? expectedVersion = null, CancellationToken ct = default);
    public Task<List<DomainEvent>> ReadEventsAsync(string streamId, int? fromVersion = null, int? toVersion = null, CancellationToken ct = default);
    public Task<int> GetStreamVersionAsync(string streamId, CancellationToken ct = default);
}
```
```csharp
public class ConcurrencyException : Exception
{
}
    public ConcurrencyException(string message) : base(message);
}
```
```csharp
public sealed record CdcConnector
{
}
    public required string ConnectorId { get; init; }
    public DatabaseType DatabaseType { get; init; }
    public required string ConnectionString { get; init; }
    public required CdcConnectorConfig Config { get; init; }
    public ConnectorStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; set; }
}
```
```csharp
public sealed record CdcConnectorConfig
{
}
    public IReadOnlyList<string>? Tables { get; init; }
    public string? SlotName { get; init; }
    public bool SnapshotOnStart { get; init; };
    public int BatchSize { get; init; };
}
```
```csharp
public sealed record CdcEvent
{
}
    public required string EventId { get; init; }
    public required string ConnectorId { get; init; }
    public CdcOperationType Operation { get; init; }
    public required string TableName { get; init; }
    public Dictionary<string, object>? Before { get; init; }
    public Dictionary<string, object>? After { get; init; }
    public long Lsn { get; init; }
    public DateTime Timestamp { get; init; }
}
```
```csharp
public sealed record ConnectorStats
{
}
    public required string ConnectorId { get; init; }
    public ConnectorStatus Status { get; init; }
    public int PendingEvents { get; init; }
    public long TotalEventsCaptured { get; init; }
}
```
```csharp
public sealed record TriggerConfig
{
}
    public required string TriggerId { get; init; }
    public required string TableName { get; init; }
    public required List<CdcOperationType> Operations { get; init; }
    public required List<string> CapturedColumns { get; init; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record ChangeRecord
{
}
    public required string ChangeId { get; init; }
    public required string TableName { get; init; }
    public CdcOperationType Operation { get; init; }
    public Dictionary<string, object>? OldValues { get; init; }
    public Dictionary<string, object>? NewValues { get; init; }
    public DateTime ChangeTimestamp { get; init; }
}
```
```csharp
public sealed record TimestampTracker
{
}
    public required string TrackerId { get; init; }
    public required string TableName { get; init; }
    public required string TimestampColumn { get; init; }
    public required TimestampTrackerConfig Config { get; init; }
    public DateTime LastCheckpoint { get; set; }
    public DateTime? LastPollAt { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record TimestampTrackerConfig
{
}
    public TimeSpan PollInterval { get; init; };
    public TimeSpan LookbackWindow { get; init; };
}
```
```csharp
public sealed record TimestampPollResult
{
}
    public required string TrackerId { get; init; }
    public DateTime PreviousCheckpoint { get; init; }
    public DateTime NewCheckpoint { get; init; }
    public int ChangesDetected { get; init; }
    public required List<Dictionary<string, object>> Changes { get; init; }
}
```
```csharp
public sealed record OutboxConfig
{
}
    public required string OutboxId { get; init; }
    public required string TableName { get; init; }
    public required OutboxOptions Options { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record OutboxOptions
{
}
    public TimeSpan RetentionPeriod { get; init; };
    public int MaxRetries { get; init; };
    public TimeSpan RetryDelay { get; init; };
}
```
```csharp
public sealed record OutboxEvent
{
}
    public required string EventId { get; init; }
    public required string OutboxId { get; init; }
    public required string AggregateType { get; init; }
    public required string AggregateId { get; init; }
    public required string EventType { get; init; }
    public required Dictionary<string, object> Payload { get; init; }
    public OutboxEventStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime? PublishedAt { get; set; }
}
```
```csharp
public sealed record EventStream
{
}
    public required string StreamId { get; init; }
    public required string AggregateType { get; init; }
    public required EventStreamConfig Config { get; init; }
    public int Version { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record EventStreamConfig
{
}
    public int MaxEventsPerStream { get; init; };
    public bool EnableSnapshots { get; init; };
    public int SnapshotInterval { get; init; };
}
```
```csharp
public sealed record DomainEvent
{
}
    public required string EventId { get; init; }
    public required string StreamId { get; init; }
    public required string EventType { get; init; }
    public required Dictionary<string, object> EventData { get; init; }
    public int Version { get; init; }
    public DateTime Timestamp { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataIntegration/Strategies/ELT/EltPatternStrategies.cs
```csharp
public sealed class CloudNativeEltStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<EltPipeline> CreatePipelineAsync(string pipelineId, EltSource source, EltTarget target, IReadOnlyList<EltTransformation> transformations, EltConfig? config = null, CancellationToken ct = default);
    public async Task<EltExecutionResult> ExecuteAsync(string pipelineId, CancellationToken ct = default);
}
```
```csharp
public sealed class DbtStyleTransformationStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<DbtProject> CreateProjectAsync(string projectId, string projectName, DbtProjectConfig? config = null, CancellationToken ct = default);
    public Task<DbtModel> AddModelAsync(string projectId, string modelName, string sqlQuery, MaterializationType materialization = MaterializationType.View, IReadOnlyList<string>? dependencies = null, CancellationToken ct = default);
    public async Task<DbtRunResult> RunAsync(string projectId, DbtRunOptions? options = null, CancellationToken ct = default);
}
```
```csharp
public sealed class ReverseEltStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<ReverseEltSync> CreateSyncAsync(string syncId, WarehouseSource source, OperationalTarget target, SyncConfig? config = null, CancellationToken ct = default);
    public async Task<ReverseSyncResult> ExecuteSyncAsync(string syncId, CancellationToken ct = default);
}
```
```csharp
public sealed class MedallionArchitectureStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<MedallionPipeline> CreatePipelineAsync(string pipelineId, MedallionSource source, MedallionConfig? config = null, CancellationToken ct = default);
    public async Task<MedallionResult> ProcessAsync(string pipelineId, CancellationToken ct = default);
}
```
```csharp
public sealed class SemanticLayerStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<SemanticModel> CreateModelAsync(string modelId, string modelName, IReadOnlyList<Dimension> dimensions, IReadOnlyList<Measure> measures, CancellationToken ct = default);
    public Task<MetricDefinition> DefineMetricAsync(string metricId, string metricName, string expression, MetricType metricType, string? description = null, CancellationToken ct = default);
    public Task<SemanticQueryResult> QueryAsync(string modelId, SemanticQuery query, CancellationToken ct = default);
}
```
```csharp
public sealed record EltPipeline
{
}
    public required string PipelineId { get; init; }
    public required EltSource Source { get; init; }
    public required EltTarget Target { get; init; }
    public required List<EltTransformation> Transformations { get; init; }
    public required EltConfig Config { get; init; }
    public EltPipelineStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record EltSource
{
}
    public required string SourceType { get; init; }
    public required string ConnectionString { get; init; }
    public string? Path { get; init; }
    public string? Format { get; init; }
}
```
```csharp
public sealed record EltTarget
{
}
    public required string WarehouseType { get; init; }
    public required string ConnectionString { get; init; }
    public required string Schema { get; init; }
    public required string Table { get; init; }
}
```
```csharp
public sealed record EltTransformation
{
}
    public required string TransformationId { get; init; }
    public required string Name { get; init; }
    public required string SqlQuery { get; init; }
    public IReadOnlyList<string>? Dependencies { get; init; }
}
```
```csharp
public sealed record EltConfig
{
}
    public bool EnableIncremental { get; init; };
    public int Parallelism { get; init; };
    public TimeSpan Timeout { get; init; };
}
```
```csharp
public sealed record EltExecutionResult
{
}
    public required string PipelineId { get; init; }
    public required string ExecutionId { get; init; }
    public long RecordsLoaded { get; init; }
    public required List<TransformationResult> TransformationResults { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public EltExecutionStatus Status { get; init; }
}
```
```csharp
public sealed record LoadResult
{
}
    public long RecordsLoaded { get; init; }
    public long BytesLoaded { get; init; }
}
```
```csharp
public sealed record TransformationResult
{
}
    public required string TransformationId { get; init; }
    public long RowsAffected { get; init; }
    public long DurationMs { get; init; }
    public TransformationStatus Status { get; init; }
}
```
```csharp
public sealed record DbtProject
{
}
    public required string ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public required DbtProjectConfig Config { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record DbtProjectConfig
{
}
    public string? TargetSchema { get; init; }
    public bool EnableTests { get; init; };
    public bool EnableDocs { get; init; };
}
```
```csharp
public sealed record DbtModel
{
}
    public required string ModelId { get; init; }
    public required string ProjectId { get; init; }
    public required string ModelName { get; init; }
    public required string SqlQuery { get; init; }
    public MaterializationType Materialization { get; init; }
    public required List<string> Dependencies { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record DbtRunOptions
{
}
    public bool FullRefresh { get; init; };
    public IReadOnlyList<string>? SelectModels { get; init; }
    public IReadOnlyList<string>? ExcludeModels { get; init; }
}
```
```csharp
public sealed record DbtRunResult
{
}
    public required string ProjectId { get; init; }
    public required string RunId { get; init; }
    public int ModelsExecuted { get; init; }
    public required List<DbtModelResult> ModelResults { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public DbtRunStatus Status { get; init; }
}
```
```csharp
public sealed record DbtModelResult
{
}
    public required string ModelId { get; init; }
    public required string ModelName { get; init; }
    public long RowsAffected { get; init; }
    public long DurationMs { get; init; }
    public DbtModelStatus Status { get; init; }
}
```
```csharp
public sealed record ReverseEltSync
{
}
    public required string SyncId { get; init; }
    public required WarehouseSource Source { get; init; }
    public required OperationalTarget Target { get; init; }
    public required SyncConfig Config { get; init; }
    public SyncStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record WarehouseSource
{
}
    public required string WarehouseType { get; init; }
    public required string ConnectionString { get; init; }
    public required string Query { get; init; }
}
```
```csharp
public sealed record OperationalTarget
{
}
    public required string TargetType { get; init; }
    public required string ConnectionString { get; init; }
    public required string ObjectName { get; init; }
    public Dictionary<string, string>? FieldMappings { get; init; }
}
```
```csharp
public sealed record SyncConfig
{
}
    public SyncMode Mode { get; init; };
    public int BatchSize { get; init; };
    public TimeSpan Interval { get; init; };
}
```
```csharp
public sealed record ReverseSyncResult
{
}
    public required string SyncId { get; init; }
    public required string ExecutionId { get; init; }
    public int RecordsExtracted { get; init; }
    public int RecordsSynced { get; init; }
    public int RecordsFailed { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public ReverseSyncStatus Status { get; init; }
}
```
```csharp
public sealed record MedallionPipeline
{
}
    public required string PipelineId { get; init; }
    public required MedallionSource Source { get; init; }
    public required MedallionConfig Config { get; init; }
    public MedallionStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record MedallionSource
{
}
    public required string SourceType { get; init; }
    public required string Path { get; init; }
    public string? Format { get; init; }
}
```
```csharp
public sealed record MedallionConfig
{
}
    public string BronzePath { get; init; };
    public string SilverPath { get; init; };
    public string GoldPath { get; init; };
    public string Format { get; init; };
}
```
```csharp
public sealed record MedallionResult
{
}
    public required string PipelineId { get; init; }
    public required string ExecutionId { get; init; }
    public int BronzeRecords { get; init; }
    public int SilverRecords { get; init; }
    public int GoldRecords { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public MedallionExecutionStatus Status { get; init; }
}
```
```csharp
public sealed record TierResult
{
}
    public required string Tier { get; init; }
    public int Records { get; init; }
}
```
```csharp
public sealed record SemanticModel
{
}
    public required string ModelId { get; init; }
    public required string ModelName { get; init; }
    public required List<Dimension> Dimensions { get; init; }
    public required List<Measure> Measures { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record Dimension
{
}
    public required string DimensionId { get; init; }
    public required string Name { get; init; }
    public required string Expression { get; init; }
    public string? Description { get; init; }
}
```
```csharp
public sealed record Measure
{
}
    public required string MeasureId { get; init; }
    public required string Name { get; init; }
    public required string Expression { get; init; }
    public required string AggregationType { get; init; }
    public string? Description { get; init; }
}
```
```csharp
public sealed record MetricDefinition
{
}
    public required string MetricId { get; init; }
    public required string MetricName { get; init; }
    public required string Expression { get; init; }
    public MetricType MetricType { get; init; }
    public string? Description { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record SemanticQuery
{
}
    public required IReadOnlyList<string> Metrics { get; init; }
    public required IReadOnlyList<string> Dimensions { get; init; }
    public IReadOnlyList<string>? Filters { get; init; }
    public int? Limit { get; init; }
}
```
```csharp
public sealed record SemanticQueryResult
{
}
    public required string QueryId { get; init; }
    public required string ModelId { get; init; }
    public int RowCount { get; init; }
    public required List<string> Columns { get; init; }
    public long ExecutionTimeMs { get; init; }
    public QueryStatus Status { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataIntegration/Strategies/ETL/EtlPipelineStrategies.cs
```csharp
public sealed class ClassicEtlPipelineStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<EtlJob> CreateJobAsync(string jobId, EtlSource source, IReadOnlyList<EtlTransformation> transformations, EtlTarget target, EtlJobConfig? config = null, CancellationToken ct = default);
    public async Task<JobExecution> ExecuteJobAsync(string jobId, CancellationToken ct = default);
}
```
```csharp
public sealed class StreamingEtlPipelineStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<StreamingEtlJob> CreateJobAsync(string jobId, StreamingSource source, IReadOnlyList<StreamingTransformation> transformations, StreamingSink sink, StreamingEtlConfig? config = null, CancellationToken ct = default);
    public Task StartJobAsync(string jobId, CancellationToken ct = default);
    public async IAsyncEnumerable<ProcessedEvent> ProcessEventsAsync(string jobId, IAsyncEnumerable<StreamingEvent> events, [EnumeratorCancellation] CancellationToken ct = default);
}
```
```csharp
public sealed class MicroBatchEtlPipelineStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<MicroBatchJob> CreateJobAsync(string jobId, MicroBatchSource source, IReadOnlyList<EtlTransformation> transformations, MicroBatchSink sink, MicroBatchConfig? config = null, CancellationToken ct = default);
    public async Task<MicroBatchResult> ProcessBatchAsync(string jobId, IReadOnlyList<Dictionary<string, object>> records, CancellationToken ct = default);
}
```
```csharp
public sealed class ParallelEtlPipelineStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<ParallelEtlJob> CreateJobAsync(string jobId, ParallelSource source, IReadOnlyList<EtlTransformation> transformations, ParallelSink sink, ParallelConfig? config = null, CancellationToken ct = default);
    public async Task<ParallelExecutionResult> ExecuteAsync(string jobId, CancellationToken ct = default);
}
```
```csharp
public sealed class IncrementalEtlPipelineStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<IncrementalEtlJob> CreateJobAsync(string jobId, IncrementalSource source, IReadOnlyList<EtlTransformation> transformations, IncrementalTarget target, IncrementalConfig? config = null, CancellationToken ct = default);
    public async Task<IncrementalRunResult> ExecuteRunAsync(string jobId, CancellationToken ct = default);
    public Task<WatermarkState> GetWatermarkAsync(string jobId);
}
```
```csharp
public sealed record EtlJob
{
}
    public required string JobId { get; init; }
    public required EtlSource Source { get; init; }
    public required List<EtlTransformation> Transformations { get; init; }
    public required EtlTarget Target { get; init; }
    public required EtlJobConfig Config { get; init; }
    public JobStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record EtlSource
{
}
    public required string SourceType { get; init; }
    public required string ConnectionString { get; init; }
    public string? Query { get; init; }
    public Dictionary<string, object>? Options { get; init; }
}
```
```csharp
public sealed record EtlTarget
{
}
    public required string TargetType { get; init; }
    public required string ConnectionString { get; init; }
    public string? TableName { get; init; }
    public WriteMode WriteMode { get; init; };
    public Dictionary<string, object>? Options { get; init; }
}
```
```csharp
public sealed record EtlTransformation
{
}
    public required string TransformationId { get; init; }
    public required TransformationType Type { get; init; }
    public Dictionary<string, string>? FieldMappings { get; init; }
    public Dictionary<string, string>? TypeMappings { get; init; }
    public Dictionary<string, string>? Expressions { get; init; }
    public string? FilterPredicate { get; init; }
}
```
```csharp
public sealed record EtlJobConfig
{
}
    public int BatchSize { get; init; };
    public int Parallelism { get; init; };
    public bool EnableStaging { get; init; };
    public bool EnableDataQuality { get; init; };
    public TimeSpan Timeout { get; init; };
}
```
```csharp
public sealed record JobExecution
{
}
    public required string ExecutionId { get; init; }
    public required string JobId { get; init; }
    public ExecutionStatus Status { get; set; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
    public int ExtractedRecords { get; set; }
    public int TransformedRecords { get; set; }
    public int LoadedRecords { get; set; }
    public string? ErrorMessage { get; set; }
}
```
```csharp
public sealed record StreamingEtlJob
{
}
    public required string JobId { get; init; }
    public required StreamingSource Source { get; init; }
    public required List<StreamingTransformation> Transformations { get; init; }
    public required StreamingSink Sink { get; init; }
    public required StreamingEtlConfig Config { get; init; }
    public StreamingJobStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; set; }
}
```
```csharp
public sealed record StreamingSource
{
}
    public required string SourceType { get; init; }
    public required string ConnectionString { get; init; }
    public string? Topic { get; init; }
    public string? ConsumerGroup { get; init; }
}
```
```csharp
public sealed record StreamingSink
{
}
    public required string SinkType { get; init; }
    public required string ConnectionString { get; init; }
    public string? Topic { get; init; }
}
```
```csharp
public sealed record StreamingTransformation
{
}
    public required string TransformationId { get; init; }
    public required string TransformationType { get; init; }
    public Dictionary<string, object>? Config { get; init; }
}
```
```csharp
public sealed record StreamingEtlConfig
{
}
    public CheckpointMode CheckpointMode { get; init; };
    public TimeSpan CheckpointInterval { get; init; };
    public int Parallelism { get; init; };
}
```
```csharp
public sealed record StreamingEvent
{
}
    public required string EventId { get; init; }
    public required Dictionary<string, object> Data { get; init; }
    public DateTime ProcessingTime { get; init; }
    public DateTime? EventTime { get; init; }
}
```
```csharp
public sealed record ProcessedEvent
{
}
    public required string EventId { get; init; }
    public required Dictionary<string, object> Data { get; init; }
    public DateTime ProcessedAt { get; init; }
    public DateTime Watermark { get; init; }
}
```
```csharp
public sealed record MicroBatchJob
{
}
    public required string JobId { get; init; }
    public required MicroBatchSource Source { get; init; }
    public required List<EtlTransformation> Transformations { get; init; }
    public required MicroBatchSink Sink { get; init; }
    public required MicroBatchConfig Config { get; init; }
    public MicroBatchStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record MicroBatchSource
{
}
    public required string SourceType { get; init; }
    public required string ConnectionString { get; init; }
}
```
```csharp
public sealed record MicroBatchSink
{
}
    public required string SinkType { get; init; }
    public required string ConnectionString { get; init; }
}
```
```csharp
public sealed record MicroBatchConfig
{
}
    public TimeSpan BatchInterval { get; init; };
    public int MaxBatchSize { get; init; };
    public int Parallelism { get; init; };
}
```
```csharp
public sealed record MicroBatchResult
{
}
    public required string BatchId { get; init; }
    public required string JobId { get; init; }
    public int InputRecords { get; init; }
    public int OutputRecords { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public BatchStatus Status { get; init; }
}
```
```csharp
public sealed record ParallelEtlJob
{
}
    public required string JobId { get; init; }
    public required ParallelSource Source { get; init; }
    public required List<EtlTransformation> Transformations { get; init; }
    public required ParallelSink Sink { get; init; }
    public required ParallelConfig Config { get; init; }
    public ParallelJobStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record ParallelSource
{
}
    public required string SourceType { get; init; }
    public required string ConnectionString { get; init; }
    public string? PartitionColumn { get; init; }
}
```
```csharp
public sealed record ParallelSink
{
}
    public required string SinkType { get; init; }
    public required string ConnectionString { get; init; }
}
```
```csharp
public sealed record ParallelConfig
{
}
    public int Parallelism { get; init; };
    public bool DynamicPartitioning { get; init; };
}
```
```csharp
public sealed record ParallelExecutionResult
{
}
    public required string JobId { get; init; }
    public int TotalPartitions { get; init; }
    public required List<PartitionResult> PartitionResults { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public ParallelExecutionStatus Status { get; init; }
}
```
```csharp
public sealed record PartitionResult
{
}
    public int PartitionId { get; init; }
    public int RecordsProcessed { get; init; }
    public PartitionStatus Status { get; init; }
}
```
```csharp
public sealed record IncrementalEtlJob
{
}
    public required string JobId { get; init; }
    public required IncrementalSource Source { get; init; }
    public required List<EtlTransformation> Transformations { get; init; }
    public required IncrementalTarget Target { get; init; }
    public required IncrementalConfig Config { get; init; }
    public IncrementalStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record IncrementalSource
{
}
    public required string SourceType { get; init; }
    public required string ConnectionString { get; init; }
    public required string WatermarkColumn { get; init; }
}
```
```csharp
public sealed record IncrementalTarget
{
}
    public required string TargetType { get; init; }
    public required string ConnectionString { get; init; }
    public MergeStrategy MergeStrategy { get; init; };
}
```
```csharp
public sealed record IncrementalConfig
{
}
    public DateTime? InitialWatermark { get; init; }
    public TimeSpan LookbackWindow { get; init; };
}
```
```csharp
public sealed record WatermarkState
{
}
    public required string JobId { get; init; }
    public DateTime LastWatermark { get; set; }
    public DateTime? LastRunAt { get; set; }
}
```
```csharp
public sealed record IncrementalRunResult
{
}
    public required string JobId { get; init; }
    public required string RunId { get; init; }
    public DateTime PreviousWatermark { get; init; }
    public DateTime NewWatermark { get; init; }
    public int RecordsProcessed { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public IncrementalRunStatus Status { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataIntegration/Strategies/Mapping/DataMappingStrategies.cs
```csharp
public sealed class SchemaMappingStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<SchemaMapping> CreateMappingAsync(string mappingId, Schema sourceSchema, Schema targetSchema, IReadOnlyList<FieldMapping>? fieldMappings = null, MappingOptions? options = null, CancellationToken ct = default);
    public Task<SchemaMappingResult> ApplyMappingAsync(string mappingId, IReadOnlyList<Dictionary<string, object>> records, CancellationToken ct = default);
}
```
```csharp
public sealed class SemanticMappingStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<BusinessTerm> RegisterTermAsync(string termId, string termName, string definition, IReadOnlyList<string>? synonyms = null, IReadOnlyList<string>? relatedTerms = null, CancellationToken ct = default);
    public Task<SemanticMapping> CreateMappingAsync(string mappingId, IReadOnlyList<SemanticFieldMapping> fieldMappings, SemanticMappingOptions? options = null, CancellationToken ct = default);
    public Task<List<MappingSuggestion>> SuggestMappingsAsync(IReadOnlyList<string> sourceFields, IReadOnlyList<string> targetFields, CancellationToken ct = default);
}
```
```csharp
public sealed class HierarchicalMappingStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<HierarchicalMapping> CreateMappingAsync(string mappingId, IReadOnlyList<PathMapping> pathMappings, HierarchicalMappingOptions? options = null, CancellationToken ct = default);
    public Task<HierarchicalMappingResult> ApplyMappingAsync(string mappingId, IReadOnlyList<Dictionary<string, object>> records, CancellationToken ct = default);
}
```
```csharp
public sealed class DynamicMappingStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<DynamicMapping> CreateMappingAsync(string mappingId, IReadOnlyList<DynamicFieldMapping> fieldMappings, DynamicMappingOptions? options = null, CancellationToken ct = default);
    public Task<DynamicMappingResult> ApplyMappingAsync(string mappingId, IReadOnlyList<Dictionary<string, object>> records, Dictionary<string, object>? runtimeContext = null, CancellationToken ct = default);
}
```
```csharp
public sealed class BidirectionalMappingStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<BidirectionalMapping> CreateMappingAsync(string mappingId, IReadOnlyList<BidirectionalFieldMapping> fieldMappings, BidirectionalMappingOptions? options = null, CancellationToken ct = default);
    public Task<BidirectionalMappingResult> MapForwardAsync(string mappingId, IReadOnlyList<Dictionary<string, object>> records, CancellationToken ct = default);
    public Task<BidirectionalMappingResult> MapReverseAsync(string mappingId, IReadOnlyList<Dictionary<string, object>> records, CancellationToken ct = default);
}
```
```csharp
public sealed record SchemaMapping
{
}
    public required string MappingId { get; init; }
    public required Schema SourceSchema { get; init; }
    public required Schema TargetSchema { get; init; }
    public required List<FieldMapping> FieldMappings { get; init; }
    public required MappingOptions Options { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record Schema
{
}
    public required string SchemaId { get; init; }
    public required string Name { get; init; }
    public required List<SchemaField> Fields { get; init; }
    public int Version { get; init; };
}
```
```csharp
public sealed record SchemaField
{
}
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public bool IsNullable { get; init; };
}
```
```csharp
public sealed record FieldMapping
{
}
    public required string SourceField { get; init; }
    public required string TargetField { get; init; }
    public FieldTransformationType TransformationType { get; init; };
    public string? TargetType { get; init; }
    public string? Expression { get; init; }
    public object? DefaultValue { get; init; }
}
```
```csharp
public sealed record MappingOptions
{
}
    public bool AllowMissingFields { get; init; };
    public bool SkipOnError { get; init; };
    public bool StrictTypeMatching { get; init; };
}
```
```csharp
public sealed record SchemaMappingResult
{
}
    public required string MappingId { get; init; }
    public int InputRecords { get; init; }
    public int OutputRecords { get; init; }
    public required List<MappingError> Errors { get; init; }
    public required List<Dictionary<string, object>> MappedData { get; init; }
    public MappingStatus Status { get; init; }
}
```
```csharp
public sealed record MappingError
{
}
    public int RecordIndex { get; init; }
    public required string ErrorMessage { get; init; }
}
```
```csharp
public sealed record SemanticMapping
{
}
    public required string MappingId { get; init; }
    public required List<SemanticFieldMapping> FieldMappings { get; init; }
    public required SemanticMappingOptions Options { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record SemanticFieldMapping
{
}
    public required string SourceField { get; init; }
    public required string TargetField { get; init; }
    public string? BusinessTermId { get; init; }
    public double Confidence { get; init; };
}
```
```csharp
public sealed record SemanticMappingOptions
{
}
    public double MinConfidence { get; init; };
    public bool UseSynonyms { get; init; };
}
```
```csharp
public sealed record BusinessTerm
{
}
    public required string TermId { get; init; }
    public required string TermName { get; init; }
    public required string Definition { get; init; }
    public required List<string> Synonyms { get; init; }
    public required List<string> RelatedTerms { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record MappingSuggestion
{
}
    public required string SourceField { get; init; }
    public required string TargetField { get; init; }
    public double Confidence { get; init; }
    public required string Reason { get; init; }
}
```
```csharp
public sealed record HierarchicalMapping
{
}
    public required string MappingId { get; init; }
    public required List<PathMapping> PathMappings { get; init; }
    public required HierarchicalMappingOptions Options { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record PathMapping
{
}
    public required string SourcePath { get; init; }
    public required string TargetPath { get; init; }
    public ArrayHandling ArrayHandling { get; init; };
}
```
```csharp
public sealed record HierarchicalMappingOptions
{
}
    public bool SkipNulls { get; init; };
    public bool PreserveUnmapped { get; init; };
}
```
```csharp
public sealed record HierarchicalMappingResult
{
}
    public required string MappingId { get; init; }
    public int InputRecords { get; init; }
    public int OutputRecords { get; init; }
    public required List<Dictionary<string, object>> MappedData { get; init; }
    public HierarchicalMappingStatus Status { get; init; }
}
```
```csharp
public sealed record DynamicMapping
{
}
    public required string MappingId { get; init; }
    public required List<DynamicFieldMapping> FieldMappings { get; init; }
    public required DynamicMappingOptions Options { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record DynamicFieldMapping
{
}
    public required string TargetField { get; init; }
    public required string Expression { get; init; }
    public string? Condition { get; init; }
}
```
```csharp
public sealed record DynamicMappingOptions
{
}
    public bool IncludeUnmappedFields { get; init; };
    public bool StrictMode { get; init; };
}
```
```csharp
public sealed record DynamicMappingResult
{
}
    public required string MappingId { get; init; }
    public int InputRecords { get; init; }
    public int OutputRecords { get; init; }
    public required List<Dictionary<string, object>> MappedData { get; init; }
    public DynamicMappingStatus Status { get; init; }
}
```
```csharp
public sealed record BidirectionalMapping
{
}
    public required string MappingId { get; init; }
    public required List<BidirectionalFieldMapping> FieldMappings { get; init; }
    public required BidirectionalMappingOptions Options { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record BidirectionalFieldMapping
{
}
    public required string SourceField { get; init; }
    public required string TargetField { get; init; }
    public FieldTransform? ForwardTransform { get; init; }
    public FieldTransform? ReverseTransform { get; init; }
}
```
```csharp
public sealed record FieldTransform
{
}
    public TransformType Type { get; init; }
    public double? Factor { get; init; }
}
```
```csharp
public sealed record BidirectionalMappingOptions
{
}
    public bool ValidateReversibility { get; init; };
}
```
```csharp
public sealed record BidirectionalMappingResult
{
}
    public required string MappingId { get; init; }
    public MappingDirection Direction { get; init; }
    public int InputRecords { get; init; }
    public int OutputRecords { get; init; }
    public required List<Dictionary<string, object>> MappedData { get; init; }
    public BidirectionalMappingStatus Status { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataIntegration/Strategies/Monitoring/IntegrationMonitoringStrategies.cs
```csharp
public sealed class PipelineHealthMonitoringStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<PipelineHealth> RegisterPipelineAsync(string pipelineId, HealthMonitoringConfig? config = null, CancellationToken ct = default);
    public Task<HealthCheck> RecordHealthCheckAsync(string pipelineId, HealthMetrics metrics, CancellationToken ct = default);
    public Task<PipelineHealth> GetHealthAsync(string pipelineId, CancellationToken ct = default);
    public Task<List<HealthCheck>> GetHealthHistoryAsync(string pipelineId, DateTime? since = null, int limit = 100, CancellationToken ct = default);
}
```
```csharp
public sealed class SlaTrackingStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<SlaDefinition> DefineSlaAsync(string slaId, string name, SlaRequirements requirements, CancellationToken ct = default);
    public Task<SlaComplianceResult> CheckComplianceAsync(string slaId, SlaMetrics metrics, CancellationToken ct = default);
    public Task<SlaReport> GetReportAsync(string slaId, DateTime fromDate, DateTime toDate, CancellationToken ct = default);
}
```
```csharp
public sealed class DataQualityMonitoringStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<DataQualityProfile> CreateProfileAsync(string profileId, string datasetName, IReadOnlyList<QualityRule> rules, CancellationToken ct = default);
    public Task<QualityEvaluation> EvaluateQualityAsync(string profileId, IReadOnlyList<Dictionary<string, object>> records, CancellationToken ct = default);
    public Task<QualityTrend> GetTrendAsync(string profileId, int periods = 10, CancellationToken ct = default);
}
```
```csharp
public sealed class IntegrationLineageTrackingStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<LineageNode> RegisterNodeAsync(string nodeId, string nodeName, LineageNodeType nodeType, Dictionary<string, object>? metadata = null, CancellationToken ct = default);
    public Task<LineageEdge> RecordEdgeAsync(string sourceNodeId, string targetNodeId, LineageEdgeType edgeType, Dictionary<string, object>? transformations = null, CancellationToken ct = default);
    public Task<LineageGraph> GetUpstreamLineageAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default);
    public Task<LineageGraph> GetDownstreamLineageAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default);
    public Task<ImpactAnalysis> AnalyzeImpactAsync(string nodeId, CancellationToken ct = default);
}
```
```csharp
public sealed class AlertNotificationStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<AlertRule> CreateRuleAsync(string ruleId, string ruleName, AlertCondition condition, AlertAction action, CancellationToken ct = default);
    public Task<NotificationChannel> RegisterChannelAsync(string channelId, string channelName, ChannelType channelType, Dictionary<string, string> config, CancellationToken ct = default);
    public Task<Alert> TriggerAlertAsync(string ruleId, Dictionary<string, object> context, CancellationToken ct = default);
    public Task<Alert> AcknowledgeAlertAsync(string alertId, string acknowledgedBy, CancellationToken ct = default);
    public Task<List<Alert>> GetActiveAlertsAsync(AlertSeverity? minSeverity = null, CancellationToken ct = default);
}
```
```csharp
public sealed record PipelineHealth
{
}
    public required string PipelineId { get; init; }
    public required HealthMonitoringConfig Config { get; init; }
    public PipelineHealthStatus Status { get; set; }
    public DateTime RegisteredAt { get; init; }
    public DateTime? LastCheckedAt { get; set; }
    public HealthCheck? LastCheck { get; set; }
    public List<HealthAlert> ActiveAlerts { get; init; };
}
```
```csharp
public sealed record HealthMonitoringConfig
{
}
    public TimeSpan CheckInterval { get; init; };
    public double WarningErrorRateThreshold { get; init; };
    public double CriticalErrorRateThreshold { get; init; };
    public TimeSpan WarningLatencyThreshold { get; init; };
    public TimeSpan CriticalLatencyThreshold { get; init; };
}
```
```csharp
public sealed record HealthMetrics
{
}
    public long RecordsProcessed { get; init; }
    public long Errors { get; init; }
    public double ErrorRate;;
    public TimeSpan Latency { get; init; }
    public double CpuUtilization { get; init; }
    public double MemoryUtilization { get; init; }
}
```
```csharp
public sealed record HealthCheck
{
}
    public required string CheckId { get; init; }
    public required string PipelineId { get; init; }
    public required HealthMetrics Metrics { get; init; }
    public PipelineHealthStatus Status { get; init; }
    public DateTime Timestamp { get; init; }
}
```
```csharp
public sealed record HealthAlert
{
}
    public required string AlertId { get; init; }
    public required string PipelineId { get; init; }
    public AlertSeverity Severity { get; init; }
    public required string Message { get; init; }
    public DateTime TriggeredAt { get; init; }
}
```
```csharp
public sealed record SlaDefinition
{
}
    public required string SlaId { get; init; }
    public required string Name { get; init; }
    public required SlaRequirements Requirements { get; init; }
    public SlaStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastChecked { get; set; }
    public bool? LastComplianceResult { get; set; }
}
```
```csharp
public sealed record SlaRequirements
{
}
    public TimeSpan? MaxDataAge { get; init; }
    public double? MinCompleteness { get; init; }
    public double? MinAvailability { get; init; }
    public TimeSpan? MaxLatency { get; init; }
}
```
```csharp
public sealed record SlaMetrics
{
}
    public TimeSpan DataAge { get; init; }
    public double Completeness { get; init; }
    public double Availability { get; init; }
    public TimeSpan ProcessingLatency { get; init; }
}
```
```csharp
public sealed record SlaComplianceResult
{
}
    public required string SlaId { get; init; }
    public DateTime CheckedAt { get; init; }
    public bool IsCompliant { get; set; }
    public required List<string> Violations { get; init; }
}
```
```csharp
public sealed record SlaViolation
{
}
    public required string ViolationId { get; init; }
    public required string SlaId { get; init; }
    public required List<string> Violations { get; init; }
    public required SlaMetrics Metrics { get; init; }
    public DateTime OccurredAt { get; init; }
}
```
```csharp
public sealed record SlaReport
{
}
    public required string SlaId { get; init; }
    public required string SlaName { get; init; }
    public DateTime FromDate { get; init; }
    public DateTime ToDate { get; init; }
    public int TotalViolations { get; init; }
    public double ComplianceRate { get; init; }
    public required List<SlaViolation> Violations { get; init; }
}
```
```csharp
public sealed record DataQualityProfile
{
}
    public required string ProfileId { get; init; }
    public required string DatasetName { get; init; }
    public required List<QualityRule> Rules { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastEvaluated { get; set; }
    public double? LastScore { get; set; }
}
```
```csharp
public sealed record QualityRule
{
}
    public required string RuleId { get; init; }
    public required string Name { get; init; }
    public QualityRuleType Type { get; init; }
    public string? ColumnName { get; init; }
    public bool AllowNull { get; init; };
    public double? MinValue { get; init; }
    public double? MaxValue { get; init; }
    public string? Pattern { get; init; }
}
```
```csharp
public sealed record QualityEvaluation
{
}
    public required string EvaluationId { get; init; }
    public required string ProfileId { get; init; }
    public int RecordsEvaluated { get; init; }
    public DateTime EvaluatedAt { get; init; }
    public double OverallScore { get; set; }
    public required List<RuleResult> RuleResults { get; init; }
}
```
```csharp
public sealed record RuleResult
{
}
    public required string RuleId { get; init; }
    public required string RuleName { get; init; }
    public double Score { get; init; }
    public int Violations { get; init; }
}
```
```csharp
public sealed record QualityScore
{
}
    public required string ProfileId { get; init; }
    public double Score { get; init; }
    public DateTime RecordedAt { get; init; }
}
```
```csharp
public sealed record QualityTrend
{
}
    public required string ProfileId { get; init; }
    public required List<QualityScore> Scores { get; init; }
    public TrendDirection TrendDirection { get; init; }
    public double TrendValue { get; init; }
}
```
```csharp
public sealed record LineageNode
{
}
    public required string NodeId { get; init; }
    public required string NodeName { get; init; }
    public LineageNodeType NodeType { get; init; }
    public required Dictionary<string, object> Metadata { get; init; }
    public DateTime RegisteredAt { get; init; }
}
```
```csharp
public sealed record LineageEdge
{
}
    public required string EdgeId { get; init; }
    public required string SourceNodeId { get; init; }
    public required string TargetNodeId { get; init; }
    public LineageEdgeType EdgeType { get; init; }
    public required Dictionary<string, object> Transformations { get; init; }
    public DateTime RecordedAt { get; init; }
}
```
```csharp
public sealed record LineageGraph
{
}
    public required string RootNodeId { get; init; }
    public LineageDirection Direction { get; init; }
    public required List<LineageNode> Nodes { get; init; }
    public required List<LineageEdge> Edges { get; init; }
}
```
```csharp
public sealed record ImpactAnalysis
{
}
    public required string SourceNodeId { get; init; }
    public required List<LineageNode> ImpactedNodes { get; init; }
    public int TotalImpactedCount { get; init; }
    public DateTime AnalyzedAt { get; init; }
}
```
```csharp
public sealed record AlertRule
{
}
    public required string RuleId { get; init; }
    public required string RuleName { get; init; }
    public required AlertCondition Condition { get; init; }
    public required AlertAction Action { get; init; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record AlertCondition
{
}
    public required string Metric { get; init; }
    public required string Operator { get; init; }
    public required double Threshold { get; init; }
    public AlertSeverity Severity { get; init; }
    public string? MessageTemplate { get; init; }
}
```
```csharp
public sealed record AlertAction
{
}
    public List<string>? NotifyChannels { get; init; }
    public TimeSpan? EscalateAfter { get; init; }
    public List<string>? EscalateTo { get; init; }
}
```
```csharp
public sealed record NotificationChannel
{
}
    public required string ChannelId { get; init; }
    public required string ChannelName { get; init; }
    public ChannelType ChannelType { get; init; }
    public required Dictionary<string, string> Config { get; init; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record Alert
{
}
    public required string AlertId { get; init; }
    public required string RuleId { get; init; }
    public required string RuleName { get; init; }
    public AlertSeverity Severity { get; init; }
    public required string Message { get; init; }
    public required Dictionary<string, object> Context { get; init; }
    public AlertStatus Status { get; set; }
    public DateTime TriggeredAt { get; init; }
    public DateTime? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
    public DateTime? ResolvedAt { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataIntegration/Strategies/SchemaEvolution/SchemaEvolutionStrategies.cs
```csharp
public sealed class ForwardCompatibleSchemaStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<SchemaVersion> RegisterSchemaAsync(string schemaId, string schemaName, IReadOnlyList<FieldDefinition> fields, CancellationToken ct = default);
    public Task<CompatibilityCheckResult> CheckCompatibilityAsync(string schemaId, IReadOnlyList<FieldDefinition> newFields, CancellationToken ct = default);
}
```
```csharp
public sealed class BackwardCompatibleSchemaStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<SchemaVersion> RegisterSchemaAsync(string schemaId, string schemaName, IReadOnlyList<FieldDefinition> fields, CancellationToken ct = default);
    public Task<CompatibilityCheckResult> CheckCompatibilityAsync(string schemaId, IReadOnlyList<FieldDefinition> newFields, CancellationToken ct = default);
}
```
```csharp
public sealed class FullCompatibleSchemaStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<SchemaVersion> RegisterSchemaAsync(string schemaId, string schemaName, IReadOnlyList<FieldDefinition> fields, CancellationToken ct = default);
    public Task<CompatibilityCheckResult> CheckCompatibilityAsync(string schemaId, IReadOnlyList<FieldDefinition> newFields, CancellationToken ct = default);
}
```
```csharp
public sealed class SchemaMigrationStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<SchemaMigration> CreateMigrationAsync(string migrationId, string schemaId, int fromVersion, int toVersion, IReadOnlyList<MigrationStep> upSteps, IReadOnlyList<MigrationStep>? downSteps = null, CancellationToken ct = default);
    public async Task<MigrationExecution> ExecuteMigrationAsync(string migrationId, MigrationDirection direction = MigrationDirection.Up, CancellationToken ct = default);
}
```
```csharp
public sealed class SchemaRegistryStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<SchemaSubject> CreateSubjectAsync(string subjectName, SchemaCompatibility compatibility = SchemaCompatibility.Backward, CancellationToken ct = default);
    public Task<RegisteredSchema> RegisterSchemaAsync(string subjectName, string schemaDefinition, SchemaType schemaType = SchemaType.Avro, CancellationToken ct = default);
    public Task<RegisteredSchema?> GetSchemaAsync(string subjectName, int? version = null, CancellationToken ct = default);
    public Task<List<int>> GetVersionsAsync(string subjectName, CancellationToken ct = default);
    public Task<bool> DeleteSchemaAsync(string subjectName, int version, CancellationToken ct = default);
}
```
```csharp
public sealed record SchemaVersion
{
}
    public required string SchemaId { get; init; }
    public required string SchemaName { get; init; }
    public int Version { get; init; }
    public required List<FieldDefinition> Fields { get; init; }
    public SchemaCompatibility Compatibility { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record FieldDefinition
{
}
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public bool IsNullable { get; init; };
    public object? DefaultValue { get; init; }
    public string? Documentation { get; init; }
}
```
```csharp
public sealed record CompatibilityCheckResult
{
}
    public bool IsCompatible { get; init; }
    public required string Message { get; init; }
    public List<string>? Issues { get; init; }
}
```
```csharp
public sealed record SchemaMigration
{
}
    public required string MigrationId { get; init; }
    public required string SchemaId { get; init; }
    public int FromVersion { get; init; }
    public int ToVersion { get; init; }
    public required List<MigrationStep> UpSteps { get; init; }
    public required List<MigrationStep> DownSteps { get; init; }
    public MigrationStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record MigrationStep
{
}
    public required string StepId { get; init; }
    public MigrationStepType Type { get; init; }
    public string? ColumnName { get; init; }
    public string? NewColumnName { get; init; }
    public string? DataType { get; init; }
    public string? CustomSql { get; init; }
}
```
```csharp
public sealed record MigrationExecution
{
}
    public required string ExecutionId { get; init; }
    public required string MigrationId { get; init; }
    public MigrationDirection Direction { get; init; }
    public MigrationExecutionStatus Status { get; set; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public List<StepResult>? StepResults { get; set; }
}
```
```csharp
public sealed record StepResult
{
}
    public required string StepId { get; init; }
    public bool Success { get; init; }
    public long DurationMs { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record SchemaSubject
{
}
    public required string SubjectName { get; init; }
    public SchemaCompatibility Compatibility { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record RegisteredSchema
{
}
    public required int SchemaId { get; init; }
    public required string SubjectName { get; init; }
    public int Version { get; init; }
    public required string SchemaDefinition { get; init; }
    public SchemaType SchemaType { get; init; }
    public required string Fingerprint { get; init; }
    public DateTime RegisteredAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataIntegration/Strategies/Transformation/DataTransformationStrategies.cs
```csharp
public sealed class TypeConversionStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<TypeMapping> RegisterMappingAsync(string mappingId, string sourceType, string targetType, ConversionRule rule, CancellationToken ct = default);
    public Task<ConversionResult> ConvertAsync(object? value, string sourceType, string targetType, CancellationToken ct = default);
}
```
```csharp
public sealed class AggregationStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<AggregationResult> AggregateAsync(IReadOnlyList<Dictionary<string, object>> records, IReadOnlyList<string> groupByColumns, IReadOnlyList<AggregationFunction> aggregations, CancellationToken ct = default);
}
```
```csharp
public sealed class DataCleansingStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<CleansingRule> RegisterRuleAsync(string ruleId, string targetColumn, CleansingOperation operation, Dictionary<string, object>? parameters = null, CancellationToken ct = default);
    public Task<CleansingResult> CleanseAsync(IReadOnlyList<Dictionary<string, object>> records, IReadOnlyList<string>? ruleIds = null, CancellationToken ct = default);
}
```
```csharp
public sealed class DataEnrichmentStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<EnrichmentSource> RegisterSourceAsync(string sourceId, EnrichmentSourceType sourceType, string connectionString, EnrichmentConfig? config = null, CancellationToken ct = default);
    public async Task<EnrichmentResult> EnrichAsync(IReadOnlyList<Dictionary<string, object>> records, string sourceId, string keyColumn, IReadOnlyList<string> enrichColumns, CancellationToken ct = default);
}
```
```csharp
public sealed class DataNormalizationStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<NormalizationRule> RegisterRuleAsync(string ruleId, string targetColumn, NormalizationType normType, Dictionary<string, object>? parameters = null, CancellationToken ct = default);
    public Task<NormalizationResult> NormalizeAsync(IReadOnlyList<Dictionary<string, object>> records, IReadOnlyList<string>? ruleIds = null, CancellationToken ct = default);
}
```
```csharp
public sealed class FlattenNestStrategy : DataIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override IntegrationCategory Category;;
    public override DataIntegrationCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<FlattenResult> FlattenAsync(IReadOnlyList<Dictionary<string, object>> records, FlattenConfig? config = null, CancellationToken ct = default);
    public Task<NestResult> NestAsync(IReadOnlyList<Dictionary<string, object>> records, NestConfig? config = null, CancellationToken ct = default);
}
```
```csharp
public sealed record TypeMapping
{
}
    public required string MappingId { get; init; }
    public required string SourceType { get; init; }
    public required string TargetType { get; init; }
    public ConversionRule Rule { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record ConversionResult
{
}
    public object? OriginalValue { get; init; }
    public object? ConvertedValue { get; set; }
    public required string SourceType { get; init; }
    public required string TargetType { get; init; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}
```
```csharp
public sealed record AggregationFunction
{
}
    public required string InputColumn { get; init; }
    public required string OutputColumn { get; init; }
    public required AggFunction Function { get; init; }
}
```
```csharp
public sealed record AggregationResult
{
}
    public int InputRecords { get; init; }
    public int OutputRecords { get; init; }
    public required List<Dictionary<string, object>> AggregatedData { get; init; }
    public AggregationStatus Status { get; init; }
}
```
```csharp
public sealed record CleansingRule
{
}
    public required string RuleId { get; init; }
    public required string TargetColumn { get; init; }
    public CleansingOperation Operation { get; init; }
    public required Dictionary<string, object> Parameters { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record CleansingResult
{
}
    public int InputRecords { get; init; }
    public int OutputRecords { get; init; }
    public int ModificationsApplied { get; init; }
    public required List<Dictionary<string, object>> CleansedData { get; init; }
    public CleansingStatus Status { get; init; }
}
```
```csharp
public sealed record EnrichmentSource
{
}
    public required string SourceId { get; init; }
    public EnrichmentSourceType SourceType { get; init; }
    public required string ConnectionString { get; init; }
    public required EnrichmentConfig Config { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record EnrichmentConfig
{
}
    public bool EnableCache { get; init; };
    public TimeSpan CacheTtl { get; init; };
    public int MaxConcurrentLookups { get; init; };
}
```
```csharp
public sealed record EnrichmentResult
{
}
    public int InputRecords { get; init; }
    public int OutputRecords { get; init; }
    public int EnrichmentsApplied { get; init; }
    public required List<Dictionary<string, object>> EnrichedData { get; init; }
    public EnrichmentStatus Status { get; init; }
}
```
```csharp
public sealed record NormalizationRule
{
}
    public required string RuleId { get; init; }
    public required string TargetColumn { get; init; }
    public NormalizationType NormalizationType { get; init; }
    public required Dictionary<string, object> Parameters { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record NormalizationResult
{
}
    public int InputRecords { get; init; }
    public int OutputRecords { get; init; }
    public int ModificationsApplied { get; init; }
    public required List<Dictionary<string, object>> NormalizedData { get; init; }
    public NormalizationStatus Status { get; init; }
}
```
```csharp
public sealed record FlattenConfig
{
}
    public string Separator { get; init; };
    public int MaxDepth { get; init; };
}
```
```csharp
public sealed record FlattenResult
{
}
    public int InputRecords { get; init; }
    public int OutputRecords { get; init; }
    public required List<Dictionary<string, object>> FlattenedData { get; init; }
    public FlattenStatus Status { get; init; }
}
```
```csharp
public sealed record NestConfig
{
}
    public string Separator { get; init; };
}
```
```csharp
public sealed record NestResult
{
}
    public int InputRecords { get; init; }
    public int OutputRecords { get; init; }
    public required List<Dictionary<string, object>> NestedData { get; init; }
    public NestStatus Status { get; init; }
}
```
