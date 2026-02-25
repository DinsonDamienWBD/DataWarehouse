using DataWarehouse.SDK.Contracts.Compute;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompute.Infrastructure;

/// <summary>
/// Priority-based job scheduler for compute task execution with configurable concurrency.
/// Uses a priority queue to order tasks and a semaphore for concurrency control.
/// </summary>
/// <remarks>
/// <para>
/// The scheduler supports:
/// </para>
/// <list type="bullet">
/// <item><description>Priority-based scheduling via <see cref="PriorityQueue{TElement,TPriority}"/></description></item>
/// <item><description>Configurable max concurrency via <see cref="SemaphoreSlim"/></description></item>
/// <item><description>Task status tracking (Pending, Running, Completed, Failed, Cancelled)</description></item>
/// <item><description>Cancellation support per task</description></item>
/// </list>
/// </remarks>
internal sealed class JobScheduler : IDisposable
{
    private readonly PriorityQueue<ScheduledJob, int> _queue = new();
    private readonly BoundedDictionary<string, ScheduledJob> _jobs = new BoundedDictionary<string, ScheduledJob>(1000);
    private readonly BoundedDictionary<string, CancellationTokenSource> _cancellations = new BoundedDictionary<string, CancellationTokenSource>(1000);
    private readonly SemaphoreSlim _concurrencySemaphore;
    private readonly object _queueLock = new();
    private readonly int _maxConcurrency;
    private int _activeJobs;
    private bool _disposed;

    /// <summary>
    /// Initializes the job scheduler with the specified maximum concurrency.
    /// </summary>
    /// <param name="maxConcurrency">Maximum number of concurrent compute tasks. Defaults to processor count.</param>
    public JobScheduler(int maxConcurrency = 0)
    {
        _maxConcurrency = maxConcurrency > 0 ? maxConcurrency : Environment.ProcessorCount;
        _concurrencySemaphore = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
    }

    /// <summary>
    /// Gets the number of pending jobs in the queue.
    /// </summary>
    public int PendingCount
    {
        get { lock (_queueLock) { return _queue.Count; } }
    }

    /// <summary>
    /// Gets the number of currently active (running) jobs.
    /// </summary>
    public int ActiveCount => Volatile.Read(ref _activeJobs);

    /// <summary>
    /// Gets the maximum concurrency level.
    /// </summary>
    public int MaxConcurrency => _maxConcurrency;

    /// <summary>
    /// Schedules a compute task for execution with the specified priority.
    /// Lower priority values are scheduled first (higher priority).
    /// </summary>
    /// <param name="task">The compute task to schedule.</param>
    /// <param name="strategy">The strategy to execute the task with.</param>
    /// <param name="priority">Scheduling priority (lower = higher priority). Defaults to 5.</param>
    /// <param name="cancellationToken">External cancellation token.</param>
    /// <returns>A task that completes with the compute result when the job finishes.</returns>
    /// <exception cref="ArgumentNullException">Thrown if task or strategy is null.</exception>
    public async Task<ComputeResult> ScheduleAsync(
        ComputeTask task,
        IComputeRuntimeStrategy strategy,
        int priority = 5,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(strategy);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var jobCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tcs = new TaskCompletionSource<ComputeResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var job = new ScheduledJob
        {
            TaskId = task.Id,
            ComputeTask = task,
            Strategy = strategy,
            Priority = priority,
            Status = JobStatus.Pending,
            ScheduledAt = DateTime.UtcNow,
            CompletionSource = tcs
        };

        _jobs[task.Id] = job;
        _cancellations[task.Id] = jobCts;

        // Enqueue and start processing
        lock (_queueLock)
        {
            _queue.Enqueue(job, priority);
        }

        _ = ProcessQueueAsync(jobCts.Token);

        return await tcs.Task;
    }

    /// <summary>
    /// Cancels a pending or running job.
    /// </summary>
    /// <param name="taskId">The task identifier to cancel.</param>
    /// <returns>True if the job was found and cancellation was requested; false otherwise.</returns>
    public bool Cancel(string taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);

        if (_cancellations.TryGetValue(taskId, out var cts))
        {
            cts.Cancel();
            if (_jobs.TryGetValue(taskId, out var job))
            {
                job.Status = JobStatus.Cancelled;
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Gets the current status of a scheduled job.
    /// </summary>
    /// <param name="taskId">The task identifier.</param>
    /// <returns>The job status, or null if the job is not found.</returns>
    public JobStatus? GetStatus(string taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        return _jobs.TryGetValue(taskId, out var job) ? job.Status : null;
    }

    /// <summary>
    /// Gets detailed information about a scheduled job.
    /// </summary>
    /// <param name="taskId">The task identifier.</param>
    /// <returns>The job info, or null if not found.</returns>
    public JobInfo? GetJobInfo(string taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        if (!_jobs.TryGetValue(taskId, out var job))
            return null;

        return new JobInfo(
            TaskId: job.TaskId,
            Status: job.Status,
            Priority: job.Priority,
            ScheduledAt: job.ScheduledAt,
            StartedAt: job.StartedAt,
            CompletedAt: job.CompletedAt
        );
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        ScheduledJob? job;
        lock (_queueLock)
        {
            if (!_queue.TryDequeue(out job, out _))
                return;
        }

        if (job.Status == JobStatus.Cancelled)
        {
            job.CompletionSource.TrySetResult(ComputeResult.CreateCancelled(job.TaskId));
            return;
        }

        try
        {
            await _concurrencySemaphore.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
            job.CompletionSource.TrySetResult(ComputeResult.CreateCancelled(job.TaskId));
            return;
        }

        Interlocked.Increment(ref _activeJobs);
        job.Status = JobStatus.Running;
        job.StartedAt = DateTime.UtcNow;

        try
        {
            var result = await job.Strategy.ExecuteAsync(job.ComputeTask, ct);
            job.Status = result.Success ? JobStatus.Completed : JobStatus.Failed;
            job.CompletedAt = DateTime.UtcNow;
            job.CompletionSource.TrySetResult(result);
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
            job.CompletionSource.TrySetResult(ComputeResult.CreateCancelled(job.TaskId));
        }
        catch (Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.CompletedAt = DateTime.UtcNow;
            job.CompletionSource.TrySetResult(ComputeResult.CreateFailure(job.TaskId, ex.Message, ex.ToString()));
        }
        finally
        {
            Interlocked.Decrement(ref _activeJobs);
            _concurrencySemaphore.Release();
            _cancellations.TryRemove(job.TaskId, out _);
        }
    }

    /// <summary>
    /// Disposes scheduler resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var cts in _cancellations.Values)
        {
            try { cts.Cancel(); cts.Dispose(); } catch { /* best effort */ }
        }

        _concurrencySemaphore.Dispose();
    }

    /// <summary>
    /// Internal representation of a scheduled job.
    /// </summary>
    private sealed class ScheduledJob
    {
        public required string TaskId { get; init; }
        public required ComputeTask ComputeTask { get; init; }
        public required IComputeRuntimeStrategy Strategy { get; init; }
        public required int Priority { get; init; }
        public JobStatus Status { get; set; }
        public required DateTime ScheduledAt { get; init; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public required TaskCompletionSource<ComputeResult> CompletionSource { get; init; }
    }
}

/// <summary>
/// Status of a scheduled compute job.
/// </summary>
internal enum JobStatus
{
    /// <summary>Job is waiting in the queue.</summary>
    Pending,
    /// <summary>Job is currently executing.</summary>
    Running,
    /// <summary>Job completed successfully.</summary>
    Completed,
    /// <summary>Job failed during execution.</summary>
    Failed,
    /// <summary>Job was cancelled.</summary>
    Cancelled
}

/// <summary>
/// Read-only information about a scheduled job.
/// </summary>
/// <param name="TaskId">The task identifier.</param>
/// <param name="Status">Current job status.</param>
/// <param name="Priority">Scheduling priority.</param>
/// <param name="ScheduledAt">When the job was scheduled.</param>
/// <param name="StartedAt">When execution started, if applicable.</param>
/// <param name="CompletedAt">When the job completed, if applicable.</param>
internal record JobInfo(
    string TaskId,
    JobStatus Status,
    int Priority,
    DateTime ScheduledAt,
    DateTime? StartedAt,
    DateTime? CompletedAt
);
