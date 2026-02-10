using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateWorkflow.Strategies.DagExecution;

/// <summary>
/// Standard DAG execution strategy with topological ordering.
/// </summary>
public sealed class TopologicalDagStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "TopologicalDag",
        Description = "Standard DAG execution with topological ordering and parallel task execution",
        Category = WorkflowCategory.DagExecution,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: false,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: false,
            SupportsCheckpointing: false,
            SupportsDistributed: false,
            SupportsPriority: true,
            SupportsResourceManagement: false),
        TypicalTaskOverheadMs = 1,
        Tags = ["dag", "topological", "parallel"]
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
        var failed = new HashSet<string>();
        var running = new ConcurrentDictionary<string, Task<TaskResult>>();
        var semaphore = new SemaphoreSlim(workflow.MaxParallelism);

        try
        {
            while (completed.Count + failed.Count < workflow.Tasks.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var readyTasks = GetReadyTasks(workflow, completed, running.Keys.ToHashSet())
                    .OrderByDescending(t => (int)t.Priority)
                    .ToList();

                foreach (var task in readyTasks)
                {
                    await semaphore.WaitAsync(cancellationToken);

                    var taskExecution = Task.Run(async () =>
                    {
                        try
                        {
                            return await ExecuteTaskAsync(task, context, cancellationToken);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, cancellationToken);

                    running[task.TaskId] = taskExecution;
                }

                if (running.Count == 0 && completed.Count + failed.Count < workflow.Tasks.Count)
                    break;

                var completedTask = await Task.WhenAny(running.Values);

                foreach (var kvp in running.Where(kv => kv.Value.IsCompleted).ToList())
                {
                    running.TryRemove(kvp.Key, out _);
                    var result = await kvp.Value;
                    context.TaskResults[kvp.Key] = result;

                    if (result.Success)
                        completed.Add(kvp.Key);
                    else
                    {
                        failed.Add(kvp.Key);
                        if (workflow.ErrorStrategy == ErrorHandlingStrategy.FailFast)
                        {
                            return new WorkflowResult
                            {
                                WorkflowInstanceId = instanceId,
                                Success = false,
                                Status = WorkflowExecutionStatus.Failed,
                                Duration = DateTime.UtcNow - startTime,
                                TaskResults = context.TaskResults.ToDictionary(kv => kv.Key, kv => kv.Value),
                                Error = $"Task '{kvp.Key}' failed: {result.Error}"
                            };
                        }
                    }
                }
            }

            return new WorkflowResult
            {
                WorkflowInstanceId = instanceId,
                Success = failed.Count == 0,
                Status = failed.Count == 0 ? WorkflowExecutionStatus.Completed : WorkflowExecutionStatus.Failed,
                Duration = DateTime.UtcNow - startTime,
                TaskResults = context.TaskResults.ToDictionary(kv => kv.Key, kv => kv.Value)
            };
        }
        catch (OperationCanceledException)
        {
            return new WorkflowResult
            {
                WorkflowInstanceId = instanceId,
                Success = false,
                Status = WorkflowExecutionStatus.Cancelled,
                Duration = DateTime.UtcNow - startTime,
                TaskResults = context.TaskResults.ToDictionary(kv => kv.Key, kv => kv.Value)
            };
        }
    }
}

/// <summary>
/// Dynamic DAG execution that supports runtime task addition.
/// </summary>
public sealed class DynamicDagStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "DynamicDag",
        Description = "Dynamic DAG execution supporting runtime task addition and modification",
        Category = WorkflowCategory.DagExecution,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: true,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: false,
            SupportsCheckpointing: true,
            SupportsDistributed: false,
            SupportsPriority: true,
            SupportsResourceManagement: true),
        Tags = ["dag", "dynamic", "runtime"]
    };

    private readonly ConcurrentDictionary<string, List<WorkflowTask>> _dynamicTasks = new();

    public void AddDynamicTask(string workflowInstanceId, WorkflowTask task)
    {
        var tasks = _dynamicTasks.GetOrAdd(workflowInstanceId, _ => new List<WorkflowTask>());
        lock (tasks) tasks.Add(task);
    }

    public override async Task<WorkflowResult> ExecuteAsync(
        WorkflowDefinition workflow,
        Dictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        var instanceId = Guid.NewGuid().ToString("N");
        _dynamicTasks[instanceId] = new List<WorkflowTask>();

        var context = new WorkflowContext
        {
            WorkflowInstanceId = instanceId,
            WorkflowId = workflow.WorkflowId,
            Parameters = parameters ?? new()
        };

        var allTasks = new List<WorkflowTask>(workflow.Tasks);
        var completed = new HashSet<string>();
        var failed = new HashSet<string>();

        try
        {
            while (true)
            {
                lock (_dynamicTasks[instanceId])
                {
                    allTasks.AddRange(_dynamicTasks[instanceId]);
                    _dynamicTasks[instanceId].Clear();
                }

                var readyTasks = allTasks
                    .Where(t => !completed.Contains(t.TaskId) && !failed.Contains(t.TaskId))
                    .Where(t => t.Dependencies.All(d => completed.Contains(d)))
                    .ToList();

                if (readyTasks.Count == 0 && completed.Count + failed.Count >= allTasks.Count)
                    break;

                var executionTasks = readyTasks
                    .Take(workflow.MaxParallelism)
                    .Select(async task =>
                    {
                        var result = await ExecuteTaskAsync(task, context, cancellationToken);
                        context.TaskResults[task.TaskId] = result;
                        return (task.TaskId, result);
                    });

                foreach (var (taskId, result) in await Task.WhenAll(executionTasks))
                {
                    if (result.Success) completed.Add(taskId);
                    else failed.Add(taskId);
                }

                if (readyTasks.Count == 0)
                    await Task.Delay(10, cancellationToken);
            }

            return new WorkflowResult
            {
                WorkflowInstanceId = instanceId,
                Success = failed.Count == 0,
                Status = failed.Count == 0 ? WorkflowExecutionStatus.Completed : WorkflowExecutionStatus.Failed,
                Duration = DateTime.UtcNow - startTime,
                TaskResults = context.TaskResults.ToDictionary(kv => kv.Key, kv => kv.Value)
            };
        }
        finally
        {
            _dynamicTasks.TryRemove(instanceId, out _);
        }
    }
}

/// <summary>
/// Layered DAG execution processing tasks in dependency layers.
/// </summary>
public sealed class LayeredDagStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "LayeredDag",
        Description = "Layered DAG execution processing tasks in dependency-based layers for deterministic execution",
        Category = WorkflowCategory.DagExecution,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: false,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: false,
            SupportsCheckpointing: true,
            SupportsDistributed: true,
            SupportsPriority: false,
            SupportsResourceManagement: false),
        Tags = ["dag", "layered", "deterministic"]
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

        var layers = ComputeLayers(workflow);
        var failed = false;

        foreach (var layer in layers)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var layerTasks = layer.Select(async task =>
            {
                var result = await ExecuteTaskAsync(task, context, cancellationToken);
                context.TaskResults[task.TaskId] = result;
                return (task.TaskId, result);
            });

            foreach (var (taskId, result) in await Task.WhenAll(layerTasks))
            {
                if (!result.Success)
                {
                    failed = true;
                    if (workflow.ErrorStrategy == ErrorHandlingStrategy.FailFast)
                        break;
                }
            }

            if (failed && workflow.ErrorStrategy == ErrorHandlingStrategy.FailFast)
                break;
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

    private List<List<WorkflowTask>> ComputeLayers(WorkflowDefinition workflow)
    {
        var layers = new List<List<WorkflowTask>>();
        var assigned = new HashSet<string>();

        while (assigned.Count < workflow.Tasks.Count)
        {
            var layer = workflow.Tasks
                .Where(t => !assigned.Contains(t.TaskId))
                .Where(t => t.Dependencies.All(d => assigned.Contains(d)))
                .ToList();

            if (layer.Count == 0) break;

            layers.Add(layer);
            foreach (var task in layer)
                assigned.Add(task.TaskId);
        }

        return layers;
    }
}

/// <summary>
/// Critical path DAG execution optimizing for minimal completion time.
/// </summary>
public sealed class CriticalPathDagStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "CriticalPathDag",
        Description = "Critical path DAG execution optimizing task ordering for minimal completion time",
        Category = WorkflowCategory.DagExecution,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: false,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: false,
            SupportsCheckpointing: false,
            SupportsDistributed: false,
            SupportsPriority: true,
            SupportsResourceManagement: true),
        Tags = ["dag", "critical-path", "optimization"]
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

        var criticalPath = ComputeCriticalPath(workflow);
        var completed = new HashSet<string>();
        var failed = new HashSet<string>();

        while (completed.Count + failed.Count < workflow.Tasks.Count)
        {
            var ready = GetReadyTasks(workflow, completed, new HashSet<string>())
                .OrderByDescending(t => criticalPath.Contains(t.TaskId) ? 1 : 0)
                .ThenByDescending(t => t.EstimatedDuration?.TotalMilliseconds ?? 0)
                .Take(workflow.MaxParallelism)
                .ToList();

            if (ready.Count == 0) break;

            var tasks = ready.Select(async task =>
            {
                var result = await ExecuteTaskAsync(task, context, cancellationToken);
                context.TaskResults[task.TaskId] = result;
                return (task.TaskId, result);
            });

            foreach (var (taskId, result) in await Task.WhenAll(tasks))
            {
                if (result.Success) completed.Add(taskId);
                else failed.Add(taskId);
            }
        }

        return new WorkflowResult
        {
            WorkflowInstanceId = instanceId,
            Success = failed.Count == 0,
            Status = failed.Count == 0 ? WorkflowExecutionStatus.Completed : WorkflowExecutionStatus.Failed,
            Duration = DateTime.UtcNow - startTime,
            TaskResults = context.TaskResults.ToDictionary(kv => kv.Key, kv => kv.Value)
        };
    }

    private HashSet<string> ComputeCriticalPath(WorkflowDefinition workflow)
    {
        var pathLengths = new Dictionary<string, double>();

        foreach (var task in workflow.GetTopologicalOrder())
        {
            var maxDepLength = task.Dependencies.Count > 0
                ? task.Dependencies.Max(d => pathLengths.GetValueOrDefault(d, 0))
                : 0;
            pathLengths[task.TaskId] = maxDepLength + (task.EstimatedDuration?.TotalMilliseconds ?? 100);
        }

        var maxLength = pathLengths.Values.Max();
        return pathLengths.Where(kv => kv.Value >= maxLength * 0.9).Select(kv => kv.Key).ToHashSet();
    }
}

/// <summary>
/// Streaming DAG execution for continuous data processing.
/// </summary>
public sealed class StreamingDagStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "StreamingDag",
        Description = "Streaming DAG execution for continuous data processing pipelines",
        Category = WorkflowCategory.DagExecution,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: true,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: false,
            SupportsCheckpointing: true,
            SupportsDistributed: true,
            SupportsPriority: true,
            SupportsResourceManagement: true),
        Tags = ["dag", "streaming", "continuous"]
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

        var topological = workflow.GetTopologicalOrder().ToList();
        var completed = new HashSet<string>();
        var streamBuffer = new ConcurrentQueue<(string TaskId, object? Data)>();

        foreach (var task in topological)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var result = await ExecuteTaskAsync(task, context, cancellationToken);
            context.TaskResults[task.TaskId] = result;
            completed.Add(task.TaskId);

            if (result.Success && result.Output != null)
                streamBuffer.Enqueue((task.TaskId, result.Output));
        }

        return new WorkflowResult
        {
            WorkflowInstanceId = instanceId,
            Success = completed.Count == workflow.Tasks.Count,
            Status = WorkflowExecutionStatus.Completed,
            Duration = DateTime.UtcNow - startTime,
            TaskResults = context.TaskResults.ToDictionary(kv => kv.Key, kv => kv.Value)
        };
    }
}
