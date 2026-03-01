using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.CarbonAwareness;

/// <summary>
/// Tracks carbon intensity of the electrical grid in real-time.
/// Monitors grid carbon emissions (gCO2e/kWh) and provides historical data
/// for carbon-aware workload scheduling decisions.
/// </summary>
public sealed class CarbonIntensityTrackingStrategy : SustainabilityStrategyBase
{
    private readonly ConcurrentQueue<CarbonIntensityDataPoint> _history = new();
    private readonly object _lock = new();
    private double _currentIntensity;
    private double _24HourAverage;
    private Timer? _pollingTimer;
    private const int MaxHistorySize = 8640; // 24 hours at 10-second intervals

    /// <inheritdoc/>
    public override string StrategyId => "carbon-intensity-tracking";

    /// <inheritdoc/>
    public override string DisplayName => "Carbon Intensity Tracking";

    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.CarbonAwareness;

    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.RealTimeMonitoring |
        SustainabilityCapabilities.CarbonCalculation |
        SustainabilityCapabilities.ExternalIntegration |
        SustainabilityCapabilities.Reporting;

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Tracks real-time carbon intensity of the electrical grid (gCO2e/kWh). " +
        "Monitors grid emissions, maintains historical data, and identifies low-carbon windows " +
        "for optimal workload scheduling. Integrates with grid carbon APIs.";

    /// <inheritdoc/>
    public override string[] Tags => new[] { "carbon", "intensity", "grid", "monitoring", "real-time" };

    /// <summary>
    /// Gets the current carbon intensity in gCO2e/kWh.
    /// </summary>
    public double CurrentIntensity
    {
        get { lock (_lock) return _currentIntensity; }
    }

    /// <summary>
    /// Gets the 24-hour average carbon intensity.
    /// </summary>
    public double Average24Hour
    {
        get { lock (_lock) return _24HourAverage; }
    }

    /// <summary>
    /// Polling interval for carbon intensity updates.
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Region/grid zone being monitored.
    /// </summary>
    public string Region { get; set; } = "US-WECC";

    /// <summary>
    /// API endpoint for carbon intensity data.
    /// </summary>
    public string? ApiEndpoint { get; set; }

    /// <summary>
    /// API key for carbon intensity service.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <inheritdoc/>
    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        // Initialize with estimated value based on region
        _currentIntensity = GetEstimatedIntensity(Region);
        _24HourAverage = _currentIntensity;

        // Start polling timer
        _pollingTimer = new Timer(
            async _ => { try { await PollCarbonIntensityAsync(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Timer callback failed: {ex.Message}"); } },
            null,
            TimeSpan.Zero,
            PollingInterval);

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _pollingTimer?.Dispose();
        _pollingTimer = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records a carbon intensity reading.
    /// </summary>
    public void RecordIntensity(double intensityGCO2ePerKwh, DateTimeOffset? timestamp = null)
    {
        var dataPoint = new CarbonIntensityDataPoint
        {
            Timestamp = timestamp ?? DateTimeOffset.UtcNow,
            Intensity = intensityGCO2ePerKwh,
            Region = Region
        };

        _history.Enqueue(dataPoint);
        while (_history.Count > MaxHistorySize)
            _history.TryDequeue(out _);

        // Finding 4421: compute the average outside the lock to avoid O(n) LINQ under lock.
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddHours(-24);
        double newAverage = 0.0;
        bool hasData = false;
        double sum = 0.0;
        int count = 0;
        foreach (var p in _history)
        {
            if (p.Timestamp >= cutoff)
            {
                sum += p.Intensity;
                count++;
            }
        }
        if (count > 0) { newAverage = sum / count; hasData = true; }

        lock (_lock)
        {
            _currentIntensity = intensityGCO2ePerKwh;
            if (hasData) _24HourAverage = newAverage;
        }

        RecordSample(0, intensityGCO2ePerKwh);

        // Generate recommendations based on current intensity
        UpdateRecommendations(intensityGCO2ePerKwh);
    }

    /// <summary>
    /// Gets historical carbon intensity data.
    /// </summary>
    public IReadOnlyList<CarbonIntensityDataPoint> GetHistory(TimeSpan? duration = null)
    {
        var cutoff = duration.HasValue
            ? DateTimeOffset.UtcNow - duration.Value
            : DateTimeOffset.MinValue;

        return _history.Where(p => p.Timestamp >= cutoff).ToList().AsReadOnly();
    }

    /// <summary>
    /// Finds the next low-carbon window within the specified lookahead period.
    /// </summary>
    public LowCarbonWindow? FindNextLowCarbonWindow(TimeSpan lookahead, double thresholdGCO2ePerKwh)
    {
        // Use historical patterns to predict low-carbon windows
        var history = GetHistory(TimeSpan.FromDays(7));
        if (history.Count < 100) return null;

        // Find typical low-carbon hours
        var hourlyAverages = history
            .GroupBy(p => p.Timestamp.Hour)
            .Select(g => new { Hour = g.Key, Average = g.Average(p => p.Intensity) })
            .OrderBy(x => x.Average)
            .ToList();

        var lowCarbonHour = hourlyAverages.FirstOrDefault(x => x.Average < thresholdGCO2ePerKwh);
        if (lowCarbonHour == null) return null;

        var now = DateTimeOffset.UtcNow;
        var nextWindow = now.Date.AddHours(lowCarbonHour.Hour);
        if (nextWindow < now) nextWindow = nextWindow.AddDays(1);

        if (nextWindow - now > lookahead) return null;

        return new LowCarbonWindow
        {
            StartTime = nextWindow,
            EndTime = nextWindow.AddHours(1),
            PredictedIntensity = lowCarbonHour.Average,
            Confidence = Math.Min(0.9, history.Count / 1000.0)
        };
    }

    /// <summary>
    /// Calculates carbon emissions for energy consumed.
    /// </summary>
    public CarbonEmission CalculateEmissions(double energyWh)
    {
        var intensity = CurrentIntensity;
        var emissionsGrams = (energyWh / 1000.0) * intensity;

        return new CarbonEmission
        {
            Timestamp = DateTimeOffset.UtcNow,
            EmissionsGrams = emissionsGrams,
            EnergyConsumedWh = energyWh,
            CarbonIntensity = intensity,
            Region = Region,
            Scope = 2
        };
    }

    private async Task PollCarbonIntensityAsync()
    {
        try
        {
            double intensity;

            if (!string.IsNullOrEmpty(ApiEndpoint) && !string.IsNullOrEmpty(ApiKey))
            {
                intensity = await FetchCarbonIntensityFromApiAsync();
            }
            else
            {
                // Use estimation with time-of-day variation
                intensity = GetEstimatedIntensity(Region);
                intensity = ApplyTimeOfDayVariation(intensity);
            }

            RecordIntensity(intensity);
        }
        catch
        {
            // Polling failed, use estimation
            var intensity = GetEstimatedIntensity(Region);
            RecordIntensity(intensity);
        }
    }

    private async Task<double> FetchCarbonIntensityFromApiAsync()
    {
        if (string.IsNullOrEmpty(ApiEndpoint) || string.IsNullOrEmpty(ApiKey))
            return ApplyTimeOfDayVariation(GetEstimatedIntensity(Region));

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Add("auth-token", ApiKey);

        var url = ApiEndpoint.TrimEnd('/');
        // Support Electricity Maps format: /carbon-intensity/latest?zone={Region}
        if (!url.Contains('?'))
            url = $"{url}?zone={Uri.EscapeDataString(Region)}";

        using var response = await http.GetAsync(url);
        if (!response.IsSuccessStatusCode)
            return ApplyTimeOfDayVariation(GetEstimatedIntensity(Region));

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonSerializer.Deserialize<JsonElement>(content);

        // Try common field names used by Electricity Maps and compatible APIs
        if (json.TryGetProperty("carbonIntensity", out var ci))
            return ci.GetDouble();
        if (json.TryGetProperty("carbon_intensity", out var ci2))
            return ci2.GetDouble();
        if (json.TryGetProperty("intensity", out var ci3))
        {
            if (ci3.ValueKind == JsonValueKind.Number)
                return ci3.GetDouble();
            if (ci3.TryGetProperty("actual", out var actual))
                return actual.GetDouble();
        }

        return ApplyTimeOfDayVariation(GetEstimatedIntensity(Region));
    }

    private static double GetEstimatedIntensity(string region)
    {
        // Regional average carbon intensities (gCO2e/kWh)
        return region switch
        {
            "US-WECC" => 350,      // Western US
            "US-ERCOT" => 400,     // Texas
            "US-MISO" => 450,      // Midwest
            "US-PJM" => 380,       // Mid-Atlantic
            "US-NPCC" => 280,      // Northeast
            "EU-DE" => 350,        // Germany
            "EU-FR" => 50,         // France (nuclear)
            "EU-NO" => 20,         // Norway (hydro)
            "EU-PL" => 650,        // Poland (coal)
            "EU-UK" => 200,        // UK
            "APAC-AU" => 600,      // Australia
            "APAC-JP" => 450,      // Japan
            "APAC-IN" => 700,      // India
            "APAC-CN" => 550,      // China
            _ => 400               // Global average
        };
    }

    private static double ApplyTimeOfDayVariation(double baseIntensity)
    {
        var hour = DateTime.UtcNow.Hour;
        // Lower intensity during solar hours (10-16), higher at night
        var variation = hour switch
        {
            >= 10 and <= 16 => -0.15,  // Solar generation peak
            >= 22 or <= 5 => 0.1,      // Night load
            _ => 0.0
        };
        return baseIntensity * (1 + variation);
    }

    // Finding 4421: RecalculateAverages removed — average now computed in RecordIntensity
    // without holding _lock, using a lock-free pass over the ConcurrentQueue.

    private void UpdateRecommendations(double currentIntensity)
    {
        ClearRecommendations();

        if (currentIntensity > _24HourAverage * 1.2)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-defer-load",
                Type = "DeferLoad",
                Priority = 8,
                Description = $"Carbon intensity is {currentIntensity:F0} gCO2e/kWh, 20% above average. Consider deferring non-urgent workloads.",
                EstimatedCarbonReductionGrams = (currentIntensity - _24HourAverage) * 0.5,
                CanAutoApply = true,
                Action = "defer",
                ActionParameters = new Dictionary<string, object>
                {
                    ["deferHours"] = 2,
                    ["targetIntensity"] = _24HourAverage
                }
            });
        }

        if (currentIntensity < _24HourAverage * 0.8)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-run-now",
                Type = "RunNow",
                Priority = 7,
                Description = $"Carbon intensity is low ({currentIntensity:F0} gCO2e/kWh). Optimal time for batch processing.",
                EstimatedCarbonReductionGrams = (_24HourAverage - currentIntensity) * 0.5,
                CanAutoApply = false,
                Action = "schedule-now"
            });
        }
    }
}

/// <summary>
/// A carbon intensity data point.
/// </summary>
public sealed record CarbonIntensityDataPoint
{
    /// <summary>
    /// Timestamp of the reading.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Carbon intensity in gCO2e/kWh.
    /// </summary>
    public required double Intensity { get; init; }

    /// <summary>
    /// Region/grid zone.
    /// </summary>
    public string Region { get; init; } = "Unknown";
}

/// <summary>
/// A predicted low-carbon window.
/// </summary>
public sealed record LowCarbonWindow
{
    /// <summary>
    /// Start time of the window.
    /// </summary>
    public required DateTimeOffset StartTime { get; init; }

    /// <summary>
    /// End time of the window.
    /// </summary>
    public required DateTimeOffset EndTime { get; init; }

    /// <summary>
    /// Predicted carbon intensity during the window.
    /// </summary>
    public required double PredictedIntensity { get; init; }

    /// <summary>
    /// Confidence in the prediction (0-1).
    /// </summary>
    public double Confidence { get; init; }
}
