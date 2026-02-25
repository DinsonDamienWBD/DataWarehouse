using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Carbon
{
    #region Carbon Summary

    /// <summary>
    /// Aggregated carbon and energy summary for a tenant or the entire system over a period.
    /// Provides high-level metrics for dashboards and executive reporting.
    /// </summary>
    public sealed record CarbonSummary
    {
        /// <summary>
        /// Total carbon emissions in grams of CO2 equivalent for the period.
        /// </summary>
        public required double TotalEmissionsGramsCO2e { get; init; }

        /// <summary>
        /// Total energy consumed in watt-hours for the period.
        /// </summary>
        public required double TotalEnergyWh { get; init; }

        /// <summary>
        /// Weighted average renewable energy percentage across all operations.
        /// </summary>
        public required double RenewablePercentage { get; init; }

        /// <summary>
        /// Weighted average carbon intensity in gCO2e/kWh across all operations.
        /// </summary>
        public required double AvgCarbonIntensity { get; init; }

        /// <summary>
        /// Region with the highest total emissions during the period.
        /// </summary>
        public required string TopEmittingRegion { get; init; }

        /// <summary>
        /// Start of the summary period (UTC).
        /// </summary>
        public required DateTimeOffset PeriodStart { get; init; }

        /// <summary>
        /// End of the summary period (UTC).
        /// </summary>
        public required DateTimeOffset PeriodEnd { get; init; }
    }

    #endregion

    /// <summary>
    /// Service contract for GHG Protocol-compliant carbon emissions reporting.
    /// Generates Scope 1/2/3 reports, per-region breakdowns, and carbon summaries
    /// from collected energy measurements and grid carbon data.
    /// </summary>
    public interface ICarbonReportingService
    {
        /// <summary>
        /// Generates a full GHG Protocol report with line items for the specified time range,
        /// optionally scoped to a specific tenant.
        /// </summary>
        /// <param name="from">Start of the reporting period (inclusive, UTC).</param>
        /// <param name="to">End of the reporting period (inclusive, UTC).</param>
        /// <param name="tenantId">Optional tenant filter. Null returns system-wide report.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Collection of <see cref="GhgReportEntry"/> items covering all scopes.</returns>
        Task<IReadOnlyList<GhgReportEntry>> GenerateGhgReportAsync(DateTimeOffset from, DateTimeOffset to, string? tenantId = null, CancellationToken ct = default);

        /// <summary>
        /// Returns total emissions in grams of CO2 equivalent for a specific GHG scope and time range.
        /// </summary>
        /// <param name="scope">GHG Protocol scope to query.</param>
        /// <param name="from">Start of the period (inclusive, UTC).</param>
        /// <param name="to">End of the period (inclusive, UTC).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Total emissions in grams of CO2 equivalent.</returns>
        Task<double> GetTotalEmissionsAsync(GhgScopeCategory scope, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

        /// <summary>
        /// Returns a breakdown of total emissions by geographic region for the specified time range.
        /// </summary>
        /// <param name="from">Start of the period (inclusive, UTC).</param>
        /// <param name="to">End of the period (inclusive, UTC).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Dictionary mapping region identifiers to total emissions in grams CO2e.</returns>
        Task<IReadOnlyDictionary<string, double>> GetEmissionsByRegionAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

        /// <summary>
        /// Returns a high-level carbon summary with aggregate metrics,
        /// optionally scoped to a specific tenant.
        /// </summary>
        /// <param name="tenantId">Optional tenant filter. Null returns system-wide summary.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Aggregated <see cref="CarbonSummary"/> for the current period.</returns>
        Task<CarbonSummary> GetCarbonSummaryAsync(string? tenantId = null, CancellationToken ct = default);
    }
}
