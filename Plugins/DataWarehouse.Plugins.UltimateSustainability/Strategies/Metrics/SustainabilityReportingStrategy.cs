using System.Text;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.Metrics;

/// <summary>
/// Generates comprehensive sustainability reports including energy usage,
/// carbon emissions, cost savings, and environmental impact metrics.
/// </summary>
public sealed class SustainabilityReportingStrategy : SustainabilityStrategyBase
{
    private readonly List<ReportRecord> _reports = new();
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "sustainability-reporting";
    /// <inheritdoc/>
    public override string DisplayName => "Sustainability Reporting";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.Metrics;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.Reporting | SustainabilityCapabilities.CarbonCalculation;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Generates comprehensive sustainability reports with energy, carbon, cost, and environmental metrics.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "reporting", "sustainability", "metrics", "esg", "compliance" };

    /// <summary>Report output format.</summary>
    public ReportFormat Format { get; set; } = ReportFormat.Json;

    /// <summary>Organization name for reports.</summary>
    public string OrganizationName { get; set; } = "DataWarehouse";

    /// <summary>Generates a sustainability report.</summary>
    public SustainabilityReport GenerateReport(
        TimeSpan period,
        double totalEnergyWh,
        double totalEmissionsGrams,
        double energySavedWh,
        double carbonAvoidedGrams,
        double costSavingsUsd)
    {
        var report = new SustainabilityReport
        {
            ReportId = Guid.NewGuid().ToString("N"),
            GeneratedAt = DateTimeOffset.UtcNow,
            Organization = OrganizationName,
            PeriodStart = DateTimeOffset.UtcNow - period,
            PeriodEnd = DateTimeOffset.UtcNow,

            TotalEnergyConsumedWh = totalEnergyWh,
            TotalEnergyConsumedKwh = totalEnergyWh / 1000.0,
            TotalEmissionsGrams = totalEmissionsGrams,
            TotalEmissionsKgCO2e = totalEmissionsGrams / 1000.0,
            TotalEmissionsTonsCO2e = totalEmissionsGrams / 1_000_000.0,

            EnergySavedWh = energySavedWh,
            EnergySavedKwh = energySavedWh / 1000.0,
            CarbonAvoidedGrams = carbonAvoidedGrams,
            CarbonAvoidedKgCO2e = carbonAvoidedGrams / 1000.0,
            CostSavingsUsd = costSavingsUsd,

            EfficiencyRatio = totalEnergyWh > 0 ? energySavedWh / totalEnergyWh * 100 : 0,
            CarbonReductionPercent = totalEmissionsGrams > 0 ? carbonAvoidedGrams / (totalEmissionsGrams + carbonAvoidedGrams) * 100 : 0,

            EquivalentTreesPlanted = carbonAvoidedGrams / 21000.0, // ~21kg CO2 per tree per year
            EquivalentMilesDriven = totalEmissionsGrams / 404.0, // ~404g CO2 per mile
            EquivalentHomesEnergy = totalEnergyWh / 10500000.0 // ~10,500 kWh per US home per year
        };

        lock (_lock)
        {
            _reports.Add(new ReportRecord { Report = report, GeneratedAt = DateTimeOffset.UtcNow });
            if (_reports.Count > 100) _reports.RemoveAt(0);
        }

        RecordOptimizationAction();
        return report;
    }

    /// <summary>Exports report in specified format.</summary>
    public string ExportReport(SustainabilityReport report, ReportFormat? format = null)
    {
        var fmt = format ?? Format;
        return fmt switch
        {
            ReportFormat.Json => System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
            ReportFormat.Csv => ExportToCsv(report),
            ReportFormat.Markdown => ExportToMarkdown(report),
            _ => System.Text.Json.JsonSerializer.Serialize(report)
        };
    }

    /// <summary>Gets report history.</summary>
    public IReadOnlyList<SustainabilityReport> GetReportHistory()
    {
        lock (_lock)
        {
            return _reports.Select(r => r.Report).ToList().AsReadOnly();
        }
    }

    private static string ExportToCsv(SustainabilityReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Metric,Value,Unit");
        sb.AppendLine($"Total Energy,{report.TotalEnergyConsumedKwh:F2},kWh");
        sb.AppendLine($"Total Emissions,{report.TotalEmissionsKgCO2e:F2},kgCO2e");
        sb.AppendLine($"Energy Saved,{report.EnergySavedKwh:F2},kWh");
        sb.AppendLine($"Carbon Avoided,{report.CarbonAvoidedKgCO2e:F2},kgCO2e");
        sb.AppendLine($"Cost Savings,{report.CostSavingsUsd:F2},USD");
        return sb.ToString();
    }

    private static string ExportToMarkdown(SustainabilityReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Sustainability Report - {report.Organization}");
        sb.AppendLine();
        sb.AppendLine($"**Report ID:** {report.ReportId}");
        sb.AppendLine($"**Period:** {report.PeriodStart:yyyy-MM-dd} to {report.PeriodEnd:yyyy-MM-dd}");
        sb.AppendLine();
        sb.AppendLine("## Energy Metrics");
        sb.AppendLine($"- Total Energy Consumed: {report.TotalEnergyConsumedKwh:F2} kWh");
        sb.AppendLine($"- Energy Saved: {report.EnergySavedKwh:F2} kWh ({report.EfficiencyRatio:F1}% efficiency)");
        sb.AppendLine();
        sb.AppendLine("## Carbon Metrics");
        sb.AppendLine($"- Total Emissions: {report.TotalEmissionsKgCO2e:F2} kgCO2e");
        sb.AppendLine($"- Carbon Avoided: {report.CarbonAvoidedKgCO2e:F2} kgCO2e");
        sb.AppendLine($"- Carbon Reduction: {report.CarbonReductionPercent:F1}%");
        sb.AppendLine();
        sb.AppendLine("## Financial Impact");
        sb.AppendLine($"- Cost Savings: ${report.CostSavingsUsd:F2}");
        sb.AppendLine();
        sb.AppendLine("## Environmental Equivalents");
        sb.AppendLine($"- Equivalent Trees Planted: {report.EquivalentTreesPlanted:F1}");
        sb.AppendLine($"- Equivalent Miles Driven: {report.EquivalentMilesDriven:F0}");
        return sb.ToString();
    }
}

/// <summary>Report format options.</summary>
public enum ReportFormat { Json, Csv, Markdown, Xml }

/// <summary>Sustainability report.</summary>
public sealed record SustainabilityReport
{
    public required string ReportId { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required string Organization { get; init; }
    public required DateTimeOffset PeriodStart { get; init; }
    public required DateTimeOffset PeriodEnd { get; init; }

    public required double TotalEnergyConsumedWh { get; init; }
    public required double TotalEnergyConsumedKwh { get; init; }
    public required double TotalEmissionsGrams { get; init; }
    public required double TotalEmissionsKgCO2e { get; init; }
    public required double TotalEmissionsTonsCO2e { get; init; }

    public required double EnergySavedWh { get; init; }
    public required double EnergySavedKwh { get; init; }
    public required double CarbonAvoidedGrams { get; init; }
    public required double CarbonAvoidedKgCO2e { get; init; }
    public required double CostSavingsUsd { get; init; }

    public required double EfficiencyRatio { get; init; }
    public required double CarbonReductionPercent { get; init; }

    public required double EquivalentTreesPlanted { get; init; }
    public required double EquivalentMilesDriven { get; init; }
    public required double EquivalentHomesEnergy { get; init; }
}

internal sealed record ReportRecord
{
    public required SustainabilityReport Report { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
}
