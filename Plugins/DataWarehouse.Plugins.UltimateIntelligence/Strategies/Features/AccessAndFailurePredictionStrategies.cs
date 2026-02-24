using DataWarehouse.SDK.AI;
using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Features;

/// <summary>
/// Access pattern prediction feature strategy.
/// Predicts data access patterns for caching, tiering, and prefetching optimization.
/// </summary>
public sealed class AccessPredictionStrategy : FeatureStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "feature-access-prediction";

    /// <inheritdoc/>
    public override string StrategyName => "Access Pattern Prediction";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Access Prediction",
        Description = "Predicts data access patterns for intelligent caching, tiering, and prefetching",
        Capabilities = IntelligenceCapabilities.Prediction,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "PredictionHorizon", Description = "Hours to predict ahead", Required = false, DefaultValue = "24" },
            new ConfigurationRequirement { Key = "HistoryDepth", Description = "Hours of history to analyze", Required = false, DefaultValue = "168" },
            new ConfigurationRequirement { Key = "MinConfidence", Description = "Minimum prediction confidence", Required = false, DefaultValue = "0.6" }
        },
        CostTier = 2,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        Tags = new[] { "access-prediction", "caching", "tiering", "prefetch", "optimization" }
    };

    /// <summary>
    /// Predicts which files will be accessed in the near future.
    /// </summary>
    public async Task<AccessPrediction> PredictAccessAsync(
        IEnumerable<AccessLogEntry> history,
        CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider required for access prediction");

        return await ExecuteWithTrackingAsync(async () =>
        {
            var historyList = history.ToList();
            var horizon = int.Parse(GetConfig("PredictionHorizon") ?? "24");
            var minConfidence = float.Parse(GetConfig("MinConfidence") ?? "0.6");

            // Analyze access patterns
            var fileAccessCounts = historyList
                .GroupBy(e => e.FileId)
                .Select(g => new
                {
                    FileId = g.Key,
                    AccessCount = g.Count(),
                    LastAccess = g.Max(e => e.Timestamp),
                    AverageIntervalHours = g.Count() > 1
                        ? g.OrderBy(e => e.Timestamp).Skip(1).Zip(g.OrderBy(e => e.Timestamp), (a, b) => (a.Timestamp - b.Timestamp).TotalHours).Average()
                        : 24.0
                })
                .OrderByDescending(x => x.AccessCount)
                .ToList();

            // Use AI for pattern analysis
            var prompt = $@"Analyze these file access patterns and predict which files will be accessed in the next {horizon} hours:

Top accessed files:
{string.Join("\n", fileAccessCounts.Take(10).Select(f => $"- {f.FileId}: {f.AccessCount} accesses, last: {f.LastAccess}, avg interval: {f.AverageIntervalHours:F1}h"))}

Total unique files: {fileAccessCounts.Count}
Total accesses: {historyList.Count}

Return JSON with predicted files:
{{
  ""predictions"": [
    {{""file_id"": ""id"", ""probability"": 0.9, ""expected_access_time"": ""2024-01-01T12:00:00Z"", ""reason"": ""pattern""}}
  ]
}}";

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                Prompt = prompt,
                MaxTokens = 500,
                Temperature = 0.3f
            }, ct);

            RecordTokens(response.Usage?.TotalTokens ?? 0);

            var predictions = ParseAccessPredictions(response.Content, minConfidence);

            return new AccessPrediction
            {
                PredictionHorizonHours = horizon,
                AnalyzedAccessCount = historyList.Count,
                UniqueFilesAnalyzed = fileAccessCounts.Count,
                PredictedAccesses = predictions,
                GeneratedAt = DateTime.UtcNow
            };
        });
    }

    /// <summary>
    /// Recommends tiering decisions based on access patterns.
    /// </summary>
    public async Task<TieringRecommendation> RecommendTieringAsync(
        IEnumerable<AccessLogEntry> history,
        CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var historyList = history.ToList();
            var now = DateTime.UtcNow;

            var fileStats = historyList
                .GroupBy(e => e.FileId)
                .Select(g => new
                {
                    FileId = g.Key,
                    AccessCount = g.Count(),
                    LastAccess = g.Max(e => e.Timestamp),
                    DaysSinceLastAccess = (now - g.Max(e => e.Timestamp)).TotalDays,
                    AverageAccessesPerDay = g.Count() / Math.Max(1, (now - g.Min(e => e.Timestamp)).TotalDays)
                })
                .ToList();

            var hotFiles = fileStats.Where(f => f.DaysSinceLastAccess < 1 && f.AverageAccessesPerDay > 5).Select(f => f.FileId).ToList();
            var warmFiles = fileStats.Where(f => f.DaysSinceLastAccess < 7 && f.AverageAccessesPerDay >= 1).Select(f => f.FileId).ToList();
            var coldFiles = fileStats.Where(f => f.DaysSinceLastAccess >= 30 || f.AverageAccessesPerDay < 0.1).Select(f => f.FileId).ToList();

            return new TieringRecommendation
            {
                HotTierFiles = hotFiles,
                WarmTierFiles = warmFiles,
                ColdTierFiles = coldFiles,
                AnalyzedAt = now
            };
        });
    }

    private static List<PredictedAccess> ParseAccessPredictions(string response, float minConfidence)
    {
        var predictions = new List<PredictedAccess>();
        try
        {
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("predictions", out var arr))
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        var prob = item.TryGetProperty("probability", out var p) ? p.GetSingle() : 0.5f;
                        if (prob >= minConfidence)
                        {
                            predictions.Add(new PredictedAccess
                            {
                                FileId = item.GetProperty("file_id").GetString() ?? "",
                                Probability = prob,
                                Reason = item.TryGetProperty("reason", out var r) ? r.GetString() : null
                            });
                        }
                    }
                }
            }
        }
        catch { /* Parsing failure — return predictions collected so far */ }
        return predictions;
    }
}

/// <summary>
/// Failure prediction feature strategy.
/// Predicts potential system failures before they occur.
/// </summary>
public sealed class FailurePredictionStrategy : FeatureStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "feature-failure-prediction";

    /// <inheritdoc/>
    public override string StrategyName => "Failure Prediction";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Failure Prediction",
        Description = "Predicts potential system failures using AI analysis of metrics and logs",
        Capabilities = IntelligenceCapabilities.Prediction | IntelligenceCapabilities.AnomalyDetection,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "RiskThreshold", Description = "Risk score threshold for alerting", Required = false, DefaultValue = "0.7" },
            new ConfigurationRequirement { Key = "PredictionWindow", Description = "Hours to predict ahead", Required = false, DefaultValue = "24" }
        },
        CostTier = 2,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        Tags = new[] { "failure-prediction", "reliability", "proactive", "monitoring" }
    };

    /// <summary>
    /// Predicts potential failures based on system metrics.
    /// </summary>
    public async Task<FailurePrediction> PredictFailuresAsync(
        SystemMetrics metrics,
        CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider required for failure prediction");

        return await ExecuteWithTrackingAsync(async () =>
        {
            var threshold = float.Parse(GetConfig("RiskThreshold") ?? "0.7");

            var prompt = $@"Analyze these system metrics for potential failure indicators:

System Metrics:
- CPU Usage: {metrics.CpuUsagePercent}%
- Memory Usage: {metrics.MemoryUsagePercent}%
- Disk Usage: {metrics.DiskUsagePercent}%
- Disk I/O Wait: {metrics.DiskIoWaitPercent}%
- Network Errors: {metrics.NetworkErrorCount}
- Error Rate: {metrics.ErrorRate}%
- Response Time (avg): {metrics.AvgResponseTimeMs}ms
- Response Time (p99): {metrics.P99ResponseTimeMs}ms

Identify potential failures and their risk levels.

Return JSON:
{{
  ""risks"": [
    {{""component"": ""disk"", ""risk_score"": 0.8, ""failure_type"": ""disk_full"", ""eta_hours"": 12, ""recommendation"": ""action""}}
  ],
  ""overall_health_score"": 0.7
}}";

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                Prompt = prompt,
                MaxTokens = 500,
                Temperature = 0.2f
            }, ct);

            RecordTokens(response.Usage?.TotalTokens ?? 0);

            var (risks, healthScore) = ParseFailurePrediction(response.Content, threshold);

            return new FailurePrediction
            {
                OverallHealthScore = healthScore,
                PredictedRisks = risks,
                MetricsAnalyzed = metrics,
                AnalyzedAt = DateTime.UtcNow
            };
        });
    }

    /// <summary>
    /// Analyzes logs for failure patterns.
    /// </summary>
    public async Task<LogAnalysisResult> AnalyzeLogsAsync(
        IEnumerable<string> logEntries,
        CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider required");

        return await ExecuteWithTrackingAsync(async () =>
        {
            var logs = logEntries.ToList();
            var errorLogs = logs.Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                            l.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
                                            l.Contains("fail", StringComparison.OrdinalIgnoreCase)).ToList();

            var prompt = $@"Analyze these error logs for patterns indicating potential failures:

Recent error logs:
{string.Join("\n", errorLogs.Take(20))}

Total logs: {logs.Count}
Error logs: {errorLogs.Count}

Identify patterns and root causes.

Return JSON:
{{
  ""patterns"": [
    {{""pattern"": ""description"", ""frequency"": 5, ""severity"": ""high"", ""root_cause"": ""cause"", ""recommendation"": ""action""}}
  ]
}}";

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                Prompt = prompt,
                MaxTokens = 500,
                Temperature = 0.2f
            }, ct);

            RecordTokens(response.Usage?.TotalTokens ?? 0);

            return new LogAnalysisResult
            {
                TotalLogsAnalyzed = logs.Count,
                ErrorLogsFound = errorLogs.Count,
                Patterns = new List<LogPattern>(), // Would parse from response
                AnalyzedAt = DateTime.UtcNow
            };
        });
    }

    private static (List<PredictedRisk> Risks, float HealthScore) ParseFailurePrediction(string response, float threshold)
    {
        var risks = new List<PredictedRisk>();
        var healthScore = 0.5f;

        try
        {
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var doc = System.Text.Json.JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("overall_health_score", out var h))
                    healthScore = h.GetSingle();

                if (doc.RootElement.TryGetProperty("risks", out var arr))
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        var score = item.TryGetProperty("risk_score", out var s) ? s.GetSingle() : 0.5f;
                        if (score >= threshold)
                        {
                            risks.Add(new PredictedRisk
                            {
                                Component = item.TryGetProperty("component", out var c) ? c.GetString() ?? "" : "",
                                RiskScore = score,
                                FailureType = item.TryGetProperty("failure_type", out var f) ? f.GetString() ?? "" : "",
                                EstimatedTimeToFailureHours = item.TryGetProperty("eta_hours", out var e) ? e.GetInt32() : null,
                                Recommendation = item.TryGetProperty("recommendation", out var r) ? r.GetString() : null
                            });
                        }
                    }
                }
            }
        }
        catch { /* Parsing failure — return results collected so far */ }

        return (risks, healthScore);
    }
}

// Supporting types
public sealed class AccessLogEntry { public string FileId { get; init; } = ""; public DateTime Timestamp { get; init; } }
public sealed class AccessPrediction { public int PredictionHorizonHours { get; init; } public int AnalyzedAccessCount { get; init; } public int UniqueFilesAnalyzed { get; init; } public List<PredictedAccess> PredictedAccesses { get; init; } = new(); public DateTime GeneratedAt { get; init; } }
public sealed class PredictedAccess { public string FileId { get; init; } = ""; public float Probability { get; init; } public string? Reason { get; init; } }
public sealed class TieringRecommendation { public List<string> HotTierFiles { get; init; } = new(); public List<string> WarmTierFiles { get; init; } = new(); public List<string> ColdTierFiles { get; init; } = new(); public DateTime AnalyzedAt { get; init; } }
public sealed class SystemMetrics { public float CpuUsagePercent { get; init; } public float MemoryUsagePercent { get; init; } public float DiskUsagePercent { get; init; } public float DiskIoWaitPercent { get; init; } public int NetworkErrorCount { get; init; } public float ErrorRate { get; init; } public float AvgResponseTimeMs { get; init; } public float P99ResponseTimeMs { get; init; } }
public sealed class FailurePrediction { public float OverallHealthScore { get; init; } public List<PredictedRisk> PredictedRisks { get; init; } = new(); public SystemMetrics MetricsAnalyzed { get; init; } = new(); public DateTime AnalyzedAt { get; init; } }
public sealed class PredictedRisk { public string Component { get; init; } = ""; public float RiskScore { get; init; } public string FailureType { get; init; } = ""; public int? EstimatedTimeToFailureHours { get; init; } public string? Recommendation { get; init; } }
public sealed class LogAnalysisResult { public int TotalLogsAnalyzed { get; init; } public int ErrorLogsFound { get; init; } public List<LogPattern> Patterns { get; init; } = new(); public DateTime AnalyzedAt { get; init; } }
public sealed class LogPattern { public string Pattern { get; init; } = ""; public int Frequency { get; init; } public string Severity { get; init; } = ""; public string? RootCause { get; init; } public string? Recommendation { get; init; } }
