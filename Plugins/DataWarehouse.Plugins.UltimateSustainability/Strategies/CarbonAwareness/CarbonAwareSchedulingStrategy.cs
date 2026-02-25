using System.Collections.Concurrent;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.CarbonAwareness;

/// <summary>
/// Schedules workloads to run during low-carbon periods.
/// Uses carbon intensity forecasts to shift flexible workloads to times
/// when the grid has higher renewable energy percentage.
/// </summary>
public sealed class CarbonAwareSchedulingStrategy : SustainabilityStrategyBase
{
    private readonly BoundedDictionary<string, ScheduledWorkload> _pendingWorkloads = new BoundedDictionary<string, ScheduledWorkload>(1000);
    private readonly ConcurrentQueue<CompletedWorkload> _completedWorkloads = new();
    private Timer? _schedulerTimer;
    private Func<Task<double>>? _carbonIntensityProvider;

    /// <inheritdoc/>
    public override string StrategyId => "carbon-aware-scheduling";

    /// <inheritdoc/>
    public override string DisplayName => "Carbon-Aware Scheduling";

    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.CarbonAwareness;

    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.Scheduling |
        SustainabilityCapabilities.CarbonCalculation |
        SustainabilityCapabilities.PredictiveAnalytics |
        SustainabilityCapabilities.IntelligenceAware;

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Schedules workloads during low-carbon periods using carbon intensity forecasts. " +
        "Shifts flexible workloads to optimize for renewable energy availability, " +
        "reducing carbon emissions without impacting delivery deadlines.";

    /// <inheritdoc/>
    public override string[] Tags => new[] { "carbon", "scheduling", "renewable", "optimization", "workload" };

    /// <summary>
    /// Maximum delay allowed for workloads (default: 24 hours).
    /// </summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Carbon intensity threshold below which workloads should run (gCO2e/kWh).
    /// </summary>
    public double CarbonThreshold { get; set; } = 200;

    /// <summary>
    /// Gets the number of pending workloads.
    /// </summary>
    public int PendingWorkloadCount => _pendingWorkloads.Count;

    /// <summary>
    /// Configures the carbon intensity data source.
    /// </summary>
    public void SetCarbonIntensityProvider(Func<Task<double>> provider)
    {
        _carbonIntensityProvider = provider;
    }

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        _schedulerTimer = new Timer(
            async _ => { try { await ProcessPendingWorkloadsAsync(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Timer callback failed: {ex.Message}"); } },
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _schedulerTimer?.Dispose();
        _schedulerTimer = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Schedules a workload for carbon-aware execution.
    /// </summary>
    public string ScheduleWorkload(
        Func<CancellationToken, Task> workload,
        string name,
        TimeSpan estimatedDuration,
        double estimatedEnergyWh,
        WorkloadPriority priority = WorkloadPriority.Normal,
        DateTimeOffset? deadline = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var scheduled = new ScheduledWorkload
        {
            Id = id,
            Name = name,
            Workload = workload,
            ScheduledAt = DateTimeOffset.UtcNow,
            EstimatedDuration = estimatedDuration,
            EstimatedEnergyWh = estimatedEnergyWh,
            Priority = priority,
            Deadline = deadline ?? DateTimeOffset.UtcNow.Add(MaxDelay)
        };

        _pendingWorkloads[id] = scheduled;
        RecordWorkloadScheduled();

        UpdateRecommendations();
        return id;
    }

    /// <summary>
    /// Cancels a scheduled workload.
    /// </summary>
    public bool CancelWorkload(string workloadId)
    {
        return _pendingWorkloads.TryRemove(workloadId, out _);
    }

    /// <summary>
    /// Gets all pending workloads.
    /// </summary>
    public IReadOnlyList<ScheduledWorkloadInfo> GetPendingWorkloads()
    {
        return _pendingWorkloads.Values
            .Select(w => new ScheduledWorkloadInfo
            {
                Id = w.Id,
                Name = w.Name,
                ScheduledAt = w.ScheduledAt,
                Deadline = w.Deadline,
                Priority = w.Priority,
                EstimatedEnergyWh = w.EstimatedEnergyWh
            })
            .OrderBy(w => w.Priority)
            .ThenBy(w => w.Deadline)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Gets recently completed workloads.
    /// </summary>
    public IReadOnlyList<CompletedWorkload> GetCompletedWorkloads(int count = 100)
    {
        return _completedWorkloads.TakeLast(count).Reverse().ToList().AsReadOnly();
    }

    /// <summary>
    /// Forces immediate execution of a workload regardless of carbon intensity.
    /// </summary>
    public async Task<WorkloadResult> ExecuteImmediatelyAsync(string workloadId, CancellationToken ct = default)
    {
        if (!_pendingWorkloads.TryRemove(workloadId, out var workload))
        {
            return new WorkloadResult
            {
                WorkloadId = workloadId,
                Success = false,
                ErrorMessage = "Workload not found"
            };
        }

        return await ExecuteWorkloadAsync(workload, await GetCurrentCarbonIntensityAsync(), ct);
    }

    private async Task ProcessPendingWorkloadsAsync()
    {
        if (_pendingWorkloads.IsEmpty) return;

        var currentIntensity = await GetCurrentCarbonIntensityAsync();
        var now = DateTimeOffset.UtcNow;

        foreach (var kvp in _pendingWorkloads.ToArray())
        {
            var workload = kvp.Value;

            // Execute if carbon intensity is low enough
            bool shouldExecute = currentIntensity <= CarbonThreshold;

            // Or if deadline is approaching
            if (!shouldExecute && now >= workload.Deadline.AddMinutes(-5))
            {
                shouldExecute = true;
            }

            // Or if high priority and intensity is reasonable
            if (!shouldExecute && workload.Priority == WorkloadPriority.High && currentIntensity <= CarbonThreshold * 1.5)
            {
                shouldExecute = true;
            }

            if (shouldExecute && _pendingWorkloads.TryRemove(kvp.Key, out _))
            {
                try
                {
                    var result = await ExecuteWorkloadAsync(workload, currentIntensity, CancellationToken.None);
                    RecordCarbonSavings(workload, currentIntensity);
                }
                catch
                {

                    // Log failure but continue processing
                    System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
                }
            }
        }

        UpdateRecommendations();
    }

    private async Task<WorkloadResult> ExecuteWorkloadAsync(ScheduledWorkload workload, double carbonIntensity, CancellationToken ct)
    {
        var startTime = DateTimeOffset.UtcNow;
        try
        {
            await workload.Workload(ct);

            var endTime = DateTimeOffset.UtcNow;
            var completed = new CompletedWorkload
            {
                Id = workload.Id,
                Name = workload.Name,
                ScheduledAt = workload.ScheduledAt,
                ExecutedAt = startTime,
                CompletedAt = endTime,
                CarbonIntensityAtExecution = carbonIntensity,
                EnergyConsumedWh = workload.EstimatedEnergyWh,
                CarbonEmittedGrams = (workload.EstimatedEnergyWh / 1000.0) * carbonIntensity,
                Success = true
            };

            _completedWorkloads.Enqueue(completed);
            while (_completedWorkloads.Count > 1000)
                _completedWorkloads.TryDequeue(out _);

            RecordOptimizationAction();

            return new WorkloadResult
            {
                WorkloadId = workload.Id,
                Success = true,
                ExecutedAt = startTime,
                Duration = endTime - startTime,
                CarbonIntensity = carbonIntensity,
                CarbonEmittedGrams = completed.CarbonEmittedGrams
            };
        }
        catch (Exception ex)
        {
            return new WorkloadResult
            {
                WorkloadId = workload.Id,
                Success = false,
                ExecutedAt = startTime,
                ErrorMessage = ex.Message
            };
        }
    }

    private async Task<double> GetCurrentCarbonIntensityAsync()
    {
        if (_carbonIntensityProvider != null)
        {
            try
            {
                return await _carbonIntensityProvider();
            }
            catch
            {

                // Fall through to default
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }
        }

        // Default estimation based on time of day
        var hour = DateTime.UtcNow.Hour;
        var baseIntensity = 350.0;
        var variation = hour >= 10 && hour <= 16 ? -0.2 : 0.15;
        return baseIntensity * (1 + variation);
    }

    private void RecordCarbonSavings(ScheduledWorkload workload, double actualIntensity)
    {
        // Compare to worst-case intensity
        var worstCaseIntensity = 500.0;
        var savingsGrams = (workload.EstimatedEnergyWh / 1000.0) * (worstCaseIntensity - actualIntensity);
        if (savingsGrams > 0)
        {
            RecordCarbonAvoided(savingsGrams);
        }
    }

    private void UpdateRecommendations()
    {
        ClearRecommendations();

        var pendingCount = _pendingWorkloads.Count;
        if (pendingCount > 0)
        {
            var urgentCount = _pendingWorkloads.Values.Count(w =>
                w.Deadline <= DateTimeOffset.UtcNow.AddHours(2));

            if (urgentCount > 0)
            {
                AddRecommendation(new SustainabilityRecommendation
                {
                    RecommendationId = $"{StrategyId}-urgent-workloads",
                    Type = "UrgentWorkloads",
                    Priority = 8,
                    Description = $"{urgentCount} workloads approaching deadline. May need to execute at current carbon intensity.",
                    CanAutoApply = true,
                    Action = "execute-urgent"
                });
            }

            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-pending-summary",
                Type = "PendingSummary",
                Priority = 5,
                Description = $"{pendingCount} workloads pending carbon-aware scheduling.",
                EstimatedEnergySavingsWh = _pendingWorkloads.Values.Sum(w => w.EstimatedEnergyWh) * 0.1,
                CanAutoApply = false
            });
        }
    }
}

/// <summary>
/// Priority level for scheduled workloads.
/// </summary>
public enum WorkloadPriority
{
    /// <summary>Low priority - can wait for optimal carbon window.</summary>
    Low = 0,
    /// <summary>Normal priority - balance between delay and carbon savings.</summary>
    Normal = 1,
    /// <summary>High priority - prefer sooner execution.</summary>
    High = 2,
    /// <summary>Critical - execute as soon as possible.</summary>
    Critical = 3
}

/// <summary>
/// A workload scheduled for carbon-aware execution.
/// </summary>
internal sealed class ScheduledWorkload
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Func<CancellationToken, Task> Workload { get; init; }
    public required DateTimeOffset ScheduledAt { get; init; }
    public required TimeSpan EstimatedDuration { get; init; }
    public required double EstimatedEnergyWh { get; init; }
    public required WorkloadPriority Priority { get; init; }
    public required DateTimeOffset Deadline { get; init; }
}

/// <summary>
/// Information about a scheduled workload.
/// </summary>
public sealed record ScheduledWorkloadInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset ScheduledAt { get; init; }
    public required DateTimeOffset Deadline { get; init; }
    public required WorkloadPriority Priority { get; init; }
    public required double EstimatedEnergyWh { get; init; }
}

/// <summary>
/// A completed workload with execution metrics.
/// </summary>
public sealed record CompletedWorkload
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset ScheduledAt { get; init; }
    public required DateTimeOffset ExecutedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required double CarbonIntensityAtExecution { get; init; }
    public required double EnergyConsumedWh { get; init; }
    public required double CarbonEmittedGrams { get; init; }
    public required bool Success { get; init; }
}

/// <summary>
/// Result of a workload execution.
/// </summary>
public sealed record WorkloadResult
{
    public required string WorkloadId { get; init; }
    public required bool Success { get; init; }
    public DateTimeOffset? ExecutedAt { get; init; }
    public TimeSpan? Duration { get; init; }
    public double? CarbonIntensity { get; init; }
    public double? CarbonEmittedGrams { get; init; }
    public string? ErrorMessage { get; init; }
}
