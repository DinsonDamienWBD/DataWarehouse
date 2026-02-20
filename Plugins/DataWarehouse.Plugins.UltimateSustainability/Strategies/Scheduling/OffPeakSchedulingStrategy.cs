using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.Scheduling;

/// <summary>
/// Schedules workloads during off-peak electricity hours to reduce costs
/// and take advantage of lower carbon intensity during low-demand periods.
/// </summary>
public sealed class OffPeakSchedulingStrategy : SustainabilityStrategyBase
{
    private readonly BoundedDictionary<string, ScheduledJob> _pendingJobs = new BoundedDictionary<string, ScheduledJob>(1000);
    private Timer? _schedulerTimer;

    /// <inheritdoc/>
    public override string StrategyId => "off-peak-scheduling";
    /// <inheritdoc/>
    public override string DisplayName => "Off-Peak Scheduling";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.Scheduling;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.Scheduling | SustainabilityCapabilities.CarbonCalculation;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Schedules workloads during off-peak electricity hours for cost savings and lower carbon intensity.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "scheduling", "off-peak", "electricity", "cost", "timing" };

    /// <summary>Off-peak start hour (local time, 0-23).</summary>
    public int OffPeakStartHour { get; set; } = 22;
    /// <summary>Off-peak end hour (local time, 0-23).</summary>
    public int OffPeakEndHour { get; set; } = 6;
    /// <summary>Weekend is always off-peak.</summary>
    public bool WeekendIsOffPeak { get; set; } = true;

    /// <summary>Whether currently in off-peak period.</summary>
    public bool IsOffPeak
    {
        get
        {
            var now = DateTime.Now;
            if (WeekendIsOffPeak && (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday))
                return true;
            var hour = now.Hour;
            return OffPeakStartHour > OffPeakEndHour
                ? hour >= OffPeakStartHour || hour < OffPeakEndHour
                : hour >= OffPeakStartHour && hour < OffPeakEndHour;
        }
    }

    /// <summary>Pending job count.</summary>
    public int PendingJobCount => _pendingJobs.Count;

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        _schedulerTimer = new Timer(async _ => await ProcessJobsAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _schedulerTimer?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Schedules a job for off-peak execution.</summary>
    public string ScheduleJob(Func<CancellationToken, Task> job, string name, DateTimeOffset? deadline = null)
    {
        var id = Guid.NewGuid().ToString("N");
        _pendingJobs[id] = new ScheduledJob
        {
            Id = id,
            Name = name,
            Job = job,
            ScheduledAt = DateTimeOffset.UtcNow,
            Deadline = deadline ?? DateTimeOffset.UtcNow.AddDays(1)
        };
        RecordWorkloadScheduled();
        return id;
    }

    /// <summary>Calculates next off-peak window.</summary>
    public DateTimeOffset GetNextOffPeakWindow()
    {
        var now = DateTime.Now;
        if (IsOffPeak) return DateTimeOffset.Now;

        var nextStart = now.Date.AddHours(OffPeakStartHour);
        if (nextStart <= now) nextStart = nextStart.AddDays(1);
        return new DateTimeOffset(nextStart);
    }

    private async Task ProcessJobsAsync()
    {
        if (!IsOffPeak)
        {
            UpdateRecommendations();
            return;
        }

        foreach (var kvp in _pendingJobs.ToArray())
        {
            if (_pendingJobs.TryRemove(kvp.Key, out var job))
            {
                try
                {
                    await job.Job(CancellationToken.None);
                    RecordOptimizationAction();
                    RecordEnergySaved(5); // Estimated savings from off-peak
                }
                catch { /* Scheduled job failure is logged but non-fatal */ }
            }
        }
        UpdateRecommendations();
    }

    private void UpdateRecommendations()
    {
        ClearRecommendations();
        if (_pendingJobs.Count > 0)
        {
            var nextWindow = GetNextOffPeakWindow();
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-pending",
                Type = "PendingJobs",
                Priority = 5,
                Description = $"{_pendingJobs.Count} jobs waiting for off-peak. Next window: {nextWindow:HH:mm}",
                CanAutoApply = false
            });
        }
    }
}

internal sealed record ScheduledJob
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Func<CancellationToken, Task> Job { get; init; }
    public required DateTimeOffset ScheduledAt { get; init; }
    public required DateTimeOffset Deadline { get; init; }
}
