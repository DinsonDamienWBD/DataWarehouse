namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.ThermalManagement;

/// <summary>
/// Implements thermal throttling to prevent overheating by reducing
/// CPU frequency and workload when temperatures exceed thresholds.
/// </summary>
public sealed class ThermalThrottlingStrategy : SustainabilityStrategyBase
{
    private double _currentTemp;
    private ThrottleLevel _currentLevel = ThrottleLevel.None;
    private Timer? _checkTimer;
    private Func<Task<double>>? _temperatureProvider;
    private Func<int, Task>? _frequencyController;
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "thermal-throttling";
    /// <inheritdoc/>
    public override string DisplayName => "Thermal Throttling";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.ThermalManagement;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.ActiveControl | SustainabilityCapabilities.RealTimeMonitoring;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Reduces CPU frequency and workload when temperatures exceed thresholds to prevent overheating and hardware damage.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "thermal", "throttling", "cpu", "protection", "cooling" };

    /// <summary>Temperature thresholds for each throttle level (C).</summary>
    public double LightThrottleC { get; set; } = 75;
    public double ModerateThrottleC { get; set; } = 85;
    public double HeavyThrottleC { get; set; } = 92;
    public double EmergencyShutdownC { get; set; } = 100;

    /// <summary>Current throttle level.</summary>
    public ThrottleLevel CurrentLevel { get { lock (_lock) return _currentLevel; } }

    /// <summary>Current temperature (C).</summary>
    public double CurrentTemperature { get { lock (_lock) return _currentTemp; } }

    /// <summary>Configures temperature source.</summary>
    public void SetTemperatureProvider(Func<Task<double>> provider) => _temperatureProvider = provider;

    /// <summary>Configures frequency controller.</summary>
    public void SetFrequencyController(Func<int, Task> controller) => _frequencyController = controller;

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        _checkTimer = new Timer(async _ => { try { await CheckAndThrottleAsync(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Timer callback failed: {ex.Message}"); } }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _checkTimer?.Dispose();
        return Task.CompletedTask;
    }

    private async Task CheckAndThrottleAsync()
    {
        // If no provider is configured, read temperature from hwmon sysfs (Linux)
        // or return the last known temperature (stable, no simulation).
        double temp;
        if (_temperatureProvider != null)
        {
            temp = await _temperatureProvider();
        }
        else
        {
            temp = ReadTemperatureFromSysFs() ?? _currentTemp;
        }

        ThrottleLevel prevLevel;
        lock (_lock)
        {
            _currentTemp = temp;
            prevLevel = _currentLevel;
        }

        // Use the user-configurable threshold properties instead of literal constants.
        ThrottleLevel newLevel;
        if (temp >= EmergencyShutdownC)
            newLevel = ThrottleLevel.Emergency;
        else if (temp >= HeavyThrottleC)
            newLevel = ThrottleLevel.Heavy;
        else if (temp >= ModerateThrottleC)
            newLevel = ThrottleLevel.Moderate;
        else if (temp >= LightThrottleC)
            newLevel = ThrottleLevel.Light;
        else
            newLevel = ThrottleLevel.None;

        if (newLevel != prevLevel)
        {
            await ApplyThrottleLevelAsync(newLevel);
            lock (_lock) _currentLevel = newLevel;
            if (newLevel > ThrottleLevel.None) RecordThermalThrottling();
            RecordOptimizationAction();
        }

        UpdateRecommendations();
    }

    private static double? ReadTemperatureFromSysFs()
    {
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
            return null;

        try
        {
            // Read first available CPU thermal zone
            var thermalBase = "/sys/class/thermal";
            if (!Directory.Exists(thermalBase)) return null;

            foreach (var zoneDir in Directory.GetDirectories(thermalBase, "thermal_zone*"))
            {
                var typePath = Path.Combine(zoneDir, "type");
                var tempPath = Path.Combine(zoneDir, "temp");
                if (!File.Exists(tempPath)) continue;

                // Prefer x86_pkg_temp or cpu-thermal zones
                var type = File.Exists(typePath) ? File.ReadAllText(typePath).Trim() : string.Empty;
                if (!type.Contains("cpu", StringComparison.OrdinalIgnoreCase) &&
                    !type.Contains("pkg", StringComparison.OrdinalIgnoreCase) &&
                    !type.Contains("thermal", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (int.TryParse(File.ReadAllText(tempPath).Trim(), out var tempMilliC))
                    return tempMilliC / 1000.0;
            }
        }
        catch { /* Sysfs not accessible */ }

        return null;
    }

    private async Task ApplyThrottleLevelAsync(ThrottleLevel level)
    {
        var maxFreqPercent = level switch
        {
            ThrottleLevel.Light => 80,
            ThrottleLevel.Moderate => 60,
            ThrottleLevel.Heavy => 40,
            ThrottleLevel.Emergency => 20,
            _ => 100
        };

        if (_frequencyController != null)
        {
            await _frequencyController(maxFreqPercent);
        }
    }

    private void UpdateRecommendations()
    {
        // Capture shared fields under lock before using them outside the lock.
        ThrottleLevel level;
        double temp;
        lock (_lock)
        {
            level = _currentLevel;
            temp = _currentTemp;
        }

        ClearRecommendations();
        if (level >= ThrottleLevel.Heavy)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-improve-cooling",
                Type = "ImproveCooling",
                Priority = 9,
                Description = $"Heavy thermal throttling active ({temp:F0}C). Improve cooling or reduce workload.",
                CanAutoApply = false
            });
        }
    }
}

/// <summary>Throttle levels based on temperature.</summary>
public enum ThrottleLevel
{
    None = 0,
    Light = 1,
    Moderate = 2,
    Heavy = 3,
    Emergency = 4
}
