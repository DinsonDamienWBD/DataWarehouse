using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Infrastructure;

/// <summary>
/// Priority-based job scheduler for storage processing operations.
/// Manages a queue of processing jobs with configurable concurrency control
/// and priority-based execution ordering.
/// </summary>
/// <remarks>
/// <para>
/// Uses a <see cref="PriorityQueue{TElement, TPriority}"/> for ordering and a
/// <see cref="SemaphoreSlim"/> for concurrency limiting. Jobs are dequeued in
/// priority order and executed up to the configured maximum concurrency level.
/// </para>
/// </remarks>
internal sealed class ProcessingJobScheduler : IDisposable
{
    private readonly PriorityQueue<ScheduledJob, int> _queue = new();
    private readonly ConcurrentDictionary<string, ScheduledJob> _activeJobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new();
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly object _queueLock = new();
    private long _jobCounter;
    private bool _disposed;

    /// <summary>
    /// Gets the maximum number of concurrent processing jobs.
    /// </summary>
    public int MaxConcurrency { get; }

    /// <summary>
    /// Gets the number of currently pending jobs in the queue.
    /// </summary>
    public int PendingCount
    {
        get { lock (_queueLock) { return _queue.Count; } }
    }

    /// <summary>
    /// Gets the number of currently active jobs.
    /// </summary>
    public int ActiveCount => _activeJobs.Count;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProcessingJobScheduler"/> class.
    /// </summary>
    /// <param name="maxConcurrency">Maximum number of concurrent processing jobs. Defaults to processor count.</param>
    public ProcessingJobScheduler(int maxConcurrency = 0)
    {
        MaxConcurrency = maxConcurrency > 0 ? maxConcurrency : Environment.ProcessorCount;
        _concurrencyGate = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
    }

    /// <summary>
    /// Schedules a processing query for execution against the specified strategy.
    /// </summary>
    /// <param name="strategy">The storage processing strategy to execute against.</param>
    /// <param name="query">The processing query to execute.</param>
    /// <param name="priority">Job priority (lower values execute first). Defaults to 5.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the scheduled job, yielding the processing result.</returns>
    public async Task<ProcessingResult> ScheduleAsync(
        IStorageProcessingStrategy strategy,
        ProcessingQuery query,
        int priority = 5,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(query);

        var jobId = $"job-{Interlocked.Increment(ref _jobCounter):D8}";
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tcs = new TaskCompletionSource<ProcessingResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var job = new ScheduledJob
        {
            JobId = jobId,
            Strategy = strategy,
            Query = query,
            Priority = priority,
            ScheduledAt = DateTimeOffset.UtcNow,
            Completion = tcs
        };

        _cancellations.TryAdd(jobId, cts);

        lock (_queueLock)
        {
            _queue.Enqueue(job, priority);
        }

        // Attempt to drain the queue
        _ = DrainQueueAsync();

        return await tcs.Task;
    }

    /// <summary>
    /// Cancels a scheduled or active job by its identifier.
    /// </summary>
    /// <param name="jobId">The job identifier to cancel.</param>
    /// <returns>True if the job was found and cancellation was requested; false otherwise.</returns>
    public bool Cancel(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);

        if (_cancellations.TryRemove(jobId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets the count of pending jobs in the queue.
    /// </summary>
    /// <returns>Number of pending jobs.</returns>
    public int GetPendingCount() => PendingCount;

    /// <summary>
    /// Gets statistics about the scheduler state.
    /// </summary>
    /// <returns>A dictionary of scheduler statistics.</returns>
    public IReadOnlyDictionary<string, object> GetStats()
    {
        return new Dictionary<string, object>
        {
            ["maxConcurrency"] = MaxConcurrency,
            ["pendingJobs"] = PendingCount,
            ["activeJobs"] = ActiveCount,
            ["totalScheduled"] = Interlocked.Read(ref _jobCounter)
        };
    }

    private async Task DrainQueueAsync()
    {
        while (true)
        {
            ScheduledJob? job;
            lock (_queueLock)
            {
                if (!_queue.TryDequeue(out job, out _))
                    return;
            }

            await _concurrencyGate.WaitAsync();

            _ = Task.Run(async () =>
            {
                try
                {
                    _activeJobs.TryAdd(job.JobId, job);

                    var cts = _cancellations.GetValueOrDefault(job.JobId);
                    var token = cts?.Token ?? CancellationToken.None;

                    var result = await job.Strategy.ProcessAsync(job.Query, token);
                    job.Completion.TrySetResult(result);
                }
                catch (OperationCanceledException)
                {
                    job.Completion.TrySetCanceled();
                }
                catch (Exception ex)
                {
                    job.Completion.TrySetException(ex);
                }
                finally
                {
                    _activeJobs.TryRemove(job.JobId, out _);
                    if (_cancellations.TryRemove(job.JobId, out var cts))
                    {
                        cts.Dispose();
                    }
                    _concurrencyGate.Release();
                }
            });
        }
    }

    /// <summary>
    /// Disposes scheduler resources including the concurrency semaphore.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        foreach (var cts in _cancellations.Values)
        {
            try { cts.Cancel(); cts.Dispose(); }
            catch { /* Ignore */ }
        }
        _cancellations.Clear();

        _concurrencyGate.Dispose();
        _disposed = true;
    }

    /// <summary>
    /// Represents a scheduled processing job.
    /// </summary>
    private sealed class ScheduledJob
    {
        /// <summary>Gets or sets the unique job identifier.</summary>
        public required string JobId { get; init; }

        /// <summary>Gets or sets the strategy to execute.</summary>
        public required IStorageProcessingStrategy Strategy { get; init; }

        /// <summary>Gets or sets the processing query.</summary>
        public required ProcessingQuery Query { get; init; }

        /// <summary>Gets or sets the job priority (lower = higher priority).</summary>
        public required int Priority { get; init; }

        /// <summary>Gets or sets when the job was scheduled.</summary>
        public required DateTimeOffset ScheduledAt { get; init; }

        /// <summary>Gets or sets the task completion source for the result.</summary>
        public required TaskCompletionSource<ProcessingResult> Completion { get; init; }
    }
}
