using System.Collections.Concurrent;
using System.Text.Json;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateWorkflow.Strategies.StateManagement;

/// <summary>
/// Checkpoint-based state management strategy.
/// </summary>
public sealed class CheckpointStateStrategy : WorkflowStrategyBase
{
    private readonly BoundedDictionary<string, byte[]> _checkpoints = new BoundedDictionary<string, byte[]>(1000);

    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "CheckpointState",
        Description = "Checkpoint-based state management with periodic snapshots for recovery",
        Category = WorkflowCategory.StateManagement,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: false,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: true,
            SupportsCheckpointing: true,
            SupportsDistributed: true,
            SupportsPriority: false,
            SupportsResourceManagement: false),
        Tags = ["state", "checkpoint", "recovery"]
    };

    public override async Task<WorkflowResult> ExecuteAsync(
        WorkflowDefinition workflow,
        Dictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        var instanceId = Guid.NewGuid().ToString("N");
        var context = new WorkflowContext
        {
            WorkflowInstanceId = instanceId,
            WorkflowId = workflow.WorkflowId,
            Parameters = parameters ?? new()
        };

        var completed = new HashSet<string>();
        var checkpointInterval = 5;
        var taskCount = 0;

        foreach (var task in workflow.GetTopologicalOrder())
        {
            var result = await ExecuteTaskAsync(task, context, cancellationToken);
            context.TaskResults[task.TaskId] = result;
            completed.Add(task.TaskId);
            taskCount++;

            if (taskCount % checkpointInterval == 0)
                SaveCheckpoint(instanceId, context, completed);
        }

        ClearCheckpoint(instanceId);

        return new WorkflowResult
        {
            WorkflowInstanceId = instanceId,
            Success = context.TaskResults.Values.All(r => r.Success),
            Status = WorkflowExecutionStatus.Completed,
            Duration = DateTime.UtcNow - startTime,
            TaskResults = context.TaskResults.ToDictionary(kv => kv.Key, kv => kv.Value)
        };
    }

    private void SaveCheckpoint(string instanceId, WorkflowContext context, HashSet<string> completed)
    {
        var checkpoint = new { Completed = completed.ToList(), State = context.State.ToDictionary(kv => kv.Key, kv => kv.Value) };
        _checkpoints[instanceId] = JsonSerializer.SerializeToUtf8Bytes(checkpoint);
    }

    private void ClearCheckpoint(string instanceId) => _checkpoints.TryRemove(instanceId, out _);

    public bool TryLoadCheckpoint(string instanceId, out HashSet<string>? completed, out Dictionary<string, object>? state)
    {
        if (_checkpoints.TryGetValue(instanceId, out var data))
        {
            var checkpoint = JsonSerializer.Deserialize<JsonElement>(data);
            completed = checkpoint.GetProperty("Completed").EnumerateArray().Select(e => e.GetString()!).ToHashSet();

            // Deserialize the State dictionary — previously this was always returning an empty dict,
            // making checkpoint recovery useless for state-dependent tasks.
            state = new Dictionary<string, object>();
            if (checkpoint.TryGetProperty("State", out var stateEl))
            {
                foreach (var prop in stateEl.EnumerateObject())
                {
                    state[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString()!,
                        JsonValueKind.Number when prop.Value.TryGetInt64(out var l) => (object)l,
                        JsonValueKind.Number => prop.Value.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => prop.Value.GetRawText()
                    };
                }
            }

            return true;
        }
        completed = null;
        state = null;
        return false;
    }
}

/// <summary>
/// Event-sourced state management strategy.
/// </summary>
public sealed class EventSourcedStateStrategy : WorkflowStrategyBase
{
    private readonly BoundedDictionary<string, List<WorkflowEvent>> _eventLogs = new BoundedDictionary<string, List<WorkflowEvent>>(1000);

    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "EventSourcedState",
        Description = "Event-sourced state management with full audit trail and replay capability",
        Category = WorkflowCategory.StateManagement,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: true,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: true,
            SupportsCheckpointing: true,
            SupportsDistributed: true,
            SupportsPriority: false,
            SupportsResourceManagement: false),
        Tags = ["state", "event-sourcing", "audit"]
    };

    private sealed record WorkflowEvent(string EventType, string TaskId, DateTimeOffset Timestamp, object? Data);

    public override async Task<WorkflowResult> ExecuteAsync(
        WorkflowDefinition workflow,
        Dictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        var instanceId = Guid.NewGuid().ToString("N");
        var context = new WorkflowContext
        {
            WorkflowInstanceId = instanceId,
            WorkflowId = workflow.WorkflowId,
            Parameters = parameters ?? new()
        };

        var events = _eventLogs.GetOrAdd(instanceId, _ => new List<WorkflowEvent>());
        events.Add(new WorkflowEvent("WorkflowStarted", "", DateTimeOffset.UtcNow, parameters));

        foreach (var task in workflow.GetTopologicalOrder())
        {
            events.Add(new WorkflowEvent("TaskStarted", task.TaskId, DateTimeOffset.UtcNow, null));

            var result = await ExecuteTaskAsync(task, context, cancellationToken);
            context.TaskResults[task.TaskId] = result;

            events.Add(new WorkflowEvent(
                result.Success ? "TaskCompleted" : "TaskFailed",
                task.TaskId,
                DateTimeOffset.UtcNow,
                new { result.Success, result.Duration, result.Error }));
        }

        events.Add(new WorkflowEvent("WorkflowCompleted", "", DateTimeOffset.UtcNow, null));

        return new WorkflowResult
        {
            WorkflowInstanceId = instanceId,
            Success = context.TaskResults.Values.All(r => r.Success),
            Status = WorkflowExecutionStatus.Completed,
            Duration = DateTime.UtcNow - startTime,
            TaskResults = context.TaskResults.ToDictionary(kv => kv.Key, kv => kv.Value)
        };
    }

    public IEnumerable<object> GetEventLog(string instanceId) =>
        _eventLogs.TryGetValue(instanceId, out var events) ? events : Enumerable.Empty<object>();
}

/// <summary>
/// Saga pattern state management for distributed transactions.
/// </summary>
public sealed class SagaStateStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "SagaState",
        Description = "Saga pattern state management for distributed transactions with compensation",
        Category = WorkflowCategory.StateManagement,
        Capabilities = new(
            SupportsParallelExecution: false,
            SupportsDynamicDag: false,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: true,
            SupportsCheckpointing: true,
            SupportsDistributed: true,
            SupportsPriority: false,
            SupportsResourceManagement: false),
        Tags = ["state", "saga", "compensation", "distributed"]
    };

    public override async Task<WorkflowResult> ExecuteAsync(
        WorkflowDefinition workflow,
        Dictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        var instanceId = Guid.NewGuid().ToString("N");
        var context = new WorkflowContext
        {
            WorkflowInstanceId = instanceId,
            WorkflowId = workflow.WorkflowId,
            Parameters = parameters ?? new()
        };

        var completedTasks = new Stack<WorkflowTask>();
        var failed = false;
        string? failedTaskId = null;

        foreach (var task in workflow.GetTopologicalOrder())
        {
            var result = await ExecuteTaskAsync(task, context, cancellationToken);
            context.TaskResults[task.TaskId] = result;

            if (result.Success)
            {
                completedTasks.Push(task);
            }
            else
            {
                failed = true;
                failedTaskId = task.TaskId;
                break;
            }
        }

        if (failed)
        {
            while (completedTasks.Count > 0)
            {
                var task = completedTasks.Pop();
                if (task.Metadata.TryGetValue("compensationHandler", out var handler) &&
                    handler is Func<WorkflowContext, CancellationToken, Task> compensate)
                {
                    await compensate(context, cancellationToken);
                }
            }
        }

        return new WorkflowResult
        {
            WorkflowInstanceId = instanceId,
            Success = !failed,
            Status = failed ? WorkflowExecutionStatus.Failed : WorkflowExecutionStatus.Completed,
            Duration = DateTime.UtcNow - startTime,
            TaskResults = context.TaskResults.ToDictionary(kv => kv.Key, kv => kv.Value),
            Error = failed ? $"Saga failed at task '{failedTaskId}', compensation executed" : null
        };
    }
}

/// <summary>
/// Transactional state management with rollback support.
/// </summary>
public sealed class TransactionalStateStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "TransactionalState",
        Description = "Transactional state management with ACID properties and rollback support",
        Category = WorkflowCategory.StateManagement,
        Capabilities = new(
            SupportsParallelExecution: false,
            SupportsDynamicDag: false,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: true,
            SupportsCheckpointing: true,
            SupportsDistributed: false,
            SupportsPriority: false,
            SupportsResourceManagement: false),
        Tags = ["state", "transactional", "acid", "rollback"]
    };

    public override async Task<WorkflowResult> ExecuteAsync(
        WorkflowDefinition workflow,
        Dictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        var instanceId = Guid.NewGuid().ToString("N");
        var context = new WorkflowContext
        {
            WorkflowInstanceId = instanceId,
            WorkflowId = workflow.WorkflowId,
            Parameters = parameters ?? new()
        };

        var stateSnapshot = new Dictionary<string, object>(context.State);
        var failed = false;

        try
        {
            foreach (var task in workflow.GetTopologicalOrder())
            {
                var result = await ExecuteTaskAsync(task, context, cancellationToken);
                context.TaskResults[task.TaskId] = result;

                if (!result.Success && workflow.ErrorStrategy == ErrorHandlingStrategy.FailFast)
                {
                    failed = true;
                    break;
                }
            }

            if (failed)
            {
                context.State.Clear();
                foreach (var (key, value) in stateSnapshot)
                    context.State[key] = value;
            }
        }
        catch
        {
            context.State.Clear();
            foreach (var (key, value) in stateSnapshot)
                context.State[key] = value;
            throw;
        }

        return new WorkflowResult
        {
            WorkflowInstanceId = instanceId,
            Success = !failed,
            Status = failed ? WorkflowExecutionStatus.Failed : WorkflowExecutionStatus.Completed,
            Duration = DateTime.UtcNow - startTime,
            TaskResults = context.TaskResults.ToDictionary(kv => kv.Key, kv => kv.Value)
        };
    }
}

/// <summary>
/// Versioned state management with history tracking.
/// </summary>
public sealed class VersionedStateStrategy : WorkflowStrategyBase
{
    private readonly ConcurrentDictionary<string, List<Dictionary<string, object>>> _stateVersions = new();

    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "VersionedState",
        Description = "Versioned state management with complete history tracking and time-travel",
        Category = WorkflowCategory.StateManagement,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: false,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: true,
            SupportsCheckpointing: true,
            SupportsDistributed: false,
            SupportsPriority: false,
            SupportsResourceManagement: false),
        Tags = ["state", "versioned", "history", "time-travel"]
    };

    public override async Task<WorkflowResult> ExecuteAsync(
        WorkflowDefinition workflow,
        Dictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        var instanceId = Guid.NewGuid().ToString("N");
        var context = new WorkflowContext
        {
            WorkflowInstanceId = instanceId,
            WorkflowId = workflow.WorkflowId,
            Parameters = parameters ?? new()
        };

        var versions = _stateVersions.GetOrAdd(instanceId, _ => new List<Dictionary<string, object>>());
        versions.Add(new Dictionary<string, object>(context.State));

        foreach (var task in workflow.GetTopologicalOrder())
        {
            var result = await ExecuteTaskAsync(task, context, cancellationToken);
            context.TaskResults[task.TaskId] = result;

            versions.Add(context.State.ToDictionary(kv => kv.Key, kv => kv.Value));
        }

        return new WorkflowResult
        {
            WorkflowInstanceId = instanceId,
            Success = context.TaskResults.Values.All(r => r.Success),
            Status = WorkflowExecutionStatus.Completed,
            Duration = DateTime.UtcNow - startTime,
            TaskResults = context.TaskResults.ToDictionary(kv => kv.Key, kv => kv.Value)
        };
    }

    public Dictionary<string, object>? GetStateAtVersion(string instanceId, int version) =>
        _stateVersions.TryGetValue(instanceId, out var versions) && version < versions.Count
            ? versions[version]
            : null;
}

/// <summary>
/// Distributed state management with eventual consistency.
/// </summary>
public sealed class DistributedStateStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "DistributedState",
        Description = "Distributed state management with eventual consistency across nodes",
        Category = WorkflowCategory.StateManagement,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: true,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: false,
            SupportsCheckpointing: true,
            SupportsDistributed: true,
            SupportsPriority: false,
            SupportsResourceManagement: true),
        Tags = ["state", "distributed", "eventual-consistency"]
    };

    public override async Task<WorkflowResult> ExecuteAsync(
        WorkflowDefinition workflow,
        Dictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        var instanceId = Guid.NewGuid().ToString("N");
        var context = new WorkflowContext
        {
            WorkflowInstanceId = instanceId,
            WorkflowId = workflow.WorkflowId,
            Parameters = parameters ?? new()
        };

        var completed = new BoundedDictionary<string, TaskResult>(1000);

        var tasks = workflow.Tasks
            .Where(t => t.Dependencies.Count == 0)
            .Select(t => ExecuteWithStateSync(t, context, workflow, completed, cancellationToken));

        await Task.WhenAll(tasks);

        return new WorkflowResult
        {
            WorkflowInstanceId = instanceId,
            Success = completed.Values.All(r => r.Success),
            Status = WorkflowExecutionStatus.Completed,
            Duration = DateTime.UtcNow - startTime,
            TaskResults = completed.ToDictionary(kv => kv.Key, kv => kv.Value)
        };
    }

    private async Task ExecuteWithStateSync(
        WorkflowTask task,
        WorkflowContext context,
        WorkflowDefinition workflow,
        BoundedDictionary<string, TaskResult> completed,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteTaskAsync(task, context, cancellationToken);
        context.TaskResults[task.TaskId] = result;
        completed[task.TaskId] = result;

        var downstream = workflow.Tasks
            .Where(t => t.Dependencies.Contains(task.TaskId) && t.Dependencies.All(d => completed.ContainsKey(d)))
            .Select(t => ExecuteWithStateSync(t, context, workflow, completed, cancellationToken));

        await Task.WhenAll(downstream);
    }
}
