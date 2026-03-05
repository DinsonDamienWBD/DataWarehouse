namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.Metrics;

/// <summary>
/// Tracks Water Usage Effectiveness (WUE) for data center sustainability.
/// Monitors cooling water consumption relative to IT equipment energy.
/// </summary>
public sealed class WaterUsageTrackingStrategy : SustainabilityStrategyBase
{
    private readonly List<WaterReading> _history = new();
    private readonly object _lock = new();
    private Timer? _trackingTimer;
    private Func<Task<double>>? _waterUsageProvider;
    private Func<Task<double>>? _itLoadProvider;

    /// <inheritdoc/>
    public override string StrategyId => "water-usage-tracking";
    /// <inheritdoc/>
    public override string DisplayName => "Water Usage Tracking (WUE)";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.Metrics;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.RealTimeMonitoring | SustainabilityCapabilities.Reporting;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Tracks Water Usage Effectiveness (WUE) for data center cooling water efficiency.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "wue", "water", "datacenter", "cooling", "sustainability", "evaporative" };

    /// <summary>Target WUE (liters per kWh). Lower is better.</summary>
    public double TargetWue { get; set; } = 1.2;
    /// <summary>Alert threshold WUE.</summary>
    public double AlertThresholdWue { get; set; } = 2.0;
    /// <summary>Water cost per liter (USD).</summary>
    public double WaterCostPerLiter { get; set; } = 0.003;

    /// <summary>Sets water usage provider (liters/hour).</summary>
    public void SetWaterUsageProvider(Func<Task<double>> provider) => _waterUsageProvider = provider;
    /// <summary>Sets IT load provider (kW).</summary>
    public void SetItLoadProvider(Func<Task<double>> provider) => _itLoadProvider = provider;

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        _trackingTimer = new Timer(async _ => { try { await TrackWaterAsync(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Timer callback failed: {ex.Message}"); } }, null, TimeSpan.Zero, TimeSpan.FromMinutes(15));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _trackingTimer?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Records a water usage reading manually.</summary>
    public void RecordWaterUsage(double waterLitersPerHour, double itLoadKw)
    {
        var wue = itLoadKw > 0 ? waterLitersPerHour / itLoadKw : 0;
        var reading = new WaterReading
        {
            Timestamp = DateTimeOffset.UtcNow,
            WaterLitersPerHour = waterLitersPerHour,
            ItLoadKw = itLoadKw,
            Wue = wue,
            CostPerHour = waterLitersPerHour * WaterCostPerLiter
        };

        lock (_lock)
        {
            _history.Add(reading);
            if (_history.Count > 10000) _history.RemoveAt(0);
        }

        RecordSample(waterLitersPerHour, 0);
        UpdateRecommendations(wue, waterLitersPerHour);
    }

    private async Task TrackWaterAsync()
    {
        if (_waterUsageProvider == null || _itLoadProvider == null) return;

        var waterUsage = await _waterUsageProvider();
        var itLoad = await _itLoadProvider();

        if (itLoad > 0)
        {
            RecordWaterUsage(waterUsage, itLoad);
        }
    }

    /// <summary>Gets current WUE.</summary>
    public double GetCurrentWue()
    {
        lock (_lock)
        {
            var latest = _history.LastOrDefault();
            return latest?.Wue ?? 0;
        }
    }

    /// <summary>Gets water usage statistics.</summary>
    public WaterStatistics GetStatistics(TimeSpan? period = null)
    {
        var since = period.HasValue ? DateTimeOffset.UtcNow - period.Value : DateTimeOffset.MinValue;

        lock (_lock)
        {
            var readings = _history.Where(r => r.Timestamp >= since).ToList();
            if (!readings.Any())
                return new WaterStatistics();

            var intervalHours = 0.25; // 15-min intervals
            return new WaterStatistics
            {
                CurrentWue = readings.Last().Wue,
                AverageWue = readings.Average(r => r.Wue),
                MinWue = readings.Min(r => r.Wue),
                MaxWue = readings.Max(r => r.Wue),
                TotalWaterLiters = readings.Sum(r => r.WaterLitersPerHour * intervalHours),
                TotalWaterCostUsd = readings.Sum(r => r.CostPerHour * intervalHours),
                ReadingCount = readings.Count
            };
        }
    }

    /// <summary>Gets water saving recommendations.</summary>
    public IReadOnlyList<WaterSavingRecommendation> GetWaterSavingRecommendations()
    {
        var recommendations = new List<WaterSavingRecommendation>();
        var stats = GetStatistics(TimeSpan.FromDays(1));

        if (stats.AverageWue > TargetWue)
        {
            var potentialReduction = (stats.AverageWue - TargetWue) / stats.AverageWue;
            var savingsLiters = stats.TotalWaterLiters * potentialReduction;

            recommendations.Add(new WaterSavingRecommendation
            {
                Type = WaterSavingType.IncreaseAirCooling,
                Description = "Increase air-side economizer usage to reduce evaporative cooling",
                EstimatedSavingsLitersPerDay = savingsLiters,
                EstimatedCostSavingsUsd = savingsLiters * WaterCostPerLiter,
                Priority = 6
            });

            recommendations.Add(new WaterSavingRecommendation
            {
                Type = WaterSavingType.OptimizeCoolingTower,
                Description = "Optimize cooling tower cycles of concentration",
                EstimatedSavingsLitersPerDay = savingsLiters * 0.3,
                EstimatedCostSavingsUsd = savingsLiters * 0.3 * WaterCostPerLiter,
                Priority = 5
            });
        }

        if (stats.MaxWue > stats.AverageWue * 1.5)
        {
            recommendations.Add(new WaterSavingRecommendation
            {
                Type = WaterSavingType.ReducePeaks,
                Description = "WUE peaks indicate inefficiency during high-load periods",
                EstimatedSavingsLitersPerDay = stats.TotalWaterLiters * 0.1,
                EstimatedCostSavingsUsd = stats.TotalWaterLiters * 0.1 * WaterCostPerLiter,
                Priority = 4
            });
        }

        return recommendations.OrderByDescending(r => r.Priority).ToList();
    }

    private void UpdateRecommendations(double wue, double waterUsage)
    {
        ClearRecommendations();

        if (wue > AlertThresholdWue)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-high-wue",
                Type = "HighWUE",
                Priority = 7,
                Description = $"WUE at {wue:F2} L/kWh exceeds threshold. Using {waterUsage:F0} L/hour.",
                CanAutoApply = false
            });
        }
        else if (wue > TargetWue)
        {
            var savings = (wue - TargetWue) * waterUsage * WaterCostPerLiter;
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-above-target",
                Type = "AboveTargetWUE",
                Priority = 4,
                Description = $"WUE at {wue:F2} L/kWh above target {TargetWue:F2}.",
                EstimatedCostSavingsUsd = savings * 24,
                CanAutoApply = false
            });
        }
    }
}

/// <summary>Water usage reading.</summary>
public sealed record WaterReading
{
    public required DateTimeOffset Timestamp { get; init; }
    public required double WaterLitersPerHour { get; init; }
    public required double ItLoadKw { get; init; }
    public required double Wue { get; init; }
    public required double CostPerHour { get; init; }
}

/// <summary>Water usage statistics.</summary>
public sealed record WaterStatistics
{
    public double CurrentWue { get; init; }
    public double AverageWue { get; init; }
    public double MinWue { get; init; }
    public double MaxWue { get; init; }
    public double TotalWaterLiters { get; init; }
    public double TotalWaterCostUsd { get; init; }
    public int ReadingCount { get; init; }
}

/// <summary>Water saving recommendation type.</summary>
public enum WaterSavingType { IncreaseAirCooling, OptimizeCoolingTower, ReducePeaks, WaterRecycling }

/// <summary>Water saving recommendation.</summary>
public sealed record WaterSavingRecommendation
{
    public required WaterSavingType Type { get; init; }
    public required string Description { get; init; }
    public required double EstimatedSavingsLitersPerDay { get; init; }
    public required double EstimatedCostSavingsUsd { get; init; }
    public required int Priority { get; init; }
}
