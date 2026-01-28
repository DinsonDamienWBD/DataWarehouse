using DataWarehouse.SDK.AI;
using DataWarehouse.Plugins.AutonomousDataManagement.Providers;
using System.Collections.Concurrent;
using System.Text.Json;

namespace DataWarehouse.Plugins.AutonomousDataManagement.Engine;

/// <summary>
/// Capacity planning engine for forecasting future resource needs.
/// </summary>
public sealed class CapacityEngine : IDisposable
{
    private readonly AIProviderSelector _providerSelector;
    private readonly ConcurrentDictionary<string, CapacityMetricsSeries> _metricsSeries;
    private readonly ConcurrentQueue<CapacityPlanningEvent> _planningHistory;
    private readonly CapacityEngineConfig _config;
    private readonly Timer _planningTimer;
    private bool _disposed;

    public CapacityEngine(AIProviderSelector providerSelector, CapacityEngineConfig? config = null)
    {
        _providerSelector = providerSelector ?? throw new ArgumentNullException(nameof(providerSelector));
        _config = config ?? new CapacityEngineConfig();
        _metricsSeries = new ConcurrentDictionary<string, CapacityMetricsSeries>();
        _planningHistory = new ConcurrentQueue<CapacityPlanningEvent>();

        _planningTimer = new Timer(
            _ => _ = PlanningCycleAsync(CancellationToken.None),
            null,
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(_config.PlanningIntervalHours));
    }

    /// <summary>
    /// Records capacity metrics for planning.
    /// </summary>
    public void RecordMetrics(string resourceType, CapacityMetrics metrics)
    {
        var series = _metricsSeries.GetOrAdd(resourceType, _ => new CapacityMetricsSeries
        {
            ResourceType = resourceType,
            WindowSize = _config.HistoryWindowDays * 24
        });

        lock (series)
        {
            series.AddMetrics(metrics);
        }
    }

    /// <summary>
    /// Generates a capacity forecast for a resource type.
    /// </summary>
    public async Task<CapacityForecast> ForecastAsync(
        string resourceType,
        int forecastDays,
        CancellationToken ct = default)
    {
        if (!_metricsSeries.TryGetValue(resourceType, out var series))
        {
            return new CapacityForecast
            {
                ResourceType = resourceType,
                ForecastDays = forecastDays,
                Confidence = 0,
                Reason = "No historical data available"
            };
        }

        List<TimestampedCapacityMetrics> history;
        lock (series)
        {
            history = series.MetricsHistory.ToList();
        }

        if (history.Count < _config.MinDataPointsForForecast)
        {
            return new CapacityForecast
            {
                ResourceType = resourceType,
                ForecastDays = forecastDays,
                Confidence = 0,
                Reason = $"Insufficient data ({history.Count} points, need {_config.MinDataPointsForForecast})"
            };
        }

        // Statistical forecast
        var storageGrowthRate = CalculateGrowthRate(history.Select(h => h.Metrics.StorageUsedGB).ToList());
        var computeGrowthRate = CalculateGrowthRate(history.Select(h => h.Metrics.ComputeUtilization).ToList());
        var networkGrowthRate = CalculateGrowthRate(history.Select(h => h.Metrics.NetworkBandwidthMbps).ToList());

        var latestMetrics = history.Last().Metrics;

        var storageProjection = ProjectGrowth(latestMetrics.StorageUsedGB, storageGrowthRate, forecastDays);
        var computeProjection = ProjectGrowth(latestMetrics.ComputeUtilization, computeGrowthRate, forecastDays);
        var networkProjection = ProjectGrowth(latestMetrics.NetworkBandwidthMbps, networkGrowthRate, forecastDays);

        var confidence = Math.Min(1.0, history.Count / (double)(_config.HistoryWindowDays * 24));

        // Calculate when capacity will be exhausted
        var storageExhaustionDays = CalculateExhaustionDays(
            latestMetrics.StorageUsedGB,
            latestMetrics.StorageTotalGB,
            storageGrowthRate);
        var computeExhaustionDays = CalculateExhaustionDays(
            latestMetrics.ComputeUtilization,
            100,
            computeGrowthRate);

        // Get AI-enhanced recommendations
        List<CapacityRecommendation> recommendations;
        try
        {
            recommendations = await GetAICapacityRecommendationsAsync(
                resourceType, latestMetrics, storageGrowthRate, computeGrowthRate, forecastDays, ct);
        }
        catch
        {
            recommendations = GenerateBasicRecommendations(
                latestMetrics, storageExhaustionDays, computeExhaustionDays);
        }

        // Generate daily projections
        var dailyProjections = new List<DailyCapacityProjection>();
        for (int day = 1; day <= forecastDays; day++)
        {
            dailyProjections.Add(new DailyCapacityProjection
            {
                Day = day,
                Date = DateTime.UtcNow.AddDays(day),
                StorageUsedGB = latestMetrics.StorageUsedGB + storageGrowthRate * day,
                StorageUtilization = (latestMetrics.StorageUsedGB + storageGrowthRate * day) / latestMetrics.StorageTotalGB * 100,
                ComputeUtilization = Math.Min(100, latestMetrics.ComputeUtilization + computeGrowthRate * day),
                NetworkUtilization = latestMetrics.NetworkBandwidthMbps + networkGrowthRate * day
            });
        }

        return new CapacityForecast
        {
            ResourceType = resourceType,
            ForecastDays = forecastDays,
            GeneratedAt = DateTime.UtcNow,
            CurrentStorage = latestMetrics.StorageUsedGB,
            CurrentStorageUtilization = latestMetrics.StorageUsedGB / latestMetrics.StorageTotalGB * 100,
            CurrentComputeUtilization = latestMetrics.ComputeUtilization,
            StorageGrowthRatePerDay = storageGrowthRate,
            ComputeGrowthRatePerDay = computeGrowthRate,
            ProjectedStorageGB = storageProjection,
            ProjectedStorageUtilization = storageProjection / latestMetrics.StorageTotalGB * 100,
            ProjectedComputeUtilization = Math.Min(100, computeProjection),
            StorageExhaustionDays = storageExhaustionDays,
            ComputeExhaustionDays = computeExhaustionDays,
            DailyProjections = dailyProjections,
            Recommendations = recommendations,
            Confidence = confidence,
            Reason = "Based on linear regression of historical data"
        };
    }

    /// <summary>
    /// Generates a budget projection for capacity needs.
    /// </summary>
    public async Task<BudgetProjection> ProjectBudgetAsync(
        string resourceType,
        int forecastMonths,
        CostRates costRates,
        CancellationToken ct = default)
    {
        var forecast = await ForecastAsync(resourceType, forecastMonths * 30, ct);

        if (forecast.Confidence < 0.5)
        {
            return new BudgetProjection
            {
                ResourceType = resourceType,
                ForecastMonths = forecastMonths,
                Confidence = forecast.Confidence,
                Reason = "Low forecast confidence"
            };
        }

        var monthlyProjections = new List<MonthlyBudgetProjection>();
        var totalBudget = 0.0;

        for (int month = 1; month <= forecastMonths; month++)
        {
            var projectedDay = forecast.DailyProjections.Skip((month - 1) * 30).FirstOrDefault()
                ?? forecast.DailyProjections.LastOrDefault();

            var storageCost = projectedDay?.StorageUsedGB * costRates.StoragePerGBMonth ?? 0;
            var computeCost = (projectedDay?.ComputeUtilization / 100 ?? 0) * costRates.ComputePerUnitMonth;
            var networkCost = projectedDay?.NetworkUtilization * costRates.NetworkPerMbpsMonth ?? 0;

            var monthlyCost = storageCost + computeCost + networkCost;
            totalBudget += monthlyCost;

            monthlyProjections.Add(new MonthlyBudgetProjection
            {
                Month = month,
                Date = DateTime.UtcNow.AddMonths(month),
                StorageCost = storageCost,
                ComputeCost = computeCost,
                NetworkCost = networkCost,
                TotalCost = monthlyCost
            });
        }

        return new BudgetProjection
        {
            ResourceType = resourceType,
            ForecastMonths = forecastMonths,
            GeneratedAt = DateTime.UtcNow,
            MonthlyProjections = monthlyProjections,
            TotalProjectedBudget = totalBudget,
            AverageMonthlyCost = totalBudget / forecastMonths,
            CostGrowthRate = monthlyProjections.Count > 1
                ? (monthlyProjections.Last().TotalCost - monthlyProjections.First().TotalCost) / monthlyProjections.First().TotalCost
                : 0,
            Confidence = forecast.Confidence,
            Reason = "Based on capacity forecast and current cost rates"
        };
    }

    /// <summary>
    /// Gets capacity planning statistics.
    /// </summary>
    public CapacityStatistics GetStatistics()
    {
        var stats = new Dictionary<string, ResourceTypeStats>();

        foreach (var (resourceType, series) in _metricsSeries)
        {
            lock (series)
            {
                if (series.MetricsHistory.Count == 0) continue;

                var latest = series.MetricsHistory.Last().Metrics;
                var values = series.MetricsHistory.Select(h => h.Metrics.StorageUsedGB).ToList();
                var growthRate = CalculateGrowthRate(values);

                stats[resourceType] = new ResourceTypeStats
                {
                    ResourceType = resourceType,
                    DataPoints = series.MetricsHistory.Count,
                    CurrentStorageUtilization = latest.StorageUsedGB / latest.StorageTotalGB * 100,
                    CurrentComputeUtilization = latest.ComputeUtilization,
                    StorageGrowthRatePerDay = growthRate,
                    LastUpdated = series.MetricsHistory.Last().Timestamp
                };
            }
        }

        return new CapacityStatistics
        {
            TrackedResourceTypes = _metricsSeries.Count,
            TotalDataPoints = _metricsSeries.Values.Sum(s => s.MetricsHistory.Count),
            ResourceTypeStats = stats,
            RecentPlanningEvents = _planningHistory.TakeLast(20).ToList()
        };
    }

    private async Task PlanningCycleAsync(CancellationToken ct)
    {
        foreach (var resourceType in _metricsSeries.Keys)
        {
            try
            {
                var forecast = await ForecastAsync(resourceType, 30, ct);

                var planningEvent = new CapacityPlanningEvent
                {
                    ResourceType = resourceType,
                    Timestamp = DateTime.UtcNow,
                    ForecastConfidence = forecast.Confidence,
                    StorageExhaustionDays = forecast.StorageExhaustionDays,
                    ComputeExhaustionDays = forecast.ComputeExhaustionDays,
                    RecommendationCount = forecast.Recommendations.Count
                };

                _planningHistory.Enqueue(planningEvent);
            }
            catch
            {
                // Continue with other resource types
            }
        }

        while (_planningHistory.Count > 1000)
        {
            _planningHistory.TryDequeue(out _);
        }
    }

    private double CalculateGrowthRate(List<double> values)
    {
        if (values.Count < 2) return 0;

        var n = values.Count;
        var sumX = Enumerable.Range(0, n).Sum();
        var sumY = values.Sum();
        var sumXY = Enumerable.Range(0, n).Select(i => i * values[i]).Sum();
        var sumX2 = Enumerable.Range(0, n).Select(i => i * i).Sum();

        return (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);
    }

    private double ProjectGrowth(double currentValue, double growthRate, int days)
    {
        return currentValue + growthRate * days;
    }

    private int? CalculateExhaustionDays(double current, double max, double growthRate)
    {
        if (growthRate <= 0) return null;
        var remaining = max - current;
        if (remaining <= 0) return 0;
        return (int)Math.Ceiling(remaining / growthRate);
    }

    private async Task<List<CapacityRecommendation>> GetAICapacityRecommendationsAsync(
        string resourceType,
        CapacityMetrics current,
        double storageGrowthRate,
        double computeGrowthRate,
        int forecastDays,
        CancellationToken ct)
    {
        var prompt = $@"Provide capacity planning recommendations based on the following data:

Resource Type: {resourceType}
Current Storage: {current.StorageUsedGB:F1} GB / {current.StorageTotalGB:F1} GB ({current.StorageUsedGB / current.StorageTotalGB * 100:F1}% utilized)
Current Compute: {current.ComputeUtilization:F1}% utilized
Storage Growth Rate: {storageGrowthRate:F2} GB/day
Compute Growth Rate: {computeGrowthRate:F2}%/day
Forecast Period: {forecastDays} days

Provide 3-5 specific capacity planning recommendations as JSON:
[
  {{
    ""priority"": ""<High/Medium/Low>"",
    ""category"": ""<Storage/Compute/Network/Cost>"",
    ""title"": ""<brief title>"",
    ""description"": ""<detailed recommendation>"",
    ""estimatedImpact"": ""<expected benefit>"",
    ""timeframe"": ""<when to implement>""
  }}
]";

        var response = await _providerSelector.ExecuteWithFailoverAsync(
            AIOpsCapabilities.CapacityPlanning,
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
                var doc = JsonDocument.Parse(json);

                return doc.RootElement.EnumerateArray().Select(elem => new CapacityRecommendation
                {
                    Priority = Enum.TryParse<RecommendationPriority>(
                        elem.TryGetProperty("priority", out var p) ? p.GetString() : "Medium",
                        out var priority) ? priority : RecommendationPriority.Medium,
                    Category = elem.TryGetProperty("category", out var c) ? c.GetString() ?? "General" : "General",
                    Title = elem.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    Description = elem.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                    EstimatedImpact = elem.TryGetProperty("estimatedImpact", out var i) ? i.GetString() ?? "" : "",
                    Timeframe = elem.TryGetProperty("timeframe", out var tf) ? tf.GetString() ?? "" : ""
                }).ToList();
            }
        }

        return GenerateBasicRecommendations(current, null, null);
    }

    private List<CapacityRecommendation> GenerateBasicRecommendations(
        CapacityMetrics current,
        int? storageExhaustionDays,
        int? computeExhaustionDays)
    {
        var recommendations = new List<CapacityRecommendation>();

        var storageUtilization = current.StorageUsedGB / current.StorageTotalGB * 100;

        if (storageUtilization > 80 || (storageExhaustionDays.HasValue && storageExhaustionDays < 30))
        {
            recommendations.Add(new CapacityRecommendation
            {
                Priority = RecommendationPriority.High,
                Category = "Storage",
                Title = "Expand Storage Capacity",
                Description = $"Storage utilization at {storageUtilization:F1}%. Consider adding capacity.",
                EstimatedImpact = "Prevent storage exhaustion",
                Timeframe = "Within 2 weeks"
            });
        }

        if (current.ComputeUtilization > 70 || (computeExhaustionDays.HasValue && computeExhaustionDays < 30))
        {
            recommendations.Add(new CapacityRecommendation
            {
                Priority = RecommendationPriority.Medium,
                Category = "Compute",
                Title = "Scale Compute Resources",
                Description = $"Compute utilization at {current.ComputeUtilization:F1}%. Consider scaling up.",
                EstimatedImpact = "Maintain performance during growth",
                Timeframe = "Within 1 month"
            });
        }

        return recommendations;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _planningTimer.Dispose();
    }
}

#region Supporting Types

public sealed class CapacityEngineConfig
{
    public int PlanningIntervalHours { get; set; } = 24;
    public int HistoryWindowDays { get; set; } = 90;
    public int MinDataPointsForForecast { get; set; } = 168; // 1 week of hourly data
}

public sealed class CapacityMetrics
{
    public double StorageUsedGB { get; init; }
    public double StorageTotalGB { get; init; }
    public double ComputeUtilization { get; init; }
    public double NetworkBandwidthMbps { get; init; }
    public int ActiveConnections { get; init; }
    public double IOPSUsed { get; init; }
    public double IOPSTotal { get; init; }
}

public sealed class TimestampedCapacityMetrics
{
    public DateTime Timestamp { get; init; }
    public CapacityMetrics Metrics { get; init; } = new();
}

public sealed class CapacityMetricsSeries
{
    public string ResourceType { get; init; } = string.Empty;
    public int WindowSize { get; init; }
    public List<TimestampedCapacityMetrics> MetricsHistory { get; } = new();

    public void AddMetrics(CapacityMetrics metrics)
    {
        MetricsHistory.Add(new TimestampedCapacityMetrics
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

public sealed class CapacityForecast
{
    public string ResourceType { get; init; } = string.Empty;
    public int ForecastDays { get; init; }
    public DateTime GeneratedAt { get; init; }
    public double CurrentStorage { get; init; }
    public double CurrentStorageUtilization { get; init; }
    public double CurrentComputeUtilization { get; init; }
    public double StorageGrowthRatePerDay { get; init; }
    public double ComputeGrowthRatePerDay { get; init; }
    public double ProjectedStorageGB { get; init; }
    public double ProjectedStorageUtilization { get; init; }
    public double ProjectedComputeUtilization { get; init; }
    public int? StorageExhaustionDays { get; init; }
    public int? ComputeExhaustionDays { get; init; }
    public List<DailyCapacityProjection> DailyProjections { get; init; } = new();
    public List<CapacityRecommendation> Recommendations { get; init; } = new();
    public double Confidence { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class DailyCapacityProjection
{
    public int Day { get; init; }
    public DateTime Date { get; init; }
    public double StorageUsedGB { get; init; }
    public double StorageUtilization { get; init; }
    public double ComputeUtilization { get; init; }
    public double NetworkUtilization { get; init; }
}

public sealed class CapacityRecommendation
{
    public RecommendationPriority Priority { get; init; }
    public string Category { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string EstimatedImpact { get; init; } = string.Empty;
    public string Timeframe { get; init; } = string.Empty;
}

public enum RecommendationPriority
{
    Low,
    Medium,
    High,
    Critical
}

public sealed class CostRates
{
    public double StoragePerGBMonth { get; init; } = 0.023; // AWS S3 standard
    public double ComputePerUnitMonth { get; init; } = 100;
    public double NetworkPerMbpsMonth { get; init; } = 0.09;
}

public sealed class BudgetProjection
{
    public string ResourceType { get; init; } = string.Empty;
    public int ForecastMonths { get; init; }
    public DateTime GeneratedAt { get; init; }
    public List<MonthlyBudgetProjection> MonthlyProjections { get; init; } = new();
    public double TotalProjectedBudget { get; init; }
    public double AverageMonthlyCost { get; init; }
    public double CostGrowthRate { get; init; }
    public double Confidence { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class MonthlyBudgetProjection
{
    public int Month { get; init; }
    public DateTime Date { get; init; }
    public double StorageCost { get; init; }
    public double ComputeCost { get; init; }
    public double NetworkCost { get; init; }
    public double TotalCost { get; init; }
}

public sealed class CapacityPlanningEvent
{
    public string ResourceType { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public double ForecastConfidence { get; init; }
    public int? StorageExhaustionDays { get; init; }
    public int? ComputeExhaustionDays { get; init; }
    public int RecommendationCount { get; init; }
}

public sealed class CapacityStatistics
{
    public int TrackedResourceTypes { get; init; }
    public int TotalDataPoints { get; init; }
    public Dictionary<string, ResourceTypeStats> ResourceTypeStats { get; init; } = new();
    public List<CapacityPlanningEvent> RecentPlanningEvents { get; init; } = new();
}

public sealed class ResourceTypeStats
{
    public string ResourceType { get; init; } = string.Empty;
    public int DataPoints { get; init; }
    public double CurrentStorageUtilization { get; init; }
    public double CurrentComputeUtilization { get; init; }
    public double StorageGrowthRatePerDay { get; init; }
    public DateTime LastUpdated { get; init; }
}

#endregion
