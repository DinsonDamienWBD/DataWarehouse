using System.Collections.Concurrent;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.UltimateWorkflow.Strategies.DagExecution;
using DataWarehouse.Plugins.UltimateWorkflow.Strategies.TaskScheduling;
using DataWarehouse.Plugins.UltimateWorkflow.Strategies.ParallelExecution;
using DataWarehouse.Plugins.UltimateWorkflow.Strategies.StateManagement;
using DataWarehouse.Plugins.UltimateWorkflow.Strategies.ErrorHandling;
using DataWarehouse.Plugins.UltimateWorkflow.Strategies.Distributed;
using DataWarehouse.Plugins.UltimateWorkflow.Strategies.AIEnhanced;

namespace DataWarehouse.Plugins.UltimateWorkflow;

/// <summary>
/// Ultimate Workflow Plugin - Complete DAG-based workflow orchestration.
///
/// Implements T135 Workflow Orchestration:
/// - DAG builder and validation
/// - Task scheduling (FIFO, priority, round-robin, deadline, MLFQ)
/// - Parallel execution (fork-join, worker pool, pipeline, dataflow, adaptive)
/// - Dependency management (topological, dynamic, layered, critical path)
/// - State management (checkpoint, event-sourced, saga, transactional, versioned)
/// - Error handling (retry, circuit breaker, fallback, bulkhead, dead letter)
/// - Distributed execution (multi-node, leader-follower, sharded, gossip, raft)
/// - AI-enhanced (optimization, self-learning, anomaly detection, predictive scaling)
///
/// Features:
/// - 45+ production-ready workflow strategies
/// - Intelligence-aware for AI-enhanced optimization
/// - Distributed execution support
/// - Comprehensive error handling
/// - State persistence and recovery
/// </summary>
public sealed class UltimateWorkflowPlugin : OrchestrationPluginBase, IDisposable
{
    private readonly WorkflowStrategyRegistry _registry = new();
    private readonly ConcurrentDictionary<string, WorkflowDefinition> _workflows = new();
    private readonly ConcurrentDictionary<string, WorkflowResult> _executions = new();
    private WorkflowStrategyBase? _activeStrategy;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    private long _totalExecutions;
    private long _successfulExecutions;
    private long _failedExecutions;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.workflow.ultimate";

    /// <inheritdoc/>
    public override string Name => "Ultimate Workflow";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string OrchestrationMode => "Workflow";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.OrchestrationProvider;

    /// <summary>Semantic description for AI discovery.</summary>
    public string SemanticDescription =>
        "Ultimate workflow plugin providing 45+ DAG-based workflow orchestration strategies including " +
        "topological/dynamic/layered/critical-path DAG execution, FIFO/priority/deadline scheduling, " +
        "fork-join/worker-pool/pipeline/dataflow parallel execution, checkpoint/event-sourced/saga state management, " +
        "retry/circuit-breaker/bulkhead error handling, distributed execution with leader-follower/sharding/raft, " +
        "and AI-enhanced optimization with self-learning, anomaly detection, and predictive scaling.";

    /// <summary>Semantic tags for AI discovery.</summary>
    public string[] SemanticTags => new[]
    {
        "workflow", "dag", "orchestration", "scheduling", "parallel", "distributed",
        "state-management", "error-handling", "retry", "circuit-breaker", "saga",
        "ai-enhanced", "self-learning", "predictive"
    };

    /// <summary>Gets the workflow strategy registry.</summary>
    public WorkflowStrategyRegistry Registry => _registry;

    /// <summary>Initializes a new instance of the Ultimate Workflow plugin.</summary>
    public UltimateWorkflowPlugin()
    {
        DiscoverAndRegisterStrategies();
    }

    /// <inheritdoc/>
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        _activeStrategy ??= _registry.Get("TopologicalDag") ?? _registry.GetAll().FirstOrDefault();

        return await Task.FromResult(new HandshakeResponse
        {
            PluginId = Id,
            Name = Name,
            Version = ParseSemanticVersion(Version),
            Category = Category,
            Success = true,
            ReadyState = PluginReadyState.Ready,
            Capabilities = GetCapabilities(),
            Metadata = GetMetadata()
        });
    }

    /// <inheritdoc/>
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        if (MessageBus != null)
        {
            MessageBus.Subscribe("workflow.execute", HandleExecuteWorkflowAsync);
            MessageBus.Subscribe("workflow.define", HandleDefineWorkflowAsync);
            MessageBus.Subscribe("workflow.strategy.select", HandleSelectStrategyAsync);
            MessageBus.Subscribe("workflow.optimize", HandleOptimizeWorkflowAsync);
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task OnStartWithoutIntelligenceAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        if (MessageBus != null)
        {
            MessageBus.Subscribe("workflow.execute", HandleExecuteWorkflowAsync);
            MessageBus.Subscribe("workflow.define", HandleDefineWorkflowAsync);
            MessageBus.Subscribe("workflow.strategy.select", HandleSelectStrategyAsync);
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task OnStopCoreAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override async Task OnMessageAsync(PluginMessage message)
    {
        if (message.Payload == null)
            return;

        var response = message.Type switch
        {
            "workflow.strategy.list" => HandleListStrategies(),
            "workflow.strategy.select" => HandleSelectStrategy(message.Payload),
            "workflow.define" => HandleDefineWorkflow(message.Payload),
            "workflow.execute" => await HandleExecuteWorkflowAsync(message.Payload),
            "workflow.status" => HandleGetStatus(message.Payload),
            "workflow.list" => HandleListWorkflows(),
            "workflow.stats" => HandleGetStats(),
            _ => new Dictionary<string, object> { ["error"] = $"Unknown command: {message.Type}" }
        };

        if (response != null)
        {
            message.Payload["_response"] = response;
        }
    }

    #region Strategy Discovery

    private void DiscoverAndRegisterStrategies()
    {
        // DAG Execution strategies (5)
        _registry.Register(new TopologicalDagStrategy());
        _registry.Register(new DynamicDagStrategy());
        _registry.Register(new LayeredDagStrategy());
        _registry.Register(new CriticalPathDagStrategy());
        _registry.Register(new StreamingDagStrategy());

        // Task Scheduling strategies (6)
        _registry.Register(new FifoSchedulingStrategy());
        _registry.Register(new PrioritySchedulingStrategy());
        _registry.Register(new RoundRobinSchedulingStrategy());
        _registry.Register(new ShortestJobFirstStrategy());
        _registry.Register(new DeadlineSchedulingStrategy());
        _registry.Register(new MultilevelFeedbackQueueStrategy());

        // Parallel Execution strategies (6)
        _registry.Register(new ForkJoinStrategy());
        _registry.Register(new WorkerPoolStrategy());
        _registry.Register(new PipelineParallelStrategy());
        _registry.Register(new DataflowParallelStrategy());
        _registry.Register(new AdaptiveParallelStrategy());
        _registry.Register(new SpeculativeParallelStrategy());

        // State Management strategies (6)
        _registry.Register(new CheckpointStateStrategy());
        _registry.Register(new EventSourcedStateStrategy());
        _registry.Register(new SagaStateStrategy());
        _registry.Register(new TransactionalStateStrategy());
        _registry.Register(new VersionedStateStrategy());
        _registry.Register(new DistributedStateStrategy());

        // Error Handling strategies (6)
        _registry.Register(new ExponentialBackoffRetryStrategy());
        _registry.Register(new CircuitBreakerStrategy());
        _registry.Register(new FallbackStrategy());
        _registry.Register(new BulkheadIsolationStrategy());
        _registry.Register(new DeadLetterQueueStrategy());
        _registry.Register(new TimeoutHandlingStrategy());

        // Distributed strategies (5)
        _registry.Register(new DistributedExecutionStrategy());
        _registry.Register(new LeaderFollowerStrategy());
        _registry.Register(new ShardedExecutionStrategy());
        _registry.Register(new GossipCoordinationStrategy());
        _registry.Register(new RaftConsensusStrategy());

        // AI-Enhanced strategies (5)
        _registry.Register(new AIOptimizedWorkflowStrategy());
        _registry.Register(new SelfLearningWorkflowStrategy());
        _registry.Register(new AnomalyDetectionWorkflowStrategy());
        _registry.Register(new PredictiveScalingStrategy());
        _registry.Register(new IntelligentRetryStrategy());
    }

    #endregion

    #region Public API

    /// <summary>Gets all registered strategy names.</summary>
    public IReadOnlyCollection<string> GetRegisteredStrategies() => _registry.RegisteredStrategies;

    /// <summary>Gets a strategy by name.</summary>
    public WorkflowStrategyBase? GetStrategy(string name) => _registry.Get(name);

    /// <summary>Sets the active strategy.</summary>
    public void SetActiveStrategy(string strategyName)
    {
        var strategy = _registry.Get(strategyName)
            ?? throw new ArgumentException($"Strategy '{strategyName}' not found");
        _activeStrategy = strategy;
    }

    /// <summary>Defines a workflow.</summary>
    public void DefineWorkflow(WorkflowDefinition workflow)
    {
        if (!workflow.ValidateDag())
            throw new ArgumentException("Workflow contains cyclic dependencies");
        _workflows[workflow.WorkflowId] = workflow;
    }

    /// <summary>Executes a workflow.</summary>
    public async Task<WorkflowResult> ExecuteWorkflowAsync(
        string workflowId,
        Dictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (!_workflows.TryGetValue(workflowId, out var workflow))
            throw new ArgumentException($"Workflow '{workflowId}' not found");

        if (_activeStrategy == null)
            throw new InvalidOperationException("No active strategy set");

        Interlocked.Increment(ref _totalExecutions);

        var result = await _activeStrategy.ExecuteAsync(workflow, parameters, cancellationToken);
        _executions[result.WorkflowInstanceId] = result;

        if (result.Success)
            Interlocked.Increment(ref _successfulExecutions);
        else
            Interlocked.Increment(ref _failedExecutions);

        return result;
    }

    /// <summary>Gets workflow execution result.</summary>
    public WorkflowResult? GetExecutionResult(string instanceId) =>
        _executions.TryGetValue(instanceId, out var result) ? result : null;

    #endregion

    #region Message Handlers

    private Dictionary<string, object> HandleListStrategies()
    {
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["count"] = _registry.Count,
            ["strategies"] = _registry.GetSummary().Select(s => new Dictionary<string, object>
            {
                ["name"] = s.Name,
                ["description"] = s.Description,
                ["category"] = s.Category.ToString(),
                ["supportsParallel"] = s.SupportsParallel
            }).ToList(),
            ["activeStrategy"] = _activeStrategy?.Characteristics.StrategyName ?? "none"
        };
    }

    private Dictionary<string, object> HandleSelectStrategy(Dictionary<string, object> payload)
    {
        try
        {
            var strategyName = payload.GetValueOrDefault("strategy")?.ToString();
            if (string.IsNullOrEmpty(strategyName))
                return new Dictionary<string, object> { ["success"] = false, ["error"] = "Strategy name required" };

            SetActiveStrategy(strategyName);
            return new Dictionary<string, object> { ["success"] = true, ["activeStrategy"] = strategyName };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object> { ["success"] = false, ["error"] = ex.Message };
        }
    }

    private Dictionary<string, object> HandleDefineWorkflow(Dictionary<string, object> payload)
    {
        try
        {
            var workflowId = payload.GetValueOrDefault("workflowId")?.ToString()
                ?? throw new ArgumentException("Workflow ID required");
            var name = payload.GetValueOrDefault("name")?.ToString() ?? workflowId;

            var workflow = new WorkflowDefinition
            {
                WorkflowId = workflowId,
                Name = name,
                Description = payload.GetValueOrDefault("description")?.ToString() ?? ""
            };

            DefineWorkflow(workflow);
            return new Dictionary<string, object> { ["success"] = true, ["workflowId"] = workflowId };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object> { ["success"] = false, ["error"] = ex.Message };
        }
    }

    private async Task<Dictionary<string, object>> HandleExecuteWorkflowAsync(Dictionary<string, object> payload)
    {
        try
        {
            var workflowId = payload.GetValueOrDefault("workflowId")?.ToString()
                ?? throw new ArgumentException("Workflow ID required");

            var parameters = payload.GetValueOrDefault("parameters") as Dictionary<string, object>;
            var result = await ExecuteWorkflowAsync(workflowId, parameters, _cts?.Token ?? default);

            return new Dictionary<string, object>
            {
                ["success"] = result.Success,
                ["instanceId"] = result.WorkflowInstanceId,
                ["status"] = result.Status.ToString(),
                ["durationMs"] = result.Duration.TotalMilliseconds,
                ["taskCount"] = result.TaskResults.Count,
                ["completedTasks"] = result.TaskResults.Count(kv => kv.Value.Success),
                ["failedTasks"] = result.TaskResults.Count(kv => !kv.Value.Success)
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object> { ["success"] = false, ["error"] = ex.Message };
        }
    }

    private Dictionary<string, object> HandleGetStatus(Dictionary<string, object> payload)
    {
        var instanceId = payload.GetValueOrDefault("instanceId")?.ToString();
        if (string.IsNullOrEmpty(instanceId))
            return new Dictionary<string, object> { ["success"] = false, ["error"] = "Instance ID required" };

        var result = GetExecutionResult(instanceId);
        if (result == null)
            return new Dictionary<string, object> { ["success"] = false, ["error"] = "Instance not found" };

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["instanceId"] = result.WorkflowInstanceId,
            ["status"] = result.Status.ToString(),
            ["workflowSuccess"] = result.Success,
            ["durationMs"] = result.Duration.TotalMilliseconds,
            ["taskResults"] = result.TaskResults.ToDictionary(
                kv => kv.Key,
                kv => new Dictionary<string, object>
                {
                    ["success"] = kv.Value.Success,
                    ["durationMs"] = kv.Value.Duration.TotalMilliseconds,
                    ["error"] = kv.Value.Error ?? ""
                })
        };
    }

    private Dictionary<string, object> HandleListWorkflows()
    {
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["count"] = _workflows.Count,
            ["workflows"] = _workflows.Values.Select(w => new Dictionary<string, object>
            {
                ["workflowId"] = w.WorkflowId,
                ["name"] = w.Name,
                ["taskCount"] = w.Tasks.Count,
                ["maxParallelism"] = w.MaxParallelism
            }).ToList()
        };
    }

    private Dictionary<string, object> HandleGetStats()
    {
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["totalExecutions"] = _totalExecutions,
            ["successfulExecutions"] = _successfulExecutions,
            ["failedExecutions"] = _failedExecutions,
            ["successRate"] = _totalExecutions > 0
                ? (double)_successfulExecutions / _totalExecutions * 100
                : 0,
            ["registeredStrategies"] = _registry.Count,
            ["definedWorkflows"] = _workflows.Count,
            ["activeStrategy"] = _activeStrategy?.Characteristics.StrategyName ?? "none"
        };
    }

    private async Task HandleExecuteWorkflowAsync(PluginMessage message)
    {
        var response = await HandleExecuteWorkflowAsync(message.Payload);
        if (MessageBus != null)
        {
            await MessageBus.PublishAsync($"{message.Type}.response", new PluginMessage
            {
                Type = $"{message.Type}.response",
                CorrelationId = message.CorrelationId,
                Source = Id,
                Payload = response
            });
        }
    }

    private async Task HandleDefineWorkflowAsync(PluginMessage message)
    {
        var response = HandleDefineWorkflow(message.Payload);
        if (MessageBus != null)
        {
            await MessageBus.PublishAsync($"{message.Type}.response", new PluginMessage
            {
                Type = $"{message.Type}.response",
                CorrelationId = message.CorrelationId,
                Source = Id,
                Payload = response
            });
        }
    }

    private async Task HandleSelectStrategyAsync(PluginMessage message)
    {
        var response = HandleSelectStrategy(message.Payload);
        if (MessageBus != null)
        {
            await MessageBus.PublishAsync($"{message.Type}.response", new PluginMessage
            {
                Type = $"{message.Type}.response",
                CorrelationId = message.CorrelationId,
                Source = Id,
                Payload = response
            });
        }
    }

    private async Task HandleOptimizeWorkflowAsync(PluginMessage message)
    {
        if (!IsIntelligenceAvailable)
        {
            if (MessageBus != null)
            {
                await MessageBus.PublishAsync($"{message.Type}.response", new PluginMessage
                {
                    Type = $"{message.Type}.response",
                    CorrelationId = message.CorrelationId,
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["success"] = false,
                        ["error"] = "Intelligence not available for optimization"
                    }
                });
            }
            return;
        }

        var workflowId = message.Payload.GetValueOrDefault("workflowId")?.ToString();
        if (_workflows.TryGetValue(workflowId ?? "", out var workflow))
        {
            var aiStrategy = _registry.Get("AIOptimized");
            if (aiStrategy != null)
            {
                _activeStrategy = aiStrategy;
            }
        }

        if (MessageBus != null)
        {
            await MessageBus.PublishAsync($"{message.Type}.response", new PluginMessage
            {
                Type = $"{message.Type}.response",
                CorrelationId = message.CorrelationId,
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["optimizedStrategy"] = _activeStrategy?.Characteristics.StrategyName ?? "none"
                }
            });
        }
    }

    #endregion

    #region Capability & Metadata

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        var capabilities = new List<PluginCapabilityDescriptor>
        {
            new() { Name = "workflow.define", Description = "Define a workflow" },
            new() { Name = "workflow.execute", Description = "Execute a workflow" },
            new() { Name = "workflow.status", Description = "Get workflow execution status" },
            new() { Name = "workflow.list", Description = "List defined workflows" },
            new() { Name = "workflow.strategy.list", Description = "List available strategies" },
            new() { Name = "workflow.strategy.select", Description = "Select active strategy" },
            new() { Name = "workflow.stats", Description = "Get execution statistics" }
        };

        foreach (var strategy in _registry.GetAll())
        {
            capabilities.Add(new PluginCapabilityDescriptor
            {
                Name = $"workflow.strategy.{strategy.StrategyId.ToLowerInvariant()}",
                Description = strategy.Characteristics.Description
            });
        }

        return capabilities;
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "UltimateWorkflow";
        metadata["StrategyCount"] = _registry.Count;
        metadata["Strategies"] = _registry.RegisteredStrategies.ToArray();
        metadata["ActiveStrategy"] = _activeStrategy?.Characteristics.StrategyName ?? "none";
        metadata["SemanticDescription"] = SemanticDescription;
        metadata["SemanticTags"] = SemanticTags;
        metadata["Categories"] = Enum.GetNames(typeof(WorkflowCategory));
        return metadata;
    }

    /// <inheritdoc/>
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
                var tags = new List<string> { "workflow", chars.Category.ToString().ToLowerInvariant() };
                tags.AddRange(chars.Tags);

                capabilities.Add(new RegisteredCapability
                {
                    CapabilityId = $"{Id}.strategy.{strategy.StrategyId.ToLowerInvariant()}",
                    DisplayName = chars.StrategyName,
                    Description = chars.Description,
                    Category = SDK.Contracts.CapabilityCategory.DataManagement,
                    SubCategory = "Workflow",
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = tags.ToArray(),
                    Metadata = new Dictionary<string, object>
                    {
                        ["category"] = chars.Category.ToString(),
                        ["supportsParallel"] = chars.Capabilities.SupportsParallelExecution,
                        ["supportsDynamic"] = chars.Capabilities.SupportsDynamicDag,
                        ["supportsDistributed"] = chars.Capabilities.SupportsDistributed,
                        ["supportsCheckpointing"] = chars.Capabilities.SupportsCheckpointing,
                        ["maxParallelTasks"] = chars.Capabilities.MaxParallelTasks
                    },
                    SemanticDescription = chars.Description
                });
            }

            return capabilities;
        }
    }

    #endregion

    /// <summary>Disposes the plugin.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_disposed) return;
            _disposed = true;
            _cts?.Cancel();
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
