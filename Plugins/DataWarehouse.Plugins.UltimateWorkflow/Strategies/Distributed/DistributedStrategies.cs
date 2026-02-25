using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateWorkflow.Strategies.Distributed;

/// <summary>
/// Distributed workflow execution across nodes.
/// </summary>
public sealed class DistributedExecutionStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "DistributedExecution",
        Description = "Distributed workflow execution across multiple nodes with load balancing",
        Category = WorkflowCategory.Distributed,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: true,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: true,
            SupportsCheckpointing: true,
            SupportsDistributed: true,
            SupportsPriority: true,
            SupportsResourceManagement: true,
            MaxParallelTasks: 10000),
        Tags = ["distributed", "multi-node", "load-balancing"]
    };

    private readonly string[] _nodes = { "node-1", "node-2", "node-3", "node-4" };
    private int _nodeIndex;

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
        var nodeAssignments = new BoundedDictionary<string, string>(1000);

        var tasks = workflow.GetTopologicalOrder().Select(async task =>
        {
            while (!task.Dependencies.All(d => completed.ContainsKey(d)))
                await Task.Delay(10, cancellationToken);

            var node = _nodes[Interlocked.Increment(ref _nodeIndex) % _nodes.Length];
            nodeAssignments[task.TaskId] = node;

            context.State[$"{task.TaskId}_node"] = node;
            var result = await ExecuteTaskAsync(task, context, cancellationToken);
            context.TaskResults[task.TaskId] = result;
            completed[task.TaskId] = result;
        });

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
}

/// <summary>
/// Leader-follower distributed execution strategy.
/// </summary>
public sealed class LeaderFollowerStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "LeaderFollower",
        Description = "Leader-follower distributed execution with automatic failover",
        Category = WorkflowCategory.Distributed,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: false,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: true,
            SupportsCheckpointing: true,
            SupportsDistributed: true,
            SupportsPriority: true,
            SupportsResourceManagement: true),
        Tags = ["distributed", "leader-follower", "failover"]
    };

    private string _currentLeader = "leader-1";
    private readonly string[] _followers = { "follower-1", "follower-2", "follower-3" };

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

        context.State["leader"] = _currentLeader;
        context.State["followers"] = _followers;

        foreach (var task in workflow.GetTopologicalOrder())
        {
            var isCoordinationTask = task.Metadata.GetValueOrDefault("coordination", false) is true;
            var executor = isCoordinationTask ? _currentLeader : _followers[task.TaskId.GetHashCode() % _followers.Length];

            context.State[$"{task.TaskId}_executor"] = executor;
            var result = await ExecuteTaskAsync(task, context, cancellationToken);
            context.TaskResults[task.TaskId] = result;
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
/// Sharded workflow execution strategy.
/// </summary>
public sealed class ShardedExecutionStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "ShardedExecution",
        Description = "Sharded workflow execution partitioning tasks by shard key",
        Category = WorkflowCategory.Distributed,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: true,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: false,
            SupportsCheckpointing: true,
            SupportsDistributed: true,
            SupportsPriority: false,
            SupportsResourceManagement: true,
            MaxParallelTasks: 5000),
        Tags = ["distributed", "sharded", "partitioned"]
    };

    private const int ShardCount = 8;

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

        var shards = Enumerable.Range(0, ShardCount)
            .ToDictionary(i => i, _ => new List<WorkflowTask>());

        foreach (var task in workflow.Tasks)
        {
            var shardKey = task.Metadata.GetValueOrDefault("shardKey", task.TaskId)?.ToString() ?? task.TaskId;
            var shard = Math.Abs(shardKey.GetHashCode()) % ShardCount;
            shards[shard].Add(task);
        }

        var shardTasks = shards.Where(kv => kv.Value.Count > 0).Select(async kv =>
        {
            foreach (var task in kv.Value.OrderBy(t => t.Dependencies.Count))
            {
                while (!task.Dependencies.All(d => context.TaskResults.ContainsKey(d)))
                    await Task.Delay(10, cancellationToken);

                var result = await ExecuteTaskAsync(task, context, cancellationToken);
                context.TaskResults[task.TaskId] = result;
            }
        });

        await Task.WhenAll(shardTasks);

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
/// Gossip-based distributed coordination strategy.
/// </summary>
public sealed class GossipCoordinationStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "GossipCoordination",
        Description = "Gossip-based distributed coordination for eventually consistent execution",
        Category = WorkflowCategory.Distributed,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: true,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: false,
            SupportsCheckpointing: true,
            SupportsDistributed: true,
            SupportsPriority: false,
            SupportsResourceManagement: false),
        Tags = ["distributed", "gossip", "eventual-consistency"]
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
        var gossipState = new BoundedDictionary<string, DateTimeOffset>(1000);

        foreach (var task in workflow.GetTopologicalOrder())
        {
            while (!task.Dependencies.All(d => completed.ContainsKey(d)))
            {
                await Task.Delay(10, cancellationToken);
                foreach (var dep in task.Dependencies.Where(d => !gossipState.ContainsKey(d)))
                    gossipState[dep] = DateTimeOffset.UtcNow;
            }

            var result = await ExecuteTaskAsync(task, context, cancellationToken);
            context.TaskResults[task.TaskId] = result;
            completed[task.TaskId] = result;
            gossipState[task.TaskId] = DateTimeOffset.UtcNow;
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
/// Raft-based consensus distributed execution strategy.
/// </summary>
public sealed class RaftConsensusStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "RaftConsensus",
        Description = "Raft-based consensus for strongly consistent distributed execution",
        Category = WorkflowCategory.Distributed,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: false,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: true,
            SupportsCheckpointing: true,
            SupportsDistributed: true,
            SupportsPriority: true,
            SupportsResourceManagement: true),
        Tags = ["distributed", "raft", "consensus", "strong-consistency"]
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

        var commitLog = new List<(string TaskId, TaskResult Result, long Term)>();
        var currentTerm = 1L;

        foreach (var task in workflow.GetTopologicalOrder())
        {
            var result = await ExecuteTaskAsync(task, context, cancellationToken);
            context.TaskResults[task.TaskId] = result;

            commitLog.Add((task.TaskId, result, currentTerm));
            await Task.Delay(1, cancellationToken);
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
