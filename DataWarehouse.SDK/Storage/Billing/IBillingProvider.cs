using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Storage.Billing;

/// <summary>
/// Interface for cloud billing API integration. Provides access to billing reports,
/// spot pricing, reserved capacity, cost forecasting, and credential validation
/// for a specific cloud provider.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public interface IBillingProvider
{
    /// <summary>
    /// Gets the cloud provider this billing integration connects to.
    /// </summary>
    CloudProvider Provider { get; }

    /// <summary>
    /// Retrieves a billing report for the specified time period.
    /// </summary>
    /// <param name="from">Start of the billing period (inclusive).</param>
    /// <param name="to">End of the billing period (exclusive).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A billing report with itemized cost breakdown.</returns>
    Task<BillingReport> GetBillingReportAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default);

    /// <summary>
    /// Gets current spot pricing for storage in the specified region (or all regions if null).
    /// </summary>
    /// <param name="region">Optional region filter. Null returns all regions.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Spot pricing entries for matching regions.</returns>
    Task<IReadOnlyList<SpotPricing>> GetSpotPricingAsync(
        string? region = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets all active reserved capacity commitments for this provider.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All active reserved capacity entries.</returns>
    Task<IReadOnlyList<ReservedCapacity>> GetReservedCapacityAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Generates a cost forecast for the specified number of days, including
    /// optimization recommendations.
    /// </summary>
    /// <param name="days">Number of days to forecast.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A cost forecast with projected spend and recommendations.</returns>
    Task<CostForecast> ForecastCostAsync(int days, CancellationToken ct = default);

    /// <summary>
    /// Validates that the configured credentials can access the provider's billing APIs.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if credentials are valid and billing access is confirmed.</returns>
    Task<bool> ValidateCredentialsAsync(CancellationToken ct = default);
}
