using System.Collections.Concurrent;
using System.Text.Json;

namespace DataWarehouse.Plugins.AutonomousDataManagement.Models;

/// <summary>
/// Cost optimizer for cloud resource and storage optimization.
/// </summary>
public sealed class CostOptimizer
{
    private readonly ConcurrentDictionary<string, ResourceCostData> _resourceCosts;
    private readonly CostAnalysisEngine _analysisEngine;
    private readonly List<CostOptimizationRule> _rules;

    public CostOptimizer()
    {
        _resourceCosts = new ConcurrentDictionary<string, ResourceCostData>();
        _analysisEngine = new CostAnalysisEngine();
        _rules = InitializeDefaultRules();
    }

    /// <summary>
    /// Records cost data for a resource.
    /// </summary>
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

            // Trim old history (keep 90 days)
            var cutoff = DateTime.UtcNow.AddDays(-90);
            data.CostHistory.RemoveAll(c => c.Timestamp < cutoff);
        }
    }

    /// <summary>
    /// Analyzes costs and returns optimization recommendations.
    /// </summary>
    public CostAnalysisResult AnalyzeCosts(TimeSpan analysisWindow)
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

        // Sort by potential savings
        recommendations = recommendations.OrderByDescending(r => r.EstimatedSavings).ToList();

        return new CostAnalysisResult
        {
            AnalysisWindow = analysisWindow,
            TotalCost = totalCost,
            CostByCategory = categoryCosts,
            CostByResource = resourceCosts,
            Recommendations = recommendations,
            ProjectedMonthlyCost = totalCost * (30.0 / analysisWindow.TotalDays),
            CostTrend = _analysisEngine.CalculateCostTrend(_resourceCosts.Values.ToList()),
            AnomalyCosts = _analysisEngine.DetectCostAnomalies(_resourceCosts.Values.ToList(), cutoff)
        };
    }

    /// <summary>
    /// Gets cost projection for future periods.
    /// </summary>
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

        // Simple linear regression for projection
        var trend = CalculateLinearTrend(dailyCosts.Select(d => d.Cost).ToList());
        var avgDailyCost = dailyCosts.TakeLast(30).Average(d => d.Cost);

        var projections = new List<DailyCostProjection>();
        var lastDate = dailyCosts.Last().Date;

        for (int i = 1; i <= daysAhead; i++)
        {
            var date = lastDate.AddDays(i);
            var projectedCost = avgDailyCost + trend * i;

            // Apply seasonal adjustment (day of week)
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
            Trend = trend > 0 ? CostTrend.Increasing : trend < 0 ? CostTrend.Decreasing : CostTrend.Stable,
            Confidence = Math.Min(1.0, dailyCosts.Count / 90.0),
            Reason = "Linear regression with seasonal adjustment"
        };
    }

    /// <summary>
    /// Estimates savings from implementing a recommendation.
    /// </summary>
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

    private double CalculateLinearTrend(List<double> values)
    {
        if (values.Count < 2) return 0;

        var n = values.Count;
        var sumX = Enumerable.Range(0, n).Sum();
        var sumY = values.Sum();
        var sumXY = Enumerable.Range(0, n).Select(i => i * values[i]).Sum();
        var sumX2 = Enumerable.Range(0, n).Select(i => i * i).Sum();

        return (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);
    }

    private double GetDayOfWeekMultiplier(List<(DateTime Date, double Cost)> historicalCosts, DayOfWeek dow)
    {
        var dowCosts = historicalCosts.Where(c => c.Date.DayOfWeek == dow).Select(c => c.Cost).ToList();
        var allCosts = historicalCosts.Select(c => c.Cost).ToList();

        if (dowCosts.Count < 4 || allCosts.Count < 7) return 1.0;

        var dowAvg = dowCosts.Average();
        var allAvg = allCosts.Average();

        return allAvg > 0 ? dowAvg / allAvg : 1.0;
    }

    private List<CostOptimizationRule> InitializeDefaultRules()
    {
        return new List<CostOptimizationRule>
        {
            new IdleResourceRule(),
            new OversizedResourceRule(),
            new UnusedStorageRule(),
            new ReservedCapacityRule(),
            new DataTransferRule()
        };
    }
}

/// <summary>
/// Query optimizer for storage and database operations.
/// </summary>
public sealed class QueryOptimizer
{
    private readonly ConcurrentDictionary<string, QueryMetrics> _queryMetrics;
    private readonly List<QueryPattern> _queryPatterns;
    private readonly int _slowQueryThresholdMs;

    public QueryOptimizer(int slowQueryThresholdMs = 1000)
    {
        _queryMetrics = new ConcurrentDictionary<string, QueryMetrics>();
        _queryPatterns = new List<QueryPattern>();
        _slowQueryThresholdMs = slowQueryThresholdMs;
    }

    /// <summary>
    /// Records query execution metrics.
    /// </summary>
    public void RecordQuery(string queryHash, QueryExecutionInfo info)
    {
        var metrics = _queryMetrics.GetOrAdd(queryHash, _ => new QueryMetrics { QueryHash = queryHash });

        lock (metrics)
        {
            metrics.ExecutionCount++;
            metrics.TotalExecutionTimeMs += info.ExecutionTimeMs;
            metrics.TotalRowsScanned += info.RowsScanned;
            metrics.TotalRowsReturned += info.RowsReturned;
            metrics.TotalBytesRead += info.BytesRead;
            metrics.LastExecution = DateTime.UtcNow;

            if (metrics.QueryPattern == null && info.QueryText != null)
            {
                metrics.QueryPattern = info.QueryText;
            }

            metrics.ExecutionHistory.Add(new QueryExecution
            {
                Timestamp = DateTime.UtcNow,
                ExecutionTimeMs = info.ExecutionTimeMs,
                RowsScanned = info.RowsScanned,
                RowsReturned = info.RowsReturned
            });

            // Trim history
            while (metrics.ExecutionHistory.Count > 1000)
            {
                metrics.ExecutionHistory.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// Gets optimization recommendations for slow queries.
    /// </summary>
    public List<QueryOptimizationRecommendation> GetOptimizationRecommendations()
    {
        var recommendations = new List<QueryOptimizationRecommendation>();

        foreach (var (hash, metrics) in _queryMetrics)
        {
            lock (metrics)
            {
                var avgTime = metrics.ExecutionCount > 0
                    ? metrics.TotalExecutionTimeMs / metrics.ExecutionCount
                    : 0;

                if (avgTime < _slowQueryThresholdMs && metrics.ExecutionCount < 100)
                    continue;

                var selectivity = metrics.TotalRowsReturned > 0 && metrics.TotalRowsScanned > 0
                    ? (double)metrics.TotalRowsReturned / metrics.TotalRowsScanned
                    : 1.0;

                // Check for full table scans
                if (selectivity < 0.01 && metrics.TotalRowsScanned > 10000)
                {
                    recommendations.Add(new QueryOptimizationRecommendation
                    {
                        QueryHash = hash,
                        QueryPattern = metrics.QueryPattern,
                        Type = OptimizationType.AddIndex,
                        Description = "Query has low selectivity, consider adding an index",
                        EstimatedImprovement = 0.8,
                        Priority = QueryPriority.High,
                        CurrentAvgTimeMs = avgTime,
                        ExecutionCount = metrics.ExecutionCount
                    });
                }

                // Check for slow queries
                if (avgTime > _slowQueryThresholdMs)
                {
                    recommendations.Add(new QueryOptimizationRecommendation
                    {
                        QueryHash = hash,
                        QueryPattern = metrics.QueryPattern,
                        Type = OptimizationType.QueryRewrite,
                        Description = $"Query averaging {avgTime:F0}ms, consider optimization",
                        EstimatedImprovement = 0.5,
                        Priority = avgTime > _slowQueryThresholdMs * 5 ? QueryPriority.Critical : QueryPriority.Medium,
                        CurrentAvgTimeMs = avgTime,
                        ExecutionCount = metrics.ExecutionCount
                    });
                }

                // Check for frequent queries that could benefit from caching
                if (metrics.ExecutionCount > 100 && avgTime > 100)
                {
                    recommendations.Add(new QueryOptimizationRecommendation
                    {
                        QueryHash = hash,
                        QueryPattern = metrics.QueryPattern,
                        Type = OptimizationType.AddCache,
                        Description = $"Frequently executed query ({metrics.ExecutionCount} times), consider caching",
                        EstimatedImprovement = 0.9,
                        Priority = QueryPriority.Medium,
                        CurrentAvgTimeMs = avgTime,
                        ExecutionCount = metrics.ExecutionCount
                    });
                }
            }
        }

        return recommendations.OrderByDescending(r => r.Priority)
            .ThenByDescending(r => r.EstimatedImprovement * r.ExecutionCount)
            .ToList();
    }

    /// <summary>
    /// Gets query performance statistics.
    /// </summary>
    public QueryPerformanceStats GetPerformanceStats()
    {
        var allMetrics = _queryMetrics.Values.ToList();

        return new QueryPerformanceStats
        {
            TotalQueries = allMetrics.Sum(m => m.ExecutionCount),
            UniqueQueryCount = allMetrics.Count,
            SlowQueryCount = allMetrics.Count(m =>
                m.ExecutionCount > 0 && m.TotalExecutionTimeMs / m.ExecutionCount > _slowQueryThresholdMs),
            AverageExecutionTimeMs = allMetrics.Sum(m => m.TotalExecutionTimeMs) /
                Math.Max(1, allMetrics.Sum(m => m.ExecutionCount)),
            TotalRowsScanned = allMetrics.Sum(m => m.TotalRowsScanned),
            TotalRowsReturned = allMetrics.Sum(m => m.TotalRowsReturned),
            OverallSelectivity = allMetrics.Sum(m => m.TotalRowsReturned) /
                Math.Max(1.0, allMetrics.Sum(m => m.TotalRowsScanned)),
            TopSlowQueries = allMetrics
                .Where(m => m.ExecutionCount > 0)
                .OrderByDescending(m => m.TotalExecutionTimeMs / m.ExecutionCount)
                .Take(10)
                .Select(m => new SlowQueryInfo
                {
                    QueryHash = m.QueryHash,
                    QueryPattern = m.QueryPattern,
                    AvgTimeMs = m.TotalExecutionTimeMs / m.ExecutionCount,
                    ExecutionCount = m.ExecutionCount
                })
                .ToList()
        };
    }
}

#region Cost Analysis Engine

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

        // Calculate trend using linear regression
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

#region Optimization Rules

internal abstract class CostOptimizationRule
{
    public abstract CostOptimizationRecommendation? Evaluate(
        string resourceId,
        ResourceCostData data,
        List<CostRecord> recentCosts);
}

internal sealed class IdleResourceRule : CostOptimizationRule
{
    public override CostOptimizationRecommendation? Evaluate(
        string resourceId,
        ResourceCostData data,
        List<CostRecord> recentCosts)
    {
        // Check if resource has consistent low utilization costs
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
                Type = RecommendationType.TerminateResource,
                Title = "Idle Resource Detected",
                Description = $"Resource has <10% utilization. Consider terminating or rightsizing.",
                EstimatedSavings = avgCost * 0.9,
                ImplementationEffort = ImplementationEffort.Low,
                Risk = RiskLevel.Low
            };
        }

        return null;
    }
}

internal sealed class OversizedResourceRule : CostOptimizationRule
{
    public override CostOptimizationRecommendation? Evaluate(
        string resourceId,
        ResourceCostData data,
        List<CostRecord> recentCosts)
    {
        var utilizationHint = data.Metadata?.GetValueOrDefault("utilization")?.ToString();

        if (double.TryParse(utilizationHint, out var utilization) && utilization < 30)
        {
            var avgCost = recentCosts.Average(c => c.Amount);
            return new CostOptimizationRecommendation
            {
                Id = Guid.NewGuid().ToString(),
                ResourceId = resourceId,
                Type = RecommendationType.Rightsize,
                Title = "Oversized Resource",
                Description = $"Resource utilization is {utilization:F0}%. Consider downsizing.",
                EstimatedSavings = avgCost * 0.5,
                ImplementationEffort = ImplementationEffort.Medium,
                Risk = RiskLevel.Medium
            };
        }

        return null;
    }
}

internal sealed class UnusedStorageRule : CostOptimizationRule
{
    public override CostOptimizationRecommendation? Evaluate(
        string resourceId,
        ResourceCostData data,
        List<CostRecord> recentCosts)
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
                    Type = RecommendationType.ArchiveData,
                    Title = "Unused Storage",
                    Description = $"Storage not accessed in {daysSinceAccess:F0} days. Consider archiving.",
                    EstimatedSavings = avgCost * 0.8,
                    ImplementationEffort = ImplementationEffort.Low,
                    Risk = RiskLevel.Low
                };
            }
        }

        return null;
    }
}

internal sealed class ReservedCapacityRule : CostOptimizationRule
{
    public override CostOptimizationRecommendation? Evaluate(
        string resourceId,
        ResourceCostData data,
        List<CostRecord> recentCosts)
    {
        // Check for consistent high usage that could benefit from reserved pricing
        if (recentCosts.Count < 30) return null;

        var avgDailyCost = recentCosts.GroupBy(c => c.Timestamp.Date).Average(g => g.Sum(c => c.Amount));
        var stdDev = Math.Sqrt(recentCosts.GroupBy(c => c.Timestamp.Date)
            .Select(g => g.Sum(c => c.Amount))
            .Select(v => Math.Pow(v - avgDailyCost, 2))
            .Average());

        // Low variance indicates steady usage
        var coefficientOfVariation = avgDailyCost > 0 ? stdDev / avgDailyCost : 1;
        if (coefficientOfVariation < 0.2 && avgDailyCost > 10)
        {
            return new CostOptimizationRecommendation
            {
                Id = Guid.NewGuid().ToString(),
                ResourceId = resourceId,
                Type = RecommendationType.PurchaseReserved,
                Title = "Reserved Capacity Opportunity",
                Description = $"Stable usage pattern detected. Reserved capacity could save ~30%.",
                EstimatedSavings = avgDailyCost * 0.3,
                ImplementationEffort = ImplementationEffort.Medium,
                Risk = RiskLevel.Medium
            };
        }

        return null;
    }
}

internal sealed class DataTransferRule : CostOptimizationRule
{
    public override CostOptimizationRecommendation? Evaluate(
        string resourceId,
        ResourceCostData data,
        List<CostRecord> recentCosts)
    {
        var transferCosts = recentCosts.Where(c => c.Category == "data-transfer").ToList();
        if (transferCosts.Count < 7) return null;

        var avgCost = transferCosts.Average(c => c.Amount);
        var totalCost = recentCosts.Sum(c => c.Amount);

        // High data transfer costs relative to total
        if (totalCost > 0 && transferCosts.Sum(c => c.Amount) / totalCost > 0.3)
        {
            return new CostOptimizationRecommendation
            {
                Id = Guid.NewGuid().ToString(),
                ResourceId = resourceId,
                Type = RecommendationType.OptimizeTransfer,
                Title = "High Data Transfer Costs",
                Description = "Data transfer costs are >30% of total. Consider CDN or regional optimization.",
                EstimatedSavings = avgCost * 0.5,
                ImplementationEffort = ImplementationEffort.High,
                Risk = RiskLevel.Low
            };
        }

        return null;
    }
}

#endregion

#region Supporting Types

public sealed class ResourceCostData
{
    public string ResourceId { get; init; } = string.Empty;
    public double TotalCost { get; set; }
    public DateTime LastUpdated { get; set; }
    public List<CostRecord> CostHistory { get; } = new();
    public Dictionary<string, double> CostByCategory { get; } = new();
    public Dictionary<string, object>? Metadata { get; set; }
}

public sealed class CostRecord
{
    public DateTime Timestamp { get; init; }
    public double Amount { get; init; }
    public string Category { get; init; } = string.Empty;
    public string Currency { get; init; } = "USD";
    public Dictionary<string, object>? Tags { get; init; }
}

public sealed class CostAnalysisResult
{
    public TimeSpan AnalysisWindow { get; init; }
    public double TotalCost { get; init; }
    public Dictionary<string, double> CostByCategory { get; init; } = new();
    public Dictionary<string, double> CostByResource { get; init; } = new();
    public List<CostOptimizationRecommendation> Recommendations { get; init; } = new();
    public double ProjectedMonthlyCost { get; init; }
    public CostTrend CostTrend { get; init; }
    public List<CostAnomaly> AnomalyCosts { get; init; } = new();
}

public sealed class CostProjection
{
    public int ProjectionDays { get; init; }
    public List<DailyCostProjection> DailyProjections { get; init; } = new();
    public double TotalProjectedCost { get; init; }
    public double AverageDailyCost { get; init; }
    public CostTrend Trend { get; init; }
    public double Confidence { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class DailyCostProjection
{
    public DateTime Date { get; init; }
    public double ProjectedCost { get; init; }
    public double LowerBound { get; init; }
    public double UpperBound { get; init; }
}

public sealed class CostOptimizationRecommendation
{
    public string Id { get; init; } = string.Empty;
    public string ResourceId { get; init; } = string.Empty;
    public RecommendationType Type { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public double EstimatedSavings { get; init; }
    public ImplementationEffort ImplementationEffort { get; init; }
    public RiskLevel Risk { get; init; }
}

public sealed class SavingsEstimate
{
    public string RecommendationId { get; init; } = string.Empty;
    public double EstimatedMonthlySavings { get; init; }
    public double EstimatedAnnualSavings { get; init; }
    public double ImplementationCost { get; init; }
    public int PaybackPeriodDays { get; init; }
    public RiskLevel RiskLevel { get; init; }
}

public sealed class CostAnomaly
{
    public string ResourceId { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public double ActualCost { get; init; }
    public double ExpectedCost { get; init; }
    public double Deviation { get; init; }
    public string Category { get; init; } = string.Empty;
}

public sealed class QueryMetrics
{
    public string QueryHash { get; init; } = string.Empty;
    public string? QueryPattern { get; set; }
    public long ExecutionCount { get; set; }
    public long TotalExecutionTimeMs { get; set; }
    public long TotalRowsScanned { get; set; }
    public long TotalRowsReturned { get; set; }
    public long TotalBytesRead { get; set; }
    public DateTime LastExecution { get; set; }
    public List<QueryExecution> ExecutionHistory { get; } = new();
}

public sealed class QueryExecution
{
    public DateTime Timestamp { get; init; }
    public long ExecutionTimeMs { get; init; }
    public long RowsScanned { get; init; }
    public long RowsReturned { get; init; }
}

public sealed class QueryExecutionInfo
{
    public string? QueryText { get; init; }
    public long ExecutionTimeMs { get; init; }
    public long RowsScanned { get; init; }
    public long RowsReturned { get; init; }
    public long BytesRead { get; init; }
}

public sealed class QueryOptimizationRecommendation
{
    public string QueryHash { get; init; } = string.Empty;
    public string? QueryPattern { get; init; }
    public OptimizationType Type { get; init; }
    public string Description { get; init; } = string.Empty;
    public double EstimatedImprovement { get; init; }
    public QueryPriority Priority { get; init; }
    public double CurrentAvgTimeMs { get; init; }
    public long ExecutionCount { get; init; }
}

public sealed class QueryPerformanceStats
{
    public long TotalQueries { get; init; }
    public int UniqueQueryCount { get; init; }
    public int SlowQueryCount { get; init; }
    public double AverageExecutionTimeMs { get; init; }
    public long TotalRowsScanned { get; init; }
    public long TotalRowsReturned { get; init; }
    public double OverallSelectivity { get; init; }
    public List<SlowQueryInfo> TopSlowQueries { get; init; } = new();
}

public sealed class SlowQueryInfo
{
    public string QueryHash { get; init; } = string.Empty;
    public string? QueryPattern { get; init; }
    public double AvgTimeMs { get; init; }
    public long ExecutionCount { get; init; }
}

public sealed class QueryPattern
{
    public string Pattern { get; init; } = string.Empty;
    public int ExecutionCount { get; init; }
    public double AvgDurationMs { get; init; }
}

public enum CostTrend
{
    Unknown,
    Decreasing,
    Stable,
    Increasing
}

public enum RecommendationType
{
    TerminateResource,
    Rightsize,
    ArchiveData,
    PurchaseReserved,
    OptimizeTransfer,
    MigrateTier,
    EnableCompression,
    Consolidate
}

public enum ImplementationEffort
{
    Low,
    Medium,
    High
}

public enum RiskLevel
{
    Low,
    Medium,
    High,
    Critical
}

public enum OptimizationType
{
    AddIndex,
    QueryRewrite,
    AddCache,
    Partitioning,
    Materialization
}

public enum QueryPriority
{
    Low,
    Medium,
    High,
    Critical
}

#endregion
