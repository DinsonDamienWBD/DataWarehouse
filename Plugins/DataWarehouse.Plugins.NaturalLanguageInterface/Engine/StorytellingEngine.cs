// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.NaturalLanguageInterface.Models;
using DataWarehouse.SDK.AI;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.NaturalLanguageInterface.Engine;

/// <summary>
/// Engine for generating data narratives and storytelling.
/// Creates human-readable summaries, trend analysis, and visualization recommendations.
/// </summary>
public sealed class StorytellingEngine
{
    private readonly IAIProviderRegistry? _aiRegistry;
    private readonly StorytellingEngineConfig _config;

    public StorytellingEngine(IAIProviderRegistry? aiRegistry = null, StorytellingEngineConfig? config = null)
    {
        _aiRegistry = aiRegistry;
        _config = config ?? new StorytellingEngineConfig();
    }

    /// <summary>
    /// Generate a narrative summary of data.
    /// </summary>
    public async Task<StoryResult> GenerateNarrativeAsync(
        NarrativeRequest request,
        CancellationToken ct = default)
    {
        var story = new StoryResult
        {
            Title = GenerateTitle(request),
            TimeRange = request.TimeRange
        };

        // Calculate key metrics
        var metrics = CalculateMetrics(request.Data);
        story.KeyMetrics = metrics;

        // Identify trends
        var trends = IdentifyTrends(request.Data, request.TimeRange);
        story.Trends = trends;

        // Generate narrative sections
        var sections = new List<NarrativeSection>();

        // Executive summary
        var summary = await GenerateSectionAsync("Executive Summary", request, metrics, trends, ct);
        sections.Add(new NarrativeSection
        {
            Title = "Executive Summary",
            Content = summary,
            Order = 1
        });

        // Key findings
        var findings = GenerateKeyFindings(metrics, trends);
        sections.Add(new NarrativeSection
        {
            Title = "Key Findings",
            Content = string.Join("\n", findings.Select((f, i) => $"{i + 1}. {f}")),
            Order = 2,
            Highlights = findings
        });

        // Trend analysis
        if (trends.Count > 0)
        {
            var trendNarrative = await GenerateTrendNarrativeAsync(trends, ct);
            sections.Add(new NarrativeSection
            {
                Title = "Trend Analysis",
                Content = trendNarrative,
                Order = 3
            });
        }

        // Recommendations
        var recommendations = GenerateRecommendations(metrics, trends);
        if (recommendations.Count > 0)
        {
            sections.Add(new NarrativeSection
            {
                Title = "Recommendations",
                Content = string.Join("\n", recommendations.Select((r, i) => $"{i + 1}. {r}")),
                Order = 4,
                Highlights = recommendations
            });
        }

        story.Sections = sections;

        // Generate visualization recommendations
        story.VisualizationRecommendations = GenerateVisualizationRecommendations(request.Data, trends);

        return story;
    }

    /// <summary>
    /// Summarize backup history.
    /// </summary>
    public async Task<StoryResult> SummarizeBackupHistoryAsync(
        BackupHistoryRequest request,
        CancellationToken ct = default)
    {
        var story = new StoryResult
        {
            Title = $"Backup Summary - {request.TimeRange?.RelativeDescription ?? "All Time"}",
            TimeRange = request.TimeRange
        };

        // Calculate backup metrics
        var totalBackups = request.Backups?.Count ?? 0;
        var successfulBackups = request.Backups?.Count(b => b.Success) ?? 0;
        var failedBackups = totalBackups - successfulBackups;
        var totalSize = request.Backups?.Sum(b => b.SizeBytes) ?? 0;
        var avgDuration = request.Backups?.Average(b => b.DurationSeconds) ?? 0;

        story.KeyMetrics = new Dictionary<string, MetricValue>
        {
            ["Total Backups"] = new() { Value = totalBackups, Unit = "backups" },
            ["Success Rate"] = new() { Value = totalBackups > 0 ? (double)successfulBackups / totalBackups * 100 : 0, Unit = "%", Format = "F1" },
            ["Total Size"] = new() { Value = totalSize, Unit = "bytes", Format = "size" },
            ["Average Duration"] = new() { Value = avgDuration, Unit = "seconds", Format = "duration" }
        };

        var sections = new List<NarrativeSection>();

        // Summary section
        var summaryBuilder = new StringBuilder();

        if (totalBackups == 0)
        {
            summaryBuilder.AppendLine("No backups were found for the specified period.");
        }
        else
        {
            summaryBuilder.AppendLine($"During this period, {totalBackups} backup operations were performed.");
            summaryBuilder.AppendLine($"Of these, {successfulBackups} completed successfully ({(double)successfulBackups / totalBackups:P0} success rate).");

            if (failedBackups > 0)
            {
                summaryBuilder.AppendLine($"There were {failedBackups} failed backup attempts that may require investigation.");
            }

            summaryBuilder.AppendLine($"The total data backed up amounts to {FormatSize(totalSize)}.");
        }

        sections.Add(new NarrativeSection
        {
            Title = "Summary",
            Content = summaryBuilder.ToString(),
            Order = 1
        });

        // Backup schedule analysis
        if (request.Backups?.Count > 1)
        {
            var backupsByDay = request.Backups
                .GroupBy(b => b.CreatedAt.DayOfWeek)
                .OrderByDescending(g => g.Count())
                .ToList();

            var scheduleContent = new StringBuilder();
            scheduleContent.AppendLine("Backup distribution by day of week:");
            foreach (var day in backupsByDay)
            {
                scheduleContent.AppendLine($"  - {day.Key}: {day.Count()} backups");
            }

            sections.Add(new NarrativeSection
            {
                Title = "Schedule Analysis",
                Content = scheduleContent.ToString(),
                Order = 2
            });
        }

        // Issues and recommendations
        var issues = new List<string>();
        var recommendations = new List<string>();

        if (failedBackups > 0 && (double)failedBackups / totalBackups > 0.1)
        {
            issues.Add($"High failure rate: {failedBackups} out of {totalBackups} backups failed.");
            recommendations.Add("Investigate failed backup causes and consider implementing retry policies.");
        }

        if (avgDuration > 3600)
        {
            issues.Add($"Backups are taking a long time (average: {FormatDuration(avgDuration)}).");
            recommendations.Add("Consider incremental backups or compression to reduce backup duration.");
        }

        var lastBackup = request.Backups?.OrderByDescending(b => b.CreatedAt).FirstOrDefault();
        if (lastBackup != null && (DateTime.UtcNow - lastBackup.CreatedAt).TotalDays > 7)
        {
            issues.Add($"No recent backups - last backup was {(DateTime.UtcNow - lastBackup.CreatedAt).Days} days ago.");
            recommendations.Add("Ensure backup schedules are running correctly.");
        }

        if (issues.Count > 0)
        {
            sections.Add(new NarrativeSection
            {
                Title = "Issues Identified",
                Content = string.Join("\n", issues.Select((i, idx) => $"{idx + 1}. {i}")),
                Order = 3,
                Highlights = issues
            });
        }

        if (recommendations.Count > 0)
        {
            sections.Add(new NarrativeSection
            {
                Title = "Recommendations",
                Content = string.Join("\n", recommendations.Select((r, idx) => $"{idx + 1}. {r}")),
                Order = 4,
                Highlights = recommendations
            });
        }

        story.Sections = sections;

        // Visualization recommendations
        story.VisualizationRecommendations = new List<VisualizationRecommendation>
        {
            new()
            {
                Type = "timeline",
                Title = "Backup Timeline",
                Description = "Shows backup operations over time",
                DataMapping = new Dictionary<string, string>
                {
                    ["x"] = "createdAt",
                    ["y"] = "sizeBytes",
                    ["color"] = "success"
                }
            },
            new()
            {
                Type = "pie",
                Title = "Backup Success Rate",
                Description = "Distribution of successful vs failed backups",
                DataMapping = new Dictionary<string, string>
                {
                    ["value"] = "count",
                    ["label"] = "status"
                }
            }
        };

        return story;
    }

    /// <summary>
    /// Generate a storage usage report narrative.
    /// </summary>
    public async Task<StoryResult> GenerateStorageReportAsync(
        StorageReportRequest request,
        CancellationToken ct = default)
    {
        var story = new StoryResult
        {
            Title = $"Storage Report - {request.ReportDate:MMMM yyyy}",
            TimeRange = new TimeRange { End = request.ReportDate }
        };

        // Key metrics
        story.KeyMetrics = new Dictionary<string, MetricValue>
        {
            ["Total Storage"] = new() { Value = request.TotalBytes, Unit = "bytes", Format = "size" },
            ["Used Storage"] = new() { Value = request.UsedBytes, Unit = "bytes", Format = "size" },
            ["Available"] = new() { Value = request.TotalBytes - request.UsedBytes, Unit = "bytes", Format = "size" },
            ["Utilization"] = new() { Value = request.TotalBytes > 0 ? (double)request.UsedBytes / request.TotalBytes * 100 : 0, Unit = "%", Format = "F1" }
        };

        var sections = new List<NarrativeSection>();

        // Overview
        var utilization = request.TotalBytes > 0 ? (double)request.UsedBytes / request.TotalBytes : 0;
        var overviewBuilder = new StringBuilder();

        overviewBuilder.AppendLine($"Your data warehouse currently holds {FormatSize(request.UsedBytes)} of data.");
        overviewBuilder.AppendLine($"This represents {utilization:P0} of your total capacity ({FormatSize(request.TotalBytes)}).");

        if (utilization > 0.9)
        {
            overviewBuilder.AppendLine("Storage is nearing capacity. Consider archiving old data or expanding storage.");
        }
        else if (utilization > 0.75)
        {
            overviewBuilder.AppendLine("Storage utilization is healthy but worth monitoring.");
        }
        else
        {
            overviewBuilder.AppendLine("Storage utilization is well within comfortable limits.");
        }

        sections.Add(new NarrativeSection
        {
            Title = "Overview",
            Content = overviewBuilder.ToString(),
            Order = 1
        });

        // Breakdown by type
        if (request.ByFileType?.Count > 0)
        {
            var typeBuilder = new StringBuilder();
            typeBuilder.AppendLine("Storage breakdown by file type:");

            var sortedTypes = request.ByFileType
                .OrderByDescending(kv => kv.Value)
                .Take(10);

            foreach (var (type, size) in sortedTypes)
            {
                var pct = request.UsedBytes > 0 ? (double)size / request.UsedBytes * 100 : 0;
                typeBuilder.AppendLine($"  - {type}: {FormatSize(size)} ({pct:F1}%)");
            }

            sections.Add(new NarrativeSection
            {
                Title = "By File Type",
                Content = typeBuilder.ToString(),
                Order = 2
            });
        }

        // Breakdown by tier
        if (request.ByTier?.Count > 0)
        {
            var tierBuilder = new StringBuilder();
            tierBuilder.AppendLine("Storage distribution across tiers:");

            foreach (var (tier, size) in request.ByTier.OrderByDescending(kv => kv.Value))
            {
                var pct = request.UsedBytes > 0 ? (double)size / request.UsedBytes * 100 : 0;
                tierBuilder.AppendLine($"  - {tier}: {FormatSize(size)} ({pct:F1}%)");
            }

            sections.Add(new NarrativeSection
            {
                Title = "By Storage Tier",
                Content = tierBuilder.ToString(),
                Order = 3
            });
        }

        // Growth trend
        if (request.HistoricalUsage?.Count > 1)
        {
            var growthBuilder = new StringBuilder();
            var firstUsage = request.HistoricalUsage.First();
            var lastUsage = request.HistoricalUsage.Last();
            var growth = lastUsage.Value - firstUsage.Value;
            var growthPct = firstUsage.Value > 0 ? (double)growth / firstUsage.Value * 100 : 0;

            growthBuilder.AppendLine($"Over the reporting period, storage usage changed by {FormatSize(Math.Abs(growth))} ({(growth >= 0 ? "+" : "")}{growthPct:F1}%).");

            if (growth > 0)
            {
                var avgGrowth = growth / (request.HistoricalUsage.Count - 1);
                growthBuilder.AppendLine($"Average growth rate: {FormatSize(avgGrowth)} per period.");

                // Project future usage
                var daysUntilFull = (request.TotalBytes - request.UsedBytes) / (avgGrowth > 0 ? avgGrowth : 1);
                if (daysUntilFull < 365 && avgGrowth > 0)
                {
                    growthBuilder.AppendLine($"At this rate, storage will be full in approximately {daysUntilFull:F0} periods.");
                }
            }

            sections.Add(new NarrativeSection
            {
                Title = "Growth Trend",
                Content = growthBuilder.ToString(),
                Order = 4
            });

            story.Trends = new List<TrendInfo>
            {
                new()
                {
                    Name = "Storage Growth",
                    Direction = growth > 0 ? "increasing" : growth < 0 ? "decreasing" : "stable",
                    ChangePercent = growthPct,
                    Period = "reporting period"
                }
            };
        }

        // Recommendations
        var recommendations = new List<string>();

        if (utilization > 0.85)
        {
            recommendations.Add("Consider expanding storage capacity or implementing data retention policies.");
        }

        if (request.ByTier?.TryGetValue("Hot", out var hotSize) == true &&
            request.UsedBytes > 0 &&
            (double)hotSize / request.UsedBytes > 0.6)
        {
            recommendations.Add("Over 60% of data is in the hot tier. Review data access patterns for tiering opportunities.");
        }

        if (recommendations.Count > 0)
        {
            sections.Add(new NarrativeSection
            {
                Title = "Recommendations",
                Content = string.Join("\n", recommendations.Select((r, i) => $"{i + 1}. {r}")),
                Order = 5,
                Highlights = recommendations
            });
        }

        story.Sections = sections;

        // Visualizations
        story.VisualizationRecommendations = new List<VisualizationRecommendation>
        {
            new()
            {
                Type = "pie",
                Title = "Storage by File Type",
                Description = "Distribution of storage across file types"
            },
            new()
            {
                Type = "bar",
                Title = "Storage by Tier",
                Description = "Distribution across storage tiers"
            },
            new()
            {
                Type = "line",
                Title = "Usage Over Time",
                Description = "Storage growth trend"
            },
            new()
            {
                Type = "gauge",
                Title = "Capacity Utilization",
                Description = "Current storage utilization percentage"
            }
        };

        return story;
    }

    #region Private Methods

    private string GenerateTitle(NarrativeRequest request)
    {
        var timeDesc = request.TimeRange?.RelativeDescription ?? "All Time";
        return request.NarrativeType switch
        {
            "storage" => $"Storage Analysis - {timeDesc}",
            "backup" => $"Backup Summary - {timeDesc}",
            "activity" => $"Activity Report - {timeDesc}",
            "performance" => $"Performance Analysis - {timeDesc}",
            _ => $"Data Summary - {timeDesc}"
        };
    }

    private Dictionary<string, MetricValue> CalculateMetrics(DataSeries? data)
    {
        var metrics = new Dictionary<string, MetricValue>();

        if (data?.Points == null || data.Points.Count == 0)
        {
            return metrics;
        }

        var values = data.Points.Select(p => p.Value).ToList();

        metrics["Total"] = new() { Value = values.Sum(), Unit = data.Unit ?? "" };
        metrics["Average"] = new() { Value = values.Average(), Unit = data.Unit ?? "", Format = "F2" };
        metrics["Maximum"] = new() { Value = values.Max(), Unit = data.Unit ?? "" };
        metrics["Minimum"] = new() { Value = values.Min(), Unit = data.Unit ?? "" };

        if (values.Count > 1)
        {
            var change = values.Last() - values.First();
            var changePct = values.First() != 0 ? change / values.First() * 100 : 0;
            metrics["Change"] = new() { Value = changePct, Unit = "%", Format = "F1" };
        }

        return metrics;
    }

    private List<TrendInfo> IdentifyTrends(DataSeries? data, TimeRange? timeRange)
    {
        var trends = new List<TrendInfo>();

        if (data?.Points == null || data.Points.Count < 2)
        {
            return trends;
        }

        var values = data.Points.OrderBy(p => p.Timestamp).Select(p => p.Value).ToList();

        // Simple linear regression to identify trend
        var n = values.Count;
        var xMean = (n - 1) / 2.0;
        var yMean = values.Average();

        var numerator = 0.0;
        var denominator = 0.0;

        for (int i = 0; i < n; i++)
        {
            numerator += (i - xMean) * (values[i] - yMean);
            denominator += (i - xMean) * (i - xMean);
        }

        var slope = denominator != 0 ? numerator / denominator : 0;
        var direction = slope > 0.1 * yMean / n ? "increasing" :
                        slope < -0.1 * yMean / n ? "decreasing" : "stable";

        var changePct = values.First() != 0
            ? (values.Last() - values.First()) / values.First() * 100
            : 0;

        trends.Add(new TrendInfo
        {
            Name = data.Name ?? "Primary Metric",
            Direction = direction,
            ChangePercent = changePct,
            Period = timeRange?.RelativeDescription ?? "observed period"
        });

        return trends;
    }

    private async Task<string> GenerateSectionAsync(
        string sectionType,
        NarrativeRequest request,
        Dictionary<string, MetricValue> metrics,
        List<TrendInfo> trends,
        CancellationToken ct)
    {
        // Try AI-generated content
        if (_aiRegistry != null && _config.UseAIForNarratives)
        {
            var provider = _aiRegistry.GetProvider(_config.ProviderRouting.StorytellingProvider)
                ?? _aiRegistry.GetDefaultProvider();

            if (provider != null && provider.IsAvailable)
            {
                try
                {
                    var content = await GenerateAINarrativeAsync(provider, sectionType, request, metrics, trends, ct);
                    if (!string.IsNullOrEmpty(content))
                    {
                        return content;
                    }
                }
                catch
                {
                    // Fall through to template
                }
            }
        }

        // Template-based generation
        return GenerateTemplateSummary(metrics, trends);
    }

    private async Task<string?> GenerateAINarrativeAsync(
        IAIProvider provider,
        string sectionType,
        NarrativeRequest request,
        Dictionary<string, MetricValue> metrics,
        List<TrendInfo> trends,
        CancellationToken ct)
    {
        var systemPrompt = """
            You are a data analyst writing a clear, professional narrative about data warehouse metrics.
            Write in a concise, business-friendly style. Focus on insights, not just numbers.
            Do not use markdown. Keep the summary to 2-4 sentences.
            """;

        var metricsStr = string.Join("\n", metrics.Select(kv =>
            $"- {kv.Key}: {FormatMetricValue(kv.Value)}"));

        var trendsStr = trends.Count > 0
            ? string.Join("\n", trends.Select(t =>
                $"- {t.Name} is {t.Direction} ({t.ChangePercent:+0.0;-0.0}% over {t.Period})"))
            : "No significant trends identified.";

        var prompt = $"""
            Generate a {sectionType} for this data:

            Time period: {request.TimeRange?.RelativeDescription ?? "all time"}
            Type: {request.NarrativeType}

            Metrics:
            {metricsStr}

            Trends:
            {trendsStr}
            """;

        var aiRequest = new AIRequest
        {
            SystemMessage = systemPrompt,
            Prompt = prompt,
            Temperature = 0.6f,
            MaxTokens = 200
        };

        var response = await provider.CompleteAsync(aiRequest, ct);
        return response.Success ? response.Content : null;
    }

    private async Task<string> GenerateTrendNarrativeAsync(List<TrendInfo> trends, CancellationToken ct)
    {
        var builder = new StringBuilder();

        foreach (var trend in trends)
        {
            builder.AppendLine($"The {trend.Name.ToLower()} is {trend.Direction} over the {trend.Period}.");

            if (Math.Abs(trend.ChangePercent) > 20)
            {
                builder.AppendLine(trend.ChangePercent > 0
                    ? $"This represents a significant increase of {trend.ChangePercent:F1}%."
                    : $"This represents a significant decrease of {Math.Abs(trend.ChangePercent):F1}%.");
            }
        }

        return builder.ToString();
    }

    private string GenerateTemplateSummary(Dictionary<string, MetricValue> metrics, List<TrendInfo> trends)
    {
        var builder = new StringBuilder();

        if (metrics.TryGetValue("Total", out var total))
        {
            builder.AppendLine($"The total value is {FormatMetricValue(total)}.");
        }

        if (metrics.TryGetValue("Average", out var avg))
        {
            builder.AppendLine($"The average is {FormatMetricValue(avg)}.");
        }

        if (trends.Count > 0)
        {
            var mainTrend = trends.First();
            builder.AppendLine($"The overall trend is {mainTrend.Direction} ({mainTrend.ChangePercent:+0.0;-0.0}%).");
        }

        return builder.ToString();
    }

    private List<string> GenerateKeyFindings(Dictionary<string, MetricValue> metrics, List<TrendInfo> trends)
    {
        var findings = new List<string>();

        if (metrics.TryGetValue("Change", out var change) && Math.Abs(change.Value) > 10)
        {
            findings.Add(change.Value > 0
                ? $"Significant growth of {change.Value:F1}% observed."
                : $"Notable decline of {Math.Abs(change.Value):F1}% observed.");
        }

        foreach (var trend in trends.Where(t => t.Direction != "stable"))
        {
            findings.Add($"{trend.Name} is {trend.Direction}.");
        }

        if (findings.Count == 0)
        {
            findings.Add("Metrics are stable with no significant changes.");
        }

        return findings;
    }

    private List<string> GenerateRecommendations(Dictionary<string, MetricValue> metrics, List<TrendInfo> trends)
    {
        var recommendations = new List<string>();

        foreach (var trend in trends)
        {
            if (trend.Direction == "increasing" && trend.ChangePercent > 50)
            {
                recommendations.Add($"Monitor {trend.Name.ToLower()} closely as it's growing rapidly.");
            }
            else if (trend.Direction == "decreasing" && trend.ChangePercent < -30)
            {
                recommendations.Add($"Investigate the decline in {trend.Name.ToLower()}.");
            }
        }

        return recommendations;
    }

    private List<VisualizationRecommendation> GenerateVisualizationRecommendations(DataSeries? data, List<TrendInfo> trends)
    {
        var recommendations = new List<VisualizationRecommendation>();

        if (data?.Points?.Count > 2)
        {
            recommendations.Add(new VisualizationRecommendation
            {
                Type = "line",
                Title = $"{data.Name ?? "Metric"} Over Time",
                Description = "Shows how the metric changes over the time period",
                DataMapping = new Dictionary<string, string>
                {
                    ["x"] = "timestamp",
                    ["y"] = "value"
                }
            });
        }

        if (trends.Any(t => t.Direction != "stable"))
        {
            recommendations.Add(new VisualizationRecommendation
            {
                Type = "area",
                Title = "Trend Analysis",
                Description = "Highlights growth or decline patterns"
            });
        }

        return recommendations;
    }

    private string FormatMetricValue(MetricValue metric)
    {
        if (metric.Format == "size")
        {
            return FormatSize((long)metric.Value);
        }
        if (metric.Format == "duration")
        {
            return FormatDuration(metric.Value);
        }

        var formatted = metric.Format != null
            ? metric.Value.ToString(metric.Format)
            : metric.Value.ToString("N0");

        return !string.IsNullOrEmpty(metric.Unit)
            ? $"{formatted} {metric.Unit}"
            : formatted;
    }

    private static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        var order = 0;
        var size = (double)bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:F2} {sizes[order]}";
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 60) return $"{seconds:F0} seconds";
        if (seconds < 3600) return $"{seconds / 60:F1} minutes";
        return $"{seconds / 3600:F1} hours";
    }

    #endregion
}

#region Storytelling Models

/// <summary>
/// Result of a storytelling request.
/// </summary>
public sealed class StoryResult
{
    public string Title { get; set; } = string.Empty;
    public TimeRange? TimeRange { get; set; }
    public Dictionary<string, MetricValue> KeyMetrics { get; set; } = new();
    public List<TrendInfo> Trends { get; set; } = new();
    public List<NarrativeSection> Sections { get; set; } = new();
    public List<VisualizationRecommendation> VisualizationRecommendations { get; set; } = new();
}

/// <summary>
/// A metric value with metadata.
/// </summary>
public sealed class MetricValue
{
    public double Value { get; init; }
    public string Unit { get; init; } = string.Empty;
    public string? Format { get; init; }
}

/// <summary>
/// Information about a trend.
/// </summary>
public sealed class TrendInfo
{
    public string Name { get; init; } = string.Empty;
    public string Direction { get; init; } = "stable"; // increasing, decreasing, stable
    public double ChangePercent { get; init; }
    public string Period { get; init; } = string.Empty;
}

/// <summary>
/// A section of the narrative.
/// </summary>
public sealed class NarrativeSection
{
    public string Title { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public int Order { get; init; }
    public List<string>? Highlights { get; init; }
}

/// <summary>
/// Recommendation for a visualization.
/// </summary>
public sealed class VisualizationRecommendation
{
    public string Type { get; init; } = string.Empty; // line, bar, pie, area, gauge, timeline
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public Dictionary<string, string>? DataMapping { get; init; }
}

/// <summary>
/// Request for narrative generation.
/// </summary>
public sealed class NarrativeRequest
{
    public string NarrativeType { get; init; } = "general"; // storage, backup, activity, performance
    public TimeRange? TimeRange { get; init; }
    public DataSeries? Data { get; init; }
    public Dictionary<string, object>? Context { get; init; }
}

/// <summary>
/// A series of data points.
/// </summary>
public sealed class DataSeries
{
    public string? Name { get; init; }
    public string? Unit { get; init; }
    public List<DataPoint> Points { get; init; } = new();
}

/// <summary>
/// A single data point.
/// </summary>
public sealed class DataPoint
{
    public DateTime Timestamp { get; init; }
    public double Value { get; init; }
    public string? Label { get; init; }
}

/// <summary>
/// Request for backup history summary.
/// </summary>
public sealed class BackupHistoryRequest
{
    public TimeRange? TimeRange { get; init; }
    public List<BackupInfo>? Backups { get; init; }
}

/// <summary>
/// Information about a backup.
/// </summary>
public sealed class BackupInfo
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public long SizeBytes { get; init; }
    public double DurationSeconds { get; init; }
    public bool Success { get; init; }
    public string? FailureReason { get; init; }
}

/// <summary>
/// Request for storage report.
/// </summary>
public sealed class StorageReportRequest
{
    public DateTime ReportDate { get; init; } = DateTime.UtcNow;
    public long TotalBytes { get; init; }
    public long UsedBytes { get; init; }
    public Dictionary<string, long>? ByFileType { get; init; }
    public Dictionary<string, long>? ByTier { get; init; }
    public List<KeyValuePair<DateTime, long>>? HistoricalUsage { get; init; }
}

#endregion

/// <summary>
/// Configuration for the storytelling engine.
/// </summary>
public sealed class StorytellingEngineConfig
{
    /// <summary>Whether to use AI for narrative generation.</summary>
    public bool UseAIForNarratives { get; init; } = true;

    /// <summary>AI provider routing configuration.</summary>
    public AIProviderRouting ProviderRouting { get; init; } = new();
}
