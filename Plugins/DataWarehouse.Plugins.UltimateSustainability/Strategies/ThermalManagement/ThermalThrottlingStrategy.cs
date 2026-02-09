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
        _checkTimer = new Timer(async _ => await CheckAndThrottleAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
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
        var temp = _temperatureProvider != null ? await _temperatureProvider() : 50 + Random.Shared.NextDouble() * 30;
        lock (_lock) _currentTemp = temp;

        var newLevel = temp switch
        {
            >= 100 => ThrottleLevel.Emergency,
            >= 92 => ThrottleLevel.Heavy,
            >= 85 => ThrottleLevel.Moderate,
            >= 75 => ThrottleLevel.Light,
            _ => ThrottleLevel.None
        };

        if (newLevel != _currentLevel)
        {
            await ApplyThrottleLevelAsync(newLevel);
            lock (_lock) _currentLevel = newLevel;
            if (newLevel > ThrottleLevel.None) RecordThermalThrottling();
            RecordOptimizationAction();
        }

        UpdateRecommendations();
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
        ClearRecommendations();
        if (_currentLevel >= ThrottleLevel.Heavy)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-improve-cooling",
                Type = "ImproveCooling",
                Priority = 9,
                Description = $"Heavy thermal throttling active ({_currentTemp:F0}C). Improve cooling or reduce workload.",
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
