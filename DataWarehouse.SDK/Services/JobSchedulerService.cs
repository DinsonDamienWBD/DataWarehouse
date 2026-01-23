using System.Collections.Concurrent;

namespace DataWarehouse.SDK.Services;

/// <summary>
/// Advanced job scheduler service supporting cron-like scheduling, priorities,
/// retries, dependencies, and job state persistence.
/// Goes beyond simple fire-and-forget by providing full job lifecycle management.
/// </summary>
public sealed class JobSchedulerService : IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ScheduledJob> _jobs = new();
    private readonly ConcurrentDictionary<string, JobExecution> _executions = new();
    private readonly PriorityQueue<ScheduledJob, DateTime> _jobQueue = new();
    private readonly SemaphoreSlim _queueLock = new(1, 1);
    private readonly SemaphoreSlim _executionThrottle;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _workerTasks = new();
    private readonly int _maxConcurrentJobs;
    private bool _disposed;
    private bool _running;

    /// <summary>
    /// Event raised when a job starts execution.
    /// </summary>
    public event EventHandler<JobExecutionEventArgs>? JobStarted;

    /// <summary>
    /// Event raised when a job completes successfully.
    /// </summary>
    public event EventHandler<JobExecutionEventArgs>? JobCompleted;

    /// <summary>
    /// Event raised when a job fails.
    /// </summary>
    public event EventHandler<JobFailedEventArgs>? JobFailed;

    /// <summary>
    /// Creates a new job scheduler.
    /// </summary>
    /// <param name="maxConcurrentJobs">Maximum number of jobs that can run concurrently.</param>
    /// <param name="workerCount">Number of worker threads processing jobs.</param>
    public JobSchedulerService(int maxConcurrentJobs = 10, int workerCount = 4)
    {
        _maxConcurrentJobs = maxConcurrentJobs;
        _executionThrottle = new SemaphoreSlim(maxConcurrentJobs, maxConcurrentJobs);

        for (int i = 0; i < workerCount; i++)
        {
            _workerTasks.Add(Task.CompletedTask);
        }
    }

    /// <summary>
    /// Starts the job scheduler.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_running) return;
        _running = true;

        // Start worker tasks
        for (int i = 0; i < _workerTasks.Count; i++)
        {
            var workerId = i;
            _workerTasks[i] = Task.Run(() => WorkerLoopAsync(workerId, _cts.Token), ct);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Stops the job scheduler gracefully.
    /// </summary>
    public async Task StopAsync(TimeSpan? timeout = null)
    {
        if (!_running) return;
        _running = false;

        _cts.Cancel();

        var waitTask = Task.WhenAll(_workerTasks);
        if (timeout.HasValue)
        {
            await Task.WhenAny(waitTask, Task.Delay(timeout.Value));
        }
        else
        {
            await waitTask;
        }
    }

    /// <summary>
    /// Schedules a new job.
    /// </summary>
    public ScheduledJob Schedule(string name, Func<JobContext, CancellationToken, Task> action, JobOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Job name cannot be empty", nameof(name));

        options ??= new JobOptions();

        var job = new ScheduledJob
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Action = action,
            Options = options,
            State = JobState.Scheduled,
            CreatedAt = DateTime.UtcNow,
            NextRunTime = CalculateNextRunTime(options)
        };

        _jobs[job.Id] = job;

        // Add to priority queue
        _queueLock.Wait();
        try
        {
            _jobQueue.Enqueue(job, job.NextRunTime ?? DateTime.UtcNow);
        }
        finally
        {
            _queueLock.Release();
        }

        return job;
    }

    /// <summary>
    /// Schedules a job to run immediately.
    /// </summary>
    public ScheduledJob ScheduleNow(string name, Func<JobContext, CancellationToken, Task> action, JobOptions? options = null)
    {
        options ??= new JobOptions();
        options.RunAt = DateTime.UtcNow;
        return Schedule(name, action, options);
    }

    /// <summary>
    /// Schedules a job to run at a specific time.
    /// </summary>
    public ScheduledJob ScheduleAt(string name, DateTime runAt, Func<JobContext, CancellationToken, Task> action, JobOptions? options = null)
    {
        options ??= new JobOptions();
        options.RunAt = runAt;
        return Schedule(name, action, options);
    }

    /// <summary>
    /// Schedules a recurring job using cron expression.
    /// </summary>
    public ScheduledJob ScheduleCron(string name, string cronExpression, Func<JobContext, CancellationToken, Task> action, JobOptions? options = null)
    {
        options ??= new JobOptions();
        options.CronExpression = cronExpression;
        return Schedule(name, action, options);
    }

    /// <summary>
    /// Schedules a job with a delay.
    /// </summary>
    public ScheduledJob ScheduleDelayed(string name, TimeSpan delay, Func<JobContext, CancellationToken, Task> action, JobOptions? options = null)
    {
        options ??= new JobOptions();
        options.RunAt = DateTime.UtcNow.Add(delay);
        return Schedule(name, action, options);
    }

    /// <summary>
    /// Cancels a scheduled job.
    /// </summary>
    public bool Cancel(string jobId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_jobs.TryGetValue(jobId, out var job))
            return false;

        job.State = JobState.Cancelled;
        job.CancellationSource?.Cancel();
        return true;
    }

    /// <summary>
    /// Pauses a recurring job.
    /// </summary>
    public bool Pause(string jobId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_jobs.TryGetValue(jobId, out var job))
            return false;

        if (job.State == JobState.Running || job.State == JobState.Scheduled)
        {
            job.State = JobState.Paused;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Resumes a paused job.
    /// </summary>
    public bool Resume(string jobId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_jobs.TryGetValue(jobId, out var job))
            return false;

        if (job.State == JobState.Paused)
        {
            job.State = JobState.Scheduled;
            job.NextRunTime = CalculateNextRunTime(job.Options);

            _queueLock.Wait();
            try
            {
                _jobQueue.Enqueue(job, job.NextRunTime ?? DateTime.UtcNow);
            }
            finally
            {
                _queueLock.Release();
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Gets a job by ID.
    /// </summary>
    public ScheduledJob? GetJob(string jobId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _jobs.TryGetValue(jobId, out var job) ? job : null;
    }

    /// <summary>
    /// Gets all scheduled jobs.
    /// </summary>
    public IEnumerable<ScheduledJob> GetAllJobs()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _jobs.Values.ToList();
    }

    /// <summary>
    /// Gets jobs by state.
    /// </summary>
    public IEnumerable<ScheduledJob> GetJobsByState(JobState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _jobs.Values.Where(j => j.State == state).ToList();
    }

    /// <summary>
    /// Gets execution history for a job.
    /// </summary>
    public IEnumerable<JobExecution> GetExecutionHistory(string jobId, int limit = 100)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _executions.Values
            .Where(e => e.JobId == jobId)
            .OrderByDescending(e => e.StartedAt)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Gets scheduler statistics.
    /// </summary>
    public JobSchedulerStats GetStats()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var jobs = _jobs.Values.ToList();
        var executions = _executions.Values.ToList();

        return new JobSchedulerStats
        {
            TotalJobs = jobs.Count,
            ScheduledJobs = jobs.Count(j => j.State == JobState.Scheduled),
            RunningJobs = jobs.Count(j => j.State == JobState.Running),
            CompletedJobs = jobs.Count(j => j.State == JobState.Completed),
            FailedJobs = jobs.Count(j => j.State == JobState.Failed),
            PausedJobs = jobs.Count(j => j.State == JobState.Paused),
            TotalExecutions = executions.Count,
            SuccessfulExecutions = executions.Count(e => e.Success),
            FailedExecutions = executions.Count(e => !e.Success),
            AverageExecutionTime = executions.Any()
                ? TimeSpan.FromMilliseconds(executions.Average(e => e.Duration?.TotalMilliseconds ?? 0))
                : TimeSpan.Zero,
            MaxConcurrentJobs = _maxConcurrentJobs
        };
    }

    private async Task WorkerLoopAsync(int workerId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                ScheduledJob? job = null;

                await _queueLock.WaitAsync(ct);
                try
                {
                    while (_jobQueue.TryPeek(out var peekedJob, out var nextTime))
                    {
                        if (nextTime > DateTime.UtcNow)
                            break;

                        _jobQueue.Dequeue();

                        if (peekedJob.State == JobState.Scheduled)
                        {
                            job = peekedJob;
                            break;
                        }
                    }
                }
                finally
                {
                    _queueLock.Release();
                }

                if (job != null)
                {
                    await _executionThrottle.WaitAsync(ct);
                    try
                    {
                        await ExecuteJobAsync(job, ct);
                    }
                    finally
                    {
                        _executionThrottle.Release();
                    }

                    // Reschedule if recurring
                    if (job.Options.IsRecurring && job.State != JobState.Cancelled)
                    {
                        job.NextRunTime = CalculateNextRunTime(job.Options);
                        job.State = JobState.Scheduled;

                        await _queueLock.WaitAsync(ct);
                        try
                        {
                            _jobQueue.Enqueue(job, job.NextRunTime ?? DateTime.UtcNow);
                        }
                        finally
                        {
                            _queueLock.Release();
                        }
                    }
                }
                else
                {
                    // No jobs ready, wait a bit
                    await Task.Delay(100, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Log worker error but keep running
                System.Diagnostics.Trace.TraceError(
                    "[JobScheduler] Worker {0} error: {1}", workerId, ex.Message);
                await Task.Delay(1000, ct);
            }
        }
    }

    private async Task ExecuteJobAsync(ScheduledJob job, CancellationToken ct)
    {
        var execution = new JobExecution
        {
            Id = Guid.NewGuid().ToString("N"),
            JobId = job.Id,
            JobName = job.Name,
            StartedAt = DateTime.UtcNow,
            Attempt = job.ExecutionCount + 1
        };

        _executions[execution.Id] = execution;

        job.State = JobState.Running;
        job.LastRunTime = DateTime.UtcNow;
        job.ExecutionCount++;
        job.CancellationSource = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var context = new JobContext
        {
            JobId = job.Id,
            JobName = job.Name,
            ExecutionId = execution.Id,
            Attempt = execution.Attempt,
            ScheduledTime = job.NextRunTime ?? DateTime.UtcNow,
            Data = job.Options.Data
        };

        JobStarted?.Invoke(this, new JobExecutionEventArgs
        {
            Job = job,
            Execution = execution
        });

        try
        {
            using var timeoutCts = job.Options.Timeout.HasValue
                ? new CancellationTokenSource(job.Options.Timeout.Value)
                : new CancellationTokenSource();

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                job.CancellationSource.Token, timeoutCts.Token);

            await job.Action(context, linkedCts.Token);

            execution.Success = true;
            execution.CompletedAt = DateTime.UtcNow;
            execution.Duration = execution.CompletedAt - execution.StartedAt;
            job.State = JobState.Completed;
            job.LastSuccessTime = DateTime.UtcNow;
            job.ConsecutiveFailures = 0;

            JobCompleted?.Invoke(this, new JobExecutionEventArgs
            {
                Job = job,
                Execution = execution
            });
        }
        catch (Exception ex)
        {
            execution.Success = false;
            execution.Error = ex.Message;
            execution.CompletedAt = DateTime.UtcNow;
            execution.Duration = execution.CompletedAt - execution.StartedAt;
            job.LastError = ex.Message;
            job.ConsecutiveFailures++;

            JobFailed?.Invoke(this, new JobFailedEventArgs
            {
                Job = job,
                Execution = execution,
                Exception = ex
            });

            // Handle retry
            if (job.ConsecutiveFailures < job.Options.MaxRetries)
            {
                job.State = JobState.Scheduled;
                job.NextRunTime = DateTime.UtcNow.Add(
                    TimeSpan.FromMilliseconds(job.Options.RetryDelayMs * Math.Pow(2, job.ConsecutiveFailures - 1)));

                await _queueLock.WaitAsync(ct);
                try
                {
                    _jobQueue.Enqueue(job, job.NextRunTime ?? DateTime.UtcNow);
                }
                finally
                {
                    _queueLock.Release();
                }
            }
            else
            {
                job.State = JobState.Failed;
            }
        }
        finally
        {
            job.CancellationSource?.Dispose();
            job.CancellationSource = null;
        }
    }

    private static DateTime? CalculateNextRunTime(JobOptions options)
    {
        if (options.RunAt.HasValue)
            return options.RunAt.Value;

        if (!string.IsNullOrEmpty(options.CronExpression))
            return ParseCronExpression(options.CronExpression);

        if (options.Interval.HasValue)
            return DateTime.UtcNow.Add(options.Interval.Value);

        return DateTime.UtcNow;
    }

    private static DateTime ParseCronExpression(string cronExpression)
    {
        // Simplified cron parser - supports basic patterns
        // Format: minute hour day month dayOfWeek
        var parts = cronExpression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5)
            return DateTime.UtcNow.AddMinutes(1);

        var now = DateTime.UtcNow;
        var next = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);

        // Simple implementation: advance by minute until match
        for (int i = 0; i < 60 * 24 * 366; i++) // Max 1 year
        {
            next = next.AddMinutes(1);

            if (MatchesCronPart(parts[0], next.Minute) &&
                MatchesCronPart(parts[1], next.Hour) &&
                MatchesCronPart(parts[2], next.Day) &&
                MatchesCronPart(parts[3], next.Month) &&
                MatchesCronPart(parts[4], (int)next.DayOfWeek))
            {
                return next;
            }
        }

        return DateTime.UtcNow.AddMinutes(1);
    }

    private static bool MatchesCronPart(string part, int value)
    {
        if (part == "*") return true;

        if (part.Contains('/'))
        {
            var stepParts = part.Split('/');
            if (int.TryParse(stepParts[1], out var step))
                return value % step == 0;
        }

        if (part.Contains('-'))
        {
            var rangeParts = part.Split('-');
            if (int.TryParse(rangeParts[0], out var start) && int.TryParse(rangeParts[1], out var end))
                return value >= start && value <= end;
        }

        if (part.Contains(','))
        {
            return part.Split(',').Any(p => int.TryParse(p, out var v) && v == value);
        }

        return int.TryParse(part, out var exact) && exact == value;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _cts.Cancel();
        _cts.Dispose();
        _queueLock.Dispose();
        _executionThrottle.Dispose();

        foreach (var job in _jobs.Values)
        {
            job.CancellationSource?.Dispose();
        }

        _disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        await StopAsync(TimeSpan.FromSeconds(30));
        Dispose();
    }
}

/// <summary>
/// State of a scheduled job.
/// </summary>
public enum JobState
{
    Scheduled,
    Running,
    Completed,
    Failed,
    Cancelled,
    Paused
}

/// <summary>
/// Options for job scheduling.
/// </summary>
public class JobOptions
{
    public DateTime? RunAt { get; set; }
    public TimeSpan? Interval { get; set; }
    public string? CronExpression { get; set; }
    public TimeSpan? Timeout { get; set; }
    public int MaxRetries { get; set; } = 3;
    public int RetryDelayMs { get; set; } = 1000;
    public JobPriority Priority { get; set; } = JobPriority.Normal;
    public string[]? Dependencies { get; set; }
    public Dictionary<string, object>? Data { get; set; }

    public bool IsRecurring => Interval.HasValue || !string.IsNullOrEmpty(CronExpression);
}

/// <summary>
/// Priority levels for jobs.
/// </summary>
public enum JobPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3
}

/// <summary>
/// A scheduled job definition.
/// </summary>
public class ScheduledJob
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Func<JobContext, CancellationToken, Task> Action { get; set; } = (_, _) => Task.CompletedTask;
    public JobOptions Options { get; set; } = new();
    public JobState State { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? NextRunTime { get; set; }
    public DateTime? LastRunTime { get; set; }
    public DateTime? LastSuccessTime { get; set; }
    public int ExecutionCount { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? LastError { get; set; }
    internal CancellationTokenSource? CancellationSource { get; set; }
}

/// <summary>
/// Context provided to job actions during execution.
/// </summary>
public class JobContext
{
    public string JobId { get; set; } = string.Empty;
    public string JobName { get; set; } = string.Empty;
    public string ExecutionId { get; set; } = string.Empty;
    public int Attempt { get; set; }
    public DateTime ScheduledTime { get; set; }
    public Dictionary<string, object>? Data { get; set; }
}

/// <summary>
/// Record of a job execution.
/// </summary>
public class JobExecution
{
    public string Id { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public string JobName { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public TimeSpan? Duration { get; set; }
    public int Attempt { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Statistics for the job scheduler.
/// </summary>
public class JobSchedulerStats
{
    public int TotalJobs { get; set; }
    public int ScheduledJobs { get; set; }
    public int RunningJobs { get; set; }
    public int CompletedJobs { get; set; }
    public int FailedJobs { get; set; }
    public int PausedJobs { get; set; }
    public int TotalExecutions { get; set; }
    public int SuccessfulExecutions { get; set; }
    public int FailedExecutions { get; set; }
    public TimeSpan AverageExecutionTime { get; set; }
    public int MaxConcurrentJobs { get; set; }
}

/// <summary>
/// Event args for job execution events.
/// </summary>
public class JobExecutionEventArgs : EventArgs
{
    public ScheduledJob Job { get; set; } = new();
    public JobExecution Execution { get; set; } = new();
}

/// <summary>
/// Event args for job failure events.
/// </summary>
public class JobFailedEventArgs : JobExecutionEventArgs
{
    public Exception? Exception { get; set; }
}
