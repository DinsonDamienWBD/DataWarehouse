using DataWarehouse.SDK.Contracts;

// FUTURE: Carbon-aware scheduling -- interfaces preserved for carbon-aware storage scheduling per AD-06.
// These types have zero current implementations but define contracts for future
// carbon-aware storage plugins. Do NOT delete during dead code cleanup.

namespace DataWarehouse.SDK.Sustainability;

/// <summary>
/// Carbon intensity level classification.
/// </summary>
public enum CarbonIntensityLevel
{
    /// <summary>Extremely low carbon intensity (renewable-heavy grid).</summary>
    VeryLow,

    /// <summary>Low carbon intensity.</summary>
    Low,

    /// <summary>Medium carbon intensity.</summary>
    Medium,

    /// <summary>High carbon intensity.</summary>
    High,

    /// <summary>Very high carbon intensity (fossil-heavy grid).</summary>
    VeryHigh
}

/// <summary>
/// Carbon intensity data for a specific region at a point in time.
/// </summary>
public record CarbonIntensityData(
    /// <summary>Region identifier (e.g., "us-west-2", "eu-central-1").</summary>
    string RegionId,

    /// <summary>Carbon intensity in grams of CO2 per kilowatt-hour.</summary>
    double GramsCO2PerKwh,

    /// <summary>Classified carbon intensity level.</summary>
    CarbonIntensityLevel Level,

    /// <summary>Percentage of renewable energy in the grid (0-100).</summary>
    double RenewablePercentage,

    /// <summary>Timestamp when the intensity was measured.</summary>
    DateTimeOffset MeasuredAt,

    /// <summary>Optional forecast validity period (null for current data).</summary>
    DateTimeOffset? ForecastedUntil
);

/// <summary>
/// Carbon intensity API provider interface.
/// Integrates with external APIs (e.g., WattTime, ElectricityMaps, Carbon Aware SDK).
/// </summary>
public interface ICarbonIntensityProvider : IPlugin
{
    /// <summary>
    /// Gets current carbon intensity for a specific region.
    /// </summary>
    /// <param name="regionId">Region identifier (e.g., "us-west-2").</param>
    /// <returns>Current carbon intensity data.</returns>
    Task<CarbonIntensityData> GetCurrentIntensityAsync(string regionId);

    /// <summary>
    /// Gets forecasted carbon intensity for planning operations.
    /// </summary>
    /// <param name="regionId">Region identifier.</param>
    /// <param name="hoursAhead">Forecast horizon in hours (default: 24).</param>
    /// <returns>List of forecasted intensity data points.</returns>
    Task<IReadOnlyList<CarbonIntensityData>> GetForecastAsync(string regionId, int hoursAhead = 24);

    /// <summary>
    /// Gets all available regions supported by this provider.
    /// </summary>
    /// <returns>List of region identifiers.</returns>
    Task<IReadOnlyList<string>> GetAvailableRegionsAsync();

    /// <summary>
    /// Finds the region with the lowest current carbon intensity.
    /// </summary>
    /// <param name="regionIds">Candidate regions to compare.</param>
    /// <returns>Region ID with the lowest carbon intensity.</returns>
    Task<string> FindLowestCarbonRegionAsync(string[] regionIds);
}

/// <summary>
/// Carbon-aware scheduling interface for deferring operations to low-carbon periods.
/// </summary>
public interface ICarbonAwareScheduler : IPlugin
{
    /// <summary>
    /// Schedules an operation to execute during the lowest carbon intensity period.
    /// </summary>
    /// <param name="operationId">Unique operation identifier.</param>
    /// <param name="operation">The async operation to execute.</param>
    /// <param name="options">Scheduling constraints and preferences.</param>
    /// <returns>Scheduled operation details.</returns>
    Task<ScheduledOperation> ScheduleForLowCarbonAsync(
        string operationId,
        Func<CancellationToken, Task> operation,
        SchedulingOptions options);

    /// <summary>
    /// Calculates the optimal execution time within a time window.
    /// </summary>
    /// <param name="regionId">Target region.</param>
    /// <param name="windowDuration">Duration of the time window to search.</param>
    /// <param name="windowStart">Window start time (default: now).</param>
    /// <returns>Optimal execution time within the window.</returns>
    Task<DateTimeOffset> GetOptimalExecutionTimeAsync(
        string regionId,
        TimeSpan windowDuration,
        DateTimeOffset? windowStart = null);

    /// <summary>
    /// Checks if current carbon intensity meets the execution threshold.
    /// </summary>
    /// <param name="regionId">Region to check.</param>
    /// <param name="threshold">Carbon threshold criteria.</param>
    /// <returns>True if conditions are favorable for execution.</returns>
    Task<bool> ShouldExecuteNowAsync(string regionId, CarbonThreshold threshold);
}

/// <summary>
/// Scheduling options for carbon-aware operations.
/// </summary>
public record SchedulingOptions(
    /// <summary>Region where the operation will run.</summary>
    string RegionId,

    /// <summary>Maximum time to wait for low carbon conditions.</summary>
    TimeSpan MaxDelay,

    /// <summary>Carbon threshold criteria for execution.</summary>
    CarbonThreshold Threshold,

    /// <summary>Whether to consider multiple regions for execution (default: false).</summary>
    bool AllowMultiRegion = false
);

/// <summary>
/// Scheduled operation details.
/// </summary>
public record ScheduledOperation(
    /// <summary>Unique operation identifier.</summary>
    string OperationId,

    /// <summary>Scheduled execution time.</summary>
    DateTimeOffset ScheduledFor,

    /// <summary>Target region for execution.</summary>
    string TargetRegion,

    /// <summary>Estimated carbon intensity at execution time.</summary>
    CarbonIntensityData EstimatedIntensity,

    /// <summary>Current operation status.</summary>
    OperationStatus Status
);

/// <summary>
/// Operation execution status.
/// </summary>
public enum OperationStatus
{
    /// <summary>Waiting for scheduled time.</summary>
    Scheduled,

    /// <summary>Currently executing.</summary>
    Running,

    /// <summary>Successfully completed.</summary>
    Completed,

    /// <summary>Cancelled before execution.</summary>
    Cancelled,

    /// <summary>Failed during execution.</summary>
    Failed
}

/// <summary>
/// Carbon intensity threshold for operation execution.
/// </summary>
public record CarbonThreshold(
    /// <summary>Maximum acceptable grams CO2 per kWh.</summary>
    double MaxGramsCO2PerKwh,

    /// <summary>Maximum acceptable intensity level.</summary>
    CarbonIntensityLevel MaxLevel
);

/// <summary>
/// Green region selection interface for choosing low-carbon infrastructure.
/// </summary>
public interface IGreenRegionSelector : IPlugin
{
    /// <summary>
    /// Selects the greenest region from available options.
    /// </summary>
    /// <param name="availableRegions">Candidate regions.</param>
    /// <param name="criteria">Selection criteria (latency, availability, etc.).</param>
    /// <returns>Selected region ID.</returns>
    Task<string> SelectGreenestRegionAsync(string[] availableRegions, RegionSelectionCriteria criteria);

    /// <summary>
    /// Ranks regions by carbon footprint with scoring.
    /// </summary>
    /// <param name="regionIds">Regions to rank.</param>
    /// <returns>Ranked list of regions with scores.</returns>
    Task<IReadOnlyList<RankedRegion>> RankRegionsByCarbonAsync(string[] regionIds);
}

/// <summary>
/// Criteria for region selection.
/// </summary>
public record RegionSelectionCriteria(
    /// <summary>Maximum acceptable latency in milliseconds (null = no constraint).</summary>
    double? MaxLatencyMs = null,

    /// <summary>Minimum availability percentage (null = no constraint).</summary>
    double? MinAvailability = null,

    /// <summary>Whether to prefer regions with higher renewable percentage (default: true).</summary>
    bool PreferRenewable = true
);

/// <summary>
/// Ranked region with carbon score.
/// </summary>
public record RankedRegion(
    /// <summary>Region identifier.</summary>
    string RegionId,

    /// <summary>Rank (1 = best, higher = worse).</summary>
    int Rank,

    /// <summary>Carbon intensity data.</summary>
    CarbonIntensityData Intensity,

    /// <summary>Composite score (higher = better).</summary>
    double Score
);

/// <summary>
/// Carbon reporting interface for tracking emissions.
/// </summary>
public interface ICarbonReporter : IPlugin
{
    /// <summary>
    /// Records carbon usage for an operation.
    /// </summary>
    /// <param name="usage">Usage record to store.</param>
    Task RecordUsageAsync(CarbonUsageRecord usage);

    /// <summary>
    /// Generates a carbon report for a time period.
    /// </summary>
    /// <param name="start">Report period start.</param>
    /// <param name="end">Report period end.</param>
    /// <param name="granularity">Report granularity (hourly, daily, etc.).</param>
    /// <returns>Aggregated carbon report.</returns>
    Task<CarbonReport> GetReportAsync(DateTimeOffset start, DateTimeOffset end, ReportGranularity granularity);

    /// <summary>
    /// Gets total carbon footprint since a specific date.
    /// </summary>
    /// <param name="since">Start date (null = all time).</param>
    /// <returns>Total carbon emissions in grams.</returns>
    Task<double> GetTotalCarbonFootprintAsync(DateTimeOffset? since = null);
}

/// <summary>
/// Carbon usage record for an operation.
/// </summary>
public record CarbonUsageRecord(
    /// <summary>Operation identifier.</summary>
    string OperationId,

    /// <summary>Region where operation ran.</summary>
    string RegionId,

    /// <summary>Energy consumed in kilowatt-hours.</summary>
    double EnergyKwh,

    /// <summary>Carbon emissions in grams.</summary>
    double CarbonGrams,

    /// <summary>Timestamp of the operation.</summary>
    DateTimeOffset Timestamp,

    /// <summary>Type of operation (e.g., "backup", "replication", "query").</summary>
    string OperationType
);

/// <summary>
/// Aggregated carbon report.
/// </summary>
public record CarbonReport(
    /// <summary>Report period start.</summary>
    DateTimeOffset Start,

    /// <summary>Report period end.</summary>
    DateTimeOffset End,

    /// <summary>Total carbon emissions in grams.</summary>
    double TotalCarbonGrams,

    /// <summary>Total energy consumed in kWh.</summary>
    double TotalEnergyKwh,

    /// <summary>Detailed entries by period.</summary>
    IReadOnlyList<CarbonReportEntry> Entries
);

/// <summary>
/// Carbon report entry for a specific period.
/// </summary>
public record CarbonReportEntry(
    /// <summary>Period timestamp.</summary>
    DateTimeOffset Period,

    /// <summary>Region identifier.</summary>
    string RegionId,

    /// <summary>Carbon emissions for this period in grams.</summary>
    double CarbonGrams,

    /// <summary>Energy consumed for this period in kWh.</summary>
    double EnergyKwh,

    /// <summary>Average renewable percentage for this period.</summary>
    double RenewablePercentage
);

/// <summary>
/// Report granularity options.
/// </summary>
public enum ReportGranularity
{
    /// <summary>Hourly aggregation.</summary>
    Hourly,

    /// <summary>Daily aggregation.</summary>
    Daily,

    /// <summary>Weekly aggregation.</summary>
    Weekly,

    /// <summary>Monthly aggregation.</summary>
    Monthly
}

/// <summary>
/// Carbon offset provider interface for purchasing emissions offsets.
/// </summary>
public interface ICarbonOffsetProvider : IPlugin
{
    /// <summary>
    /// Purchases carbon offsets for emissions.
    /// </summary>
    /// <param name="carbonGrams">Amount of carbon to offset in grams.</param>
    /// <param name="options">Offset purchase options.</param>
    /// <returns>Purchase confirmation details.</returns>
    Task<OffsetPurchase> PurchaseOffsetsAsync(double carbonGrams, OffsetOptions options);

    /// <summary>
    /// Gets available carbon offset projects.
    /// </summary>
    /// <returns>List of offset projects.</returns>
    Task<IReadOnlyList<OffsetProject>> GetAvailableProjectsAsync();

    /// <summary>
    /// Gets historical offset purchase records.
    /// </summary>
    /// <returns>List of past purchases.</returns>
    Task<IReadOnlyList<OffsetPurchase>> GetPurchaseHistoryAsync();
}

/// <summary>
/// Options for purchasing carbon offsets.
/// </summary>
public record OffsetOptions(
    /// <summary>Preferred project ID (null = automatic selection).</summary>
    string? PreferredProjectId = null,

    /// <summary>Required certification standard (null = any).</summary>
    OffsetStandard? RequiredStandard = null
);

/// <summary>
/// Carbon offset certification standards.
/// </summary>
public enum OffsetStandard
{
    /// <summary>Verified Carbon Standard.</summary>
    VCS,

    /// <summary>Gold Standard.</summary>
    GoldStandard,

    /// <summary>American Carbon Registry.</summary>
    ACR,

    /// <summary>Climate Action Reserve.</summary>
    CAR
}

/// <summary>
/// Carbon offset project details.
/// </summary>
public record OffsetProject(
    /// <summary>Unique project identifier.</summary>
    string ProjectId,

    /// <summary>Project name.</summary>
    string Name,

    /// <summary>Project type (e.g., "Reforestation", "Renewable Energy").</summary>
    string Type,

    /// <summary>Certification standard.</summary>
    OffsetStandard Standard,

    /// <summary>Price per metric ton of CO2.</summary>
    double PricePerTonCO2,

    /// <summary>Country where project is located.</summary>
    string Country
);

/// <summary>
/// Carbon offset purchase record.
/// </summary>
public record OffsetPurchase(
    /// <summary>Unique purchase identifier.</summary>
    string PurchaseId,

    /// <summary>Project that was purchased from.</summary>
    string ProjectId,

    /// <summary>Amount of carbon offset in grams.</summary>
    double CarbonGramsOffset,

    /// <summary>Purchase cost.</summary>
    double Cost,

    /// <summary>Purchase timestamp.</summary>
    DateTimeOffset PurchasedAt,

    /// <summary>URL to offset certificate.</summary>
    string CertificateUrl
);
