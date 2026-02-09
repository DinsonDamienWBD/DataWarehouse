namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.BatteryAwareness;

/// <summary>
/// Schedules workloads based on battery charge state and power source.
/// Defers intensive operations when on battery and accelerates when plugged in.
/// </summary>
public sealed class ChargeAwareSchedulingStrategy : SustainabilityStrategyBase
{
    private bool _onBattery;
    private int _batteryPercent = 100;
    private readonly Queue<DeferredWorkload> _deferredWorkloads = new();
    private Timer? _checkTimer;
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "charge-aware-scheduling";
    /// <inheritdoc/>
    public override string DisplayName => "Charge-Aware Scheduling";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.BatteryAwareness;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.Scheduling | SustainabilityCapabilities.RealTimeMonitoring;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Schedules workloads based on battery state and power source. " +
        "Defers heavy operations when on battery, runs them when plugged in.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "battery", "scheduling", "charge", "power-source" };

    /// <summary>Battery threshold below which to defer intensive workloads.</summary>
    public int DeferThreshold { get; set; } = 50;

    /// <summary>Whether currently on battery power.</summary>
    public bool OnBattery { get { lock (_lock) return _onBattery; } }

    /// <summary>Deferred workload count.</summary>
    public int DeferredCount { get { lock (_lock) return _deferredWorkloads.Count; } }

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        _checkTimer = new Timer(async _ => await CheckPowerStateAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _checkTimer?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Schedules a workload that may be deferred if on battery.</summary>
    public string ScheduleWorkload(Func<CancellationToken, Task> workload, string name, bool intensive)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (_lock)
        {
            if (intensive && _onBattery && _batteryPercent < DeferThreshold)
            {
                _deferredWorkloads.Enqueue(new DeferredWorkload { Id = id, Name = name, Workload = workload, DeferredAt = DateTimeOffset.UtcNow });
                RecordWorkloadScheduled();
                return id;
            }
        }
        _ = Task.Run(() => workload(CancellationToken.None));
        RecordOptimizationAction();
        return id;
    }

    private async Task CheckPowerStateAsync()
    {
        var wasOnBattery = _onBattery;
        UpdatePowerState();

        // If switched to AC power, run deferred workloads
        if (wasOnBattery && !_onBattery)
        {
            await RunDeferredWorkloadsAsync();
        }
        UpdateRecommendations();
    }

    private void UpdatePowerState()
    {
        // Read from /sys/class/power_supply/AC0/online on Linux
        try
        {
            if (File.Exists("/sys/class/power_supply/AC0/online"))
            {
                var online = File.ReadAllText("/sys/class/power_supply/AC0/online").Trim();
                lock (_lock) _onBattery = online == "0";
            }
            if (File.Exists("/sys/class/power_supply/BAT0/capacity"))
            {
                lock (_lock) _batteryPercent = int.Parse(File.ReadAllText("/sys/class/power_supply/BAT0/capacity").Trim());
            }
        }
        catch { lock (_lock) _onBattery = false; }
    }

    private async Task RunDeferredWorkloadsAsync()
    {
        while (true)
        {
            DeferredWorkload? workload;
            lock (_lock)
            {
                if (!_deferredWorkloads.TryDequeue(out workload)) break;
            }
            try
            {
                await workload.Workload(CancellationToken.None);
                RecordOptimizationAction();
            }
            catch { }
        }
    }

    private void UpdateRecommendations()
    {
        ClearRecommendations();
        lock (_lock)
        {
            if (_deferredWorkloads.Count > 0)
            {
                AddRecommendation(new SustainabilityRecommendation
                {
                    RecommendationId = $"{StrategyId}-deferred",
                    Type = "DeferredWorkloads",
                    Priority = 5,
                    Description = $"{_deferredWorkloads.Count} workloads deferred. Will run when on AC power.",
                    CanAutoApply = false
                });
            }
        }
    }
}

internal sealed record DeferredWorkload
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Func<CancellationToken, Task> Workload { get; init; }
    public required DateTimeOffset DeferredAt { get; init; }
}
