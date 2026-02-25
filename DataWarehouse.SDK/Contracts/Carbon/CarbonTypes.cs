using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Contracts.Carbon
{
    #region Energy Component & Source Enums

    /// <summary>
    /// Identifies the hardware component consuming energy during an operation.
    /// </summary>
    public enum EnergyComponent
    {
        /// <summary>CPU processing cycles.</summary>
        Cpu,
        /// <summary>GPU compute or rendering cycles.</summary>
        Gpu,
        /// <summary>RAM read/write energy.</summary>
        Memory,
        /// <summary>Disk I/O (SSD, HDD, NVMe).</summary>
        Storage,
        /// <summary>Network interface transmission.</summary>
        Network,
        /// <summary>Aggregate system-level measurement.</summary>
        System,
        /// <summary>Component could not be determined.</summary>
        Unknown
    }

    /// <summary>
    /// Identifies how the energy measurement was obtained.
    /// </summary>
    public enum EnergySource
    {
        /// <summary>Intel Running Average Power Limit (RAPL) hardware counters.</summary>
        Rapl,
        /// <summary>Linux powercap sysfs interface.</summary>
        Powercap,
        /// <summary>IPMI Data Center Manageability Interface (DCMI) power readings.</summary>
        IpmiDcmi,
        /// <summary>Smart Power Distribution Unit (PDU) metering.</summary>
        SmartPdu,
        /// <summary>Cloud provider energy/carbon API (e.g., Azure, GCP).</summary>
        CloudProviderApi,
        /// <summary>Software-based power estimation model.</summary>
        Estimation,
        /// <summary>Manually entered measurement.</summary>
        Manual
    }

    #endregion

    #region Energy Measurement

    /// <summary>
    /// Represents a single energy measurement for a storage operation,
    /// capturing power draw, duration, and the computed energy consumed in watt-hours.
    /// </summary>
    public sealed record EnergyMeasurement
    {
        /// <summary>
        /// Unique identifier for the measured operation.
        /// </summary>
        public required string OperationId { get; init; }

        /// <summary>
        /// UTC timestamp when the measurement was taken.
        /// </summary>
        public required DateTimeOffset Timestamp { get; init; }

        /// <summary>
        /// Instantaneous or average power draw in watts during the operation.
        /// </summary>
        public required double WattsConsumed { get; init; }

        /// <summary>
        /// Duration of the operation in milliseconds.
        /// </summary>
        public required double DurationMs { get; init; }

        /// <summary>
        /// Computed energy consumed in watt-hours: <c>WattsConsumed * DurationMs / 3,600,000</c>.
        /// </summary>
        public double EnergyWh => WattsConsumed * DurationMs / 3_600_000.0;

        /// <summary>
        /// Hardware component that consumed the energy.
        /// </summary>
        public required EnergyComponent Component { get; init; }

        /// <summary>
        /// Method used to obtain the measurement.
        /// </summary>
        public required EnergySource Source { get; init; }

        /// <summary>
        /// Optional tenant identifier for multi-tenant attribution.
        /// </summary>
        public string? TenantId { get; init; }

        /// <summary>
        /// The type of storage operation (e.g., "read", "write", "delete", "list").
        /// </summary>
        public required string OperationType { get; init; }
    }

    #endregion

    #region Carbon Budget

    /// <summary>
    /// Defines the period granularity for carbon budgets.
    /// </summary>
    public enum CarbonBudgetPeriod
    {
        /// <summary>Budget resets every hour.</summary>
        Hourly,
        /// <summary>Budget resets every day.</summary>
        Daily,
        /// <summary>Budget resets every week.</summary>
        Weekly,
        /// <summary>Budget resets every calendar month.</summary>
        Monthly,
        /// <summary>Budget resets every quarter.</summary>
        Quarterly,
        /// <summary>Budget resets every year.</summary>
        Annual
    }

    /// <summary>
    /// Represents a per-tenant carbon emission budget for a defined period.
    /// Tracks allocated budget, current usage, and throttle/exhaust thresholds.
    /// </summary>
    public sealed record CarbonBudget
    {
        /// <summary>
        /// Tenant that owns this carbon budget.
        /// </summary>
        public required string TenantId { get; init; }

        /// <summary>
        /// Granularity of the budget period.
        /// </summary>
        public required CarbonBudgetPeriod BudgetPeriod { get; init; }

        /// <summary>
        /// Total allocated carbon budget in grams of CO2 equivalent.
        /// </summary>
        public required double BudgetGramsCO2e { get; init; }

        /// <summary>
        /// Carbon already consumed in the current period in grams of CO2 equivalent.
        /// </summary>
        public required double UsedGramsCO2e { get; init; }

        /// <summary>
        /// Remaining carbon budget: <c>BudgetGramsCO2e - UsedGramsCO2e</c>.
        /// Returns zero if usage exceeds budget.
        /// </summary>
        public double RemainingGramsCO2e => Math.Max(0, BudgetGramsCO2e - UsedGramsCO2e);

        /// <summary>
        /// Percentage of budget usage at which soft throttling begins. Default 80%.
        /// </summary>
        public double ThrottleThresholdPercent { get; init; } = 80.0;

        /// <summary>
        /// Percentage of budget usage at which operations are blocked. Default 100%.
        /// </summary>
        public double HardLimitPercent { get; init; } = 100.0;

        /// <summary>
        /// Start of the current budget period (UTC).
        /// </summary>
        public required DateTimeOffset PeriodStart { get; init; }

        /// <summary>
        /// End of the current budget period (UTC).
        /// </summary>
        public required DateTimeOffset PeriodEnd { get; init; }

        /// <summary>
        /// Whether soft throttling is active (usage exceeds <see cref="ThrottleThresholdPercent"/>).
        /// </summary>
        public bool IsThrottled => BudgetGramsCO2e > 0 && (UsedGramsCO2e / BudgetGramsCO2e * 100.0) >= ThrottleThresholdPercent;

        /// <summary>
        /// Whether the budget is fully exhausted (usage exceeds <see cref="HardLimitPercent"/>).
        /// </summary>
        public bool IsExhausted => BudgetGramsCO2e > 0 && (UsedGramsCO2e / BudgetGramsCO2e * 100.0) >= HardLimitPercent;
    }

    #endregion

    #region Grid Carbon Data

    /// <summary>
    /// Identifies the external data source providing grid carbon intensity data.
    /// </summary>
    public enum GridDataSource
    {
        /// <summary>WattTime API (US and international grids).</summary>
        WattTime,
        /// <summary>Electricity Maps API (global coverage).</summary>
        ElectricityMaps,
        /// <summary>UK Carbon Intensity API (National Grid ESO).</summary>
        CarbonIntensityUkApi,
        /// <summary>US Energy Information Administration Open Data.</summary>
        EiaOpenData,
        /// <summary>Software-based estimation when no API is available.</summary>
        Estimation
    }

    /// <summary>
    /// Represents real-time or forecasted carbon intensity data for an electricity grid region.
    /// Used by green placement strategies to route data to lower-carbon regions.
    /// </summary>
    public sealed record GridCarbonData
    {
        /// <summary>
        /// Grid region identifier (e.g., "US-CAL-CISO", "GB", "DE").
        /// </summary>
        public required string Region { get; init; }

        /// <summary>
        /// UTC timestamp of the measurement or forecast.
        /// </summary>
        public required DateTimeOffset Timestamp { get; init; }

        /// <summary>
        /// Carbon intensity in grams of CO2 equivalent per kilowatt-hour.
        /// </summary>
        public required double CarbonIntensityGCO2ePerKwh { get; init; }

        /// <summary>
        /// Percentage of grid generation from renewable sources (0-100).
        /// </summary>
        public required double RenewablePercentage { get; init; }

        /// <summary>
        /// Percentage of grid generation from fossil fuel sources: <c>100 - RenewablePercentage</c>.
        /// </summary>
        public double FossilPercentage => 100.0 - RenewablePercentage;

        /// <summary>
        /// External data source that provided this reading.
        /// </summary>
        public required GridDataSource Source { get; init; }

        /// <summary>
        /// Number of hours into the future this forecast covers (0 for real-time).
        /// </summary>
        public required int ForecastHours { get; init; }

        /// <summary>
        /// Marginal carbon intensity (gCO2e/kWh) if available -- represents the carbon cost
        /// of producing one additional kWh on the grid.
        /// </summary>
        public double? MarginalIntensity { get; init; }

        /// <summary>
        /// Average carbon intensity (gCO2e/kWh) over the forecast period if available.
        /// </summary>
        public double? AverageIntensity { get; init; }
    }

    #endregion

    #region Green Score

    /// <summary>
    /// Composite sustainability score for a storage backend, combining renewable energy percentage,
    /// carbon intensity, power usage effectiveness (PUE), and water usage effectiveness (WUE).
    /// </summary>
    public sealed record GreenScore
    {
        /// <summary>
        /// Unique identifier of the storage backend.
        /// </summary>
        public required string BackendId { get; init; }

        /// <summary>
        /// Geographic region where the backend operates.
        /// </summary>
        public required string Region { get; init; }

        /// <summary>
        /// Percentage of energy sourced from renewables (0-100).
        /// </summary>
        public required double RenewablePercentage { get; init; }

        /// <summary>
        /// Grid carbon intensity in grams of CO2 equivalent per kilowatt-hour.
        /// </summary>
        public required double CarbonIntensityGCO2ePerKwh { get; init; }

        /// <summary>
        /// Power Usage Effectiveness ratio (1.0 = ideal, typical data center ~1.2-1.6).
        /// </summary>
        public required double PowerUsageEffectiveness { get; init; }

        /// <summary>
        /// Water Usage Effectiveness in liters per kWh, if available.
        /// </summary>
        public double? WaterUsageEffectiveness { get; init; }

        /// <summary>
        /// Composite green score (0-100). Higher is more sustainable.
        /// Weighted combination of renewable %, carbon intensity, PUE, and WUE.
        /// </summary>
        public required double Score { get; init; }

        /// <summary>
        /// UTC timestamp when this score was last computed or refreshed.
        /// </summary>
        public required DateTimeOffset LastUpdated { get; init; }
    }

    #endregion

    #region GHG Reporting

    /// <summary>
    /// GHG Protocol scope categories for emissions classification.
    /// </summary>
    public enum GhgScopeCategory
    {
        /// <summary>Scope 1: Direct emissions from owned or controlled sources.</summary>
        Scope1_DirectEmissions,
        /// <summary>Scope 2: Indirect emissions from purchased electricity, steam, heating, and cooling.</summary>
        Scope2_PurchasedElectricity,
        /// <summary>Scope 3: All other indirect emissions in the value chain.</summary>
        Scope3_ValueChain
    }

    /// <summary>
    /// Quality level of the emissions data, following GHG Protocol data quality guidance.
    /// </summary>
    public enum DataQualityLevel
    {
        /// <summary>Direct hardware measurement (RAPL, PDU, smart meter).</summary>
        Measured,
        /// <summary>Calculated from activity data and published emission factors.</summary>
        Calculated,
        /// <summary>Estimated using proxy data or models.</summary>
        Estimated,
        /// <summary>Default emission factor applied when no specific data is available.</summary>
        Default
    }

    /// <summary>
    /// A single line item in a GHG Protocol report, attributing emissions to a scope,
    /// category, region, and time period with data quality metadata.
    /// </summary>
    public sealed record GhgReportEntry
    {
        /// <summary>
        /// GHG Protocol scope (1, 2, or 3).
        /// </summary>
        public required GhgScopeCategory Scope { get; init; }

        /// <summary>
        /// Emission category within the scope (e.g., "data-storage", "network-transfer", "cooling").
        /// </summary>
        public required string Category { get; init; }

        /// <summary>
        /// Total emissions for this entry in grams of CO2 equivalent.
        /// </summary>
        public required double EmissionsGramsCO2e { get; init; }

        /// <summary>
        /// Total energy consumed for this entry in watt-hours.
        /// </summary>
        public required double EnergyConsumedWh { get; init; }

        /// <summary>
        /// Start of the reporting period (UTC).
        /// </summary>
        public required DateTimeOffset PeriodStart { get; init; }

        /// <summary>
        /// End of the reporting period (UTC).
        /// </summary>
        public required DateTimeOffset PeriodEnd { get; init; }

        /// <summary>
        /// Geographic region for this emission entry.
        /// </summary>
        public required string Region { get; init; }

        /// <summary>
        /// Description of the data source (e.g., "RAPL hardware counters", "Azure Carbon API").
        /// </summary>
        public required string Source { get; init; }

        /// <summary>
        /// Quality level of this emissions data.
        /// </summary>
        public required DataQualityLevel DataQuality { get; init; }
    }

    #endregion

    #region Carbon Placement Decision

    /// <summary>
    /// Result of a green placement evaluation, recommending the most sustainable
    /// storage backend along with estimated carbon and energy costs.
    /// </summary>
    public sealed record CarbonPlacementDecision
    {
        /// <summary>
        /// Identifier of the recommended storage backend.
        /// </summary>
        public required string PreferredBackendId { get; init; }

        /// <summary>
        /// Green sustainability score of the recommended backend.
        /// </summary>
        public required GreenScore GreenScore { get; init; }

        /// <summary>
        /// Human-readable explanation of why this backend was selected.
        /// </summary>
        public required string Reason { get; init; }

        /// <summary>
        /// Alternative backend identifiers ranked by green score, excluding the preferred backend.
        /// </summary>
        public required IReadOnlyList<string> AlternativeBackendIds { get; init; }

        /// <summary>
        /// Estimated carbon emissions in grams of CO2 equivalent for the operation on this backend.
        /// </summary>
        public required double EstimatedCarbonGramsCO2e { get; init; }

        /// <summary>
        /// Estimated energy consumption in watt-hours for the operation on this backend.
        /// </summary>
        public required double EstimatedEnergyWh { get; init; }
    }

    #endregion
}
