using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.ModuleManagement;

/// <summary>
/// Risk level classification for a module addition option.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Module addition risk classification (OMOD-06)")]
public enum RiskLevel : byte
{
    /// <summary>No risk -- operation is purely metadata with no data movement.</summary>
    None = 0,

    /// <summary>Low risk -- WAL-journaled or checkpointed; crash-safe.</summary>
    Low = 1,

    /// <summary>Medium risk -- involves data movement or temporary dual state.</summary>
    Medium = 2,

    /// <summary>High risk -- significant structural change with extended exposure window.</summary>
    High = 3,
}

/// <summary>
/// Expected downtime classification for a module addition option.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Module addition downtime estimate (OMOD-06)")]
public enum DowntimeEstimate : byte
{
    /// <summary>No downtime -- fully online operation.</summary>
    Zero = 0,

    /// <summary>Sub-second pause for atomic metadata swap.</summary>
    SubSecond = 1,

    /// <summary>Seconds of reduced availability during transition.</summary>
    Seconds = 2,

    /// <summary>Minutes of reduced availability or read-only mode.</summary>
    Minutes = 3,

    /// <summary>Minutes to hours depending on data volume.</summary>
    MinutesToHours = 4,
}

/// <summary>
/// Describes a single module addition option with its feasibility, risk, downtime, and
/// performance characteristics. Used by <see cref="OptionComparison"/> to present all
/// four options to the caller before committing to one.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Module addition option descriptor (OMOD-06)")]
public readonly record struct AdditionOption
{
    /// <summary>Option number (1-4) identifying the strategy.</summary>
    public int OptionNumber { get; init; }

    /// <summary>Human-readable option name.</summary>
    public string Name { get; init; }

    /// <summary>1-2 sentence explanation of this option.</summary>
    public string Description { get; init; }

    /// <summary>True if this option is feasible for the current VDE state.</summary>
    public bool Available { get; init; }

    /// <summary>Reason this option is not available; null when available.</summary>
    public string? UnavailableReason { get; init; }

    /// <summary>Risk assessment for this option.</summary>
    public RiskLevel Risk { get; init; }

    /// <summary>Expected downtime for this option.</summary>
    public DowntimeEstimate Downtime { get; init; }

    /// <summary>Description of I/O or CPU overhead during the operation.</summary>
    public string PerformanceImpact { get; init; }

    /// <summary>Rough time estimate for the operation to complete.</summary>
    public TimeSpan EstimatedDuration { get; init; }

    /// <summary>True if this is the recommended option for the current VDE state.</summary>
    public bool IsRecommended { get; init; }
}

/// <summary>
/// Comparison of all four module addition options with formatted output for CLI/GUI display.
/// Satisfies OMOD-06: user sees all options with performance/downtime/risk before committing.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Module addition option comparison (OMOD-06)")]
public sealed class OptionComparison
{
    /// <summary>All four addition options with their current feasibility and metrics.</summary>
    public IReadOnlyList<AdditionOption> Options { get; }

    /// <summary>The recommended option, or null if no options are available.</summary>
    public AdditionOption? Recommended { get; }

    /// <summary>
    /// Creates an option comparison from a list of evaluated options.
    /// </summary>
    /// <param name="options">The four evaluated options.</param>
    public OptionComparison(IReadOnlyList<AdditionOption> options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Recommended = null;
        for (int i = 0; i < options.Count; i++)
        {
            if (options[i].IsRecommended)
            {
                Recommended = options[i];
                break;
            }
        }
    }

    /// <summary>
    /// Returns a formatted ASCII table comparing all options side by side.
    /// </summary>
    public string ToComparisonTable()
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("| # | Option                      | Available | Downtime       | Risk   | Est. Duration | Recommended |");
        sb.AppendLine("|---|---------------------------- |-----------|----------------|--------|---------------|-------------|");

        foreach (var opt in Options)
        {
            string available = opt.Available ? "Yes" : "No";
            string downtime = FormatDowntime(opt.Downtime);
            string risk = opt.Risk.ToString();
            string duration = FormatDuration(opt.EstimatedDuration);
            string recommended = opt.IsRecommended ? "*" : "";

            sb.AppendLine($"| {opt.OptionNumber} | {opt.Name,-27} | {available,-9} | {downtime,-14} | {risk,-6} | {duration,-13} | {recommended,-11} |");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Returns a detailed multi-paragraph report explaining each option.
    /// </summary>
    public string ToDetailedReport()
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine("=== Module Addition Options - Detailed Report ===");
        sb.AppendLine();

        foreach (var opt in Options)
        {
            sb.AppendLine($"--- Option {opt.OptionNumber}: {opt.Name} ---");
            sb.AppendLine($"  Description:      {opt.Description}");
            sb.AppendLine($"  Available:        {(opt.Available ? "Yes" : "No")}");
            if (!opt.Available && opt.UnavailableReason is not null)
                sb.AppendLine($"  Unavailable:      {opt.UnavailableReason}");
            sb.AppendLine($"  Risk:             {opt.Risk}");
            sb.AppendLine($"  Downtime:         {FormatDowntime(opt.Downtime)}");
            sb.AppendLine($"  Performance:      {opt.PerformanceImpact}");
            sb.AppendLine($"  Est. Duration:    {FormatDuration(opt.EstimatedDuration)}");
            if (opt.IsRecommended)
                sb.AppendLine($"  ** RECOMMENDED **");
            sb.AppendLine();
        }

        if (Recommended.HasValue)
        {
            sb.AppendLine($"Recommendation: Option {Recommended.Value.OptionNumber} ({Recommended.Value.Name})");
        }
        else
        {
            sb.AppendLine("Recommendation: No options currently available.");
        }

        return sb.ToString();
    }

    private static string FormatDowntime(DowntimeEstimate estimate) => estimate switch
    {
        DowntimeEstimate.Zero => "Zero",
        DowntimeEstimate.SubSecond => "Sub-second",
        DowntimeEstimate.Seconds => "Seconds",
        DowntimeEstimate.Minutes => "Minutes",
        DowntimeEstimate.MinutesToHours => "Min-Hours",
        _ => estimate.ToString(),
    };

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 1)
            return "< 1s";
        if (duration.TotalMinutes < 1)
            return $"~{(int)duration.TotalSeconds}s";
        if (duration.TotalHours < 1)
            return $"~{(int)duration.TotalMinutes} min";
        return $"~{duration.TotalHours:F1} hr";
    }
}
