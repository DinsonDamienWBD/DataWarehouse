using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Storage.Billing;

/// <summary>
/// Identifies the cloud infrastructure provider for billing and pricing operations.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public enum CloudProvider
{
    AWS,
    Azure,
    GCP,
    Oracle,
    Alibaba,
    OnPremise,
    Custom
}

/// <summary>
/// Categorizes cost line items in a billing report.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public enum CostCategory
{
    Storage,
    Compute,
    Egress,
    Operations,
    Transfer,
    ReservedCapacity,
    SpotInstance,
    Other
}

/// <summary>
/// Type of cost optimization recommendation.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public enum RecommendationType
{
    MoveToSpot,
    ReserveCapacity,
    TierTransition,
    RegionChange,
    ProviderSwitch
}

/// <summary>
/// A billing report for a specific provider and time period, with itemized cost breakdown.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public sealed record BillingReport(
    string ProviderId,
    CloudProvider Provider,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    decimal TotalCost,
    string Currency,
    IReadOnlyList<CostBreakdown> Breakdown);

/// <summary>
/// A single cost line item within a billing report.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public sealed record CostBreakdown(
    CostCategory Category,
    string ServiceName,
    decimal Amount,
    string Unit,
    double Quantity,
    string? Region = null);

/// <summary>
/// Spot pricing information for a specific provider, region, and storage class.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public sealed record SpotPricing(
    CloudProvider Provider,
    string Region,
    string StorageClass,
    decimal CurrentPricePerGBMonth,
    decimal SpotPricePerGBMonth,
    double SavingsPercent,
    long AvailableCapacityGB,
    double InterruptionProbability);

/// <summary>
/// Reserved capacity commitment details for a specific provider and region.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public sealed record ReservedCapacity(
    CloudProvider Provider,
    string Region,
    string StorageClass,
    long CommittedGB,
    decimal ReservedPricePerGBMonth,
    decimal OnDemandPricePerGBMonth,
    double SavingsPercent,
    int TermMonths,
    DateTimeOffset ExpiresUtc);

/// <summary>
/// A cost forecast with projected spend and optimization recommendations.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public sealed record CostForecast(
    CloudProvider Provider,
    int ForecastPeriodDays,
    decimal ProjectedCost,
    double ConfidencePercent,
    IReadOnlyList<CostRecommendation> Recommendations);

/// <summary>
/// A specific cost optimization recommendation with estimated savings and implementation details.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public sealed record CostRecommendation(
    RecommendationType Type,
    string Description,
    decimal EstimatedMonthlySavings,
    string ImplementationEffort,
    string RiskLevel);
