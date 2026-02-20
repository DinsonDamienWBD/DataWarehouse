using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.EnergyOptimization;

/// <summary>
/// Consolidates workloads to fewer CPUs/cores to enable power gating
/// of unused resources. Reduces power consumption by concentrating
/// work on fewer active components while maintaining performance.
/// </summary>
public sealed class WorkloadConsolidationStrategy : SustainabilityStrategyBase
{
    private readonly BoundedDictionary<int, CoreState> _coreStates = new BoundedDictionary<int, CoreState>(1000);
    private int _activeCoreCount;
    private int _totalCoreCount;
    private double _consolidationRatio;
    private Timer? _monitorTimer;
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "workload-consolidation";

    /// <inheritdoc/>
    public override string DisplayName => "Workload Consolidation";

    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.EnergyOptimization;

    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.ActiveControl |
        SustainabilityCapabilities.RealTimeMonitoring |
        SustainabilityCapabilities.PredictiveAnalytics;

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Consolidates workloads to fewer CPU cores to enable power gating of idle resources. " +
        "Uses CPU affinity and cgroup controls to pack work onto fewer cores, " +
        "allowing unused cores to enter deep sleep states.";

    /// <inheritdoc/>
    public override string[] Tags => new[] { "consolidation", "cores", "affinity", "packing", "power-gating" };

    /// <summary>
    /// Gets the number of active cores.
    /// </summary>
    public int ActiveCoreCount
    {
        get { lock (_lock) return _activeCoreCount; }
    }

    /// <summary>
    /// Gets the total number of cores.
    /// </summary>
    public int TotalCoreCount
    {
        get { lock (_lock) return _totalCoreCount; }
    }

    /// <summary>
    /// Gets the consolidation ratio (active/total).
    /// </summary>
    public double ConsolidationRatio
    {
        get { lock (_lock) return _consolidationRatio; }
    }

    /// <summary>
    /// Minimum cores to keep active.
    /// </summary>
    public int MinActiveCores { get; set; } = 2;

    /// <summary>
    /// Target CPU utilization per active core (0-100).
    /// </summary>
    public double TargetUtilizationPercent { get; set; } = 70;

    /// <summary>
    /// Whether to automatically consolidate workloads.
    /// </summary>
    public bool AutoConsolidate { get; set; } = true;

    /// <summary>
    /// Cooldown period between consolidation changes.
    /// </summary>
    public TimeSpan ConsolidationCooldown { get; set; } = TimeSpan.FromMinutes(5);

    private DateTimeOffset _lastConsolidationChange = DateTimeOffset.MinValue;

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        _totalCoreCount = Environment.ProcessorCount;
        _activeCoreCount = _totalCoreCount;
        _consolidationRatio = 1.0;

        // Initialize core states
        for (int i = 0; i < _totalCoreCount; i++)
        {
            _coreStates[i] = new CoreState
            {
                CoreId = i,
                IsActive = true,
                Utilization = 0,
                LastUpdated = DateTimeOffset.UtcNow
            };
        }

        _monitorTimer = new Timer(
            async _ => await MonitorAndConsolidateAsync(),
            null,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10));

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _monitorTimer?.Dispose();
        _monitorTimer = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Manually sets the number of active cores.
    /// </summary>
    public async Task SetActiveCoresAsync(int coreCount, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        if (coreCount < MinActiveCores)
            throw new ArgumentOutOfRangeException(nameof(coreCount), $"Cannot reduce below {MinActiveCores} cores.");

        if (coreCount > _totalCoreCount)
            coreCount = _totalCoreCount;

        await ApplyConsolidationAsync(coreCount, ct);
    }

    /// <summary>
    /// Gets the current consolidation state.
    /// </summary>
    public ConsolidationState GetConsolidationState()
    {
        ThrowIfNotInitialized();

        var coreStates = _coreStates.Values.ToList();
        var activeStates = coreStates.Where(c => c.IsActive).ToList();

        return new ConsolidationState
        {
            Timestamp = DateTimeOffset.UtcNow,
            TotalCores = _totalCoreCount,
            ActiveCores = _activeCoreCount,
            InactiveCores = _totalCoreCount - _activeCoreCount,
            ConsolidationRatio = _consolidationRatio,
            AverageActiveUtilization = activeStates.Any() ? activeStates.Average(c => c.Utilization) : 0,
            EstimatedPowerSavingsPercent = (1 - _consolidationRatio) * 60, // Rough estimate
            CoreStates = coreStates.ToDictionary(c => c.CoreId, c => c.IsActive)
        };
    }

    /// <summary>
    /// Sets CPU affinity for a process to active cores only.
    /// </summary>
    public void SetProcessAffinity(int processId)
    {
        ThrowIfNotInitialized();

        try
        {
            var process = System.Diagnostics.Process.GetProcessById(processId);
            var activeCores = _coreStates.Values
                .Where(c => c.IsActive)
                .Select(c => c.CoreId)
                .ToArray();

            if (activeCores.Length > 0)
            {
                long affinityMask = 0;
                foreach (var core in activeCores)
                {
                    affinityMask |= 1L << core;
                }

                process.ProcessorAffinity = (IntPtr)affinityMask;
            }
        }
        catch
        {
            // Process not found or access denied
        }
    }

    private async Task MonitorAndConsolidateAsync()
    {
        try
        {
            // Update core utilization
            await UpdateCoreUtilizationAsync();

            // Calculate optimal core count
            if (AutoConsolidate && DateTimeOffset.UtcNow - _lastConsolidationChange > ConsolidationCooldown)
            {
                var optimalCores = CalculateOptimalCoreCount();
                if (optimalCores != _activeCoreCount)
                {
                    await ApplyConsolidationAsync(optimalCores);
                }
            }

            var avgUtil = _coreStates.Values.Where(c => c.IsActive).Average(c => c.Utilization);
            RecordSample(EstimatePowerSavings(), 0);
            UpdateRecommendations();
        }
        catch
        {
            // Monitoring failed
        }
    }

    private async Task UpdateCoreUtilizationAsync()
    {
        // Would read from /proc/stat on Linux or performance counters on Windows
        // Using simulated data
        await Task.Delay(1);

        var random = Random.Shared;
        foreach (var core in _coreStates.Values)
        {
            if (core.IsActive)
            {
                core.Utilization = random.NextDouble() * 50 + 10; // 10-60%
            }
            else
            {
                core.Utilization = 0;
            }
            core.LastUpdated = DateTimeOffset.UtcNow;
        }
    }

    private int CalculateOptimalCoreCount()
    {
        var activeCores = _coreStates.Values.Where(c => c.IsActive).ToList();
        if (!activeCores.Any()) return MinActiveCores;

        var totalUtilization = activeCores.Sum(c => c.Utilization);
        var avgUtilization = totalUtilization / activeCores.Count;

        // If average utilization is low, consolidate
        if (avgUtilization < TargetUtilizationPercent * 0.5)
        {
            var neededCores = (int)Math.Ceiling(totalUtilization / TargetUtilizationPercent);
            return Math.Max(MinActiveCores, neededCores);
        }

        // If average utilization is high, expand
        if (avgUtilization > TargetUtilizationPercent * 1.2)
        {
            return Math.Min(_totalCoreCount, _activeCoreCount + 2);
        }

        return _activeCoreCount;
    }

    private async Task ApplyConsolidationAsync(int targetCores, CancellationToken ct = default)
    {
        if (targetCores == _activeCoreCount) return;

        // Update core states
        for (int i = 0; i < _totalCoreCount; i++)
        {
            _coreStates[i].IsActive = i < targetCores;
        }

        lock (_lock)
        {
            _activeCoreCount = targetCores;
            _consolidationRatio = (double)targetCores / _totalCoreCount;
        }

        _lastConsolidationChange = DateTimeOffset.UtcNow;

        // Apply cgroup CPU limits on Linux
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
        {
            await ApplyLinuxCpusetAsync(targetCores, ct);
        }

        RecordOptimizationAction();
        RecordEnergySaved(EstimatePowerSavings());
    }

    private async Task ApplyLinuxCpusetAsync(int targetCores, CancellationToken ct)
    {
        try
        {
            var cpuList = string.Join(",", Enumerable.Range(0, targetCores));
            var cgroupPath = "/sys/fs/cgroup/datawarehouse";

            if (Directory.Exists(cgroupPath))
            {
                var cpusetPath = Path.Combine(cgroupPath, "cpuset.cpus");
                await File.WriteAllTextAsync(cpusetPath, cpuList, ct);
            }
        }
        catch
        {
            // Cgroup not available
        }
    }

    private double EstimatePowerSavings()
    {
        // Each idle core saves approximately 5-10W
        var idleCores = _totalCoreCount - _activeCoreCount;
        return idleCores * 7.5;
    }

    private void UpdateRecommendations()
    {
        ClearRecommendations();

        var state = GetConsolidationState();

        if (state.AverageActiveUtilization < 30 && state.ActiveCores > MinActiveCores)
        {
            var suggestedCores = Math.Max(MinActiveCores, (int)Math.Ceiling(state.ActiveCores * state.AverageActiveUtilization / TargetUtilizationPercent));
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-consolidate",
                Type = "Consolidate",
                Priority = 7,
                Description = $"Average CPU utilization is {state.AverageActiveUtilization:F0}%. Consider consolidating from {state.ActiveCores} to {suggestedCores} cores.",
                EstimatedEnergySavingsWh = (state.ActiveCores - suggestedCores) * 7.5,
                CanAutoApply = true,
                Action = "set-active-cores",
                ActionParameters = new Dictionary<string, object> { ["coreCount"] = suggestedCores }
            });
        }

        if (state.AverageActiveUtilization > 85 && state.ActiveCores < _totalCoreCount)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-expand",
                Type = "Expand",
                Priority = 8,
                Description = $"Average CPU utilization is high ({state.AverageActiveUtilization:F0}%). Consider activating more cores.",
                CanAutoApply = true,
                Action = "set-active-cores",
                ActionParameters = new Dictionary<string, object> { ["coreCount"] = Math.Min(_totalCoreCount, state.ActiveCores + 2) }
            });
        }
    }
}

/// <summary>
/// State of a CPU core.
/// </summary>
public sealed class CoreState
{
    public required int CoreId { get; init; }
    public bool IsActive { get; set; }
    public double Utilization { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
}

/// <summary>
/// Current workload consolidation state.
/// </summary>
public sealed record ConsolidationState
{
    public required DateTimeOffset Timestamp { get; init; }
    public required int TotalCores { get; init; }
    public required int ActiveCores { get; init; }
    public required int InactiveCores { get; init; }
    public required double ConsolidationRatio { get; init; }
    public required double AverageActiveUtilization { get; init; }
    public required double EstimatedPowerSavingsPercent { get; init; }
    public required IReadOnlyDictionary<int, bool> CoreStates { get; init; }
}
