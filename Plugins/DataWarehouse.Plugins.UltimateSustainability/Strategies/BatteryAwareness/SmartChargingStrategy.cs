namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.BatteryAwareness;

/// <summary>
/// Implements smart charging to extend battery lifespan and optimize for grid carbon.
/// Controls charge rates and limits based on usage patterns and carbon intensity.
/// </summary>
public sealed class SmartChargingStrategy : SustainabilityStrategyBase
{
    private ChargingProfile _currentProfile = new();
    private readonly List<ChargingSession> _sessions = new();
    private readonly object _lock = new();
    private Func<Task<double>>? _carbonIntensityProvider;

    /// <inheritdoc/>
    public override string StrategyId => "smart-charging";
    /// <inheritdoc/>
    public override string DisplayName => "Smart Charging";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.BatteryAwareness;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.ActiveControl | SustainabilityCapabilities.Scheduling | SustainabilityCapabilities.ExternalIntegration;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Optimizes battery charging to extend lifespan and reduce carbon footprint through smart scheduling.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "battery", "charging", "smart", "lifespan", "carbon", "scheduling" };

    /// <summary>Target charge level for longevity (%).</summary>
    public int LongevityChargeTarget { get; set; } = 80;
    /// <summary>Minimum charge level (%).</summary>
    public int MinimumChargeLevel { get; set; } = 20;
    /// <summary>Enable carbon-aware charging.</summary>
    public bool CarbonAwareCharging { get; set; } = true;
    /// <summary>Carbon intensity threshold for fast charging (gCO2e/kWh).</summary>
    public double LowCarbonThreshold { get; set; } = 200;

    /// <summary>Sets the carbon intensity provider.</summary>
    public void SetCarbonIntensityProvider(Func<Task<double>> provider) => _carbonIntensityProvider = provider;

    /// <summary>Gets the recommended charging action.</summary>
    public async Task<ChargingAction> GetChargingActionAsync(int currentChargePercent, bool isPluggedIn)
    {
        if (!isPluggedIn)
            return new ChargingAction { Action = ChargingActionType.None, Reason = "Not plugged in" };

        if (currentChargePercent >= LongevityChargeTarget)
            return new ChargingAction { Action = ChargingActionType.StopCharging, Reason = $"Reached longevity target ({LongevityChargeTarget}%)" };

        if (currentChargePercent < MinimumChargeLevel)
            return new ChargingAction { Action = ChargingActionType.FastCharge, Reason = "Below minimum level" };

        if (CarbonAwareCharging && _carbonIntensityProvider != null)
        {
            var carbonIntensity = await _carbonIntensityProvider();
            if (carbonIntensity < LowCarbonThreshold)
                return new ChargingAction
                {
                    Action = ChargingActionType.FastCharge,
                    Reason = $"Low carbon intensity ({carbonIntensity:F0} gCO2e/kWh)",
                    CarbonIntensity = carbonIntensity
                };
            else
                return new ChargingAction
                {
                    Action = ChargingActionType.SlowCharge,
                    Reason = $"High carbon intensity ({carbonIntensity:F0} gCO2e/kWh), trickle charging",
                    CarbonIntensity = carbonIntensity
                };
        }

        return new ChargingAction { Action = ChargingActionType.NormalCharge, Reason = "Default charging" };
    }

    /// <summary>Starts a charging session.</summary>
    public void StartSession(int initialChargePercent)
    {
        lock (_lock)
        {
            _sessions.Add(new ChargingSession
            {
                SessionId = Guid.NewGuid().ToString("N"),
                StartedAt = DateTimeOffset.UtcNow,
                InitialChargePercent = initialChargePercent
            });
        }
        RecordOptimizationAction();
    }

    /// <summary>Ends the current charging session.</summary>
    public void EndSession(int finalChargePercent, double energyUsedWh)
    {
        double overheadSaved = 0;
        lock (_lock)
        {
            var session = _sessions.LastOrDefault();
            if (session != null)
            {
                session.EndedAt = DateTimeOffset.UtcNow;
                session.FinalChargePercent = finalChargePercent;
                session.EnergyUsedWh = energyUsedWh;
                // P2-4407: Compute actual energy saved vs fast-charge overhead baseline.
                // Smart charging avoids ~8% fast-charge degradation overhead, scaled by
                // the fraction of charge replenished this session.
                if (finalChargePercent > session.InitialChargePercent)
                    overheadSaved = energyUsedWh * 0.08
                        * (finalChargePercent - session.InitialChargePercent) / 100.0;
            }
        }
        if (overheadSaved > 0)
            RecordEnergySaved(overheadSaved);
    }

    /// <summary>Gets charging statistics.</summary>
    public ChargingStatistics GetChargingStatistics()
    {
        lock (_lock)
        {
            var completedSessions = _sessions.Where(s => s.EndedAt.HasValue).ToList();
            return new ChargingStatistics
            {
                TotalSessions = completedSessions.Count,
                TotalEnergyWh = completedSessions.Sum(s => s.EnergyUsedWh),
                AverageSessionDurationMinutes = completedSessions.Any()
                    ? completedSessions.Average(s => (s.EndedAt!.Value - s.StartedAt).TotalMinutes)
                    : 0,
                AverageChargeGained = completedSessions.Any()
                    ? completedSessions.Average(s => s.FinalChargePercent - s.InitialChargePercent)
                    : 0
            };
        }
    }
}

/// <summary>Charging action type.</summary>
public enum ChargingActionType { None, StopCharging, SlowCharge, NormalCharge, FastCharge }

/// <summary>Charging action recommendation.</summary>
public sealed record ChargingAction
{
    public required ChargingActionType Action { get; init; }
    public required string Reason { get; init; }
    public double? CarbonIntensity { get; init; }
}

/// <summary>Charging profile settings.</summary>
public sealed class ChargingProfile
{
    public int MaxChargePercent { get; set; } = 80;
    public int MinChargePercent { get; set; } = 20;
    public TimeSpan? ChargeByTime { get; set; }
    public bool PreferLowCarbon { get; set; } = true;
}

/// <summary>Charging session record.</summary>
public sealed class ChargingSession
{
    public required string SessionId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; set; }
    public required int InitialChargePercent { get; init; }
    public int FinalChargePercent { get; set; }
    public double EnergyUsedWh { get; set; }
}

/// <summary>Charging statistics.</summary>
public sealed record ChargingStatistics
{
    public int TotalSessions { get; init; }
    public double TotalEnergyWh { get; init; }
    public double AverageSessionDurationMinutes { get; init; }
    public double AverageChargeGained { get; init; }
}
