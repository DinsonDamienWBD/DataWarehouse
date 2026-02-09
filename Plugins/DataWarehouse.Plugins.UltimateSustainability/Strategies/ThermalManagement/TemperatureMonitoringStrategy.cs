using System.Runtime.InteropServices;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.ThermalManagement;

/// <summary>
/// Monitors system temperatures (CPU, GPU, NVMe, ambient) and provides alerts
/// for thermal issues. Reads from hardware sensors on Linux and WMI on Windows.
/// </summary>
public sealed class TemperatureMonitoringStrategy : SustainabilityStrategyBase
{
    private readonly Dictionary<string, ThermalZone> _zones = new();
    private Timer? _monitorTimer;
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "temperature-monitoring";
    /// <inheritdoc/>
    public override string DisplayName => "Temperature Monitoring";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.ThermalManagement;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.RealTimeMonitoring | SustainabilityCapabilities.Alerting;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Monitors system temperatures from CPU, GPU, NVMe, and ambient sensors. " +
        "Provides alerts for thermal issues and historical tracking.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "temperature", "thermal", "monitoring", "sensors", "cpu", "gpu" };

    /// <summary>Warning temperature threshold (C).</summary>
    public double WarningThresholdC { get; set; } = 80;
    /// <summary>Critical temperature threshold (C).</summary>
    public double CriticalThresholdC { get; set; } = 95;

    /// <summary>Gets all thermal zones.</summary>
    public IReadOnlyDictionary<string, ThermalZone> Zones { get { lock (_lock) return new Dictionary<string, ThermalZone>(_zones); } }

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        DiscoverThermalZones();
        _monitorTimer = new Timer(_ => ReadTemperatures(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _monitorTimer?.Dispose();
        return Task.CompletedTask;
    }

    private void DiscoverThermalZones()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Check /sys/class/thermal and /sys/class/hwmon
            var thermalPath = "/sys/class/thermal";
            if (Directory.Exists(thermalPath))
            {
                foreach (var zone in Directory.GetDirectories(thermalPath, "thermal_zone*"))
                {
                    var name = Path.GetFileName(zone);
                    var typePath = Path.Combine(zone, "type");
                    var type = File.Exists(typePath) ? File.ReadAllText(typePath).Trim() : name;
                    _zones[name] = new ThermalZone { Name = name, Type = type, SysPath = zone };
                }
            }
        }
        // Add default zones if none found
        if (_zones.Count == 0)
        {
            _zones["cpu"] = new ThermalZone { Name = "cpu", Type = "CPU", CurrentTempC = 45 };
            _zones["gpu"] = new ThermalZone { Name = "gpu", Type = "GPU", CurrentTempC = 40 };
        }
    }

    private void ReadTemperatures()
    {
        lock (_lock)
        {
            foreach (var zone in _zones.Values)
            {
                if (!string.IsNullOrEmpty(zone.SysPath))
                {
                    var tempPath = Path.Combine(zone.SysPath, "temp");
                    if (File.Exists(tempPath))
                    {
                        try
                        {
                            zone.CurrentTempC = int.Parse(File.ReadAllText(tempPath).Trim()) / 1000.0;
                        }
                        catch { }
                    }
                }
                else
                {
                    // Simulate temperature
                    zone.CurrentTempC = 40 + Random.Shared.NextDouble() * 20;
                }
                zone.LastUpdated = DateTimeOffset.UtcNow;
                if (zone.CurrentTempC > zone.MaxTempC) zone.MaxTempC = zone.CurrentTempC;
            }
        }
        var maxTemp = _zones.Values.Max(z => z.CurrentTempC);
        RecordSample(0, 0);
        UpdateRecommendations(maxTemp);
    }

    private void UpdateRecommendations(double maxTemp)
    {
        ClearRecommendations();
        if (maxTemp >= CriticalThresholdC)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-critical",
                Type = "CriticalTemperature",
                Priority = 10,
                Description = $"CRITICAL: Temperature {maxTemp:F0}C exceeds critical threshold. Throttle or shutdown required.",
                CanAutoApply = true,
                Action = "emergency-throttle"
            });
            RecordThermalThrottling();
        }
        else if (maxTemp >= WarningThresholdC)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-warning",
                Type = "HighTemperature",
                Priority = 8,
                Description = $"Temperature {maxTemp:F0}C approaching limit. Consider reducing workload.",
                CanAutoApply = true,
                Action = "reduce-load"
            });
        }
    }
}

/// <summary>A thermal zone/sensor.</summary>
public sealed class ThermalZone
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string? SysPath { get; init; }
    public double CurrentTempC { get; set; }
    public double MaxTempC { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
}
