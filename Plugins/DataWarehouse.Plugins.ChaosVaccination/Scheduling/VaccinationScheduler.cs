using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.ChaosVaccination;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.ChaosVaccination.Scheduling;

/// <summary>
/// Vaccination scheduler that runs chaos experiments on a configurable cadence.
///
/// Supports two scheduling modes:
/// - Cron-based: Uses standard 5-field cron expressions via <see cref="CronParser"/>
/// - Interval-based: Runs at fixed intervals (in milliseconds)
///
/// Features:
/// - Time window constraints (day-of-week + hour range + timezone)
/// - Weighted random experiment selection across scheduled experiments
/// - Concurrent experiment limits per schedule
/// - Background 1-minute tick timer for schedule evaluation
/// - Full IDisposable/IAsyncDisposable cleanup
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 61: Vaccination scheduler")]
public sealed class VaccinationScheduler : IVaccinationScheduler, IDisposable, IAsyncDisposable
{
    private readonly IChaosInjectionEngine _engine;
    private readonly IMessageBus? _messageBus;
    private readonly ConcurrentDictionary<string, VaccinationSchedule> _schedules = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRunTimes = new();
    private readonly ConcurrentDictionary<string, int> _runningCounts = new();
    private readonly Timer _tickTimer;
    private readonly Random _random = new();
    private readonly object _tickLock = new();
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="VaccinationScheduler"/>.
    /// </summary>
    /// <param name="engine">The chaos injection engine used to execute experiments.</param>
    /// <param name="messageBus">Optional message bus for schedule event notifications.</param>
    public VaccinationScheduler(IChaosInjectionEngine engine, IMessageBus? messageBus = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _messageBus = messageBus;

        // 1-minute tick interval for schedule evaluation
        _tickTimer = new Timer(OnTick, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <inheritdoc/>
    public async Task AddScheduleAsync(VaccinationSchedule schedule, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(schedule);

        if (string.IsNullOrWhiteSpace(schedule.Id))
            throw new ArgumentException("Schedule ID cannot be null or empty.", nameof(schedule));

        if (schedule.Experiments.Length == 0)
            throw new ArgumentException("Schedule must contain at least one experiment.", nameof(schedule));

        // Validate cron expression if specified
        if (!string.IsNullOrWhiteSpace(schedule.CronExpression))
        {
            try
            {
                CronParser.Parse(schedule.CronExpression);
            }
            catch (FormatException ex)
            {
                throw new ArgumentException($"Invalid cron expression: {ex.Message}", nameof(schedule), ex);
            }
        }

        // Must have either cron or interval
        if (string.IsNullOrWhiteSpace(schedule.CronExpression) && !schedule.IntervalMs.HasValue)
            throw new ArgumentException("Schedule must specify either a CronExpression or IntervalMs.", nameof(schedule));

        if (!_schedules.TryAdd(schedule.Id, schedule))
            throw new InvalidOperationException($"Schedule with ID '{schedule.Id}' already exists.");

        _runningCounts.TryAdd(schedule.Id, 0);

        if (_messageBus != null)
        {
            await _messageBus.PublishAsync("chaos.schedule.added", new PluginMessage
            {
                Type = "chaos.schedule.added",
                SourcePluginId = "com.datawarehouse.chaos.vaccination",
                Payload = new Dictionary<string, object>
                {
                    ["scheduleId"] = schedule.Id,
                    ["scheduleName"] = schedule.Name,
                    ["enabled"] = schedule.Enabled,
                    ["experimentCount"] = schedule.Experiments.Length
                }
            }, ct);
        }
    }

    /// <inheritdoc/>
    public async Task RemoveScheduleAsync(string scheduleId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_schedules.TryRemove(scheduleId, out _))
            throw new KeyNotFoundException($"Schedule with ID '{scheduleId}' not found.");

        _lastRunTimes.TryRemove(scheduleId, out _);
        _runningCounts.TryRemove(scheduleId, out _);

        if (_messageBus != null)
        {
            await _messageBus.PublishAsync("chaos.schedule.removed", new PluginMessage
            {
                Type = "chaos.schedule.removed",
                SourcePluginId = "com.datawarehouse.chaos.vaccination",
                Payload = new Dictionary<string, object>
                {
                    ["scheduleId"] = scheduleId
                }
            }, ct);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<VaccinationSchedule>> GetSchedulesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<VaccinationSchedule>>(_schedules.Values.ToList());
    }

    /// <inheritdoc/>
    public Task<VaccinationSchedule?> GetScheduleAsync(string scheduleId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _schedules.TryGetValue(scheduleId, out var schedule);
        return Task.FromResult(schedule);
    }

    /// <inheritdoc/>
    public async Task EnableScheduleAsync(string scheduleId, bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_schedules.TryGetValue(scheduleId, out var existing))
            throw new KeyNotFoundException($"Schedule with ID '{scheduleId}' not found.");

        // Records are immutable -- create new with toggled Enabled flag
        var updated = existing with { Enabled = enabled };
        _schedules[scheduleId] = updated;

        if (_messageBus != null)
        {
            await _messageBus.PublishAsync($"chaos.schedule.{(enabled ? "enabled" : "disabled")}", new PluginMessage
            {
                Type = $"chaos.schedule.{(enabled ? "enabled" : "disabled")}",
                SourcePluginId = "com.datawarehouse.chaos.vaccination",
                Payload = new Dictionary<string, object>
                {
                    ["scheduleId"] = scheduleId,
                    ["enabled"] = enabled
                }
            }, ct);
        }
    }

    /// <inheritdoc/>
    public Task<DateTimeOffset?> GetNextRunTimeAsync(string scheduleId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_schedules.TryGetValue(scheduleId, out var schedule))
            return Task.FromResult<DateTimeOffset?>(null);

        if (!schedule.Enabled)
            return Task.FromResult<DateTimeOffset?>(null);

        var now = DateTimeOffset.UtcNow;

        if (!string.IsNullOrWhiteSpace(schedule.CronExpression))
        {
            var cronSchedule = CronParser.Parse(schedule.CronExpression);
            var candidate = cronSchedule.GetNextOccurrence(now);

            // If time windows are specified, advance until we find a match within a window
            if (candidate.HasValue && schedule.TimeWindows is { Length: > 0 })
            {
                var maxSearch = now.AddYears(1);
                while (candidate.HasValue && candidate.Value <= maxSearch)
                {
                    if (IsWithinTimeWindow(candidate.Value, schedule.TimeWindows))
                        return Task.FromResult(candidate);

                    candidate = cronSchedule.GetNextOccurrence(candidate.Value);
                }
                return Task.FromResult<DateTimeOffset?>(null);
            }

            return Task.FromResult(candidate);
        }

        if (schedule.IntervalMs.HasValue)
        {
            DateTimeOffset nextRun;
            if (_lastRunTimes.TryGetValue(scheduleId, out var lastRun))
            {
                nextRun = lastRun.AddMilliseconds(schedule.IntervalMs.Value);
                if (nextRun <= now)
                    nextRun = now; // Overdue -- next tick will trigger it
            }
            else
            {
                nextRun = now; // Never run -- eligible immediately
            }

            // If time windows specified, advance to next valid window if needed
            if (schedule.TimeWindows is { Length: > 0 } && !IsWithinTimeWindow(nextRun, schedule.TimeWindows))
            {
                nextRun = FindNextTimeWindowStart(nextRun, schedule.TimeWindows);
            }

            return Task.FromResult<DateTimeOffset?>(nextRun);
        }

        return Task.FromResult<DateTimeOffset?>(null);
    }

    /// <summary>
    /// Timer callback -- evaluates all schedules on each tick (every 1 minute).
    /// </summary>
    private void OnTick(object? state)
    {
        if (_disposed) return;

        // Prevent overlapping ticks
        if (!Monitor.TryEnter(_tickLock))
            return;

        try
        {
            var now = DateTimeOffset.UtcNow;

            foreach (var kvp in _schedules)
            {
                var scheduleId = kvp.Key;
                var schedule = kvp.Value;

                if (!schedule.Enabled)
                    continue;

                // Check time window constraints
                if (schedule.TimeWindows is { Length: > 0 } && !IsWithinTimeWindow(now, schedule.TimeWindows))
                    continue;

                // Check concurrent experiment limit
                var currentRunning = _runningCounts.GetOrAdd(scheduleId, 0);
                if (currentRunning >= schedule.MaxConcurrent)
                    continue;

                bool isDue = false;

                // Check cron-based schedule
                if (!string.IsNullOrWhiteSpace(schedule.CronExpression))
                {
                    try
                    {
                        var cronSchedule = CronParser.Parse(schedule.CronExpression);
                        // Match on current minute (truncated to minute boundary)
                        var truncatedNow = new DateTimeOffset(
                            now.Year, now.Month, now.Day,
                            now.Hour, now.Minute, 0, now.Offset);
                        isDue = cronSchedule.Matches(truncatedNow);

                        // Avoid double-execution within the same minute
                        if (isDue && _lastRunTimes.TryGetValue(scheduleId, out var lastRun))
                        {
                            var lastRunMinute = new DateTimeOffset(
                                lastRun.Year, lastRun.Month, lastRun.Day,
                                lastRun.Hour, lastRun.Minute, 0, lastRun.Offset);
                            if (lastRunMinute == truncatedNow)
                                isDue = false;
                        }
                    }
                    catch (FormatException)
                    {
                        // Invalid cron -- skip this schedule
                        continue;
                    }
                }
                // Check interval-based schedule
                else if (schedule.IntervalMs.HasValue)
                {
                    if (_lastRunTimes.TryGetValue(scheduleId, out var lastRun))
                    {
                        isDue = (now - lastRun).TotalMilliseconds >= schedule.IntervalMs.Value;
                    }
                    else
                    {
                        isDue = true; // Never run before -- execute now
                    }
                }

                if (!isDue)
                    continue;

                // Select experiment using weighted random selection
                var selectedExperiment = SelectWeightedExperiment(schedule.Experiments);
                if (selectedExperiment == null)
                    continue;

                // Track last run time before firing
                _lastRunTimes[scheduleId] = now;

                // Increment running count
                _runningCounts.AddOrUpdate(scheduleId, 1, (_, c) => c + 1);

                // Fire-and-forget with error handling
                var capturedScheduleId = scheduleId;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _engine.ExecuteExperimentAsync(selectedExperiment.Experiment);
                    }
                    catch (Exception)
                    {
                        // Swallow -- scheduled experiments must not crash the scheduler
                    }
                    finally
                    {
                        _runningCounts.AddOrUpdate(capturedScheduleId, 0, (_, c) => Math.Max(0, c - 1));
                    }
                });
            }
        }
        finally
        {
            Monitor.Exit(_tickLock);
        }
    }

    /// <summary>
    /// Selects an experiment from the array using weighted random selection.
    /// Higher weight = higher probability of selection.
    /// </summary>
    private ScheduledExperiment? SelectWeightedExperiment(ScheduledExperiment[] experiments)
    {
        if (experiments.Length == 0)
            return null;

        if (experiments.Length == 1)
            return experiments[0];

        var totalWeight = experiments.Sum(e => e.Weight);
        if (totalWeight <= 0)
            return experiments[0]; // Fallback to first if all weights are zero/negative

        double roll;
        lock (_random)
        {
            roll = _random.NextDouble() * totalWeight;
        }

        double cumulative = 0;
        foreach (var experiment in experiments)
        {
            cumulative += experiment.Weight;
            if (roll <= cumulative)
                return experiment;
        }

        return experiments[^1]; // Fallback due to floating-point rounding
    }

    /// <summary>
    /// Checks whether the given time falls within at least one of the specified time windows.
    /// </summary>
    private static bool IsWithinTimeWindow(DateTimeOffset time, TimeWindow[] windows)
    {
        foreach (var window in windows)
        {
            // Convert time to the window's timezone for comparison
            TimeZoneInfo tz;
            try
            {
                tz = TimeZoneInfo.FindSystemTimeZoneById(window.TimeZone);
            }
            catch (TimeZoneNotFoundException)
            {
                // Invalid timezone -- skip this window
                continue;
            }

            var localTime = TimeZoneInfo.ConvertTime(time, tz);

            // Check day-of-week constraint (null means any day)
            if (window.DayOfWeek.HasValue && localTime.DayOfWeek != window.DayOfWeek.Value)
                continue;

            // Check hour range
            if (window.StartHour <= window.EndHour)
            {
                // Normal range: e.g., 9-17
                if (localTime.Hour >= window.StartHour && localTime.Hour < window.EndHour)
                    return true;
            }
            else
            {
                // Overnight range: e.g., 22-6 (10 PM to 6 AM)
                if (localTime.Hour >= window.StartHour || localTime.Hour < window.EndHour)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the next time that falls within any of the specified time windows.
    /// </summary>
    private static DateTimeOffset FindNextTimeWindowStart(DateTimeOffset from, TimeWindow[] windows)
    {
        // Simple forward scan -- check each hour for up to 7 days
        var candidate = new DateTimeOffset(
            from.Year, from.Month, from.Day,
            from.Hour, 0, 0, from.Offset);

        var maxSearch = from.AddDays(7);
        while (candidate <= maxSearch)
        {
            if (IsWithinTimeWindow(candidate, windows))
                return candidate;

            candidate = candidate.AddHours(1);
        }

        // No window found within 7 days -- return original
        return from;
    }

    /// <summary>
    /// Stops the background timer and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _tickTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _tickTimer.Dispose();
    }

    /// <summary>
    /// Asynchronously stops the background timer and releases resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _tickTimer.Change(Timeout.Infinite, Timeout.Infinite);
        await _tickTimer.DisposeAsync();
    }
}
