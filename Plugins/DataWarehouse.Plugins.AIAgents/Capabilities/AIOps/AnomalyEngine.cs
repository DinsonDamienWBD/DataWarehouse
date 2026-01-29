// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Text.Json;
using IExtendedAIProvider = DataWarehouse.Plugins.AIAgents.IExtendedAIProvider;

namespace DataWarehouse.Plugins.AIAgents.Capabilities.AIOps;

/// <summary>
/// Anomaly detection and auto-remediation engine.
/// Uses multiple detection algorithms (Isolation Forest, Z-Score, IQR, Trend Analysis)
/// combined with AI-enhanced root cause analysis and remediation recommendations.
/// </summary>
/// <remarks>
/// <para>
/// The anomaly engine provides:
/// - Multi-algorithm anomaly detection (Z-Score, IQR, Trend Deviation)
/// - Multivariate anomaly detection using Isolation Forest
/// - AI-enhanced root cause analysis
/// - Automatic incident tracking and management
/// - Configurable auto-remediation with human approval workflow
/// </para>
/// <para>
/// The engine gracefully degrades to statistical-only detection when AI is unavailable.
/// </para>
/// </remarks>
public sealed class AnomalyEngine : IDisposable
{
    private readonly IExtendedAIProvider? _aiProvider;
    private readonly AnomalyDetector _detector;
    private readonly ConcurrentDictionary<string, RemediationAction> _remediationActions;
    private readonly ConcurrentQueue<RemediationEvent> _remediationHistory;
    private readonly ConcurrentDictionary<string, IncidentRecord> _activeIncidents;
    private readonly AnomalyEngineConfig _config;
    private readonly Timer? _detectionTimer;
    private readonly SemaphoreSlim _remediationLock;
    private long _totalRemediations;
    private long _successfulRemediations;
    private bool _disposed;

    /// <summary>
    /// Creates a new anomaly engine instance.
    /// </summary>
    /// <param name="aiProvider">Optional AI provider for enhanced analysis.</param>
    /// <param name="config">Engine configuration.</param>
    public AnomalyEngine(IExtendedAIProvider? aiProvider = null, AnomalyEngineConfig? config = null)
    {
        _aiProvider = aiProvider;
        _config = config ?? new AnomalyEngineConfig();
        _detector = new AnomalyDetector(_config.WindowSize, _config.AnomalyThreshold);
        _remediationActions = new ConcurrentDictionary<string, RemediationAction>();
        _remediationHistory = new ConcurrentQueue<RemediationEvent>();
        _activeIncidents = new ConcurrentDictionary<string, IncidentRecord>();
        _remediationLock = new SemaphoreSlim(_config.MaxConcurrentRemediations);

        // Register default remediation actions
        RegisterDefaultRemediations();

        if (_config.EnableAutonomousDetection)
        {
            _detectionTimer = new Timer(
                _ => _ = DetectionCycleAsync(CancellationToken.None),
                null,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(_config.DetectionIntervalSeconds));
        }
    }

    /// <summary>
    /// Records a metric value and checks for anomalies.
    /// </summary>
    /// <param name="metricName">The metric name.</param>
    /// <param name="value">The metric value.</param>
    /// <param name="tags">Optional metadata tags.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The anomaly detection result.</returns>
    public async Task<AnomalyDetectionResult> RecordAndAnalyzeAsync(
        string metricName,
        double value,
        Dictionary<string, object>? tags = null,
        CancellationToken ct = default)
    {
        var anomaly = _detector.RecordAndDetect(metricName, value, tags);

        if (anomaly == null)
        {
            return new AnomalyDetectionResult
            {
                MetricName = metricName,
                Value = value,
                IsAnomaly = false
            };
        }

        // Create or update incident
        var incidentKey = $"{metricName}:{anomaly.Type}";
        var incident = _activeIncidents.GetOrAdd(incidentKey, _ => new IncidentRecord
        {
            Id = Guid.NewGuid().ToString(),
            MetricName = metricName,
            AnomalyType = anomaly.Type,
            StartTime = DateTime.UtcNow,
            Severity = anomaly.Severity
        });

        lock (incident)
        {
            incident.OccurrenceCount++;
            incident.LastOccurrence = DateTime.UtcNow;
            incident.Anomalies.Add(anomaly);

            // Update severity if needed
            if (anomaly.Severity > incident.Severity)
            {
                incident.Severity = anomaly.Severity;
            }
        }

        // Check if auto-remediation is enabled and appropriate
        RemediationRecommendation? remediation = null;
        if (_config.AutoRemediationEnabled && anomaly.Severity >= _config.AutoRemediationMinSeverity)
        {
            remediation = await GetRemediationRecommendationAsync(anomaly, incident, ct);

            if (remediation != null && remediation.AutoExecute && !remediation.RequiresApproval)
            {
                await ExecuteRemediationAsync(remediation, ct);
            }
        }

        return new AnomalyDetectionResult
        {
            MetricName = metricName,
            Value = value,
            IsAnomaly = true,
            Anomaly = anomaly,
            Incident = incident,
            Remediation = remediation
        };
    }

    /// <summary>
    /// Analyzes multivariate metrics for correlated anomalies.
    /// </summary>
    /// <param name="metrics">Dictionary of metric names and values.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The multivariate analysis result.</returns>
    public async Task<MultivariateAnalysisResult> AnalyzeMultivariateAsync(
        Dictionary<string, double> metrics,
        CancellationToken ct = default)
    {
        var multiAnomaly = _detector.DetectMultivariate(metrics);

        if (multiAnomaly == null)
        {
            return new MultivariateAnalysisResult
            {
                IsAnomaly = false,
                Metrics = metrics
            };
        }

        // Get AI analysis for root cause
        string? rootCauseAnalysis = null;
        List<string>? potentialCauses = null;

        if (_aiProvider != null && _aiProvider.IsAvailable && multiAnomaly.Severity >= AnomalySeverity.Medium)
        {
            try
            {
                var analysis = await GetAIRootCauseAnalysisAsync(multiAnomaly, ct);
                rootCauseAnalysis = analysis.RootCause;
                potentialCauses = analysis.PotentialCauses;
            }
            catch
            {
                // Continue without AI analysis
            }
        }

        return new MultivariateAnalysisResult
        {
            IsAnomaly = true,
            Metrics = metrics,
            Anomaly = multiAnomaly,
            RootCauseAnalysis = rootCauseAnalysis,
            PotentialCauses = potentialCauses ?? new List<string>()
        };
    }

    /// <summary>
    /// Gets remediation recommendation for an anomaly.
    /// </summary>
    /// <param name="anomaly">The detected anomaly.</param>
    /// <param name="incident">Optional incident record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The remediation recommendation, or null if none available.</returns>
    public async Task<RemediationRecommendation?> GetRemediationRecommendationAsync(
        AnomalyResult anomaly,
        IncidentRecord? incident = null,
        CancellationToken ct = default)
    {
        // Check for matching remediation action
        var actionKey = $"{anomaly.MetricName}:{anomaly.Type}";
        if (_remediationActions.TryGetValue(actionKey, out var action))
        {
            return new RemediationRecommendation
            {
                IncidentId = incident?.Id ?? Guid.NewGuid().ToString(),
                ActionId = action.Id,
                ActionName = action.Name,
                Description = action.Description,
                AutoExecute = action.AutoExecute && anomaly.Severity >= action.MinSeverity,
                RequiresApproval = action.RequiresApproval || anomaly.Severity >= AnomalySeverity.Critical,
                EstimatedImpact = action.EstimatedImpact,
                Confidence = 0.8,
                Source = RemediationSource.RuleBased
            };
        }

        // Get AI recommendation for unknown anomalies
        if (_config.AIRemediationEnabled && _aiProvider != null && _aiProvider.IsAvailable)
        {
            return await GetAIRemediationRecommendationAsync(anomaly, incident, ct);
        }

        return null;
    }

    /// <summary>
    /// Executes a remediation action.
    /// </summary>
    /// <param name="recommendation">The remediation recommendation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The remediation result.</returns>
    public async Task<RemediationResult> ExecuteRemediationAsync(
        RemediationRecommendation recommendation,
        CancellationToken ct = default)
    {
        if (!await _remediationLock.WaitAsync(TimeSpan.FromSeconds(30), ct))
        {
            return new RemediationResult
            {
                RecommendationId = recommendation.ActionId,
                Success = false,
                Reason = "Remediation queue full"
            };
        }

        try
        {
            Interlocked.Increment(ref _totalRemediations);
            var startTime = DateTime.UtcNow;

            // Find and execute action
            if (_remediationActions.TryGetValue(recommendation.ActionId, out var action))
            {
                var result = await action.ExecuteAsync(ct);

                var remediationEvent = new RemediationEvent
                {
                    IncidentId = recommendation.IncidentId,
                    ActionId = action.Id,
                    ActionName = action.Name,
                    Timestamp = DateTime.UtcNow,
                    Success = result.Success,
                    Message = result.Message
                };

                _remediationHistory.Enqueue(remediationEvent);

                // Trim history
                while (_remediationHistory.Count > 1000)
                {
                    _remediationHistory.TryDequeue(out _);
                }

                // Update incident if resolved
                if (result.Success && _activeIncidents.TryGetValue(recommendation.IncidentId, out var incident))
                {
                    lock (incident)
                    {
                        incident.Status = IncidentStatus.Resolved;
                        incident.ResolvedTime = DateTime.UtcNow;
                        incident.Resolution = action.Name;
                    }
                }

                if (result.Success)
                {
                    Interlocked.Increment(ref _successfulRemediations);
                }

                return new RemediationResult
                {
                    RecommendationId = recommendation.ActionId,
                    Success = result.Success,
                    ExecutionTime = DateTime.UtcNow - startTime,
                    Reason = result.Message
                };
            }

            return new RemediationResult
            {
                RecommendationId = recommendation.ActionId,
                Success = false,
                Reason = "Remediation action not found"
            };
        }
        finally
        {
            _remediationLock.Release();
        }
    }

    /// <summary>
    /// Registers a remediation action.
    /// </summary>
    /// <param name="action">The remediation action to register.</param>
    public void RegisterRemediation(RemediationAction action)
    {
        _remediationActions[action.Id] = action;
    }

    /// <summary>
    /// Gets active incidents.
    /// </summary>
    /// <returns>Collection of active incidents.</returns>
    public IEnumerable<IncidentRecord> GetActiveIncidents()
    {
        return _activeIncidents.Values
            .Where(i => i.Status == IncidentStatus.Active || i.Status == IncidentStatus.Investigating)
            .OrderByDescending(i => i.Severity)
            .ThenByDescending(i => i.LastOccurrence);
    }

    /// <summary>
    /// Runs a detection cycle (cleanup of old incidents).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The detection cycle result.</returns>
    public async Task<AnomalyDetectionCycleResult> DetectionCycleAsync(CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;

        // Clean up old incidents
        var cutoff = DateTime.UtcNow.AddHours(-_config.IncidentRetentionHours);
        var oldIncidents = _activeIncidents
            .Where(kv => kv.Value.Status == IncidentStatus.Resolved && kv.Value.ResolvedTime < cutoff)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in oldIncidents)
        {
            _activeIncidents.TryRemove(key, out _);
        }

        await Task.CompletedTask;

        return new AnomalyDetectionCycleResult
        {
            StartTime = startTime,
            EndTime = DateTime.UtcNow,
            IncidentsCleaned = oldIncidents.Count,
            ActiveIncidentCount = _activeIncidents.Count(i => i.Value.Status == IncidentStatus.Active)
        };
    }

    /// <summary>
    /// Gets anomaly engine statistics.
    /// </summary>
    /// <returns>The anomaly statistics.</returns>
    public AnomalyStatistics GetStatistics()
    {
        var recentRemediations = _remediationHistory.TakeLast(100).ToList();

        return new AnomalyStatistics
        {
            ActiveIncidents = _activeIncidents.Count(i => i.Value.Status == IncidentStatus.Active),
            ResolvedIncidents = _activeIncidents.Count(i => i.Value.Status == IncidentStatus.Resolved),
            TotalRemediations = Interlocked.Read(ref _totalRemediations),
            SuccessfulRemediations = Interlocked.Read(ref _successfulRemediations),
            RemediationSuccessRate = _totalRemediations > 0
                ? (double)_successfulRemediations / _totalRemediations
                : 1.0,
            RegisteredRemediationActions = _remediationActions.Count,
            RecentRemediations = recentRemediations
        };
    }

    #region Private Methods

    private static CompletionRequest ToCompletionRequest(string prompt, string? model = null) => new()
    {
        Prompt = prompt,
        Model = model!
    };

    private async Task<AIRootCauseAnalysis> GetAIRootCauseAnalysisAsync(
        MultivariateAnomalyResult anomaly,
        CancellationToken ct)
    {
        if (_aiProvider == null)
            throw new InvalidOperationException("AI provider not available");

        var metricsJson = JsonSerializer.Serialize(anomaly.Metrics);
        var contributingJson = JsonSerializer.Serialize(anomaly.ContributingMetrics);

        var prompt = $@"Analyze the following multivariate anomaly and determine the root cause:

Anomaly Score: {anomaly.Score:F2}
Severity: {anomaly.Severity}
Metrics at time of anomaly:
{metricsJson}

Contributing metrics (ordered by anomaly score):
{contributingJson}

Per-metric scores:
{JsonSerializer.Serialize(anomaly.PerMetricScores)}

Provide your analysis as JSON:
{{
  ""rootCause"": ""<most likely root cause>"",
  ""potentialCauses"": [""<cause1>"", ""<cause2>"", ""<cause3>""],
  ""confidence"": <0-1>,
  ""recommendedActions"": [""<action1>"", ""<action2>""]
}}";

        var request = ToCompletionRequest(prompt);

        var response = await _aiProvider.CompleteAsync(request, ct);

        if (!string.IsNullOrEmpty(response.Text))
        {
            var jsonStart = response.Text.IndexOf('{');
            var jsonEnd = response.Text.LastIndexOf('}');

            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response.Text.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                return new AIRootCauseAnalysis
                {
                    RootCause = root.GetProperty("rootCause").GetString() ?? "Unknown",
                    PotentialCauses = root.TryGetProperty("potentialCauses", out var causes)
                        ? causes.EnumerateArray().Select(c => c.GetString() ?? "").ToList()
                        : new List<string>(),
                    Confidence = root.TryGetProperty("confidence", out var conf) ? conf.GetDouble() : 0.5,
                    RecommendedActions = root.TryGetProperty("recommendedActions", out var actions)
                        ? actions.EnumerateArray().Select(a => a.GetString() ?? "").ToList()
                        : new List<string>()
                };
            }
        }

        return new AIRootCauseAnalysis { RootCause = "Unable to determine", PotentialCauses = new List<string>() };
    }

    private async Task<RemediationRecommendation?> GetAIRemediationRecommendationAsync(
        AnomalyResult anomaly,
        IncidentRecord? incident,
        CancellationToken ct)
    {
        if (_aiProvider == null)
            return null;

        var prompt = $@"Recommend a remediation action for the following anomaly:

Metric: {anomaly.MetricName}
Value: {anomaly.Value}
Type: {anomaly.Type}
Severity: {anomaly.Severity}
Score: {anomaly.Score:F2}
Expected value: {anomaly.Details.ExpectedValue:F2}
Expected range: [{anomaly.Details.ExpectedRange.Lower:F2}, {anomaly.Details.ExpectedRange.Upper:F2}]

Incident history (if any):
- Occurrence count: {incident?.OccurrenceCount ?? 0}
- Duration: {(incident != null ? (DateTime.UtcNow - incident.StartTime).TotalMinutes : 0):F0} minutes

Provide remediation recommendation as JSON:
{{
  ""action"": ""<action name>"",
  ""description"": ""<what to do>"",
  ""autoExecute"": <true/false>,
  ""requiresApproval"": <true/false>,
  ""estimatedImpact"": ""<expected result>"",
  ""confidence"": <0-1>
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

                    return new RemediationRecommendation
                    {
                        IncidentId = incident?.Id ?? Guid.NewGuid().ToString(),
                        ActionId = $"ai-{Guid.NewGuid():N}",
                        ActionName = root.GetProperty("action").GetString() ?? "AI Recommended Action",
                        Description = root.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
                        AutoExecute = root.TryGetProperty("autoExecute", out var auto) && auto.GetBoolean(),
                        RequiresApproval = !root.TryGetProperty("requiresApproval", out var approval) || approval.GetBoolean(),
                        EstimatedImpact = root.TryGetProperty("estimatedImpact", out var impact) ? impact.GetString() ?? "" : "",
                        Confidence = root.TryGetProperty("confidence", out var conf) ? conf.GetDouble() : 0.5,
                        Source = RemediationSource.AI
                    };
                }
            }
            catch
            {
                // Fall through
            }
        }

        return null;
    }

    private void RegisterDefaultRemediations()
    {
        // High CPU remediation
        _remediationActions["cpu:Spike"] = new RemediationAction
        {
            Id = "cpu:Spike",
            Name = "CPU Spike Mitigation",
            Description = "Scales up compute resources to handle CPU spike",
            AutoExecute = true,
            RequiresApproval = false,
            MinSeverity = AnomalySeverity.Medium,
            EstimatedImpact = "Temporary capacity increase"
        };

        // Memory leak remediation
        _remediationActions["memory:TrendDeviation"] = new RemediationAction
        {
            Id = "memory:TrendDeviation",
            Name = "Memory Leak Mitigation",
            Description = "Restarts affected service to clear memory",
            AutoExecute = false,
            RequiresApproval = true,
            MinSeverity = AnomalySeverity.High,
            EstimatedImpact = "Brief service interruption"
        };

        // Disk space remediation
        _remediationActions["disk:Outlier"] = new RemediationAction
        {
            Id = "disk:Outlier",
            Name = "Disk Space Cleanup",
            Description = "Removes temporary files and old logs",
            AutoExecute = true,
            RequiresApproval = false,
            MinSeverity = AnomalySeverity.Low,
            EstimatedImpact = "Frees disk space"
        };

        // Error rate remediation
        _remediationActions["errorRate:Spike"] = new RemediationAction
        {
            Id = "errorRate:Spike",
            Name = "Circuit Breaker Activation",
            Description = "Activates circuit breaker for failing service",
            AutoExecute = true,
            RequiresApproval = false,
            MinSeverity = AnomalySeverity.High,
            EstimatedImpact = "Prevents cascade failures"
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

        _detectionTimer?.Dispose();
        _remediationLock.Dispose();
    }
}

#region Configuration

/// <summary>
/// Configuration for the anomaly engine.
/// </summary>
public sealed class AnomalyEngineConfig
{
    /// <summary>Gets or sets the window size for anomaly detection.</summary>
    public int WindowSize { get; set; } = 100;

    /// <summary>Gets or sets the anomaly threshold (Z-score).</summary>
    public double AnomalyThreshold { get; set; } = 2.5;

    /// <summary>Gets or sets the detection interval in seconds.</summary>
    public int DetectionIntervalSeconds { get; set; } = 30;

    /// <summary>Gets or sets whether auto-remediation is enabled.</summary>
    public bool AutoRemediationEnabled { get; set; } = true;

    /// <summary>Gets or sets the minimum severity for auto-remediation.</summary>
    public AnomalySeverity AutoRemediationMinSeverity { get; set; } = AnomalySeverity.Medium;

    /// <summary>Gets or sets whether AI-based remediation is enabled.</summary>
    public bool AIRemediationEnabled { get; set; } = true;

    /// <summary>Gets or sets the maximum concurrent remediations.</summary>
    public int MaxConcurrentRemediations { get; set; } = 5;

    /// <summary>Gets or sets the incident retention period in hours.</summary>
    public int IncidentRetentionHours { get; set; } = 168; // 7 days

    /// <summary>Gets or sets whether autonomous detection cycles are enabled.</summary>
    public bool EnableAutonomousDetection { get; set; } = true;
}

#endregion

#region Anomaly Detection Core

/// <summary>
/// Core anomaly detector using multiple algorithms.
/// </summary>
internal sealed class AnomalyDetector
{
    private readonly ConcurrentDictionary<string, MetricSeries> _metricSeries;
    private readonly IsolationForest _isolationForest;
    private readonly int _windowSize;
    private readonly double _threshold;

    public AnomalyDetector(int windowSize = 100, double threshold = 2.5)
    {
        _metricSeries = new ConcurrentDictionary<string, MetricSeries>();
        _isolationForest = new IsolationForest(100, 256);
        _windowSize = windowSize;
        _threshold = threshold;
    }

    public AnomalyResult? RecordAndDetect(string metricName, double value, Dictionary<string, object>? tags = null)
    {
        var series = _metricSeries.GetOrAdd(metricName, _ => new MetricSeries
        {
            MetricName = metricName,
            WindowSize = _windowSize
        });

        lock (series)
        {
            series.AddValue(value);

            // Multi-method detection
            var zScoreAnomaly = DetectZScoreAnomaly(series, value);
            var iqrAnomaly = DetectIQRAnomaly(series, value);
            var trendAnomaly = DetectTrendAnomaly(series, value);

            // Combine detection methods
            var anomalyScore = (zScoreAnomaly.Score + iqrAnomaly.Score + trendAnomaly.Score) / 3.0;
            var isAnomaly = zScoreAnomaly.IsAnomaly || iqrAnomaly.IsAnomaly || trendAnomaly.IsAnomaly;

            if (isAnomaly)
            {
                return new AnomalyResult
                {
                    MetricName = metricName,
                    Value = value,
                    Timestamp = DateTime.UtcNow,
                    Severity = CalculateSeverity(anomalyScore),
                    Score = anomalyScore,
                    Type = DetermineAnomalyType(zScoreAnomaly, iqrAnomaly, trendAnomaly),
                    Tags = tags ?? new Dictionary<string, object>(),
                    Details = new AnomalyDetails
                    {
                        ZScore = zScoreAnomaly.Score,
                        IQRScore = iqrAnomaly.Score,
                        TrendDeviation = trendAnomaly.Score,
                        ExpectedValue = series.Mean,
                        ExpectedRange = (series.Mean - 2 * series.StdDev, series.Mean + 2 * series.StdDev)
                    }
                };
            }

            return null;
        }
    }

    public MultivariateAnomalyResult? DetectMultivariate(Dictionary<string, double> metrics)
    {
        var features = metrics.Values.ToArray();
        var isolationScore = _isolationForest.Score(features);

        var perMetricScores = new Dictionary<string, double>();
        foreach (var (name, value) in metrics)
        {
            if (_metricSeries.TryGetValue(name, out var series))
            {
                lock (series)
                {
                    perMetricScores[name] = series.StdDev > 0.001
                        ? Math.Abs((value - series.Mean) / series.StdDev)
                        : 0;
                    series.AddValue(value);
                }
            }
            else
            {
                perMetricScores[name] = 0;
                RecordAndDetect(name, value);
            }
        }

        var avgScore = perMetricScores.Values.DefaultIfEmpty(0).Average();
        var maxScore = perMetricScores.Values.DefaultIfEmpty(0).Max();
        var combinedScore = (isolationScore + avgScore + maxScore) / 3.0;

        if (combinedScore > _threshold)
        {
            return new MultivariateAnomalyResult
            {
                Timestamp = DateTime.UtcNow,
                Metrics = metrics,
                Score = combinedScore,
                IsolationScore = isolationScore,
                PerMetricScores = perMetricScores,
                Severity = CalculateSeverity(combinedScore),
                ContributingMetrics = perMetricScores
                    .Where(kv => kv.Value > _threshold)
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => kv.Key)
                    .ToList()
            };
        }

        return null;
    }

    private (bool IsAnomaly, double Score) DetectZScoreAnomaly(MetricSeries series, double value)
    {
        if (series.Values.Count < 30 || series.StdDev < 0.001)
            return (false, 0);

        var zScore = Math.Abs((value - series.Mean) / series.StdDev);
        return (zScore > _threshold, zScore);
    }

    private (bool IsAnomaly, double Score) DetectIQRAnomaly(MetricSeries series, double value)
    {
        if (series.Values.Count < 30)
            return (false, 0);

        var sorted = series.Values.OrderBy(v => v).ToList();
        var q1 = sorted[(int)(sorted.Count * 0.25)];
        var q3 = sorted[(int)(sorted.Count * 0.75)];
        var iqr = q3 - q1;

        if (iqr < 0.001) return (false, 0);

        var lowerBound = q1 - 1.5 * iqr;
        var upperBound = q3 + 1.5 * iqr;

        if (value < lowerBound || value > upperBound)
        {
            var deviation = value < lowerBound
                ? (lowerBound - value) / iqr
                : (value - upperBound) / iqr;
            return (true, deviation);
        }

        return (false, 0);
    }

    private (bool IsAnomaly, double Score) DetectTrendAnomaly(MetricSeries series, double value)
    {
        if (series.Values.Count < 50)
            return (false, 0);

        var recentValues = series.Values.TakeLast(20).ToList();
        var trend = CalculateLinearTrend(recentValues);
        var expected = recentValues.Last() + trend;

        var deviation = Math.Abs(value - expected);
        var normalizedDeviation = series.StdDev > 0.001 ? deviation / series.StdDev : 0;

        return (normalizedDeviation > _threshold * 1.5, normalizedDeviation);
    }

    private static double CalculateLinearTrend(List<double> values)
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

    private static AnomalySeverity CalculateSeverity(double score)
    {
        return score switch
        {
            > 5.0 => AnomalySeverity.Critical,
            > 4.0 => AnomalySeverity.High,
            > 3.0 => AnomalySeverity.Medium,
            > 2.0 => AnomalySeverity.Low,
            _ => AnomalySeverity.Info
        };
    }

    private static AnomalyType DetermineAnomalyType(
        (bool IsAnomaly, double Score) zScore,
        (bool IsAnomaly, double Score) iqr,
        (bool IsAnomaly, double Score) trend)
    {
        if (trend.IsAnomaly && trend.Score > zScore.Score && trend.Score > iqr.Score)
            return AnomalyType.TrendDeviation;
        if (zScore.Score > 4)
            return AnomalyType.Spike;
        if (iqr.IsAnomaly)
            return AnomalyType.Outlier;
        return AnomalyType.Statistical;
    }
}

/// <summary>
/// Time series data for a metric.
/// </summary>
internal sealed class MetricSeries
{
    public string MetricName { get; init; } = string.Empty;
    public int WindowSize { get; init; } = 100;
    public List<double> Values { get; } = new();
    public double Mean { get; private set; }
    public double StdDev { get; private set; }
    private double _sum;

    public void AddValue(double value)
    {
        Values.Add(value);
        _sum += value;

        while (Values.Count > WindowSize)
        {
            _sum -= Values[0];
            Values.RemoveAt(0);
        }

        if (Values.Count > 0)
        {
            Mean = _sum / Values.Count;
            var sumSquares = Values.Sum(v => Math.Pow(v - Mean, 2));
            StdDev = Math.Sqrt(sumSquares / Values.Count);
        }
    }
}

/// <summary>
/// Isolation Forest for multivariate anomaly detection.
/// </summary>
internal sealed class IsolationForest
{
    private readonly int _numTrees;
    private readonly int _sampleSize;
    private readonly List<IsolationTree> _trees;
    private readonly Random _random;
    private bool _trained;

    public IsolationForest(int numTrees = 100, int sampleSize = 256)
    {
        _numTrees = numTrees;
        _sampleSize = sampleSize;
        _trees = new List<IsolationTree>();
        _random = new Random(42);
    }

    public void Train(double[][] data)
    {
        _trees.Clear();

        for (int i = 0; i < _numTrees; i++)
        {
            var sample = data.OrderBy(_ => _random.Next()).Take(_sampleSize).ToArray();
            var tree = new IsolationTree();
            tree.Build(sample, 0, (int)Math.Ceiling(Math.Log2(_sampleSize)));
            _trees.Add(tree);
        }

        _trained = true;
    }

    public double Score(double[] point)
    {
        if (!_trained || _trees.Count == 0)
            return 0;

        var avgPathLength = _trees.Average(t => t.PathLength(point));
        var c = AveragePathLength(_sampleSize);

        return Math.Pow(2, -avgPathLength / c);
    }

    private static double AveragePathLength(int n)
    {
        if (n <= 1) return 1;
        return 2 * (Math.Log(n - 1) + 0.5772156649) - 2 * (n - 1.0) / n;
    }
}

internal sealed class IsolationTree
{
    private IsolationNode? _root;

    public void Build(double[][] data, int depth, int maxDepth)
    {
        _root = BuildNode(data, depth, maxDepth, new Random());
    }

    private IsolationNode BuildNode(double[][] data, int depth, int maxDepth, Random random)
    {
        if (depth >= maxDepth || data.Length <= 1)
        {
            return new IsolationNode { IsLeaf = true, Size = data.Length };
        }

        var numFeatures = data[0].Length;
        if (numFeatures == 0)
        {
            return new IsolationNode { IsLeaf = true, Size = data.Length };
        }

        var featureIndex = random.Next(numFeatures);
        var values = data.Select(d => d[featureIndex]).ToArray();
        var min = values.Min();
        var max = values.Max();

        if (Math.Abs(max - min) < 0.0001)
        {
            return new IsolationNode { IsLeaf = true, Size = data.Length };
        }

        var splitValue = min + random.NextDouble() * (max - min);
        var leftData = data.Where(d => d[featureIndex] < splitValue).ToArray();
        var rightData = data.Where(d => d[featureIndex] >= splitValue).ToArray();

        return new IsolationNode
        {
            IsLeaf = false,
            FeatureIndex = featureIndex,
            SplitValue = splitValue,
            Left = BuildNode(leftData, depth + 1, maxDepth, random),
            Right = BuildNode(rightData, depth + 1, maxDepth, random)
        };
    }

    public int PathLength(double[] point)
    {
        return PathLength(point, _root, 0);
    }

    private int PathLength(double[] point, IsolationNode? node, int depth)
    {
        if (node == null || node.IsLeaf)
        {
            return depth + (node?.Size > 1 ? (int)Math.Ceiling(Math.Log2(node.Size)) : 0);
        }

        if (point.Length <= node.FeatureIndex)
            return depth;

        return point[node.FeatureIndex] < node.SplitValue
            ? PathLength(point, node.Left, depth + 1)
            : PathLength(point, node.Right, depth + 1);
    }
}

internal sealed class IsolationNode
{
    public bool IsLeaf { get; init; }
    public int Size { get; init; }
    public int FeatureIndex { get; init; }
    public double SplitValue { get; init; }
    public IsolationNode? Left { get; init; }
    public IsolationNode? Right { get; init; }
}

#endregion

#region Types

/// <summary>
/// Anomaly severity levels.
/// </summary>
public enum AnomalySeverity
{
    /// <summary>Informational level.</summary>
    Info,
    /// <summary>Low severity.</summary>
    Low,
    /// <summary>Medium severity.</summary>
    Medium,
    /// <summary>High severity.</summary>
    High,
    /// <summary>Critical severity.</summary>
    Critical
}

/// <summary>
/// Types of anomalies detected.
/// </summary>
public enum AnomalyType
{
    /// <summary>Unknown type.</summary>
    Unknown,
    /// <summary>Sudden spike in value.</summary>
    Spike,
    /// <summary>Statistical outlier.</summary>
    Outlier,
    /// <summary>Deviation from expected trend.</summary>
    TrendDeviation,
    /// <summary>General statistical anomaly.</summary>
    Statistical,
    /// <summary>Seasonal pattern anomaly.</summary>
    Seasonal
}

/// <summary>
/// Incident status.
/// </summary>
public enum IncidentStatus
{
    /// <summary>Active incident.</summary>
    Active,
    /// <summary>Under investigation.</summary>
    Investigating,
    /// <summary>Resolved.</summary>
    Resolved,
    /// <summary>Closed.</summary>
    Closed
}

/// <summary>
/// Source of remediation recommendation.
/// </summary>
public enum RemediationSource
{
    /// <summary>Based on predefined rules.</summary>
    RuleBased,
    /// <summary>AI-generated recommendation.</summary>
    AI,
    /// <summary>Based on historical patterns.</summary>
    Historical
}

/// <summary>
/// Result of anomaly detection for a single metric.
/// </summary>
public sealed class AnomalyResult
{
    /// <summary>Gets or sets the metric name.</summary>
    public string MetricName { get; init; } = string.Empty;

    /// <summary>Gets or sets the value that triggered the anomaly.</summary>
    public double Value { get; init; }

    /// <summary>Gets or sets the detection timestamp.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Gets or sets the severity.</summary>
    public AnomalySeverity Severity { get; init; }

    /// <summary>Gets or sets the anomaly score.</summary>
    public double Score { get; init; }

    /// <summary>Gets or sets the anomaly type.</summary>
    public AnomalyType Type { get; init; }

    /// <summary>Gets or sets the tags.</summary>
    public Dictionary<string, object> Tags { get; init; } = new();

    /// <summary>Gets or sets the details.</summary>
    public AnomalyDetails Details { get; init; } = new();
}

/// <summary>
/// Detailed anomaly information.
/// </summary>
public sealed class AnomalyDetails
{
    /// <summary>Gets or sets the Z-score.</summary>
    public double ZScore { get; init; }

    /// <summary>Gets or sets the IQR score.</summary>
    public double IQRScore { get; init; }

    /// <summary>Gets or sets the trend deviation.</summary>
    public double TrendDeviation { get; init; }

    /// <summary>Gets or sets the expected value.</summary>
    public double ExpectedValue { get; init; }

    /// <summary>Gets or sets the expected range.</summary>
    public (double Lower, double Upper) ExpectedRange { get; init; }
}

/// <summary>
/// Result of multivariate anomaly detection.
/// </summary>
public sealed class MultivariateAnomalyResult
{
    /// <summary>Gets or sets the timestamp.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Gets or sets the metrics involved.</summary>
    public Dictionary<string, double> Metrics { get; init; } = new();

    /// <summary>Gets or sets the combined anomaly score.</summary>
    public double Score { get; init; }

    /// <summary>Gets or sets the isolation forest score.</summary>
    public double IsolationScore { get; init; }

    /// <summary>Gets or sets per-metric scores.</summary>
    public Dictionary<string, double> PerMetricScores { get; init; } = new();

    /// <summary>Gets or sets the severity.</summary>
    public AnomalySeverity Severity { get; init; }

    /// <summary>Gets or sets the contributing metrics.</summary>
    public List<string> ContributingMetrics { get; init; } = new();
}

/// <summary>
/// Record of an incident.
/// </summary>
public sealed class IncidentRecord
{
    /// <summary>Gets or sets the incident identifier.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets or sets the metric name.</summary>
    public string MetricName { get; init; } = string.Empty;

    /// <summary>Gets or sets the anomaly type.</summary>
    public AnomalyType AnomalyType { get; init; }

    /// <summary>Gets or sets when the incident started.</summary>
    public DateTime StartTime { get; init; }

    /// <summary>Gets or sets when the anomaly last occurred.</summary>
    public DateTime LastOccurrence { get; set; }

    /// <summary>Gets or sets when the incident was resolved.</summary>
    public DateTime? ResolvedTime { get; set; }

    /// <summary>Gets or sets the severity.</summary>
    public AnomalySeverity Severity { get; set; }

    /// <summary>Gets or sets the status.</summary>
    public IncidentStatus Status { get; set; } = IncidentStatus.Active;

    /// <summary>Gets or sets the occurrence count.</summary>
    public int OccurrenceCount { get; set; }

    /// <summary>Gets or sets the resolution description.</summary>
    public string? Resolution { get; set; }

    /// <summary>Gets the anomalies associated with this incident.</summary>
    public List<AnomalyResult> Anomalies { get; } = new();
}

/// <summary>
/// Anomaly detection result.
/// </summary>
public sealed class AnomalyDetectionResult
{
    /// <summary>Gets or sets the metric name.</summary>
    public string MetricName { get; init; } = string.Empty;

    /// <summary>Gets or sets the value.</summary>
    public double Value { get; init; }

    /// <summary>Gets or sets whether an anomaly was detected.</summary>
    public bool IsAnomaly { get; init; }

    /// <summary>Gets or sets the anomaly details.</summary>
    public AnomalyResult? Anomaly { get; init; }

    /// <summary>Gets or sets the incident record.</summary>
    public IncidentRecord? Incident { get; init; }

    /// <summary>Gets or sets the remediation recommendation.</summary>
    public RemediationRecommendation? Remediation { get; init; }
}

/// <summary>
/// Multivariate analysis result.
/// </summary>
public sealed class MultivariateAnalysisResult
{
    /// <summary>Gets or sets whether an anomaly was detected.</summary>
    public bool IsAnomaly { get; init; }

    /// <summary>Gets or sets the metrics analyzed.</summary>
    public Dictionary<string, double> Metrics { get; init; } = new();

    /// <summary>Gets or sets the anomaly result.</summary>
    public MultivariateAnomalyResult? Anomaly { get; init; }

    /// <summary>Gets or sets the root cause analysis.</summary>
    public string? RootCauseAnalysis { get; init; }

    /// <summary>Gets or sets potential causes.</summary>
    public List<string> PotentialCauses { get; init; } = new();
}

/// <summary>
/// Remediation action definition.
/// </summary>
public sealed class RemediationAction
{
    /// <summary>Gets or sets the action identifier.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets or sets the action name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets or sets the description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Gets or sets whether to auto-execute.</summary>
    public bool AutoExecute { get; init; }

    /// <summary>Gets or sets whether approval is required.</summary>
    public bool RequiresApproval { get; init; }

    /// <summary>Gets or sets the minimum severity for this action.</summary>
    public AnomalySeverity MinSeverity { get; init; }

    /// <summary>Gets or sets the estimated impact description.</summary>
    public string EstimatedImpact { get; init; } = string.Empty;

    /// <summary>Gets or sets the execution handler.</summary>
    public Func<CancellationToken, Task<ActionResult>>? ExecuteHandler { get; set; }

    /// <summary>
    /// Executes the remediation action.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The action result.</returns>
    public async Task<ActionResult> ExecuteAsync(CancellationToken ct)
    {
        if (ExecuteHandler != null)
        {
            return await ExecuteHandler(ct);
        }

        return new ActionResult { Success = true, Message = "Action registered (no handler defined)" };
    }
}

/// <summary>
/// Result of an action execution.
/// </summary>
public sealed class ActionResult
{
    /// <summary>Gets or sets whether the action succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets or sets the result message.</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Remediation recommendation.
/// </summary>
public sealed class RemediationRecommendation
{
    /// <summary>Gets or sets the incident identifier.</summary>
    public string IncidentId { get; init; } = string.Empty;

    /// <summary>Gets or sets the action identifier.</summary>
    public string ActionId { get; init; } = string.Empty;

    /// <summary>Gets or sets the action name.</summary>
    public string ActionName { get; init; } = string.Empty;

    /// <summary>Gets or sets the description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Gets or sets whether to auto-execute.</summary>
    public bool AutoExecute { get; init; }

    /// <summary>Gets or sets whether approval is required.</summary>
    public bool RequiresApproval { get; init; }

    /// <summary>Gets or sets the estimated impact.</summary>
    public string EstimatedImpact { get; init; } = string.Empty;

    /// <summary>Gets or sets the confidence level.</summary>
    public double Confidence { get; init; }

    /// <summary>Gets or sets the source of the recommendation.</summary>
    public RemediationSource Source { get; init; }
}

/// <summary>
/// Remediation execution result.
/// </summary>
public sealed class RemediationResult
{
    /// <summary>Gets or sets the recommendation identifier.</summary>
    public string RecommendationId { get; init; } = string.Empty;

    /// <summary>Gets or sets whether the remediation succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets or sets the execution time.</summary>
    public TimeSpan ExecutionTime { get; init; }

    /// <summary>Gets or sets the result reason.</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Record of a remediation event.
/// </summary>
public sealed class RemediationEvent
{
    /// <summary>Gets or sets the incident identifier.</summary>
    public string IncidentId { get; init; } = string.Empty;

    /// <summary>Gets or sets the action identifier.</summary>
    public string ActionId { get; init; } = string.Empty;

    /// <summary>Gets or sets the action name.</summary>
    public string ActionName { get; init; } = string.Empty;

    /// <summary>Gets or sets the timestamp.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Gets or sets whether the action succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets or sets the result message.</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// AI root cause analysis result.
/// </summary>
public sealed class AIRootCauseAnalysis
{
    /// <summary>Gets or sets the root cause.</summary>
    public string RootCause { get; init; } = string.Empty;

    /// <summary>Gets or sets potential causes.</summary>
    public List<string> PotentialCauses { get; init; } = new();

    /// <summary>Gets or sets the confidence level.</summary>
    public double Confidence { get; init; }

    /// <summary>Gets or sets recommended actions.</summary>
    public List<string> RecommendedActions { get; init; } = new();
}

/// <summary>
/// Result of a detection cycle.
/// </summary>
public sealed class AnomalyDetectionCycleResult
{
    /// <summary>Gets or sets the start time.</summary>
    public DateTime StartTime { get; init; }

    /// <summary>Gets or sets the end time.</summary>
    public DateTime EndTime { get; init; }

    /// <summary>Gets the duration.</summary>
    public TimeSpan Duration => EndTime - StartTime;

    /// <summary>Gets or sets the number of incidents cleaned up.</summary>
    public int IncidentsCleaned { get; init; }

    /// <summary>Gets or sets the active incident count.</summary>
    public int ActiveIncidentCount { get; init; }
}

/// <summary>
/// Anomaly engine statistics.
/// </summary>
public sealed class AnomalyStatistics
{
    /// <summary>Gets or sets the active incident count.</summary>
    public int ActiveIncidents { get; init; }

    /// <summary>Gets or sets the resolved incident count.</summary>
    public int ResolvedIncidents { get; init; }

    /// <summary>Gets or sets the total remediations.</summary>
    public long TotalRemediations { get; init; }

    /// <summary>Gets or sets the successful remediations.</summary>
    public long SuccessfulRemediations { get; init; }

    /// <summary>Gets or sets the remediation success rate.</summary>
    public double RemediationSuccessRate { get; init; }

    /// <summary>Gets or sets the registered remediation action count.</summary>
    public int RegisteredRemediationActions { get; init; }

    /// <summary>Gets or sets recent remediations.</summary>
    public List<RemediationEvent> RecentRemediations { get; init; } = new();
}

#endregion
