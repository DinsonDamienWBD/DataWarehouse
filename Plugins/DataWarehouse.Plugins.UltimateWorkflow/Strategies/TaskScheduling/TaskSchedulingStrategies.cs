using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateWorkflow.Strategies.TaskScheduling;

/// <summary>
/// FIFO task scheduling strategy.
/// </summary>
public sealed class FifoSchedulingStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "FifoScheduling",
        Description = "First-In-First-Out task scheduling for predictable execution order",
        Category = WorkflowCategory.TaskScheduling,
        Capabilities = new(
            SupportsParallelExecution: false,
            SupportsDynamicDag: false,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: false,
            SupportsCheckpointing: false,
            SupportsDistributed: false,
            SupportsPriority: false,
            SupportsResourceManagement: false),
        Tags = ["scheduling", "fifo", "sequential"]
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

        var queue = new Queue<WorkflowTask>(workflow.GetTopologicalOrder());
        var failed = false;

        while (queue.Count > 0 && !failed)
        {
            var task = queue.Dequeue();
            var result = await ExecuteTaskAsync(task, context, cancellationToken);
            context.TaskResults[task.TaskId] = result;
            failed = !result.Success && workflow.ErrorStrategy == ErrorHandlingStrategy.FailFast;
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
/// Priority-based task scheduling strategy.
/// </summary>
public sealed class PrioritySchedulingStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "PriorityScheduling",
        Description = "Priority-based task scheduling executing high-priority tasks first",
        Category = WorkflowCategory.TaskScheduling,
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
        Tags = ["scheduling", "priority", "parallel"]
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

        var priorityQueue = new PriorityQueue<WorkflowTask, int>();
        var completed = new HashSet<string>();
        var pending = new HashSet<string>(workflow.Tasks.Select(t => t.TaskId));

        foreach (var task in workflow.Tasks.Where(t => t.Dependencies.Count == 0))
            priorityQueue.Enqueue(task, -(int)task.Priority);

        while (priorityQueue.Count > 0)
        {
            var batch = new List<WorkflowTask>();
            while (priorityQueue.Count > 0 && batch.Count < workflow.MaxParallelism)
            {
                if (priorityQueue.TryDequeue(out var task, out _))
                    batch.Add(task);
            }

            var tasks = batch.Select(async task =>
            {
                var result = await ExecuteTaskAsync(task, context, cancellationToken);
                context.TaskResults[task.TaskId] = result;
                return (task.TaskId, result);
            });

            foreach (var (taskId, result) in await Task.WhenAll(tasks))
            {
                completed.Add(taskId);
                pending.Remove(taskId);

                foreach (var next in workflow.Tasks.Where(t => t.Dependencies.Contains(taskId)))
                {
                    if (next.Dependencies.All(d => completed.Contains(d)))
                        priorityQueue.Enqueue(next, -(int)next.Priority);
                }
            }
        }

        return new WorkflowResult
        {
            WorkflowInstanceId = instanceId,
            Success = true,
            Status = WorkflowExecutionStatus.Completed,
            Duration = DateTime.UtcNow - startTime,
            TaskResults = context.TaskResults.ToDictionary(kv => kv.Key, kv => kv.Value)
        };
    }
}

/// <summary>
/// Round-robin task scheduling for fair resource distribution.
/// </summary>
public sealed class RoundRobinSchedulingStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "RoundRobinScheduling",
        Description = "Round-robin task scheduling for fair resource distribution across task groups",
        Category = WorkflowCategory.TaskScheduling,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: false,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: false,
            SupportsCheckpointing: false,
            SupportsDistributed: true,
            SupportsPriority: false,
            SupportsResourceManagement: true),
        Tags = ["scheduling", "round-robin", "fair"]
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

        var groups = workflow.Tasks.GroupBy(t => t.Metadata.GetValueOrDefault("group", "default")?.ToString() ?? "default").ToList();
        var completed = new HashSet<string>();
        var groupIndex = 0;

        while (completed.Count < workflow.Tasks.Count)
        {
            var group = groups[groupIndex % groups.Count];
            var readyTask = group.FirstOrDefault(t =>
                !completed.Contains(t.TaskId) &&
                t.Dependencies.All(d => completed.Contains(d)));

            if (readyTask != null)
            {
                var result = await ExecuteTaskAsync(readyTask, context, cancellationToken);
                context.TaskResults[readyTask.TaskId] = result;
                completed.Add(readyTask.TaskId);
            }

            groupIndex++;
            if (groupIndex > groups.Count * workflow.Tasks.Count) break;
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

/// <summary>
/// Shortest job first scheduling strategy.
/// </summary>
public sealed class ShortestJobFirstStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "ShortestJobFirst",
        Description = "Shortest job first scheduling minimizing average completion time",
        Category = WorkflowCategory.TaskScheduling,
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
        Tags = ["scheduling", "sjf", "optimization"]
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

        while (completed.Count < workflow.Tasks.Count)
        {
            var ready = GetReadyTasks(workflow, completed, new HashSet<string>())
                .OrderBy(t => t.EstimatedDuration ?? TimeSpan.FromHours(1))
                .Take(workflow.MaxParallelism)
                .ToList();

            if (ready.Count == 0) break;

            var tasks = ready.Select(async task =>
            {
                var result = await ExecuteTaskAsync(task, context, cancellationToken);
                context.TaskResults[task.TaskId] = result;
                return task.TaskId;
            });

            foreach (var taskId in await Task.WhenAll(tasks))
                completed.Add(taskId);
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

/// <summary>
/// Deadline-aware task scheduling strategy.
/// </summary>
public sealed class DeadlineSchedulingStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "DeadlineScheduling",
        Description = "Deadline-aware task scheduling prioritizing tasks with approaching deadlines",
        Category = WorkflowCategory.TaskScheduling,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: false,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: false,
            SupportsCheckpointing: false,
            SupportsDistributed: true,
            SupportsPriority: true,
            SupportsResourceManagement: false),
        Tags = ["scheduling", "deadline", "time-critical"]
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

        while (completed.Count < workflow.Tasks.Count)
        {
            var ready = GetReadyTasks(workflow, completed, new HashSet<string>())
                .OrderBy(t =>
                {
                    if (t.Metadata.TryGetValue("deadline", out var dl) && dl is DateTimeOffset deadline)
                        return deadline;
                    return DateTimeOffset.MaxValue;
                })
                .Take(workflow.MaxParallelism)
                .ToList();

            if (ready.Count == 0) break;

            var tasks = ready.Select(async task =>
            {
                var result = await ExecuteTaskAsync(task, context, cancellationToken);
                context.TaskResults[task.TaskId] = result;
                return task.TaskId;
            });

            foreach (var taskId in await Task.WhenAll(tasks))
                completed.Add(taskId);
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

/// <summary>
/// Multi-level feedback queue scheduling strategy.
/// </summary>
public sealed class MultilevelFeedbackQueueStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "MultilevelFeedbackQueue",
        Description = "Multi-level feedback queue scheduling with dynamic priority adjustment based on execution history",
        Category = WorkflowCategory.TaskScheduling,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: true,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: false,
            SupportsCheckpointing: false,
            SupportsDistributed: false,
            SupportsPriority: true,
            SupportsResourceManagement: true),
        Tags = ["scheduling", "mlfq", "adaptive"]
    };

    private readonly ConcurrentDictionary<string, int> _taskQueues = new();

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
        var queues = new List<Queue<WorkflowTask>> { new(), new(), new() };

        foreach (var task in workflow.Tasks.Where(t => t.Dependencies.Count == 0))
            queues[0].Enqueue(task);

        while (completed.Count < workflow.Tasks.Count)
        {
            WorkflowTask? taskToRun = null;
            var queueLevel = 0;

            for (var i = 0; i < queues.Count; i++)
            {
                if (queues[i].Count > 0)
                {
                    taskToRun = queues[i].Dequeue();
                    queueLevel = i;
                    break;
                }
            }

            if (taskToRun == null) break;

            var execStart = DateTime.UtcNow;
            var result = await ExecuteTaskAsync(taskToRun, context, cancellationToken);
            context.TaskResults[taskToRun.TaskId] = result;
            completed.Add(taskToRun.TaskId);

            var execTime = DateTime.UtcNow - execStart;

            foreach (var next in workflow.Tasks.Where(t =>
                t.Dependencies.Contains(taskToRun.TaskId) &&
                t.Dependencies.All(d => completed.Contains(d))))
            {
                var targetQueue = execTime.TotalMilliseconds > 100 ? Math.Min(queueLevel + 1, 2) : queueLevel;
                queues[targetQueue].Enqueue(next);
            }
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
