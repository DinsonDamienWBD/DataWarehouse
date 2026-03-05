using System.Collections.Concurrent;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateWorkflow.Strategies.ErrorHandling;

/// <summary>
/// Retry with exponential backoff strategy.
/// </summary>
public sealed class ExponentialBackoffRetryStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "ExponentialBackoffRetry",
        Description = "Retry strategy with exponential backoff and jitter for transient failures",
        Category = WorkflowCategory.ErrorHandling,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: false,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: false,
            SupportsCheckpointing: false,
            SupportsDistributed: true,
            SupportsPriority: false,
            SupportsResourceManagement: false),
        Tags = ["error", "retry", "exponential-backoff"]
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
            var result = await ExecuteWithExponentialBackoff(task, context, cancellationToken);
            context.TaskResults[task.TaskId] = result;

            if (!result.Success && workflow.ErrorStrategy == ErrorHandlingStrategy.FailFast)
                break;
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

    private async Task<TaskResult> ExecuteWithExponentialBackoff(
        WorkflowTask task,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        var random = Random.Shared;
        var baseDelay = task.RetryDelay.TotalMilliseconds;
        var attempt = 0;

        while (attempt <= task.MaxRetries)
        {
            var result = await ExecuteTaskAsync(task with { MaxRetries = 0 }, context, cancellationToken);
            if (result.Success)
                return result with { RetryCount = attempt };

            attempt++;
            if (attempt > task.MaxRetries)
                return result with { RetryCount = attempt };

            var delay = baseDelay * Math.Pow(2, attempt - 1);
            var jitter = delay * 0.2 * (Random.Shared.NextDouble() - 0.5);
            await Task.Delay(TimeSpan.FromMilliseconds(delay + jitter), cancellationToken);
        }

        return TaskResult.Failed("Max retries exceeded");
    }
}

/// <summary>
/// Circuit breaker error handling strategy.
/// </summary>
public sealed class CircuitBreakerStrategy : WorkflowStrategyBase
{
    private readonly BoundedDictionary<string, CircuitState> _circuits = new BoundedDictionary<string, CircuitState>(1000);

    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "CircuitBreaker",
        Description = "Circuit breaker pattern preventing cascading failures",
        Category = WorkflowCategory.ErrorHandling,
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
        Tags = ["error", "circuit-breaker", "resilience"]
    };

    private sealed class CircuitState
    {
        public int FailureCount { get; set; }
        public DateTimeOffset? OpenedAt { get; set; }
        public bool IsOpen => OpenedAt.HasValue && DateTimeOffset.UtcNow - OpenedAt.Value < TimeSpan.FromSeconds(30);
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

        foreach (var task in workflow.GetTopologicalOrder())
        {
            var circuit = _circuits.GetOrAdd(task.TaskId, _ => new CircuitState());

            if (circuit.IsOpen)
            {
                context.TaskResults[task.TaskId] = TaskResult.Failed("Circuit open");
                continue;
            }

            var result = await ExecuteTaskAsync(task, context, cancellationToken);
            context.TaskResults[task.TaskId] = result;

            if (!result.Success)
            {
                circuit.FailureCount++;
                if (circuit.FailureCount >= 5)
                    circuit.OpenedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                circuit.FailureCount = 0;
                circuit.OpenedAt = null;
            }
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
/// Fallback error handling strategy.
/// </summary>
public sealed class FallbackStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Fallback",
        Description = "Fallback strategy executing alternative tasks on failure",
        Category = WorkflowCategory.ErrorHandling,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: true,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: false,
            SupportsCheckpointing: false,
            SupportsDistributed: true,
            SupportsPriority: false,
            SupportsResourceManagement: false),
        Tags = ["error", "fallback", "alternative"]
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
            var result = await ExecuteTaskAsync(task, context, cancellationToken);

            if (!result.Success && task.Metadata.TryGetValue("fallbackHandler", out var fallback))
            {
                if (fallback is Func<WorkflowContext, CancellationToken, Task<TaskResult>> fallbackFunc)
                {
                    result = await fallbackFunc(context, cancellationToken);
                }
            }

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
/// Bulkhead isolation error handling strategy.
/// </summary>
public sealed class BulkheadIsolationStrategy : WorkflowStrategyBase, IDisposable
{
    private readonly BoundedDictionary<string, SemaphoreSlim> _bulkheads = new BoundedDictionary<string, SemaphoreSlim>(1000);
    private bool _disposed;

    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "BulkheadIsolation",
        Description = "Bulkhead pattern isolating task groups to prevent cascading failures",
        Category = WorkflowCategory.ErrorHandling,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: false,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: false,
            SupportsCheckpointing: false,
            SupportsDistributed: true,
            SupportsPriority: true,
            SupportsResourceManagement: true),
        Tags = ["error", "bulkhead", "isolation"]
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

        var tasks = workflow.GetTopologicalOrder().Select(async task =>
        {
            var bulkheadName = task.Metadata.GetValueOrDefault("bulkhead", "default")?.ToString() ?? "default";
            var bulkhead = _bulkheads.GetOrAdd(bulkheadName, _ => new SemaphoreSlim(3));

            await bulkhead.WaitAsync(cancellationToken);
            try
            {
                while (!task.Dependencies.All(d => completed.ContainsKey(d)))
                    await Task.Delay(10, cancellationToken);

                var result = await ExecuteTaskAsync(task, context, cancellationToken);
                context.TaskResults[task.TaskId] = result;
                completed[task.TaskId] = result;
            }
            finally
            {
                bulkhead.Release();
            }
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

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var semaphore in _bulkheads.Values)
            semaphore.Dispose();
    }
}

/// <summary>
/// Dead letter queue error handling strategy.
/// </summary>
public sealed class DeadLetterQueueStrategy : WorkflowStrategyBase
{
    private readonly ConcurrentQueue<(WorkflowTask Task, TaskResult Result, DateTimeOffset Timestamp)> _deadLetterQueue = new();

    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "DeadLetterQueue",
        Description = "Dead letter queue for failed tasks enabling later reprocessing",
        Category = WorkflowCategory.ErrorHandling,
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
        Tags = ["error", "dead-letter", "reprocessing"]
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
            var result = await ExecuteTaskAsync(task, context, cancellationToken);
            context.TaskResults[task.TaskId] = result;

            if (!result.Success)
            {
                _deadLetterQueue.Enqueue((task, result, DateTimeOffset.UtcNow));
            }
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

    public IEnumerable<(WorkflowTask Task, TaskResult Result, DateTimeOffset Timestamp)> GetDeadLetters() =>
        _deadLetterQueue.ToArray();

    public void ClearDeadLetterQueue()
    {
        while (_deadLetterQueue.TryDequeue(out _)) { }
    }
}

/// <summary>
/// Timeout handling strategy with cancellation.
/// </summary>
public sealed class TimeoutHandlingStrategy : WorkflowStrategyBase
{
    public override WorkflowCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "TimeoutHandling",
        Description = "Timeout handling with graceful cancellation and cleanup",
        Category = WorkflowCategory.ErrorHandling,
        Capabilities = new(
            SupportsParallelExecution: true,
            SupportsDynamicDag: false,
            SupportsConditionalBranching: true,
            SupportsRetry: true,
            SupportsCompensation: true,
            SupportsCheckpointing: false,
            SupportsDistributed: true,
            SupportsPriority: true,
            SupportsResourceManagement: false),
        Tags = ["error", "timeout", "cancellation"]
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

        using var globalCts = workflow.GlobalTimeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;

        if (globalCts != null)
            globalCts.CancelAfter(workflow.GlobalTimeout!.Value);

        var effectiveToken = globalCts?.Token ?? cancellationToken;
        var timedOut = false;

        try
        {
            foreach (var task in workflow.GetTopologicalOrder())
            {
                var result = await ExecuteTaskAsync(task, context, effectiveToken);
                context.TaskResults[task.TaskId] = result;
            }
        }
        catch (OperationCanceledException) when (globalCts?.IsCancellationRequested == true)
        {
            timedOut = true;
        }

        return new WorkflowResult
        {
            WorkflowInstanceId = instanceId,
            Success = !timedOut && context.TaskResults.Values.All(r => r.Success),
            Status = timedOut ? WorkflowExecutionStatus.TimedOut : WorkflowExecutionStatus.Completed,
            Duration = DateTime.UtcNow - startTime,
            TaskResults = context.TaskResults.ToDictionary(kv => kv.Key, kv => kv.Value),
            Error = timedOut ? "Workflow timed out" : null
        };
    }
}
