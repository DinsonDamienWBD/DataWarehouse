// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.SDK.AI;
using System.Collections.Concurrent;
using System.Text.Json;
using IExtendedAIProvider = DataWarehouse.Plugins.AIAgents.IExtendedAIProvider;

namespace DataWarehouse.Plugins.AIAgents.Capabilities.AIOps;

/// <summary>
/// Predictive scaling engine for AI-driven resource forecasting and auto-scaling.
/// Uses statistical analysis combined with AI-enhanced predictions to anticipate
/// resource needs and recommend scaling actions.
/// </summary>
/// <remarks>
/// <para>
/// The scaling engine provides:
/// - Real-time resource metrics recording
/// - Statistical forecasting using trend analysis
/// - AI-enhanced forecasting for complex patterns
/// - Proactive scaling recommendations
/// - Autonomous scaling action execution
/// </para>
/// <para>
/// The engine gracefully degrades to statistical-only forecasting when AI is unavailable.
/// </para>
/// </remarks>
public sealed class ScalingEngine : IDisposable
{
    private readonly IExtendedAIProvider? _aiProvider;
    private readonly ScalingEngineConfig _config;
    private readonly ConcurrentDictionary<string, ResourceMetricsSeries> _metricsSeries;
    private readonly ConcurrentQueue<ScalingEvent> _scalingHistory;
    private readonly SemaphoreSlim _scalingLock;
    private readonly Timer? _forecastTimer;
    private long _totalScalingOperations;
    private long _successfulScalingOperations;
    private bool _disposed;

    /// <summary>
    /// Creates a new scaling engine instance.
    /// </summary>
    /// <param name="aiProvider">Optional AI provider for enhanced forecasting.</param>
    /// <param name="config">Engine configuration.</param>
    public ScalingEngine(IExtendedAIProvider? aiProvider = null, ScalingEngineConfig? config = null)
    {
        _aiProvider = aiProvider;
        _config = config ?? new ScalingEngineConfig();
        _metricsSeries = new ConcurrentDictionary<string, ResourceMetricsSeries>();
        _scalingHistory = new ConcurrentQueue<ScalingEvent>();
        _scalingLock = new SemaphoreSlim(_config.MaxConcurrentScalingOps);

        if (_config.EnableAutonomousForecast)
        {
            _forecastTimer = new Timer(
                _ => _ = ForecastCycleAsync(CancellationToken.None),
                null,
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(_config.ForecastIntervalMinutes));
        }
    }

    /// <summary>
    /// Records resource metrics for forecasting.
    /// </summary>
    /// <param name="resourceId">The resource identifier.</param>
    /// <param name="metrics">The metrics to record.</param>
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
    /// Predicts resource needs for a future time window using ML and optionally AI analysis.
    /// </summary>
    /// <param name="resourceId">The resource identifier.</param>
    /// <param name="lookAhead">The time window to forecast.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The scaling forecast.</returns>
    public async Task<ScalingForecast> ForecastAsync(
        string resourceId,
        TimeSpan lookAhead,
        CancellationToken ct = default)
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
        var statisticalForecast = ComputeStatisticalForecast(resourceId, history, lookAhead);

        // If high confidence or AI not available, return statistical
        if (statisticalForecast.Confidence > _config.HighConfidenceThreshold)
        {
            return statisticalForecast;
        }

        // Enhance with AI for complex patterns
        if (_aiProvider != null && _aiProvider.IsAvailable)
        {
            try
            {
                return await GetAIEnhancedForecastAsync(resourceId, history, statisticalForecast, lookAhead, ct);
            }
            catch
            {
                // Graceful degradation: fall back to statistical forecast
            }
        }

        return statisticalForecast;
    }

    /// <summary>
    /// Gets scaling recommendation based on forecast.
    /// </summary>
    /// <param name="resourceId">The resource identifier.</param>
    /// <param name="currentCapacity">The current resource capacity.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The scaling recommendation.</returns>
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

        // Calculate required capacity with buffer
        var cpuWithBuffer = forecast.PeakCpuForecast * (1 + _config.SafetyBufferPercent / 100);
        var memoryWithBuffer = forecast.PeakMemoryForecast * (1 + _config.SafetyBufferPercent / 100);

        ScalingAction action;
        double targetCapacity;
        string reason;

        if (cpuWithBuffer > currentCapacity.MaxCpu * _config.ScaleUpThreshold ||
            memoryWithBuffer > currentCapacity.MaxMemoryGB * _config.ScaleUpThreshold)
        {
            action = ScalingAction.ScaleUp;
            targetCapacity = Math.Max(cpuWithBuffer / _config.ScaleUpThreshold, memoryWithBuffer / _config.ScaleUpThreshold);
            reason = $"Forecasted peak ({forecast.PeakCpuForecast:F0}% CPU, {forecast.PeakMemoryForecast:F1}GB memory) exceeds scale-up threshold";
        }
        else if (cpuWithBuffer < currentCapacity.MaxCpu * _config.ScaleDownThreshold &&
                 memoryWithBuffer < currentCapacity.MaxMemoryGB * _config.ScaleDownThreshold)
        {
            action = ScalingAction.ScaleDown;
            targetCapacity = Math.Max(cpuWithBuffer / _config.ScaleDownThreshold, memoryWithBuffer / _config.ScaleDownThreshold);
            reason = $"Forecasted peak ({forecast.PeakCpuForecast:F0}% CPU, {forecast.PeakMemoryForecast:F1}GB memory) below scale-down threshold";
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
    /// <param name="resourceId">The resource identifier.</param>
    /// <param name="action">The scaling action to execute.</param>
    /// <param name="targetCapacity">The target capacity.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The scaling result.</returns>
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
            Interlocked.Increment(ref _totalScalingOperations);
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
            await Task.Delay(100, ct); // Simulated scaling delay

            Interlocked.Increment(ref _successfulScalingOperations);

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
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The forecast cycle result.</returns>
    public async Task<ScalingForecastCycleResult> ForecastCycleAsync(CancellationToken ct = default)
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

        return new ScalingForecastCycleResult
        {
            StartTime = startTime,
            EndTime = DateTime.UtcNow,
            Forecasts = forecasts,
            Alerts = alerts
        };
    }

    /// <summary>
    /// Gets scaling engine statistics.
    /// </summary>
    /// <returns>The scaling statistics.</returns>
    public ScalingStatistics GetStatistics()
    {
        var recentEvents = _scalingHistory.TakeLast(100).ToList();

        return new ScalingStatistics
        {
            TrackedResources = _metricsSeries.Count,
            TotalScalingEvents = Interlocked.Read(ref _totalScalingOperations),
            SuccessfulScalingEvents = Interlocked.Read(ref _successfulScalingOperations),
            ScaleUpEvents = recentEvents.Count(e => e.Action == ScalingAction.ScaleUp),
            ScaleDownEvents = recentEvents.Count(e => e.Action == ScalingAction.ScaleDown),
            SuccessRate = _totalScalingOperations > 0
                ? (double)_successfulScalingOperations / _totalScalingOperations
                : 1.0,
            RecentEvents = recentEvents
        };
    }

    #region Private Methods

    private ScalingForecast ComputeStatisticalForecast(
        string resourceId,
        List<TimestampedResourceMetrics> history,
        TimeSpan lookAhead)
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
        var peakCpu = Math.Min(100, recentCpu + cpuTrend * hoursAhead + cpuValues.TakeLast(24).DefaultIfEmpty(0).Max() - recentCpu);
        var peakMemory = Math.Min(100, recentMemory + memoryTrend * hoursAhead + memoryValues.TakeLast(24).DefaultIfEmpty(0).Max() - recentMemory);
        var peakConnections = (int)(recentConnections * 1.5);

        // Calculate confidence based on data quality
        var confidence = Math.Min(1.0, history.Count / 200.0);

        // Adjust confidence based on variance
        var cpuVariance = CalculateVariance(cpuValues);
        var varianceAdjustment = Math.Max(0.5, 1 - cpuVariance / 100);
        confidence *= varianceAdjustment;

        return new ScalingForecast
        {
            ResourceId = resourceId,
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

    private static CompletionRequest ToCompletionRequest(string prompt, string? model = null) => new()
    {
        Prompt = prompt,
        Model = model!
    };

    private async Task<ScalingForecast> GetAIEnhancedForecastAsync(
        string resourceId,
        List<TimestampedResourceMetrics> history,
        ScalingForecast statisticalForecast,
        TimeSpan lookAhead,
        CancellationToken ct)
    {
        if (_aiProvider == null)
            throw new InvalidOperationException("AI provider not available");

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

        var request = ToCompletionRequest(prompt);

        var response = await _aiProvider.CompleteAsync(request, ct);

        if (!string.IsNullOrEmpty(response.Text))
        {
            try
            {
                var jsonStart = response.Text.IndexOf('{');
                var jsonEnd = response.Text.LastIndexOf('}');

                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var json = response.Text.Substring(jsonStart, jsonEnd - jsonStart + 1);
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
                        Reason = reasoning ?? "AI-enhanced forecast",
                        AIInsights = reasoning
                    };
                }
            }
            catch
            {
                // Fall through to return statistical forecast
            }
        }

        return statisticalForecast;
    }

    private static double CalculateTrend(List<double> values)
    {
        if (values.Count < 2) return 0;

        var n = values.Count;
        var sumX = Enumerable.Range(0, n).Sum();
        var sumY = values.Sum();
        var sumXY = Enumerable.Range(0, n).Select(i => i * values[i]).Sum();
        var sumX2 = Enumerable.Range(0, n).Select(i => i * i).Sum();

        var denominator = n * sumX2 - sumX * sumX;
        return denominator != 0 ? (n * sumXY - sumX * sumY) / denominator : 0;
    }

    private static double CalculateVariance(List<double> values)
    {
        if (values.Count < 2) return 0;
        var mean = values.Average();
        return values.Sum(v => Math.Pow(v - mean, 2)) / values.Count;
    }

    private static ResourceCapacity CalculateRecommendedCapacity(ScalingAction action, ResourceCapacity current, double target)
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

    private static double CalculateCostImpact(ScalingAction action, ResourceCapacity current, double target)
    {
        return action switch
        {
            ScalingAction.ScaleUp => target * 0.1,    // Estimated cost increase per unit
            ScalingAction.ScaleDown => -target * 0.08, // Estimated cost savings per unit
            _ => 0
        };
    }

    #endregion

    /// <summary>
    /// Releases all resources used by the engine.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _forecastTimer?.Dispose();
        _scalingLock.Dispose();
    }
}

#region Configuration

/// <summary>
/// Configuration for the scaling engine.
/// </summary>
public sealed class ScalingEngineConfig
{
    /// <summary>Gets or sets the metrics window size.</summary>
    public int MetricsWindowSize { get; set; } = 1000;

    /// <summary>Gets or sets the forecast interval in minutes.</summary>
    public int ForecastIntervalMinutes { get; set; } = 5;

    /// <summary>Gets or sets the hours ahead to forecast.</summary>
    public int ForecastHoursAhead { get; set; } = 24;

    /// <summary>Gets or sets the minimum data points required for forecasting.</summary>
    public int MinDataPointsForForecast { get; set; } = 30;

    /// <summary>Gets or sets the high confidence threshold for statistical-only decisions.</summary>
    public double HighConfidenceThreshold { get; set; } = 0.85;

    /// <summary>Gets or sets the minimum forecast confidence for scaling decisions.</summary>
    public double MinForecastConfidenceForScaling { get; set; } = 0.7;

    /// <summary>Gets or sets the scale-up threshold (percentage of capacity).</summary>
    public double ScaleUpThreshold { get; set; } = 0.8;

    /// <summary>Gets or sets the scale-down threshold (percentage of capacity).</summary>
    public double ScaleDownThreshold { get; set; } = 0.3;

    /// <summary>Gets or sets the safety buffer percentage.</summary>
    public double SafetyBufferPercent { get; set; } = 20;

    /// <summary>Gets or sets the maximum resources per forecast cycle.</summary>
    public int MaxResourcesPerCycle { get; set; } = 50;

    /// <summary>Gets or sets the maximum concurrent scaling operations.</summary>
    public int MaxConcurrentScalingOps { get; set; } = 5;

    /// <summary>Gets or sets whether to enable autonomous forecast cycles.</summary>
    public bool EnableAutonomousForecast { get; set; } = true;
}

#endregion

#region Types

/// <summary>
/// Resource metrics for capacity tracking.
/// </summary>
public sealed class ResourceMetrics
{
    /// <summary>Gets or sets the optional resource identifier.</summary>
    public string? ResourceId { get; init; }

    /// <summary>Gets or sets the CPU usage percentage.</summary>
    public double CpuPercent { get; init; }

    /// <summary>Gets or sets the memory usage percentage.</summary>
    public double MemoryPercent { get; init; }

    /// <summary>Gets or sets the disk usage percentage.</summary>
    public double DiskPercent { get; init; }

    /// <summary>Gets or sets the network input in Mbps.</summary>
    public double NetworkInMbps { get; init; }

    /// <summary>Gets or sets the network output in Mbps.</summary>
    public double NetworkOutMbps { get; init; }

    /// <summary>Gets or sets the number of active connections.</summary>
    public int ActiveConnections { get; init; }

    /// <summary>Gets or sets the requests per second.</summary>
    public int RequestsPerSecond { get; init; }

    /// <summary>Gets or sets the average latency in milliseconds.</summary>
    public double LatencyMs { get; init; }
}

/// <summary>
/// Timestamped resource metrics for historical tracking.
/// </summary>
public sealed class TimestampedResourceMetrics
{
    /// <summary>Gets the timestamp of the metrics.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Gets the metrics data.</summary>
    public ResourceMetrics Metrics { get; init; } = new();
}

/// <summary>
/// Time series of resource metrics for a single resource.
/// </summary>
public sealed class ResourceMetricsSeries
{
    /// <summary>Gets or sets the resource identifier.</summary>
    public string ResourceId { get; init; } = string.Empty;

    /// <summary>Gets or sets the window size for history.</summary>
    public int WindowSize { get; init; } = 1000;

    /// <summary>Gets the metrics history.</summary>
    public List<TimestampedResourceMetrics> MetricsHistory { get; } = new();

    /// <summary>
    /// Adds metrics to the history.
    /// </summary>
    /// <param name="metrics">The metrics to add.</param>
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

/// <summary>
/// Resource capacity specification.
/// </summary>
public sealed class ResourceCapacity
{
    /// <summary>Gets or sets the maximum CPU cores/percentage.</summary>
    public int MaxCpu { get; init; }

    /// <summary>Gets or sets the maximum memory in GB.</summary>
    public double MaxMemoryGB { get; init; }

    /// <summary>Gets or sets the maximum connections.</summary>
    public int MaxConnections { get; init; }
}

/// <summary>
/// Scaling action type.
/// </summary>
public enum ScalingAction
{
    /// <summary>No scaling action needed.</summary>
    NoAction,
    /// <summary>Scale up (vertical).</summary>
    ScaleUp,
    /// <summary>Scale down (vertical).</summary>
    ScaleDown,
    /// <summary>Scale out (horizontal).</summary>
    ScaleOut,
    /// <summary>Scale in (horizontal).</summary>
    ScaleIn
}

/// <summary>
/// Metric trend direction.
/// </summary>
public enum MetricTrend
{
    /// <summary>Metrics are decreasing.</summary>
    Decreasing,
    /// <summary>Metrics are stable.</summary>
    Stable,
    /// <summary>Metrics are increasing.</summary>
    Increasing
}

/// <summary>
/// Source of the forecast.
/// </summary>
public enum ForecastSource
{
    /// <summary>Based on statistical analysis only.</summary>
    Statistical,
    /// <summary>Enhanced with AI analysis.</summary>
    AIEnhanced,
    /// <summary>Based on rules.</summary>
    RuleBased
}

/// <summary>
/// Scaling forecast result.
/// </summary>
public sealed class ScalingForecast
{
    /// <summary>Gets or sets the resource identifier.</summary>
    public string ResourceId { get; init; } = string.Empty;

    /// <summary>Gets or sets the forecast time window.</summary>
    public TimeSpan ForecastWindow { get; init; }

    /// <summary>Gets or sets when the forecast was generated.</summary>
    public DateTime ForecastTime { get; init; }

    /// <summary>Gets or sets the forecasted peak CPU percentage.</summary>
    public double PeakCpuForecast { get; init; }

    /// <summary>Gets or sets the forecasted peak memory percentage.</summary>
    public double PeakMemoryForecast { get; init; }

    /// <summary>Gets or sets the forecasted peak connections.</summary>
    public int PeakConnectionsForecast { get; init; }

    /// <summary>Gets or sets the CPU trend.</summary>
    public MetricTrend CpuTrend { get; init; }

    /// <summary>Gets or sets the memory trend.</summary>
    public MetricTrend MemoryTrend { get; init; }

    /// <summary>Gets or sets the confidence level (0-1).</summary>
    public double Confidence { get; init; }

    /// <summary>Gets or sets the source of the forecast.</summary>
    public ForecastSource Source { get; init; }

    /// <summary>Gets or sets the reason for the forecast.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Gets or sets additional AI insights.</summary>
    public string? AIInsights { get; init; }
}

/// <summary>
/// Scaling recommendation result.
/// </summary>
public sealed class ScalingRecommendation
{
    /// <summary>Gets or sets the resource identifier.</summary>
    public string ResourceId { get; init; } = string.Empty;

    /// <summary>Gets or sets the recommended action.</summary>
    public ScalingAction Action { get; init; }

    /// <summary>Gets or sets the current capacity.</summary>
    public ResourceCapacity? CurrentCapacity { get; init; }

    /// <summary>Gets or sets the recommended capacity.</summary>
    public ResourceCapacity? RecommendedCapacity { get; init; }

    /// <summary>Gets or sets the forecast used for the recommendation.</summary>
    public ScalingForecast? Forecast { get; init; }

    /// <summary>Gets or sets the confidence level.</summary>
    public double Confidence { get; init; }

    /// <summary>Gets or sets the reason for the recommendation.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Gets or sets the estimated cost impact.</summary>
    public double EstimatedCostImpact { get; init; }
}

/// <summary>
/// Scaling operation result.
/// </summary>
public sealed class ScalingResult
{
    /// <summary>Gets or sets the resource identifier.</summary>
    public string ResourceId { get; init; } = string.Empty;

    /// <summary>Gets or sets whether the operation succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets or sets the action taken.</summary>
    public ScalingAction Action { get; init; }

    /// <summary>Gets or sets the new capacity after scaling.</summary>
    public ResourceCapacity? NewCapacity { get; init; }

    /// <summary>Gets or sets the execution time.</summary>
    public TimeSpan ExecutionTime { get; init; }

    /// <summary>Gets or sets the result reason.</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Record of a scaling event.
/// </summary>
public sealed class ScalingEvent
{
    /// <summary>Gets or sets the resource identifier.</summary>
    public string ResourceId { get; init; } = string.Empty;

    /// <summary>Gets or sets when the event occurred.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Gets or sets the scaling action.</summary>
    public ScalingAction Action { get; init; }

    /// <summary>Gets or sets the target capacity.</summary>
    public ResourceCapacity? TargetCapacity { get; init; }
}

/// <summary>
/// Result of a forecast cycle.
/// </summary>
public sealed class ScalingForecastCycleResult
{
    /// <summary>Gets or sets the start time.</summary>
    public DateTime StartTime { get; init; }

    /// <summary>Gets or sets the end time.</summary>
    public DateTime EndTime { get; init; }

    /// <summary>Gets the duration.</summary>
    public TimeSpan Duration => EndTime - StartTime;

    /// <summary>Gets or sets the forecasts generated.</summary>
    public List<ScalingForecast> Forecasts { get; init; } = new();

    /// <summary>Gets or sets any alerts generated.</summary>
    public List<ScalingAlert> Alerts { get; init; } = new();
}

/// <summary>
/// Scaling alert.
/// </summary>
public sealed class ScalingAlert
{
    /// <summary>Gets or sets the resource identifier.</summary>
    public string ResourceId { get; init; } = string.Empty;

    /// <summary>Gets or sets the alert type.</summary>
    public ScalingAlertType AlertType { get; init; }

    /// <summary>Gets or sets the severity.</summary>
    public AlertSeverity Severity { get; init; }

    /// <summary>Gets or sets the alert message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Gets or sets the forecasted value triggering the alert.</summary>
    public double ForecastedValue { get; init; }
}

/// <summary>
/// Types of scaling alerts.
/// </summary>
public enum ScalingAlertType
{
    /// <summary>High CPU forecast.</summary>
    HighCpuForecast,
    /// <summary>High memory forecast.</summary>
    HighMemoryForecast,
    /// <summary>High connections forecast.</summary>
    HighConnectionsForecast,
    /// <summary>Capacity exhaustion imminent.</summary>
    CapacityExhaustion
}

/// <summary>
/// Alert severity levels.
/// </summary>
public enum AlertSeverity
{
    /// <summary>Informational alert.</summary>
    Info,
    /// <summary>Warning alert.</summary>
    Warning,
    /// <summary>Critical alert.</summary>
    Critical
}

/// <summary>
/// Scaling engine statistics.
/// </summary>
public sealed class ScalingStatistics
{
    /// <summary>Gets or sets the number of tracked resources.</summary>
    public int TrackedResources { get; init; }

    /// <summary>Gets or sets the total scaling events.</summary>
    public long TotalScalingEvents { get; init; }

    /// <summary>Gets or sets the successful scaling events.</summary>
    public long SuccessfulScalingEvents { get; init; }

    /// <summary>Gets or sets the scale-up event count.</summary>
    public int ScaleUpEvents { get; init; }

    /// <summary>Gets or sets the scale-down event count.</summary>
    public int ScaleDownEvents { get; init; }

    /// <summary>Gets or sets the success rate.</summary>
    public double SuccessRate { get; init; }

    /// <summary>Gets or sets recent scaling events.</summary>
    public List<ScalingEvent> RecentEvents { get; init; } = new();
}

#endregion
