namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.BatteryAwareness;

/// <summary>
/// Manages switching between power sources (grid, battery, solar, generator)
/// for optimal cost, carbon, and reliability.
/// </summary>
public sealed class PowerSourceSwitchingStrategy : SustainabilityStrategyBase
{
    private readonly Dictionary<string, PowerSource> _sources = new();
    private string? _activePrimarySource;
    private readonly List<SwitchEvent> _switchHistory = new();
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "power-source-switching";
    /// <inheritdoc/>
    public override string DisplayName => "Power Source Switching";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.BatteryAwareness;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.ActiveControl | SustainabilityCapabilities.ExternalIntegration;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Manages switching between grid, battery, solar, and generator power for optimal efficiency.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "power", "source", "switching", "solar", "grid", "generator", "ups" };

    /// <summary>Priority order for power sources.</summary>
    public List<string> SourcePriority { get; set; } = new() { "solar", "grid", "battery", "generator" };
    /// <summary>Minimum battery reserve (%).</summary>
    public int MinBatteryReservePercent { get; set; } = 20;

    /// <summary>Registers a power source.</summary>
    public void RegisterSource(string sourceId, PowerSourceType type, double maxPowerWatts, double carbonIntensity = 0, double costPerKwh = 0)
    {
        lock (_lock)
        {
            _sources[sourceId] = new PowerSource
            {
                SourceId = sourceId,
                Type = type,
                MaxPowerWatts = maxPowerWatts,
                CarbonIntensityGCO2ePerKwh = carbonIntensity,
                CostPerKwh = costPerKwh,
                IsAvailable = true
            };
        }
    }

    /// <summary>Updates source availability.</summary>
    public void UpdateSourceAvailability(string sourceId, bool isAvailable, double? currentPowerWatts = null, double? batteryPercent = null)
    {
        lock (_lock)
        {
            if (_sources.TryGetValue(sourceId, out var source))
            {
                source.IsAvailable = isAvailable;
                if (currentPowerWatts.HasValue) source.CurrentPowerWatts = currentPowerWatts.Value;
                if (batteryPercent.HasValue) source.BatteryPercent = batteryPercent;
            }
        }
        RecordSample(currentPowerWatts ?? 0, 0);
        EvaluateSwitching();
    }

    /// <summary>Gets the recommended power source.</summary>
    public PowerSourceRecommendation GetRecommendedSource(double requiredPowerWatts)
    {
        lock (_lock)
        {
            var availableSources = _sources.Values
                .Where(s => s.IsAvailable && s.MaxPowerWatts >= requiredPowerWatts)
                .ToList();

            // Exclude battery if below reserve
            availableSources = availableSources
                .Where(s => s.Type != PowerSourceType.Battery || (s.BatteryPercent ?? 100) > MinBatteryReservePercent)
                .ToList();

            // Sort by priority
            var sortedSources = availableSources
                .OrderBy(s => SourcePriority.IndexOf(s.SourceId) >= 0 ? SourcePriority.IndexOf(s.SourceId) : 999)
                .ThenBy(s => s.CarbonIntensityGCO2ePerKwh)
                .ThenBy(s => s.CostPerKwh)
                .ToList();

            var recommended = sortedSources.FirstOrDefault();
            if (recommended == null)
            {
                return new PowerSourceRecommendation
                {
                    RecommendedSourceId = null,
                    Reason = "No suitable power source available",
                    IsEmergency = true
                };
            }

            return new PowerSourceRecommendation
            {
                RecommendedSourceId = recommended.SourceId,
                SourceType = recommended.Type,
                Reason = $"Best available source: {recommended.Type}",
                CarbonIntensity = recommended.CarbonIntensityGCO2ePerKwh,
                CostPerKwh = recommended.CostPerKwh,
                IsEmergency = false
            };
        }
    }

    /// <summary>Switches to a power source.</summary>
    public bool SwitchTo(string sourceId, string reason)
    {
        lock (_lock)
        {
            if (!_sources.TryGetValue(sourceId, out var source) || !source.IsAvailable)
                return false;

            var previousSource = _activePrimarySource;
            _activePrimarySource = sourceId;

            _switchHistory.Add(new SwitchEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                FromSource = previousSource,
                ToSource = sourceId,
                Reason = reason
            });

            if (_switchHistory.Count > 1000) _switchHistory.RemoveAt(0);
        }

        RecordOptimizationAction();
        return true;
    }

    /// <summary>Gets switch history.</summary>
    public IReadOnlyList<SwitchEvent> GetSwitchHistory(int count = 100)
    {
        lock (_lock)
        {
            return _switchHistory.TakeLast(count).ToList();
        }
    }

    private void EvaluateSwitching()
    {
        ClearRecommendations();
        var recommendation = GetRecommendedSource(1000); // Assume 1kW baseline

        if (recommendation.IsEmergency)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-emergency",
                Type = "PowerEmergency",
                Priority = 10,
                Description = "No suitable power source available!",
                CanAutoApply = false
            });
        }
        else if (_activePrimarySource != recommendation.RecommendedSourceId)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-switch",
                Type = "PowerSourceSwitch",
                Priority = 7,
                Description = $"Switch to {recommendation.SourceType}: {recommendation.Reason}",
                CanAutoApply = true,
                Action = "switch-power-source",
                ActionParameters = new() { ["sourceId"] = recommendation.RecommendedSourceId! }
            });
        }
    }
}

/// <summary>Power source type.</summary>
public enum PowerSourceType { Grid, Battery, Solar, Wind, Generator, Fuel_Cell }

/// <summary>Power source information.</summary>
public sealed class PowerSource
{
    public required string SourceId { get; init; }
    public required PowerSourceType Type { get; init; }
    public required double MaxPowerWatts { get; init; }
    public double CarbonIntensityGCO2ePerKwh { get; set; }
    public double CostPerKwh { get; set; }
    public bool IsAvailable { get; set; }
    public double CurrentPowerWatts { get; set; }
    public double? BatteryPercent { get; set; }
}

/// <summary>Power source recommendation.</summary>
public sealed record PowerSourceRecommendation
{
    public string? RecommendedSourceId { get; init; }
    public PowerSourceType? SourceType { get; init; }
    public required string Reason { get; init; }
    public double CarbonIntensity { get; init; }
    public double CostPerKwh { get; init; }
    public bool IsEmergency { get; init; }
}

/// <summary>Power source switch event.</summary>
public sealed record SwitchEvent
{
    public required DateTimeOffset Timestamp { get; init; }
    public string? FromSource { get; init; }
    public required string ToSource { get; init; }
    public required string Reason { get; init; }
}
