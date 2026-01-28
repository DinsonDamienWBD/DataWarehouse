using DataWarehouse.SDK.AI;
using DataWarehouse.Plugins.AutonomousDataManagement.Models;
using DataWarehouse.Plugins.AutonomousDataManagement.Providers;
using System.Collections.Concurrent;
using System.Text.Json;

namespace DataWarehouse.Plugins.AutonomousDataManagement.Engine;

/// <summary>
/// Anomaly detection and auto-remediation engine.
/// </summary>
public sealed class AnomalyEngine : IDisposable
{
    private readonly AIProviderSelector _providerSelector;
    private readonly AnomalyDetector _detector;
    private readonly ConcurrentDictionary<string, RemediationAction> _remediationActions;
    private readonly ConcurrentQueue<RemediationEvent> _remediationHistory;
    private readonly ConcurrentDictionary<string, IncidentRecord> _activeIncidents;
    private readonly AnomalyEngineConfig _config;
    private readonly Timer _detectionTimer;
    private readonly SemaphoreSlim _remediationLock;
    private bool _disposed;

    public AnomalyEngine(AIProviderSelector providerSelector, AnomalyEngineConfig? config = null)
    {
        _providerSelector = providerSelector ?? throw new ArgumentNullException(nameof(providerSelector));
        _config = config ?? new AnomalyEngineConfig();
        _detector = new AnomalyDetector(_config.WindowSize, _config.AnomalyThreshold);
        _remediationActions = new ConcurrentDictionary<string, RemediationAction>();
        _remediationHistory = new ConcurrentQueue<RemediationEvent>();
        _activeIncidents = new ConcurrentDictionary<string, IncidentRecord>();
        _remediationLock = new SemaphoreSlim(_config.MaxConcurrentRemediations);

        // Register default remediation actions
        RegisterDefaultRemediations();

        _detectionTimer = new Timer(
            _ => _ = DetectionCycleAsync(CancellationToken.None),
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(_config.DetectionIntervalSeconds));
    }

    /// <summary>
    /// Records a metric value and checks for anomalies.
    /// </summary>
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

        if (multiAnomaly.Severity >= AnomalySeverity.Medium)
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
                Confidence = 0.8
            };
        }

        // Get AI recommendation for unknown anomalies
        if (_config.AIRemediationEnabled)
        {
            return await GetAIRemediationRecommendationAsync(anomaly, incident, ct);
        }

        return null;
    }

    /// <summary>
    /// Executes a remediation action.
    /// </summary>
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
    public void RegisterRemediation(RemediationAction action)
    {
        _remediationActions[action.Id] = action;
    }

    /// <summary>
    /// Gets active incidents.
    /// </summary>
    public IEnumerable<IncidentRecord> GetActiveIncidents()
    {
        return _activeIncidents.Values
            .Where(i => i.Status == IncidentStatus.Active || i.Status == IncidentStatus.Investigating)
            .OrderByDescending(i => i.Severity)
            .ThenByDescending(i => i.LastOccurrence);
    }

    /// <summary>
    /// Gets anomaly statistics.
    /// </summary>
    public AnomalyEngineStatistics GetStatistics()
    {
        var stats = new Dictionary<string, AnomalyStatistics>();

        foreach (var metricName in _activeIncidents.Values.Select(i => i.MetricName).Distinct())
        {
            stats[metricName] = _detector.GetStatistics(metricName);
        }

        return new AnomalyEngineStatistics
        {
            ActiveIncidents = _activeIncidents.Count(i => i.Value.Status == IncidentStatus.Active),
            ResolvedIncidents = _activeIncidents.Count(i => i.Value.Status == IncidentStatus.Resolved),
            TotalRemediations = _remediationHistory.Count,
            SuccessfulRemediations = _remediationHistory.Count(r => r.Success),
            MetricStatistics = stats,
            RecentRemediations = _remediationHistory.TakeLast(20).ToList()
        };
    }

    private async Task DetectionCycleAsync(CancellationToken ct)
    {
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
    }

    private async Task<AIRootCauseAnalysis> GetAIRootCauseAnalysisAsync(MultivariateAnomalyResult anomaly, CancellationToken ct)
    {
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

        var response = await _providerSelector.ExecuteWithFailoverAsync(
            AIOpsCapabilities.AnomalyDetection,
            prompt,
            null,
            ct);

        if (response.Success && !string.IsNullOrEmpty(response.Content))
        {
            var jsonStart = response.Content.IndexOf('{');
            var jsonEnd = response.Content.LastIndexOf('}');

            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response.Content.Substring(jsonStart, jsonEnd - jsonStart + 1);
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

        var response = await _providerSelector.ExecuteWithFailoverAsync(
            AIOpsCapabilities.AnomalyDetection,
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
                        Source = AnomalyRecommendationSource.AI
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _detectionTimer.Dispose();
        _remediationLock.Dispose();
    }
}

#region Supporting Types

public sealed class AnomalyEngineConfig
{
    public int WindowSize { get; set; } = 100;
    public double AnomalyThreshold { get; set; } = 2.5;
    public int DetectionIntervalSeconds { get; set; } = 30;
    public bool AutoRemediationEnabled { get; set; } = true;
    public AnomalySeverity AutoRemediationMinSeverity { get; set; } = AnomalySeverity.Medium;
    public bool AIRemediationEnabled { get; set; } = true;
    public int MaxConcurrentRemediations { get; set; } = 5;
    public int IncidentRetentionHours { get; set; } = 168; // 7 days
}

public sealed class AnomalyDetectionResult
{
    public string MetricName { get; init; } = string.Empty;
    public double Value { get; init; }
    public bool IsAnomaly { get; init; }
    public AnomalyResult? Anomaly { get; init; }
    public IncidentRecord? Incident { get; init; }
    public RemediationRecommendation? Remediation { get; init; }
}

public sealed class MultivariateAnalysisResult
{
    public bool IsAnomaly { get; init; }
    public Dictionary<string, double> Metrics { get; init; } = new();
    public MultivariateAnomalyResult? Anomaly { get; init; }
    public string? RootCauseAnalysis { get; init; }
    public List<string> PotentialCauses { get; init; } = new();
}

public sealed class IncidentRecord
{
    public string Id { get; init; } = string.Empty;
    public string MetricName { get; init; } = string.Empty;
    public AnomalyType AnomalyType { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime LastOccurrence { get; set; }
    public DateTime? ResolvedTime { get; set; }
    public AnomalySeverity Severity { get; set; }
    public IncidentStatus Status { get; set; } = IncidentStatus.Active;
    public int OccurrenceCount { get; set; }
    public string? Resolution { get; set; }
    public List<AnomalyResult> Anomalies { get; } = new();
}

public enum IncidentStatus
{
    Active,
    Investigating,
    Resolved,
    Closed
}

public sealed class RemediationAction
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool AutoExecute { get; init; }
    public bool RequiresApproval { get; init; }
    public AnomalySeverity MinSeverity { get; init; }
    public string EstimatedImpact { get; init; } = string.Empty;

    public Func<CancellationToken, Task<ActionResult>>? ExecuteHandler { get; set; }

    public async Task<ActionResult> ExecuteAsync(CancellationToken ct)
    {
        if (ExecuteHandler != null)
        {
            return await ExecuteHandler(ct);
        }

        // Default no-op execution
        return new ActionResult { Success = true, Message = "Action registered (no handler defined)" };
    }
}

public sealed class ActionResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class RemediationRecommendation
{
    public string IncidentId { get; init; } = string.Empty;
    public string ActionId { get; init; } = string.Empty;
    public string ActionName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool AutoExecute { get; init; }
    public bool RequiresApproval { get; init; }
    public string EstimatedImpact { get; init; } = string.Empty;
    public double Confidence { get; init; }
    public AnomalyRecommendationSource Source { get; init; }
}

public enum AnomalyRecommendationSource
{
    RuleBased,
    AI,
    Historical
}

public sealed class RemediationResult
{
    public string RecommendationId { get; init; } = string.Empty;
    public bool Success { get; init; }
    public TimeSpan ExecutionTime { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class RemediationEvent
{
    public string IncidentId { get; init; } = string.Empty;
    public string ActionId { get; init; } = string.Empty;
    public string ActionName { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class AIRootCauseAnalysis
{
    public string RootCause { get; init; } = string.Empty;
    public List<string> PotentialCauses { get; init; } = new();
    public double Confidence { get; init; }
    public List<string> RecommendedActions { get; init; } = new();
}

public sealed class AnomalyEngineStatistics
{
    public int ActiveIncidents { get; init; }
    public int ResolvedIncidents { get; init; }
    public int TotalRemediations { get; init; }
    public int SuccessfulRemediations { get; init; }
    public double RemediationSuccessRate => TotalRemediations > 0 ? (double)SuccessfulRemediations / TotalRemediations : 0;
    public Dictionary<string, AnomalyStatistics> MetricStatistics { get; init; } = new();
    public List<RemediationEvent> RecentRemediations { get; init; } = new();
}

#endregion
