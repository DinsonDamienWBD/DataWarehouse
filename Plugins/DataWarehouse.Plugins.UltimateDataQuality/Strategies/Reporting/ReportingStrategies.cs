using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDataQuality.Strategies.Reporting;

#region Report Types

/// <summary>
/// Quality report output format.
/// </summary>
public enum ReportFormat
{
    /// <summary>JSON format.</summary>
    Json,
    /// <summary>HTML format.</summary>
    Html,
    /// <summary>Markdown format.</summary>
    Markdown,
    /// <summary>Plain text format.</summary>
    Text,
    /// <summary>CSV format.</summary>
    Csv
}

/// <summary>
/// Quality report configuration.
/// </summary>
public sealed class ReportConfig
{
    /// <summary>
    /// Report title.
    /// </summary>
    public string Title { get; init; } = "Data Quality Report";

    /// <summary>
    /// Output format.
    /// </summary>
    public ReportFormat Format { get; init; } = ReportFormat.Html;

    /// <summary>
    /// Whether to include detailed field analysis.
    /// </summary>
    public bool IncludeFieldDetails { get; init; } = true;

    /// <summary>
    /// Whether to include trend analysis.
    /// </summary>
    public bool IncludeTrends { get; init; } = true;

    /// <summary>
    /// Whether to include recommendations.
    /// </summary>
    public bool IncludeRecommendations { get; init; } = true;

    /// <summary>
    /// Maximum issues to include.
    /// </summary>
    public int MaxIssues { get; init; } = 100;

    /// <summary>
    /// Report period.
    /// </summary>
    public TimeSpan? ReportPeriod { get; init; }
}

/// <summary>
/// Generated quality report.
/// </summary>
public sealed class QualityReport
{
    /// <summary>
    /// Report identifier.
    /// </summary>
    public string ReportId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Report title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// When the report was generated.
    /// </summary>
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Report format.
    /// </summary>
    public ReportFormat Format { get; init; }

    /// <summary>
    /// Report content.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// Report summary.
    /// </summary>
    public required ReportSummary Summary { get; init; }
}

/// <summary>
/// Report summary data.
/// </summary>
public sealed class ReportSummary
{
    /// <summary>
    /// Overall quality score.
    /// </summary>
    public double OverallScore { get; init; }

    /// <summary>
    /// Total records analyzed.
    /// </summary>
    public int TotalRecords { get; init; }

    /// <summary>
    /// Total issues found.
    /// </summary>
    public int TotalIssues { get; init; }

    /// <summary>
    /// Critical issues count.
    /// </summary>
    public int CriticalIssues { get; init; }

    /// <summary>
    /// Dimension scores.
    /// </summary>
    public Dictionary<string, double> DimensionScores { get; init; } = new();

    /// <summary>
    /// Top recommendations.
    /// </summary>
    public List<string> TopRecommendations { get; init; } = new();
}

/// <summary>
/// Report section.
/// </summary>
public sealed class ReportSection
{
    /// <summary>
    /// Section title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Section content.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// Section order.
    /// </summary>
    public int Order { get; init; }
}

#endregion

#region Report Generation Strategy

/// <summary>
/// Quality report generation strategy.
/// </summary>
public sealed class ReportGenerationStrategy : DataQualityStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "report-generation";

    /// <inheritdoc/>
    public override string DisplayName => "Quality Report Generation";

    /// <inheritdoc/>
    public override DataQualityCategory Category => DataQualityCategory.Reporting;

    /// <inheritdoc/>
    public override DataQualityCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = false,
        SupportsStreaming = false,
        SupportsDistributed = false,
        SupportsIncremental = false,
        MaxThroughput = 100,
        TypicalLatencyMs = 500
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Generates comprehensive data quality reports in multiple formats. " +
        "Includes executive summaries, detailed analysis, and actionable recommendations.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "reporting", "documentation", "analysis", "recommendations"
    };

    /// <summary>
    /// Generates a quality report from scoring results.
    /// </summary>
    public Task<QualityReport> GenerateReportAsync(
        Scoring.DatasetQualityScore datasetScore,
        ReportConfig config,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var summary = new ReportSummary
        {
            OverallScore = datasetScore.OverallScore,
            TotalRecords = datasetScore.TotalRecords,
            TotalIssues = datasetScore.TopIssues.Sum(i => i.AffectedCount),
            CriticalIssues = datasetScore.TopIssues.Count(i => i.ScoreImpact > 10),
            DimensionScores = datasetScore.DimensionScores.ToDictionary(
                kvp => kvp.Key.ToString(),
                kvp => kvp.Value),
            TopRecommendations = GenerateRecommendations(datasetScore).Take(5).ToList()
        };

        var content = config.Format switch
        {
            ReportFormat.Html => GenerateHtmlReport(datasetScore, config, summary),
            ReportFormat.Markdown => GenerateMarkdownReport(datasetScore, config, summary),
            ReportFormat.Json => GenerateJsonReport(datasetScore, config, summary),
            ReportFormat.Text => GenerateTextReport(datasetScore, config, summary),
            ReportFormat.Csv => GenerateCsvReport(datasetScore, config),
            _ => GenerateTextReport(datasetScore, config, summary)
        };

        return Task.FromResult(new QualityReport
        {
            Title = config.Title,
            Format = config.Format,
            Content = content,
            Summary = summary
        });
    }

    private string GenerateHtmlReport(
        Scoring.DatasetQualityScore score,
        ReportConfig config,
        ReportSummary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html><head>");
        sb.AppendLine($"<title>{config.Title}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body { font-family: Arial, sans-serif; margin: 20px; }");
        sb.AppendLine("h1 { color: #333; }");
        sb.AppendLine("h2 { color: #666; border-bottom: 1px solid #ccc; }");
        sb.AppendLine(".score { font-size: 48px; font-weight: bold; }");
        sb.AppendLine(".excellent { color: #28a745; }");
        sb.AppendLine(".good { color: #5cb85c; }");
        sb.AppendLine(".acceptable { color: #f0ad4e; }");
        sb.AppendLine(".poor { color: #d9534f; }");
        sb.AppendLine(".critical { color: #c9302c; }");
        sb.AppendLine(".metric { display: inline-block; margin: 10px; padding: 15px; border: 1px solid #ddd; border-radius: 5px; }");
        sb.AppendLine("table { border-collapse: collapse; width: 100%; }");
        sb.AppendLine("th, td { border: 1px solid #ddd; padding: 8px; text-align: left; }");
        sb.AppendLine("th { background-color: #f5f5f5; }");
        sb.AppendLine(".issue-row { background-color: #fff3cd; }");
        sb.AppendLine("</style>");
        sb.AppendLine("</head><body>");

        // Header
        sb.AppendLine($"<h1>{config.Title}</h1>");
        sb.AppendLine($"<p>Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</p>");

        // Executive Summary
        sb.AppendLine("<h2>Executive Summary</h2>");
        var gradeClass = score.Grade.ToString().ToLower();
        sb.AppendLine($"<div class='metric'><div class='score {gradeClass}'>{score.OverallScore:F1}</div><div>Overall Score</div></div>");
        sb.AppendLine($"<div class='metric'><div>{score.TotalRecords:N0}</div><div>Total Records</div></div>");
        sb.AppendLine($"<div class='metric'><div>{score.TopIssues.Count}</div><div>Issues Found</div></div>");

        // Dimension Scores
        sb.AppendLine("<h2>Quality Dimensions</h2>");
        sb.AppendLine("<table><tr><th>Dimension</th><th>Score</th><th>Status</th></tr>");
        foreach (var (dim, dimScore) in score.DimensionScores.OrderByDescending(d => d.Value))
        {
            var status = dimScore >= 90 ? "Excellent" : dimScore >= 70 ? "Good" : dimScore >= 50 ? "Needs Improvement" : "Critical";
            sb.AppendLine($"<tr><td>{dim}</td><td>{dimScore:F1}</td><td>{status}</td></tr>");
        }
        sb.AppendLine("</table>");

        // Field Scores
        if (config.IncludeFieldDetails && score.FieldScores.Count > 0)
        {
            sb.AppendLine("<h2>Field Quality</h2>");
            sb.AppendLine("<table><tr><th>Field</th><th>Score</th></tr>");
            foreach (var (field, fieldScore) in score.FieldScores.OrderBy(f => f.Value))
            {
                var rowClass = fieldScore < 70 ? "class='issue-row'" : "";
                sb.AppendLine($"<tr {rowClass}><td>{field}</td><td>{fieldScore:F1}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        // Issues
        if (score.TopIssues.Count > 0)
        {
            sb.AppendLine("<h2>Top Issues</h2>");
            sb.AppendLine("<table><tr><th>Issue</th><th>Affected Records</th><th>Impact</th></tr>");
            foreach (var issue in score.TopIssues.Take(config.MaxIssues))
            {
                sb.AppendLine($"<tr class='issue-row'><td>{issue.Description}</td><td>{issue.AffectedCount:N0}</td><td>{issue.ScoreImpact:F1}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        // Recommendations
        if (config.IncludeRecommendations)
        {
            sb.AppendLine("<h2>Recommendations</h2>");
            sb.AppendLine("<ul>");
            foreach (var rec in summary.TopRecommendations)
            {
                sb.AppendLine($"<li>{rec}</li>");
            }
            sb.AppendLine("</ul>");
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private string GenerateMarkdownReport(
        Scoring.DatasetQualityScore score,
        ReportConfig config,
        ReportSummary summary)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# {config.Title}");
        sb.AppendLine();
        sb.AppendLine($"*Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC*");
        sb.AppendLine();

        // Executive Summary
        sb.AppendLine("## Executive Summary");
        sb.AppendLine();
        sb.AppendLine($"| Metric | Value |");
        sb.AppendLine($"|--------|-------|");
        sb.AppendLine($"| Overall Score | **{score.OverallScore:F1}** ({score.Grade}) |");
        sb.AppendLine($"| Total Records | {score.TotalRecords:N0} |");
        sb.AppendLine($"| Issues Found | {score.TopIssues.Count} |");
        sb.AppendLine();

        // Dimension Scores
        sb.AppendLine("## Quality Dimensions");
        sb.AppendLine();
        sb.AppendLine("| Dimension | Score |");
        sb.AppendLine("|-----------|-------|");
        foreach (var (dim, dimScore) in score.DimensionScores.OrderByDescending(d => d.Value))
        {
            var indicator = dimScore >= 90 ? "+" : dimScore >= 70 ? "=" : dimScore >= 50 ? "!" : "X";
            sb.AppendLine($"| {dim} | {dimScore:F1} {indicator} |");
        }
        sb.AppendLine();

        // Field Scores
        if (config.IncludeFieldDetails && score.FieldScores.Count > 0)
        {
            sb.AppendLine("## Field Quality");
            sb.AppendLine();
            sb.AppendLine("| Field | Score |");
            sb.AppendLine("|-------|-------|");
            foreach (var (field, fieldScore) in score.FieldScores.OrderBy(f => f.Value).Take(20))
            {
                sb.AppendLine($"| {field} | {fieldScore:F1} |");
            }
            sb.AppendLine();
        }

        // Issues
        if (score.TopIssues.Count > 0)
        {
            sb.AppendLine("## Top Issues");
            sb.AppendLine();
            foreach (var issue in score.TopIssues.Take(10))
            {
                sb.AppendLine($"- **{issue.Description}** ({issue.AffectedCount:N0} records affected)");
            }
            sb.AppendLine();
        }

        // Recommendations
        if (config.IncludeRecommendations && summary.TopRecommendations.Count > 0)
        {
            sb.AppendLine("## Recommendations");
            sb.AppendLine();
            foreach (var rec in summary.TopRecommendations)
            {
                sb.AppendLine($"1. {rec}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private string GenerateJsonReport(
        Scoring.DatasetQualityScore score,
        ReportConfig config,
        ReportSummary summary)
    {
        var report = new
        {
            title = config.Title,
            generatedAt = DateTime.UtcNow,
            summary = new
            {
                overallScore = score.OverallScore,
                grade = score.Grade.ToString(),
                totalRecords = score.TotalRecords,
                issueCount = score.TopIssues.Count
            },
            dimensions = score.DimensionScores.ToDictionary(
                kvp => kvp.Key.ToString(),
                kvp => kvp.Value),
            fields = config.IncludeFieldDetails ? score.FieldScores : null,
            issues = score.TopIssues.Take(config.MaxIssues).Select(i => new
            {
                description = i.Description,
                affectedCount = i.AffectedCount,
                impact = i.ScoreImpact
            }),
            recommendations = config.IncludeRecommendations ? summary.TopRecommendations : null
        };

        return JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private string GenerateTextReport(
        Scoring.DatasetQualityScore score,
        ReportConfig config,
        ReportSummary summary)
    {
        var sb = new StringBuilder();

        sb.AppendLine("=".PadRight(60, '='));
        sb.AppendLine($"  {config.Title}");
        sb.AppendLine($"  Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine("=".PadRight(60, '='));
        sb.AppendLine();

        sb.AppendLine("EXECUTIVE SUMMARY");
        sb.AppendLine("-".PadRight(40, '-'));
        sb.AppendLine($"Overall Score:  {score.OverallScore:F1} ({score.Grade})");
        sb.AppendLine($"Total Records:  {score.TotalRecords:N0}");
        sb.AppendLine($"Issues Found:   {score.TopIssues.Count}");
        sb.AppendLine();

        sb.AppendLine("QUALITY DIMENSIONS");
        sb.AppendLine("-".PadRight(40, '-'));
        foreach (var (dim, dimScore) in score.DimensionScores.OrderByDescending(d => d.Value))
        {
            sb.AppendLine($"{dim,-20} {dimScore,8:F1}");
        }
        sb.AppendLine();

        if (score.TopIssues.Count > 0)
        {
            sb.AppendLine("TOP ISSUES");
            sb.AppendLine("-".PadRight(40, '-'));
            foreach (var issue in score.TopIssues.Take(10))
            {
                sb.AppendLine($"* {issue.Description} ({issue.AffectedCount} affected)");
            }
            sb.AppendLine();
        }

        if (config.IncludeRecommendations && summary.TopRecommendations.Count > 0)
        {
            sb.AppendLine("RECOMMENDATIONS");
            sb.AppendLine("-".PadRight(40, '-'));
            var i = 1;
            foreach (var rec in summary.TopRecommendations)
            {
                sb.AppendLine($"{i}. {rec}");
                i++;
            }
        }

        return sb.ToString();
    }

    private string GenerateCsvReport(
        Scoring.DatasetQualityScore score,
        ReportConfig config)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Section,Name,Value");
        sb.AppendLine($"Summary,OverallScore,{score.OverallScore:F2}");
        sb.AppendLine($"Summary,Grade,{score.Grade}");
        sb.AppendLine($"Summary,TotalRecords,{score.TotalRecords}");

        foreach (var (dim, dimScore) in score.DimensionScores)
        {
            sb.AppendLine($"Dimension,{dim},{dimScore:F2}");
        }

        if (config.IncludeFieldDetails)
        {
            foreach (var (field, fieldScore) in score.FieldScores)
            {
                sb.AppendLine($"Field,{field},{fieldScore:F2}");
            }
        }

        foreach (var issue in score.TopIssues.Take(config.MaxIssues))
        {
            sb.AppendLine($"Issue,\"{issue.Description}\",{issue.AffectedCount}");
        }

        return sb.ToString();
    }

    private List<string> GenerateRecommendations(Scoring.DatasetQualityScore score)
    {
        var recommendations = new List<string>();

        // Based on dimension scores
        foreach (var (dim, dimScore) in score.DimensionScores.OrderBy(d => d.Value))
        {
            if (dimScore < 70)
            {
                recommendations.Add(dim switch
                {
                    Scoring.QualityDimension.Completeness =>
                        "Improve data completeness by implementing required field validation",
                    Scoring.QualityDimension.Accuracy =>
                        "Enhance data accuracy through validation rules and source verification",
                    Scoring.QualityDimension.Consistency =>
                        "Standardize data formats and implement consistency checks",
                    Scoring.QualityDimension.Validity =>
                        "Add format validation rules for all data fields",
                    Scoring.QualityDimension.Uniqueness =>
                        "Implement duplicate detection and deduplication processes",
                    Scoring.QualityDimension.Timeliness =>
                        "Establish data refresh schedules and monitor data currency",
                    Scoring.QualityDimension.Integrity =>
                        "Implement referential integrity checks and foreign key validation",
                    _ => $"Review and improve {dim} dimension"
                });
            }
        }

        // Based on issues
        if (score.TopIssues.Count > 10)
        {
            recommendations.Add("Consider automated data cleansing for recurring issues");
        }

        // Based on field scores
        var lowScoreFields = score.FieldScores.Where(f => f.Value < 50).Select(f => f.Key).ToList();
        if (lowScoreFields.Count > 0)
        {
            recommendations.Add($"Focus on improving data quality for: {string.Join(", ", lowScoreFields.Take(5))}");
        }

        return recommendations;
    }
}

#endregion

#region Dashboard Data Strategy

/// <summary>
/// Generates data for quality dashboards.
/// </summary>
public sealed class DashboardDataStrategy : DataQualityStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "dashboard-data";

    /// <inheritdoc/>
    public override string DisplayName => "Dashboard Data Generation";

    /// <inheritdoc/>
    public override DataQualityCategory Category => DataQualityCategory.Reporting;

    /// <inheritdoc/>
    public override DataQualityCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = false,
        SupportsStreaming = false,
        SupportsDistributed = true,
        SupportsIncremental = true,
        MaxThroughput = 1000,
        TypicalLatencyMs = 50
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Generates structured data for quality dashboards including charts, " +
        "KPIs, and visualization-ready metrics.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "reporting", "dashboard", "visualization", "kpi"
    };

    /// <summary>
    /// Generates dashboard data.
    /// </summary>
    public DashboardData GenerateDashboardData(
        Scoring.DatasetQualityScore currentScore,
        List<Scoring.DatasetQualityScore>? historicalScores = null)
    {
        ThrowIfNotInitialized();

        var kpis = new List<KpiData>
        {
            new()
            {
                Name = "Overall Quality Score",
                Value = currentScore.OverallScore,
                Unit = "score",
                Trend = CalculateTrend(historicalScores?.Select(s => s.OverallScore).ToList()),
                Target = 95
            },
            new()
            {
                Name = "Total Records",
                Value = currentScore.TotalRecords,
                Unit = "records"
            },
            new()
            {
                Name = "Issues Count",
                Value = currentScore.TopIssues.Count,
                Unit = "issues",
                IsLowerBetter = true
            }
        };

        var dimensionChart = new ChartData
        {
            Title = "Quality by Dimension",
            Type = "radar",
            Labels = currentScore.DimensionScores.Keys.Select(k => k.ToString()).ToList(),
            Datasets = new List<ChartDataset>
            {
                new()
                {
                    Label = "Current",
                    Data = currentScore.DimensionScores.Values.ToList()
                }
            }
        };

        var gradeDistribution = new ChartData
        {
            Title = "Records by Grade",
            Type = "pie",
            Labels = currentScore.RecordsByGrade.Keys.Select(k => k.ToString()).ToList(),
            Datasets = new List<ChartDataset>
            {
                new()
                {
                    Label = "Count",
                    Data = currentScore.RecordsByGrade.Values.Select(v => (double)v).ToList()
                }
            }
        };

        var trendChart = historicalScores != null && historicalScores.Count > 1
            ? new ChartData
            {
                Title = "Quality Trend",
                Type = "line",
                Labels = historicalScores.Select(s => s.ScoredAt.ToString("MM/dd")).ToList(),
                Datasets = new List<ChartDataset>
                {
                    new()
                    {
                        Label = "Score",
                        Data = historicalScores.Select(s => s.OverallScore).ToList()
                    }
                }
            }
            : null;

        return new DashboardData
        {
            GeneratedAt = DateTime.UtcNow,
            Kpis = kpis,
            Charts = new List<ChartData>
            {
                dimensionChart,
                gradeDistribution
            }.Where(c => c != null).ToList()!,
            TrendChart = trendChart
        };
    }

    private string CalculateTrend(List<double>? values)
    {
        if (values == null || values.Count < 2)
            return "stable";

        var change = values.Last() - values.First();
        if (change > 2) return "up";
        if (change < -2) return "down";
        return "stable";
    }
}

/// <summary>
/// Dashboard data structure.
/// </summary>
public sealed class DashboardData
{
    public DateTime GeneratedAt { get; init; }
    public List<KpiData> Kpis { get; init; } = new();
    public List<ChartData> Charts { get; init; } = new();
    public ChartData? TrendChart { get; init; }
}

/// <summary>
/// KPI data for dashboard.
/// </summary>
public sealed class KpiData
{
    public required string Name { get; init; }
    public double Value { get; init; }
    public string? Unit { get; init; }
    public string Trend { get; init; } = "stable";
    public double? Target { get; init; }
    public bool IsLowerBetter { get; init; }
}

/// <summary>
/// Chart data for visualization.
/// </summary>
public sealed class ChartData
{
    public required string Title { get; init; }
    public required string Type { get; init; }
    public List<string> Labels { get; init; } = new();
    public List<ChartDataset> Datasets { get; init; } = new();
}

/// <summary>
/// Chart dataset.
/// </summary>
public sealed class ChartDataset
{
    public required string Label { get; init; }
    public List<double> Data { get; init; } = new();
}

#endregion
