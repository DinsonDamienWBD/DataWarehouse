using System.Collections.Concurrent;
using System.Threading.Channels;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateWorkflow.Strategies.ParallelExecution;

/// <summary>
/// Fork-join parallel execution strategy.
/// </summary>
public sealed class ForkJoinStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "ForkJoin",
        Description = "Fork-join parallel execution with work stealing for load balancing",
        Category = WorkflowCategory.ParallelExecution,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: true,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: false,
            SupportsCheckpointing: false,
            SupportsDistributed: false,
            SupportsPriority: false,
            SupportsResourceManagement: true,
            MaxParallelTasks: 1000),
        Tags = ["parallel", "fork-join", "work-stealing"]
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

        foreach (var layer in layers)
        {
            var tasks = layer.Select(async task =>
            {
                var result = await ExecuteTaskAsync(task, context, cancellationToken);
                context.TaskResults[task.TaskId] = result;
                return result;
            });

            await Task.WhenAll(tasks);
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
            foreach (var task in layer) assigned.Add(task.TaskId);
        }

        return layers;
    }
}

/// <summary>
/// Worker pool parallel execution strategy.
/// </summary>
public sealed class WorkerPoolStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "WorkerPool",
        Description = "Fixed worker pool parallel execution with bounded concurrency",
        Category = WorkflowCategory.ParallelExecution,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: false,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: false,
            SupportsCheckpointing: false,
            SupportsDistributed: true,
            SupportsPriority: true,
            SupportsResourceManagement: true,
            MaxParallelTasks: 100),
        Tags = ["parallel", "worker-pool", "bounded"]
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

        var channel = Channel.CreateUnbounded<WorkflowTask>();
        var completed = new BoundedDictionary<string, bool>(1000);
        var running = new BoundedDictionary<string, bool>(1000);

        foreach (var task in workflow.Tasks.Where(t => t.Dependencies.Count == 0))
            await channel.Writer.WriteAsync(task, cancellationToken);

        var workers = Enumerable.Range(0, workflow.MaxParallelism)
            .Select(_ => Task.Run(async () =>
            {
                await foreach (var task in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    if (completed.ContainsKey(task.TaskId)) continue;
                    running[task.TaskId] = true;

                    var result = await ExecuteTaskAsync(task, context, cancellationToken);
                    context.TaskResults[task.TaskId] = result;
                    completed[task.TaskId] = result.Success;
                    running.TryRemove(task.TaskId, out var _removed);

                    foreach (var next in workflow.Tasks.Where(t =>
                        t.Dependencies.Contains(task.TaskId) &&
                        t.Dependencies.All(d => completed.ContainsKey(d))))
                    {
                        await channel.Writer.WriteAsync(next, cancellationToken);
                    }

                    if (completed.Count >= workflow.Tasks.Count)
                        channel.Writer.TryComplete();
                }
            }, cancellationToken))
            .ToArray();

        await Task.Delay(100, cancellationToken);
        while (completed.Count < workflow.Tasks.Count && !cancellationToken.IsCancellationRequested)
            await Task.Delay(10, cancellationToken);

        channel.Writer.TryComplete();
        await Task.WhenAll(workers);

        return new WorkflowResult
        {
            WorkflowInstanceId = instanceId,
            Success = completed.Values.All(v => v),
            Status = WorkflowExecutionStatus.Completed,
            Duration = DateTime.UtcNow - startTime,
            TaskResults = context.TaskResults.ToDictionary(kv => kv.Key, kv => kv.Value)
        };
    }
}

/// <summary>
/// Pipeline parallel execution strategy.
/// </summary>
public sealed class PipelineParallelStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "PipelineParallel",
        Description = "Pipeline parallel execution with stage-based processing",
        Category = WorkflowCategory.ParallelExecution,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: false,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: false,
            SupportsCheckpointing: true,
            SupportsDistributed: true,
            SupportsPriority: false,
            SupportsResourceManagement: true),
        Tags = ["parallel", "pipeline", "stages"]
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

        var stages = workflow.Tasks
            .GroupBy(t => t.Metadata.GetValueOrDefault("stage", 0))
            .OrderBy(g => Convert.ToInt32(g.Key))
            .ToList();

        foreach (var stage in stages)
        {
            var stageTasks = stage.Select(async task =>
            {
                var result = await ExecuteTaskAsync(task, context, cancellationToken);
                context.TaskResults[task.TaskId] = result;
                return result;
            });

            await Task.WhenAll(stageTasks);
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
}

/// <summary>
/// Dataflow parallel execution strategy.
/// </summary>
public sealed class DataflowParallelStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "DataflowParallel",
        Description = "Dataflow parallel execution with message passing between tasks",
        Category = WorkflowCategory.ParallelExecution,
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
        Tags = ["parallel", "dataflow", "message-passing"]
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

        var channels = workflow.Tasks.ToDictionary(
            t => t.TaskId,
            t => Channel.CreateBounded<object?>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait }));

        var taskDict = workflow.Tasks.ToDictionary(t => t.TaskId);

        // Build downstream adjacency list once so each executor doesn't O(n) scan workflow.Tasks.
        var downstreamOf = workflow.Tasks.ToDictionary(
            t => t.TaskId,
            t => workflow.Tasks.Where(other => other.Dependencies.Contains(t.TaskId)).ToList());

        var executors = workflow.Tasks.Select(async task =>
        {
            foreach (var dep in task.Dependencies)
            {
                var input = await channels[dep].Reader.ReadAsync(cancellationToken);
                context.State[$"{task.TaskId}_input_{dep}"] = input!;
            }

            var result = await ExecuteTaskAsync(task, context, cancellationToken);
            context.TaskResults[task.TaskId] = result;

            // Write output to all downstream task channels, then complete this channel.
            // TryComplete() is called AFTER writes so readers can always drain the written value.
            foreach (var downstream in downstreamOf[task.TaskId])
                await channels[task.TaskId].Writer.WriteAsync(result.Output, cancellationToken);

            channels[task.TaskId].Writer.TryComplete();
            return result;
        }).ToArray();

        // Do NOT pre-complete root-task channels here — the executor above already completes them
        // after writing any output. Pre-completing would race with WriteAsync and cause ChannelClosedException.

        await Task.WhenAll(executors);

        return new WorkflowResult
        {
            WorkflowInstanceId = instanceId,
            Success = context.TaskResults.Values.All(r => r.Success),
            Status = WorkflowExecutionStatus.Completed,
            Duration = DateTime.UtcNow - startTime,
            TaskResults = context.TaskResults.ToDictionary(kv => kv.Key, kv => kv.Value)
        };
    }
}

/// <summary>
/// Adaptive parallel execution strategy that adjusts parallelism dynamically.
/// </summary>
public sealed class AdaptiveParallelStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "AdaptiveParallel",
        Description = "Adaptive parallel execution that dynamically adjusts parallelism based on system load",
        Category = WorkflowCategory.ParallelExecution,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: true,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: false,
            SupportsCheckpointing: false,
            SupportsDistributed: true,
            SupportsPriority: true,
            SupportsResourceManagement: true,
            MaxParallelTasks: 500),
        Tags = ["parallel", "adaptive", "auto-scaling"]
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
        var currentParallelism = Math.Min(4, workflow.MaxParallelism);
        var executionTimes = new ConcurrentBag<double>();

        while (completed.Count < workflow.Tasks.Count)
        {
            var ready = workflow.Tasks
                .Where(t => !completed.ContainsKey(t.TaskId))
                .Where(t => t.Dependencies.All(d => completed.ContainsKey(d)))
                .Take(currentParallelism)
                .ToList();

            if (ready.Count == 0) break;

            var batchStart = DateTime.UtcNow;
            var tasks = ready.Select(async task =>
            {
                var taskStart = DateTime.UtcNow;
                var result = await ExecuteTaskAsync(task, context, cancellationToken);
                executionTimes.Add((DateTime.UtcNow - taskStart).TotalMilliseconds);
                context.TaskResults[task.TaskId] = result;
                completed[task.TaskId] = result;
                return result;
            });

            await Task.WhenAll(tasks);

            var avgTime = executionTimes.Count > 0 ? executionTimes.Average() : 100;
            if (avgTime < 50)
                currentParallelism = Math.Min(currentParallelism + 2, workflow.MaxParallelism);
            else if (avgTime > 200)
                currentParallelism = Math.Max(currentParallelism - 1, 1);
        }

        return new WorkflowResult
        {
            WorkflowInstanceId = instanceId,
            Success = completed.Values.All(r => r.Success),
            Status = WorkflowExecutionStatus.Completed,
            Duration = DateTime.UtcNow - startTime,
            TaskResults = completed.ToDictionary(kv => kv.Key, kv => kv.Value)
        };
    }
}

/// <summary>
/// Speculative parallel execution running multiple paths simultaneously.
/// </summary>
public sealed class SpeculativeParallelStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "SpeculativeParallel",
        Description = "Speculative parallel execution running multiple conditional paths simultaneously",
        Category = WorkflowCategory.ParallelExecution,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: true,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: true,
            SupportsCheckpointing: true,
            SupportsDistributed: false,
            SupportsPriority: true,
            SupportsResourceManagement: true),
        Tags = ["parallel", "speculative", "conditional"]
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
        var speculativeTasks = new BoundedDictionary<string, CancellationTokenSource>(1000);

        foreach (var task in workflow.GetTopologicalOrder())
        {
            if (task.Dependencies.All(d => completed.ContainsKey(d)))
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                speculativeTasks[task.TaskId] = cts;

                var result = await ExecuteTaskAsync(task, context, cts.Token);
                context.TaskResults[task.TaskId] = result;
                completed[task.TaskId] = result;

                foreach (var spec in speculativeTasks.Where(kv =>
                    kv.Key != task.TaskId && !completed.ContainsKey(kv.Key)))
                {
                    var specTask = workflow.Tasks.Find(t => t.TaskId == spec.Key);
                    if (specTask?.Condition != null && !specTask.Condition(context))
                        spec.Value.Cancel();
                }
            }
        }

        return new WorkflowResult
        {
            WorkflowInstanceId = instanceId,
            Success = completed.Values.All(r => r.Success),
            Status = WorkflowExecutionStatus.Completed,
            Duration = DateTime.UtcNow - startTime,
            TaskResults = completed.ToDictionary(kv => kv.Key, kv => kv.Value)
        };
    }
}
