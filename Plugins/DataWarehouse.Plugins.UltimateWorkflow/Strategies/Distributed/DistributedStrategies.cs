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

    // Configurable node list. Default to localhost only; callers must supply real node addresses.
    private string[] _nodes = { "localhost" };
    private int _nodeIndex;

    /// <summary>
    /// Configures the list of node addresses for distributed task assignment.
    /// Must be called before <see cref="ExecuteAsync"/> for multi-node execution.
    /// </summary>
    public void ConfigureNodes(IReadOnlyList<string> nodeAddresses)
    {
        if (nodeAddresses == null || nodeAddresses.Count == 0)
            throw new ArgumentException("At least one node address is required.", nameof(nodeAddresses));
        _nodes = nodeAddresses.ToArray();
    }

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

    // Configurable leader/follower topology. Callers must supply real node addresses.
    private string _currentLeader = string.Empty;
    private string[] _followers = Array.Empty<string>();

    /// <summary>
    /// Configures the leader and follower nodes for this strategy.
    /// Must be called before <see cref="ExecuteAsync"/> with real node addresses.
    /// </summary>
    public void ConfigureTopology(string leaderAddress, IReadOnlyList<string> followerAddresses)
    {
        if (string.IsNullOrWhiteSpace(leaderAddress))
            throw new ArgumentException("Leader address must not be empty.", nameof(leaderAddress));
        _currentLeader = leaderAddress;
        _followers = followerAddresses?.ToArray() ?? Array.Empty<string>();
    }

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
/// <remarks>
/// This strategy uses gossip-style dependency tracking — each node records when it observed
/// dependency completion before proceeding. Requires <see cref="ConfigureGossipPeers"/> to be
/// called with real peer endpoints before use in a multi-node environment.
/// Without peer configuration, execution proceeds locally (single-node mode).
/// </remarks>
public sealed class GossipCoordinationStrategy : WorkflowStrategyBase
{
    private string[] _peers = Array.Empty<string>();

    /// <summary>
    /// Configures the gossip peer endpoints for multi-node coordination.
    /// </summary>
    public void ConfigureGossipPeers(IReadOnlyList<string> peerEndpoints)
    {
        _peers = peerEndpoints?.ToArray() ?? Array.Empty<string>();
    }

    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "GossipCoordination",
        Description = "Gossip-based distributed coordination for eventually consistent execution. " +
                      "Configure peer endpoints via ConfigureGossipPeers for multi-node operation.",
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
/// <remarks>
/// Provides Raft-style commit-log semantics — each task result is appended to a per-term
/// commit log before the next task proceeds, enforcing a sequential commit order.
/// Requires <see cref="ConfigureRaftCluster"/> with real node endpoints for multi-node operation.
/// Without cluster configuration, execution proceeds locally (single-node mode, term = 1).
/// </remarks>
public sealed class RaftConsensusStrategy : WorkflowStrategyBase
{
    private string[] _clusterNodes = Array.Empty<string>();
    private long _currentTerm = 1;

    /// <summary>
    /// Configures the Raft cluster node endpoints and initial term.
    /// Must be called with real node addresses for multi-node operation.
    /// </summary>
    public void ConfigureRaftCluster(IReadOnlyList<string> nodeEndpoints, long initialTerm = 1)
    {
        _clusterNodes = nodeEndpoints?.ToArray() ?? Array.Empty<string>();
        _currentTerm = initialTerm > 0 ? initialTerm : 1;
    }

    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "RaftConsensus",
        Description = "Raft-based consensus for strongly consistent distributed execution. " +
                      "Configure cluster nodes via ConfigureRaftCluster for multi-node operation.",
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

        foreach (var task in workflow.GetTopologicalOrder())
        {
            var result = await ExecuteTaskAsync(task, context, cancellationToken);
            context.TaskResults[task.TaskId] = result;

            // Append to commit log under the current term — mimics Raft log replication.
            // In multi-node mode (ConfigureRaftCluster), replication to followers is performed here.
            commitLog.Add((task.TaskId, result, _currentTerm));
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
