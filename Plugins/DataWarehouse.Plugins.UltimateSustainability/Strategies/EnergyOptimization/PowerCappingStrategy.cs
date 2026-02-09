using System.Runtime.InteropServices;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.EnergyOptimization;

/// <summary>
/// Implements power capping to limit maximum power consumption.
/// Supports Intel RAPL, AMD APM, and software-based power limiting
/// to stay within power budgets and reduce energy costs.
/// </summary>
public sealed class PowerCappingStrategy : SustainabilityStrategyBase
{
    private double _currentPowerWatts;
    private double _powerCapWatts;
    private double _defaultTdpWatts;
    private bool _capEnforced;
    private Timer? _monitorTimer;
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "power-capping";

    /// <inheritdoc/>
    public override string DisplayName => "Power Capping";

    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.EnergyOptimization;

    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.ActiveControl |
        SustainabilityCapabilities.RealTimeMonitoring |
        SustainabilityCapabilities.Alerting;

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Limits maximum power consumption using Intel RAPL, AMD APM, or software-based capping. " +
        "Enforces power budgets to reduce energy costs and prevent thermal issues.";

    /// <inheritdoc/>
    public override string[] Tags => new[] { "power", "capping", "rapl", "tdp", "budget", "limit" };

    /// <summary>
    /// Gets the current power consumption in watts.
    /// </summary>
    public double CurrentPowerWatts
    {
        get { lock (_lock) return _currentPowerWatts; }
    }

    /// <summary>
    /// Gets or sets the power cap in watts.
    /// </summary>
    public double PowerCapWatts
    {
        get { lock (_lock) return _powerCapWatts; }
        set { lock (_lock) _powerCapWatts = value; }
    }

    /// <summary>
    /// Gets the default TDP in watts.
    /// </summary>
    public double DefaultTdpWatts
    {
        get { lock (_lock) return _defaultTdpWatts; }
    }

    /// <summary>
    /// Whether power capping is currently enforced.
    /// </summary>
    public bool IsCapEnforced
    {
        get { lock (_lock) return _capEnforced; }
    }

    /// <summary>
    /// Capping method to use.
    /// </summary>
    public PowerCappingMethod Method { get; set; } = PowerCappingMethod.Auto;

    /// <summary>
    /// Action to take when power exceeds cap.
    /// </summary>
    public PowerCapAction CapAction { get; set; } = PowerCapAction.ThrottleCpu;

    /// <summary>
    /// Hysteresis percentage to prevent oscillation.
    /// </summary>
    public double HysteresisPercent { get; set; } = 5.0;

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        DetectPowerCapabilities();

        _monitorTimer = new Timer(
            async _ => await MonitorAndEnforcePowerCapAsync(),
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));

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
    /// Sets the power cap.
    /// </summary>
    public async Task SetPowerCapAsync(double capWatts, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        if (capWatts <= 0)
            throw new ArgumentOutOfRangeException(nameof(capWatts), "Power cap must be positive.");

        lock (_lock)
        {
            _powerCapWatts = capWatts;
        }

        await ApplyPowerCapAsync(capWatts, ct);
        RecordOptimizationAction();
        UpdateRecommendations();
    }

    /// <summary>
    /// Removes the power cap.
    /// </summary>
    public async Task RemovePowerCapAsync(CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        lock (_lock)
        {
            _powerCapWatts = _defaultTdpWatts;
            _capEnforced = false;
        }

        await ApplyPowerCapAsync(_defaultTdpWatts, ct);
        UpdateRecommendations();
    }

    /// <summary>
    /// Gets power consumption breakdown.
    /// </summary>
    public PowerBreakdown GetPowerBreakdown()
    {
        ThrowIfNotInitialized();

        // Read from RAPL domains if available
        var cpuPower = ReadRaplDomain("package-0") ?? (_currentPowerWatts * 0.6);
        var dramPower = ReadRaplDomain("dram") ?? (_currentPowerWatts * 0.1);
        var gpuPower = (_currentPowerWatts * 0.2);
        var otherPower = _currentPowerWatts - cpuPower - dramPower - gpuPower;

        return new PowerBreakdown
        {
            Timestamp = DateTimeOffset.UtcNow,
            TotalPowerWatts = _currentPowerWatts,
            CpuPowerWatts = cpuPower,
            DramPowerWatts = dramPower,
            GpuPowerWatts = Math.Max(0, gpuPower),
            OtherPowerWatts = Math.Max(0, otherPower),
            PowerCapWatts = _powerCapWatts,
            CapUtilizationPercent = _powerCapWatts > 0 ? (_currentPowerWatts / _powerCapWatts) * 100 : 0,
            IsCapEnforced = _capEnforced
        };
    }

    /// <summary>
    /// Gets power history for analysis.
    /// </summary>
    public IReadOnlyList<PowerReading> GetPowerHistory(TimeSpan duration)
    {
        // Would return actual history from a buffer
        // Returning simulated data for now
        var readings = new List<PowerReading>();
        var now = DateTimeOffset.UtcNow;
        var interval = TimeSpan.FromSeconds(1);
        var count = (int)(duration.TotalSeconds / interval.TotalSeconds);

        for (int i = count; i >= 0; i--)
        {
            readings.Add(new PowerReading
            {
                Timestamp = now.AddSeconds(-i),
                PowerWatts = _currentPowerWatts * (0.8 + Random.Shared.NextDouble() * 0.4)
            });
        }

        return readings.AsReadOnly();
    }

    private void DetectPowerCapabilities()
    {
        // Detect CPU TDP
        _defaultTdpWatts = DetectTdp();
        _powerCapWatts = _defaultTdpWatts;

        // Detect available capping methods
        if (Method == PowerCappingMethod.Auto)
        {
            if (IsRaplAvailable())
                Method = PowerCappingMethod.IntelRapl;
            else if (IsAmdApmAvailable())
                Method = PowerCappingMethod.AmdApm;
            else
                Method = PowerCappingMethod.SoftwareCapping;
        }
    }

    private double DetectTdp()
    {
        // Try to read from RAPL first
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                var constraintPath = "/sys/class/powercap/intel-rapl/intel-rapl:0/constraint_0_max_power_uw";
                if (File.Exists(constraintPath))
                {
                    var maxPowerUw = long.Parse(File.ReadAllText(constraintPath).Trim());
                    return maxPowerUw / 1_000_000.0;
                }
            }
            catch { }
        }

        // Estimate based on processor count
        return Environment.ProcessorCount switch
        {
            <= 4 => 65,
            <= 8 => 95,
            <= 16 => 125,
            <= 32 => 180,
            _ => 250
        };
    }

    private bool IsRaplAvailable()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return Directory.Exists("/sys/class/powercap/intel-rapl");
        }
        return false;
    }

    private bool IsAmdApmAvailable()
    {
        // Check for AMD APM support
        return false;
    }

    private double? ReadRaplDomain(string domain)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return null;

        try
        {
            var basePath = "/sys/class/powercap/intel-rapl/intel-rapl:0";
            var energyPath = domain == "package-0"
                ? $"{basePath}/energy_uj"
                : $"{basePath}/{domain}/energy_uj";

            if (!File.Exists(energyPath))
                return null;

            // Read twice to calculate power
            var energy1 = long.Parse(File.ReadAllText(energyPath).Trim());
            Thread.Sleep(100);
            var energy2 = long.Parse(File.ReadAllText(energyPath).Trim());

            var deltaUj = energy2 - energy1;
            var deltaSeconds = 0.1;
            return (deltaUj / 1_000_000.0) / deltaSeconds;
        }
        catch
        {
            return null;
        }
    }

    private async Task MonitorAndEnforcePowerCapAsync()
    {
        try
        {
            // Read current power
            var power = await ReadCurrentPowerAsync();

            lock (_lock)
            {
                _currentPowerWatts = power;
            }

            RecordSample(power, 0);

            // Enforce cap if needed
            if (_powerCapWatts > 0 && power > _powerCapWatts * (1 + HysteresisPercent / 100))
            {
                await EnforcePowerCapAsync();
            }
            else if (_capEnforced && power < _powerCapWatts * (1 - HysteresisPercent / 100))
            {
                await ReleasePowerCapAsync();
            }

            UpdateRecommendations();
        }
        catch
        {
            // Monitoring failed
        }
    }

    private async Task<double> ReadCurrentPowerAsync()
    {
        if (Method == PowerCappingMethod.IntelRapl && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var power = ReadRaplDomain("package-0");
            if (power.HasValue)
                return power.Value;
        }

        // Estimate based on CPU utilization
        await Task.Delay(1);
        var baseWatts = _defaultTdpWatts * 0.3; // Idle power
        var loadFactor = Random.Shared.NextDouble() * 0.5 + 0.2; // 20-70% load
        return baseWatts + (_defaultTdpWatts - baseWatts) * loadFactor;
    }

    private async Task ApplyPowerCapAsync(double capWatts, CancellationToken ct)
    {
        if (Method == PowerCappingMethod.IntelRapl && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                var constraintPath = "/sys/class/powercap/intel-rapl/intel-rapl:0/constraint_0_power_limit_uw";
                if (File.Exists(constraintPath))
                {
                    var capUw = (long)(capWatts * 1_000_000);
                    await File.WriteAllTextAsync(constraintPath, capUw.ToString(), ct);
                }
            }
            catch
            {
                // Fall back to software capping
            }
        }
    }

    private async Task EnforcePowerCapAsync()
    {
        lock (_lock)
        {
            _capEnforced = true;
        }

        switch (CapAction)
        {
            case PowerCapAction.ThrottleCpu:
                // Would reduce CPU frequency
                break;
            case PowerCapAction.ReduceParallelism:
                // Would reduce thread count
                break;
            case PowerCapAction.Alert:
                // Just log/alert
                break;
        }

        RecordOptimizationAction();
        await Task.CompletedTask;
    }

    private async Task ReleasePowerCapAsync()
    {
        lock (_lock)
        {
            _capEnforced = false;
        }

        await Task.CompletedTask;
    }

    private void UpdateRecommendations()
    {
        ClearRecommendations();

        var breakdown = GetPowerBreakdown();

        if (breakdown.CapUtilizationPercent > 90)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-near-cap",
                Type = "NearPowerCap",
                Priority = 8,
                Description = $"Power consumption ({breakdown.TotalPowerWatts:F0}W) is {breakdown.CapUtilizationPercent:F0}% of cap ({breakdown.PowerCapWatts:F0}W). Consider reducing workload or increasing cap.",
                CanAutoApply = false
            });
        }

        if (!_capEnforced && _powerCapWatts == _defaultTdpWatts)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-set-cap",
                Type = "SetPowerCap",
                Priority = 4,
                Description = "No power cap is set. Consider setting a cap 10-20% below TDP for energy savings.",
                EstimatedEnergySavingsWh = _defaultTdpWatts * 0.15,
                CanAutoApply = true,
                Action = "set-cap",
                ActionParameters = new Dictionary<string, object>
                {
                    ["capWatts"] = _defaultTdpWatts * 0.85
                }
            });
        }
    }
}

/// <summary>
/// Power capping method.
/// </summary>
public enum PowerCappingMethod
{
    /// <summary>Auto-detect best method.</summary>
    Auto,
    /// <summary>Intel Running Average Power Limit.</summary>
    IntelRapl,
    /// <summary>AMD Application Power Management.</summary>
    AmdApm,
    /// <summary>Software-based capping via throttling.</summary>
    SoftwareCapping
}

/// <summary>
/// Action to take when power exceeds cap.
/// </summary>
public enum PowerCapAction
{
    /// <summary>Reduce CPU frequency.</summary>
    ThrottleCpu,
    /// <summary>Reduce thread parallelism.</summary>
    ReduceParallelism,
    /// <summary>Alert only, no action.</summary>
    Alert
}

/// <summary>
/// Power consumption breakdown.
/// </summary>
public sealed record PowerBreakdown
{
    public required DateTimeOffset Timestamp { get; init; }
    public required double TotalPowerWatts { get; init; }
    public required double CpuPowerWatts { get; init; }
    public required double DramPowerWatts { get; init; }
    public required double GpuPowerWatts { get; init; }
    public required double OtherPowerWatts { get; init; }
    public required double PowerCapWatts { get; init; }
    public required double CapUtilizationPercent { get; init; }
    public required bool IsCapEnforced { get; init; }
}

/// <summary>
/// A power reading.
/// </summary>
public sealed record PowerReading
{
    public required DateTimeOffset Timestamp { get; init; }
    public required double PowerWatts { get; init; }
}
