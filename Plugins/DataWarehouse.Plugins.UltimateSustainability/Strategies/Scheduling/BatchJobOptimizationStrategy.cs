namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.Scheduling;

/// <summary>
/// Optimizes batch job scheduling for energy efficiency.
/// Considers carbon intensity, electricity prices, and resource availability.
/// </summary>
public sealed class BatchJobOptimizationStrategy : SustainabilityStrategyBase
{
    private readonly List<BatchJob> _pendingJobs = new();
    private readonly List<BatchJobExecution> _executionHistory = new();
    private readonly object _lock = new();
    private Func<Task<double>>? _carbonIntensityProvider;
    private Func<Task<double>>? _electricityPriceProvider;

    /// <inheritdoc/>
    public override string StrategyId => "batch-job-optimization";
    /// <inheritdoc/>
    public override string DisplayName => "Batch Job Optimization";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.Scheduling;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.Scheduling | SustainabilityCapabilities.PredictiveAnalytics | SustainabilityCapabilities.CarbonCalculation;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Schedules batch jobs to minimize carbon footprint and energy costs based on forecasts.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "batch", "scheduling", "jobs", "carbon", "cost", "optimization" };

    /// <summary>Carbon intensity threshold for immediate execution (gCO2e/kWh).</summary>
    public double LowCarbonThreshold { get; set; } = 150;
    /// <summary>Maximum delay for batch jobs (hours).</summary>
    public int MaxDelayHours { get; set; } = 24;
    /// <summary>Weight for carbon in scheduling decisions (0-1).</summary>
    public double CarbonWeight { get; set; } = 0.7;
    /// <summary>Weight for cost in scheduling decisions (0-1).</summary>
    public double CostWeight { get; set; } = 0.3;

    /// <summary>Sets carbon intensity provider.</summary>
    public void SetCarbonIntensityProvider(Func<Task<double>> provider) => _carbonIntensityProvider = provider;
    /// <summary>Sets electricity price provider.</summary>
    public void SetElectricityPriceProvider(Func<Task<double>> provider) => _electricityPriceProvider = provider;

    /// <summary>Submits a batch job for scheduling.</summary>
    public string SubmitJob(string name, double estimatedKwh, TimeSpan maxDelay, int priority = 5)
    {
        var job = new BatchJob
        {
            JobId = Guid.NewGuid().ToString("N"),
            Name = name,
            EstimatedKwh = estimatedKwh,
            MaxDelay = maxDelay,
            Priority = priority,
            SubmittedAt = DateTimeOffset.UtcNow,
            Deadline = DateTimeOffset.UtcNow.Add(maxDelay)
        };

        lock (_lock)
        {
            _pendingJobs.Add(job);
        }

        RecordWorkloadScheduled();
        return job.JobId;
    }

    /// <summary>Gets the optimal execution time for a job.</summary>
    public async Task<ScheduleRecommendation> GetOptimalScheduleAsync(string jobId)
    {
        BatchJob? job;
        lock (_lock)
        {
            job = _pendingJobs.FirstOrDefault(j => j.JobId == jobId);
        }

        if (job == null)
            return new ScheduleRecommendation { JobId = jobId, Success = false, Reason = "Job not found" };

        var currentCarbon = _carbonIntensityProvider != null ? await _carbonIntensityProvider() : 400;
        var currentPrice = _electricityPriceProvider != null ? await _electricityPriceProvider() : 0.12;

        // If current conditions are good, execute now
        if (currentCarbon < LowCarbonThreshold)
        {
            return new ScheduleRecommendation
            {
                JobId = jobId,
                Success = true,
                RecommendedExecutionTime = DateTimeOffset.UtcNow,
                ExpectedCarbonIntensity = currentCarbon,
                ExpectedPrice = currentPrice,
                Reason = "Low carbon intensity - execute immediately"
            };
        }

        // Otherwise, suggest waiting (in real implementation, would use forecast)
        var bestTime = DateTimeOffset.UtcNow.AddHours(Math.Min(job.MaxDelay.TotalHours * 0.5, 4));
        return new ScheduleRecommendation
        {
            JobId = jobId,
            Success = true,
            RecommendedExecutionTime = bestTime,
            ExpectedCarbonIntensity = currentCarbon * 0.7, // Estimate
            ExpectedPrice = currentPrice * 0.8,
            Reason = $"Defer to {bestTime:HH:mm} for lower carbon intensity"
        };
    }

    /// <summary>Executes a job.</summary>
    public async Task<JobExecutionResult> ExecuteJobAsync(string jobId, CancellationToken ct = default)
    {
        BatchJob? job;
        lock (_lock)
        {
            job = _pendingJobs.FirstOrDefault(j => j.JobId == jobId);
            if (job != null) _pendingJobs.Remove(job);
        }

        if (job == null)
            return new JobExecutionResult { JobId = jobId, Success = false, Reason = "Job not found" };

        var carbonIntensity = _carbonIntensityProvider != null ? await _carbonIntensityProvider() : 400;
        var carbonEmissions = job.EstimatedKwh * carbonIntensity;

        var execution = new BatchJobExecution
        {
            JobId = job.JobId,
            JobName = job.Name,
            StartedAt = DateTimeOffset.UtcNow,
            CarbonIntensity = carbonIntensity,
            EstimatedEmissionsGrams = carbonEmissions
        };

        lock (_lock)
        {
            _executionHistory.Add(execution);
            if (_executionHistory.Count > 1000) _executionHistory.RemoveAt(0);
        }

        RecordOptimizationAction();

        return new JobExecutionResult
        {
            JobId = jobId,
            Success = true,
            StartedAt = execution.StartedAt,
            CarbonIntensity = carbonIntensity,
            EstimatedEmissionsGrams = carbonEmissions
        };
    }

    /// <summary>Gets pending jobs.</summary>
    public IReadOnlyList<BatchJob> GetPendingJobs()
    {
        lock (_lock) return _pendingJobs.ToList();
    }

    /// <summary>Gets execution statistics.</summary>
    public BatchJobStatistics GetJobStatistics()
    {
        lock (_lock)
        {
            return new BatchJobStatistics
            {
                TotalJobsExecuted = _executionHistory.Count,
                TotalEmissionsGrams = _executionHistory.Sum(e => e.EstimatedEmissionsGrams),
                AverageCarbonIntensity = _executionHistory.Any() ? _executionHistory.Average(e => e.CarbonIntensity) : 0,
                PendingJobCount = _pendingJobs.Count,
                PendingJobsKwh = _pendingJobs.Sum(j => j.EstimatedKwh)
            };
        }
    }
}

/// <summary>Batch job information.</summary>
public sealed class BatchJob
{
    public required string JobId { get; init; }
    public required string Name { get; init; }
    public required double EstimatedKwh { get; init; }
    public required TimeSpan MaxDelay { get; init; }
    public required int Priority { get; init; }
    public required DateTimeOffset SubmittedAt { get; init; }
    public required DateTimeOffset Deadline { get; init; }
}

/// <summary>Schedule recommendation.</summary>
public sealed record ScheduleRecommendation
{
    public required string JobId { get; init; }
    public bool Success { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset? RecommendedExecutionTime { get; init; }
    public double ExpectedCarbonIntensity { get; init; }
    public double ExpectedPrice { get; init; }
}

/// <summary>Job execution result.</summary>
public sealed record JobExecutionResult
{
    public required string JobId { get; init; }
    public bool Success { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public double CarbonIntensity { get; init; }
    public double EstimatedEmissionsGrams { get; init; }
}

/// <summary>Batch job execution record.</summary>
public sealed class BatchJobExecution
{
    public required string JobId { get; init; }
    public required string JobName { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required double CarbonIntensity { get; init; }
    public required double EstimatedEmissionsGrams { get; init; }
}

/// <summary>Batch job statistics.</summary>
public sealed record BatchJobStatistics
{
    public int TotalJobsExecuted { get; init; }
    public double TotalEmissionsGrams { get; init; }
    public double AverageCarbonIntensity { get; init; }
    public int PendingJobCount { get; init; }
    public double PendingJobsKwh { get; init; }
}
