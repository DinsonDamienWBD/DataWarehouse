using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataProtection.Scheduler
{
    /// <summary>
    /// Backup scheduling engine supporting cron-like scheduling, dependencies, and resource awareness.
    /// </summary>
    public sealed class BackupScheduler : IAsyncDisposable
    {
        private readonly BoundedDictionary<string, ScheduledBackupJob> _jobs = new BoundedDictionary<string, ScheduledBackupJob>(1000);
        private readonly BoundedDictionary<string, Timer> _timers = new BoundedDictionary<string, Timer>(1000);
        private readonly SemaphoreSlim _concurrencyLimiter;
        private bool _disposed;

        /// <summary>
        /// Event raised when a scheduled job starts.
        /// </summary>
        public event EventHandler<ScheduledBackupJob>? JobStarted;

        /// <summary>
        /// Event raised when a scheduled job completes.
        /// </summary>
        public event EventHandler<ScheduledJobResult>? JobCompleted;

        /// <summary>
        /// Creates a new backup scheduler.
        /// </summary>
        /// <param name="maxConcurrentJobs">Maximum concurrent backup jobs.</param>
        public BackupScheduler(int maxConcurrentJobs = 3)
        {
            _concurrencyLimiter = new SemaphoreSlim(maxConcurrentJobs, maxConcurrentJobs);
        }

        /// <summary>
        /// Schedules a backup job.
        /// </summary>
        /// <param name="job">The job to schedule.</param>
        public void Schedule(ScheduledBackupJob job)
        {
            ArgumentNullException.ThrowIfNull(job);

            _jobs[job.JobId] = job;

            if (job.Schedule != null && job.IsEnabled)
            {
                var nextRun = CalculateNextRun(job.Schedule);
                if (nextRun.HasValue)
                {
                    ScheduleTimer(job.JobId, nextRun.Value);
                }
            }
        }

        /// <summary>
        /// Removes a scheduled job.
        /// </summary>
        /// <param name="jobId">The job ID to remove.</param>
        public bool Unschedule(string jobId)
        {
            if (_timers.TryRemove(jobId, out var timer))
            {
                timer.Dispose();
            }
            return _jobs.TryRemove(jobId, out _);
        }

        /// <summary>
        /// Gets a scheduled job by ID.
        /// </summary>
        public ScheduledBackupJob? GetJob(string jobId)
        {
            _jobs.TryGetValue(jobId, out var job);
            return job;
        }

        /// <summary>
        /// Gets all scheduled jobs.
        /// </summary>
        public IEnumerable<ScheduledBackupJob> GetAllJobs()
        {
            return _jobs.Values;
        }

        /// <summary>
        /// Enables or disables a job.
        /// </summary>
        public void SetEnabled(string jobId, bool enabled)
        {
            if (_jobs.TryGetValue(jobId, out var job))
            {
                _jobs[jobId] = job with { IsEnabled = enabled };

                if (enabled && job.Schedule != null)
                {
                    var nextRun = CalculateNextRun(job.Schedule);
                    if (nextRun.HasValue)
                    {
                        ScheduleTimer(jobId, nextRun.Value);
                    }
                }
                else if (!enabled && _timers.TryRemove(jobId, out var timer))
                {
                    timer.Dispose();
                }
            }
        }

        /// <summary>
        /// Runs a job immediately, bypassing the schedule.
        /// </summary>
        /// <param name="jobId">The job ID to run.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task<ScheduledJobResult> RunNowAsync(string jobId, CancellationToken ct = default)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
            {
                return new ScheduledJobResult
                {
                    JobId = jobId,
                    Success = false,
                    Error = "Job not found"
                };
            }

            return await ExecuteJobAsync(job, ct);
        }

        private void ScheduleTimer(string jobId, DateTimeOffset nextRun)
        {
            var delay = nextRun - DateTimeOffset.UtcNow;
            if (delay < TimeSpan.Zero)
                delay = TimeSpan.Zero;

            if (_timers.TryRemove(jobId, out var existingTimer))
            {
                existingTimer.Dispose();
            }

            var timer = new Timer(
                async _ => { try { await OnTimerElapsedAsync(jobId); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Timer callback failed: {ex.Message}"); } },
                null,
                delay,
                Timeout.InfiniteTimeSpan
            );

            _timers[jobId] = timer;
        }

        private async Task OnTimerElapsedAsync(string jobId)
        {
            if (!_jobs.TryGetValue(jobId, out var job) || !job.IsEnabled)
                return;

            // Check dependencies
            if (job.DependsOn != null && job.DependsOn.Count > 0)
            {
                var dependenciesSatisfied = job.DependsOn.All(depId =>
                    _jobs.TryGetValue(depId, out var dep) &&
                    dep.LastResult?.Success == true &&
                    dep.LastRunTime.HasValue);

                if (!dependenciesSatisfied)
                {
                    // Reschedule for later
                    ScheduleTimer(jobId, DateTimeOffset.UtcNow.AddMinutes(5));
                    return;
                }
            }

            await ExecuteJobAsync(job, CancellationToken.None);

            // Schedule next run
            if (job.Schedule != null)
            {
                var nextRun = CalculateNextRun(job.Schedule);
                if (nextRun.HasValue)
                {
                    ScheduleTimer(jobId, nextRun.Value);
                }
            }
        }

        private async Task<ScheduledJobResult> ExecuteJobAsync(ScheduledBackupJob job, CancellationToken ct)
        {
            var result = new ScheduledJobResult
            {
                JobId = job.JobId,
                StartTime = DateTimeOffset.UtcNow
            };

            try
            {
                // Wait for concurrency slot
                await _concurrencyLimiter.WaitAsync(ct);
                try
                {
                    JobStarted?.Invoke(this, job);

                    // Execute the job's action
                    if (job.Action != null)
                    {
                        await job.Action(ct);
                        result.Success = true;
                    }
                    else
                    {
                        result.Success = false;
                        result.Error = "No action defined";
                    }
                }
                finally
                {
                    _concurrencyLimiter.Release();
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }

            result.EndTime = DateTimeOffset.UtcNow;

            // Update job state
            if (_jobs.TryGetValue(job.JobId, out var currentJob))
            {
                _jobs[job.JobId] = currentJob with
                {
                    LastRunTime = result.StartTime,
                    LastResult = result
                };
            }

            JobCompleted?.Invoke(this, result);
            return result;
        }

        private static DateTimeOffset? CalculateNextRun(BackupSchedule schedule)
        {
            var now = DateTimeOffset.UtcNow;

            return schedule.Type switch
            {
                ScheduleType.Interval => now.Add(schedule.Interval ?? TimeSpan.FromHours(1)),
                ScheduleType.Daily => GetNextDaily(now, schedule.TimeOfDay ?? TimeSpan.FromHours(2)),
                ScheduleType.Weekly => GetNextWeekly(now, schedule.DayOfWeek ?? DayOfWeek.Sunday, schedule.TimeOfDay ?? TimeSpan.FromHours(2)),
                ScheduleType.Monthly => GetNextMonthly(now, schedule.DayOfMonth ?? 1, schedule.TimeOfDay ?? TimeSpan.FromHours(2)),
                ScheduleType.Cron => ParseCronNext(schedule.CronExpression ?? "0 2 * * *", now),
                _ => null
            };
        }

        private static DateTimeOffset GetNextDaily(DateTimeOffset now, TimeSpan timeOfDay)
        {
            var today = now.Date.Add(timeOfDay);
            if (today <= now.DateTime)
                today = today.AddDays(1);
            return new DateTimeOffset(today, now.Offset);
        }

        private static DateTimeOffset GetNextWeekly(DateTimeOffset now, DayOfWeek dayOfWeek, TimeSpan timeOfDay)
        {
            var daysUntil = ((int)dayOfWeek - (int)now.DayOfWeek + 7) % 7;
            if (daysUntil == 0 && now.TimeOfDay >= timeOfDay)
                daysUntil = 7;
            var next = now.Date.AddDays(daysUntil).Add(timeOfDay);
            return new DateTimeOffset(next, now.Offset);
        }

        private static DateTimeOffset GetNextMonthly(DateTimeOffset now, int dayOfMonth, TimeSpan timeOfDay)
        {
            var month = now.Month;
            var year = now.Year;
            var day = Math.Min(dayOfMonth, DateTime.DaysInMonth(year, month));
            var candidate = new DateTime(year, month, day).Add(timeOfDay);

            if (candidate <= now.DateTime)
            {
                month++;
                if (month > 12) { month = 1; year++; }
                day = Math.Min(dayOfMonth, DateTime.DaysInMonth(year, month));
                candidate = new DateTime(year, month, day).Add(timeOfDay);
            }

            return new DateTimeOffset(candidate, now.Offset);
        }

        private static DateTimeOffset? ParseCronNext(string cronExpression, DateTimeOffset now)
        {
            // Simplified cron parser - in production, use a library like Cronos
            // Format: minute hour dayOfMonth month dayOfWeek
            var parts = cronExpression.Split(' ');
            if (parts.Length < 5) return now.AddHours(1);

            try
            {
                var minute = parts[0] == "*" ? 0 : int.Parse(parts[0]);
                var hour = parts[1] == "*" ? 2 : int.Parse(parts[1]);
                var next = now.Date.AddHours(hour).AddMinutes(minute);
                if (next <= now.DateTime) next = next.AddDays(1);
                return new DateTimeOffset(next, now.Offset);
            }
            catch
            {
                return now.AddHours(1);
            }
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var timer in _timers.Values)
            {
                await timer.DisposeAsync();
            }
            _timers.Clear();

            _concurrencyLimiter.Dispose();
        }
    }

    /// <summary>
    /// A scheduled backup job.
    /// </summary>
    public sealed record ScheduledBackupJob
    {
        /// <summary>Unique job identifier.</summary>
        public required string JobId { get; init; }

        /// <summary>Job name.</summary>
        public required string Name { get; init; }

        /// <summary>Strategy ID to use.</summary>
        public required string StrategyId { get; init; }

        /// <summary>Backup request parameters.</summary>
        public BackupRequest? Request { get; init; }

        /// <summary>Schedule configuration.</summary>
        public BackupSchedule? Schedule { get; init; }

        /// <summary>Whether the job is enabled.</summary>
        public bool IsEnabled { get; init; } = true;

        /// <summary>Job IDs this job depends on.</summary>
        public IReadOnlyList<string>? DependsOn { get; init; }

        /// <summary>Priority (lower = higher priority).</summary>
        public int Priority { get; init; } = 100;

        /// <summary>Last run time.</summary>
        public DateTimeOffset? LastRunTime { get; init; }

        /// <summary>Next scheduled run time.</summary>
        public DateTimeOffset? NextRunTime { get; init; }

        /// <summary>Last execution result.</summary>
        public ScheduledJobResult? LastResult { get; init; }

        /// <summary>Action to execute.</summary>
        public Func<CancellationToken, Task>? Action { get; init; }
    }

    /// <summary>
    /// Backup schedule configuration.
    /// </summary>
    public sealed record BackupSchedule
    {
        /// <summary>Type of schedule.</summary>
        public required ScheduleType Type { get; init; }

        /// <summary>Interval for interval-based schedules.</summary>
        public TimeSpan? Interval { get; init; }

        /// <summary>Time of day for daily/weekly/monthly schedules.</summary>
        public TimeSpan? TimeOfDay { get; init; }

        /// <summary>Day of week for weekly schedules.</summary>
        public DayOfWeek? DayOfWeek { get; init; }

        /// <summary>Day of month for monthly schedules.</summary>
        public int? DayOfMonth { get; init; }

        /// <summary>Cron expression for cron schedules.</summary>
        public string? CronExpression { get; init; }
    }

    /// <summary>
    /// Schedule type.
    /// </summary>
    public enum ScheduleType
    {
        /// <summary>Run at fixed intervals.</summary>
        Interval,

        /// <summary>Run daily at a specific time.</summary>
        Daily,

        /// <summary>Run weekly on a specific day.</summary>
        Weekly,

        /// <summary>Run monthly on a specific day.</summary>
        Monthly,

        /// <summary>Run based on cron expression.</summary>
        Cron
    }

    /// <summary>
    /// Result of a scheduled job execution.
    /// </summary>
    public sealed record ScheduledJobResult
    {
        /// <summary>Job ID.</summary>
        public required string JobId { get; init; }

        /// <summary>Whether execution succeeded.</summary>
        public bool Success { get; set; }

        /// <summary>Error message if failed.</summary>
        public string? Error { get; set; }

        /// <summary>Start time.</summary>
        public DateTimeOffset StartTime { get; set; }

        /// <summary>End time.</summary>
        public DateTimeOffset EndTime { get; set; }

        /// <summary>Execution duration.</summary>
        public TimeSpan Duration => EndTime - StartTime;
    }
}
