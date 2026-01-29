// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.SDK.AI;
using System.Collections.Concurrent;
using System.Text.Json;
using IExtendedAIProvider = DataWarehouse.Plugins.AIAgents.IExtendedAIProvider;

namespace DataWarehouse.Plugins.AIAgents.Capabilities.AIOps;

/// <summary>
/// Cost optimization engine for cloud resource and storage cost analysis.
/// Uses rule-based analysis combined with AI-enhanced recommendations to
/// identify cost optimization opportunities and provide actionable insights.
/// </summary>
/// <remarks>
/// <para>
/// The cost engine provides:
/// - Real-time cost tracking and recording
/// - Rule-based cost optimization analysis
/// - AI-enhanced cost recommendations
/// - Cost projection and forecasting
/// - Cost anomaly detection
/// </para>
/// <para>
/// The engine gracefully degrades to rule-based-only recommendations when AI is unavailable.
/// </para>
/// </remarks>
public sealed class CostEngine : IDisposable
{
    private readonly IExtendedAIProvider? _aiProvider;
    private readonly CostEngineConfig _config;
    private readonly ConcurrentDictionary<string, ResourceCostData> _resourceCosts;
    private readonly List<ICostOptimizationRule> _rules;
    private readonly CostAnalysisEngine _analysisEngine;
    private readonly Timer? _analysisTimer;
    private CostAnalysisResult? _lastAnalysis;
    private bool _disposed;

    /// <summary>
    /// Creates a new cost engine instance.
    /// </summary>
    /// <param name="aiProvider">Optional AI provider for enhanced analysis.</param>
    /// <param name="config">Engine configuration.</param>
    public CostEngine(IExtendedAIProvider? aiProvider = null, CostEngineConfig? config = null)
    {
        _aiProvider = aiProvider;
        _config = config ?? new CostEngineConfig();
        _resourceCosts = new ConcurrentDictionary<string, ResourceCostData>();
        _rules = InitializeDefaultRules();
        _analysisEngine = new CostAnalysisEngine();

        if (_config.EnableAutonomousAnalysis)
        {
            _analysisTimer = new Timer(
                _ => _ = AnalysisCycleAsync(CancellationToken.None),
                null,
                TimeSpan.FromHours(1),
                TimeSpan.FromHours(_config.AnalysisIntervalHours));
        }
    }

    /// <summary>
    /// Records a cost entry for a resource.
    /// </summary>
    /// <param name="resourceId">The resource identifier.</param>
    /// <param name="cost">The cost record to add.</param>
    public void RecordCost(string resourceId, CostRecord cost)
    {
        var data = _resourceCosts.GetOrAdd(resourceId, _ => new ResourceCostData { ResourceId = resourceId });

        lock (data)
        {
            data.CostHistory.Add(cost);
            data.TotalCost += cost.Amount;
            data.LastUpdated = DateTime.UtcNow;

            // Update category totals
            if (!data.CostByCategory.ContainsKey(cost.Category))
                data.CostByCategory[cost.Category] = 0;
            data.CostByCategory[cost.Category] += cost.Amount;

            // Trim old history
            var cutoff = DateTime.UtcNow.AddDays(-_config.CostRetentionDays);
            data.CostHistory.RemoveAll(c => c.Timestamp < cutoff);
        }
    }

    /// <summary>
    /// Records usage metadata for cost optimization analysis.
    /// </summary>
    /// <param name="resourceId">The resource identifier.</param>
    /// <param name="metadata">The metadata dictionary.</param>
    public void RecordUsageMetadata(string resourceId, Dictionary<string, object> metadata)
    {
        var data = _resourceCosts.GetOrAdd(resourceId, _ => new ResourceCostData { ResourceId = resourceId });

        lock (data)
        {
            data.Metadata ??= new Dictionary<string, object>();
            foreach (var (key, value) in metadata)
            {
                data.Metadata[key] = value;
            }
        }
    }

    /// <summary>
    /// Analyzes costs and returns optimization recommendations.
    /// </summary>
    /// <param name="analysisWindow">The time window to analyze.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The cost analysis result.</returns>
    public async Task<CostAnalysisResult> AnalyzeCostsAsync(
        TimeSpan analysisWindow,
        CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - analysisWindow;
        var recommendations = new List<CostOptimizationRecommendation>();
        var totalCost = 0.0;
        var categoryCosts = new Dictionary<string, double>();
        var resourceCosts = new Dictionary<string, double>();

        foreach (var (resourceId, data) in _resourceCosts)
        {
            lock (data)
            {
                var recentCosts = data.CostHistory.Where(c => c.Timestamp >= cutoff).ToList();
                var resourceTotal = recentCosts.Sum(c => c.Amount);
                totalCost += resourceTotal;
                resourceCosts[resourceId] = resourceTotal;

                foreach (var cost in recentCosts)
                {
                    if (!categoryCosts.ContainsKey(cost.Category))
                        categoryCosts[cost.Category] = 0;
                    categoryCosts[cost.Category] += cost.Amount;
                }

                // Apply optimization rules
                foreach (var rule in _rules)
                {
                    var recommendation = rule.Evaluate(resourceId, data, recentCosts);
                    if (recommendation != null)
                    {
                        recommendations.Add(recommendation);
                    }
                }
            }
        }

        // Get AI recommendations if available
        if (_config.AIRecommendationsEnabled && _aiProvider != null && _aiProvider.IsAvailable && totalCost > 0)
        {
            try
            {
                var aiRecs = await GetAIRecommendationsAsync(categoryCosts, resourceCosts, totalCost, ct);
                recommendations.AddRange(aiRecs);
            }
            catch
            {
                // Continue with rule-based recommendations
            }
        }

        // Sort by potential savings
        recommendations = recommendations
            .GroupBy(r => r.ResourceId + r.Type)
            .Select(g => g.OrderByDescending(r => r.EstimatedSavings).First())
            .OrderByDescending(r => r.EstimatedSavings)
            .ToList();

        var result = new CostAnalysisResult
        {
            AnalysisTime = DateTime.UtcNow,
            AnalysisWindow = analysisWindow,
            TotalCost = totalCost,
            CostByCategory = categoryCosts,
            CostByResource = resourceCosts,
            Recommendations = recommendations,
            ProjectedMonthlyCost = totalCost * (30.0 / analysisWindow.TotalDays),
            CostTrend = _analysisEngine.CalculateCostTrend(_resourceCosts.Values.ToList()),
            AnomalyCosts = _analysisEngine.DetectCostAnomalies(_resourceCosts.Values.ToList(), cutoff)
        };

        _lastAnalysis = result;
        return result;
    }

    /// <summary>
    /// Projects future costs based on historical patterns.
    /// </summary>
    /// <param name="daysAhead">The number of days to project.</param>
    /// <returns>The cost projection.</returns>
    public CostProjection ProjectCosts(int daysAhead)
    {
        var historicalData = new List<(DateTime Date, double Cost)>();

        foreach (var data in _resourceCosts.Values)
        {
            lock (data)
            {
                foreach (var cost in data.CostHistory)
                {
                    historicalData.Add((cost.Timestamp.Date, cost.Amount));
                }
            }
        }

        // Group by day
        var dailyCosts = historicalData
            .GroupBy(h => h.Date)
            .OrderBy(g => g.Key)
            .Select(g => (Date: g.Key, Cost: g.Sum(x => x.Cost)))
            .ToList();

        if (dailyCosts.Count < 7)
        {
            return new CostProjection
            {
                ProjectionDays = daysAhead,
                Confidence = 0,
                DailyProjections = new List<DailyCostProjection>(),
                Reason = "Insufficient historical data for projection"
            };
        }

        // Linear regression for projection
        var trend = CalculateLinearTrend(dailyCosts.Select(d => d.Cost).ToList());
        var avgDailyCost = dailyCosts.TakeLast(30).DefaultIfEmpty().Average(d => d.Cost);

        var projections = new List<DailyCostProjection>();
        var lastDate = dailyCosts.Last().Date;

        for (int i = 1; i <= daysAhead; i++)
        {
            var date = lastDate.AddDays(i);
            var projectedCost = avgDailyCost + trend * i;

            // Apply day-of-week adjustment
            var dowMultiplier = GetDayOfWeekMultiplier(dailyCosts, date.DayOfWeek);
            projectedCost *= dowMultiplier;

            projections.Add(new DailyCostProjection
            {
                Date = date,
                ProjectedCost = Math.Max(0, projectedCost),
                LowerBound = Math.Max(0, projectedCost * 0.8),
                UpperBound = projectedCost * 1.2
            });
        }

        return new CostProjection
        {
            ProjectionDays = daysAhead,
            DailyProjections = projections,
            TotalProjectedCost = projections.Sum(p => p.ProjectedCost),
            AverageDailyCost = projections.Average(p => p.ProjectedCost),
            Trend = trend > 0.01 ? CostTrend.Increasing : trend < -0.01 ? CostTrend.Decreasing : CostTrend.Stable,
            Confidence = Math.Min(1.0, dailyCosts.Count / 90.0),
            Reason = "Linear regression with seasonal adjustment"
        };
    }

    /// <summary>
    /// Estimates savings from implementing a recommendation.
    /// </summary>
    /// <param name="recommendation">The recommendation to estimate.</param>
    /// <returns>The savings estimate.</returns>
    public SavingsEstimate EstimateSavings(CostOptimizationRecommendation recommendation)
    {
        return new SavingsEstimate
        {
            RecommendationId = recommendation.Id,
            EstimatedMonthlySavings = recommendation.EstimatedSavings * 30,
            EstimatedAnnualSavings = recommendation.EstimatedSavings * 365,
            ImplementationCost = recommendation.ImplementationEffort switch
            {
                ImplementationEffort.Low => 0,
                ImplementationEffort.Medium => recommendation.EstimatedSavings * 5,
                ImplementationEffort.High => recommendation.EstimatedSavings * 20,
                _ => 0
            },
            PaybackPeriodDays = recommendation.ImplementationEffort switch
            {
                ImplementationEffort.Low => 0,
                ImplementationEffort.Medium => 5,
                ImplementationEffort.High => 20,
                _ => 0
            },
            RiskLevel = recommendation.Risk
        };
    }

    /// <summary>
    /// Gets cost summary by category.
    /// </summary>
    /// <param name="since">The start time for the summary.</param>
    /// <returns>Category cost summary.</returns>
    public Dictionary<string, CategoryCostSummary> GetCostSummaryByCategory(DateTime since)
    {
        var summary = new Dictionary<string, CategoryCostSummary>();

        foreach (var data in _resourceCosts.Values)
        {
            lock (data)
            {
                foreach (var cost in data.CostHistory.Where(c => c.Timestamp >= since))
                {
                    if (!summary.ContainsKey(cost.Category))
                    {
                        summary[cost.Category] = new CategoryCostSummary { Category = cost.Category };
                    }

                    summary[cost.Category].TotalCost += cost.Amount;
                    summary[cost.Category].RecordCount++;
                    summary[cost.Category].Resources.Add(data.ResourceId);
                }
            }
        }

        // Calculate percentages
        var total = summary.Values.Sum(s => s.TotalCost);
        foreach (var cat in summary.Values)
        {
            cat.PercentOfTotal = total > 0 ? cat.TotalCost / total * 100 : 0;
        }

        return summary;
    }

    /// <summary>
    /// Gets cost engine statistics.
    /// </summary>
    /// <returns>The cost statistics.</returns>
    public CostEngineStatistics GetStatistics()
    {
        var allCosts = _resourceCosts.Values.SelectMany(d =>
        {
            lock (d) { return d.CostHistory.ToList(); }
        }).ToList();

        return new CostEngineStatistics
        {
            TrackedResources = _resourceCosts.Count,
            TotalCostRecords = allCosts.Count,
            TotalHistoricalCost = allCosts.Sum(c => c.Amount),
            OldestRecord = allCosts.Count > 0 ? allCosts.Min(c => c.Timestamp) : null,
            NewestRecord = allCosts.Count > 0 ? allCosts.Max(c => c.Timestamp) : null,
            LastAnalysisTime = _lastAnalysis?.AnalysisTime,
            RecommendationCount = _lastAnalysis?.Recommendations.Count ?? 0,
            PotentialSavings = _lastAnalysis?.Recommendations.Sum(r => r.EstimatedSavings) ?? 0
        };
    }

    /// <summary>
    /// Runs an analysis cycle.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The analysis result.</returns>
    public async Task<CostAnalysisResult> AnalysisCycleAsync(CancellationToken ct = default)
    {
        return await AnalyzeCostsAsync(TimeSpan.FromDays(_config.DefaultAnalysisWindowDays), ct);
    }

    #region Private Methods

    private async Task<List<CostOptimizationRecommendation>> GetAIRecommendationsAsync(
        Dictionary<string, double> categoryCosts,
        Dictionary<string, double> resourceCosts,
        double totalCost,
        CancellationToken ct)
    {
        if (_aiProvider == null)
            return new List<CostOptimizationRecommendation>();

        // Prepare cost summary
        var topResources = resourceCosts
            .OrderByDescending(kv => kv.Value)
            .Take(10)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var prompt = $@"Analyze the following cost data and provide optimization recommendations:

Total Cost: ${totalCost:F2}

Cost by Category:
{JsonSerializer.Serialize(categoryCosts, new JsonSerializerOptions { WriteIndented = true })}

Top 10 Resources by Cost:
{JsonSerializer.Serialize(topResources, new JsonSerializerOptions { WriteIndented = true })}

Provide 3-5 specific optimization recommendations as JSON:
[
  {{
    ""resourceId"": ""<resource or 'all'>"",
    ""type"": ""<Rightsize|TerminateResource|ArchiveData|PurchaseReserved|OptimizeTransfer|MigrateTier|EnableCompression|Consolidate>"",
    ""title"": ""<brief title>"",
    ""description"": ""<detailed recommendation>"",
    ""estimatedSavings"": <daily savings in USD>,
    ""implementationEffort"": ""<Low|Medium|High>"",
    ""risk"": ""<Low|Medium|High>""
  }}
]";

        var request = new AIRequest
        {
            Prompt = prompt,
            SystemMessage = "You are a cloud cost optimization expert. Analyze spending patterns and provide actionable recommendations to reduce costs while maintaining performance.",
            MaxTokens = 2048,
            Temperature = 0.3f
        };

        var response = await _aiProvider.CompleteAsync(request, ct);

        if (response.Success && !string.IsNullOrEmpty(response.Content))
        {
            try
            {
                var jsonStart = response.Content.IndexOf('[');
                var jsonEnd = response.Content.LastIndexOf(']');

                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var json = response.Content.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    var doc = JsonDocument.Parse(json);

                    return doc.RootElement.EnumerateArray().Select(elem =>
                    {
                        Enum.TryParse<CostRecommendationType>(
                            elem.TryGetProperty("type", out var t) ? t.GetString() : "Rightsize",
                            out var type);

                        Enum.TryParse<ImplementationEffort>(
                            elem.TryGetProperty("implementationEffort", out var e) ? e.GetString() : "Medium",
                            out var effort);

                        Enum.TryParse<RiskLevel>(
                            elem.TryGetProperty("risk", out var r) ? r.GetString() : "Low",
                            out var risk);

                        return new CostOptimizationRecommendation
                        {
                            Id = Guid.NewGuid().ToString(),
                            ResourceId = elem.TryGetProperty("resourceId", out var res) ? res.GetString() ?? "all" : "all",
                            Type = type,
                            Title = elem.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                            Description = elem.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
                            EstimatedSavings = elem.TryGetProperty("estimatedSavings", out var sav) ? sav.GetDouble() : 0,
                            ImplementationEffort = effort,
                            Risk = risk,
                            Source = RecommendationSource.AI
                        };
                    }).ToList();
                }
            }
            catch
            {
                // Fall through
            }
        }

        return new List<CostOptimizationRecommendation>();
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

    private static double GetDayOfWeekMultiplier(List<(DateTime Date, double Cost)> historicalCosts, DayOfWeek dow)
    {
        var dowCosts = historicalCosts.Where(c => c.Date.DayOfWeek == dow).Select(c => c.Cost).ToList();
        var allCosts = historicalCosts.Select(c => c.Cost).ToList();

        if (dowCosts.Count < 4 || allCosts.Count < 7) return 1.0;

        var dowAvg = dowCosts.Average();
        var allAvg = allCosts.Average();

        return allAvg > 0 ? dowAvg / allAvg : 1.0;
    }

    private static List<ICostOptimizationRule> InitializeDefaultRules()
    {
        return new List<ICostOptimizationRule>
        {
            new IdleResourceRule(),
            new OversizedResourceRule(),
            new UnusedStorageRule(),
            new ReservedCapacityRule(),
            new DataTransferRule()
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

        _analysisTimer?.Dispose();
    }
}

#region Configuration

/// <summary>
/// Configuration for the cost engine.
/// </summary>
public sealed class CostEngineConfig
{
    /// <summary>Gets or sets the cost retention period in days.</summary>
    public int CostRetentionDays { get; set; } = 90;

    /// <summary>Gets or sets the analysis interval in hours.</summary>
    public int AnalysisIntervalHours { get; set; } = 24;

    /// <summary>Gets or sets the default analysis window in days.</summary>
    public int DefaultAnalysisWindowDays { get; set; } = 30;

    /// <summary>Gets or sets whether AI recommendations are enabled.</summary>
    public bool AIRecommendationsEnabled { get; set; } = true;

    /// <summary>Gets or sets whether autonomous analysis is enabled.</summary>
    public bool EnableAutonomousAnalysis { get; set; } = true;
}

#endregion

#region Cost Analysis Engine

/// <summary>
/// Internal engine for cost trend and anomaly analysis.
/// </summary>
internal sealed class CostAnalysisEngine
{
    public CostTrend CalculateCostTrend(List<ResourceCostData> resources)
    {
        var dailyCosts = new Dictionary<DateTime, double>();

        foreach (var resource in resources)
        {
            lock (resource)
            {
                foreach (var cost in resource.CostHistory)
                {
                    var date = cost.Timestamp.Date;
                    if (!dailyCosts.ContainsKey(date))
                        dailyCosts[date] = 0;
                    dailyCosts[date] += cost.Amount;
                }
            }
        }

        if (dailyCosts.Count < 7) return CostTrend.Unknown;

        var sorted = dailyCosts.OrderBy(kv => kv.Key).ToList();
        var values = sorted.TakeLast(14).Select(kv => kv.Value).ToList();

        var n = values.Count;
        var sumX = Enumerable.Range(0, n).Sum();
        var sumY = values.Sum();
        var sumXY = Enumerable.Range(0, n).Select(i => i * values[i]).Sum();
        var sumX2 = Enumerable.Range(0, n).Select(i => i * i).Sum();

        var slope = (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);
        var avgCost = values.Average();
        var changeRate = avgCost > 0 ? slope / avgCost : 0;

        if (changeRate > 0.05) return CostTrend.Increasing;
        if (changeRate < -0.05) return CostTrend.Decreasing;
        return CostTrend.Stable;
    }

    public List<CostAnomaly> DetectCostAnomalies(List<ResourceCostData> resources, DateTime since)
    {
        var anomalies = new List<CostAnomaly>();

        foreach (var resource in resources)
        {
            lock (resource)
            {
                var recentCosts = resource.CostHistory
                    .Where(c => c.Timestamp >= since)
                    .OrderBy(c => c.Timestamp)
                    .ToList();

                if (recentCosts.Count < 10) continue;

                var values = recentCosts.Select(c => c.Amount).ToList();
                var mean = values.Average();
                var stdDev = Math.Sqrt(values.Sum(v => Math.Pow(v - mean, 2)) / values.Count);

                foreach (var cost in recentCosts)
                {
                    if (stdDev > 0.001)
                    {
                        var zScore = Math.Abs((cost.Amount - mean) / stdDev);
                        if (zScore > 2.5)
                        {
                            anomalies.Add(new CostAnomaly
                            {
                                ResourceId = resource.ResourceId,
                                Timestamp = cost.Timestamp,
                                ActualCost = cost.Amount,
                                ExpectedCost = mean,
                                Deviation = zScore,
                                Category = cost.Category
                            });
                        }
                    }
                }
            }
        }

        return anomalies.OrderByDescending(a => a.Deviation).ToList();
    }
}

#endregion

#region Cost Optimization Rules

/// <summary>
/// Interface for cost optimization rules.
/// </summary>
internal interface ICostOptimizationRule
{
    CostOptimizationRecommendation? Evaluate(string resourceId, ResourceCostData data, List<CostRecord> recentCosts);
}

internal sealed class IdleResourceRule : ICostOptimizationRule
{
    public CostOptimizationRecommendation? Evaluate(string resourceId, ResourceCostData data, List<CostRecord> recentCosts)
    {
        var computeCosts = recentCosts.Where(c => c.Category == "compute").ToList();
        if (computeCosts.Count < 7) return null;

        var avgCost = computeCosts.Average(c => c.Amount);
        var utilizationHint = data.Metadata?.GetValueOrDefault("utilization")?.ToString();

        if (double.TryParse(utilizationHint, out var utilization) && utilization < 10)
        {
            return new CostOptimizationRecommendation
            {
                Id = Guid.NewGuid().ToString(),
                ResourceId = resourceId,
                Type = CostRecommendationType.TerminateResource,
                Title = "Idle Resource Detected",
                Description = $"Resource has <10% utilization. Consider terminating or rightsizing.",
                EstimatedSavings = avgCost * 0.9,
                ImplementationEffort = ImplementationEffort.Low,
                Risk = RiskLevel.Low,
                Source = RecommendationSource.Rule
            };
        }

        return null;
    }
}

internal sealed class OversizedResourceRule : ICostOptimizationRule
{
    public CostOptimizationRecommendation? Evaluate(string resourceId, ResourceCostData data, List<CostRecord> recentCosts)
    {
        var utilizationHint = data.Metadata?.GetValueOrDefault("utilization")?.ToString();

        if (double.TryParse(utilizationHint, out var utilization) && utilization < 30 && utilization >= 10)
        {
            var avgCost = recentCosts.Average(c => c.Amount);
            return new CostOptimizationRecommendation
            {
                Id = Guid.NewGuid().ToString(),
                ResourceId = resourceId,
                Type = CostRecommendationType.Rightsize,
                Title = "Oversized Resource",
                Description = $"Resource utilization is {utilization:F0}%. Consider downsizing.",
                EstimatedSavings = avgCost * 0.5,
                ImplementationEffort = ImplementationEffort.Medium,
                Risk = RiskLevel.Medium,
                Source = RecommendationSource.Rule
            };
        }

        return null;
    }
}

internal sealed class UnusedStorageRule : ICostOptimizationRule
{
    public CostOptimizationRecommendation? Evaluate(string resourceId, ResourceCostData data, List<CostRecord> recentCosts)
    {
        var storageCosts = recentCosts.Where(c => c.Category == "storage").ToList();
        if (storageCosts.Count < 7) return null;

        var lastAccessHint = data.Metadata?.GetValueOrDefault("lastAccess")?.ToString();
        if (DateTime.TryParse(lastAccessHint, out var lastAccess))
        {
            var daysSinceAccess = (DateTime.UtcNow - lastAccess).TotalDays;
            if (daysSinceAccess > 90)
            {
                var avgCost = storageCosts.Average(c => c.Amount);
                return new CostOptimizationRecommendation
                {
                    Id = Guid.NewGuid().ToString(),
                    ResourceId = resourceId,
                    Type = CostRecommendationType.ArchiveData,
                    Title = "Unused Storage",
                    Description = $"Storage not accessed in {daysSinceAccess:F0} days. Consider archiving.",
                    EstimatedSavings = avgCost * 0.8,
                    ImplementationEffort = ImplementationEffort.Low,
                    Risk = RiskLevel.Low,
                    Source = RecommendationSource.Rule
                };
            }
        }

        return null;
    }
}

internal sealed class ReservedCapacityRule : ICostOptimizationRule
{
    public CostOptimizationRecommendation? Evaluate(string resourceId, ResourceCostData data, List<CostRecord> recentCosts)
    {
        if (recentCosts.Count < 30) return null;

        var avgDailyCost = recentCosts.GroupBy(c => c.Timestamp.Date).Average(g => g.Sum(c => c.Amount));
        var dailyTotals = recentCosts.GroupBy(c => c.Timestamp.Date).Select(g => g.Sum(c => c.Amount)).ToList();
        var stdDev = Math.Sqrt(dailyTotals.Select(v => Math.Pow(v - avgDailyCost, 2)).Average());

        var coefficientOfVariation = avgDailyCost > 0 ? stdDev / avgDailyCost : 1;
        if (coefficientOfVariation < 0.2 && avgDailyCost > 10)
        {
            return new CostOptimizationRecommendation
            {
                Id = Guid.NewGuid().ToString(),
                ResourceId = resourceId,
                Type = CostRecommendationType.PurchaseReserved,
                Title = "Reserved Capacity Opportunity",
                Description = "Stable usage pattern detected. Reserved capacity could save ~30%.",
                EstimatedSavings = avgDailyCost * 0.3,
                ImplementationEffort = ImplementationEffort.Medium,
                Risk = RiskLevel.Medium,
                Source = RecommendationSource.Rule
            };
        }

        return null;
    }
}

internal sealed class DataTransferRule : ICostOptimizationRule
{
    public CostOptimizationRecommendation? Evaluate(string resourceId, ResourceCostData data, List<CostRecord> recentCosts)
    {
        var transferCosts = recentCosts.Where(c => c.Category == "data-transfer").ToList();
        if (transferCosts.Count < 7) return null;

        var avgCost = transferCosts.Average(c => c.Amount);
        var totalCost = recentCosts.Sum(c => c.Amount);

        if (totalCost > 0 && transferCosts.Sum(c => c.Amount) / totalCost > 0.3)
        {
            return new CostOptimizationRecommendation
            {
                Id = Guid.NewGuid().ToString(),
                ResourceId = resourceId,
                Type = CostRecommendationType.OptimizeTransfer,
                Title = "High Data Transfer Costs",
                Description = "Data transfer costs are >30% of total. Consider CDN or regional optimization.",
                EstimatedSavings = avgCost * 0.5,
                ImplementationEffort = ImplementationEffort.High,
                Risk = RiskLevel.Low,
                Source = RecommendationSource.Rule
            };
        }

        return null;
    }
}

#endregion

#region Types

/// <summary>
/// Resource cost data tracking.
/// </summary>
public sealed class ResourceCostData
{
    /// <summary>Gets or sets the resource identifier.</summary>
    public string ResourceId { get; init; } = string.Empty;

    /// <summary>Gets or sets the total cost.</summary>
    public double TotalCost { get; set; }

    /// <summary>Gets or sets the last update time.</summary>
    public DateTime LastUpdated { get; set; }

    /// <summary>Gets the cost history.</summary>
    public List<CostRecord> CostHistory { get; } = new();

    /// <summary>Gets the cost by category.</summary>
    public Dictionary<string, double> CostByCategory { get; } = new();

    /// <summary>Gets or sets the metadata.</summary>
    public Dictionary<string, object>? Metadata { get; set; }
}

/// <summary>
/// Individual cost record.
/// </summary>
public sealed class CostRecord
{
    /// <summary>Gets or sets the timestamp.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Gets or sets the amount.</summary>
    public double Amount { get; init; }

    /// <summary>Gets or sets the category.</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>Gets or sets the currency.</summary>
    public string Currency { get; init; } = "USD";

    /// <summary>Gets or sets optional tags.</summary>
    public Dictionary<string, object>? Tags { get; init; }
}

/// <summary>
/// Cost analysis result.
/// </summary>
public sealed class CostAnalysisResult
{
    /// <summary>Gets or sets the analysis time.</summary>
    public DateTime AnalysisTime { get; init; }

    /// <summary>Gets or sets the analysis window.</summary>
    public TimeSpan AnalysisWindow { get; init; }

    /// <summary>Gets or sets the total cost.</summary>
    public double TotalCost { get; init; }

    /// <summary>Gets or sets costs by category.</summary>
    public Dictionary<string, double> CostByCategory { get; init; } = new();

    /// <summary>Gets or sets costs by resource.</summary>
    public Dictionary<string, double> CostByResource { get; init; } = new();

    /// <summary>Gets or sets the recommendations.</summary>
    public List<CostOptimizationRecommendation> Recommendations { get; init; } = new();

    /// <summary>Gets or sets the projected monthly cost.</summary>
    public double ProjectedMonthlyCost { get; init; }

    /// <summary>Gets or sets the cost trend.</summary>
    public CostTrend CostTrend { get; init; }

    /// <summary>Gets or sets any detected cost anomalies.</summary>
    public List<CostAnomaly> AnomalyCosts { get; init; } = new();
}

/// <summary>
/// Cost projection result.
/// </summary>
public sealed class CostProjection
{
    /// <summary>Gets or sets the number of projection days.</summary>
    public int ProjectionDays { get; init; }

    /// <summary>Gets or sets daily projections.</summary>
    public List<DailyCostProjection> DailyProjections { get; init; } = new();

    /// <summary>Gets or sets the total projected cost.</summary>
    public double TotalProjectedCost { get; init; }

    /// <summary>Gets or sets the average daily cost.</summary>
    public double AverageDailyCost { get; init; }

    /// <summary>Gets or sets the trend.</summary>
    public CostTrend Trend { get; init; }

    /// <summary>Gets or sets the confidence level.</summary>
    public double Confidence { get; init; }

    /// <summary>Gets or sets the projection reason.</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Daily cost projection.
/// </summary>
public sealed class DailyCostProjection
{
    /// <summary>Gets or sets the date.</summary>
    public DateTime Date { get; init; }

    /// <summary>Gets or sets the projected cost.</summary>
    public double ProjectedCost { get; init; }

    /// <summary>Gets or sets the lower bound.</summary>
    public double LowerBound { get; init; }

    /// <summary>Gets or sets the upper bound.</summary>
    public double UpperBound { get; init; }
}

/// <summary>
/// Cost optimization recommendation.
/// </summary>
public sealed class CostOptimizationRecommendation
{
    /// <summary>Gets or sets the recommendation identifier.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets or sets the resource identifier.</summary>
    public string ResourceId { get; init; } = string.Empty;

    /// <summary>Gets or sets the recommendation type.</summary>
    public CostRecommendationType Type { get; init; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets or sets the description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Gets or sets the estimated daily savings.</summary>
    public double EstimatedSavings { get; init; }

    /// <summary>Gets or sets the implementation effort.</summary>
    public ImplementationEffort ImplementationEffort { get; init; }

    /// <summary>Gets or sets the risk level.</summary>
    public RiskLevel Risk { get; init; }

    /// <summary>Gets or sets the source of the recommendation.</summary>
    public RecommendationSource Source { get; init; }
}

/// <summary>
/// Savings estimate for a recommendation.
/// </summary>
public sealed class SavingsEstimate
{
    /// <summary>Gets or sets the recommendation identifier.</summary>
    public string RecommendationId { get; init; } = string.Empty;

    /// <summary>Gets or sets the estimated monthly savings.</summary>
    public double EstimatedMonthlySavings { get; init; }

    /// <summary>Gets or sets the estimated annual savings.</summary>
    public double EstimatedAnnualSavings { get; init; }

    /// <summary>Gets or sets the implementation cost.</summary>
    public double ImplementationCost { get; init; }

    /// <summary>Gets or sets the payback period in days.</summary>
    public int PaybackPeriodDays { get; init; }

    /// <summary>Gets or sets the risk level.</summary>
    public RiskLevel RiskLevel { get; init; }
}

/// <summary>
/// Cost anomaly.
/// </summary>
public sealed class CostAnomaly
{
    /// <summary>Gets or sets the resource identifier.</summary>
    public string ResourceId { get; init; } = string.Empty;

    /// <summary>Gets or sets the timestamp.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Gets or sets the actual cost.</summary>
    public double ActualCost { get; init; }

    /// <summary>Gets or sets the expected cost.</summary>
    public double ExpectedCost { get; init; }

    /// <summary>Gets or sets the deviation (Z-score).</summary>
    public double Deviation { get; init; }

    /// <summary>Gets or sets the category.</summary>
    public string Category { get; init; } = string.Empty;
}

/// <summary>
/// Category cost summary.
/// </summary>
public sealed class CategoryCostSummary
{
    /// <summary>Gets or sets the category name.</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>Gets or sets the total cost.</summary>
    public double TotalCost { get; set; }

    /// <summary>Gets or sets the percentage of total.</summary>
    public double PercentOfTotal { get; set; }

    /// <summary>Gets or sets the record count.</summary>
    public int RecordCount { get; set; }

    /// <summary>Gets the resources in this category.</summary>
    public HashSet<string> Resources { get; } = new();
}

/// <summary>
/// Cost trend direction.
/// </summary>
public enum CostTrend
{
    /// <summary>Trend unknown.</summary>
    Unknown,
    /// <summary>Costs decreasing.</summary>
    Decreasing,
    /// <summary>Costs stable.</summary>
    Stable,
    /// <summary>Costs increasing.</summary>
    Increasing
}

/// <summary>
/// Cost recommendation types.
/// </summary>
public enum CostRecommendationType
{
    /// <summary>Terminate an idle resource.</summary>
    TerminateResource,
    /// <summary>Rightsize a resource.</summary>
    Rightsize,
    /// <summary>Archive unused data.</summary>
    ArchiveData,
    /// <summary>Purchase reserved capacity.</summary>
    PurchaseReserved,
    /// <summary>Optimize data transfer.</summary>
    OptimizeTransfer,
    /// <summary>Migrate to different tier.</summary>
    MigrateTier,
    /// <summary>Enable compression.</summary>
    EnableCompression,
    /// <summary>Consolidate resources.</summary>
    Consolidate
}

/// <summary>
/// Implementation effort levels.
/// </summary>
public enum ImplementationEffort
{
    /// <summary>Low effort.</summary>
    Low,
    /// <summary>Medium effort.</summary>
    Medium,
    /// <summary>High effort.</summary>
    High
}

/// <summary>
/// Risk levels.
/// </summary>
public enum RiskLevel
{
    /// <summary>Low risk.</summary>
    Low,
    /// <summary>Medium risk.</summary>
    Medium,
    /// <summary>High risk.</summary>
    High,
    /// <summary>Critical risk.</summary>
    Critical
}

/// <summary>
/// Cost engine statistics.
/// </summary>
public sealed class CostEngineStatistics
{
    /// <summary>Gets or sets the number of tracked resources.</summary>
    public int TrackedResources { get; init; }

    /// <summary>Gets or sets the total cost records.</summary>
    public int TotalCostRecords { get; init; }

    /// <summary>Gets or sets the total historical cost.</summary>
    public double TotalHistoricalCost { get; init; }

    /// <summary>Gets or sets the oldest record timestamp.</summary>
    public DateTime? OldestRecord { get; init; }

    /// <summary>Gets or sets the newest record timestamp.</summary>
    public DateTime? NewestRecord { get; init; }

    /// <summary>Gets or sets the last analysis time.</summary>
    public DateTime? LastAnalysisTime { get; init; }

    /// <summary>Gets or sets the recommendation count from last analysis.</summary>
    public int RecommendationCount { get; init; }

    /// <summary>Gets or sets the potential savings identified.</summary>
    public double PotentialSavings { get; init; }
}

#endregion
