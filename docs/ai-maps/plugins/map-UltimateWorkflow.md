# Plugin: UltimateWorkflow
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateWorkflow

### File: Plugins/DataWarehouse.Plugins.UltimateWorkflow/UltimateWorkflowPlugin.cs
```csharp
public sealed class UltimateWorkflowPlugin : OrchestrationPluginBase, IDisposable
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string OrchestrationMode;;
    public override PluginCategory Category;;
    public string SemanticDescription;;
    public string[] SemanticTags;;
    public StrategyRegistry<WorkflowStrategyBase> Registry;;
    public UltimateWorkflowPlugin();
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request);
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct);
    protected override async Task OnStartWithoutIntelligenceAsync(CancellationToken ct);
    protected override async Task OnStopCoreAsync();
    public override async Task OnMessageAsync(PluginMessage message);
    public IReadOnlyCollection<string> GetRegisteredStrategies();;
    public WorkflowStrategyBase? GetStrategy(string name);;
    public void SetActiveStrategy(string strategyName);
    public void DefineWorkflow(WorkflowDefinition workflow);
    public async Task<WorkflowResult> ExecuteWorkflowAsync(string workflowId, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
    public WorkflowResult? GetExecutionResult(string instanceId);;
    protected override List<PluginCapabilityDescriptor> GetCapabilities();
    protected override Dictionary<string, object> GetMetadata();
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
{
    get
    {
        var capabilities = new List<RegisteredCapability>
        {
            new()
            {
                CapabilityId = $"{Id}.orchestrate",
                DisplayName = "Ultimate Workflow Orchestration",
                Description = SemanticDescription,
                Category = SDK.Contracts.CapabilityCategory.DataManagement,
                SubCategory = "Workflow",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = SemanticTags,
                SemanticDescription = SemanticDescription
            }
        };
        foreach (var strategy in _registry.GetAll())
        {
            var chars = strategy.Characteristics;
            var tags = new List<string>
            {
                "workflow",
                chars.Category.ToString().ToLowerInvariant()
            };
            tags.AddRange(chars.Tags);
            capabilities.Add(new RegisteredCapability { CapabilityId = $"{Id}.strategy.{strategy.StrategyId.ToLowerInvariant()}", DisplayName = chars.StrategyName, Description = chars.Description, Category = SDK.Contracts.CapabilityCategory.DataManagement, SubCategory = "Workflow", PluginId = Id, PluginName = Name, PluginVersion = Version, Tags = tags.ToArray(), Metadata = new Dictionary<string, object> { ["category"] = chars.Category.ToString(), ["supportsParallel"] = chars.Capabilities.SupportsParallelExecution, ["supportsDynamic"] = chars.Capabilities.SupportsDynamicDag, ["supportsDistributed"] = chars.Capabilities.SupportsDistributed, ["supportsCheckpointing"] = chars.Capabilities.SupportsCheckpointing, ["maxParallelTasks"] = chars.Capabilities.MaxParallelTasks }, SemanticDescription = chars.Description });
        }

        return capabilities;
    }
}
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateWorkflow/WorkflowStrategyBase.cs
```csharp
public sealed record WorkflowTask
{
}
    public required string TaskId { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; };
    public HashSet<string> Dependencies { get; init; };
    public Func<WorkflowContext, CancellationToken, Task<TaskResult>>? Handler { get; set; }
    public int MaxRetries { get; init; };
    public TimeSpan RetryDelay { get; init; };
    public TimeSpan? Timeout { get; init; }
    public WorkflowPriority Priority { get; init; };
    public Func<WorkflowContext, bool>? Condition { get; set; }
    public Dictionary<string, object> Metadata { get; init; };
    public TimeSpan? EstimatedDuration { get; init; }
    public Dictionary<string, double> ResourceRequirements { get; init; };
}
```
```csharp
public sealed record TaskResult
{
}
    public bool Success { get; init; }
    public object? Output { get; init; }
    public string? Error { get; init; }
    public TimeSpan Duration { get; init; }
    public int RetryCount { get; init; }
    public Dictionary<string, double> Metrics { get; init; };
    public static TaskResult Succeeded(object? output = null, TimeSpan? duration = null);;
    public static TaskResult Failed(string error, TimeSpan? duration = null);;
}
```
```csharp
public sealed class WorkflowContext
{
}
    public required string WorkflowInstanceId { get; init; }
    public required string WorkflowId { get; init; }
    public Dictionary<string, object> Parameters { get; init; };
    public BoundedDictionary<string, object> State { get; };
    public BoundedDictionary<string, TaskResult> TaskResults { get; };
    public DateTimeOffset StartTime { get; init; };
    public Dictionary<string, object> Metadata { get; init; };
    public T? GetTaskOutput<T>(string taskId);;
}
```
```csharp
public sealed class WorkflowDefinition
{
}
    public required string WorkflowId { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; };
    public string Version { get; init; };
    public List<WorkflowTask> Tasks { get; init; };
    public TimeSpan? GlobalTimeout { get; init; }
    public int MaxParallelism { get; init; };
    public WorkflowPriority Priority { get; init; };
    public Dictionary<string, object> Metadata { get; init; };
    public ErrorHandlingStrategy ErrorStrategy { get; init; };
    public bool ValidateDag();
    public IEnumerable<WorkflowTask> GetTopologicalOrder();
}
```
```csharp
public sealed class WorkflowResult
{
}
    public required string WorkflowInstanceId { get; init; }
    public bool Success { get; init; }
    public WorkflowExecutionStatus Status { get; init; }
    public TimeSpan Duration { get; init; }
    public Dictionary<string, TaskResult> TaskResults { get; init; };
    public string? Error { get; init; }
    public object? Output { get; init; }
}
```
```csharp
public sealed record WorkflowCharacteristics
{
}
    public required string StrategyName { get; init; }
    public required string Description { get; init; }
    public required WorkflowCategory Category { get; init; }
    public required WorkflowCapabilities Capabilities { get; init; }
    public int TypicalTaskOverheadMs { get; init; };
    public string[] Tags { get; init; };
}
```
```csharp
public abstract class WorkflowStrategyBase
{
}
    public abstract WorkflowCharacteristics Characteristics { get; }
    public string StrategyId;;
    public abstract Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);;
    public virtual bool Validate(WorkflowDefinition workflow, out List<string> errors);
    protected IEnumerable<WorkflowTask> GetReadyTasks(WorkflowDefinition workflow, HashSet<string> completed, HashSet<string> running);
    protected async Task<TaskResult> ExecuteTaskAsync(WorkflowTask task, WorkflowContext context, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateWorkflow/Scaling/PipelineScalingManager.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-10: Pipeline stage captured state with spill support")]
public sealed record PipelineStageCapturedState(string PipelineId, int StageIndex, string StageName, byte[]? StateData, bool IsSpilled, IReadOnlyList<int> DependsOnStages)
{
}
    public long SizeBytes;;
}
```
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-10: Pipeline scaling manager with depth limits and parallel rollback")]
public sealed class PipelineScalingManager : IScalableSubsystem, IDisposable
{
}
    public PipelineScalingManager(IPersistentBackingStore? backingStore = null, ScalingLimits? limits = null);
    public int MaxPipelineDepth { get => _maxPipelineDepth; set => _maxPipelineDepth = Math.Max(1, value); }
    public long MaxStateBytesPerStage { get => Interlocked.Read(ref _maxStateBytesPerStage); set => Interlocked.Exchange(ref _maxStateBytesPerStage, Math.Max(1024, value)); }
    public bool ValidatePipelineDepth(int stageCount);
    public async Task<bool> AcquireTransactionSlotAsync(CancellationToken ct = default);
    public void ReleaseTransactionSlot();
    public async Task CaptureStageStateAsync(string pipelineId, int stageIndex, string stageName, byte[] stateData, IReadOnlyList<int>? dependsOnStages = null, CancellationToken ct = default);
    public async Task<byte[]?> LoadSpilledStateAsync(string pipelineId, int stageIndex, CancellationToken ct = default);
    public async Task<PipelineRollbackResult> RollbackAsync(string pipelineId, Func<PipelineStageCapturedState, CancellationToken, Task> rollbackAction, CancellationToken ct = default);
    public IReadOnlyDictionary<string, object> GetScalingMetrics();
    public async Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default);
    public ScalingLimits CurrentLimits;;
    public BackpressureState CurrentBackpressureState
{
    get
    {
        long pending = Interlocked.Read(ref _pendingTransactions);
        int maxQueue = _currentLimits.MaxQueueDepth;
        if (pending <= 0)
            return BackpressureState.Normal;
        if (pending < maxQueue * 0.5)
            return BackpressureState.Normal;
        if (pending < maxQueue * 0.8)
            return BackpressureState.Warning;
        if (pending < maxQueue)
            return BackpressureState.Critical;
        return BackpressureState.Shedding;
    }
}
    public void Dispose();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateWorkflow/Strategies/WorkflowAdvancedFeatures.cs
```csharp
public sealed class WorkflowXComManager
{
}
    public void Push(string dagRunId, string taskId, string key, object value);
    public object? Pull(string dagRunId, string sourceTaskId, string key);
    public T? Pull<T>(string dagRunId, string sourceTaskId, string key)
    where T : class;
    public IReadOnlyList<XComEntry> GetAll(string dagRunId);
    public int Clear(string dagRunId);
}
```
```csharp
public sealed record XComEntry
{
}
    public required string DagRunId { get; init; }
    public required string TaskId { get; init; }
    public required string Key { get; init; }
    public required object Value { get; init; }
    public DateTime PushedAt { get; init; }
}
```
```csharp
public sealed class WorkflowVersionManager
{
}
    public void RegisterVersion(string workflowType, string version, string? changeId = null);
    public int GetVersion(string workflowType, string changeId, int minSupported, int maxSupported);
    public void RecordPatch(string workflowType, string fromVersion, string toVersion, string description);
    public IReadOnlyList<WorkflowVersionInfo> GetVersions(string workflowType);
}
```
```csharp
public sealed record WorkflowVersionInfo
{
}
    public required string WorkflowType { get; init; }
    public required string Version { get; init; }
    public required string ChangeId { get; init; }
    public DateTime RegisteredAt { get; init; }
}
```
```csharp
public sealed record VersionPatch
{
}
    public required string FromVersion { get; init; }
    public required string ToVersion { get; init; }
    public required string Description { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed class ChildWorkflowExecutor
{
}
    public async Task<ChildWorkflowHandle> StartChildWorkflowAsync(string parentWorkflowId, WorkflowDefinition childDefinition, WorkflowStrategyBase strategy, Dictionary<string, object>? parameters = null, ChildWorkflowOptions? options = null, CancellationToken ct = default);
    public IReadOnlyList<ChildWorkflowRecord> GetChildren(string parentWorkflowId);
    public int CancelChildren(string parentWorkflowId);
}
```
```csharp
public sealed record ChildWorkflowOptions
{
}
    public ParentClosePolicy ParentClosePolicy { get; init; };
    public TimeSpan? ExecutionTimeout { get; init; }
    public int MaxRetries { get; init; }
}
```
```csharp
public sealed class ChildWorkflowRecord
{
}
    public required string ChildInstanceId { get; init; }
    public required string ParentWorkflowId { get; init; }
    public required string ChildWorkflowId { get; init; }
    public ChildWorkflowStatus Status { get; set; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
    public WorkflowResult? Result { get; set; }
    public ParentClosePolicy CancellationOnParentClose { get; init; }
}
```
```csharp
public sealed record ChildWorkflowHandle
{
}
    public required string ChildInstanceId { get; init; }
    public ChildWorkflowStatus Status { get; init; }
    public WorkflowResult? Result { get; init; }
}
```
```csharp
public sealed class ActivityHeartbeatManager
{
}
    public ActivityHeartbeatManager(TimeSpan? heartbeatTimeout = null);
    public void RecordHeartbeat(string activityId, object? details = null);
    public bool IsTimedOut(string activityId);
    public ActivityHeartbeat? GetHeartbeat(string activityId);;
    public IReadOnlyList<ActivityHeartbeat> GetTimedOutActivities();
    public void RemoveActivity(string activityId);;
}
```
```csharp
public sealed class ActivityHeartbeat
{
}
    public required string ActivityId { get; init; }
    public object? Details { get; set; }
    public DateTime LastHeartbeat { get; set; }
    public long HeartbeatCount { get; set; }
}
```
```csharp
public sealed class WorkflowCheckpointManager
{
}
    public WorkflowCheckpoint SaveCheckpoint(string workflowInstanceId, WorkflowContext context, IReadOnlySet<string> completedTasks, IReadOnlySet<string> failedTasks);
    public WorkflowCheckpoint? GetCheckpoint(string workflowInstanceId);;
    public bool RemoveCheckpoint(string workflowInstanceId);;
    public IReadOnlyList<WorkflowCheckpoint> ListCheckpoints();;
}
```
```csharp
public sealed record WorkflowCheckpoint
{
}
    public required string WorkflowInstanceId { get; init; }
    public List<string> CompletedTaskIds { get; init; };
    public List<string> FailedTaskIds { get; init; };
    public Dictionary<string, TaskResult> TaskResults { get; init; };
    public Dictionary<string, object> SharedState { get; init; };
    public DateTime CheckpointedAt { get; init; }
}
```
```csharp
public sealed class WorkflowTriggerManager
{
}
    public string RegisterScheduleTrigger(string workflowId, string cronExpression, string? timezone = null);
    public string RegisterEventTrigger(string workflowId, string eventType, string? eventFilter = null);
    public string RegisterWebhookTrigger(string workflowId, string path);
    public string RegisterDependencyTrigger(string workflowId, string upstreamWorkflowId);
    public IReadOnlyList<WorkflowTrigger> EvaluateEventTriggers(string eventType);
    public bool DeactivateTrigger(string triggerId);
    public IReadOnlyList<WorkflowTrigger> GetTriggers(string workflowId);;
    public IReadOnlyList<WorkflowTrigger> GetActiveTriggers();;
}
```
```csharp
public sealed class WorkflowTrigger
{
}
    public required string TriggerId { get; init; }
    public required string WorkflowId { get; init; }
    public TriggerType Type { get; init; }
    public string? CronExpression { get; init; }
    public string? Timezone { get; init; }
    public string? EventType { get; init; }
    public string? EventFilter { get; init; }
    public string? WebhookPath { get; init; }
    public string? UpstreamWorkflowId { get; init; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastTriggeredAt { get; set; }
}
```
```csharp
public sealed class FlowScheduler
{
}
    public string CreateSchedule(string flowId, string cronExpression, Dictionary<string, object>? parameters = null);
    public FlowRun CreateRun(string flowId, Dictionary<string, object>? parameters = null);
    public FlowRunStatus TransitionState(string runId, FlowRunStatus newStatus, string? message = null);
    public FlowRun? GetRun(string runId);;
    public IReadOnlyList<FlowScheduleEntry> GetActiveSchedules();;
    public IReadOnlyList<FlowRun> ListRuns(string? flowId = null, int limit = 100);
}
```
```csharp
public sealed class FlowScheduleEntry
{
}
    public required string ScheduleId { get; init; }
    public required string FlowId { get; init; }
    public required string CronExpression { get; init; }
    public Dictionary<string, object> Parameters { get; init; };
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime? NextRunAt { get; set; }
}
```
```csharp
public sealed class FlowRun
{
}
    public required string RunId { get; init; }
    public required string FlowId { get; init; }
    public FlowRunStatus Status { get; set; }
    public string? StateMessage { get; set; }
    public Dictionary<string, object> Parameters { get; init; };
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public object? Result { get; set; }
}
```
```csharp
public sealed class TaskResultPersistence
{
}
    public string Persist(string taskRunId, object result, string? serializer = null);
    public object? Retrieve(string key);;
    public T? Retrieve<T>(string key)
    where T : class;;
    public bool Exists(string key);;
    public int Cleanup(TimeSpan maxAge);
}
```
```csharp
public sealed record PersistedResult
{
}
    public required string Key { get; init; }
    public required string TaskRunId { get; init; }
    public required object Value { get; init; }
    public string Serializer { get; init; };
    public DateTime PersistedAt { get; init; }
}
```
```csharp
public sealed class WorkflowSearchAttributes
{
}
    public void Set(string workflowId, string key, object value, SearchAttributeType type = SearchAttributeType.Keyword);
    public IReadOnlyDictionary<string, SearchAttributeValue> Get(string workflowId);
    public IReadOnlyList<string> Search(string key, object value);
}
```
```csharp
public sealed record SearchAttributeValue
{
}
    public required string Key { get; init; }
    public required object Value { get; init; }
    public SearchAttributeType Type { get; init; }
    public DateTime UpdatedAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateWorkflow/Strategies/AIEnhanced/AIEnhancedStrategies.cs
```csharp
public sealed class AIOptimizedWorkflowStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class SelfLearningWorkflowStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class AnomalyDetectionWorkflowStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class PredictiveScalingStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class IntelligentRetryStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateWorkflow/Strategies/DagExecution/DagExecutionStrategies.cs
```csharp
public sealed class TopologicalDagStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class DynamicDagStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public void AddDynamicTask(string workflowInstanceId, WorkflowTask task);
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class LayeredDagStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class CriticalPathDagStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class StreamingDagStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateWorkflow/Strategies/Distributed/DistributedStrategies.cs
```csharp
public sealed class DistributedExecutionStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class LeaderFollowerStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class ShardedExecutionStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class GossipCoordinationStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class RaftConsensusStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateWorkflow/Strategies/ErrorHandling/ErrorHandlingStrategies.cs
```csharp
public sealed class ExponentialBackoffRetryStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class CircuitBreakerStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
private sealed class CircuitState
{
}
    public int FailureCount { get; set; }
    public DateTimeOffset? OpenedAt { get; set; }
    public bool IsOpen;;
}
```
```csharp
public sealed class FallbackStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class BulkheadIsolationStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class DeadLetterQueueStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
    public IEnumerable<(WorkflowTask Task, TaskResult Result, DateTimeOffset Timestamp)> GetDeadLetters();;
    public void ClearDeadLetterQueue();
}
```
```csharp
public sealed class TimeoutHandlingStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateWorkflow/Strategies/ParallelExecution/ParallelExecutionStrategies.cs
```csharp
public sealed class ForkJoinStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class WorkerPoolStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class PipelineParallelStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class DataflowParallelStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class AdaptiveParallelStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class SpeculativeParallelStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateWorkflow/Strategies/StateManagement/StateManagementStrategies.cs
```csharp
public sealed class CheckpointStateStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
    public bool TryLoadCheckpoint(string instanceId, out HashSet<string>? completed, out Dictionary<string, object>? state);
}
```
```csharp
public sealed class EventSourcedStateStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
    public IEnumerable<object> GetEventLog(string instanceId);;
}
```
```csharp
public sealed class SagaStateStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class TransactionalStateStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class VersionedStateStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
    public Dictionary<string, object>? GetStateAtVersion(string instanceId, int version);;
}
```
```csharp
public sealed class DistributedStateStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateWorkflow/Strategies/TaskScheduling/TaskSchedulingStrategies.cs
```csharp
public sealed class FifoSchedulingStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class PrioritySchedulingStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class RoundRobinSchedulingStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class ShortestJobFirstStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class DeadlineSchedulingStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class MultilevelFeedbackQueueStrategy : WorkflowStrategyBase
{
}
    public override WorkflowCharacteristics Characteristics { get; };
    public override async Task<WorkflowResult> ExecuteAsync(WorkflowDefinition workflow, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);
}
```
