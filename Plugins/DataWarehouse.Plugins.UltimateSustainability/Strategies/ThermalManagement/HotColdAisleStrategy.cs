namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.ThermalManagement;

/// <summary>
/// Manages hot/cold aisle containment for data center efficiency.
/// Monitors temperature differentials and airflow optimization.
/// </summary>
public sealed class HotColdAisleStrategy : SustainabilityStrategyBase
{
    private readonly Dictionary<string, AisleZone> _zones = new();
    private Timer? _monitorTimer;
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "hot-cold-aisle";
    /// <inheritdoc/>
    public override string DisplayName => "Hot/Cold Aisle Management";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.ThermalManagement;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.RealTimeMonitoring | SustainabilityCapabilities.ActiveControl | SustainabilityCapabilities.Alerting;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Manages hot/cold aisle containment for optimal data center airflow and cooling efficiency.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "datacenter", "aisle", "containment", "airflow", "crah", "cooling" };

    /// <summary>Target cold aisle temperature (Celsius).</summary>
    public double TargetColdAisleTemp { get; set; } = 24;
    /// <summary>Maximum hot aisle temperature (Celsius).</summary>
    public double MaxHotAisleTemp { get; set; } = 40;
    /// <summary>Target delta-T across racks (Celsius).</summary>
    public double TargetDeltaT { get; set; } = 12;

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        _monitorTimer = new Timer(_ => MonitorZones(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _monitorTimer?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Registers an aisle zone.</summary>
    public void RegisterZone(string zoneId, string name, AisleType type, int rackCount)
    {
        lock (_lock)
        {
            _zones[zoneId] = new AisleZone
            {
                ZoneId = zoneId,
                Name = name,
                Type = type,
                RackCount = rackCount
            };
        }
    }

    /// <summary>Updates zone sensor readings.</summary>
    public void UpdateZoneReadings(string zoneId, double temperature, double humidity, double airflowCfm)
    {
        lock (_lock)
        {
            if (_zones.TryGetValue(zoneId, out var zone))
            {
                zone.CurrentTemperature = temperature;
                zone.CurrentHumidity = humidity;
                zone.AirflowCfm = airflowCfm;
                zone.LastUpdated = DateTimeOffset.UtcNow;
            }
        }
        RecordSample(temperature, 0);
    }

    /// <summary>Gets containment effectiveness.</summary>
    public ContainmentEffectiveness GetContainmentEffectiveness()
    {
        lock (_lock)
        {
            var coldAisles = _zones.Values.Where(z => z.Type == AisleType.Cold).ToList();
            var hotAisles = _zones.Values.Where(z => z.Type == AisleType.Hot).ToList();

            if (!coldAisles.Any() || !hotAisles.Any())
                return new ContainmentEffectiveness { Success = false, Reason = "Insufficient zone data" };

            var avgColdTemp = coldAisles.Average(z => z.CurrentTemperature);
            var avgHotTemp = hotAisles.Average(z => z.CurrentTemperature);
            var actualDeltaT = avgHotTemp - avgColdTemp;

            // Effectiveness = actual delta-T / target delta-T
            var effectiveness = Math.Min(100, (actualDeltaT / TargetDeltaT) * 100);

            // Check for hot spots
            var hotSpots = coldAisles.Where(z => z.CurrentTemperature > TargetColdAisleTemp + 3).ToList();

            return new ContainmentEffectiveness
            {
                Success = true,
                EffectivenessPercent = effectiveness,
                AverageColdAisleTemp = avgColdTemp,
                AverageHotAisleTemp = avgHotTemp,
                ActualDeltaT = actualDeltaT,
                HotSpotCount = hotSpots.Count,
                HotSpotZones = hotSpots.Select(z => z.ZoneId).ToList()
            };
        }
    }

    /// <summary>Gets airflow recommendations.</summary>
    public IReadOnlyList<AirflowRecommendation> GetAirflowRecommendations()
    {
        var recommendations = new List<AirflowRecommendation>();

        lock (_lock)
        {
            var effectiveness = GetContainmentEffectiveness();

            foreach (var zone in _zones.Values.Where(z => z.Type == AisleType.Cold))
            {
                if (zone.CurrentTemperature > TargetColdAisleTemp + 2)
                {
                    recommendations.Add(new AirflowRecommendation
                    {
                        ZoneId = zone.ZoneId,
                        Type = AirflowRecommendationType.IncreaseAirflow,
                        Reason = $"Cold aisle temp {zone.CurrentTemperature:F1}C exceeds target {TargetColdAisleTemp}C",
                        EstimatedSavingsWatts = 0,
                        Priority = 8
                    });
                }
                else if (zone.CurrentTemperature < TargetColdAisleTemp - 3 && zone.AirflowCfm > 0)
                {
                    recommendations.Add(new AirflowRecommendation
                    {
                        ZoneId = zone.ZoneId,
                        Type = AirflowRecommendationType.ReduceAirflow,
                        Reason = $"Cold aisle temp {zone.CurrentTemperature:F1}C well below target, reduce cooling",
                        EstimatedSavingsWatts = zone.AirflowCfm * 0.1, // Rough estimate
                        Priority = 5
                    });
                }
            }

            foreach (var zone in _zones.Values.Where(z => z.Type == AisleType.Hot))
            {
                if (zone.CurrentTemperature > MaxHotAisleTemp)
                {
                    recommendations.Add(new AirflowRecommendation
                    {
                        ZoneId = zone.ZoneId,
                        Type = AirflowRecommendationType.CheckContainment,
                        Reason = $"Hot aisle temp {zone.CurrentTemperature:F1}C exceeds max {MaxHotAisleTemp}C",
                        EstimatedSavingsWatts = 0,
                        Priority = 9
                    });
                }
            }
        }

        return recommendations.OrderByDescending(r => r.Priority).ToList();
    }

    private void MonitorZones()
    {
        ClearRecommendations();
        var recs = GetAirflowRecommendations();
        var effectiveness = GetContainmentEffectiveness();

        foreach (var rec in recs.Take(3))
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-{rec.ZoneId}-{rec.Type}",
                Type = rec.Type.ToString(),
                Priority = rec.Priority,
                Description = rec.Reason,
                EstimatedEnergySavingsWh = rec.EstimatedSavingsWatts * 24,
                CanAutoApply = rec.Type == AirflowRecommendationType.ReduceAirflow
            });
        }

        if (effectiveness.Success && effectiveness.EffectivenessPercent < 70)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-containment-low",
                Type = "LowContainmentEffectiveness",
                Priority = 7,
                Description = $"Containment effectiveness at {effectiveness.EffectivenessPercent:F0}%. Check for air leaks.",
                CanAutoApply = false
            });
        }
    }
}

/// <summary>Aisle type.</summary>
public enum AisleType { Cold, Hot }

/// <summary>Aisle zone information.</summary>
public sealed class AisleZone
{
    public required string ZoneId { get; init; }
    public required string Name { get; init; }
    public required AisleType Type { get; init; }
    public required int RackCount { get; init; }
    public double CurrentTemperature { get; set; }
    public double CurrentHumidity { get; set; }
    public double AirflowCfm { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
}

/// <summary>Containment effectiveness metrics.</summary>
public sealed record ContainmentEffectiveness
{
    public bool Success { get; init; }
    public string? Reason { get; init; }
    public double EffectivenessPercent { get; init; }
    public double AverageColdAisleTemp { get; init; }
    public double AverageHotAisleTemp { get; init; }
    public double ActualDeltaT { get; init; }
    public int HotSpotCount { get; init; }
    public List<string> HotSpotZones { get; init; } = new();
}

/// <summary>Airflow recommendation type.</summary>
public enum AirflowRecommendationType { IncreaseAirflow, ReduceAirflow, CheckContainment, AdjustVanes }

/// <summary>Airflow recommendation.</summary>
public sealed record AirflowRecommendation
{
    public required string ZoneId { get; init; }
    public required AirflowRecommendationType Type { get; init; }
    public required string Reason { get; init; }
    public required double EstimatedSavingsWatts { get; init; }
    public required int Priority { get; init; }
}
