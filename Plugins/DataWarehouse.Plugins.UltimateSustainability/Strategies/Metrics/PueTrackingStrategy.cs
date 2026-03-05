namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.Metrics;

/// <summary>
/// Tracks Power Usage Effectiveness (PUE) for data center efficiency.
/// Monitors IT load vs total facility power to measure infrastructure overhead.
/// </summary>
public sealed class PueTrackingStrategy : SustainabilityStrategyBase
{
    private readonly List<PueReading> _history = new();
    private readonly object _lock = new();
    private Timer? _trackingTimer;
    private Func<Task<double>>? _itLoadProvider;
    private Func<Task<double>>? _totalPowerProvider;

    /// <inheritdoc/>
    public override string StrategyId => "pue-tracking";
    /// <inheritdoc/>
    public override string DisplayName => "PUE Tracking";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.Metrics;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.RealTimeMonitoring | SustainabilityCapabilities.Reporting;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Tracks Power Usage Effectiveness (PUE) for data center efficiency monitoring.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "pue", "datacenter", "efficiency", "power", "metrics", "green-grid" };

    /// <summary>Target PUE (1.0 is ideal, typical is 1.5-2.0).</summary>
    public double TargetPue { get; set; } = 1.4;
    /// <summary>Alert threshold PUE.</summary>
    public double AlertThresholdPue { get; set; } = 1.8;

    /// <summary>Sets IT load power provider.</summary>
    public void SetItLoadProvider(Func<Task<double>> provider) => _itLoadProvider = provider;
    /// <summary>Sets total facility power provider.</summary>
    public void SetTotalPowerProvider(Func<Task<double>> provider) => _totalPowerProvider = provider;

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        _trackingTimer = new Timer(async _ => { try { await TrackPueAsync(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Timer callback failed: {ex.Message}"); } }, null, TimeSpan.Zero, TimeSpan.FromMinutes(5));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _trackingTimer?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Records a PUE reading manually.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="itLoadKw"/> is not positive.</exception>
    public void RecordPue(double itLoadKw, double totalPowerKw)
    {
        // P2-4433: Guard against division by zero and nonsensical negative IT load.
        if (itLoadKw <= 0)
            throw new ArgumentOutOfRangeException(nameof(itLoadKw), itLoadKw, "IT load must be a positive value in kW.");

        var pue = totalPowerKw / itLoadKw;
        var reading = new PueReading
        {
            Timestamp = DateTimeOffset.UtcNow,
            ItLoadKw = itLoadKw,
            TotalPowerKw = totalPowerKw,
            Pue = pue,
            OverheadKw = totalPowerKw - itLoadKw
        };

        lock (_lock)
        {
            _history.Add(reading);
            if (_history.Count > 10000) _history.RemoveAt(0);
        }

        RecordSample(totalPowerKw, 0);
        UpdateRecommendations(pue);
    }

    private async Task TrackPueAsync()
    {
        if (_itLoadProvider == null || _totalPowerProvider == null) return;

        var itLoad = await _itLoadProvider();
        var totalPower = await _totalPowerProvider();

        if (itLoad > 0 && totalPower >= itLoad)
        {
            RecordPue(itLoad, totalPower);
        }
    }

    /// <summary>Gets current PUE.</summary>
    public double GetCurrentPue()
    {
        lock (_lock)
        {
            var latest = _history.LastOrDefault();
            return latest?.Pue ?? 0;
        }
    }

    /// <summary>Gets PUE statistics.</summary>
    public PueStatistics GetStatistics(TimeSpan? period = null)
    {
        var since = period.HasValue ? DateTimeOffset.UtcNow - period.Value : DateTimeOffset.MinValue;

        lock (_lock)
        {
            var readings = _history.Where(r => r.Timestamp >= since).ToList();
            if (!readings.Any())
                return new PueStatistics();

            return new PueStatistics
            {
                CurrentPue = readings.Last().Pue,
                AveragePue = readings.Average(r => r.Pue),
                MinPue = readings.Min(r => r.Pue),
                MaxPue = readings.Max(r => r.Pue),
                TotalItLoadKwh = readings.Sum(r => r.ItLoadKw * 5.0 / 60.0), // 5-min intervals
                TotalOverheadKwh = readings.Sum(r => r.OverheadKw * 5.0 / 60.0),
                ReadingCount = readings.Count,
                TimeAboveTarget = TimeSpan.FromMinutes(readings.Count(r => r.Pue > TargetPue) * 5)
            };
        }
    }

    /// <summary>Gets PUE trend.</summary>
    public PueTrend GetTrend()
    {
        lock (_lock)
        {
            if (_history.Count < 10)
                return new PueTrend { HasSufficientData = false };

            var recent = _history.TakeLast(12).ToList(); // Last hour
            var previous = _history.SkipLast(12).TakeLast(12).ToList();

            if (!previous.Any())
                return new PueTrend { HasSufficientData = false };

            var recentAvg = recent.Average(r => r.Pue);
            var previousAvg = previous.Average(r => r.Pue);
            var change = recentAvg - previousAvg;

            return new PueTrend
            {
                HasSufficientData = true,
                CurrentAverage = recentAvg,
                PreviousAverage = previousAvg,
                Change = change,
                ChangePercent = previousAvg > 0 ? (change / previousAvg) * 100 : 0,
                IsImproving = change < 0,
                IsDegrading = change > 0.05
            };
        }
    }

    private void UpdateRecommendations(double pue)
    {
        ClearRecommendations();

        if (pue > AlertThresholdPue)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-high-pue",
                Type = "HighPUE",
                Priority = 8,
                Description = $"PUE at {pue:F2} exceeds alert threshold {AlertThresholdPue:F2}. Check cooling efficiency.",
                CanAutoApply = false
            });
        }
        else if (pue > TargetPue)
        {
            var overheadWaste = (pue - TargetPue) * 100; // Rough estimate
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-above-target",
                Type = "AboveTargetPUE",
                Priority = 5,
                Description = $"PUE at {pue:F2} above target {TargetPue:F2}. Potential for {overheadWaste:F0}W savings.",
                EstimatedEnergySavingsWh = overheadWaste * 24,
                CanAutoApply = false
            });
        }
    }
}

/// <summary>PUE reading.</summary>
public sealed record PueReading
{
    public required DateTimeOffset Timestamp { get; init; }
    public required double ItLoadKw { get; init; }
    public required double TotalPowerKw { get; init; }
    public required double Pue { get; init; }
    public required double OverheadKw { get; init; }
}

/// <summary>PUE statistics.</summary>
public sealed record PueStatistics
{
    public double CurrentPue { get; init; }
    public double AveragePue { get; init; }
    public double MinPue { get; init; }
    public double MaxPue { get; init; }
    public double TotalItLoadKwh { get; init; }
    public double TotalOverheadKwh { get; init; }
    public int ReadingCount { get; init; }
    public TimeSpan TimeAboveTarget { get; init; }
}

/// <summary>PUE trend analysis.</summary>
public sealed record PueTrend
{
    public bool HasSufficientData { get; init; }
    public double CurrentAverage { get; init; }
    public double PreviousAverage { get; init; }
    public double Change { get; init; }
    public double ChangePercent { get; init; }
    public bool IsImproving { get; init; }
    public bool IsDegrading { get; init; }
}
