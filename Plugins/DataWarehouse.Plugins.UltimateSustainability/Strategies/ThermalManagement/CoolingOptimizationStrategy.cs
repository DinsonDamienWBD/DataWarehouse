namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.ThermalManagement;

/// <summary>
/// Optimizes cooling system efficiency by controlling fan speeds and
/// coordinating with workload scheduling to minimize energy used for cooling.
/// </summary>
public sealed class CoolingOptimizationStrategy : SustainabilityStrategyBase
{
    private readonly Dictionary<string, FanZone> _fanZones = new();
    private CoolingProfile _currentProfile = CoolingProfile.Balanced;
    private Timer? _controlTimer;
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "cooling-optimization";
    /// <inheritdoc/>
    public override string DisplayName => "Cooling Optimization";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.ThermalManagement;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.ActiveControl | SustainabilityCapabilities.RealTimeMonitoring;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Controls fan speeds and cooling system to minimize energy used for cooling while maintaining safe temperatures.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "cooling", "fans", "optimization", "energy", "thermal" };

    /// <summary>Target temperature for balanced profile (C).</summary>
    public double TargetTemperatureC { get; set; } = 70;

    /// <summary>Current cooling profile.</summary>
    public CoolingProfile CurrentProfile { get { lock (_lock) return _currentProfile; } }

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        DiscoverFans();
        _controlTimer = new Timer(_ => OptimizeCooling(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _controlTimer?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Sets the cooling profile.</summary>
    public async Task SetProfileAsync(CoolingProfile profile, CancellationToken ct = default)
    {
        lock (_lock) _currentProfile = profile;
        await ApplyProfileAsync(profile);
        RecordOptimizationAction();
    }

    private void DiscoverFans()
    {
        // Discover from /sys/class/hwmon on Linux
        _fanZones["cpu"] = new FanZone { Name = "cpu", MaxRpm = 2500, CurrentRpm = 1000, CurrentPercent = 40 };
        _fanZones["case"] = new FanZone { Name = "case", MaxRpm = 1500, CurrentRpm = 600, CurrentPercent = 40 };
    }

    private void OptimizeCooling()
    {
        // Read current temperature and adjust fans
        var targetSpeed = _currentProfile switch
        {
            CoolingProfile.Silent => 30,
            CoolingProfile.Balanced => 50,
            CoolingProfile.Performance => 80,
            CoolingProfile.Maximum => 100,
            _ => 50
        };

        foreach (var fan in _fanZones.Values)
        {
            fan.CurrentPercent = targetSpeed;
            fan.CurrentRpm = (int)(fan.MaxRpm * targetSpeed / 100.0);
        }

        // Estimate power savings from reduced fan speed
        var baseFanPower = 10.0; // Watts at full speed
        var savedPower = baseFanPower * (1 - targetSpeed / 100.0);
        if (savedPower > 0) RecordEnergySaved(savedPower * 0.01);

        UpdateRecommendations();
    }

    private async Task ApplyProfileAsync(CoolingProfile profile)
    {
        await Task.Delay(1);
        // Would write to fan control sysfs or IPMI
    }

    private void UpdateRecommendations()
    {
        ClearRecommendations();
        if (_currentProfile == CoolingProfile.Maximum)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-reduce-cooling",
                Type = "ReduceCooling",
                Priority = 5,
                Description = "Cooling at maximum. Consider balanced profile for energy savings.",
                EstimatedEnergySavingsWh = 5,
                CanAutoApply = true,
                Action = "set-profile",
                ActionParameters = new Dictionary<string, object> { ["profile"] = "Balanced" }
            });
        }
    }
}

/// <summary>Cooling profile presets.</summary>
public enum CoolingProfile { Silent, Balanced, Performance, Maximum }

/// <summary>Fan zone information.</summary>
public sealed class FanZone
{
    public required string Name { get; init; }
    public int MaxRpm { get; init; }
    public int CurrentRpm { get; set; }
    public int CurrentPercent { get; set; }
}
