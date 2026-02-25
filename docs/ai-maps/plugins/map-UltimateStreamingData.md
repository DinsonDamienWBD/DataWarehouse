# Plugin: UltimateStreamingData
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateStreamingData

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/UltimateStreamingDataPlugin.cs
```csharp
public sealed class UltimateStreamingDataPlugin : StreamingPluginBase, IDisposable
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override PluginCategory Category;;
    public string SemanticDescription;;
    public string[] SemanticTags;;
    public StreamingStrategyRegistry Registry;;
    public bool AuditEnabled { get => _auditEnabled; set => _auditEnabled = value; }
    public bool AutoOptimizationEnabled { get => _autoOptimizationEnabled; set => _autoOptimizationEnabled = value; }
    public UltimateStreamingDataPlugin();
    public async Task InitializeStrategiesAsync(CancellationToken ct = default);
    public IStreamingDataStrategy? GetStrategy(string strategyId);;
    public IEnumerable<IStreamingDataStrategy> GetStrategiesByCategory(StreamingCategory category);;
    public async Task<StreamPipeline> CreatePipelineAsync(StreamPipelineConfig config, CancellationToken ct = default);
    public async IAsyncEnumerable<ProcessedEvent> ProcessEventsAsync(StreamPipeline pipeline, IAsyncEnumerable<StreamEvent> events, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default);
    public StreamingStatistics GetStatistics();;
    public override async Task PublishAsync(string topic, Stream data, CancellationToken ct = default);
    public override async IAsyncEnumerable<Dictionary<string, object>> SubscribeAsync(string topic, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default);
    protected override void Dispose(bool disposing);
}
```
```csharp
public interface IStreamingDataStrategy
{
}
    string StrategyId { get; }
    string DisplayName { get; }
    StreamingCategory Category { get; }
    StreamingDataCapabilities Capabilities { get; }
    string SemanticDescription { get; }
    string[] Tags { get; }
}
```
```csharp
public sealed record StreamingDataCapabilities
{
}
    public bool SupportsExactlyOnce { get; init; }
    public bool SupportsWindowing { get; init; }
    public bool SupportsStateManagement { get; init; }
    public bool SupportsCheckpointing { get; init; }
    public bool SupportsBackpressure { get; init; }
    public bool SupportsPartitioning { get; init; }
    public bool SupportsAutoScaling { get; init; }
    public bool SupportsDistributed { get; init; }
    public long MaxThroughputEventsPerSec { get; init; }
    public double TypicalLatencyMs { get; init; }
}
```
```csharp
public sealed record StreamPipelineConfig
{
}
    public string? PipelineId { get; init; }
    public required string Name { get; init; }
    public required StreamSource[] Sources { get; init; }
    public required StreamSink[] Sinks { get; init; }
    public StreamProcessor[] Processors { get; init; };
    public WindowConfig? WindowConfig { get; init; }
    public StateConfig? StateConfig { get; init; }
    public FaultToleranceConfig? FaultToleranceConfig { get; init; }
    public ScalabilityConfig? ScalabilityConfig { get; init; }
}
```
```csharp
public sealed record StreamPipeline
{
}
    public required string PipelineId { get; init; }
    public required string Name { get; init; }
    public required StreamSource[] Sources { get; init; }
    public required StreamSink[] Sinks { get; init; }
    public StreamProcessor[] Processors { get; init; };
    public WindowConfig? WindowConfig { get; init; }
    public StateConfig? StateConfig { get; init; }
    public FaultToleranceConfig? FaultToleranceConfig { get; init; }
    public ScalabilityConfig? ScalabilityConfig { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public PipelineStatus Status { get; init; }
}
```
```csharp
public sealed record StreamSource
{
}
    public required string SourceId { get; init; }
    public required string SourceType { get; init; }
    public required string ConnectionString { get; init; }
    public string? Topic { get; init; }
    public string? ConsumerGroup { get; init; }
    public Dictionary<string, object>? Properties { get; init; }
}
```
```csharp
public sealed record StreamSink
{
}
    public required string SinkId { get; init; }
    public required string SinkType { get; init; }
    public required string ConnectionString { get; init; }
    public string? Topic { get; init; }
    public Dictionary<string, object>? Properties { get; init; }
}
```
```csharp
public sealed record StreamProcessor
{
}
    public required string ProcessorId { get; init; }
    public required string ProcessorType { get; init; }
    public Dictionary<string, object>? Config { get; init; }
}
```
```csharp
public sealed record WindowConfig
{
}
    public WindowType Type { get; init; }
    public TimeSpan Size { get; init; }
    public TimeSpan? Slide { get; init; }
    public TimeSpan? Gap { get; init; }
    public TimeSpan? AllowedLateness { get; init; }
}
```
```csharp
public sealed record StateConfig
{
}
    public StateBackendType BackendType { get; init; }
    public string? StoragePath { get; init; }
    public TimeSpan? StateTtl { get; init; }
    public bool EnableChangeLog { get; init; }
    public int? MaxStateSize { get; init; }
}
```
```csharp
public sealed record FaultToleranceConfig
{
}
    public CheckpointMode CheckpointMode { get; init; }
    public TimeSpan CheckpointInterval { get; init; }
    public int MinPauseBetweenCheckpoints { get; init; }
    public TimeSpan CheckpointTimeout { get; init; }
    public int MaxConcurrentCheckpoints { get; init; }
    public bool ExternalizedCheckpoints { get; init; }
    public string? CheckpointStorage { get; init; }
    public RestartStrategy RestartStrategy { get; init; }
    public int MaxRestartAttempts { get; init; }
    public TimeSpan RestartDelay { get; init; }
}
```
```csharp
public sealed record ScalabilityConfig
{
}
    public int Parallelism { get; init; };
    public int MaxParallelism { get; init; };
    public bool AutoScale { get; init; }
    public int MinInstances { get; init; };
    public int MaxInstances { get; init; };
    public double ScaleUpThreshold { get; init; };
    public double ScaleDownThreshold { get; init; };
    public TimeSpan ScaleCooldown { get; init; };
    public BackpressureStrategy BackpressureStrategy { get; init; }
    public int BufferSize { get; init; };
}
```
```csharp
public sealed record StreamEvent
{
}
    public required string EventId { get; init; }
    public required byte[] Data { get; init; }
    public string? Key { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public DateTimeOffset? EventTime { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    public int? Partition { get; init; }
    public long? Offset { get; init; }
}
```
```csharp
public sealed record ProcessedEvent
{
}
    public required string EventId { get; init; }
    public required byte[] Data { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public DateTimeOffset ProcessedAt { get; init; }
    public string? PipelineId { get; init; }
    public TimeSpan? ProcessingLatency { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}
```
```csharp
public sealed record StreamingPolicy
{
}
    public required string PolicyId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public StreamingCategory TargetCategory { get; init; }
    public Dictionary<string, object>? Settings { get; init; }
    public bool IsEnabled { get; init; };
    public DateTimeOffset CreatedAt { get; init; }
}
```
```csharp
public sealed record StreamingStatistics
{
}
    public long TotalOperations { get; init; }
    public long TotalEventsProcessed { get; init; }
    public long TotalFailures { get; init; }
    public int RegisteredStrategies { get; init; }
    public int ActivePolicies { get; init; }
    public Dictionary<string, long> UsageByStrategy { get; init; };
}
```
```csharp
public sealed class StreamingStrategyRegistry
{
}
    public int Count;;
    public void Register(IStreamingDataStrategy strategy);
    public IStreamingDataStrategy? GetStrategy(string strategyId);;
    public IEnumerable<IStreamingDataStrategy> GetByCategory(StreamingCategory category);;
    public IEnumerable<IStreamingDataStrategy> GetAllStrategies();;
}
```
```csharp
public abstract class StreamingDataStrategyBase : StrategyBase, IStreamingDataStrategy, IInitializable
{
}
    public abstract override string StrategyId { get; }
    public abstract string DisplayName { get; }
    public override string Name;;
    public abstract StreamingCategory Category { get; }
    public abstract StreamingDataCapabilities Capabilities { get; }
    public abstract string SemanticDescription { get; }
    public abstract string[] Tags { get; }
    protected void RecordRead(long bytes, double latencyMs, bool hit = true, bool miss = false);
    protected void RecordWrite(long bytes, double latencyMs);
    protected void RecordFailure();
    protected void RecordOperation(string operationName);
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
    public long CacheHits { get; init; }
    public long CacheMisses { get; init; }
}
```
```csharp
public interface IInitializable
{
}
    Task InitializeAsync(CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Scaling/StreamingBackpressureHandler.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-02: Streaming backpressure handler with 5 strategies")]
public sealed class StreamingBackpressureHandler : IBackpressureAware, IDisposable
{
}
    public StreamingBackpressureHandler(long maxCapacity = 10_000, double warningThreshold = DefaultWarningThreshold, double criticalThreshold = DefaultCriticalThreshold, double sheddingThreshold = DefaultSheddingThreshold);
    public SdkBackpressureStrategy Strategy { get => _strategy; set => _strategy = value; }
    public SdkBackpressureState CurrentState;;
    public event Action<BackpressureStateChangedEventArgs>? OnBackpressureChanged;
    public Task ApplyBackpressureAsync(BackpressureContext context, CancellationToken ct = default);
    public void RecordEventQueued();
    public void RecordEventDequeued();
    public int CurrentQueueDepth;;
    public int ApplyDropOldest(int excessCount);
    public async Task ApplyBlockProducerAsync(CancellationToken ct = default);
    public bool ApplyShedLoad();
    public DegradationDirective ApplyDegradeQuality();
    public async Task<SdkBackpressureStrategy> ApplyAdaptiveAsync(CancellationToken ct = default);
    public Task ReconfigureThresholdsAsync(long maxCapacity = 0, double warningThreshold = -1, double criticalThreshold = -1, double sheddingThreshold = -1, CancellationToken ct = default);
    public void Dispose();
}
```
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-02: Degradation directive for streaming backpressure")]
public sealed record DegradationDirective
{
}
    public bool SkipEnrichment { get; init; }
    public bool BatchAggressively { get; init; }
    public bool ReduceAckGuarantees { get; init; }
    public double UtilizationRatio { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Scaling/StreamingScalingManager.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-02: Streaming scaling manager with real dispatch")]
public sealed class StreamingScalingManager : IScalableSubsystem, IDisposable
{
}
    public StreamingScalingManager(StreamingStrategyRegistry strategyRegistry, ScalabilityConfig? scalabilityConfig, StreamingBackpressureHandler backpressureHandler, IMessageBus? messageBus = null, IPersistentBackingStore? backingStore = null);
    public IReadOnlyDictionary<string, object> GetScalingMetrics();
    public async Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default);
    public ScalingLimits CurrentLimits;;
    public SdkBackpressureState CurrentBackpressureState;;
    public async Task PublishAsync(StreamEvent @event, CancellationToken ct = default);
    public async IAsyncEnumerable<StreamEvent> SubscribeAsync(string consumerGroupId, [EnumeratorCancellation] CancellationToken ct = default);
    public async Task CommitCheckpointAsync(string consumerGroupId, CancellationToken ct = default);
    public int PartitionCount;;
    public void Dispose();
}
```
```csharp
private sealed class ConsumerGroup
{
}
    public ConsumerGroup(string groupId);
    public string GroupId;;
    public int ConsumerCount;;
    public void IncrementConsumerCount();;
    public void UpdateOffset(int partition, long offset);
    public IReadOnlyDictionary<int, long> GetAllOffsets();
}
```
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-02: Streaming checkpoint for exactly-once semantics")]
public sealed record StreamCheckpoint
{
}
    public required string ConsumerGroupId { get; init; }
    public required int Partition { get; init; }
    public required long Offset { get; init; }
    public required DateTimeOffset CommittedAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Features/WatermarkManagement.cs
```csharp
public sealed record WatermarkConfig
{
}
    public WatermarkStrategy Strategy { get; init; };
    public TimeSpan MaxOutOfOrderness { get; init; };
    public TimeSpan AllowedLateness { get; init; };
    public TimeSpan PeriodicInterval { get; init; };
    public WindowTriggerStrategy TriggerStrategy { get; init; };
    public int CountTriggerThreshold { get; init; };
    public TimeSpan ProcessingTimeDelay { get; init; };
    public bool EmitEarlyResults { get; init; }
    public TimeSpan EarlyFiringInterval { get; init; };
}
```
```csharp
public sealed record WatermarkState
{
}
    public required string PartitionId { get; init; }
    public DateTimeOffset CurrentWatermark { get; init; }
    public DateTimeOffset MaxObservedEventTime { get; init; }
    public long EventsProcessed { get; init; }
    public long LateEventsAccepted { get; init; }
    public long EventsDiscarded { get; init; }
    public DateTimeOffset LastUpdated { get; init; };
}
```
```csharp
public sealed record WatermarkEventResult
{
}
    public required StreamEvent Event { get; init; }
    public required EventTimeliness Timeliness { get; init; }
    public DateTimeOffset WatermarkAtClassification { get; init; }
    public bool WatermarkAdvanced { get; init; }
    public IReadOnlyList<string>? TriggeredWindows { get; init; }
}
```
```csharp
internal sealed class WatermarkManagement : IDisposable
{
}
    public WatermarkManagement(WatermarkConfig? config = null, IMessageBus? messageBus = null);
    public long TotalEventsProcessed;;
    public long TotalLateEvents;;
    public long TotalDiscardedEvents;;
    public long WatermarkAdvances;;
    public async Task<WatermarkEventResult> ProcessEventAsync(string partitionId, StreamEvent evt, CancellationToken ct = default);
    public WatermarkState? GetPartitionState(string partitionId);
    public IReadOnlyCollection<WatermarkState> GetAllPartitionStates();
    public void RegisterWindow(string partitionId, string windowId, DateTimeOffset windowEnd);
    public void Dispose();
}
```
```csharp
private sealed class PartitionWatermarkState
{
}
    public DateTimeOffset CurrentWatermark;
    public DateTimeOffset MaxObservedEventTime;
    public long EventsProcessed;
    public long LateEventsAccepted;
    public long EventsDiscarded;
    public DateTimeOffset LastUpdated = DateTimeOffset.UtcNow;
    public readonly BoundedDictionary<string, DateTimeOffset> PendingWindows = new BoundedDictionary<string, DateTimeOffset>(1000);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Features/ComplexEventProcessing.cs
```csharp
public sealed record CepPattern
{
}
    public required string PatternId { get; init; }
    public required string Name { get; init; }
    public required CepPatternType PatternType { get; init; }
    public required string[] EventTypes { get; init; }
    public required TimeSpan Window { get; init; }
    public Dictionary<string, Func<StreamEvent, bool>>? Filters { get; init; }
    public Func<StreamEvent, string>? JoinKeyExtractor { get; init; }
    public CepAggregateFunction? AggregateFunction { get; init; }
    public Func<StreamEvent, double>? ValueExtractor { get; init; }
    public double? AggregateThreshold { get; init; }
}
```
```csharp
public sealed record CepMatchResult
{
}
    public required string PatternId { get; init; }
    public required IReadOnlyList<StreamEvent> MatchedEvents { get; init; }
    public DateTimeOffset DetectedAt { get; init; };
    public TimeSpan MatchDuration { get; init; }
    public double? AggregateValue { get; init; }
    public string? JoinKey { get; init; }
}
```
```csharp
internal sealed class ComplexEventProcessing : IDisposable
{
}
    public ComplexEventProcessing(IMessageBus? messageBus = null, TimeSpan? cleanupInterval = null);
    public long TotalEventsProcessed;;
    public long TotalMatchesDetected;;
    public void RegisterPattern(CepPattern pattern);
    public bool UnregisterPattern(string patternId);
    public async Task<IReadOnlyList<CepMatchResult>> ProcessEventAsync(StreamEvent evt, CancellationToken ct = default);
    public async Task<IReadOnlyList<CepMatchResult>> ProcessBatchAsync(IEnumerable<StreamEvent> events, CancellationToken ct = default);
    public IReadOnlyCollection<CepPattern> GetPatterns();;
    public void Dispose();
}
```
```csharp
private sealed class PatternState
{
}
    public void AddEvent(string eventType, StreamEvent evt, TimeSpan window);
    public IReadOnlyList<StreamEvent> GetEventsForType(string eventType);
    public void ClearMatched();
    public void PurgeExpired(TimeSpan window);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Features/BackpressureHandling.cs
```csharp
public sealed record BackpressureConfig
{
}
    public BackpressureMode Mode { get; init; };
    public int BufferCapacity { get; init; };
    public double HighWatermark { get; init; };
    public double LowWatermark { get; init; };
    public double MinProducerRatePerSec { get; init; };
    public double MaxProducerRatePerSec { get; init; };
    public double SamplingRatio { get; init; };
    public TimeSpan RateAdjustmentInterval { get; init; };
    public TimeSpan MetricsInterval { get; init; };
}
```
```csharp
public sealed record BackpressureStatus
{
}
    public required string ChannelId { get; init; }
    public bool IsBackpressureActive { get; init; }
    public double BufferUtilization { get; init; }
    public int BufferedEvents { get; init; }
    public int BufferCapacity { get; init; }
    public double CurrentProducerRate { get; init; }
    public double CurrentConsumerRate { get; init; }
    public long TotalAccepted { get; init; }
    public long TotalDropped { get; init; }
    public long TotalThrottled { get; init; }
    public BackpressureMode ActiveMode { get; init; }
}
```
```csharp
internal sealed class BackpressureHandling : IDisposable
{
}
    public BackpressureHandling(BackpressureConfig? config = null, IMessageBus? messageBus = null);
    public long GlobalAccepted;;
    public long GlobalDropped;;
    public long GlobalThrottled;;
    public string CreateChannel(string channelId, BackpressureConfig? config = null);
    public async Task<bool> SubmitAsync(string channelId, StreamEvent evt, CancellationToken ct = default);
    public async IAsyncEnumerable<StreamEvent> ConsumeAsync(string channelId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default);
    public BackpressureStatus? GetStatus(string channelId);
    public IReadOnlyCollection<BackpressureStatus> GetAllStatuses();
    public bool CloseChannel(string channelId);
    public void Dispose();
}
```
```csharp
private sealed class BackpressureChannel
{
}
    public readonly Channel<StreamEvent> Channel;
    public readonly BackpressureConfig Config;
    public volatile bool IsBackpressureActive;
    public long Accepted;
    public long Dropped;
    public long Throttled;
    public long ProducerEventCounter;
    public long ConsumerEventCounter;
    public DateTimeOffset LastRateCalculation = DateTimeOffset.UtcNow;
    public double ProducerRate { get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _producerRateBits)); set => Interlocked.Exchange(ref _producerRateBits, BitConverter.DoubleToInt64Bits(value)); }
    public double ConsumerRate { get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _consumerRateBits)); set => Interlocked.Exchange(ref _consumerRateBits, BitConverter.DoubleToInt64Bits(value)); }
    public BackpressureChannel(Channel<StreamEvent> channel, BackpressureConfig config);
    public void RecordProducerEvent();;
    public void RecordConsumerEvent();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Features/StatefulStreamProcessing.cs
```csharp
public sealed record StatefulProcessingConfig
{
}
    public StreamStateBackend Backend { get; init; };
    public TimeSpan CheckpointInterval { get; init; };
    public TimeSpan? StateTtl { get; init; }
    public int MaxStateEntries { get; init; };
    public string? CheckpointPath { get; init; }
    public bool IncrementalCheckpoints { get; init; };
}
```
```csharp
public sealed record KeyedStateEntry
{
}
    public required string Key { get; init; }
    public required byte[] Value { get; init; }
    public DateTimeOffset LastUpdated { get; init; };
    public string Checksum { get; init; };
}
```
```csharp
public sealed record StateCheckpoint
{
}
    public required string CheckpointId { get; init; }
    public int EntryCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; };
    public int ChangedEntries { get; init; }
    public bool IsIncremental { get; init; }
}
```
```csharp
public sealed record WindowedAggregation<TResult>
{
}
    public required string Key { get; init; }
    public required DateTimeOffset WindowStart { get; init; }
    public required DateTimeOffset WindowEnd { get; init; }
    public required TResult Result { get; init; }
    public int EventCount { get; init; }
}
```
```csharp
internal sealed class StatefulStreamProcessing : IDisposable
{
}
    public StatefulStreamProcessing(string processorNamespace, StatefulProcessingConfig? config = null, IMessageBus? messageBus = null);
    public long TotalReads;;
    public long TotalWrites;;
    public long CheckpointCount;;
    public int StateSize;;
    public Task<byte[]?> GetStateAsync(string key, CancellationToken ct = default);
    public Task PutStateAsync(string key, byte[] value, CancellationToken ct = default);
    public Task<bool> RemoveStateAsync(string key, CancellationToken ct = default);
    public async Task<IReadOnlyList<WindowedAggregation<TResult>>> ProcessTumblingWindowAsync<TResult>(IAsyncEnumerable<StreamEvent> events, Func<StreamEvent, string> keyExtractor, TimeSpan windowSize, Func<IReadOnlyList<StreamEvent>, TResult> aggregator, CancellationToken ct = default);
    public async Task<IReadOnlyList<WindowedAggregation<TResult>>> ProcessSlidingWindowAsync<TResult>(IAsyncEnumerable<StreamEvent> events, Func<StreamEvent, string> keyExtractor, TimeSpan windowSize, TimeSpan slideInterval, Func<IReadOnlyList<StreamEvent>, TResult> aggregator, CancellationToken ct = default);
    public async Task<StateCheckpoint> TriggerCheckpointAsync(CancellationToken ct = default);
    public async Task<int> RestoreFromCheckpointAsync(CancellationToken ct = default);
    public void Dispose();
}
```
```csharp
private sealed record StateEntry(byte[] Value, DateTimeOffset LastUpdated, string Checksum)
{
}
    public bool IsExpired(TimeSpan? ttl);;
}
```
```csharp
private sealed record CheckpointEntry
{
}
    public string ValueBase64 { get; init; };
    public DateTimeOffset Timestamp { get; init; }
    public string Checksum { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/AiDrivenStrategies/IntelligentRoutingStream.cs
```csharp
public sealed record RoutingDecision
{
}
    public required int TargetPartition { get; init; }
    public required RoutingStrategy StrategyUsed { get; init; }
    public string AnalysisMethod { get; init; };
    public double Confidence { get; init; }
    public string Reason { get; init; };
    public double EstimatedPartitionLoad { get; init; }
    public DateTimeOffset DecidedAt { get; init; };
}
```
```csharp
public sealed record PartitionLoadInfo
{
}
    public required int PartitionId { get; init; }
    public long EventCount { get; init; }
    public double Throughput { get; init; }
    public double LatencyMs { get; init; }
    public long ConsumerLag { get; init; }
    public double Utilization { get; init; }
    public bool IsHealthy { get; init; };
    public DateTimeOffset LastUpdated { get; init; };
}
```
```csharp
public sealed record IntelligentRoutingConfig
{
}
    public int PartitionCount { get; init; };
    public double MaxImbalanceRatio { get; init; };
    public TimeSpan IntelligenceTimeout { get; init; };
    public TimeSpan LoadRefreshInterval { get; init; };
    public double UnhealthyLatencyThresholdMs { get; init; };
    public int AffinityHashSeed { get; init; };
}
```
```csharp
internal sealed class IntelligentRoutingStream : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public IntelligentRoutingStream(IMessageBus? messageBus = null, IntelligentRoutingConfig? config = null);
    public IntelligentRoutingStream() : this(null, null);
    public long MlRoutings;;
    public long HashFallbacks;;
    public async Task<RoutingDecision> RouteEventAsync(StreamEvent evt, CancellationToken ct = default);
    public RoutingDecision RouteWithStrategy(StreamEvent evt, RoutingStrategy strategy);
    public void UpdatePartitionLoadInfo(PartitionLoadInfo info);
    public IReadOnlyCollection<PartitionLoadInfo> GetPartitionLoads();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/AiDrivenStrategies/PredictiveScalingStream.cs
```csharp
public sealed record ScalingRecommendation
{
}
    public required ScalingDirection Direction { get; init; }
    public required ScalableResource Resource { get; init; }
    public int RecommendedTarget { get; init; }
    public int CurrentValue { get; init; }
    public double Confidence { get; init; }
    public string AnalysisMethod { get; init; };
    public double PredictedWorkload { get; init; }
    public double CurrentWorkload { get; init; }
    public string Reason { get; init; };
    public DateTimeOffset GeneratedAt { get; init; };
}
```
```csharp
public sealed record PredictiveScalingConfig
{
}
    public double ScaleUpThreshold { get; init; };
    public double ScaleDownThreshold { get; init; };
    public int MinResources { get; init; };
    public int MaxResources { get; init; };
    public TimeSpan ScalingCooldown { get; init; };
    public TimeSpan ForecastHorizon { get; init; };
    public int ObservationWindowSize { get; init; };
    public TimeSpan IntelligenceTimeout { get; init; };
}
```
```csharp
public sealed record StreamWorkloadMetrics
{
}
    public required string StreamId { get; init; }
    public double Throughput { get; init; }
    public double LatencyMs { get; init; }
    public double Utilization { get; init; }
    public long ConsumerLag { get; init; }
    public int ActiveResources { get; init; }
    public DateTimeOffset Timestamp { get; init; };
}
```
```csharp
internal sealed class PredictiveScalingStream : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public PredictiveScalingStream(IMessageBus? messageBus = null, PredictiveScalingConfig? config = null);
    public PredictiveScalingStream() : this(null, null);
    public long MlPredictions;;
    public long RuleBasedFallbacks;;
    public async Task<ScalingRecommendation> EvaluateAndRecommendAsync(StreamWorkloadMetrics metrics, ScalableResource resource, CancellationToken ct = default);
    public void RecordScalingAction(string streamId);
}
```
```csharp
private sealed class MetricsHistory
{
}
    public MetricsHistory(int maxSize);
    public int Count
{
    get
    {
        lock (_lock)
        {
            return _history.Count;
        }
    }
}
    public double AverageLatency
{
    get
    {
        lock (_lock)
        {
            return _history.Count > 0 ? _history.Average(m => m.LatencyMs) : 0;
        }
    }
}
    public void Add(StreamWorkloadMetrics metrics);
    public double GetThroughputTrend();
    public double GetUtilizationTrend();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/AiDrivenStrategies/AnomalyDetectionStream.cs
```csharp
public sealed record StreamAnomalyResult
{
}
    public bool IsAnomaly { get; init; }
    public double AnomalyScore { get; init; }
    public StreamAnomalyType? AnomalyType { get; init; }
    public StreamAnomalySeverity Severity { get; init; }
    public string AnalysisMethod { get; init; };
    public double Confidence { get; init; }
    public required StreamEvent Event { get; init; }
    public double? ZScore { get; init; }
    public double? BaselineMean { get; init; }
    public double? BaselineStdDev { get; init; }
    public DateTimeOffset AnalyzedAt { get; init; };
}
```
```csharp
public sealed record AnomalyDetectionConfig
{
}
    public double ZScoreThreshold { get; init; };
    public int MinObservations { get; init; };
    public int StatisticalWindowSize { get; init; };
    public TimeSpan IntelligenceTimeout { get; init; };
    public Func<StreamEvent, double>? ValueExtractor { get; init; }
}
```
```csharp
internal sealed class AnomalyDetectionStream : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AnomalyDetectionStream(IMessageBus? messageBus = null, AnomalyDetectionConfig? config = null);
    public AnomalyDetectionStream() : this(null, null);
    public long MlAnalyses;;
    public long StatisticalFallbacks;;
    public async Task<StreamAnomalyResult> AnalyzeAsync(StreamEvent evt, string? partitionKey = null, CancellationToken ct = default);
}
```
```csharp
private sealed class RunningStatistics
{
}
    public RunningStatistics(int maxSize);
    public int Count
{
    get
    {
        lock (_lock)
        {
            return _window.Count;
        }
    }
}
    public double Mean
{
    get
    {
        lock (_lock)
        {
            return _window.Count > 0 ? _sum / _window.Count : 0;
        }
    }
}
    public double StdDev
{
    get
    {
        lock (_lock)
        {
            if (_window.Count < 2)
                return 0;
            var mean = _sum / _window.Count;
            var variance = (_sumSquares / _window.Count) - (mean * mean);
            return variance > 0 ? Math.Sqrt(variance) : 0;
        }
    }
}
    public double Min
{
    get
    {
        lock (_lock)
        {
            return _min == double.MaxValue ? 0 : _min;
        }
    }
}
    public double Max
{
    get
    {
        lock (_lock)
        {
            return _max == double.MinValue ? 0 : _max;
        }
    }
}
    public void Add(double value);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/StreamingInfrastructure.cs
```csharp
public sealed class RedisStreamsConsumerGroupManager
{
}
    public void CreateGroup(string streamKey, string groupName, string startId = "$");
    public IReadOnlyList<RedisStreamEntry> ReadGroup(string streamKey, string groupName, string consumerName, int count = 10, string id = ">");
    public int Acknowledge(string streamKey, string groupName, params string[] messageIds);
    public PendingInfo GetPending(string streamKey, string groupName, int count = 100, string? consumerFilter = null);
    public long Trim(string streamKey, long? maxLen = null, string? minId = null);
    public string AddEntry(string streamKey, Dictionary<string, string> fields);
}
```
```csharp
public sealed class RedisStreamState
{
}
    public required string StreamKey { get; init; }
    public List<RedisStreamEntry> Entries { get; };
    public long SequenceCounter { get; set; }
}
```
```csharp
public sealed record RedisStreamEntry
{
}
    public required string Id { get; init; }
    public required Dictionary<string, string> Fields { get; init; }
    public DateTime Timestamp { get; init; }
}
```
```csharp
public sealed class RedisConsumerGroupState
{
}
    public required string GroupName { get; init; }
    public string LastDeliveredId { get; set; };
    public long TotalDelivered { get; set; }
    public long TotalAcknowledged { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed class PendingEntry
{
}
    public required string Id { get; init; }
    public required string ConsumerName { get; init; }
    public DateTime DeliveredAt { get; init; }
    public int DeliveryCount { get; set; }
}
```
```csharp
public sealed record PendingInfo
{
}
    public required string GroupName { get; init; }
    public long TotalPending { get; init; }
    public string? MinId { get; init; }
    public string? MaxId { get; init; }
    public List<PendingEntry> Entries { get; init; };
}
```
```csharp
public sealed class KinesisCheckpointManager
{
}
    public bool TakeLease(string streamName, string shardId, string workerId);
    public void Checkpoint(string streamName, string shardId, string sequenceNumber, long subSequenceNumber = 0);
    public bool RenewLease(string streamName, string shardId, string workerId);
    public string? GetCheckpoint(string streamName, string shardId);
    public IReadOnlyList<KinesisLeaseEntry> GetAllLeases();;
}
```
```csharp
public sealed class KinesisLeaseEntry
{
}
    public required string StreamName { get; init; }
    public required string ShardId { get; init; }
    public string WorkerId { get; set; };
    public string? Checkpoint { get; set; }
    public long SubSequenceNumber { get; set; }
    public long LeaseCounter { get; set; }
    public DateTime TakenAt { get; set; }
    public DateTime LastRenewed { get; set; };
    public DateTime? CheckpointedAt { get; set; }
}
```
```csharp
public sealed class KinesisShardIteratorManager
{
}
    public string GetShardIterator(string streamName, string shardId, KinesisShardIteratorType type, string? startingSequenceNumber = null, DateTime? timestamp = null);
    public ShardIteratorState? GetIterator(string iteratorId);
}
```
```csharp
public sealed record ShardIteratorState
{
}
    public required string IteratorId { get; init; }
    public required string StreamName { get; init; }
    public required string ShardId { get; init; }
    public KinesisShardIteratorType Type { get; init; }
    public string? StartingSequenceNumber { get; init; }
    public DateTime? Timestamp { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
}
```
```csharp
public sealed class KinesisReshardingDetector
{
}
    public ReshardingEvent RecordSplit(string streamName, string parentShardId, string childShardId1, string childShardId2);
    public ReshardingEvent RecordMerge(string streamName, string shardId1, string shardId2, string mergedShardId);
    public IReadOnlyList<string> GetHistory(string streamName);;
}
```
```csharp
public sealed record ReshardingEvent
{
}
    public required string StreamName { get; init; }
    public ReshardingEventType EventType { get; init; }
    public required string ParentShardId { get; init; }
    public List<string> ChildShardIds { get; init; };
    public DateTime DetectedAt { get; init; }
}
```
```csharp
public sealed class EventHubsPartitionBalancer
{
}
    public bool ClaimOwnership(string eventHubName, string consumerGroup, string partitionId, string ownerId, string? existingETag = null);
    public Dictionary<string, List<string>> Balance(string eventHubName, string consumerGroup, IReadOnlyList<string> partitionIds, IReadOnlyList<string> processorIds);
    public IReadOnlyList<EventHubsOwnership> GetOwnership(string eventHubName, string consumerGroup);
}
```
```csharp
public sealed record EventHubsOwnership
{
}
    public required string EventHubName { get; init; }
    public required string ConsumerGroup { get; init; }
    public required string PartitionId { get; init; }
    public required string OwnerId { get; init; }
    public string ETag { get; init; };
    public DateTime LastModified { get; init; }
}
```
```csharp
public sealed class EventHubsCheckpointStore
{
}
    public bool UpdateCheckpoint(string eventHubName, string consumerGroup, string partitionId, long sequenceNumber, string offset, string? expectedETag = null);
    public EventHubsCheckpointEntry? GetCheckpoint(string eventHubName, string consumerGroup, string partitionId);
    public IReadOnlyList<EventHubsCheckpointEntry> ListCheckpoints(string eventHubName, string consumerGroup);
}
```
```csharp
public sealed record EventHubsCheckpointEntry
{
}
    public required string EventHubName { get; init; }
    public required string ConsumerGroup { get; init; }
    public required string PartitionId { get; init; }
    public long SequenceNumber { get; init; }
    public required string Offset { get; init; }
    public string ETag { get; init; };
    public DateTime UpdatedAt { get; init; }
}
```
```csharp
public sealed class MqttQoS2ProtocolHandler
{
}
    public int InitiatePublish(byte[] payload, string topic);
    public bool HandlePubRec(int packetId);
    public bool HandlePubRel(int packetId);
    public bool HandlePubComp(int packetId);
    public IReadOnlyList<MqttQoS2State> GetPendingMessages();;
    public int CleanupExpired(TimeSpan maxAge);
}
```
```csharp
public sealed class MqttQoS2State
{
}
    public int PacketId { get; init; }
    public required byte[] Payload { get; init; }
    public required string Topic { get; init; }
    public MqttQoS2Phase Phase { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed class MqttRetainedMessageCache
{
}
    public void Set(string topic, byte[] payload, byte qos = 1);
    public IReadOnlyList<MqttRetainedMessage> GetMatching(string topicFilter);
    public bool Remove(string topic);;
    public int Count;;
}
```
```csharp
public sealed record MqttRetainedMessage
{
}
    public required string Topic { get; init; }
    public required byte[] Payload { get; init; }
    public byte QoS { get; init; }
    public DateTime UpdatedAt { get; init; }
}
```
```csharp
public sealed class MqttSessionPersistenceManager
{
}
    public MqttPersistentSession GetOrCreateSession(string clientId, bool cleanSession);
    public void AddSubscription(string clientId, string topicFilter, byte qos);
    public void QueueMessage(string clientId, byte[] payload, string topic, byte qos);
    public IReadOnlyList<MqttPendingMessage> DrainPendingMessages(string clientId, int maxMessages = 1000);
    public bool RemoveSession(string clientId);;
}
```
```csharp
public sealed class MqttPersistentSession
{
}
    public required string ClientId { get; init; }
    public bool CleanSession { get; init; }
    public BoundedDictionary<string, byte> Subscriptions { get; };
    public ConcurrentQueue<MqttPendingMessage> PendingMessages { get; };
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record MqttPendingMessage
{
}
    public required string Topic { get; init; }
    public required byte[] Payload { get; init; }
    public byte QoS { get; init; }
    public DateTime QueuedAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/MqttStreamingStrategy.cs
```csharp
[SdkCompatibility("3.0.0", Notes = "Phase 36: MQTT streaming strategy (EDGE-02)")]
public sealed class MqttStreamingStrategy : StreamingStrategyBase, IAsyncDisposable
{
}
    public MqttStreamingStrategy(MqttConnectionSettings connectionSettings);
    public override string StrategyId;;
    public override string Name;;
    public override StreamingCapabilities Capabilities;;
    public override IReadOnlyList<string> SupportedProtocols;;
    public override async Task<PublishResult> PublishAsync(string streamName, StreamMessage message, CancellationToken ct = default);
    public override async IAsyncEnumerable<StreamMessage> SubscribeAsync(string streamName, ConsumerGroup? consumerGroup = null, SubscriptionOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default);
    public override async Task CreateStreamAsync(string streamName, StreamConfiguration? config = null, CancellationToken ct = default);
    public override async Task DeleteStreamAsync(string streamName, CancellationToken ct = default);
    public override Task<bool> StreamExistsAsync(string streamName, CancellationToken ct = default);
    public override async IAsyncEnumerable<string> ListStreamsAsync([EnumeratorCancellation] CancellationToken ct = default);
    public override Task<StreamInfo> GetStreamInfoAsync(string streamName, CancellationToken ct = default);
    public async Task ConnectAsync(CancellationToken ct = default);
    public async Task DisconnectAsync(CancellationToken ct = default);
    protected override void Dispose(bool disposing);
    public new async ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/IoT/ZigbeeStreamStrategy.cs
```csharp
internal sealed class ZigbeeStreamStrategy : StreamingDataStrategyBase, IStreamingStrategy
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public IReadOnlyList<string> SupportedProtocols;;
    public async Task<IReadOnlyList<PublishResult>> PublishBatchAsync(string streamName, IEnumerable<StreamMessage> messages, CancellationToken ct = default);
    public async Task<PublishResult> PublishAsync(string streamName, StreamMessage message, CancellationToken ct = default);
    public async IAsyncEnumerable<StreamMessage> SubscribeAsync(string streamName, ConsumerGroup? consumerGroup = null, SubscriptionOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default);
    public Task CreateStreamAsync(string streamName, StreamConfiguration? config = null, CancellationToken ct = default);
    public Task DeleteStreamAsync(string streamName, CancellationToken ct = default);
    public Task<bool> StreamExistsAsync(string streamName, CancellationToken ct = default);
    public async IAsyncEnumerable<string> ListStreamsAsync([EnumeratorCancellation] CancellationToken ct = default);
    public Task<StreamInfo> GetStreamInfoAsync(string streamName, CancellationToken ct = default);
    public Task CommitOffsetAsync(string streamName, ConsumerGroup consumerGroup, StreamOffset offset, CancellationToken ct = default);
    public Task SeekAsync(string streamName, ConsumerGroup consumerGroup, StreamOffset offset, CancellationToken ct = default);
    public Task<StreamOffset> GetOffsetAsync(string streamName, ConsumerGroup consumerGroup, CancellationToken ct = default);
    public override void ConfigureIntelligence(IMessageBus? messageBus);
    public ZigbeeDeviceState RegisterDevice(string ieeeAddress, ZigbeeDeviceRole role);
    public void CreateBinding(string sourceAddr, string destAddr, string clusterId);
    public void CreateGroup(string groupId, IEnumerable<string> members);
    internal sealed record ZigbeeDeviceState;
    internal enum ZigbeeDeviceRole;
}
```
```csharp
internal sealed record ZigbeeDeviceState
{
}
    public required string IeeeAddress { get; init; }
    public required string NwkAddress { get; init; }
    public ZigbeeDeviceRole Role { get; init; }
    public DateTime JoinedAt { get; init; }
}
```
```csharp
private sealed record ZigbeeGroupState
{
}
    public required string GroupId { get; init; }
    public List<string> Members { get; init; };
}
```
```csharp
private sealed record ZigbeeBinding
{
}
    public required string SourceAddress { get; init; }
    public required string DestAddress { get; init; }
    public required string ClusterId { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/IoT/MqttStreamStrategy.cs
```csharp
internal sealed class MqttStreamStrategy : StreamingDataStrategyBase, IStreamingStrategy
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public IReadOnlyList<string> SupportedProtocols;;
    public async Task<IReadOnlyList<PublishResult>> PublishBatchAsync(string streamName, IEnumerable<StreamMessage> messages, CancellationToken ct = default);
    public async Task<PublishResult> PublishAsync(string streamName, StreamMessage message, CancellationToken ct = default);
    public async IAsyncEnumerable<StreamMessage> SubscribeAsync(string streamName, ConsumerGroup? consumerGroup = null, SubscriptionOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default);
    public Task CreateStreamAsync(string streamName, StreamConfiguration? config = null, CancellationToken ct = default);
    public Task DeleteStreamAsync(string streamName, CancellationToken ct = default);
    public Task<bool> StreamExistsAsync(string streamName, CancellationToken ct = default);
    public async IAsyncEnumerable<string> ListStreamsAsync([EnumeratorCancellation] CancellationToken ct = default);
    public Task<StreamInfo> GetStreamInfoAsync(string streamName, CancellationToken ct = default);
    public Task CommitOffsetAsync(string streamName, ConsumerGroup consumerGroup, StreamOffset offset, CancellationToken ct = default);
    public Task SeekAsync(string streamName, ConsumerGroup consumerGroup, StreamOffset offset, CancellationToken ct = default);
    public Task<StreamOffset> GetOffsetAsync(string streamName, ConsumerGroup consumerGroup, CancellationToken ct = default);
    public override void ConfigureIntelligence(IMessageBus? messageBus);
}
```
```csharp
private sealed record MqttTopicState
{
}
    public required string TopicName { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
private sealed class MqttSessionState
{
}
    public required string ClientId { get; init; }
    public bool CleanSession { get; init; }
    public long LastDeliveredPacketId { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/IoT/CoapStreamStrategy.cs
```csharp
internal sealed class CoapStreamStrategy : StreamingDataStrategyBase, IStreamingStrategy
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public IReadOnlyList<string> SupportedProtocols;;
    public async Task<IReadOnlyList<PublishResult>> PublishBatchAsync(string streamName, IEnumerable<StreamMessage> messages, CancellationToken ct = default);
    public async Task<PublishResult> PublishAsync(string streamName, StreamMessage message, CancellationToken ct = default);
    public async IAsyncEnumerable<StreamMessage> SubscribeAsync(string streamName, ConsumerGroup? consumerGroup = null, SubscriptionOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default);
    public Task CreateStreamAsync(string streamName, StreamConfiguration? config = null, CancellationToken ct = default);
    public Task DeleteStreamAsync(string streamName, CancellationToken ct = default);
    public Task<bool> StreamExistsAsync(string streamName, CancellationToken ct = default);
    public async IAsyncEnumerable<string> ListStreamsAsync([EnumeratorCancellation] CancellationToken ct = default);
    public Task<StreamInfo> GetStreamInfoAsync(string streamName, CancellationToken ct = default);
    public Task CommitOffsetAsync(string streamName, ConsumerGroup consumerGroup, StreamOffset offset, CancellationToken ct = default);
    public Task SeekAsync(string streamName, ConsumerGroup consumerGroup, StreamOffset offset, CancellationToken ct = default);
    public Task<StreamOffset> GetOffsetAsync(string streamName, ConsumerGroup consumerGroup, CancellationToken ct = default);
    public override void ConfigureIntelligence(IMessageBus? messageBus);
}
```
```csharp
private sealed record CoapResourceState
{
}
    public required string Uri { get; init; }
    public required byte[] CurrentValue { get; init; }
    public required string ETag { get; init; }
    public string ContentFormat { get; init; };
    public DateTime LastModified { get; init; }
    public long ObserveSequence { get; init; }
}
```
```csharp
private sealed class CoapObserverState
{
}
    public required string ObserverId { get; init; }
    public required string ResourceUri { get; init; }
    public DateTime RegisteredAt { get; init; }
    public ConcurrentQueue<StreamMessage> PendingNotifications { get; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/IoT/LoRaWanStreamStrategy.cs
```csharp
internal sealed class LoRaWanStreamStrategy : StreamingDataStrategyBase, IStreamingStrategy
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public IReadOnlyList<string> SupportedProtocols;;
    public async Task<IReadOnlyList<PublishResult>> PublishBatchAsync(string streamName, IEnumerable<StreamMessage> messages, CancellationToken ct = default);
    public async Task<PublishResult> PublishAsync(string streamName, StreamMessage message, CancellationToken ct = default);
    public async IAsyncEnumerable<StreamMessage> SubscribeAsync(string streamName, ConsumerGroup? consumerGroup = null, SubscriptionOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default);
    public Task CreateStreamAsync(string streamName, StreamConfiguration? config = null, CancellationToken ct = default);
    public Task DeleteStreamAsync(string streamName, CancellationToken ct = default);
    public Task<bool> StreamExistsAsync(string streamName, CancellationToken ct = default);
    public async IAsyncEnumerable<string> ListStreamsAsync([EnumeratorCancellation] CancellationToken ct = default);
    public Task<StreamInfo> GetStreamInfoAsync(string streamName, CancellationToken ct = default);
    public Task CommitOffsetAsync(string streamName, ConsumerGroup consumerGroup, StreamOffset offset, CancellationToken ct = default);
    public Task SeekAsync(string streamName, ConsumerGroup consumerGroup, StreamOffset offset, CancellationToken ct = default);
    public Task<StreamOffset> GetOffsetAsync(string streamName, ConsumerGroup consumerGroup, CancellationToken ct = default);
    public override void ConfigureIntelligence(IMessageBus? messageBus);
}
```
```csharp
private sealed record LoRaWanDeviceState
{
}
    public required string DevEui { get; init; }
    public LoRaWanDeviceClass DeviceClass { get; init; }
    public LoRaWanActivation ActivationMode { get; init; }
    public DateTime LastSeen { get; init; }
    public long FrameCounterUp { get; init; }
}
```
```csharp
private sealed record LoRaWanGatewayState
{
}
    public required string GatewayId { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public int ConnectedDevices { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/StreamProcessing/StreamProcessingEngineStrategies.cs
```csharp
public sealed class KafkaStreamProcessingStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<KafkaTopic> CreateTopicAsync(string topicName, int partitions = 12, short replicationFactor = 3, Dictionary<string, string>? config = null, CancellationToken ct = default);
    public Task<ProduceResult> ProduceAsync(string topic, string? key, byte[] value, Dictionary<string, string>? headers = null, int? partition = null, CancellationToken ct = default);
    public async Task<IReadOnlyList<ProduceResult>> ProduceBatchAsync(string topic, IEnumerable<(string? Key, byte[] Value)> records, CancellationToken ct = default);
    public async IAsyncEnumerable<KafkaRecord> ConsumeAsync(string topic, string groupId, long? fromOffset = null, [EnumeratorCancellation] CancellationToken ct = default);
    public Task CommitOffsetAsync(string groupId, string topic, int partition, long offset, CancellationToken ct = default);
    public Task<long> GetOffsetAsync(string topic, int partition, CancellationToken ct = default);
    public Task<IReadOnlyList<KafkaTopic>> ListTopicsAsync(CancellationToken ct = default);
}
```
```csharp
public sealed record KafkaTopic
{
}
    public required string Name { get; init; }
    public int Partitions { get; init; }
    public short ReplicationFactor { get; init; }
    public Dictionary<string, string> Config { get; init; };
    public DateTimeOffset CreatedAt { get; init; }
}
```
```csharp
public sealed record KafkaRecord
{
}
    public required string Topic { get; init; }
    public int Partition { get; init; }
    public long Offset { get; init; }
    public string? Key { get; init; }
    public required byte[] Value { get; init; }
    public Dictionary<string, string> Headers { get; init; };
    public DateTimeOffset Timestamp { get; init; }
}
```
```csharp
public sealed record ProduceResult
{
}
    public required string Topic { get; init; }
    public int Partition { get; init; }
    public long Offset { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}
```
```csharp
internal sealed class ConsumerGroupState
{
}
    public required string GroupId { get; init; }
    public BoundedDictionary<string, long> CommittedOffsets { get; init; };
}
```
```csharp
public sealed class PulsarStreamProcessingStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<PulsarTopic> CreateTopicAsync(string tenant, string namespace_, string topic, int partitions = 0, Dictionary<string, string>? policies = null, CancellationToken ct = default);
    public Task<PulsarSendResult> SendAsync(string topic, byte[] payload, string? key = null, Dictionary<string, string>? properties = null, DateTimeOffset? eventTime = null, CancellationToken ct = default);
    public Task<PulsarSubscription> CreateSubscriptionAsync(string topic, string subscriptionName, SubscriptionType type = SubscriptionType.Exclusive, CancellationToken ct = default);
    public async IAsyncEnumerable<PulsarMessage> ReceiveAsync(string topic, string subscriptionName, [EnumeratorCancellation] CancellationToken ct = default);
    public Task AcknowledgeAsync(string subscriptionKey, string messageId, CancellationToken ct = default);
}
```
```csharp
public sealed record PulsarTopic
{
}
    public required string FullName { get; init; }
    public required string Tenant { get; init; }
    public required string Namespace { get; init; }
    public required string LocalName { get; init; }
    public int Partitions { get; init; }
    public Dictionary<string, string> Policies { get; init; };
    public DateTimeOffset CreatedAt { get; init; }
}
```
```csharp
public sealed record PulsarMessage
{
}
    public required string MessageId { get; init; }
    public required string Topic { get; init; }
    public required byte[] Payload { get; init; }
    public string? Key { get; init; }
    public Dictionary<string, string> Properties { get; init; };
    public DateTimeOffset PublishTime { get; init; }
    public DateTimeOffset? EventTime { get; init; }
}
```
```csharp
public sealed record PulsarSubscription
{
}
    public required string Name { get; init; }
    public required string Topic { get; init; }
    public SubscriptionType Type { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
```
```csharp
public sealed record PulsarSendResult
{
}
    public required string MessageId { get; init; }
    public required string Topic { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}
```
```csharp
public sealed class FlinkStreamProcessingStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<FlinkJob> SubmitJobAsync(FlinkJobSpec spec, CancellationToken ct = default);
    public Task<FlinkSavepoint> TriggerSavepointAsync(string jobId, string? targetDirectory = null, CancellationToken ct = default);
    public async Task<FlinkSavepoint> CancelWithSavepointAsync(string jobId, string? targetDirectory = null, CancellationToken ct = default);
    public async Task<FlinkJob> RestoreFromSavepointAsync(FlinkJobSpec spec, string savepointPath, CancellationToken ct = default);
    public Task<FlinkJob?> GetJobAsync(string jobId, CancellationToken ct = default);
    public Task<IReadOnlyList<FlinkJob>> ListJobsAsync(CancellationToken ct = default);
}
```
```csharp
public sealed record FlinkJobSpec
{
}
    public required string Name { get; init; }
    public int Parallelism { get; init; };
    public bool CheckpointingEnabled { get; init; };
    public TimeSpan CheckpointInterval { get; init; };
    public string StateBackend { get; init; };
    public Dictionary<string, string>? Config { get; init; }
}
```
```csharp
public sealed class FlinkJob
{
}
    public required string JobId { get; init; }
    public required string Name { get; init; }
    public int Parallelism { get; init; }
    public bool CheckpointingEnabled { get; init; }
    public TimeSpan CheckpointInterval { get; init; }
    public string StateBackend { get; init; };
    public FlinkJobStatus Status { get; set; }
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset? EndTime { get; set; }
    public string? RestoredFromSavepoint { get; set; }
}
```
```csharp
public sealed record FlinkSavepoint
{
}
    public required string SavepointId { get; init; }
    public required string JobId { get; init; }
    public required string Path { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public SavepointStatus Status { get; init; }
}
```
```csharp
public sealed class SparkStreamingStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<SparkStreamingQuery> StartQueryAsync(SparkStreamingQueryConfig config, CancellationToken ct = default);
    public Task StopQueryAsync(string queryId, CancellationToken ct = default);
    public Task<SparkStreamingQuery?> GetQueryAsync(string queryId, CancellationToken ct = default);
    public Task<IReadOnlyList<SparkStreamingQuery>> ListQueriesAsync(CancellationToken ct = default);
    public Task<IReadOnlyList<QueryProgress>> GetProgressAsync(string queryId, CancellationToken ct = default);
}
```
```csharp
public sealed record SparkStreamingQueryConfig
{
}
    public required string Name { get; init; }
    public required string SourceFormat { get; init; }
    public required string SinkFormat { get; init; }
    public ProcessingMode ProcessingMode { get; init; };
    public TimeSpan? TriggerInterval { get; init; }
    public string? CheckpointLocation { get; init; }
    public Dictionary<string, string>? Options { get; init; }
}
```
```csharp
public sealed class SparkStreamingQuery
{
}
    public required string QueryId { get; init; }
    public required string Name { get; init; }
    public required string SourceFormat { get; init; }
    public required string SinkFormat { get; init; }
    public ProcessingMode ProcessingMode { get; init; }
    public TimeSpan? TriggerInterval { get; init; }
    public string? CheckpointLocation { get; init; }
    public QueryStatus Status { get; set; }
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset? EndTime { get; set; }
    public long LastBatchId { get; set; }
}
```
```csharp
public sealed record QueryProgress
{
}
    public required string QueryId { get; init; }
    public long BatchId { get; init; }
    public long NumInputRows { get; init; }
    public double InputRowsPerSecond { get; init; }
    public double ProcessedRowsPerSecond { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
```
```csharp
public sealed class RedisStreamsStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<string> XAddAsync(string streamKey, Dictionary<string, string> fields, string? id = null, CancellationToken ct = default);
    public Task<IReadOnlyList<RedisStreamEntry>> XReadAsync(string streamKey, string? lastId = null, int count = 100, CancellationToken ct = default);
    public Task XGroupCreateAsync(string streamKey, string groupName, string? startId = null, CancellationToken ct = default);
    public Task<IReadOnlyList<RedisStreamEntry>> XReadGroupAsync(string streamKey, string groupName, string consumerName, int count = 100, CancellationToken ct = default);
    public Task<long> XAckAsync(string streamKey, string groupName, params string[] ids);
}
```
```csharp
public sealed record RedisStreamEntry
{
}
    public required string Id { get; init; }
    public required Dictionary<string, string> Fields { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
```
```csharp
internal sealed class RedisConsumerGroup
{
}
    public required string Name { get; init; }
    public required string StreamKey { get; init; }
    public string LastDeliveredId { get; set; };
    public BoundedDictionary<string, RedisConsumer> Consumers { get; init; };
}
```
```csharp
internal sealed class RedisConsumer
{
}
    public required string Name { get; init; }
    public long PendingCount { get; set; }
}
```
```csharp
public sealed class KinesisStreamProcessingStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<KinesisStream> CreateStreamAsync(string streamName, int shardCount = 1, CancellationToken ct = default);
    public Task<PutRecordResult> PutRecordAsync(string streamName, byte[] data, string partitionKey, CancellationToken ct = default);
    public Task<GetRecordsResult> GetRecordsAsync(string streamName, string shardId, string? startingSequenceNumber = null, int limit = 100, CancellationToken ct = default);
    public Task<KinesisStream?> DescribeStreamAsync(string streamName, CancellationToken ct = default);
}
```
```csharp
public sealed record KinesisStream
{
}
    public required string StreamName { get; init; }
    public required string StreamARN { get; init; }
    public int ShardCount { get; init; }
    public StreamStatus Status { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
```
```csharp
public sealed record KinesisRecord
{
}
    public required string SequenceNumber { get; init; }
    public required string ShardId { get; init; }
    public required string PartitionKey { get; init; }
    public required byte[] Data { get; init; }
    public DateTimeOffset ApproximateArrivalTimestamp { get; init; }
}
```
```csharp
public sealed record PutRecordResult
{
}
    public required string ShardId { get; init; }
    public required string SequenceNumber { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}
```
```csharp
public sealed record GetRecordsResult
{
}
    public required IReadOnlyList<KinesisRecord> Records { get; init; }
    public string? NextShardIterator { get; init; }
    public long MillisBehindLatest { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/Scalability/ScalabilityStrategies.cs
```csharp
public sealed class PartitioningStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<string> CreateManagerAsync(string streamId, PartitionConfig config, CancellationToken ct = default);
    public Task<int> AssignPartitionAsync(string managerId, string? key, CancellationToken ct = default);
    public Task<RebalanceResult> RebalanceAsync(string managerId, string[] workers, CancellationToken ct = default);
    public Task<IReadOnlyList<PartitionStats>> GetPartitionStatsAsync(string managerId, CancellationToken ct = default);
}
```
```csharp
public sealed record PartitionConfig
{
}
    public int PartitionCount { get; init; };
    public PartitionStrategy Strategy { get; init; };
    public bool EnableRebalancing { get; init; };
}
```
```csharp
internal sealed class PartitionManager
{
}
    public required string ManagerId { get; init; }
    public required string StreamId { get; init; }
    public required PartitionConfig Config { get; init; }
    public required BoundedDictionary<int, PartitionState> Partitions { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public long RoundRobinCounter;
}
```
```csharp
internal sealed class PartitionState
{
}
    public int PartitionId { get; init; }
    public long EventCount;
    public DateTimeOffset LastEventTime { get; set; }
    public string? AssignedWorker { get; set; }
}
```
```csharp
public sealed record RebalanceResult
{
}
    public required Dictionary<int, string> Assignments { get; init; }
    public int WorkerCount { get; init; }
    public int PartitionCount { get; init; }
    public DateTimeOffset RebalancedAt { get; init; }
}
```
```csharp
public sealed record PartitionStats
{
}
    public int PartitionId { get; init; }
    public long EventCount { get; init; }
    public DateTimeOffset LastEventTime { get; init; }
    public string? AssignedWorker { get; init; }
}
```
```csharp
public sealed class AutoScalingStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<string> CreateScalerAsync(string jobId, AutoScaleConfig config, CancellationToken ct = default);
    public Task ReportMetricsAsync(string scalerId, ScalingMetrics metrics, CancellationToken ct = default);
    public Task<ScalingDecision> EvaluateScalingAsync(string scalerId, CancellationToken ct = default);
    public Task ApplyScalingAsync(string scalerId, ScalingDecision decision, CancellationToken ct = default);
    public Task<IReadOnlyList<ScalingEvent>> GetScalingHistoryAsync(string scalerId, int limit = 20, CancellationToken ct = default);
}
```
```csharp
public sealed record AutoScaleConfig
{
}
    public int MinInstances { get; init; };
    public int MaxInstances { get; init; };
    public double ScaleUpCpuThreshold { get; init; };
    public double ScaleDownCpuThreshold { get; init; };
    public long ScaleUpBacklogThreshold { get; init; };
    public long ScaleDownBacklogThreshold { get; init; };
    public int ScaleUpIncrement { get; init; };
    public int ScaleDownDecrement { get; init; };
    public TimeSpan ScaleCooldown { get; init; };
    public TimeSpan MetricWindow { get; init; };
}
```
```csharp
internal sealed class AutoScaler
{
}
    public required string ScalerId { get; init; }
    public required string JobId { get; init; }
    public required AutoScaleConfig Config { get; init; }
    public int CurrentInstances { get; set; }
    public required List<ScalingMetrics> MetricsHistory { get; init; }
    public required List<ScalingEvent> ScalingHistory { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastScaleTime { get; set; }
}
```
```csharp
public sealed record ScalingMetrics
{
}
    public double CpuUtilization { get; init; }
    public double MemoryUtilization { get; init; }
    public double EventsPerSecond { get; init; }
    public long BacklogSize { get; init; }
    public double AverageLatencyMs { get; init; }
    public DateTimeOffset Timestamp { get; init; };
}
```
```csharp
public sealed record ScalingDecision
{
}
    public ScalingAction Action { get; init; }
    public int CurrentInstances { get; init; }
    public int TargetInstances { get; init; }
    public required string Reason { get; init; }
}
```
```csharp
public sealed record ScalingEvent
{
}
    public DateTimeOffset Timestamp { get; init; }
    public ScalingAction Action { get; init; }
    public int PreviousInstances { get; init; }
    public int NewInstances { get; init; }
    public string? Reason { get; init; }
}
```
```csharp
public sealed class BackpressureStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<string> CreateControllerAsync(string streamId, BackpressureConfig config, CancellationToken ct = default);
    public Task<SubmitResult> TrySubmitAsync<T>(string controllerId, T @event, CancellationToken ct = default);
    public Task<IReadOnlyList<BufferedEvent>> TakeAsync(string controllerId, int maxCount = 100, CancellationToken ct = default);
    public Task<BackpressureStats> GetStatsAsync(string controllerId, CancellationToken ct = default);
    public Task AdjustConfigAsync(string controllerId, BackpressureConfig newConfig, CancellationToken ct = default);
}
```
```csharp
public sealed record BackpressureConfig
{
}
    public BackpressureStrategyType Strategy { get; init; };
    public int MaxBufferSize { get; init; };
    public long? RateLimitPerSecond { get; init; }
    public TimeSpan? BlockTimeout { get; init; }
    public double SamplingRate { get; init; };
}
```
```csharp
internal sealed class BackpressureController
{
}
    public required string ControllerId { get; init; }
    public required string StreamId { get; init; }
    public required BackpressureConfig Config { get; init; }
    public required ConcurrentQueue<BufferedEvent> Buffer { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public long AcceptedCount;
    public long DroppedCount;
    public long BlockedCount;
    public long RateLimitedCount;
    public long SampledOutCount;
    public DateTimeOffset RateLimitWindowStart = DateTimeOffset.UtcNow;
    public long RateLimitCounter;
}
```
```csharp
public sealed record BufferedEvent
{
}
    public required string EventId { get; init; }
    public object? Payload { get; init; }
    public DateTimeOffset EnqueuedAt { get; init; }
}
```
```csharp
public sealed record SubmitResult
{
}
    public bool Accepted { get; init; }
    public BackpressureAction Action { get; init; }
    public string? Reason { get; init; }
    public int BufferSize { get; init; }
}
```
```csharp
public sealed record BackpressureStats
{
}
    public int BufferSize { get; init; }
    public long AcceptedCount { get; init; }
    public long DroppedCount { get; init; }
    public long BlockedCount { get; init; }
    public long RateLimitedCount { get; init; }
    public long SampledOutCount { get; init; }
    public double BufferUtilization { get; init; }
}
```
```csharp
public sealed class LoadBalancingStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<string> CreateBalancerAsync(string name, LoadBalanceConfig config, CancellationToken ct = default);
    public Task<WorkerSelection> SelectWorkerAsync(string balancerId, string? affinityKey = null, CancellationToken ct = default);
    public Task ReleaseWorkerAsync(string balancerId, string workerId, CancellationToken ct = default);
    public Task UpdateWorkerHealthAsync(string balancerId, string workerId, bool isHealthy, CancellationToken ct = default);
    public Task<LoadBalancerStats> GetStatsAsync(string balancerId, CancellationToken ct = default);
}
```
```csharp
public sealed record LoadBalanceConfig
{
}
    public LoadBalanceAlgorithm Algorithm { get; init; };
    public required WorkerConfig[] Workers { get; init; }
    public bool EnableAffinity { get; init; }
    public TimeSpan HealthCheckInterval { get; init; };
}
```
```csharp
public sealed record WorkerConfig
{
}
    public required string WorkerId { get; init; }
    public string? Address { get; init; }
    public double Weight { get; init; };
}
```
```csharp
internal sealed class LoadBalancer
{
}
    public required string BalancerId { get; init; }
    public required string Name { get; init; }
    public required LoadBalanceConfig Config { get; init; }
    public required BoundedDictionary<string, WorkerState> Workers { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public long RoundRobinCounter;
}
```
```csharp
internal sealed class WorkerState
{
}
    public required string WorkerId { get; init; }
    public double Weight { get; init; }
    public bool IsHealthy { get; set; }
    public long ActiveConnections;
    public long TotalRequests;
    public DateTimeOffset LastHealthCheck { get; set; }
}
```
```csharp
public sealed record WorkerSelection
{
}
    public bool Success { get; init; }
    public string? WorkerId { get; init; }
    public string? Reason { get; init; }
}
```
```csharp
public sealed record LoadBalancerStats
{
}
    public int TotalWorkers { get; init; }
    public int HealthyWorkers { get; init; }
    public long TotalActiveConnections { get; init; }
    public long TotalRequests { get; init; }
    public required List<WorkerStats> WorkerStats { get; init; }
}
```
```csharp
public sealed record WorkerStats
{
}
    public required string WorkerId { get; init; }
    public bool IsHealthy { get; init; }
    public long ActiveConnections { get; init; }
    public long TotalRequests { get; init; }
    public double Weight { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/Healthcare/Hl7StreamStrategy.cs
```csharp
public sealed record Hl7Message
{
}
    public required string MessageControlId { get; init; }
    public Hl7MessageType MessageType { get; init; }
    public Hl7TriggerEvent TriggerEvent { get; init; }
    public string Version { get; init; };
    public string? SendingApplication { get; init; }
    public string? SendingFacility { get; init; }
    public string? ReceivingApplication { get; init; }
    public string? ReceivingFacility { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public Hl7ProcessingId ProcessingId { get; init; };
    public Dictionary<string, List<string[]>> Segments { get; init; };
    public string? RawMessage { get; init; }
}
```
```csharp
public sealed record Hl7Acknowledgment
{
}
    public Hl7AckCode AckCode { get; init; }
    public required string MessageControlId { get; init; }
    public string? TextMessage { get; init; }
    public string? ErrorCondition { get; init; }
    public required string RawAck { get; init; }
}
```
```csharp
public sealed record MllpConnectionConfig
{
}
    public required string Host { get; init; }
    public int Port { get; init; };
    public int ConnectionTimeoutMs { get; init; };
    public int ReceiveTimeoutMs { get; init; };
    public bool UseTls { get; init; }
    public int MaxMessageSizeBytes { get; init; };
    public bool AutoAcknowledge { get; init; };
    public bool EnhancedAcknowledgment { get; init; }
}
```
```csharp
internal sealed class Hl7StreamStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<string> ConnectAsync(MllpConnectionConfig config, CancellationToken ct = default);
    public Hl7Message ParseMessage(string rawMessage);
    public byte[] WrapInMllp(string hl7Message);
    public string UnwrapFromMllp(byte[] mllpData);
    public Hl7Acknowledgment GenerateAck(Hl7Message originalMessage, Hl7AckCode ackCode = Hl7AckCode.AA, string? textMessage = null);
    public Task<Hl7Acknowledgment> SendMessageAsync(string connectionId, Hl7Message message, CancellationToken ct = default);
    public string? ExtractPatientId(Hl7Message message);
    public (string? FamilyName, string? GivenName)? ExtractPatientName(Hl7Message message);
    public IReadOnlyList<(string Identifier, string Value, string? Units)> ExtractObservations(Hl7Message message);
    public IReadOnlyList<string> ValidateMessage(Hl7Message message);
    public Task DisconnectAsync(string connectionId, CancellationToken ct = default);
    public long TotalMessages;;
    public long TotalAcknowledgments;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/Healthcare/FhirStreamStrategy.cs
```csharp
public sealed record FhirSubscription
{
}
    public required string Id { get; init; }
    public FhirSubscriptionStatus Status { get; init; };
    public string? Reason { get; init; }
    public required string Criteria { get; init; }
    public required FhirSubscriptionChannel Channel { get; init; }
    public DateTimeOffset? End { get; init; }
    public FhirResourceType[] ResourceTypes { get; init; };
    public List<FhirSubscriptionFilter> Filters { get; init; };
    public DateTimeOffset CreatedAt { get; init; };
}
```
```csharp
public sealed record FhirSubscriptionChannel
{
}
    public FhirSubscriptionChannelType Type { get; init; };
    public string? Endpoint { get; init; }
    public string Payload { get; init; };
    public Dictionary<string, string>? Headers { get; init; }
}
```
```csharp
public sealed record FhirSubscriptionFilter
{
}
    public required string ParamName { get; init; }
    public FhirFilterOperator Operator { get; init; };
    public required string Value { get; init; }
}
```
```csharp
public sealed record FhirNotification
{
}
    public required string NotificationId { get; init; }
    public required string SubscriptionId { get; init; }
    public FhirNotificationType Type { get; init; };
    public FhirResource? Focus { get; init; }
    public List<FhirResource>? AdditionalContext { get; init; }
    public DateTimeOffset Timestamp { get; init; };
    public long SequenceNumber { get; init; }
    public long EventsSinceSubscriptionStart { get; init; }
}
```
```csharp
public sealed record FhirResource
{
}
    public required string ResourceType { get; init; }
    public required string Id { get; init; }
    public string? VersionId { get; init; }
    public DateTimeOffset LastUpdated { get; init; };
    public required string JsonContent { get; init; }
    public List<string>? Profiles { get; init; }
}
```
```csharp
public sealed record FhirWebhookDeliveryResult
{
}
    public required string NotificationId { get; init; }
    public bool Success { get; init; }
    public int HttpStatusCode { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset DeliveredAt { get; init; };
    public int AttemptNumber { get; init; };
}
```
```csharp
internal sealed class FhirStreamStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<FhirSubscription> CreateSubscriptionAsync(FhirSubscription subscription, CancellationToken ct = default);
    public FhirSubscription? GetSubscription(string subscriptionId);
    public Task<FhirSubscription> UpdateSubscriptionStatusAsync(string subscriptionId, FhirSubscriptionStatus status, CancellationToken ct = default);
    public Task<IReadOnlyList<FhirNotification>> ProcessResourceChangeAsync(FhirResource resource, CancellationToken ct = default);
    public Task<IReadOnlyList<FhirWebhookDeliveryResult>> DeliverNotificationsAsync(string subscriptionId, int maxBatchSize = 10, CancellationToken ct = default);
    public Task<FhirNotification> SendHeartbeatAsync(string subscriptionId, CancellationToken ct = default);
    public string BuildNotificationBundle(IReadOnlyList<FhirNotification> notifications);
    public Task DeleteSubscriptionAsync(string subscriptionId, CancellationToken ct = default);
    public int ActiveSubscriptionCount;;
    public long TotalNotifications;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/Cloud/PubSubStreamStrategy.cs
```csharp
public sealed record PubSubTopic
{
}
    public required string TopicName { get; init; }
    public required string ProjectId { get; init; }
    public required string ShortName { get; init; }
    public string? KmsKeyName { get; init; }
    public TimeSpan? MessageRetentionDuration { get; init; }
    public PubSubSchemaSettings? SchemaSettings { get; init; }
    public Dictionary<string, string> Labels { get; init; };
    public DateTimeOffset CreatedAt { get; init; };
}
```
```csharp
public sealed record PubSubSchemaSettings
{
}
    public required string SchemaName { get; init; }
    public string Encoding { get; init; };
    public string? FirstRevisionId { get; init; }
    public string? LastRevisionId { get; init; }
}
```
```csharp
public sealed record PubSubSubscription
{
}
    public required string SubscriptionName { get; init; }
    public required string TopicName { get; init; }
    public PubSubDeliveryType DeliveryType { get; init; };
    public int AckDeadlineSeconds { get; init; };
    public TimeSpan MessageRetention { get; init; };
    public bool RetainAckedMessages { get; init; }
    public string? DeadLetterTopic { get; init; }
    public int MaxDeliveryAttempts { get; init; };
    public string? Filter { get; init; }
    public bool EnableMessageOrdering { get; init; }
    public bool EnableExactlyOnceDelivery { get; init; }
    public string? PushEndpoint { get; init; }
    public TimeSpan? ExpirationTtl { get; init; }
    public DateTimeOffset CreatedAt { get; init; };
}
```
```csharp
public sealed record PubSubMessage
{
}
    public required string MessageId { get; init; }
    public required byte[] Data { get; init; }
    public Dictionary<string, string> Attributes { get; init; };
    public string? OrderingKey { get; init; }
    public DateTimeOffset PublishTime { get; init; };
    public int DeliveryAttempt { get; init; };
    public string? AckId { get; init; }
    public PubSubAckStatus AckStatus { get; init; };
}
```
```csharp
public sealed record PubSubPublishResult
{
}
    public required IReadOnlyList<string> MessageIds { get; init; }
    public int MessageCount { get; init; }
    public bool Success { get; init; };
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record PubSubSnapshot
{
}
    public required string SnapshotName { get; init; }
    public required string SubscriptionName { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; };
}
```
```csharp
internal sealed class PubSubStreamStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<PubSubTopic> CreateTopicAsync(string projectId, string topicName, string? kmsKeyName = null, TimeSpan? messageRetention = null, PubSubSchemaSettings? schemaSettings = null, CancellationToken ct = default);
    public Task<PubSubSubscription> CreateSubscriptionAsync(string projectId, string subscriptionName, string topicName, PubSubDeliveryType deliveryType = PubSubDeliveryType.Pull, int ackDeadlineSeconds = 10, bool enableOrdering = false, bool enableExactlyOnce = false, string? filter = null, string? deadLetterTopic = null, int maxDeliveryAttempts = 5, string? pushEndpoint = null, CancellationToken ct = default);
    public Task<PubSubPublishResult> PublishAsync(string topicName, IReadOnlyList<(byte[] Data, Dictionary<string, string>? Attributes, string? OrderingKey)> messages, CancellationToken ct = default);
    public Task<IReadOnlyList<PubSubMessage>> PullAsync(string subscriptionName, int maxMessages = 100, CancellationToken ct = default);
    public Task AcknowledgeAsync(string subscriptionName, IReadOnlyList<string> ackIds, CancellationToken ct = default);
    public Task NegativeAcknowledgeAsync(string subscriptionName, IReadOnlyList<string> ackIds, CancellationToken ct = default);
    public Task ModifyAckDeadlineAsync(string subscriptionName, IReadOnlyList<string> ackIds, int ackDeadlineSeconds, CancellationToken ct = default);
    public Task<PubSubSnapshot> CreateSnapshotAsync(string projectId, string snapshotName, string subscriptionName, CancellationToken ct = default);
    public Task SeekAsync(string subscriptionName, string? snapshotName = null, DateTimeOffset? timestamp = null, CancellationToken ct = default);
    public Task DeleteTopicAsync(string topicName, CancellationToken ct = default);
    public Task DeleteSubscriptionAsync(string subscriptionName, CancellationToken ct = default);
    public long TotalPublished;;
    public long TotalDelivered;;
    public long TotalAcknowledged;;
    public long TotalDeadLettered;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/Cloud/EventHubsStreamStrategy.cs
```csharp
public sealed record EventHubConfig
{
}
    public required string NamespaceFqdn { get; init; }
    public required string EventHubName { get; init; }
    public int PartitionCount { get; init; };
    public int RetentionDays { get; init; };
    public EventHubsTier Tier { get; init; };
    public int ThroughputUnits { get; init; };
    public bool CaptureEnabled { get; init; }
    public string? CaptureDestination { get; init; }
    public int CaptureWindowSeconds { get; init; };
    public long CaptureWindowBytes { get; init; };
    public bool SchemaRegistryEnabled { get; init; }
}
```
```csharp
public sealed record EventHubPartition
{
}
    public required string PartitionId { get; init; }
    public long BeginningSequenceNumber { get; init; }
    public long LastEnqueuedSequenceNumber { get; init; }
    public string LastEnqueuedOffset { get; init; };
    public DateTimeOffset LastEnqueuedTime { get; init; }
    public bool IsEmpty { get; init; };
}
```
```csharp
public sealed record EventHubEvent
{
}
    public required byte[] Body { get; init; }
    public string? PartitionKey { get; init; }
    public Dictionary<string, object> Properties { get; init; };
    public EventHubSystemProperties? SystemProperties { get; init; }
    public string? ContentType { get; init; }
    public string? CorrelationId { get; init; }
    public string? MessageId { get; init; }
}
```
```csharp
public sealed record EventHubSystemProperties
{
}
    public long SequenceNumber { get; init; }
    public string Offset { get; init; };
    public DateTimeOffset EnqueuedTime { get; init; }
    public string PartitionId { get; init; };
    public string? PartitionKey { get; init; }
}
```
```csharp
public sealed record EventHubConsumerGroup
{
}
    public required string GroupName { get; init; }
    public bool IsDefault { get; init; }
    public DateTimeOffset CreatedAt { get; init; };
}
```
```csharp
public sealed record EventHubCheckpoint
{
}
    public required string ConsumerGroup { get; init; }
    public required string PartitionId { get; init; }
    public required string Offset { get; init; }
    public long SequenceNumber { get; init; }
    public DateTimeOffset CheckpointedAt { get; init; };
}
```
```csharp
public sealed record EventHubSendResult
{
}
    public int EventCount { get; init; }
    public string? PartitionId { get; init; }
    public bool Success { get; init; };
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed record PartitionOwnership
{
}
    public required string ConsumerGroup { get; init; }
    public required string PartitionId { get; init; }
    public required string OwnerId { get; init; }
    public string ETag { get; init; };
    public DateTimeOffset LastModified { get; init; };
}
```
```csharp
internal sealed class EventHubsStreamStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<EventHubConfig> CreateEventHubAsync(EventHubConfig config, CancellationToken ct = default);
    public Task<EventHubSendResult> SendEventsAsync(string namespaceFqdn, string eventHubName, IReadOnlyList<EventHubEvent> events, string? partitionId = null, CancellationToken ct = default);
    public Task<IReadOnlyList<EventHubEvent>> ReceiveEventsAsync(string namespaceFqdn, string eventHubName, string partitionId, string consumerGroup = "$Default", int maxEvents = 100, CancellationToken ct = default);
    public Task<EventHubConsumerGroup> CreateConsumerGroupAsync(string namespaceFqdn, string eventHubName, string groupName, CancellationToken ct = default);
    public Task<EventHubCheckpoint> CheckpointAsync(string namespaceFqdn, string eventHubName, string consumerGroup, string partitionId, string offset, long sequenceNumber, CancellationToken ct = default);
    public Task<PartitionOwnership> ClaimOwnershipAsync(string namespaceFqdn, string eventHubName, string consumerGroup, string partitionId, string ownerId, CancellationToken ct = default);
    public Task<IReadOnlyList<EventHubPartition>> GetPartitionsAsync(string namespaceFqdn, string eventHubName, CancellationToken ct = default);
    public Task DeleteEventHubAsync(string namespaceFqdn, string eventHubName, CancellationToken ct = default);
    public long TotalEventsSent;;
    public long TotalEventsReceived;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/Cloud/KinesisStreamStrategy.cs
```csharp
public sealed record KinesisStream
{
}
    public required string StreamName { get; init; }
    public string Region { get; init; };
    public int ShardCount { get; init; };
    public int RetentionHours { get; init; };
    public bool EncryptionEnabled { get; init; }
    public string? KmsKeyId { get; init; }
    public DateTimeOffset CreatedAt { get; init; };
    public bool EnhancedFanOutEnabled { get; init; }
    public string StreamMode { get; init; };
}
```
```csharp
public sealed record KinesisShard
{
}
    public required string ShardId { get; init; }
    public string StartingHashKey { get; init; };
    public string EndingHashKey { get; init; };
    public string? ParentShardId { get; init; }
    public string? AdjacentParentShardId { get; init; }
    public string StartingSequenceNumber { get; init; };
    public KinesisShardStatus Status { get; init; };
}
```
```csharp
public sealed record KinesisRecord
{
}
    public required string SequenceNumber { get; init; }
    public required string PartitionKey { get; init; }
    public required byte[] Data { get; init; }
    public string? ExplicitHashKey { get; init; }
    public DateTimeOffset ApproximateArrivalTimestamp { get; init; };
    public string EncryptionType { get; init; };
    public long? SubSequenceNumber { get; init; }
}
```
```csharp
public sealed record KinesisPutResult
{
}
    public required string SequenceNumber { get; init; }
    public required string ShardId { get; init; }
    public string EncryptionType { get; init; };
    public bool WasThrottled { get; init; }
}
```
```csharp
public sealed record KinesisConsumer
{
}
    public required string ConsumerName { get; init; }
    public required string ConsumerArn { get; init; }
    public KinesisConsumerType ConsumerType { get; init; };
    public DateTimeOffset CreatedAt { get; init; };
    public string Status { get; init; };
}
```
```csharp
public sealed record KinesisCheckpoint
{
}
    public required string ShardId { get; init; }
    public required string SequenceNumber { get; init; }
    public long SubSequenceNumber { get; init; }
    public DateTimeOffset CheckpointedAt { get; init; };
}
```
```csharp
internal sealed class KinesisStreamStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<KinesisStream> CreateStreamAsync(string streamName, int shardCount = 1, int retentionHours = 24, bool encryptionEnabled = false, string? kmsKeyId = null, string region = "us-east-1", CancellationToken ct = default);
    public Task<KinesisPutResult> PutRecordAsync(string streamName, string partitionKey, byte[] data, string? explicitHashKey = null, CancellationToken ct = default);
    public async Task<IReadOnlyList<KinesisPutResult>> PutRecordsBatchAsync(string streamName, IReadOnlyList<(string PartitionKey, byte[] Data)> records, CancellationToken ct = default);
    public Task<IReadOnlyList<KinesisRecord>> GetRecordsAsync(string streamName, string shardId, KinesisIteratorType iteratorType = KinesisIteratorType.TrimHorizon, string? sequenceNumber = null, int maxRecords = 10000, CancellationToken ct = default);
    public Task<KinesisConsumer> RegisterConsumerAsync(string streamName, string consumerName, CancellationToken ct = default);
    public Task<KinesisCheckpoint> CheckpointAsync(string streamName, string shardId, string sequenceNumber, string consumerName, CancellationToken ct = default);
    public Task<(KinesisShard Child1, KinesisShard Child2)> SplitShardAsync(string streamName, string shardId, CancellationToken ct = default);
    public Task<IReadOnlyList<KinesisShard>> ListShardsAsync(string streamName, CancellationToken ct = default);
    public Task DeleteStreamAsync(string streamName, CancellationToken ct = default);
    public long TotalRecordsPut;;
    public long TotalRecordsRead;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/EventDriven/EventDrivenArchitectureStrategies.cs
```csharp
public sealed class EventSourcingStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<EventStream> CreateStreamAsync(string streamId, string aggregateType, EventStreamConfig? config = null, CancellationToken cancellationToken = default);
    public Task<AppendResult> AppendEventsAsync(string streamId, IReadOnlyList<DomainEvent> events, long expectedVersion, CancellationToken cancellationToken = default);
    public async IAsyncEnumerable<StoredEvent> ReadEventsAsync(string streamId, long fromVersion = 0, long? toVersion = null, [EnumeratorCancellation] CancellationToken cancellationToken = default);
    public async Task<TAggregateState> ReplayAsync<TAggregateState>(string streamId, Func<TAggregateState, StoredEvent, TAggregateState> applyEvent, TAggregateState initialState, CancellationToken cancellationToken = default)
    where TAggregateState : class;
    public Task<Snapshot> CreateSnapshotAsync(string streamId, object state, CancellationToken cancellationToken = default);
    public Task<EventStreamMetadata> GetMetadataAsync(string streamId);
}
```
```csharp
public sealed class CqrsStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task RegisterCommandHandlerAsync<TCommand>(string commandType, Func<TCommand, CancellationToken, Task<CommandResult>> handler, CancellationToken cancellationToken = default)
    where TCommand : ICommand;
    public Task RegisterQueryHandlerAsync<TQuery, TResult>(string queryType, Func<TQuery, CancellationToken, Task<TResult>> handler, CancellationToken cancellationToken = default)
    where TQuery : IQuery<TResult>;
    public async Task<CommandResult> ExecuteCommandAsync<TCommand>(TCommand command, CancellationToken cancellationToken = default)
    where TCommand : ICommand;
    public async Task<TResult> ExecuteQueryAsync<TQuery, TResult>(TQuery query, CancellationToken cancellationToken = default)
    where TQuery : IQuery<TResult>;
    public Task<ReadModel> CreateReadModelAsync(string modelId, ReadModelConfig? config = null, CancellationToken cancellationToken = default);
    public async Task UpdateReadModelAsync(string modelId, IAsyncEnumerable<DomainEvent> events, Func<ReadModel, DomainEvent, ReadModel> projector, CancellationToken cancellationToken = default);
    public Task<T?> QueryReadModelAsync<T>(string modelId, string key, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class SagaOrchestrationStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<SagaDefinition> DefineSagaAsync(string sagaType, IReadOnlyList<SagaStep> steps, SagaConfig? config = null, CancellationToken cancellationToken = default);
    public Task<SagaInstance> StartSagaAsync(string sagaType, string sagaId, Dictionary<string, object>? initialData = null, CancellationToken cancellationToken = default);
    public async Task<StepResult> ExecuteStepAsync(string sagaId, CancellationToken cancellationToken = default);
    public async IAsyncEnumerable<CompensationResult> CompensateAsync(string sagaId, [EnumeratorCancellation] CancellationToken cancellationToken = default);
    public Task<SagaStatus> GetStatusAsync(string sagaId);
}
```
```csharp
public sealed class EventBusStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<EventTopic> CreateTopicAsync(string topicName, TopicConfig? config = null, CancellationToken cancellationToken = default);
    public Task<Subscription> SubscribeAsync(string topicName, string subscriberId, Func<PublishedEvent, CancellationToken, Task<bool>> handler, SubscriptionConfig? config = null, CancellationToken cancellationToken = default);
    public async Task<PublishResult> PublishAsync(string topicName, object eventData, string? eventType = null, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default);
    public async IAsyncEnumerable<PublishResult> PublishBatchAsync(string topicName, IAsyncEnumerable<object> events, string? eventType = null, [EnumeratorCancellation] CancellationToken cancellationToken = default);
    public Task UnsubscribeAsync(string topicName, string subscriberId, CancellationToken cancellationToken = default);
    public Task<IReadOnlyList<PublishedEvent>> GetDeadLettersAsync(string topicName, int limit = 100, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class DomainEventsStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task RegisterHandlerAsync<TEvent>(Func<TEvent, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
    where TEvent : IDomainEvent;
    public async Task RaiseAsync<TEvent>(TEvent domainEvent, string? aggregateId = null, CancellationToken cancellationToken = default)
    where TEvent : IDomainEvent;
    public async Task RaiseManyAsync(IEnumerable<IDomainEvent> events, string? aggregateId = null, CancellationToken cancellationToken = default);
    public Task<IReadOnlyList<IDomainEvent>> CollectEventsAsync(string aggregateId, CancellationToken cancellationToken = default);
    public Task RegisterAggregateAsync(string aggregateId, AggregateRoot aggregate, CancellationToken cancellationToken = default);
    public Task<IReadOnlyList<DomainEventRecord>> GetEventHistoryAsync(string eventType, int limit = 100, CancellationToken cancellationToken = default);
}
```
```csharp
public record EventStream
{
}
    public required string StreamId { get; init; }
    public required string AggregateType { get; init; }
    public required EventStreamConfig Config { get; init; }
    public long Version { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public record EventStreamConfig
{
}
    public int SnapshotEveryN { get; init; };
    public bool EnableCompression { get; init; };
}
```
```csharp
public record DomainEvent
{
}
    public string? EventId { get; init; }
    public required string EventType { get; init; }
    public Dictionary<string, object>? Data { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}
```
```csharp
public record StoredEvent
{
}
    public required string EventId { get; init; }
    public required string StreamId { get; init; }
    public required string EventType { get; init; }
    public Dictionary<string, object>? Data { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
    public long Version { get; init; }
    public long GlobalSequence { get; init; }
    public DateTime Timestamp { get; init; }
}
```
```csharp
public record AppendResult
{
}
    public required string StreamId { get; init; }
    public long NewVersion { get; init; }
    public int EventsAppended { get; init; }
    public long FirstEventSequence { get; init; }
    public long LastEventSequence { get; init; }
}
```
```csharp
public record Snapshot
{
}
    public required string StreamId { get; init; }
    public long Version { get; init; }
    public object? State { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public record EventStreamMetadata
{
}
    public required string StreamId { get; init; }
    public required string AggregateType { get; init; }
    public long CurrentVersion { get; init; }
    public int EventCount { get; init; }
    public bool HasSnapshot { get; init; }
    public long? SnapshotVersion { get; init; }
    public DateTime CreatedAt { get; init; }
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
public record CqrsAggregate
{
}
    public required string AggregateId { get; init; }
    public required string AggregateType { get; init; }
    public long Version { get; init; }
}
```
```csharp
public record CommandResult
{
}
    public bool Success { get; init; }
    public string? Error { get; init; }
    public Dictionary<string, object>? Data { get; init; }
}
```
```csharp
public record ReadModel
{
}
    public required string ModelId { get; init; }
    public required ReadModelConfig Config { get; init; }
    public required BoundedDictionary<string, object> Data { get; init; }
    public DateTime LastUpdated { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public record ReadModelConfig
{
}
    public bool EnableCaching { get; init; };
    public TimeSpan CacheTtl { get; init; };
}
```
```csharp
public class CommandHandlerWrapper<TCommand> : ICommandHandler where TCommand : ICommand
{
}
    public CommandHandlerWrapper(Func<TCommand, CancellationToken, Task<CommandResult>> handler);
    public Task<CommandResult> HandleAsync(TCommand command, CancellationToken ct);;
}
```
```csharp
public class QueryHandlerWrapper<TQuery, TResult> : IQueryHandler where TQuery : IQuery<TResult>
{
}
    public QueryHandlerWrapper(Func<TQuery, CancellationToken, Task<TResult>> handler);
    public Task<TResult> HandleAsync(TQuery query, CancellationToken ct);;
}
```
```csharp
public record SagaDefinition
{
}
    public required string SagaType { get; init; }
    public required List<SagaStep> Steps { get; init; }
    public required SagaConfig Config { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public record SagaStep
{
}
    public required string StepName { get; init; }
    public required Func<Dictionary<string, object>, CancellationToken, Task<StepActionResult>> Action { get; init; }
    public Func<Dictionary<string, object>, CancellationToken, Task>? CompensationAction { get; init; }
    public TimeSpan? Timeout { get; init; }
}
```
```csharp
public record SagaConfig
{
}
    public TimeSpan DefaultTimeout { get; init; };
    public int MaxRetries { get; init; };
}
```
```csharp
public record SagaInstance
{
}
    public required string SagaId { get; init; }
    public required string SagaType { get; init; }
    public SagaState State { get; set; }
    public int CurrentStep { get; set; }
    public required Dictionary<string, object> Data { get; init; }
    public DateTime StartedAt { get; init; }
}
```
```csharp
public record SagaEvent
{
}
    public required SagaEventType EventType { get; init; }
    public int StepIndex { get; init; }
    public string? StepName { get; init; }
    public string? Error { get; init; }
    public DateTime Timestamp { get; init; }
}
```
```csharp
public record StepActionResult
{
}
    public bool Success { get; init; }
    public Dictionary<string, object>? Output { get; init; }
    public string? Error { get; init; }
}
```
```csharp
public record StepResult
{
}
    public required string SagaId { get; init; }
    public int StepIndex { get; init; }
    public string? StepName { get; init; }
    public required StepStatus Status { get; init; }
    public Dictionary<string, object>? Output { get; init; }
    public string? Error { get; init; }
}
```
```csharp
public record CompensationResult
{
}
    public required string SagaId { get; init; }
    public int StepIndex { get; init; }
    public string? StepName { get; init; }
    public required CompensationStatus Status { get; init; }
    public string? Error { get; init; }
}
```
```csharp
public record SagaStatus
{
}
    public required string SagaId { get; init; }
    public required string SagaType { get; init; }
    public required SagaState State { get; init; }
    public int CurrentStep { get; init; }
    public int TotalSteps { get; init; }
    public required List<SagaEvent> Events { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
}
```
```csharp
public record EventTopic
{
}
    public required string TopicName { get; init; }
    public required TopicConfig Config { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public record TopicConfig
{
}
    public bool EnableDeadLetter { get; init; };
    public int MaxRetries { get; init; };
    public TimeSpan MessageTtl { get; init; };
}
```
```csharp
public record Subscription
{
}
    public required string SubscriberId { get; init; }
    public required string TopicName { get; init; }
    public required Func<PublishedEvent, CancellationToken, Task<bool>> Handler { get; init; }
    public required SubscriptionConfig Config { get; init; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public record SubscriptionConfig
{
}
    public int MaxRetries { get; init; };
    public int RetryDelayMs { get; init; };
    public DeliveryGuarantee Guarantee { get; init; };
}
```
```csharp
public record PublishedEvent
{
}
    public required string EventId { get; init; }
    public required string TopicName { get; init; }
    public required string EventType { get; init; }
    public required object Data { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
    public DateTime PublishedAt { get; init; }
}
```
```csharp
public record PublishResult
{
}
    public required string EventId { get; init; }
    public required string TopicName { get; init; }
    public int SubscriberCount { get; init; }
    public required List<DeliveryResult> DeliveryResults { get; init; }
}
```
```csharp
public record DeliveryResult
{
}
    public required string SubscriberId { get; init; }
    public bool Delivered { get; init; }
    public string? Error { get; init; }
}
```
```csharp
public interface IDomainEvent
{
}
    string EventId { get; }
    DateTime OccurredAt { get; }
}
```
```csharp
public record DomainEventRecord
{
}
    public required string EventId { get; init; }
    public required string EventType { get; init; }
    public string? AggregateId { get; init; }
    public required object EventData { get; init; }
    public DateTime RaisedAt { get; init; }
}
```
```csharp
public class DomainEventHandlerWrapper<TEvent> : IDomainEventHandler where TEvent : IDomainEvent
{
}
    public DomainEventHandlerWrapper(Func<TEvent, CancellationToken, Task> handler);
    public Task HandleAsync(TEvent evt, CancellationToken ct);;
}
```
```csharp
public abstract class AggregateRoot
{
}
    public IReadOnlyList<IDomainEvent> UncommittedEvents;;
    protected void RaiseEvent(IDomainEvent domainEvent);
    public void ClearUncommittedEvents();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/Financial/FixStreamStrategy.cs
```csharp
public sealed record FixMessage
{
}
    public required string BeginString { get; init; }
    public required FixMsgType MsgType { get; init; }
    public required string SenderCompId { get; init; }
    public required string TargetCompId { get; init; }
    public long MsgSeqNum { get; init; }
    public DateTimeOffset SendingTime { get; init; }
    public Dictionary<int, string> Fields { get; init; };
    public string? RawMessage { get; init; }
}
```
```csharp
public sealed record FixSessionConfig
{
}
    public required string SenderCompId { get; init; }
    public required string TargetCompId { get; init; }
    public FixVersion Version { get; init; };
    public int HeartbeatIntervalSeconds { get; init; };
    public bool ResetOnLogon { get; init; }
    public int MaxResendSize { get; init; };
    public string? Host { get; init; }
    public int Port { get; init; };
}
```
```csharp
public sealed class FixSession
{
}
    public required string SessionId { get; init; }
    public required FixSessionConfig Config { get; init; }
    public FixSessionState State { get; set; };
    public long OutgoingSeqNum = 1;
    public long IncomingSeqNum = 1;
    public BoundedDictionary<long, FixMessage> SentMessages { get; };
    public ConcurrentQueue<FixMessage> ReceivedMessages { get; };
    public DateTimeOffset LastHeartbeatSent { get; set; }
    public DateTimeOffset LastHeartbeatReceived { get; set; }
    public DateTimeOffset CreatedAt { get; init; };
}
```
```csharp
internal sealed class FixStreamStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<string> CreateSessionAsync(FixSessionConfig config, CancellationToken ct = default);
    public Task<FixMessage> LogonAsync(string sessionId, CancellationToken ct = default);
    public Task<FixMessage> LogoutAsync(string sessionId, string? text = null, CancellationToken ct = default);
    public Task<FixMessage> SendHeartbeatAsync(string sessionId, string? testReqId = null, CancellationToken ct = default);
    public Task<FixMessage> SendNewOrderSingleAsync(string sessionId, string clOrdId, string symbol, FixSide side, decimal orderQty, FixOrdType ordType, decimal? price = null, CancellationToken ct = default);
    public Task<FixMessage> SendMarketDataRequestAsync(string sessionId, string mdReqId, string[] symbols, int subscriptionType = 1, CancellationToken ct = default);
    public Task<FixMessage> SendResendRequestAsync(string sessionId, long beginSeqNo, long endSeqNo = 0, CancellationToken ct = default);
    public FixMessage ParseMessage(string rawMessage);
    public static string ComputeChecksum(string messageBody);
    public IReadOnlyList<FixMessage> ReceiveMessages(string sessionId, int maxCount = 100);
    public Task DestroySessionAsync(string sessionId, CancellationToken ct = default);
    public long TotalMessages;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/Financial/SwiftStreamStrategy.cs
```csharp
public sealed record SwiftMessage
{
}
    public required string TransactionReference { get; init; }
    public SwiftMtType MessageType { get; init; }
    public SwiftMessageCategory Category { get; init; }
    public required string SenderBic { get; init; }
    public required string ReceiverBic { get; init; }
    public char Priority { get; init; };
    public SwiftAmount? Amount { get; init; }
    public string? OrderingCustomer { get; init; }
    public string? BeneficiaryCustomer { get; init; }
    public string? RemittanceInfo { get; init; }
    public string? ChargesDetail { get; init; }
    public Dictionary<string, string> Fields { get; init; };
    public SwiftValidationStatus ValidationStatus { get; init; };
    public List<string> ValidationMessages { get; init; };
    public string? RawMessage { get; init; }
    public DateTimeOffset Timestamp { get; init; };
}
```
```csharp
public sealed record SwiftAmount
{
}
    public required string ValueDate { get; init; }
    public required string Currency { get; init; }
    public decimal Amount { get; init; }
}
```
```csharp
public sealed record SwiftDeliveryNotification
{
}
    public required string TransactionReference { get; init; }
    public SwiftDeliveryStatus Status { get; init; }
    public DateTimeOffset DeliveredAt { get; init; }
    public string? AckReference { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorDescription { get; init; }
}
```
```csharp
internal sealed class SwiftStreamStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<SwiftMessage> CreateMT103Async(string senderBic, string receiverBic, string currency, decimal amount, string orderingCustomer, string beneficiaryCustomer, string? remittanceInfo = null, string chargesDetail = "SHA", CancellationToken ct = default);
    public Task<SwiftMessage> CreateMT202Async(string senderBic, string receiverBic, string currency, decimal amount, CancellationToken ct = default);
    public IReadOnlyList<string> ValidateMessage(SwiftMessage message);
    public IReadOnlyList<SwiftMessage> DrainOutbound(string senderBic, int maxCount = 50);
    public Task<SwiftDeliveryNotification> RecordDeliveryAsync(string txRef, SwiftDeliveryStatus status, string? ackRef = null, CancellationToken ct = default);
    public static string GenerateUetr();
    public static bool IsValidBic(string bic);
    public long TotalMessages;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/Pipelines/RealTimePipelineStrategies.cs
```csharp
public sealed class RealTimeEtlPipelineStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<EtlPipeline> CreatePipelineAsync(string pipelineId, EtlSource source, IReadOnlyList<EtlTransform> transforms, EtlSink sink, EtlPipelineConfig? config = null, CancellationToken cancellationToken = default);
    public Task StartPipelineAsync(string pipelineId, CancellationToken cancellationToken = default);
    public async IAsyncEnumerable<EtlOutputRecord> ProcessBatchAsync(string pipelineId, IAsyncEnumerable<EtlInputRecord> records, [EnumeratorCancellation] CancellationToken cancellationToken = default);
    public Task<PipelineMetrics> GetMetricsAsync(string pipelineId);
    public Task StopPipelineAsync(string pipelineId, bool waitForCompletion = true, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class CdcPipelineStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<CdcConnector> CreateConnectorAsync(string connectorId, CdcConnectorType connectorType, CdcDatabaseConfig databaseConfig, CdcConnectorConfig? config = null, CancellationToken cancellationToken = default);
    public Task StartCaptureAsync(string connectorId, CancellationToken cancellationToken = default);
    public async IAsyncEnumerable<CdcEvent> ConsumeEventsAsync(string connectorId, long? fromPosition = null, [EnumeratorCancellation] CancellationToken cancellationToken = default);
    public Task<CdcEvent> SimulateChangeAsync(string connectorId, CdcOperationType operationType, string tableName, Dictionary<string, object>? before, Dictionary<string, object>? after, CancellationToken cancellationToken = default);
    public Task<SchemaVersion> RegisterSchemaAsync(string tableName, string schemaDefinition, CancellationToken cancellationToken = default);
    public Task<CdcConnectorStats> GetStatsAsync(string connectorId);
}
```
```csharp
public sealed class EventRouterPipelineStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<EventRouter> CreateRouterAsync(string routerId, EventRouterConfig? config = null, CancellationToken cancellationToken = default);
    public Task<RoutingRule> AddRoutingRuleAsync(string routerId, string ruleId, RoutingCondition condition, IReadOnlyList<string> targetTopics, int priority = 0, CancellationToken cancellationToken = default);
    public async IAsyncEnumerable<RoutedEvent> RouteEventsAsync(string routerId, IAsyncEnumerable<IncomingEvent> events, [EnumeratorCancellation] CancellationToken cancellationToken = default);
    public Task<IReadOnlyList<RoutedEvent>> GetDeadLetterEventsAsync(string routerId, int limit = 100, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class DataIntegrationHubStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<IntegrationHub> CreateHubAsync(string hubId, IntegrationHubConfig? config = null, CancellationToken cancellationToken = default);
    public Task<DataSource> RegisterSourceAsync(string hubId, string sourceId, SourceType sourceType, string connectionString, DataSourceConfig? config = null, CancellationToken cancellationToken = default);
    public Task<DataSink> RegisterSinkAsync(string hubId, string sinkId, SinkType sinkType, string connectionString, DataSinkConfig? config = null, CancellationToken cancellationToken = default);
    public async IAsyncEnumerable<IntegrationResult> ProcessDataAsync(string hubId, string sourceId, IAsyncEnumerable<IntegrationRecord> records, IReadOnlyList<string> targetSinks, [EnumeratorCancellation] CancellationToken cancellationToken = default);
    public Task<IReadOnlyList<LineageRecord>> GetLineageAsync(string hubId, string? recordId = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class StreamEnrichmentPipelineStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<EnrichmentPipeline> CreatePipelineAsync(string pipelineId, EnrichmentPipelineConfig? config = null, CancellationToken cancellationToken = default);
    public Task<EnrichmentSource> RegisterEnrichmentSourceAsync(string pipelineId, string sourceId, EnrichmentSourceType sourceType, EnrichmentSourceConfig config, CancellationToken cancellationToken = default);
    public async IAsyncEnumerable<EnrichedRecord> EnrichStreamAsync(string pipelineId, IAsyncEnumerable<BaseRecord> records, [EnumeratorCancellation] CancellationToken cancellationToken = default);
}
```
```csharp
public record EtlPipeline
{
}
    public required string PipelineId { get; init; }
    public required EtlSource Source { get; init; }
    public required List<EtlTransform> Transforms { get; init; }
    public required EtlSink Sink { get; init; }
    public required EtlPipelineConfig Config { get; init; }
    public PipelineState State { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; set; }
}
```
```csharp
public record EtlSource
{
}
    public required string SourceType { get; init; }
    public required string ConnectionString { get; init; }
    public Dictionary<string, string>? Options { get; init; }
}
```
```csharp
public record EtlTransform
{
}
    public required TransformType Type { get; init; }
    public Func<Dictionary<string, object>, Dictionary<string, object>>? MapFunction { get; init; }
    public string? EnrichmentSource { get; init; }
    public string? Schema { get; init; }
    public string? FilterPredicate { get; init; }
}
```
```csharp
public record EtlSink
{
}
    public required string SinkType { get; init; }
    public required string ConnectionString { get; init; }
    public Dictionary<string, string>? Options { get; init; }
}
```
```csharp
public record EtlPipelineConfig
{
}
    public bool EnableDeadLetterQueue { get; init; };
    public int BatchSize { get; init; };
    public int ParallelismDegree { get; init; };
    public TimeSpan CheckpointInterval { get; init; };
}
```
```csharp
public record EtlInputRecord
{
}
    public required string RecordId { get; init; }
    public required Dictionary<string, object> Data { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}
```
```csharp
public record EtlOutputRecord
{
}
    public required string RecordId { get; init; }
    public required Dictionary<string, object> Data { get; init; }
    public bool IsDeadLetter { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTime ProcessedAt { get; init; }
}
```
```csharp
public record PipelineMetrics
{
}
    public required string PipelineId { get; init; }
    public long RecordsReceived { get; set; }
    public long RecordsProcessed { get; set; }
    public long TransformErrors { get; set; }
    public double AvgLatencyMs { get; set; }
}
```
```csharp
public record CdcConnector
{
}
    public required string ConnectorId { get; init; }
    public required CdcConnectorType ConnectorType { get; init; }
    public required CdcDatabaseConfig DatabaseConfig { get; init; }
    public required CdcConnectorConfig Config { get; init; }
    public ConnectorState State { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; set; }
}
```
```csharp
public record CdcDatabaseConfig
{
}
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Database { get; init; }
    public required string Username { get; init; }
    public required string Password { get; init; }
    public List<string>? Tables { get; init; }
}
```
```csharp
public record CdcConnectorConfig
{
}
    public string? ServerId { get; init; }
    public bool SnapshotOnStart { get; init; };
    public string? OffsetStorageTopic { get; init; }
    public string? SchemaHistoryTopic { get; init; }
}
```
```csharp
public record CdcEvent
{
}
    public required string EventId { get; init; }
    public required string ConnectorId { get; init; }
    public required CdcOperationType OperationType { get; init; }
    public required string TableName { get; init; }
    public Dictionary<string, object>? Before { get; init; }
    public Dictionary<string, object>? After { get; init; }
    public long Position { get; init; }
    public DateTime Timestamp { get; init; }
}
```
```csharp
public record SchemaVersion
{
}
    public required string TableName { get; init; }
    public required int Version { get; init; }
    public required string SchemaDefinition { get; init; }
    public DateTime RegisteredAt { get; init; }
}
```
```csharp
public record CdcConnectorStats
{
}
    public required string ConnectorId { get; init; }
    public long TotalEvents { get; init; }
    public long Inserts { get; init; }
    public long Updates { get; init; }
    public long Deletes { get; init; }
    public long CurrentPosition { get; init; }
    public ConnectorState State { get; init; }
}
```
```csharp
public record EventRouter
{
}
    public required string RouterId { get; init; }
    public required EventRouterConfig Config { get; init; }
    public RouterState State { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public record EventRouterConfig
{
}
    public bool ContinueOnMatch { get; init; };
    public bool EnableDeadLetterQueue { get; init; };
}
```
```csharp
public record RoutingRule
{
}
    public required string RuleId { get; init; }
    public required string RouterId { get; init; }
    public required RoutingCondition Condition { get; init; }
    public required List<string> TargetTopics { get; init; }
    public int Priority { get; init; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public record RoutingCondition
{
}
    public required ConditionType Type { get; init; }
    public string? Key { get; init; }
    public ConditionOperator Operator { get; init; };
    public string? Value { get; init; }
}
```
```csharp
public record IncomingEvent
{
}
    public required string EventId { get; init; }
    public required string SourceTopic { get; init; }
    public string? EventType { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    public Dictionary<string, object>? Payload { get; init; }
}
```
```csharp
public record RoutedEvent
{
}
    public required string EventId { get; init; }
    public required string SourceTopic { get; init; }
    public required string TargetTopic { get; init; }
    public Dictionary<string, object>? Payload { get; init; }
    public string? MatchedRule { get; init; }
    public bool IsDeadLetter { get; init; }
    public DateTime RoutedAt { get; init; }
}
```
```csharp
public record IntegrationHub
{
}
    public required string HubId { get; init; }
    public required IntegrationHubConfig Config { get; init; }
    public HubState State { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public record IntegrationHubConfig
{
}
    public List<QualityRule>? QualityRules { get; init; }
    public bool RejectInvalidRecords { get; init; };
    public bool EnableLineageTracking { get; init; };
}
```
```csharp
public record QualityRule
{
}
    public required string RuleName { get; init; }
    public required QualityRuleType Type { get; init; }
    public string? FieldName { get; init; }
    public string? Pattern { get; init; }
    public object? MinValue { get; init; }
    public object? MaxValue { get; init; }
}
```
```csharp
public record DataSource
{
}
    public required string SourceId { get; init; }
    public required string HubId { get; init; }
    public required SourceType SourceType { get; init; }
    public required string ConnectionString { get; init; }
    public required DataSourceConfig Config { get; init; }
    public SourceState State { get; set; }
    public DateTime RegisteredAt { get; init; }
}
```
```csharp
public record DataSourceConfig
{
}
    public Dictionary<string, string>? SchemaMapping { get; init; }
    public string? Query { get; init; }
    public int BatchSize { get; init; };
}
```
```csharp
public record DataSink
{
}
    public required string SinkId { get; init; }
    public required string HubId { get; init; }
    public required SinkType SinkType { get; init; }
    public required string ConnectionString { get; init; }
    public required DataSinkConfig Config { get; init; }
    public SinkState State { get; set; }
    public DateTime RegisteredAt { get; init; }
}
```
```csharp
public record DataSinkConfig
{
}
    public string? TableName { get; init; }
    public WriteMode WriteMode { get; init; };
    public int BatchSize { get; init; };
}
```
```csharp
public record IntegrationRecord
{
}
    public required string RecordId { get; init; }
    public Dictionary<string, object>? Data { get; init; }
}
```
```csharp
public record IntegrationResult
{
}
    public required string RecordId { get; init; }
    public required IntegrationStatus Status { get; init; }
    public Dictionary<string, object>? MappedData { get; init; }
    public List<string>? TargetSinks { get; init; }
    public List<string>? QualityErrors { get; init; }
    public string? LineageId { get; init; }
    public DateTime ProcessedAt { get; init; }
}
```
```csharp
public record QualityValidationResult
{
}
    public bool IsValid { get; init; }
    public List<string>? Errors { get; init; }
}
```
```csharp
public record LineageRecord
{
}
    public required string RecordId { get; init; }
    public required string SourceId { get; init; }
    public required List<string> TargetSinks { get; init; }
    public required List<string> TransformationsApplied { get; init; }
    public DateTime ProcessedAt { get; init; }
}
```
```csharp
public record EnrichmentPipeline
{
}
    public required string PipelineId { get; init; }
    public required EnrichmentPipelineConfig Config { get; init; }
    public PipelineState State { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public record EnrichmentPipelineConfig
{
}
    public int MaxConcurrentLookups { get; init; };
    public TimeSpan LookupTimeout { get; init; };
    public bool FailOnEnrichmentError { get; init; };
}
```
```csharp
public record EnrichmentSource
{
}
    public required string SourceId { get; init; }
    public required string PipelineId { get; init; }
    public required EnrichmentSourceType SourceType { get; init; }
    public required EnrichmentSourceConfig Config { get; init; }
    public bool IsActive { get; set; }
}
```
```csharp
public record EnrichmentSourceConfig
{
}
    public string? ConnectionString { get; init; }
    public string? Endpoint { get; init; }
    public string? KeyField { get; init; }
    public string? ModelId { get; init; }
    public int CacheTtlSeconds { get; init; };
    public Dictionary<string, object>? StaticValues { get; init; }
}
```
```csharp
public record BaseRecord
{
}
    public required string RecordId { get; init; }
    public Dictionary<string, object>? Data { get; init; }
}
```
```csharp
public record EnrichedRecord
{
}
    public required string RecordId { get; init; }
    public Dictionary<string, object>? OriginalData { get; init; }
    public required Dictionary<string, object> EnrichedData { get; init; }
    public required List<EnrichmentResult> EnrichmentResults { get; init; }
    public DateTime ProcessedAt { get; init; }
}
```
```csharp
public record EnrichmentResult
{
}
    public required string SourceId { get; init; }
    public required EnrichmentSourceType SourceType { get; init; }
    public bool Success { get; set; }
    public Dictionary<string, object>? EnrichedFields { get; set; }
    public bool FromCache { get; set; }
    public string? ErrorMessage { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/Windowing/WindowingStrategies.cs
```csharp
public sealed class TumblingWindowStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public TumblingWindowSpec CreateWindow(TimeSpan size, TimeSpan? allowedLateness = null);
    public WindowAssignment AssignWindow(TumblingWindowSpec spec, DateTimeOffset eventTime);
    public async IAsyncEnumerable<WindowResult<TResult>> ProcessAsync<TEvent, TResult>(IAsyncEnumerable<TEvent> events, TumblingWindowSpec spec, Func<TEvent, DateTimeOffset> eventTimeExtractor, Func<IEnumerable<TEvent>, TResult> aggregator, [EnumeratorCancellation] CancellationToken ct = default)
    where TEvent : class;
}
```
```csharp
public sealed record TumblingWindowSpec
{
}
    public TimeSpan Size { get; init; }
    public TimeSpan AllowedLateness { get; init; }
}
```
```csharp
public sealed class SlidingWindowStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SlidingWindowSpec CreateWindow(TimeSpan size, TimeSpan slide, TimeSpan? allowedLateness = null);
    public IReadOnlyList<WindowAssignment> AssignWindows(SlidingWindowSpec spec, DateTimeOffset eventTime);
    public async IAsyncEnumerable<WindowResult<TResult>> ProcessAsync<TEvent, TResult>(IAsyncEnumerable<TEvent> events, SlidingWindowSpec spec, Func<TEvent, DateTimeOffset> eventTimeExtractor, Func<IEnumerable<TEvent>, TResult> aggregator, [EnumeratorCancellation] CancellationToken ct = default)
    where TEvent : class;
}
```
```csharp
public sealed record SlidingWindowSpec
{
}
    public TimeSpan Size { get; init; }
    public TimeSpan Slide { get; init; }
    public TimeSpan AllowedLateness { get; init; }
}
```
```csharp
public sealed class SessionWindowStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SessionWindowSpec CreateWindow(TimeSpan gap, TimeSpan? maxDuration = null, TimeSpan? allowedLateness = null);
    public async IAsyncEnumerable<WindowResult<TResult>> ProcessAsync<TEvent, TKey, TResult>(IAsyncEnumerable<TEvent> events, SessionWindowSpec spec, Func<TEvent, TKey> keyExtractor, Func<TEvent, DateTimeOffset> eventTimeExtractor, Func<IEnumerable<TEvent>, TResult> aggregator, [EnumeratorCancellation] CancellationToken ct = default)
    where TEvent : class where TKey : notnull;
}
```
```csharp
public sealed record SessionWindowSpec
{
}
    public TimeSpan Gap { get; init; }
    public TimeSpan? MaxDuration { get; init; }
    public TimeSpan AllowedLateness { get; init; }
}
```
```csharp
internal sealed class SessionState<TEvent>
{
}
    public required string SessionId { get; init; }
    public required object Key { get; init; }
    public required List<TEvent> Events { get; init; }
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset LastEventTime { get; set; }
}
```
```csharp
public sealed class GlobalWindowStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public GlobalWindowSpec CreateCountTriggeredWindow(int triggerCount);
    public GlobalWindowSpec CreateTimeTriggeredWindow(TimeSpan triggerInterval);
    public async IAsyncEnumerable<WindowResult<TResult>> ProcessAsync<TEvent, TKey, TResult>(IAsyncEnumerable<TEvent> events, GlobalWindowSpec spec, Func<TEvent, TKey> keyExtractor, Func<IEnumerable<TEvent>, TResult> aggregator, [EnumeratorCancellation] CancellationToken ct = default)
    where TEvent : class where TKey : notnull;
}
```
```csharp
public sealed record GlobalWindowSpec
{
}
    public GlobalTriggerType TriggerType { get; init; }
    public int TriggerCount { get; init; }
    public TimeSpan? TriggerInterval { get; init; }
}
```
```csharp
internal sealed class GlobalWindowState<TEvent>
{
}
    public required object Key { get; init; }
    public required List<TEvent> Events { get; init; }
    public int WindowNumber { get; set; }
    public DateTimeOffset LastTriggerTime { get; set; }
}
```
```csharp
public sealed class CountWindowStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public CountWindowSpec CreateWindow(int count, int? slide = null);
    public async IAsyncEnumerable<WindowResult<TResult>> ProcessAsync<TEvent, TKey, TResult>(IAsyncEnumerable<TEvent> events, CountWindowSpec spec, Func<TEvent, TKey> keyExtractor, Func<IEnumerable<TEvent>, TResult> aggregator, [EnumeratorCancellation] CancellationToken ct = default)
    where TEvent : class where TKey : notnull;
}
```
```csharp
public sealed record CountWindowSpec
{
}
    public int Count { get; init; }
    public int Slide { get; init; }
}
```
```csharp
internal sealed class CountWindowState<TEvent>
{
}
    public required object Key { get; init; }
    public required List<TEvent> Events { get; init; }
    public int WindowNumber { get; set; }
}
```
```csharp
public sealed record WindowAssignment
{
}
    public required string WindowId { get; init; }
    public DateTimeOffset WindowStart { get; init; }
    public DateTimeOffset WindowEnd { get; init; }
    public DateTimeOffset EventTime { get; init; }
}
```
```csharp
public sealed record WindowResult<TResult>
{
}
    public required string WindowId { get; init; }
    public DateTimeOffset WindowStart { get; init; }
    public DateTimeOffset WindowEnd { get; init; }
    public required TResult Result { get; init; }
    public int EventCount { get; init; }
    public DateTimeOffset EmittedAt { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}
```
```csharp
internal sealed class WindowState
{
}
    public required string WindowId { get; init; }
    public DateTimeOffset WindowStart { get; init; }
    public DateTimeOffset WindowEnd { get; init; }
    public List<object> Events { get; };
    public bool IsClosed { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/Analytics/StreamAnalyticsStrategies.cs
```csharp
public sealed class RealTimeAggregationStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<AggregationJob> CreateJobAsync(string jobId, AggregationDefinition definition, AggregationJobConfig? config = null, CancellationToken cancellationToken = default);
    public async IAsyncEnumerable<AggregationResult> ProcessEventsAsync(string jobId, IAsyncEnumerable<StreamEvent> events, [EnumeratorCancellation] CancellationToken cancellationToken = default);
    public Task<IReadOnlyDictionary<string, AggregateValue>> GetAggregatesAsync(string jobId, CancellationToken cancellationToken = default);
    public Task ResetStateAsync(string jobId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class ComplexEventProcessingStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<CepEngine> CreateEngineAsync(string engineId, CepEngineConfig? config = null, CancellationToken cancellationToken = default);
    public Task<CepPattern> RegisterPatternAsync(string engineId, string patternId, PatternDefinition definition, CancellationToken cancellationToken = default);
    public async IAsyncEnumerable<PatternMatch> DetectPatternsAsync(string engineId, IAsyncEnumerable<CepEvent> events, [EnumeratorCancellation] CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class MlInferenceStreamingStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<MlModel> RegisterModelAsync(string modelId, ModelType modelType, string modelPath, ModelConfig? config = null, CancellationToken cancellationToken = default);
    public Task<InferenceJob> CreateJobAsync(string jobId, string modelId, InferenceJobConfig? config = null, CancellationToken cancellationToken = default);
    public async IAsyncEnumerable<InferenceResult> InferStreamAsync(string jobId, IAsyncEnumerable<InferenceRequest> requests, [EnumeratorCancellation] CancellationToken cancellationToken = default);
    public Task<ModelMetrics> GetMetricsAsync(string modelId);
}
```
```csharp
public sealed class TimeSeriesAnalyticsStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<TimeSeriesAnalyzer> CreateAnalyzerAsync(string analyzerId, TimeSeriesConfig? config = null, CancellationToken cancellationToken = default);
    public async IAsyncEnumerable<TimeSeriesResult> AnalyzeStreamAsync(string analyzerId, IAsyncEnumerable<TimeSeriesPoint> points, [EnumeratorCancellation] CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class StreamingSqlQueryStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<StreamTable> RegisterTableAsync(string tableName, TableSchema schema, CancellationToken cancellationToken = default);
    public Task<StreamingQuery> CreateQueryAsync(string queryId, string sqlQuery, StreamingQueryConfig? config = null, CancellationToken cancellationToken = default);
    public async IAsyncEnumerable<QueryResult> ExecuteQueryAsync(string queryId, IAsyncEnumerable<StreamRow> inputRows, [EnumeratorCancellation] CancellationToken cancellationToken = default);
}
```
```csharp
public record AggregationJob
{
}
    public required string JobId { get; init; }
    public required AggregationDefinition Definition { get; init; }
    public required AggregationJobConfig Config { get; init; }
    public JobState State { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public record AggregationDefinition
{
}
    public List<string>? GroupByFields { get; init; }
    public string? ValueField { get; init; }
    public List<AggregateType>? Aggregations { get; init; }
}
```
```csharp
public record AggregationJobConfig
{
}
    public TriggerMode TriggerMode { get; init; };
    public int EmitEveryN { get; init; };
    public TimeSpan EmitInterval { get; init; };
}
```
```csharp
public record AggregationState
{
}
    public required string JobId { get; init; }
    public required BoundedDictionary<string, AggregateValue> Aggregates { get; init; }
}
```
```csharp
public class AggregateValue
{
}
    public required string GroupKey { get; init; }
    public long Count { get; set; }
    public double Sum { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public DateTime? WindowStart { get; set; }
    public DateTime LastUpdated { get; set; }
    public DateTime LastEmitted { get; set; }
}
```
```csharp
public record StreamEvent
{
}
    public required string EventId { get; init; }
    public required DateTime Timestamp { get; init; }
    public Dictionary<string, object>? Data { get; init; }
}
```
```csharp
public record AggregationResult
{
}
    public required string JobId { get; init; }
    public required string GroupKey { get; init; }
    public long Count { get; init; }
    public double Sum { get; init; }
    public double Avg { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public DateTime? WindowStart { get; init; }
    public DateTime? WindowEnd { get; init; }
    public DateTime EmittedAt { get; init; }
}
```
```csharp
public record CepEngine
{
}
    public required string EngineId { get; init; }
    public required CepEngineConfig Config { get; init; }
    public EngineState State { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public record CepEngineConfig
{
}
    public TimeSpan MatchTimeout { get; init; };
    public int MaxPartialMatches { get; init; };
}
```
```csharp
public record CepPattern
{
}
    public required string PatternId { get; init; }
    public required string EngineId { get; init; }
    public required PatternDefinition Definition { get; init; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public record PatternDefinition
{
}
    public required List<PatternStep> Steps { get; init; }
    public TimeSpan? WithinTime { get; init; }
}
```
```csharp
public record PatternStep
{
}
    public required string StepName { get; init; }
    public required PatternCondition Condition { get; init; }
    public Quantifier Quantifier { get; init; };
}
```
```csharp
public record PatternCondition
{
}
    public string? EventType { get; init; }
    public List<FieldCondition>? FieldConditions { get; init; }
}
```
```csharp
public record FieldCondition
{
}
    public required string FieldName { get; init; }
    public required ComparisonOperator Operator { get; init; }
    public object? Value { get; init; }
}
```
```csharp
public record CepEvent
{
}
    public required string EventId { get; init; }
    public required string EventType { get; init; }
    public required DateTime Timestamp { get; init; }
    public Dictionary<string, object>? Data { get; init; }
}
```
```csharp
public class PartialMatch
{
}
    public required string PatternId { get; init; }
    public required List<CepEvent> Events { get; init; }
    public int CurrentStep { get; init; }
    public DateTime StartedAt { get; init; }
}
```
```csharp
public record PatternMatch
{
}
    public required string PatternId { get; init; }
    public required string EngineId { get; init; }
    public required List<CepEvent> MatchedEvents { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public DateTime MatchedAt { get; init; }
}
```
```csharp
public record MlModel
{
}
    public required string ModelId { get; init; }
    public required ModelType ModelType { get; init; }
    public required string ModelPath { get; init; }
    public required ModelConfig Config { get; init; }
    public ModelState State { get; set; }
    public DateTime LoadedAt { get; init; }
}
```
```csharp
public record ModelConfig
{
}
    public int BatchSize { get; init; };
    public bool UseGpu { get; init; };
    public Dictionary<string, FeatureTransform>? FeatureTransforms { get; init; }
}
```
```csharp
public record FeatureTransform
{
}
    public required TransformType Type { get; init; }
    public double Mean { get; init; }
    public double Std { get; init; };
    public double Min { get; init; }
    public double Max { get; init; };
}
```
```csharp
public record InferenceJob
{
}
    public required string JobId { get; init; }
    public required string ModelId { get; init; }
    public required InferenceJobConfig Config { get; init; }
    public JobState State { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public record InferenceJobConfig
{
}
    public int MaxConcurrency { get; init; };
    public TimeSpan Timeout { get; init; };
}
```
```csharp
public record InferenceRequest
{
}
    public required string RequestId { get; init; }
    public Dictionary<string, object>? Features { get; init; }
}
```
```csharp
public record InferenceResult
{
}
    public required string RequestId { get; init; }
    public required string ModelId { get; init; }
    public Prediction? Prediction { get; init; }
    public double? Confidence { get; init; }
    public double LatencyMs { get; init; }
    public string? Error { get; init; }
    public DateTime InferredAt { get; init; }
}
```
```csharp
public record Prediction
{
}
    public required PredictionType Type { get; init; }
    public string? Label { get; init; }
    public double? Value { get; init; }
    public int? ClusterId { get; init; }
    public bool? IsAnomaly { get; init; }
    public double? AnomalyScore { get; init; }
    public double Confidence { get; init; }
    public Dictionary<string, double>? Probabilities { get; init; }
}
```
```csharp
public class ModelMetrics
{
}
    public required string ModelId { get; init; }
    public long TotalPredictions;
    public long TotalErrors;
    public double AvgLatencyMs { get; set; }
}
```
```csharp
public record TimeSeriesAnalyzer
{
}
    public required string AnalyzerId { get; init; }
    public required TimeSeriesConfig Config { get; init; }
    public AnalyzerState State { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public record TimeSeriesConfig
{
}
    public int BufferSize { get; init; };
    public int MinDataPoints { get; init; };
    public int TrendWindow { get; init; };
    public int SeasonalPeriod { get; init; };
    public double AnomalyThreshold { get; init; };
}
```
```csharp
public record TimeSeriesPoint
{
}
    public required DateTime Timestamp { get; init; }
    public required double Value { get; init; }
    public Dictionary<string, object>? Labels { get; init; }
}
```
```csharp
public record TimeSeriesResult
{
}
    public required string AnalyzerId { get; init; }
    public required DateTime Timestamp { get; init; }
    public double CurrentValue { get; init; }
    public double Trend { get; init; }
    public double SeasonalComponent { get; init; }
    public double Residual { get; init; }
    public bool IsAnomaly { get; init; }
    public double AnomalyScore { get; init; }
    public double Forecast { get; init; }
    public (double Lower, double Upper) ConfidenceInterval { get; init; }
}
```
```csharp
public record TimeSeriesAnalysis
{
}
    public double Trend { get; init; }
    public double SeasonalComponent { get; init; }
    public double Residual { get; init; }
    public bool IsAnomaly { get; init; }
    public double AnomalyScore { get; init; }
    public double Forecast { get; init; }
    public (double Lower, double Upper) ConfidenceInterval { get; init; }
}
```
```csharp
public record StreamingQuery
{
}
    public required string QueryId { get; init; }
    public required string SqlQuery { get; init; }
    public required ParsedQuery ParsedQuery { get; init; }
    public required StreamingQueryConfig Config { get; init; }
    public QueryState State { get; set; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public record StreamingQueryConfig
{
}
    public TimeSpan EmitDelay { get; init; };
    public bool AllowLateEvents { get; init; };
    public TimeSpan LateEventThreshold { get; init; };
}
```
```csharp
public class ParsedQuery
{
}
    public required List<SelectColumn> SelectColumns { get; init; }
    public string FromTable { get; set; };
    public string? WhereClause { get; set; }
    public List<string> GroupByColumns { get; set; };
    public WindowSpec? WindowSpec { get; set; }
}
```
```csharp
public record SelectColumn
{
}
    public required string Expression { get; init; }
    public string? SourceColumn { get; init; }
    public string? Alias { get; init; }
    public AggregateFunction? AggregateFunction { get; init; }
}
```
```csharp
public record WindowSpec
{
}
    public required WindowType Type { get; init; }
    public required TimeSpan Size { get; init; }
    public TimeSpan? Slide { get; init; }
    public TimeSpan? Gap { get; init; }
}
```
```csharp
public record StreamTable
{
}
    public required string TableName { get; init; }
    public required TableSchema Schema { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public record TableSchema
{
}
    public required List<ColumnDefinition> Columns { get; init; }
    public string? TimestampColumn { get; init; }
    public List<string>? KeyColumns { get; init; }
}
```
```csharp
public record ColumnDefinition
{
}
    public required string Name { get; init; }
    public required ColumnType Type { get; init; }
    public bool Nullable { get; init; };
}
```
```csharp
public record StreamRow
{
}
    public required DateTime Timestamp { get; init; }
    public required Dictionary<string, object> Data { get; init; }
}
```
```csharp
public record QueryResult
{
}
    public required string QueryId { get; init; }
    public required Dictionary<string, object> Data { get; init; }
    public DateTime? WindowStart { get; init; }
    public DateTime? WindowEnd { get; init; }
    public DateTime EmittedAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/FaultTolerance/FaultToleranceStrategies.cs
```csharp
public sealed class CheckpointStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<string> CreateCoordinatorAsync(string jobId, CheckpointConfig config, CancellationToken ct = default);
    public async Task<CheckpointMetadata> TriggerCheckpointAsync(string coordinatorId, CancellationToken ct = default);
    public Task AcknowledgeCheckpointAsync(string coordinatorId, long checkpointId, string operatorId, byte[] stateHandle, CancellationToken ct = default);
    public Task<RestoreResult> RestoreFromCheckpointAsync(string coordinatorId, long? checkpointId = null, CancellationToken ct = default);
    public Task<IReadOnlyList<CheckpointMetadata>> GetCheckpointHistoryAsync(string coordinatorId, int limit = 10, CancellationToken ct = default);
    public Task<CheckpointStats> GetCheckpointStatsAsync(string coordinatorId, CancellationToken ct = default);
}
```
```csharp
public sealed record CheckpointConfig
{
}
    public TimeSpan Interval { get; init; };
    public TimeSpan Timeout { get; init; };
    public int MinPauseBetweenCheckpoints { get; init; };
    public int MaxConcurrentCheckpoints { get; init; };
    public CheckpointingMode Mode { get; init; };
    public bool UnalignedCheckpoints { get; init; }
    public string? ExternalizedCheckpointPath { get; init; }
}
```
```csharp
internal sealed class CheckpointCoordinator
{
}
    public required string CoordinatorId { get; init; }
    public required string JobId { get; init; }
    public required CheckpointConfig Config { get; init; }
    public required BoundedDictionary<long, CheckpointMetadata> Checkpoints { get; init; }
    public CoordinatorStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public long NextCheckpointId;
    public long CurrentCheckpointId;
    public long LastCompletedCheckpointId;
}
```
```csharp
public sealed class CheckpointMetadata
{
}
    public long CheckpointId { get; init; }
    public required string JobId { get; init; }
    public CheckpointStatus Status { get; set; }
    public DateTimeOffset TriggerTimestamp { get; init; }
    public DateTimeOffset? CompletionTimestamp { get; set; }
    public TimeSpan? Duration { get; set; }
    public long StateSize { get; set; }
    public BoundedDictionary<string, OperatorCheckpoint> Operators { get; init; };
    public string? FailureReason { get; set; }
}
```
```csharp
public sealed record OperatorCheckpoint
{
}
    public required string OperatorId { get; init; }
    public required byte[] StateHandle { get; init; }
    public DateTimeOffset AckedAt { get; init; }
}
```
```csharp
public sealed record RestoreResult
{
}
    public bool Success { get; init; }
    public long RestoredCheckpointId { get; init; }
    public DateTimeOffset RestoredAt { get; init; }
    public int OperatorsRestored { get; init; }
    public string? Error { get; init; }
}
```
```csharp
public sealed record CheckpointStats
{
}
    public int TotalCheckpoints { get; init; }
    public int CompletedCheckpoints { get; init; }
    public int FailedCheckpoints { get; init; }
    public double AverageDurationMs { get; init; }
    public long AverageStateSizeBytes { get; init; }
    public long LastCheckpointId { get; init; }
}
```
```csharp
public sealed class RestartStrategyHandler : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<string> CreateTrackerAsync(string taskId, RestartStrategyConfig config, CancellationToken ct = default);
    public Task<RestartDecision> ShouldRestartAsync(string trackerId, Exception failure, CancellationToken ct = default);
    public Task RecordRestartAsync(string trackerId, bool successful, string? reason = null, CancellationToken ct = default);
    public Task ResetRestartCounterAsync(string trackerId, CancellationToken ct = default);
    public Task<RestartStats> GetRestartStatsAsync(string trackerId, CancellationToken ct = default);
}
```
```csharp
public sealed record RestartStrategyConfig
{
}
    public RestartStrategyType Strategy { get; init; };
    public int MaxRestartAttempts { get; init; };
    public TimeSpan RestartDelay { get; init; };
    public TimeSpan InitialBackoff { get; init; };
    public double BackoffMultiplier { get; init; };
    public TimeSpan? MaxBackoff { get; init; };
    public TimeSpan FailureRateInterval { get; init; };
    public int MaxFailuresPerInterval { get; init; };
}
```
```csharp
internal sealed class RestartTracker
{
}
    public required string TrackerId { get; init; }
    public required string TaskId { get; init; }
    public required RestartStrategyConfig Config { get; init; }
    public required List<RestartAttempt> RestartHistory { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public int RestartCount;
    public DateTimeOffset? LastSuccessfulRestart;
}
```
```csharp
public sealed record RestartDecision
{
}
    public bool ShouldRestart { get; init; }
    public TimeSpan? Delay { get; init; }
    public required string Reason { get; init; }
}
```
```csharp
public sealed record RestartAttempt
{
}
    public int AttemptNumber { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public bool Successful { get; init; }
    public string? Reason { get; init; }
}
```
```csharp
public sealed record RestartStats
{
}
    public int TotalRestarts { get; init; }
    public int SuccessfulRestarts { get; init; }
    public int FailedRestarts { get; init; }
    public DateTimeOffset? LastRestartAt { get; init; }
    public DateTimeOffset? LastSuccessfulRestartAt { get; init; }
}
```
```csharp
public sealed class WriteAheadLogStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<string> CreateWalAsync(string name, WalConfig? config = null, CancellationToken ct = default);
    public Task<long> AppendAsync(string walId, byte[] data, WalEntryType entryType = WalEntryType.Data, CancellationToken ct = default);
    public Task<long> AppendBatchAsync(string walId, IEnumerable<byte[]> entries, CancellationToken ct = default);
    public Task<IReadOnlyList<WalEntry>> ReadAsync(string walId, long fromLsn = 0, int limit = 1000, CancellationToken ct = default);
    public Task TruncateAsync(string walId, long upToLsn, CancellationToken ct = default);
    public Task SyncAsync(string walId, CancellationToken ct = default);
    public Task<WalStats> GetStatsAsync(string walId, CancellationToken ct = default);
}
```
```csharp
public sealed record WalConfig
{
}
    public WalSyncMode SyncMode { get; init; };
    public TimeSpan SyncInterval { get; init; };
    public int MaxSegmentSizeBytes { get; init; };
    public int MaxRetainedSegments { get; init; };
}
```
```csharp
internal sealed class WalStore
{
}
    public required string WalId { get; init; }
    public required string Name { get; init; }
    public required WalConfig Config { get; init; }
    public required List<WalEntry> Entries { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public long NextLsn;
}
```
```csharp
public sealed record WalEntry
{
}
    public long Lsn { get; init; }
    public required byte[] Data { get; init; }
    public WalEntryType Type { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public uint Checksum { get; init; }
}
```
```csharp
public sealed record WalStats
{
}
    public int EntryCount { get; init; }
    public long TotalSizeBytes { get; init; }
    public long MinLsn { get; init; }
    public long MaxLsn { get; init; }
    public long CurrentLsn { get; init; }
}
```
```csharp
public sealed class IdempotentProcessingStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<string> CreateStoreAsync(string name, IdempotencyConfig? config = null, CancellationToken ct = default);
    public Task<IdempotencyResult> TryProcessAsync(string storeId, string messageId, CancellationToken ct = default);
    public Task MarkProcessedAsync(string storeId, string messageId, byte[]? result = null, CancellationToken ct = default);
    public Task<byte[]?> GetCachedResultAsync(string storeId, string messageId, CancellationToken ct = default);
    public Task CleanupAsync(string storeId, CancellationToken ct = default);
    public Task<IdempotencyStats> GetStatsAsync(string storeId, CancellationToken ct = default);
}
```
```csharp
public sealed record IdempotencyConfig
{
}
    public TimeSpan RetentionPeriod { get; init; };
    public int MaxTrackedMessages { get; init; };
    public bool CacheResults { get; init; };
}
```
```csharp
internal sealed class IdempotencyStore
{
}
    public required string StoreId { get; init; }
    public required string Name { get; init; }
    public required IdempotencyConfig Config { get; init; }
    public required BoundedDictionary<string, ProcessedRecord> ProcessedIds { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
```
```csharp
internal sealed class ProcessedRecord
{
}
    public required string MessageId { get; init; }
    public DateTimeOffset ProcessedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public byte[]? Result { get; init; }
    public long DuplicateCount;
}
```
```csharp
public sealed record IdempotencyResult
{
}
    public bool IsNew { get; init; }
    public required string MessageId { get; init; }
    public DateTimeOffset FirstProcessedAt { get; init; }
    public long DuplicateCount { get; init; }
}
```
```csharp
public sealed record IdempotencyStats
{
}
    public int TotalTracked { get; init; }
    public long TotalDuplicates { get; init; }
    public DateTimeOffset? OldestRecord { get; init; }
    public DateTimeOffset? NewestRecord { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/MessageQueue/KafkaStreamStrategy.cs
```csharp
internal sealed class KafkaStreamStrategy : StreamingDataStrategyBase, IStreamingStrategy
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public IReadOnlyList<string> SupportedProtocols;;
    public async Task<IReadOnlyList<PublishResult>> PublishBatchAsync(string streamName, IEnumerable<StreamMessage> messages, CancellationToken ct = default);
    public async Task<PublishResult> PublishAsync(string streamName, StreamMessage message, CancellationToken ct = default);
    public async IAsyncEnumerable<StreamMessage> SubscribeAsync(string streamName, ConsumerGroup? consumerGroup = null, SubscriptionOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default);
    public Task CreateStreamAsync(string streamName, StreamConfiguration? config = null, CancellationToken ct = default);
    public Task DeleteStreamAsync(string streamName, CancellationToken ct = default);
    public Task<bool> StreamExistsAsync(string streamName, CancellationToken ct = default);
    public async IAsyncEnumerable<string> ListStreamsAsync([EnumeratorCancellation] CancellationToken ct = default);
    public Task<StreamInfo> GetStreamInfoAsync(string streamName, CancellationToken ct = default);
    public Task CommitOffsetAsync(string streamName, ConsumerGroup consumerGroup, StreamOffset offset, CancellationToken ct = default);
    public Task SeekAsync(string streamName, ConsumerGroup consumerGroup, StreamOffset offset, CancellationToken ct = default);
    public Task<StreamOffset> GetOffsetAsync(string streamName, ConsumerGroup consumerGroup, CancellationToken ct = default);
    public override void ConfigureIntelligence(IMessageBus? messageBus);
    public long TotalPublished;;
    public long TotalConsumed;;
}
```
```csharp
private sealed record KafkaTopicState
{
}
    public required string Name { get; init; }
    public int PartitionCount { get; init; };
    public int ReplicationFactor { get; init; };
    public TimeSpan RetentionPeriod { get; init; };
    public long MaxSizeBytes { get; init; };
    public string CompressionType { get; init; };
    public DateTime CreatedAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/MessageQueue/PulsarStreamStrategy.cs
```csharp
internal sealed class PulsarStreamStrategy : StreamingDataStrategyBase, IStreamingStrategy
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public IReadOnlyList<string> SupportedProtocols;;
    public async Task<IReadOnlyList<PublishResult>> PublishBatchAsync(string streamName, IEnumerable<StreamMessage> messages, CancellationToken ct = default);
    public async Task<PublishResult> PublishAsync(string streamName, StreamMessage message, CancellationToken ct = default);
    public async IAsyncEnumerable<StreamMessage> SubscribeAsync(string streamName, ConsumerGroup? consumerGroup = null, SubscriptionOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default);
    public Task CreateStreamAsync(string streamName, StreamConfiguration? config = null, CancellationToken ct = default);
    public Task DeleteStreamAsync(string streamName, CancellationToken ct = default);
    public Task<bool> StreamExistsAsync(string streamName, CancellationToken ct = default);
    public async IAsyncEnumerable<string> ListStreamsAsync([EnumeratorCancellation] CancellationToken ct = default);
    public Task<StreamInfo> GetStreamInfoAsync(string streamName, CancellationToken ct = default);
    public Task CommitOffsetAsync(string streamName, ConsumerGroup consumerGroup, StreamOffset offset, CancellationToken ct = default);
    public Task SeekAsync(string streamName, ConsumerGroup consumerGroup, StreamOffset offset, CancellationToken ct = default);
    public Task<StreamOffset> GetOffsetAsync(string streamName, ConsumerGroup consumerGroup, CancellationToken ct = default);
    public override void ConfigureIntelligence(IMessageBus? messageBus);
}
```
```csharp
private sealed record PulsarTopicState
{
}
    public required string Name { get; init; }
    public int PartitionCount { get; init; };
    public int ReplicationFactor { get; init; };
    public TimeSpan? RetentionPeriod { get; init; }
    public long? MaxSizeBytes { get; init; }
    public string Tenant { get; init; };
    public string Namespace { get; init; };
    public DateTime CreatedAt { get; init; }
}
```
```csharp
private sealed class PulsarSubscriptionState
{
}
    public required string SubscriptionName { get; init; }
    public PulsarSubscriptionMode Mode { get; init; }
    public long CurrentOffset { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/MessageQueue/RabbitMqStreamStrategy.cs
```csharp
internal sealed class RabbitMqStreamStrategy : StreamingDataStrategyBase, IStreamingStrategy
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public IReadOnlyList<string> SupportedProtocols;;
    public async Task<IReadOnlyList<PublishResult>> PublishBatchAsync(string streamName, IEnumerable<StreamMessage> messages, CancellationToken ct = default);
    public async Task<PublishResult> PublishAsync(string streamName, StreamMessage message, CancellationToken ct = default);
    public async IAsyncEnumerable<StreamMessage> SubscribeAsync(string streamName, ConsumerGroup? consumerGroup = null, SubscriptionOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default);
    public Task CreateStreamAsync(string streamName, StreamConfiguration? config = null, CancellationToken ct = default);
    public Task DeleteStreamAsync(string streamName, CancellationToken ct = default);
    public Task<bool> StreamExistsAsync(string streamName, CancellationToken ct = default);
    public async IAsyncEnumerable<string> ListStreamsAsync([EnumeratorCancellation] CancellationToken ct = default);
    public Task<StreamInfo> GetStreamInfoAsync(string streamName, CancellationToken ct = default);
    public Task CommitOffsetAsync(string streamName, ConsumerGroup consumerGroup, StreamOffset offset, CancellationToken ct = default);
    public Task SeekAsync(string streamName, ConsumerGroup consumerGroup, StreamOffset offset, CancellationToken ct = default);
    public Task<StreamOffset> GetOffsetAsync(string streamName, ConsumerGroup consumerGroup, CancellationToken ct = default);
    public override void ConfigureIntelligence(IMessageBus? messageBus);
}
```
```csharp
private sealed record RabbitExchangeState
{
}
    public required string Name { get; init; }
    public RabbitExchangeType Type { get; init; }
    public bool Durable { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
private sealed record RabbitQueueState
{
}
    public required string Name { get; init; }
    public bool Durable { get; init; }
    public long? MaxLength { get; init; }
    public TimeSpan? MessageTtl { get; init; }
    public string? DeadLetterExchange { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
private sealed record QueueBinding
{
}
    public required string QueueName { get; init; }
    public required string RoutingKey { get; init; }
    public Dictionary<string, object>? Arguments { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/MessageQueue/KafkaAdvancedFeatures.cs
```csharp
public sealed class KafkaConsumerGroupManager
{
}
    public ConsumerGroupJoinResult JoinGroup(string groupId, string memberId, string? instanceId = null, IReadOnlyList<string>? subscribedTopics = null);
    public void LeaveGroup(string groupId, string memberId);
    public void Heartbeat(string groupId, string memberId);
    public ConsumerGroupInfo? GetGroupInfo(string groupId);
}
```
```csharp
public sealed class ConsumerGroupState
{
}
    public required string GroupId { get; init; }
    public GroupState State { get; set; }
    public int GenerationId { get; set; }
    public string? LeaderId { get; set; }
    public string ProtocolType { get; init; };
}
```
```csharp
public sealed class ConsumerMemberState
{
}
    public required string MemberId { get; init; }
    public string? GroupInstanceId { get; init; }
    public IReadOnlyList<string> SubscribedTopics { get; init; };
    public DateTime JoinedAt { get; init; }
    public DateTime LastHeartbeat { get; set; }
}
```
```csharp
public sealed record ConsumerGroupJoinResult
{
}
    public required string GroupId { get; init; }
    public required string MemberId { get; init; }
    public int GenerationId { get; init; }
    public bool IsLeader { get; init; }
    public List<TopicPartitionAssignment> Assignments { get; init; };
}
```
```csharp
public sealed record TopicPartitionAssignment
{
}
    public required string Topic { get; init; }
    public int Partition { get; init; }
}
```
```csharp
public sealed record ConsumerGroupInfo
{
}
    public required string GroupId { get; init; }
    public GroupState State { get; init; }
    public int GenerationId { get; init; }
    public List<ConsumerMemberState> Members { get; init; };
    public string? LeaderId { get; init; }
}
```
```csharp
public sealed class KafkaOffsetCommitManager
{
}
    public KafkaOffsetCommitManager(OffsetCommitStrategy strategy = OffsetCommitStrategy.AutoCommit, int autoCommitIntervalMs = 5000);
    public void MarkProcessed(string topic, int partition, long offset);
    public CommitResult CommitSync();
    public Task<CommitResult> CommitAsync();
    public CommitResult CommitTransaction(string transactionId);
    public long GetCommittedOffset(string topic, int partition);
}
```
```csharp
public sealed record CommittedOffset
{
}
    public required string TopicPartition { get; init; }
    public long Offset { get; init; }
    public DateTime CommittedAt { get; init; }
}
```
```csharp
public sealed record CommitResult
{
}
    public bool Success { get; init; }
    public List<CommittedOffset> CommittedOffsets { get; init; };
    public DateTime CommittedAt { get; init; }
    public string? TransactionId { get; init; }
}
```
```csharp
public sealed class KafkaDeadLetterQueueRouter
{
}
    public KafkaDeadLetterQueueRouter(int maxRetries = 3);
    public DeadLetterRouteResult RouteToDeadLetter(string originalTopic, int partition, long offset, byte[] messageData, string? key, Exception failureReason, int attemptCount);
    public IReadOnlyList<DeadLetterMessage> DrainDlq(string originalTopic, int maxMessages = 100);
    public long TotalRouted;;
}
```
```csharp
public sealed record DeadLetterMessage
{
}
    public required string OriginalTopic { get; init; }
    public int OriginalPartition { get; init; }
    public long OriginalOffset { get; init; }
    public required byte[] MessageData { get; init; }
    public string? MessageKey { get; init; }
    public required string FailureReason { get; init; }
    public required string FailureType { get; init; }
    public int AttemptCount { get; init; }
    public int MaxRetries { get; init; }
    public DateTime RoutedAt { get; init; }
}
```
```csharp
public sealed record DeadLetterRouteResult
{
}
    public required string DlqTopic { get; init; }
    public bool Success { get; init; }
    public bool ShouldRetry { get; init; }
    public long TotalDlqMessages { get; init; }
}
```
```csharp
public sealed class KafkaBackpressureManager
{
}
    public KafkaBackpressureManager(long highWatermark = 100_000, long lowWatermark = 10_000);
    public BackpressureDecision UpdateLag(string topic, int partition, long currentOffset, long highWatermarkOffset);
    public BackpressureState State;;
    public int CurrentThrottleMs;;
}
```
```csharp
public sealed record PartitionLagInfo
{
}
    public required string Topic { get; init; }
    public int Partition { get; init; }
    public long CurrentOffset { get; init; }
    public long HighWatermark { get; init; }
    public long Lag { get; init; }
    public DateTime UpdatedAt { get; init; }
}
```
```csharp
public sealed record BackpressureDecision
{
}
    public BackpressureState State { get; init; }
    public int ThrottleMs { get; init; }
    public long TotalLag { get; init; }
    public bool ShouldPause { get; init; }
}
```
```csharp
public sealed class KafkaKTableStateStore
{
}
    public void Put(string key, byte[] value, DateTime? timestamp = null);
    public KTableEntry? Get(string key);;
    public void Delete(string key);;
    public void PutWindowed(string key, byte[] value, WindowType windowType, TimeSpan windowSize, DateTime? timestamp = null);
    public IReadOnlyList<WindowedKTableEntry> FetchWindowed(string key, DateTime from, DateTime to);
    public IReadOnlyList<KTableEntry> All();;
    public long Count;;
}
```
```csharp
public sealed record KTableEntry
{
}
    public required string Key { get; init; }
    public required byte[] Value { get; init; }
    public DateTime Timestamp { get; init; }
    public long Version { get; init; }
}
```
```csharp
public sealed record WindowedKTableEntry
{
}
    public required string Key { get; init; }
    public required byte[] Value { get; init; }
    public WindowType WindowType { get; init; }
    public TimeSpan WindowSize { get; init; }
    public DateTime WindowStart { get; init; }
    public DateTime WindowEnd { get; init; }
    public DateTime Timestamp { get; init; }
}
```
```csharp
public sealed class StreamingMetricsCollector
{
}
    public StreamingMetricsCollector(int maxSamples = 10_000);
    public void RecordThroughput(string topic, long messagesPerSec);;
    public void RecordLag(string topic, int partition, long lag);;
    public void RecordError(string topic, string errorType);;
    public void RecordBytes(string topic, long bytes, bool isRead);;
    public StreamingMetricsSnapshot GetSnapshot();
}
```
```csharp
public sealed class StreamingMetricCounter
{
}
    public required string Key { get; init; }
    public long Value { get; set; }
}
```
```csharp
public sealed record StreamingMetricSample
{
}
    public required string Key { get; init; }
    public required string Metric { get; init; }
    public long Value { get; init; }
    public DateTime Timestamp { get; init; }
}
```
```csharp
public sealed record StreamingMetricsSnapshot
{
}
    public Dictionary<string, long> Counters { get; init; };
    public List<StreamingMetricSample> RecentSamples { get; init; };
    public DateTime CollectedAt { get; init; }
}
```
```csharp
public sealed class KafkaTransactionCoordinator
{
}
    public string InitTransactions(string transactionalId);
    public TransactionHandle BeginTransaction(string transactionalId);
    public TransactionResult CommitTransaction(string transactionalId);
    public TransactionResult AbortTransaction(string transactionalId);
}
```
```csharp
public sealed class TransactionState
{
}
    public required string TransactionalId { get; init; }
    public long ProducerId { get; init; }
    public int ProducerEpoch { get; set; }
    public TransactionStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime? CurrentTransactionStartedAt { get; set; }
    public long CommittedCount { get; set; }
    public long AbortedCount { get; set; }
}
```
```csharp
public sealed record TransactionHandle
{
}
    public required string TransactionalId { get; init; }
    public long ProducerId { get; init; }
    public int ProducerEpoch { get; init; }
}
```
```csharp
public sealed record TransactionResult
{
}
    public bool Success { get; init; }
    public TransactionStatus Status { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/MessageQueue/NatsStreamStrategy.cs
```csharp
internal sealed class NatsStreamStrategy : StreamingDataStrategyBase, IStreamingStrategy
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public IReadOnlyList<string> SupportedProtocols;;
    public async Task<IReadOnlyList<PublishResult>> PublishBatchAsync(string streamName, IEnumerable<StreamMessage> messages, CancellationToken ct = default);
    public async Task<PublishResult> PublishAsync(string streamName, StreamMessage message, CancellationToken ct = default);
    public async IAsyncEnumerable<StreamMessage> SubscribeAsync(string streamName, ConsumerGroup? consumerGroup = null, SubscriptionOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default);
    public Task CreateStreamAsync(string streamName, StreamConfiguration? config = null, CancellationToken ct = default);
    public Task DeleteStreamAsync(string streamName, CancellationToken ct = default);
    public Task<bool> StreamExistsAsync(string streamName, CancellationToken ct = default);
    public async IAsyncEnumerable<string> ListStreamsAsync([EnumeratorCancellation] CancellationToken ct = default);
    public Task<StreamInfo> GetStreamInfoAsync(string streamName, CancellationToken ct = default);
    public Task CommitOffsetAsync(string streamName, ConsumerGroup consumerGroup, StreamOffset offset, CancellationToken ct = default);
    public Task SeekAsync(string streamName, ConsumerGroup consumerGroup, StreamOffset offset, CancellationToken ct = default);
    public Task<StreamOffset> GetOffsetAsync(string streamName, ConsumerGroup consumerGroup, CancellationToken ct = default);
    public override void ConfigureIntelligence(IMessageBus? messageBus);
}
```
```csharp
private sealed record NatsStreamState
{
}
    public required string Name { get; init; }
    public List<string> Subjects { get; init; };
    public TimeSpan RetentionPeriod { get; init; };
    public long MaxSizeBytes { get; init; };
    public long MaxMessages { get; init; };
    public int ReplicationFactor { get; init; };
    public DateTime CreatedAt { get; init; }
}
```
```csharp
private sealed class NatsConsumerState
{
}
    public required string ConsumerId { get; init; }
    public required string GroupId { get; init; }
    public long LastDeliveredSeq { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/AdaptiveTransport/AdaptiveTransportStrategy.cs
```csharp
public sealed class BandwidthAwareSyncMonitor : IDisposable
{
}
    public event EventHandler<LinkClassificationChangedEventArgs>? OnLinkClassChanged;
    public BandwidthAwareSyncMonitor(BandwidthMonitorOptions? options = null);
    public Task StartAsync(CancellationToken ct = default);
    public Task StopAsync();
    public SyncParameters GetCurrentParameters();
    public LinkClassification? GetCurrentClassification();;
    public void EnqueueSync(SyncOperation operation);;
    public Task<SyncOperation?> ProcessNextSyncAsync(CancellationToken ct = default);
    public void Dispose();
}
```
```csharp
public sealed class LinkClassificationChangedEventArgs : EventArgs
{
}
    public LinkClass PreviousClass { get; }
    public LinkClass NewClass { get; }
    public LinkClassification Classification { get; }
    public LinkClassificationChangedEventArgs(LinkClass previousClass, LinkClass newClass, LinkClassification classification);
}
```
```csharp
public sealed class BandwidthProbe
{
}
    public async Task<BandwidthMeasurement> MeasureAsync(string endpoint, CancellationToken ct);
    public BandwidthMeasurement? GetAverageMeasurement(TimeSpan window);
}
```
```csharp
public sealed class LinkClassifier
{
}
    public LinkClassification? CurrentClass;;
    public LinkClassifier(double hysteresisThreshold = 0.7);
    public LinkClassification Classify(BandwidthMeasurement measurement);
}
```
```csharp
public sealed class SyncParameterAdjuster
{
}
    public SyncParameters GetParameters(LinkClassification link);
}
```
```csharp
public sealed class SyncPriorityQueue
{
}
    public int Count
{
    get
    {
        lock (_lock)
        {
            return _queues.Values.Sum(q => q.Count);
        }
    }
}
    public void Enqueue(SyncOperation operation);
    public bool TryDequeue(out SyncOperation? operation);
    public SyncOperation? Peek();
    public Dictionary<SyncPriority, int> CountByPriority();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/State/StateManagementStrategies.cs
```csharp
public sealed class InMemoryStateBackendStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<string> CreateStateStoreAsync(string storeName, StateStoreConfig? config = null, CancellationToken ct = default);
    public Task<byte[]?> GetAsync(string storeId, string key, CancellationToken ct = default);
    public Task PutAsync(string storeId, string key, byte[] value, TimeSpan? ttl = null, CancellationToken ct = default);
    public Task DeleteAsync(string storeId, string key, CancellationToken ct = default);
    public Task<IReadOnlyList<string>> GetKeysAsync(string storeId, string? prefix = null, CancellationToken ct = default);
    public Task<StateSnapshot> CreateSnapshotAsync(string storeId, CancellationToken ct = default);
    public Task RestoreFromSnapshotAsync(string storeId, StateSnapshot snapshot, CancellationToken ct = default);
}
```
```csharp
public sealed record StateStoreConfig
{
}
    public TimeSpan? DefaultTtl { get; init; }
    public int MaxEntries { get; init; };
    public long MaxSizeBytes { get; init; };
    public bool EnableChangeLog { get; init; }
}
```
```csharp
public sealed record StateEntry
{
}
    public required string Key { get; init; }
    public required byte[] Value { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}
```
```csharp
public sealed record StateSnapshot
{
}
    public required string SnapshotId { get; init; }
    public required string StoreId { get; init; }
    public required byte[] Data { get; init; }
    public int EntryCount { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
```
```csharp
public sealed class RocksDbStateBackendStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<string> OpenStoreAsync(string dbPath, RocksDbConfig? config = null, CancellationToken ct = default);
    public Task<byte[]?> GetAsync(string storeId, string key, CancellationToken ct = default);
    public Task PutAsync(string storeId, string key, byte[] value, CancellationToken ct = default);
    public Task WriteBatchAsync(string storeId, IEnumerable<(string Key, byte[] Value)> entries, CancellationToken ct = default);
    public Task<IncrementalCheckpoint> CreateIncrementalCheckpointAsync(string storeId, string checkpointPath, CancellationToken ct = default);
    public Task CompactAsync(string storeId, CancellationToken ct = default);
    public Task<RocksDbStats> GetStatsAsync(string storeId, CancellationToken ct = default);
}
```
```csharp
public sealed record RocksDbConfig
{
}
    public long WriteBufferSize { get; init; };
    public int MaxWriteBufferNumber { get; init; };
    public bool EnableCompression { get; init; };
    public string CompressionType { get; init; };
    public int MaxBackgroundCompactions { get; init; };
    public int MaxBackgroundFlushes { get; init; };
}
```
```csharp
internal sealed class RocksDbStore
{
}
    public required string StoreId { get; init; }
    public required string DbPath { get; init; }
    public required RocksDbConfig Config { get; init; }
    public required BoundedDictionary<string, byte[]> Data { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public long WriteCount { get; set; }
}
```
```csharp
public sealed record IncrementalCheckpoint
{
}
    public required string CheckpointId { get; init; }
    public required string StoreId { get; init; }
    public required string Path { get; init; }
    public long SequenceNumber { get; init; }
    public long SizeBytes { get; init; }
    public int EntryCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
```
```csharp
public sealed record RocksDbStats
{
}
    public required string StoreId { get; init; }
    public int EntryCount { get; init; }
    public long TotalSizeBytes { get; init; }
    public long WriteCount { get; init; }
    public int CompactionCount { get; init; }
}
```
```csharp
public sealed class DistributedStateStoreStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<string> CreateStoreAsync(string storeName, DistributedStoreConfig config, CancellationToken ct = default);
    public Task<byte[]?> GetAsync(string storeId, string key, ConsistencyLevel consistency = ConsistencyLevel.Strong, CancellationToken ct = default);
    public Task PutAsync(string storeId, string key, byte[] value, ConsistencyLevel consistency = ConsistencyLevel.Strong, CancellationToken ct = default);
    public Task<IReadOnlyDictionary<string, byte[]>> GetPartitionStateAsync(string storeId, int partitionId, CancellationToken ct = default);
    public Task RestorePartitionStateAsync(string storeId, int partitionId, IReadOnlyDictionary<string, byte[]> state, CancellationToken ct = default);
}
```
```csharp
public sealed record DistributedStoreConfig
{
}
    public int PartitionCount { get; init; };
    public int ReplicationFactor { get; init; };
    public string[] Nodes { get; init; };
    public ConsistencyLevel DefaultConsistency { get; init; };
}
```
```csharp
internal sealed class DistributedStore
{
}
    public required string StoreId { get; init; }
    public required string Name { get; init; }
    public required DistributedStoreConfig Config { get; init; }
    public required BoundedDictionary<int, PartitionState> Partitions { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
```
```csharp
internal sealed class PartitionState
{
}
    public int PartitionId { get; init; }
    public required BoundedDictionary<string, byte[]> Data { get; init; }
    public required string Leader { get; init; }
}
```
```csharp
public sealed class ChangelogStateStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<string> CreateStoreAsync(string storeName, ChangelogConfig? config = null, CancellationToken ct = default);
    public Task<byte[]?> GetAsync(string storeId, string key, CancellationToken ct = default);
    public Task PutAsync(string storeId, string key, byte[] value, CancellationToken ct = default);
    public Task DeleteAsync(string storeId, string key, CancellationToken ct = default);
    public Task ReplayChangelogAsync(string storeId, long fromSequence = 0, long? toSequence = null, CancellationToken ct = default);
    public Task<CompactionResult> CompactChangelogAsync(string storeId, CancellationToken ct = default);
    public Task<IReadOnlyList<ChangelogEntry>> GetChangelogAsync(string storeId, long fromSequence = 0, int limit = 1000, CancellationToken ct = default);
}
```
```csharp
public sealed record ChangelogConfig
{
}
    public TimeSpan? RetentionPeriod { get; init; }
    public int? MaxEntries { get; init; }
    public bool AutoCompaction { get; init; };
    public TimeSpan CompactionInterval { get; init; };
}
```
```csharp
internal sealed class ChangelogStore
{
}
    public required string StoreId { get; init; }
    public required string Name { get; init; }
    public required ChangelogConfig Config { get; init; }
    public required BoundedDictionary<string, byte[]> CurrentState { get; init; }
    public required List<ChangelogEntry> Changelog { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public long SequenceNumber;
}
```
```csharp
public sealed record ChangelogEntry
{
}
    public long SequenceNumber { get; init; }
    public ChangeOperation Operation { get; init; }
    public required string Key { get; init; }
    public byte[]? Value { get; init; }
    public byte[]? OldValue { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
```
```csharp
public sealed record CompactionResult
{
}
    public int EntriesBefore { get; init; }
    public int EntriesAfter { get; init; }
    public int EntriesRemoved { get; init; }
    public DateTimeOffset CompactedAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/Industrial/ModbusStreamStrategy.cs
```csharp
public sealed record ModbusConnectionConfig
{
}
    public required string Host { get; init; }
    public int Port { get; init; };
    public byte UnitId { get; init; };
    public ModbusTransport Transport { get; init; };
    public int TimeoutMs { get; init; };
    public int Retries { get; init; };
    public ModbusByteOrder ByteOrder { get; init; };
}
```
```csharp
public sealed record ModbusRegisterMap
{
}
    public required string TagName { get; init; }
    public required ushort Address { get; init; }
    public ModbusFunctionCode FunctionCode { get; init; };
    public ModbusDataType DataType { get; init; };
    public double ScaleFactor { get; init; };
    public double Offset { get; init; }
    public string? EngineeringUnit { get; init; }
    public ushort? RegisterCount { get; init; }
}
```
```csharp
public sealed record ModbusReadResult
{
}
    public required string TagName { get; init; }
    public required ushort[] RawRegisters { get; init; }
    public double EngineeringValue { get; init; }
    public string? EngineeringUnit { get; init; }
    public bool Quality { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public ModbusExceptionCode ExceptionCode { get; init; }
}
```
```csharp
public sealed record ModbusPollingConfig
{
}
    public required string GroupId { get; init; }
    public required ModbusConnectionConfig Connection { get; init; }
    public required List<ModbusRegisterMap> RegisterMaps { get; init; }
    public int PollingIntervalMs { get; init; };
    public bool CoalesceReads { get; init; };
    public int MaxCoalesceGap { get; init; };
}
```
```csharp
internal sealed class ModbusStreamStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<string> CreatePollingGroupAsync(ModbusPollingConfig config, CancellationToken ct = default);
    public Task<ushort[]> ReadRegistersAsync(ModbusConnectionConfig connection, ModbusFunctionCode functionCode, ushort startAddress, ushort count, CancellationToken ct = default);
    public Task WriteSingleRegisterAsync(ModbusConnectionConfig connection, ushort address, ushort value, CancellationToken ct = default);
    public Task WriteMultipleRegistersAsync(ModbusConnectionConfig connection, ushort startAddress, ushort[] values, CancellationToken ct = default);
    public Task<IReadOnlyList<ModbusReadResult>> ReadMappedRegistersAsync(ModbusConnectionConfig connection, IReadOnlyList<ModbusRegisterMap> registerMaps, CancellationToken ct = default);
    public async Task<IReadOnlyList<ModbusReadResult>> PollGroupAsync(string groupId, CancellationToken ct = default);
    public IReadOnlyList<ModbusReadResult> DrainQueue(string groupId, int maxCount = 100);
    public static ushort ComputeCrc16(ReadOnlySpan<byte> data);
    public static byte[] BuildTcpRequestFrame(ushort transactionId, byte unitId, ModbusFunctionCode functionCode, ushort startAddress, ushort quantity);
    public static byte[] BuildRtuRequestFrame(byte unitId, ModbusFunctionCode functionCode, ushort startAddress, ushort quantity);
    public Task RemovePollingGroupAsync(string groupId, CancellationToken ct = default);
    public long TotalPolls;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStreamingData/Strategies/Industrial/OpcUaStreamStrategy.cs
```csharp
public sealed record OpcUaNodeId
{
}
    public required ushort NamespaceIndex { get; init; }
    public required string Identifier { get; init; }
    public OpcUaNodeIdType IdType { get; init; };
    public override string ToString();;
}
```
```csharp
public sealed record OpcUaDataValue
{
}
    public required OpcUaNodeId NodeId { get; init; }
    public object? Value { get; init; }
    public uint StatusCode { get; init; }
    public DateTimeOffset SourceTimestamp { get; init; }
    public DateTimeOffset ServerTimestamp { get; init; }
    public bool IsGood;;
}
```
```csharp
public sealed record OpcUaSubscription
{
}
    public required string SubscriptionId { get; init; }
    public double PublishingIntervalMs { get; init; };
    public uint KeepAliveCount { get; init; };
    public uint MaxNotificationsPerPublish { get; init; };
    public List<OpcUaMonitoredItem> MonitoredItems { get; init; };
    public bool IsActive { get; init; };
}
```
```csharp
public sealed record OpcUaMonitoredItem
{
}
    public required OpcUaNodeId NodeId { get; init; }
    public double SamplingIntervalMs { get; init; };
    public uint QueueSize { get; init; };
    public bool DiscardOldest { get; init; };
    public int DataChangeTrigger { get; init; };
    public uint DeadbandType { get; init; }
    public double DeadbandValue { get; init; }
}
```
```csharp
public sealed record OpcUaSessionConfig
{
}
    public required string EndpointUrl { get; init; }
    public OpcUaSecurityMode SecurityMode { get; init; };
    public OpcUaSecurityPolicy SecurityPolicy { get; init; };
    public double SessionTimeoutMs { get; init; };
    public string? CertificateThumbprint { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
}
```
```csharp
internal sealed class OpcUaStreamStrategy : StreamingDataStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override StreamingCategory Category;;
    public override StreamingDataCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<string> ConnectSessionAsync(OpcUaSessionConfig config, CancellationToken ct = default);
    public Task<OpcUaSubscription> CreateSubscriptionAsync(string sessionId, List<OpcUaMonitoredItem> monitoredItems, double publishingIntervalMs = 1000, CancellationToken ct = default);
    public Task<IReadOnlyList<OpcUaDataValue>> ReadNodesAsync(string sessionId, IReadOnlyList<OpcUaNodeId> nodeIds, CancellationToken ct = default);
    public Task<IReadOnlyList<uint>> WriteNodesAsync(string sessionId, IReadOnlyList<(OpcUaNodeId NodeId, object Value)> writeValues, CancellationToken ct = default);
    public Task<IReadOnlyList<OpcUaBrowseResult>> BrowseAsync(string sessionId, OpcUaNodeId? startNodeId = null, int maxResults = 100, CancellationToken ct = default);
    public Task<IReadOnlyList<OpcUaDataValue>> PollNotificationsAsync(string subscriptionId, int maxCount = 100, CancellationToken ct = default);
    public void EnqueueNotifications(string subscriptionId, IEnumerable<OpcUaDataValue> values);
    public Task DeleteSubscriptionAsync(string subscriptionId, CancellationToken ct = default);
    public Task DisconnectSessionAsync(string sessionId, CancellationToken ct = default);
    public int ActiveSubscriptionCount;;
    public long TotalNotifications;;
}
```
```csharp
public sealed record OpcUaBrowseResult
{
}
    public required OpcUaNodeId NodeId { get; init; }
    public required string DisplayName { get; init; }
    public OpcUaNodeClass NodeClass { get; init; }
    public string? TypeDefinition { get; init; }
}
```
