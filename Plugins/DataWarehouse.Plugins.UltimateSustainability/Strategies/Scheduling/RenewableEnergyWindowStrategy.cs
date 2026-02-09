namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.Scheduling;

/// <summary>
/// Schedules workloads during periods of high renewable energy availability.
/// Uses solar and wind forecasts to identify optimal windows.
/// </summary>
public sealed class RenewableEnergyWindowStrategy : SustainabilityStrategyBase
{
    private double _currentRenewablePercent;
    private readonly Queue<(DateTimeOffset time, double percent)> _forecast = new();
    private Timer? _forecastTimer;
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "renewable-energy-window";
    /// <inheritdoc/>
    public override string DisplayName => "Renewable Energy Windows";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.Scheduling;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.Scheduling | SustainabilityCapabilities.PredictiveAnalytics | SustainabilityCapabilities.ExternalIntegration;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Schedules workloads during high renewable energy periods using solar and wind forecasts.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "renewable", "solar", "wind", "green", "scheduling" };

    /// <summary>Minimum renewable percentage to trigger scheduling.</summary>
    public double MinRenewablePercent { get; set; } = 50;

    /// <summary>Current renewable energy percentage.</summary>
    public double CurrentRenewablePercent { get { lock (_lock) return _currentRenewablePercent; } }

    /// <summary>Whether current period has high renewable energy.</summary>
    public bool IsHighRenewable => CurrentRenewablePercent >= MinRenewablePercent;

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        _forecastTimer = new Timer(async _ => await UpdateForecastAsync(), null, TimeSpan.Zero, TimeSpan.FromMinutes(15));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _forecastTimer?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Finds the next high-renewable window.</summary>
    public RenewableWindow? FindNextWindow(TimeSpan lookahead)
    {
        lock (_lock)
        {
            var cutoff = DateTimeOffset.UtcNow.Add(lookahead);
            var highPeriods = _forecast.Where(f => f.time <= cutoff && f.percent >= MinRenewablePercent).ToList();
            if (!highPeriods.Any()) return null;

            return new RenewableWindow
            {
                StartTime = highPeriods.First().time,
                EndTime = highPeriods.Last().time.AddHours(1),
                AverageRenewablePercent = highPeriods.Average(p => p.percent)
            };
        }
    }

    private async Task UpdateForecastAsync()
    {
        // Simulate forecast based on time of day
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            _forecast.Clear();
            for (int h = 0; h < 48; h++)
            {
                var time = now.AddHours(h);
                var hour = time.Hour;
                // Solar: high during day, wind: higher at night
                var solar = hour >= 8 && hour <= 18 ? 30 + (hour - 8) * 3 : 0;
                var wind = 15 + Random.Shared.NextDouble() * 15;
                var renewable = Math.Min(100, solar + wind);
                _forecast.Enqueue((time, renewable));
            }
            _currentRenewablePercent = _forecast.FirstOrDefault().percent;
        }

        RecordSample(0, 0);
        await Task.CompletedTask;
        UpdateRecommendations();
    }

    private void UpdateRecommendations()
    {
        ClearRecommendations();
        if (IsHighRenewable)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-run-now",
                Type = "HighRenewable",
                Priority = 7,
                Description = $"Renewable energy at {CurrentRenewablePercent:F0}%. Ideal time for intensive workloads.",
                EstimatedCarbonReductionGrams = 50,
                CanAutoApply = false
            });
        }
        else
        {
            var nextWindow = FindNextWindow(TimeSpan.FromHours(24));
            if (nextWindow != null)
            {
                AddRecommendation(new SustainabilityRecommendation
                {
                    RecommendationId = $"{StrategyId}-defer",
                    Type = "LowRenewable",
                    Priority = 5,
                    Description = $"Renewable at {CurrentRenewablePercent:F0}%. Next high window at {nextWindow.StartTime:HH:mm}.",
                    CanAutoApply = true,
                    Action = "defer"
                });
            }
        }
    }
}

/// <summary>A high-renewable energy window.</summary>
public sealed record RenewableWindow
{
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset EndTime { get; init; }
    public required double AverageRenewablePercent { get; init; }
}
