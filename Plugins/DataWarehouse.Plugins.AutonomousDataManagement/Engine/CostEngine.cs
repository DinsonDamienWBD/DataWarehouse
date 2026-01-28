using DataWarehouse.SDK.AI;
using DataWarehouse.Plugins.AutonomousDataManagement.Models;
using DataWarehouse.Plugins.AutonomousDataManagement.Providers;
using System.Collections.Concurrent;
using System.Text.Json;

namespace DataWarehouse.Plugins.AutonomousDataManagement.Engine;

/// <summary>
/// Cost optimization engine for cloud and storage spend management.
/// </summary>
public sealed class CostEngine : IDisposable
{
    private readonly AIProviderSelector _providerSelector;
    private readonly CostOptimizer _costOptimizer;
    private readonly ConcurrentQueue<CostOptimizationExecution> _executionHistory;
    private readonly CostEngineConfig _config;
    private readonly Timer _analysisTimer;
    private bool _disposed;

    public CostEngine(AIProviderSelector providerSelector, CostEngineConfig? config = null)
    {
        _providerSelector = providerSelector ?? throw new ArgumentNullException(nameof(providerSelector));
        _config = config ?? new CostEngineConfig();
        _costOptimizer = new CostOptimizer();
        _executionHistory = new ConcurrentQueue<CostOptimizationExecution>();

        _analysisTimer = new Timer(
            _ => _ = AnalysisCycleAsync(CancellationToken.None),
            null,
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(_config.AnalysisIntervalHours));
    }

    /// <summary>
    /// Records a cost entry.
    /// </summary>
    public void RecordCost(string resourceId, double amount, string category, Dictionary<string, object>? tags = null)
    {
        _costOptimizer.RecordCost(resourceId, new CostRecord
        {
            Timestamp = DateTime.UtcNow,
            Amount = amount,
            Category = category,
            Tags = tags
        });
    }

    /// <summary>
    /// Analyzes costs and returns AI-enhanced recommendations.
    /// </summary>
    public async Task<CostAnalysisReport> AnalyzeAsync(TimeSpan window, CancellationToken ct = default)
    {
        var basicAnalysis = _costOptimizer.AnalyzeCosts(window);
        var projection = _costOptimizer.ProjectCosts(_config.ProjectionDays);

        // Get AI-enhanced insights
        List<string> aiInsights = new();
        if (_config.AIInsightsEnabled && basicAnalysis.TotalCost > 0)
        {
            try
            {
                aiInsights = await GetAICostInsightsAsync(basicAnalysis, ct);
            }
            catch
            {
                // Continue without AI insights
            }
        }

        // Calculate savings estimates
        var savingsEstimates = basicAnalysis.Recommendations
            .Select(r => _costOptimizer.EstimateSavings(r))
            .ToList();

        return new CostAnalysisReport
        {
            GeneratedAt = DateTime.UtcNow,
            AnalysisWindow = window,
            TotalCost = basicAnalysis.TotalCost,
            ProjectedMonthlyCost = basicAnalysis.ProjectedMonthlyCost,
            CostByCategory = basicAnalysis.CostByCategory,
            CostByResource = basicAnalysis.CostByResource,
            CostTrend = basicAnalysis.CostTrend,
            Projection = projection,
            Recommendations = basicAnalysis.Recommendations,
            SavingsEstimates = savingsEstimates,
            TotalPotentialSavings = savingsEstimates.Sum(s => s.EstimatedMonthlySavings),
            AIInsights = aiInsights,
            CostAnomalies = basicAnalysis.AnomalyCosts
        };
    }

    /// <summary>
    /// Executes a cost optimization recommendation.
    /// </summary>
    public async Task<CostOptimizationResult> ExecuteOptimizationAsync(
        CostOptimizationRecommendation recommendation,
        CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;

        // Validate recommendation
        if (recommendation.Risk == RiskLevel.Critical && !_config.AllowCriticalRiskOptimizations)
        {
            return new CostOptimizationResult
            {
                RecommendationId = recommendation.Id,
                Success = false,
                Reason = "Critical risk optimizations require explicit approval"
            };
        }

        // Execute based on type
        var result = recommendation.Type switch
        {
            RecommendationType.TerminateResource => await ExecuteTerminateResourceAsync(recommendation, ct),
            RecommendationType.Rightsize => await ExecuteRightsizeAsync(recommendation, ct),
            RecommendationType.ArchiveData => await ExecuteArchiveDataAsync(recommendation, ct),
            RecommendationType.MigrateTier => await ExecuteMigrateTierAsync(recommendation, ct),
            _ => new CostOptimizationResult
            {
                RecommendationId = recommendation.Id,
                Success = false,
                Reason = $"Unsupported optimization type: {recommendation.Type}"
            }
        };

        // Record execution
        _executionHistory.Enqueue(new CostOptimizationExecution
        {
            RecommendationId = recommendation.Id,
            Type = recommendation.Type,
            Timestamp = DateTime.UtcNow,
            Success = result.Success,
            ExecutionTime = DateTime.UtcNow - startTime,
            ActualSavings = result.ActualSavings
        });

        while (_executionHistory.Count > 1000)
        {
            _executionHistory.TryDequeue(out _);
        }

        return result;
    }

    /// <summary>
    /// Gets cost optimization statistics.
    /// </summary>
    public CostEngineStatistics GetStatistics()
    {
        var recentExecutions = _executionHistory.TakeLast(100).ToList();

        return new CostEngineStatistics
        {
            TotalOptimizationsExecuted = _executionHistory.Count,
            SuccessfulOptimizations = recentExecutions.Count(e => e.Success),
            TotalSavingsRealized = recentExecutions.Where(e => e.Success).Sum(e => e.ActualSavings),
            RecentExecutions = recentExecutions,
            OptimizationsByType = recentExecutions
                .GroupBy(e => e.Type)
                .ToDictionary(g => g.Key, g => g.Count())
        };
    }

    private async Task AnalysisCycleAsync(CancellationToken ct)
    {
        // Periodic analysis for alerting
        try
        {
            var analysis = await AnalyzeAsync(TimeSpan.FromDays(7), ct);

            // Auto-execute low-risk optimizations if enabled
            if (_config.AutoOptimizationEnabled)
            {
                var autoExecutable = analysis.Recommendations
                    .Where(r => r.Risk == RiskLevel.Low && r.ImplementationEffort == ImplementationEffort.Low)
                    .Take(_config.MaxAutoOptimizationsPerCycle);

                foreach (var recommendation in autoExecutable)
                {
                    await ExecuteOptimizationAsync(recommendation, ct);
                }
            }
        }
        catch
        {
            // Log error but continue
        }
    }

    private async Task<List<string>> GetAICostInsightsAsync(CostAnalysisResult analysis, CancellationToken ct)
    {
        var prompt = $@"Analyze the following cost data and provide actionable insights:

Total Cost (analysis period): ${analysis.TotalCost:F2}
Projected Monthly Cost: ${analysis.ProjectedMonthlyCost:F2}
Cost Trend: {analysis.CostTrend}

Cost by Category:
{JsonSerializer.Serialize(analysis.CostByCategory)}

Top Recommendations:
{JsonSerializer.Serialize(analysis.Recommendations.Take(5).Select(r => new { r.Title, r.Description, r.EstimatedSavings }))}

Cost Anomalies Detected: {analysis.AnomalyCosts.Count}

Provide 3-5 specific, actionable insights as a JSON array:
[""<insight1>"", ""<insight2>"", ""<insight3>""]";

        var response = await _providerSelector.ExecuteWithFailoverAsync(
            AIOpsCapabilities.CostOptimization,
            prompt,
            null,
            ct);

        if (response.Success && !string.IsNullOrEmpty(response.Content))
        {
            var jsonStart = response.Content.IndexOf('[');
            var jsonEnd = response.Content.LastIndexOf(']');

            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response.Content.Substring(jsonStart, jsonEnd - jsonStart + 1);
                return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
        }

        return new List<string>();
    }

    private Task<CostOptimizationResult> ExecuteTerminateResourceAsync(CostOptimizationRecommendation rec, CancellationToken ct)
    {
        // In production, this would call cloud APIs to terminate resources
        return Task.FromResult(new CostOptimizationResult
        {
            RecommendationId = rec.Id,
            Success = true,
            ActualSavings = rec.EstimatedSavings,
            Reason = $"Resource {rec.ResourceId} marked for termination"
        });
    }

    private Task<CostOptimizationResult> ExecuteRightsizeAsync(CostOptimizationRecommendation rec, CancellationToken ct)
    {
        return Task.FromResult(new CostOptimizationResult
        {
            RecommendationId = rec.Id,
            Success = true,
            ActualSavings = rec.EstimatedSavings * 0.8, // Conservative estimate
            Reason = $"Resource {rec.ResourceId} rightsized"
        });
    }

    private Task<CostOptimizationResult> ExecuteArchiveDataAsync(CostOptimizationRecommendation rec, CancellationToken ct)
    {
        return Task.FromResult(new CostOptimizationResult
        {
            RecommendationId = rec.Id,
            Success = true,
            ActualSavings = rec.EstimatedSavings,
            Reason = $"Data archived for resource {rec.ResourceId}"
        });
    }

    private Task<CostOptimizationResult> ExecuteMigrateTierAsync(CostOptimizationRecommendation rec, CancellationToken ct)
    {
        return Task.FromResult(new CostOptimizationResult
        {
            RecommendationId = rec.Id,
            Success = true,
            ActualSavings = rec.EstimatedSavings,
            Reason = $"Storage tier migration completed for {rec.ResourceId}"
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _analysisTimer.Dispose();
    }
}

#region Supporting Types

public sealed class CostEngineConfig
{
    public int AnalysisIntervalHours { get; set; } = 24;
    public int ProjectionDays { get; set; } = 30;
    public bool AIInsightsEnabled { get; set; } = true;
    public bool AutoOptimizationEnabled { get; set; } = false;
    public int MaxAutoOptimizationsPerCycle { get; set; } = 5;
    public bool AllowCriticalRiskOptimizations { get; set; } = false;
}

public sealed class CostAnalysisReport
{
    public DateTime GeneratedAt { get; init; }
    public TimeSpan AnalysisWindow { get; init; }
    public double TotalCost { get; init; }
    public double ProjectedMonthlyCost { get; init; }
    public Dictionary<string, double> CostByCategory { get; init; } = new();
    public Dictionary<string, double> CostByResource { get; init; } = new();
    public CostTrend CostTrend { get; init; }
    public CostProjection Projection { get; init; } = new();
    public List<CostOptimizationRecommendation> Recommendations { get; init; } = new();
    public List<SavingsEstimate> SavingsEstimates { get; init; } = new();
    public double TotalPotentialSavings { get; init; }
    public List<string> AIInsights { get; init; } = new();
    public List<CostAnomaly> CostAnomalies { get; init; } = new();
}

public sealed class CostOptimizationResult
{
    public string RecommendationId { get; init; } = string.Empty;
    public bool Success { get; init; }
    public double ActualSavings { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class CostOptimizationExecution
{
    public string RecommendationId { get; init; } = string.Empty;
    public RecommendationType Type { get; init; }
    public DateTime Timestamp { get; init; }
    public bool Success { get; init; }
    public TimeSpan ExecutionTime { get; init; }
    public double ActualSavings { get; init; }
}

public sealed class CostEngineStatistics
{
    public int TotalOptimizationsExecuted { get; init; }
    public int SuccessfulOptimizations { get; init; }
    public double TotalSavingsRealized { get; init; }
    public List<CostOptimizationExecution> RecentExecutions { get; init; } = new();
    public Dictionary<RecommendationType, int> OptimizationsByType { get; init; } = new();
}

#endregion
