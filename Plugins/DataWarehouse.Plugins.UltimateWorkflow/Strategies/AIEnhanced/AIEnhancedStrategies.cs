using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateWorkflow.Strategies.AIEnhanced;

/// <summary>
/// AI-enhanced workflow optimization strategy.
/// </summary>
public sealed class AIOptimizedWorkflowStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "AIOptimizedWorkflow",
        Description = "AI-enhanced workflow optimization with predictive scheduling and resource allocation",
        Category = WorkflowCategory.AIEnhanced,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: true,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: false,
            SupportsCheckpointing: true,
            SupportsDistributed: true,
            SupportsPriority: true,
            SupportsResourceManagement: true,
            MaxParallelTasks: 1000),
        Tags = ["ai", "optimization", "predictive"]
    };

    private readonly BoundedDictionary<string, double> _taskPerformanceHistory = new BoundedDictionary<string, double>(1000);

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

        var optimizedOrder = OptimizeExecutionOrder(workflow);
        var completed = new HashSet<string>();
        var predictedParallelism = PredictOptimalParallelism(workflow);

        while (completed.Count < workflow.Tasks.Count)
        {
            var ready = optimizedOrder
                .Where(t => !completed.Contains(t.TaskId))
                .Where(t => t.Dependencies.All(d => completed.Contains(d)))
                .Take(predictedParallelism)
                .ToList();

            if (ready.Count == 0) break;

            var tasks = ready.Select(async task =>
            {
                var taskStart = DateTime.UtcNow;
                var result = await ExecuteTaskAsync(task, context, cancellationToken);
                var duration = (DateTime.UtcNow - taskStart).TotalMilliseconds;

                _taskPerformanceHistory[task.TaskId] = duration;
                context.TaskResults[task.TaskId] = result;
                return task.TaskId;
            });

            foreach (var taskId in await Task.WhenAll(tasks))
                completed.Add(taskId);
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

    private List<WorkflowTask> OptimizeExecutionOrder(WorkflowDefinition workflow)
    {
        return workflow.Tasks
            .OrderByDescending(t => t.Dependencies.Count)
            .ThenByDescending(t => _taskPerformanceHistory.GetValueOrDefault(t.TaskId, 1000))
            .ToList();
    }

    private int PredictOptimalParallelism(WorkflowDefinition workflow)
    {
        if (_taskPerformanceHistory.Count == 0)
            return Math.Min(Environment.ProcessorCount, workflow.MaxParallelism);

        var avgDuration = _taskPerformanceHistory.Values.Average();
        return avgDuration < 50 ? workflow.MaxParallelism : Math.Max(2, workflow.MaxParallelism / 2);
    }
}

/// <summary>
/// Self-learning workflow strategy.
/// </summary>
public sealed class SelfLearningWorkflowStrategy : WorkflowStrategyBase
{
    private readonly BoundedDictionary<string, List<double>> _executionHistory = new BoundedDictionary<string, List<double>>(1000);
    private readonly BoundedDictionary<string, int> _failureHistory = new BoundedDictionary<string, int>(1000);

    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "SelfLearning",
        Description = "Self-learning workflow strategy that improves over time based on execution patterns",
        Category = WorkflowCategory.AIEnhanced,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: true,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: true,
            SupportsCheckpointing: true,
            SupportsDistributed: true,
            SupportsPriority: true,
            SupportsResourceManagement: true),
        Tags = ["ai", "self-learning", "adaptive"]
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

        foreach (var task in workflow.GetTopologicalOrder())
        {
            var adaptedTask = AdaptTaskBasedOnHistory(task);
            var taskStart = DateTime.UtcNow;

            var result = await ExecuteTaskAsync(adaptedTask, context, cancellationToken);
            var duration = (DateTime.UtcNow - taskStart).TotalMilliseconds;

            RecordExecution(task.TaskId, duration, result.Success);
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

    private WorkflowTask AdaptTaskBasedOnHistory(WorkflowTask task)
    {
        var failureRate = GetFailureRate(task.TaskId);
        var avgDuration = GetAverageDuration(task.TaskId);

        var adaptedRetries = failureRate > 0.3 ? task.MaxRetries + 2 : task.MaxRetries;
        var adaptedTimeout = avgDuration > 0 ? TimeSpan.FromMilliseconds(avgDuration * 3) : task.Timeout;

        return task with { MaxRetries = adaptedRetries, Timeout = adaptedTimeout };
    }

    private void RecordExecution(string taskId, double durationMs, bool success)
    {
        var history = _executionHistory.GetOrAdd(taskId, _ => new List<double>());
        lock (history)
        {
            history.Add(durationMs);
            if (history.Count > 100) history.RemoveAt(0);
        }

        if (!success)
            _failureHistory.AddOrUpdate(taskId, 1, (_, c) => c + 1);
    }

    private double GetFailureRate(string taskId)
    {
        var failures = _failureHistory.GetValueOrDefault(taskId, 0);
        var total = _executionHistory.TryGetValue(taskId, out var h) ? h.Count : 1;
        return (double)failures / Math.Max(total, 1);
    }

    private double GetAverageDuration(string taskId)
    {
        if (_executionHistory.TryGetValue(taskId, out var history) && history.Count > 0)
            lock (history) return history.Average();
        return 0;
    }
}

/// <summary>
/// Anomaly detection workflow strategy.
/// </summary>
public sealed class AnomalyDetectionWorkflowStrategy : WorkflowStrategyBase
{
    private readonly BoundedDictionary<string, (double Mean, double StdDev)> _taskStats = new BoundedDictionary<string, (double Mean, double StdDev)>(1000);

    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "AnomalyDetection",
        Description = "Anomaly detection workflow strategy that identifies unusual execution patterns",
        Category = WorkflowCategory.AIEnhanced,
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
        Tags = ["ai", "anomaly-detection", "monitoring"]
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

        var anomalies = new List<(string TaskId, string Anomaly)>();

        foreach (var task in workflow.GetTopologicalOrder())
        {
            var taskStart = DateTime.UtcNow;
            var result = await ExecuteTaskAsync(task, context, cancellationToken);
            var duration = (DateTime.UtcNow - taskStart).TotalMilliseconds;

            var anomaly = DetectAnomaly(task.TaskId, duration, result.Success);
            if (anomaly != null)
            {
                anomalies.Add((task.TaskId, anomaly));
                context.State[$"anomaly_{task.TaskId}"] = anomaly;
            }

            UpdateStats(task.TaskId, duration);
            context.TaskResults[task.TaskId] = result;
        }

        context.State["detected_anomalies"] = anomalies;

        return new WorkflowResult
        {
            WorkflowInstanceId = instanceId,
            Success = context.TaskResults.Values.All(r => r.Success),
            Status = WorkflowExecutionStatus.Completed,
            Duration = DateTime.UtcNow - startTime,
            TaskResults = context.TaskResults.ToDictionary(kv => kv.Key, kv => kv.Value)
        };
    }

    private string? DetectAnomaly(string taskId, double durationMs, bool success)
    {
        if (!_taskStats.TryGetValue(taskId, out var stats))
            return null;

        if (Math.Abs(durationMs - stats.Mean) > 3 * stats.StdDev)
            return $"Duration anomaly: {durationMs:F0}ms vs expected {stats.Mean:F0}ms (+/- {stats.StdDev:F0})";

        return null;
    }

    private void UpdateStats(string taskId, double durationMs)
    {
        _taskStats.AddOrUpdate(taskId,
            _ => (durationMs, 0),
            (_, old) =>
            {
                var newMean = (old.Mean + durationMs) / 2;
                var variance = Math.Pow(durationMs - newMean, 2) / 2;
                return (newMean, Math.Sqrt((old.StdDev * old.StdDev + variance) / 2));
            });
    }
}

/// <summary>
/// Predictive scaling workflow strategy.
/// </summary>
public sealed class PredictiveScalingStrategy : WorkflowStrategyBase
{
    private readonly BoundedDictionary<string, Queue<double>> _loadHistory = new BoundedDictionary<string, Queue<double>>(1000);

    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "PredictiveScaling",
        Description = "Predictive scaling workflow strategy that anticipates resource needs",
        Category = WorkflowCategory.AIEnhanced,
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
            MaxParallelTasks: 2000),
        Tags = ["ai", "predictive-scaling", "auto-scaling"]
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
        var currentParallelism = PredictInitialParallelism(workflow);

        while (completed.Count < workflow.Tasks.Count)
        {
            var pendingCount = workflow.Tasks.Count - completed.Count;
            currentParallelism = PredictNextParallelism(workflow.WorkflowId, pendingCount, currentParallelism);

            var ready = workflow.Tasks
                .Where(t => !completed.Contains(t.TaskId))
                .Where(t => t.Dependencies.All(d => completed.Contains(d)))
                .Take(currentParallelism)
                .ToList();

            if (ready.Count == 0) break;

            var loadStart = DateTime.UtcNow;
            var tasks = ready.Select(async task =>
            {
                var result = await ExecuteTaskAsync(task, context, cancellationToken);
                context.TaskResults[task.TaskId] = result;
                return task.TaskId;
            });

            foreach (var taskId in await Task.WhenAll(tasks))
                completed.Add(taskId);

            RecordLoad(workflow.WorkflowId, (DateTime.UtcNow - loadStart).TotalMilliseconds / ready.Count);
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

    private int PredictInitialParallelism(WorkflowDefinition workflow)
    {
        if (!_loadHistory.TryGetValue(workflow.WorkflowId, out var history) || history.Count == 0)
            return Math.Min(4, workflow.MaxParallelism);

        var avgLoad = history.Average();
        return avgLoad < 50 ? workflow.MaxParallelism : Math.Max(2, workflow.MaxParallelism / 2);
    }

    private int PredictNextParallelism(string workflowId, int pendingTasks, int currentParallelism)
    {
        if (!_loadHistory.TryGetValue(workflowId, out var history) || history.Count < 3)
            return currentParallelism;

        var trend = history.TakeLast(3).ToList();
        var increasing = trend[2] > trend[0];

        if (increasing && pendingTasks > currentParallelism * 2)
            return Math.Min(currentParallelism + 2, 100);
        if (!increasing && currentParallelism > 2)
            return currentParallelism - 1;

        return currentParallelism;
    }

    private void RecordLoad(string workflowId, double loadMs)
    {
        var history = _loadHistory.GetOrAdd(workflowId, _ => new Queue<double>());
        lock (history)
        {
            history.Enqueue(loadMs);
            while (history.Count > 20) history.Dequeue();
        }
    }
}

/// <summary>
/// Intelligent retry workflow strategy.
/// </summary>
public sealed class IntelligentRetryStrategy : WorkflowStrategyBase
{
    private readonly BoundedDictionary<string, List<(bool Success, string? ErrorType)>> _retryHistory = new BoundedDictionary<string, List<(bool Success, string? ErrorType)>>(1000);

    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "IntelligentRetry",
        Description = "Intelligent retry strategy that learns from failure patterns to optimize retry behavior",
        Category = WorkflowCategory.AIEnhanced,
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
        Tags = ["ai", "intelligent-retry", "adaptive"]
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

        foreach (var task in workflow.GetTopologicalOrder())
        {
            var intelligentRetries = CalculateIntelligentRetries(task);
            var adaptedTask = task with { MaxRetries = intelligentRetries };

            var result = await ExecuteTaskAsync(adaptedTask, context, cancellationToken);
            RecordOutcome(task.TaskId, result.Success, result.Error);
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

    private int CalculateIntelligentRetries(WorkflowTask task)
    {
        if (!_retryHistory.TryGetValue(task.TaskId, out var history) || history.Count < 5)
            return task.MaxRetries;

        var recentFailures = history.TakeLast(10).Where(h => !h.Success).ToList();
        var failureRate = (double)recentFailures.Count / 10;

        if (failureRate > 0.5)
        {
            var transientErrors = recentFailures.Count(f => IsTransientError(f.ErrorType));
            return transientErrors > recentFailures.Count / 2 ? task.MaxRetries + 3 : 1;
        }

        return task.MaxRetries;
    }

    private bool IsTransientError(string? errorType) =>
        errorType?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true ||
        errorType?.Contains("connection", StringComparison.OrdinalIgnoreCase) == true ||
        errorType?.Contains("temporary", StringComparison.OrdinalIgnoreCase) == true;

    private void RecordOutcome(string taskId, bool success, string? errorType)
    {
        var history = _retryHistory.GetOrAdd(taskId, _ => new List<(bool, string?)>());
        lock (history)
        {
            history.Add((success, errorType));
            if (history.Count > 100) history.RemoveAt(0);
        }
    }
}
