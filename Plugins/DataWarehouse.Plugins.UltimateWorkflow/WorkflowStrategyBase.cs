using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateWorkflow;

/// <summary>
/// Workflow task execution status.
/// </summary>
public enum TaskExecutionStatus
{
    Pending,
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
    Skipped,
    Retrying,
    TimedOut
}

/// <summary>
/// Workflow execution priority.
/// </summary>
public enum WorkflowPriority
{
    Low = 0,
    Normal = 50,
    High = 75,
    Critical = 100
}

/// <summary>
/// Workflow task definition.
/// </summary>
public sealed record WorkflowTask
{
    /// <summary>Task unique identifier.</summary>
    public required string TaskId { get; init; }

    /// <summary>Display name for the task.</summary>
    public required string Name { get; init; }

    /// <summary>Task description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Task dependencies (must complete before this task).</summary>
    public HashSet<string> Dependencies { get; init; } = new();

    /// <summary>Task execution handler.</summary>
    public Func<WorkflowContext, CancellationToken, Task<TaskResult>>? Handler { get; set; }

    /// <summary>Maximum retry attempts.</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>Retry delay strategy.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Task timeout.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Task priority.</summary>
    public WorkflowPriority Priority { get; init; } = WorkflowPriority.Normal;

    /// <summary>Condition to evaluate before execution.</summary>
    public Func<WorkflowContext, bool>? Condition { get; set; }

    /// <summary>Custom metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();

    /// <summary>Estimated execution time.</summary>
    public TimeSpan? EstimatedDuration { get; init; }

    /// <summary>Resource requirements.</summary>
    public Dictionary<string, double> ResourceRequirements { get; init; } = new();
}

/// <summary>
/// Result of a task execution.
/// </summary>
public sealed record TaskResult
{
    /// <summary>Whether the task succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Output data from the task.</summary>
    public object? Output { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? Error { get; init; }

    /// <summary>Execution duration.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Number of retries performed.</summary>
    public int RetryCount { get; init; }

    /// <summary>Custom metrics.</summary>
    public Dictionary<string, double> Metrics { get; init; } = new();

    public static TaskResult Succeeded(object? output = null, TimeSpan? duration = null) =>
        new() { Success = true, Output = output, Duration = duration ?? TimeSpan.Zero };

    public static TaskResult Failed(string error, TimeSpan? duration = null) =>
        new() { Success = false, Error = error, Duration = duration ?? TimeSpan.Zero };
}

/// <summary>
/// Workflow execution context.
/// </summary>
public sealed class WorkflowContext
{
    /// <summary>Workflow instance ID.</summary>
    public required string WorkflowInstanceId { get; init; }

    /// <summary>Workflow definition ID.</summary>
    public required string WorkflowId { get; init; }

    /// <summary>Input parameters.</summary>
    public Dictionary<string, object> Parameters { get; init; } = new();

    /// <summary>Shared state across tasks.</summary>
    public ConcurrentDictionary<string, object> State { get; } = new();

    /// <summary>Results from completed tasks.</summary>
    public ConcurrentDictionary<string, TaskResult> TaskResults { get; } = new();

    /// <summary>Workflow start time.</summary>
    public DateTimeOffset StartTime { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Current execution metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();

    /// <summary>Gets result of a previous task.</summary>
    public T? GetTaskOutput<T>(string taskId) =>
        TaskResults.TryGetValue(taskId, out var result) && result.Output is T output ? output : default;
}

/// <summary>
/// Workflow definition representing a DAG.
/// </summary>
public sealed class WorkflowDefinition
{
    /// <summary>Unique workflow ID.</summary>
    public required string WorkflowId { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Version.</summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>Tasks in this workflow.</summary>
    public List<WorkflowTask> Tasks { get; init; } = new();

    /// <summary>Global timeout for entire workflow.</summary>
    public TimeSpan? GlobalTimeout { get; init; }

    /// <summary>Maximum parallel tasks.</summary>
    public int MaxParallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>Workflow priority.</summary>
    public WorkflowPriority Priority { get; init; } = WorkflowPriority.Normal;

    /// <summary>Custom metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();

    /// <summary>Error handling strategy.</summary>
    public ErrorHandlingStrategy ErrorStrategy { get; init; } = ErrorHandlingStrategy.FailFast;

    /// <summary>
    /// Validates the workflow DAG is acyclic.
    /// </summary>
    public bool ValidateDag()
    {
        var visited = new HashSet<string>();
        var recursionStack = new HashSet<string>();

        foreach (var task in Tasks)
        {
            if (HasCycle(task.TaskId, visited, recursionStack))
                return false;
        }
        return true;
    }

    private bool HasCycle(string taskId, HashSet<string> visited, HashSet<string> recursionStack)
    {
        if (recursionStack.Contains(taskId)) return true;
        if (visited.Contains(taskId)) return false;

        visited.Add(taskId);
        recursionStack.Add(taskId);

        var task = Tasks.Find(t => t.TaskId == taskId);
        if (task != null)
        {
            foreach (var dep in task.Dependencies)
            {
                if (HasCycle(dep, visited, recursionStack))
                    return true;
            }
        }

        recursionStack.Remove(taskId);
        return false;
    }

    /// <summary>
    /// Gets tasks in topological order.
    /// </summary>
    public IEnumerable<WorkflowTask> GetTopologicalOrder()
    {
        var inDegree = Tasks.ToDictionary(t => t.TaskId, t => t.Dependencies.Count);
        var queue = new Queue<WorkflowTask>(Tasks.Where(t => t.Dependencies.Count == 0));
        var result = new List<WorkflowTask>();

        while (queue.Count > 0)
        {
            var task = queue.Dequeue();
            result.Add(task);

            foreach (var dependent in Tasks.Where(t => t.Dependencies.Contains(task.TaskId)))
            {
                inDegree[dependent.TaskId]--;
                if (inDegree[dependent.TaskId] == 0)
                    queue.Enqueue(dependent);
            }
        }

        return result;
    }
}

/// <summary>
/// Error handling strategy for workflows.
/// </summary>
public enum ErrorHandlingStrategy
{
    FailFast,
    ContinueOnError,
    FailAfterAll,
    Compensate
}

/// <summary>
/// Workflow execution result.
/// </summary>
public sealed class WorkflowResult
{
    /// <summary>Workflow instance ID.</summary>
    public required string WorkflowInstanceId { get; init; }

    /// <summary>Overall success.</summary>
    public bool Success { get; init; }

    /// <summary>Execution status.</summary>
    public WorkflowExecutionStatus Status { get; init; }

    /// <summary>Total duration.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Task results.</summary>
    public Dictionary<string, TaskResult> TaskResults { get; init; } = new();

    /// <summary>Error message if failed.</summary>
    public string? Error { get; init; }

    /// <summary>Workflow output.</summary>
    public object? Output { get; init; }
}

/// <summary>
/// Workflow execution status.
/// </summary>
public enum WorkflowExecutionStatus
{
    NotStarted,
    Running,
    Completed,
    Failed,
    Cancelled,
    Paused,
    TimedOut
}

/// <summary>
/// Workflow strategy capabilities.
/// </summary>
public sealed record WorkflowCapabilities(
    bool SupportsParallelExecution,
    bool SupportsDynamicDag,
    bool SupportsConditionalBranching,
    bool SupportsRetry,
    bool SupportsCompensation,
    bool SupportsCheckpointing,
    bool SupportsDistributed,
    bool SupportsPriority,
    bool SupportsResourceManagement,
    int MaxParallelTasks = 100,
    int MaxTasksPerWorkflow = 10000);

/// <summary>
/// Workflow strategy characteristics.
/// </summary>
public sealed record WorkflowCharacteristics
{
    /// <summary>Strategy name.</summary>
    public required string StrategyName { get; init; }

    /// <summary>Description.</summary>
    public required string Description { get; init; }

    /// <summary>Category.</summary>
    public required WorkflowCategory Category { get; init; }

    /// <summary>Capabilities.</summary>
    public required WorkflowCapabilities Capabilities { get; init; }

    /// <summary>Typical task overhead in ms.</summary>
    public int TypicalTaskOverheadMs { get; init; } = 1;

    /// <summary>Semantic tags.</summary>
    public string[] Tags { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Workflow strategy category.
/// </summary>
public enum WorkflowCategory
{
    DagExecution,
    TaskScheduling,
    ParallelExecution,
    DependencyManagement,
    StateManagement,
    ErrorHandling,
    ResourceManagement,
    Distributed,
    AIEnhanced
}

/// <summary>
/// Base class for workflow strategies.
/// </summary>
public abstract class WorkflowStrategyBase
{
    /// <summary>Strategy characteristics.</summary>
    public abstract WorkflowCharacteristics Characteristics { get; }

    /// <summary>Strategy ID for registry.</summary>
    public string StrategyId => Characteristics.StrategyName.Replace(" ", "");

    /// <summary>
    /// Executes a workflow definition.
    /// </summary>
    public abstract Task<WorkflowResult> ExecuteAsync(
        WorkflowDefinition workflow,
        Dictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a workflow definition.
    /// </summary>
    public virtual bool Validate(WorkflowDefinition workflow, out List<string> errors)
    {
        errors = new List<string>();

        if (string.IsNullOrEmpty(workflow.WorkflowId))
            errors.Add("Workflow ID is required");

        if (workflow.Tasks.Count == 0)
            errors.Add("Workflow must have at least one task");

        if (!workflow.ValidateDag())
            errors.Add("Workflow contains cyclic dependencies");

        var taskIds = workflow.Tasks.Select(t => t.TaskId).ToHashSet();
        foreach (var task in workflow.Tasks)
        {
            foreach (var dep in task.Dependencies)
            {
                if (!taskIds.Contains(dep))
                    errors.Add($"Task '{task.TaskId}' has unknown dependency '{dep}'");
            }
        }

        return errors.Count == 0;
    }

    /// <summary>
    /// Gets ready tasks (all dependencies satisfied).
    /// </summary>
    protected IEnumerable<WorkflowTask> GetReadyTasks(
        WorkflowDefinition workflow,
        HashSet<string> completed,
        HashSet<string> running)
    {
        return workflow.Tasks
            .Where(t => !completed.Contains(t.TaskId) && !running.Contains(t.TaskId))
            .Where(t => t.Dependencies.All(d => completed.Contains(d)));
    }

    /// <summary>
    /// Executes a single task with retry logic.
    /// </summary>
    protected async Task<TaskResult> ExecuteTaskAsync(
        WorkflowTask task,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        if (task.Handler == null)
            return TaskResult.Failed("No handler defined for task");

        if (task.Condition != null && !task.Condition(context))
            return new TaskResult { Success = true, Output = null, Duration = TimeSpan.Zero };

        var startTime = DateTime.UtcNow;
        var retryCount = 0;
        Exception? lastException = null;

        while (retryCount <= task.MaxRetries)
        {
            try
            {
                using var cts = task.Timeout.HasValue
                    ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                    : null;

                if (cts != null)
                    cts.CancelAfter(task.Timeout!.Value);

                var result = await task.Handler(context, cts?.Token ?? cancellationToken);
                result = result with { RetryCount = retryCount, Duration = DateTime.UtcNow - startTime };
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return TaskResult.Failed("Task cancelled", DateTime.UtcNow - startTime);
            }
            catch (OperationCanceledException)
            {
                return new TaskResult
                {
                    Success = false,
                    Error = "Task timed out",
                    Duration = DateTime.UtcNow - startTime,
                    RetryCount = retryCount
                };
            }
            catch (Exception ex)
            {
                lastException = ex;
                retryCount++;

                if (retryCount <= task.MaxRetries)
                {
                    var delay = TimeSpan.FromMilliseconds(
                        task.RetryDelay.TotalMilliseconds * Math.Pow(2, retryCount - 1));
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }

        return new TaskResult
        {
            Success = false,
            Error = lastException?.Message ?? "Task failed after retries",
            Duration = DateTime.UtcNow - startTime,
            RetryCount = retryCount
        };
    }
}
