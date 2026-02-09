using System.Runtime.InteropServices;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.BatteryAwareness;

/// <summary>
/// Monitors battery level and health for mobile and UPS-backed systems.
/// Provides real-time battery status, health metrics, and charge/discharge rate tracking.
/// </summary>
public sealed class BatteryLevelMonitoringStrategy : SustainabilityStrategyBase
{
    private BatteryStatus _currentStatus = new();
    private Timer? _monitorTimer;
    private readonly List<BatteryReading> _history = new();
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "battery-level-monitoring";
    /// <inheritdoc/>
    public override string DisplayName => "Battery Level Monitoring";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.BatteryAwareness;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.RealTimeMonitoring | SustainabilityCapabilities.Alerting;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Monitors battery level, health, and charge/discharge rates for mobile and UPS systems. " +
        "Provides alerts for low battery and degraded health conditions.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "battery", "monitoring", "level", "health", "ups" };

    /// <summary>Low battery threshold (%).</summary>
    public int LowBatteryThreshold { get; set; } = 20;
    /// <summary>Critical battery threshold (%).</summary>
    public int CriticalBatteryThreshold { get; set; } = 10;

    /// <summary>Gets the current battery status.</summary>
    public BatteryStatus CurrentStatus { get { lock (_lock) return _currentStatus; } }

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        _monitorTimer = new Timer(_ => MonitorBattery(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _monitorTimer?.Dispose();
        return Task.CompletedTask;
    }

    private void MonitorBattery()
    {
        var status = ReadBatteryStatus();
        lock (_lock)
        {
            _currentStatus = status;
            _history.Add(new BatteryReading { Timestamp = DateTimeOffset.UtcNow, ChargePercent = status.ChargePercent, IsCharging = status.IsCharging });
            if (_history.Count > 1000) _history.RemoveAt(0);
        }
        RecordSample(status.DischargingWatts, 0);
        UpdateRecommendations(status);
    }

    private BatteryStatus ReadBatteryStatus()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return ReadLinuxBattery();
        }
        return new BatteryStatus { IsPresent = false, ChargePercent = 100, IsCharging = true, IsFull = true };
    }

    private BatteryStatus ReadLinuxBattery()
    {
        try
        {
            var batteryPath = "/sys/class/power_supply/BAT0";
            if (!Directory.Exists(batteryPath)) batteryPath = "/sys/class/power_supply/BAT1";
            if (!Directory.Exists(batteryPath)) return new BatteryStatus { IsPresent = false };

            var status = new BatteryStatus { IsPresent = true };

            if (File.Exists($"{batteryPath}/capacity"))
                status.ChargePercent = int.Parse(File.ReadAllText($"{batteryPath}/capacity").Trim());
            if (File.Exists($"{batteryPath}/status"))
            {
                var state = File.ReadAllText($"{batteryPath}/status").Trim().ToLower();
                status.IsCharging = state == "charging";
                status.IsFull = state == "full";
                status.IsDischarging = state == "discharging";
            }
            if (File.Exists($"{batteryPath}/power_now"))
                status.DischargingWatts = long.Parse(File.ReadAllText($"{batteryPath}/power_now").Trim()) / 1_000_000.0;
            if (File.Exists($"{batteryPath}/energy_full"))
                status.DesignCapacityWh = long.Parse(File.ReadAllText($"{batteryPath}/energy_full").Trim()) / 1_000_000.0;
            if (File.Exists($"{batteryPath}/energy_full_design"))
            {
                var designCapacity = long.Parse(File.ReadAllText($"{batteryPath}/energy_full_design").Trim()) / 1_000_000.0;
                status.HealthPercent = designCapacity > 0 ? (status.DesignCapacityWh / designCapacity) * 100 : 100;
            }

            return status;
        }
        catch { return new BatteryStatus { IsPresent = false }; }
    }

    private void UpdateRecommendations(BatteryStatus status)
    {
        ClearRecommendations();
        if (!status.IsPresent) return;

        if (status.ChargePercent <= CriticalBatteryThreshold && status.IsDischarging)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-critical",
                Type = "CriticalBattery",
                Priority = 10,
                Description = $"CRITICAL: Battery at {status.ChargePercent}%. Suspend non-essential operations immediately.",
                CanAutoApply = true,
                Action = "emergency-shutdown"
            });
        }
        else if (status.ChargePercent <= LowBatteryThreshold && status.IsDischarging)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-low",
                Type = "LowBattery",
                Priority = 8,
                Description = $"Battery at {status.ChargePercent}%. Consider reducing power consumption.",
                CanAutoApply = true,
                Action = "reduce-power"
            });
        }

        if (status.HealthPercent < 80)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-health",
                Type = "BatteryHealth",
                Priority = 6,
                Description = $"Battery health degraded to {status.HealthPercent:F0}%. Consider replacement.",
                CanAutoApply = false
            });
        }
    }
}

/// <summary>Battery status information.</summary>
public sealed class BatteryStatus
{
    public bool IsPresent { get; set; }
    public int ChargePercent { get; set; }
    public bool IsCharging { get; set; }
    public bool IsDischarging { get; set; }
    public bool IsFull { get; set; }
    public double DischargingWatts { get; set; }
    public double DesignCapacityWh { get; set; }
    public double HealthPercent { get; set; } = 100;
    public TimeSpan? TimeRemaining { get; set; }
}

/// <summary>Battery reading for history.</summary>
public sealed record BatteryReading
{
    public required DateTimeOffset Timestamp { get; init; }
    public required int ChargePercent { get; init; }
    public required bool IsCharging { get; init; }
}
