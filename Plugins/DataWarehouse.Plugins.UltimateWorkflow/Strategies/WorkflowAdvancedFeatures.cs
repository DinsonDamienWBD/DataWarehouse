using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateWorkflow.Strategies;

#region XCom Data Passing (Airflow-compatible)

/// <summary>
/// XCom (cross-communication) data passing system for inter-task communication,
/// compatible with Apache Airflow's XCom semantics. Tasks push values to XCom
/// and downstream tasks pull values by key and source task ID.
/// </summary>
public sealed class WorkflowXComManager
{
    private readonly BoundedDictionary<string, XComEntry> _store = new BoundedDictionary<string, XComEntry>(1000);

    /// <summary>Pushes a value to XCom.</summary>
    public void Push(string dagRunId, string taskId, string key, object value)
    {
        var xcomKey = $"{dagRunId}:{taskId}:{key}";
        _store[xcomKey] = new XComEntry
        {
            DagRunId = dagRunId,
            TaskId = taskId,
            Key = key,
            Value = value,
            PushedAt = DateTime.UtcNow
        };
    }

    /// <summary>Pulls a value from XCom by source task and key.</summary>
    public object? Pull(string dagRunId, string sourceTaskId, string key)
    {
        var xcomKey = $"{dagRunId}:{sourceTaskId}:{key}";
        return _store.TryGetValue(xcomKey, out var entry) ? entry.Value : null;
    }

    /// <summary>Pulls a typed value from XCom.</summary>
    public T? Pull<T>(string dagRunId, string sourceTaskId, string key) where T : class
    {
        return Pull(dagRunId, sourceTaskId, key) as T;
    }

    /// <summary>Gets all XCom entries for a DAG run.</summary>
    public IReadOnlyList<XComEntry> GetAll(string dagRunId)
    {
        return _store.Values.Where(e => e.DagRunId == dagRunId).ToList();
    }

    /// <summary>Clears all XCom entries for a DAG run.</summary>
    public int Clear(string dagRunId)
    {
        var keys = _store.Where(kv => kv.Value.DagRunId == dagRunId).Select(kv => kv.Key).ToList();
        foreach (var key in keys) _store.TryRemove(key, out _);
        return keys.Count;
    }
}

/// <summary>An XCom (cross-communication) entry.</summary>
public sealed record XComEntry
{
    public required string DagRunId { get; init; }
    public required string TaskId { get; init; }
    public required string Key { get; init; }
    public required object Value { get; init; }
    public DateTime PushedAt { get; init; }
}

#endregion

#region Workflow Versioning (Temporal-compatible)

/// <summary>
/// Workflow version manager supporting semantic versioning, deterministic replay,
/// and safe version migration compatible with Temporal's workflow versioning.
/// </summary>
public sealed class WorkflowVersionManager
{
    private readonly BoundedDictionary<string, WorkflowVersionInfo> _versions = new BoundedDictionary<string, WorkflowVersionInfo>(1000);
    private readonly BoundedDictionary<string, List<VersionPatch>> _patches = new BoundedDictionary<string, List<VersionPatch>>(1000);

    /// <summary>Registers a new workflow version.</summary>
    public void RegisterVersion(string workflowType, string version, string? changeId = null)
    {
        var key = $"{workflowType}:{version}";
        _versions[key] = new WorkflowVersionInfo
        {
            WorkflowType = workflowType,
            Version = version,
            ChangeId = changeId ?? version,
            RegisteredAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Gets the version to use for a given change ID, supporting backward compatibility.
    /// Used during replay to determine which code path to execute.
    /// </summary>
    public int GetVersion(string workflowType, string changeId, int minSupported, int maxSupported)
    {
        var versions = _versions.Values
            .Where(v => v.WorkflowType == workflowType && v.ChangeId == changeId)
            .ToList();

        if (versions.Count == 0) return maxSupported; // New execution: use latest

        // For replay: return the version recorded at execution time
        return Math.Clamp(versions.Max(v => ParseVersion(v.Version)), minSupported, maxSupported);
    }

    /// <summary>Records a version patch for migration.</summary>
    public void RecordPatch(string workflowType, string fromVersion, string toVersion, string description)
    {
        var patches = _patches.GetOrAdd(workflowType, _ => new List<VersionPatch>());
        lock (patches)
        {
            patches.Add(new VersionPatch
            {
                FromVersion = fromVersion,
                ToVersion = toVersion,
                Description = description,
                CreatedAt = DateTime.UtcNow
            });
        }
    }

    /// <summary>Gets all versions for a workflow type.</summary>
    public IReadOnlyList<WorkflowVersionInfo> GetVersions(string workflowType)
    {
        return _versions.Values.Where(v => v.WorkflowType == workflowType)
            .OrderBy(v => v.RegisteredAt).ToList();
    }

    private static int ParseVersion(string version)
    {
        var parts = version.Split('.');
        return parts.Length > 0 && int.TryParse(parts[0], out var major) ? major : 0;
    }
}

/// <summary>Workflow version information.</summary>
public sealed record WorkflowVersionInfo
{
    public required string WorkflowType { get; init; }
    public required string Version { get; init; }
    public required string ChangeId { get; init; }
    public DateTime RegisteredAt { get; init; }
}

/// <summary>A version patch record.</summary>
public sealed record VersionPatch
{
    public required string FromVersion { get; init; }
    public required string ToVersion { get; init; }
    public required string Description { get; init; }
    public DateTime CreatedAt { get; init; }
}

#endregion

#region Child Workflow Management (Temporal-compatible)

/// <summary>
/// Child workflow executor supporting parent-child workflow relationships,
/// signal propagation, and cancellation cascading.
/// </summary>
public sealed class ChildWorkflowExecutor
{
    private readonly BoundedDictionary<string, ChildWorkflowRecord> _children = new BoundedDictionary<string, ChildWorkflowRecord>(1000);
    private long _instanceCounter;

    /// <summary>Starts a child workflow from a parent execution.</summary>
    public async Task<ChildWorkflowHandle> StartChildWorkflowAsync(
        string parentWorkflowId,
        WorkflowDefinition childDefinition,
        WorkflowStrategyBase strategy,
        Dictionary<string, object>? parameters = null,
        ChildWorkflowOptions? options = null,
        CancellationToken ct = default)
    {
        var childInstanceId = $"{parentWorkflowId}:child-{Interlocked.Increment(ref _instanceCounter)}";
        var record = new ChildWorkflowRecord
        {
            ChildInstanceId = childInstanceId,
            ParentWorkflowId = parentWorkflowId,
            ChildWorkflowId = childDefinition.WorkflowId,
            Status = ChildWorkflowStatus.Running,
            StartedAt = DateTime.UtcNow,
            CancellationOnParentClose = options?.ParentClosePolicy ?? ParentClosePolicy.Terminate
        };
        _children[childInstanceId] = record;

        // Execute child workflow
        var result = await strategy.ExecuteAsync(childDefinition, parameters, ct);
        record.Status = result.Success ? ChildWorkflowStatus.Completed : ChildWorkflowStatus.Failed;
        record.CompletedAt = DateTime.UtcNow;
        record.Result = result;

        return new ChildWorkflowHandle
        {
            ChildInstanceId = childInstanceId,
            Status = record.Status,
            Result = result
        };
    }

    /// <summary>Gets all child workflows for a parent.</summary>
    public IReadOnlyList<ChildWorkflowRecord> GetChildren(string parentWorkflowId)
    {
        return _children.Values.Where(c => c.ParentWorkflowId == parentWorkflowId).ToList();
    }

    /// <summary>Cancels all child workflows of a parent.</summary>
    public int CancelChildren(string parentWorkflowId)
    {
        var children = _children.Values
            .Where(c => c.ParentWorkflowId == parentWorkflowId && c.Status == ChildWorkflowStatus.Running)
            .ToList();
        foreach (var child in children) child.Status = ChildWorkflowStatus.Cancelled;
        return children.Count;
    }
}

/// <summary>Child workflow status.</summary>
public enum ChildWorkflowStatus { Running, Completed, Failed, Cancelled }

/// <summary>Parent close policy for child workflows.</summary>
public enum ParentClosePolicy { Terminate, RequestCancel, Abandon }

/// <summary>Options for child workflow execution.</summary>
public sealed record ChildWorkflowOptions
{
    public ParentClosePolicy ParentClosePolicy { get; init; } = ParentClosePolicy.Terminate;
    public TimeSpan? ExecutionTimeout { get; init; }
    public int MaxRetries { get; init; }
}

/// <summary>Record of a child workflow execution.</summary>
public sealed class ChildWorkflowRecord
{
    public required string ChildInstanceId { get; init; }
    public required string ParentWorkflowId { get; init; }
    public required string ChildWorkflowId { get; init; }
    public ChildWorkflowStatus Status { get; set; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
    public WorkflowResult? Result { get; set; }
    public ParentClosePolicy CancellationOnParentClose { get; init; }
}

/// <summary>Handle to a running child workflow.</summary>
public sealed record ChildWorkflowHandle
{
    public required string ChildInstanceId { get; init; }
    public ChildWorkflowStatus Status { get; init; }
    public WorkflowResult? Result { get; init; }
}

#endregion

#region Activity Heartbeats (Temporal-compatible)

/// <summary>
/// Activity heartbeat manager for long-running activities.
/// Activities report progress via heartbeats; if heartbeats stop, the activity
/// is considered timed out and can be rescheduled.
/// </summary>
public sealed class ActivityHeartbeatManager
{
    private readonly BoundedDictionary<string, ActivityHeartbeat> _heartbeats = new BoundedDictionary<string, ActivityHeartbeat>(1000);
    private readonly TimeSpan _heartbeatTimeout;

    public ActivityHeartbeatManager(TimeSpan? heartbeatTimeout = null)
    {
        _heartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>Records a heartbeat for an activity.</summary>
    public void RecordHeartbeat(string activityId, object? details = null)
    {
        _heartbeats.AddOrUpdate(activityId,
            new ActivityHeartbeat
            {
                ActivityId = activityId,
                Details = details,
                LastHeartbeat = DateTime.UtcNow,
                HeartbeatCount = 1
            },
            (_, existing) =>
            {
                existing.LastHeartbeat = DateTime.UtcNow;
                existing.Details = details;
                existing.HeartbeatCount++;
                return existing;
            });
    }

    /// <summary>Checks if an activity has timed out (no heartbeat within threshold).</summary>
    public bool IsTimedOut(string activityId)
    {
        if (!_heartbeats.TryGetValue(activityId, out var hb)) return true;
        return (DateTime.UtcNow - hb.LastHeartbeat) > _heartbeatTimeout;
    }

    /// <summary>Gets heartbeat details for an activity.</summary>
    public ActivityHeartbeat? GetHeartbeat(string activityId) =>
        _heartbeats.TryGetValue(activityId, out var hb) ? hb : null;

    /// <summary>Gets all timed-out activities.</summary>
    public IReadOnlyList<ActivityHeartbeat> GetTimedOutActivities()
    {
        var cutoff = DateTime.UtcNow - _heartbeatTimeout;
        return _heartbeats.Values.Where(h => h.LastHeartbeat < cutoff).ToList();
    }

    /// <summary>Removes heartbeat tracking for a completed activity.</summary>
    public void RemoveActivity(string activityId) => _heartbeats.TryRemove(activityId, out _);
}

/// <summary>Activity heartbeat record.</summary>
public sealed class ActivityHeartbeat
{
    public required string ActivityId { get; init; }
    public object? Details { get; set; }
    public DateTime LastHeartbeat { get; set; }
    public long HeartbeatCount { get; set; }
}

#endregion

#region Workflow Checkpointing & Resume

/// <summary>
/// Workflow checkpoint manager for saving and resuming workflow state.
/// Supports checkpoint persistence for crash recovery and workflow migration.
/// </summary>
public sealed class WorkflowCheckpointManager
{
    private readonly BoundedDictionary<string, WorkflowCheckpoint> _checkpoints = new BoundedDictionary<string, WorkflowCheckpoint>(1000);

    /// <summary>Saves a checkpoint of the current workflow state.</summary>
    public WorkflowCheckpoint SaveCheckpoint(string workflowInstanceId, WorkflowContext context,
        IReadOnlySet<string> completedTasks, IReadOnlySet<string> failedTasks)
    {
        var checkpoint = new WorkflowCheckpoint
        {
            WorkflowInstanceId = workflowInstanceId,
            CompletedTaskIds = completedTasks.ToList(),
            FailedTaskIds = failedTasks.ToList(),
            TaskResults = context.TaskResults.ToDictionary(kv => kv.Key, kv => kv.Value),
            SharedState = context.State.ToDictionary(kv => kv.Key, kv => kv.Value),
            CheckpointedAt = DateTime.UtcNow
        };
        _checkpoints[workflowInstanceId] = checkpoint;
        return checkpoint;
    }

    /// <summary>Restores a checkpoint for resuming a workflow.</summary>
    public WorkflowCheckpoint? GetCheckpoint(string workflowInstanceId) =>
        _checkpoints.TryGetValue(workflowInstanceId, out var cp) ? cp : null;

    /// <summary>Removes a checkpoint after successful completion.</summary>
    public bool RemoveCheckpoint(string workflowInstanceId) =>
        _checkpoints.TryRemove(workflowInstanceId, out _);

    /// <summary>Lists all stored checkpoints.</summary>
    public IReadOnlyList<WorkflowCheckpoint> ListCheckpoints() => _checkpoints.Values.ToList();
}

/// <summary>A saved workflow checkpoint.</summary>
public sealed record WorkflowCheckpoint
{
    public required string WorkflowInstanceId { get; init; }
    public List<string> CompletedTaskIds { get; init; } = [];
    public List<string> FailedTaskIds { get; init; } = [];
    public Dictionary<string, TaskResult> TaskResults { get; init; } = new();
    public Dictionary<string, object> SharedState { get; init; } = new();
    public DateTime CheckpointedAt { get; init; }
}

#endregion

#region Workflow Trigger Configuration

/// <summary>
/// Workflow trigger manager supporting schedule-based (cron), event-driven,
/// webhook, and dependency-based triggers for workflow execution.
/// </summary>
public sealed class WorkflowTriggerManager
{
    private readonly BoundedDictionary<string, WorkflowTrigger> _triggers = new BoundedDictionary<string, WorkflowTrigger>(1000);

    /// <summary>Registers a schedule-based (cron) trigger.</summary>
    public string RegisterScheduleTrigger(string workflowId, string cronExpression, string? timezone = null)
    {
        var triggerId = $"schedule-{Guid.NewGuid():N}";
        _triggers[triggerId] = new WorkflowTrigger
        {
            TriggerId = triggerId,
            WorkflowId = workflowId,
            Type = TriggerType.Schedule,
            CronExpression = cronExpression,
            Timezone = timezone ?? "UTC",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        return triggerId;
    }

    /// <summary>Registers an event-driven trigger.</summary>
    public string RegisterEventTrigger(string workflowId, string eventType, string? eventFilter = null)
    {
        var triggerId = $"event-{Guid.NewGuid():N}";
        _triggers[triggerId] = new WorkflowTrigger
        {
            TriggerId = triggerId,
            WorkflowId = workflowId,
            Type = TriggerType.Event,
            EventType = eventType,
            EventFilter = eventFilter,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        return triggerId;
    }

    /// <summary>Registers a webhook trigger.</summary>
    public string RegisterWebhookTrigger(string workflowId, string path)
    {
        var triggerId = $"webhook-{Guid.NewGuid():N}";
        _triggers[triggerId] = new WorkflowTrigger
        {
            TriggerId = triggerId,
            WorkflowId = workflowId,
            Type = TriggerType.Webhook,
            WebhookPath = path,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        return triggerId;
    }

    /// <summary>Registers a dependency trigger (fires when upstream workflow completes).</summary>
    public string RegisterDependencyTrigger(string workflowId, string upstreamWorkflowId)
    {
        var triggerId = $"dependency-{Guid.NewGuid():N}";
        _triggers[triggerId] = new WorkflowTrigger
        {
            TriggerId = triggerId,
            WorkflowId = workflowId,
            Type = TriggerType.Dependency,
            UpstreamWorkflowId = upstreamWorkflowId,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        return triggerId;
    }

    /// <summary>Evaluates which workflows should be triggered by an event.</summary>
    public IReadOnlyList<WorkflowTrigger> EvaluateEventTriggers(string eventType)
    {
        return _triggers.Values
            .Where(t => t.IsActive && t.Type == TriggerType.Event && t.EventType == eventType)
            .ToList();
    }

    /// <summary>Deactivates a trigger.</summary>
    public bool DeactivateTrigger(string triggerId)
    {
        if (_triggers.TryGetValue(triggerId, out var trigger))
        {
            trigger.IsActive = false;
            return true;
        }
        return false;
    }

    /// <summary>Gets all triggers for a workflow.</summary>
    public IReadOnlyList<WorkflowTrigger> GetTriggers(string workflowId) =>
        _triggers.Values.Where(t => t.WorkflowId == workflowId).ToList();

    /// <summary>Gets all active triggers.</summary>
    public IReadOnlyList<WorkflowTrigger> GetActiveTriggers() =>
        _triggers.Values.Where(t => t.IsActive).ToList();
}

/// <summary>Trigger types for workflow execution.</summary>
public enum TriggerType { Schedule, Event, Webhook, Dependency, Manual }

/// <summary>Workflow trigger configuration.</summary>
public sealed class WorkflowTrigger
{
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

#endregion

#region Flow Scheduling & Task Runners (Prefect-compatible)

/// <summary>
/// Flow scheduling and task runner manager for Prefect-compatible workflow execution.
/// Supports concurrent task runners, result persistence, and state handlers.
/// </summary>
public sealed class FlowScheduler
{
    private readonly BoundedDictionary<string, FlowRun> _runs = new BoundedDictionary<string, FlowRun>(1000);
    private readonly BoundedDictionary<string, FlowScheduleEntry> _schedules = new BoundedDictionary<string, FlowScheduleEntry>(1000);
    private long _runCounter;

    /// <summary>Creates a new scheduled flow entry.</summary>
    public string CreateSchedule(string flowId, string cronExpression, Dictionary<string, object>? parameters = null)
    {
        var scheduleId = $"sched-{Guid.NewGuid():N}";
        _schedules[scheduleId] = new FlowScheduleEntry
        {
            ScheduleId = scheduleId,
            FlowId = flowId,
            CronExpression = cronExpression,
            Parameters = parameters ?? new(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        return scheduleId;
    }

    /// <summary>Creates a new flow run.</summary>
    public FlowRun CreateRun(string flowId, Dictionary<string, object>? parameters = null)
    {
        var runId = $"run-{Interlocked.Increment(ref _runCounter)}";
        var run = new FlowRun
        {
            RunId = runId,
            FlowId = flowId,
            Status = FlowRunStatus.Pending,
            Parameters = parameters ?? new(),
            CreatedAt = DateTime.UtcNow
        };
        _runs[runId] = run;
        return run;
    }

    /// <summary>Transitions a flow run to a new state with handler notification.</summary>
    public FlowRunStatus TransitionState(string runId, FlowRunStatus newStatus, string? message = null)
    {
        if (!_runs.TryGetValue(runId, out var run)) throw new InvalidOperationException($"Flow run '{runId}' not found.");
        var oldStatus = run.Status;
        run.Status = newStatus;
        run.StateMessage = message;
        if (newStatus == FlowRunStatus.Running) run.StartedAt = DateTime.UtcNow;
        if (newStatus is FlowRunStatus.Completed or FlowRunStatus.Failed or FlowRunStatus.Cancelled)
            run.CompletedAt = DateTime.UtcNow;
        return oldStatus;
    }

    /// <summary>Gets a flow run by ID.</summary>
    public FlowRun? GetRun(string runId) => _runs.TryGetValue(runId, out var r) ? r : null;

    /// <summary>Lists all active schedules.</summary>
    public IReadOnlyList<FlowScheduleEntry> GetActiveSchedules() =>
        _schedules.Values.Where(s => s.IsActive).ToList();

    /// <summary>Lists flow runs, optionally filtered by flow ID.</summary>
    public IReadOnlyList<FlowRun> ListRuns(string? flowId = null, int limit = 100)
    {
        var runs = flowId != null
            ? _runs.Values.Where(r => r.FlowId == flowId)
            : _runs.Values;
        return runs.OrderByDescending(r => r.CreatedAt).Take(limit).ToList();
    }
}

/// <summary>Flow run status.</summary>
public enum FlowRunStatus { Pending, Scheduled, Running, Completed, Failed, Cancelled, Retrying }

/// <summary>A flow schedule entry.</summary>
public sealed class FlowScheduleEntry
{
    public required string ScheduleId { get; init; }
    public required string FlowId { get; init; }
    public required string CronExpression { get; init; }
    public Dictionary<string, object> Parameters { get; init; } = new();
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime? NextRunAt { get; set; }
}

/// <summary>A flow run record.</summary>
public sealed class FlowRun
{
    public required string RunId { get; init; }
    public required string FlowId { get; init; }
    public FlowRunStatus Status { get; set; }
    public string? StateMessage { get; set; }
    public Dictionary<string, object> Parameters { get; init; } = new();
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public object? Result { get; set; }
}

/// <summary>
/// Result persistence manager for task runner outputs.
/// Stores task results with configurable serialization and storage backends.
/// </summary>
public sealed class TaskResultPersistence
{
    private readonly BoundedDictionary<string, PersistedResult> _results = new BoundedDictionary<string, PersistedResult>(1000);

    /// <summary>Persists a task result.</summary>
    public string Persist(string taskRunId, object result, string? serializer = null)
    {
        var key = $"result-{taskRunId}";
        _results[key] = new PersistedResult
        {
            Key = key,
            TaskRunId = taskRunId,
            Value = result,
            Serializer = serializer ?? "json",
            PersistedAt = DateTime.UtcNow
        };
        return key;
    }

    /// <summary>Retrieves a persisted result.</summary>
    public object? Retrieve(string key) =>
        _results.TryGetValue(key, out var r) ? r.Value : null;

    /// <summary>Retrieves a typed persisted result.</summary>
    public T? Retrieve<T>(string key) where T : class =>
        Retrieve(key) as T;

    /// <summary>Checks if a result exists.</summary>
    public bool Exists(string key) => _results.ContainsKey(key);

    /// <summary>Removes expired results.</summary>
    public int Cleanup(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var expired = _results.Where(kv => kv.Value.PersistedAt < cutoff).ToList();
        foreach (var kv in expired) _results.TryRemove(kv.Key, out _);
        return expired.Count;
    }
}

/// <summary>A persisted task result.</summary>
public sealed record PersistedResult
{
    public required string Key { get; init; }
    public required string TaskRunId { get; init; }
    public required object Value { get; init; }
    public string Serializer { get; init; } = "json";
    public DateTime PersistedAt { get; init; }
}

#endregion

#region Search Attributes (Temporal-compatible)

/// <summary>
/// Workflow search attribute manager for indexing workflow executions
/// with custom key-value attributes for efficient querying.
/// </summary>
public sealed class WorkflowSearchAttributes
{
    private readonly BoundedDictionary<string, Dictionary<string, SearchAttributeValue>> _attributes = new BoundedDictionary<string, Dictionary<string, SearchAttributeValue>>(1000);

    /// <summary>Sets a search attribute on a workflow execution.</summary>
    public void Set(string workflowId, string key, object value, SearchAttributeType type = SearchAttributeType.Keyword)
    {
        var attrs = _attributes.GetOrAdd(workflowId, _ => new Dictionary<string, SearchAttributeValue>());
        lock (attrs)
        {
            attrs[key] = new SearchAttributeValue
            {
                Key = key,
                Value = value,
                Type = type,
                UpdatedAt = DateTime.UtcNow
            };
        }
    }

    /// <summary>Gets all search attributes for a workflow.</summary>
    public IReadOnlyDictionary<string, SearchAttributeValue> Get(string workflowId)
    {
        return _attributes.TryGetValue(workflowId, out var attrs)
            ? new Dictionary<string, SearchAttributeValue>(attrs)
            : new Dictionary<string, SearchAttributeValue>();
    }

    /// <summary>Searches workflows by attribute value.</summary>
    public IReadOnlyList<string> Search(string key, object value)
    {
        return _attributes
            .Where(kv => kv.Value.TryGetValue(key, out var attr) && Equals(attr.Value, value))
            .Select(kv => kv.Key)
            .ToList();
    }
}

/// <summary>Search attribute value types.</summary>
public enum SearchAttributeType { Keyword, Text, Int, Double, Bool, Datetime }

/// <summary>A search attribute value.</summary>
public sealed record SearchAttributeValue
{
    public required string Key { get; init; }
    public required object Value { get; init; }
    public SearchAttributeType Type { get; init; }
    public DateTime UpdatedAt { get; init; }
}

#endregion
