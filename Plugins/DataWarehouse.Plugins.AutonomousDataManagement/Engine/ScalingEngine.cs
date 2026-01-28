using DataWarehouse.SDK.AI;
using DataWarehouse.Plugins.AutonomousDataManagement.Models;
using DataWarehouse.Plugins.AutonomousDataManagement.Providers;
using System.Collections.Concurrent;
using System.Text.Json;

namespace DataWarehouse.Plugins.AutonomousDataManagement.Engine;

/// <summary>
/// Predictive scaling engine for forecasting resource needs and auto-scaling.
/// </summary>
public sealed class ScalingEngine : IDisposable
{
    private readonly AIProviderSelector _providerSelector;
    private readonly ConcurrentDictionary<string, ResourceMetricsSeries> _metricsSeries;
    private readonly ConcurrentQueue<ScalingEvent> _scalingHistory;
    private readonly ScalingEngineConfig _config;
    private readonly Timer _forecastTimer;
    private readonly SemaphoreSlim _scalingLock;
    private bool _disposed;

    public ScalingEngine(AIProviderSelector providerSelector, ScalingEngineConfig? config = null)
    {
        _providerSelector = providerSelector ?? throw new ArgumentNullException(nameof(providerSelector));
        _config = config ?? new ScalingEngineConfig();
        _metricsSeries = new ConcurrentDictionary<string, ResourceMetricsSeries>();
        _scalingHistory = new ConcurrentQueue<ScalingEvent>();
        _scalingLock = new SemaphoreSlim(_config.MaxConcurrentScalingOps);

        _forecastTimer = new Timer(
            _ => _ = ForecastCycleAsync(CancellationToken.None),
            null,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(_config.ForecastIntervalMinutes));
    }

    /// <summary>
    /// Records resource metrics for forecasting.
    /// </summary>
    public void RecordMetrics(string resourceId, ResourceMetrics metrics)
    {
        var series = _metricsSeries.GetOrAdd(resourceId, _ => new ResourceMetricsSeries
        {
            ResourceId = resourceId,
            WindowSize = _config.MetricsWindowSize
        });

        lock (series)
        {
            series.AddMetrics(metrics);
        }
    }

    /// <summary>
    /// Predicts resource needs for a future time window.
    /// </summary>
    public async Task<ScalingForecast> ForecastAsync(string resourceId, TimeSpan lookAhead, CancellationToken ct = default)
    {
        if (!_metricsSeries.TryGetValue(resourceId, out var series))
        {
            return new ScalingForecast
            {
                ResourceId = resourceId,
                ForecastWindow = lookAhead,
                Confidence = 0,
                Reason = "No metrics history available"
            };
        }

        List<TimestampedResourceMetrics> history;
        lock (series)
        {
            history = series.MetricsHistory.ToList();
        }

        if (history.Count < _config.MinDataPointsForForecast)
        {
            return new ScalingForecast
            {
                ResourceId = resourceId,
                ForecastWindow = lookAhead,
                Confidence = 0,
                Reason = $"Insufficient data ({history.Count} points, need {_config.MinDataPointsForForecast})"
            };
        }

        // Statistical forecast
        var statisticalForecast = ComputeStatisticalForecast(history, lookAhead);

        // If high confidence or AI not needed, return statistical
        if (statisticalForecast.Confidence > _config.HighConfidenceThreshold)
        {
            return statisticalForecast;
        }

        // Enhance with AI for complex patterns
        try
        {
            var aiEnhanced = await GetAIEnhancedForecastAsync(resourceId, history, statisticalForecast, lookAhead, ct);
            return aiEnhanced;
        }
        catch
        {
            return statisticalForecast;
        }
    }

    /// <summary>
    /// Gets scaling recommendation based on forecast.
    /// </summary>
    public async Task<ScalingRecommendation> GetScalingRecommendationAsync(
        string resourceId,
        ResourceCapacity currentCapacity,
        CancellationToken ct = default)
    {
        var forecast = await ForecastAsync(resourceId, TimeSpan.FromHours(_config.ForecastHoursAhead), ct);

        if (forecast.Confidence < _config.MinForecastConfidenceForScaling)
        {
            return new ScalingRecommendation
            {
                ResourceId = resourceId,
                Action = ScalingAction.NoAction,
                Confidence = forecast.Confidence,
                Reason = "Forecast confidence too low for scaling decision"
            };
        }

        // Calculate required capacity
        var peakCpu = forecast.PeakCpuForecast;
        var peakMemory = forecast.PeakMemoryForecast;
        var peakConnections = forecast.PeakConnectionsForecast;

        // Buffer for safety
        var cpuWithBuffer = peakCpu * (1 + _config.SafetyBufferPercent / 100);
        var memoryWithBuffer = peakMemory * (1 + _config.SafetyBufferPercent / 100);

        ScalingAction action;
        double targetCapacity;
        string reason;

        if (cpuWithBuffer > currentCapacity.MaxCpu * _config.ScaleUpThreshold ||
            memoryWithBuffer > currentCapacity.MaxMemoryGB * _config.ScaleUpThreshold)
        {
            action = ScalingAction.ScaleUp;
            targetCapacity = Math.Max(cpuWithBuffer / _config.ScaleUpThreshold, memoryWithBuffer / _config.ScaleUpThreshold);
            reason = $"Forecasted peak ({peakCpu:F0}% CPU, {peakMemory:F1}GB memory) exceeds scale-up threshold";
        }
        else if (cpuWithBuffer < currentCapacity.MaxCpu * _config.ScaleDownThreshold &&
                 memoryWithBuffer < currentCapacity.MaxMemoryGB * _config.ScaleDownThreshold)
        {
            action = ScalingAction.ScaleDown;
            targetCapacity = Math.Max(cpuWithBuffer / _config.ScaleDownThreshold, memoryWithBuffer / _config.ScaleDownThreshold);
            reason = $"Forecasted peak ({peakCpu:F0}% CPU, {peakMemory:F1}GB memory) below scale-down threshold";
        }
        else
        {
            action = ScalingAction.NoAction;
            targetCapacity = currentCapacity.MaxCpu;
            reason = "Current capacity appropriate for forecasted demand";
        }

        return new ScalingRecommendation
        {
            ResourceId = resourceId,
            Action = action,
            CurrentCapacity = currentCapacity,
            RecommendedCapacity = CalculateRecommendedCapacity(action, currentCapacity, targetCapacity),
            Forecast = forecast,
            Confidence = forecast.Confidence,
            Reason = reason,
            EstimatedCostImpact = CalculateCostImpact(action, currentCapacity, targetCapacity)
        };
    }

    /// <summary>
    /// Executes a scaling action.
    /// </summary>
    public async Task<ScalingResult> ExecuteScalingAsync(
        string resourceId,
        ScalingAction action,
        ResourceCapacity targetCapacity,
        CancellationToken ct = default)
    {
        if (!await _scalingLock.WaitAsync(TimeSpan.FromSeconds(30), ct))
        {
            return new ScalingResult
            {
                ResourceId = resourceId,
                Success = false,
                Reason = "Scaling operation queue full"
            };
        }

        try
        {
            var startTime = DateTime.UtcNow;

            // Record scaling event
            var scalingEvent = new ScalingEvent
            {
                ResourceId = resourceId,
                Timestamp = startTime,
                Action = action,
                TargetCapacity = targetCapacity
            };

            _scalingHistory.Enqueue(scalingEvent);

            // Trim history
            while (_scalingHistory.Count > 1000)
            {
                _scalingHistory.TryDequeue(out _);
            }

            // Actual scaling would be performed by cloud provider integration
            // This is where you would call AWS Auto Scaling, Azure VMSS, etc.
            await Task.Delay(100, ct); // Simulated scaling delay

            return new ScalingResult
            {
                ResourceId = resourceId,
                Success = true,
                Action = action,
                NewCapacity = targetCapacity,
                ExecutionTime = DateTime.UtcNow - startTime,
                Reason = $"Successfully executed {action}"
            };
        }
        finally
        {
            _scalingLock.Release();
        }
    }

    /// <summary>
    /// Runs a forecast cycle for all tracked resources.
    /// </summary>
    public async Task<ForecastCycleResult> ForecastCycleAsync(CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;
        var forecasts = new List<ScalingForecast>();
        var alerts = new List<ScalingAlert>();

        foreach (var resourceId in _metricsSeries.Keys.Take(_config.MaxResourcesPerCycle))
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var forecast = await ForecastAsync(resourceId, TimeSpan.FromHours(_config.ForecastHoursAhead), ct);
                forecasts.Add(forecast);

                // Generate alerts for capacity concerns
                if (forecast.Confidence > 0.7)
                {
                    if (forecast.PeakCpuForecast > 90)
                    {
                        alerts.Add(new ScalingAlert
                        {
                            ResourceId = resourceId,
                            AlertType = ScalingAlertType.HighCpuForecast,
                            Severity = forecast.PeakCpuForecast > 95 ? AlertSeverity.Critical : AlertSeverity.Warning,
                            Message = $"CPU forecast to reach {forecast.PeakCpuForecast:F0}%",
                            ForecastedValue = forecast.PeakCpuForecast
                        });
                    }

                    if (forecast.PeakMemoryForecast > 90)
                    {
                        alerts.Add(new ScalingAlert
                        {
                            ResourceId = resourceId,
                            AlertType = ScalingAlertType.HighMemoryForecast,
                            Severity = forecast.PeakMemoryForecast > 95 ? AlertSeverity.Critical : AlertSeverity.Warning,
                            Message = $"Memory forecast to reach {forecast.PeakMemoryForecast:F0}%",
                            ForecastedValue = forecast.PeakMemoryForecast
                        });
                    }
                }
            }
            catch
            {
                // Continue with other resources
            }
        }

        return new ForecastCycleResult
        {
            StartTime = startTime,
            EndTime = DateTime.UtcNow,
            Forecasts = forecasts,
            Alerts = alerts
        };
    }

    /// <summary>
    /// Gets scaling statistics.
    /// </summary>
    public ScalingStatistics GetStatistics()
    {
        var recentEvents = _scalingHistory.TakeLast(100).ToList();

        return new ScalingStatistics
        {
            TrackedResources = _metricsSeries.Count,
            TotalScalingEvents = _scalingHistory.Count,
            ScaleUpEvents = recentEvents.Count(e => e.Action == ScalingAction.ScaleUp),
            ScaleDownEvents = recentEvents.Count(e => e.Action == ScalingAction.ScaleDown),
            RecentEvents = recentEvents
        };
    }

    private ScalingForecast ComputeStatisticalForecast(List<TimestampedResourceMetrics> history, TimeSpan lookAhead)
    {
        var cpuValues = history.Select(h => h.Metrics.CpuPercent).ToList();
        var memoryValues = history.Select(h => h.Metrics.MemoryPercent).ToList();
        var connectionValues = history.Select(h => (double)h.Metrics.ActiveConnections).ToList();

        // Calculate trends
        var cpuTrend = CalculateTrend(cpuValues);
        var memoryTrend = CalculateTrend(memoryValues);

        // Forecast peak values
        var hoursAhead = lookAhead.TotalHours;
        var recentCpu = cpuValues.TakeLast(10).Average();
        var recentMemory = memoryValues.TakeLast(10).Average();
        var recentConnections = connectionValues.TakeLast(10).Average();

        // Apply trend and add buffer for peak
        var peakCpu = Math.Min(100, recentCpu + cpuTrend * hoursAhead + cpuValues.TakeLast(24).Max() - recentCpu);
        var peakMemory = Math.Min(100, recentMemory + memoryTrend * hoursAhead + memoryValues.TakeLast(24).Max() - recentMemory);
        var peakConnections = (int)(recentConnections * 1.5);

        // Calculate confidence based on data quality
        var confidence = Math.Min(1.0, history.Count / 200.0);

        // Adjust confidence based on variance
        var cpuVariance = CalculateVariance(cpuValues);
        var varianceAdjustment = Math.Max(0.5, 1 - cpuVariance / 100);
        confidence *= varianceAdjustment;

        return new ScalingForecast
        {
            ResourceId = history.First().Metrics.ResourceId ?? "unknown",
            ForecastWindow = lookAhead,
            ForecastTime = DateTime.UtcNow,
            PeakCpuForecast = peakCpu,
            PeakMemoryForecast = peakMemory,
            PeakConnectionsForecast = peakConnections,
            CpuTrend = cpuTrend > 0.5 ? MetricTrend.Increasing : cpuTrend < -0.5 ? MetricTrend.Decreasing : MetricTrend.Stable,
            MemoryTrend = memoryTrend > 0.5 ? MetricTrend.Increasing : memoryTrend < -0.5 ? MetricTrend.Decreasing : MetricTrend.Stable,
            Confidence = confidence,
            Source = ForecastSource.Statistical,
            Reason = "Based on statistical analysis of historical metrics"
        };
    }

    private async Task<ScalingForecast> GetAIEnhancedForecastAsync(
        string resourceId,
        List<TimestampedResourceMetrics> history,
        ScalingForecast statisticalForecast,
        TimeSpan lookAhead,
        CancellationToken ct)
    {
        // Prepare summary of recent metrics
        var recentMetrics = history.TakeLast(24).ToList();
        var metricsJson = JsonSerializer.Serialize(recentMetrics.Select(m => new
        {
            time = m.Timestamp.ToString("HH:mm"),
            cpu = m.Metrics.CpuPercent,
            memory = m.Metrics.MemoryPercent,
            connections = m.Metrics.ActiveConnections
        }));

        var prompt = $@"Analyze the following resource metrics and provide a capacity forecast for the next {lookAhead.TotalHours} hours:

Resource ID: {resourceId}
Statistical Forecast: CPU {statisticalForecast.PeakCpuForecast:F1}%, Memory {statisticalForecast.PeakMemoryForecast:F1}%
Statistical Confidence: {statisticalForecast.Confidence:P1}

Recent Metrics (last 24 data points):
{metricsJson}

Consider:
1. Time-of-day patterns
2. Trend acceleration/deceleration
3. Anomalous spikes that may recur
4. Seasonal patterns if visible

Respond with JSON:
{{
  ""peakCpu"": <predicted peak CPU %>,
  ""peakMemory"": <predicted peak memory %>,
  ""peakConnections"": <predicted peak connections>,
  ""confidence"": <0-1>,
  ""reasoning"": ""<explanation>"",
  ""riskFactors"": [""<risk1>"", ""<risk2>""]
}}";

        var response = await _providerSelector.ExecuteWithFailoverAsync(
            AIOpsCapabilities.PredictiveScaling,
            prompt,
            null,
            ct);

        if (response.Success && !string.IsNullOrEmpty(response.Content))
        {
            try
            {
                var jsonStart = response.Content.IndexOf('{');
                var jsonEnd = response.Content.LastIndexOf('}');

                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var json = response.Content.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    var aiCpu = root.GetProperty("peakCpu").GetDouble();
                    var aiMemory = root.GetProperty("peakMemory").GetDouble();
                    var aiConnections = root.TryGetProperty("peakConnections", out var conn) ? conn.GetInt32() : statisticalForecast.PeakConnectionsForecast;
                    var aiConfidence = root.GetProperty("confidence").GetDouble();
                    var reasoning = root.TryGetProperty("reasoning", out var r) ? r.GetString() : "";

                    // Blend AI and statistical forecasts
                    var blendedCpu = (statisticalForecast.PeakCpuForecast + aiCpu) / 2;
                    var blendedMemory = (statisticalForecast.PeakMemoryForecast + aiMemory) / 2;
                    var blendedConfidence = (statisticalForecast.Confidence + aiConfidence) / 2;

                    return new ScalingForecast
                    {
                        ResourceId = resourceId,
                        ForecastWindow = lookAhead,
                        ForecastTime = DateTime.UtcNow,
                        PeakCpuForecast = blendedCpu,
                        PeakMemoryForecast = blendedMemory,
                        PeakConnectionsForecast = aiConnections,
                        CpuTrend = statisticalForecast.CpuTrend,
                        MemoryTrend = statisticalForecast.MemoryTrend,
                        Confidence = blendedConfidence,
                        Source = ForecastSource.AIEnhanced,
                        Reason = reasoning ?? "AI-enhanced forecast"
                    };
                }
            }
            catch
            {
                // Fall through
            }
        }

        return statisticalForecast;
    }

    private double CalculateTrend(List<double> values)
    {
        if (values.Count < 2) return 0;

        var n = values.Count;
        var sumX = Enumerable.Range(0, n).Sum();
        var sumY = values.Sum();
        var sumXY = Enumerable.Range(0, n).Select(i => i * values[i]).Sum();
        var sumX2 = Enumerable.Range(0, n).Select(i => i * i).Sum();

        return (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);
    }

    private double CalculateVariance(List<double> values)
    {
        if (values.Count < 2) return 0;
        var mean = values.Average();
        return values.Sum(v => Math.Pow(v - mean, 2)) / values.Count;
    }

    private ResourceCapacity CalculateRecommendedCapacity(ScalingAction action, ResourceCapacity current, double target)
    {
        return action switch
        {
            ScalingAction.ScaleUp => new ResourceCapacity
            {
                MaxCpu = (int)Math.Ceiling(current.MaxCpu * 1.5),
                MaxMemoryGB = current.MaxMemoryGB * 1.5,
                MaxConnections = current.MaxConnections + 1000
            },
            ScalingAction.ScaleDown => new ResourceCapacity
            {
                MaxCpu = Math.Max(1, (int)(current.MaxCpu * 0.7)),
                MaxMemoryGB = Math.Max(1, current.MaxMemoryGB * 0.7),
                MaxConnections = Math.Max(100, current.MaxConnections - 500)
            },
            _ => current
        };
    }

    private double CalculateCostImpact(ScalingAction action, ResourceCapacity current, double target)
    {
        return action switch
        {
            ScalingAction.ScaleUp => target * 0.1,  // Estimated cost increase per unit
            ScalingAction.ScaleDown => -target * 0.08, // Estimated cost savings per unit
            _ => 0
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _forecastTimer.Dispose();
        _scalingLock.Dispose();
    }
}

#region Supporting Types

public sealed class ScalingEngineConfig
{
    public int MetricsWindowSize { get; set; } = 1000;
    public int ForecastIntervalMinutes { get; set; } = 5;
    public int ForecastHoursAhead { get; set; } = 24;
    public int MinDataPointsForForecast { get; set; } = 30;
    public double HighConfidenceThreshold { get; set; } = 0.85;
    public double MinForecastConfidenceForScaling { get; set; } = 0.7;
    public double ScaleUpThreshold { get; set; } = 0.8;
    public double ScaleDownThreshold { get; set; } = 0.3;
    public double SafetyBufferPercent { get; set; } = 20;
    public int MaxResourcesPerCycle { get; set; } = 50;
    public int MaxConcurrentScalingOps { get; set; } = 5;
}

public sealed class ResourceMetrics
{
    public string? ResourceId { get; init; }
    public double CpuPercent { get; init; }
    public double MemoryPercent { get; init; }
    public double DiskPercent { get; init; }
    public double NetworkInMbps { get; init; }
    public double NetworkOutMbps { get; init; }
    public int ActiveConnections { get; init; }
    public int RequestsPerSecond { get; init; }
    public double LatencyMs { get; init; }
}

public sealed class TimestampedResourceMetrics
{
    public DateTime Timestamp { get; init; }
    public ResourceMetrics Metrics { get; init; } = new();
}

public sealed class ResourceMetricsSeries
{
    public string ResourceId { get; init; } = string.Empty;
    public int WindowSize { get; init; } = 1000;
    public List<TimestampedResourceMetrics> MetricsHistory { get; } = new();

    public void AddMetrics(ResourceMetrics metrics)
    {
        MetricsHistory.Add(new TimestampedResourceMetrics
        {
            Timestamp = DateTime.UtcNow,
            Metrics = metrics
        });

        while (MetricsHistory.Count > WindowSize)
        {
            MetricsHistory.RemoveAt(0);
        }
    }
}

public sealed class ResourceCapacity
{
    public int MaxCpu { get; init; }
    public double MaxMemoryGB { get; init; }
    public int MaxConnections { get; init; }
}

public enum ScalingAction
{
    NoAction,
    ScaleUp,
    ScaleDown,
    ScaleOut,
    ScaleIn
}

public enum MetricTrend
{
    Decreasing,
    Stable,
    Increasing
}

public enum ForecastSource
{
    Statistical,
    AIEnhanced,
    RuleBased
}

public sealed class ScalingForecast
{
    public string ResourceId { get; init; } = string.Empty;
    public TimeSpan ForecastWindow { get; init; }
    public DateTime ForecastTime { get; init; }
    public double PeakCpuForecast { get; init; }
    public double PeakMemoryForecast { get; init; }
    public int PeakConnectionsForecast { get; init; }
    public MetricTrend CpuTrend { get; init; }
    public MetricTrend MemoryTrend { get; init; }
    public double Confidence { get; init; }
    public ForecastSource Source { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class ScalingRecommendation
{
    public string ResourceId { get; init; } = string.Empty;
    public ScalingAction Action { get; init; }
    public ResourceCapacity? CurrentCapacity { get; init; }
    public ResourceCapacity? RecommendedCapacity { get; init; }
    public ScalingForecast? Forecast { get; init; }
    public double Confidence { get; init; }
    public string Reason { get; init; } = string.Empty;
    public double EstimatedCostImpact { get; init; }
}

public sealed class ScalingResult
{
    public string ResourceId { get; init; } = string.Empty;
    public bool Success { get; init; }
    public ScalingAction Action { get; init; }
    public ResourceCapacity? NewCapacity { get; init; }
    public TimeSpan ExecutionTime { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class ScalingEvent
{
    public string ResourceId { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public ScalingAction Action { get; init; }
    public ResourceCapacity? TargetCapacity { get; init; }
}

public sealed class ForecastCycleResult
{
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public List<ScalingForecast> Forecasts { get; init; } = new();
    public List<ScalingAlert> Alerts { get; init; } = new();
}

public sealed class ScalingAlert
{
    public string ResourceId { get; init; } = string.Empty;
    public ScalingAlertType AlertType { get; init; }
    public AlertSeverity Severity { get; init; }
    public string Message { get; init; } = string.Empty;
    public double ForecastedValue { get; init; }
}

public enum ScalingAlertType
{
    HighCpuForecast,
    HighMemoryForecast,
    HighConnectionsForecast,
    CapacityExhaustion
}

public enum AlertSeverity
{
    Info,
    Warning,
    Critical
}

public sealed class ScalingStatistics
{
    public int TrackedResources { get; init; }
    public int TotalScalingEvents { get; init; }
    public int ScaleUpEvents { get; init; }
    public int ScaleDownEvents { get; init; }
    public List<ScalingEvent> RecentEvents { get; init; } = new();
}

#endregion
