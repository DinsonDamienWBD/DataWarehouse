using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Carbon
{
    #region Throttle Decision

    /// <summary>
    /// Throttle severity level applied when a tenant approaches or exceeds their carbon budget.
    /// </summary>
    public enum ThrottleLevel
    {
        /// <summary>No throttling -- budget usage is within acceptable limits.</summary>
        None,
        /// <summary>Soft throttle -- usage exceeds the warning threshold; operations are delayed.</summary>
        Soft,
        /// <summary>Hard throttle -- budget is exhausted; non-essential operations are blocked.</summary>
        Hard
    }

    /// <summary>
    /// Result of evaluating a tenant's carbon budget against current usage.
    /// Provides the throttle level, current usage percentage, and a recommended delay.
    /// </summary>
    public sealed record CarbonThrottleDecision
    {
        /// <summary>
        /// The throttle severity to apply.
        /// </summary>
        public required ThrottleLevel ThrottleLevel { get; init; }

        /// <summary>
        /// Current carbon usage as a percentage of the allocated budget (0-100+).
        /// </summary>
        public required double CurrentUsagePercent { get; init; }

        /// <summary>
        /// Recommended delay in milliseconds before proceeding with the next operation.
        /// Zero when <see cref="ThrottleLevel"/> is <see cref="Carbon.ThrottleLevel.None"/>.
        /// </summary>
        public required int RecommendedDelayMs { get; init; }

        /// <summary>
        /// Human-readable message describing the throttle decision.
        /// </summary>
        public required string Message { get; init; }
    }

    #endregion

    /// <summary>
    /// Service contract for managing per-tenant carbon emission budgets.
    /// Enables carbon-aware operations by tracking budget allocation, usage,
    /// and throttle decisions based on configurable thresholds.
    /// </summary>
    public interface ICarbonBudgetService
    {
        /// <summary>
        /// Retrieves the current carbon budget for a tenant, including usage and remaining allowance.
        /// </summary>
        /// <param name="tenantId">Tenant identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The tenant's current <see cref="CarbonBudget"/>.</returns>
        Task<CarbonBudget> GetBudgetAsync(string tenantId, CancellationToken ct = default);

        /// <summary>
        /// Sets or updates the carbon budget for a tenant.
        /// </summary>
        /// <param name="tenantId">Tenant identifier.</param>
        /// <param name="budgetGramsCO2e">Total budget in grams of CO2 equivalent for the period.</param>
        /// <param name="period">Budget period granularity.</param>
        /// <param name="ct">Cancellation token.</param>
        Task SetBudgetAsync(string tenantId, double budgetGramsCO2e, CarbonBudgetPeriod period, CancellationToken ct = default);

        /// <summary>
        /// Checks whether a tenant has sufficient carbon budget to proceed with an operation
        /// of the estimated carbon cost. Returns false if the budget is exhausted.
        /// </summary>
        /// <param name="tenantId">Tenant identifier.</param>
        /// <param name="estimatedCarbonGramsCO2e">Estimated carbon cost of the pending operation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if the operation can proceed within budget; false if budget is exhausted.</returns>
        Task<bool> CanProceedAsync(string tenantId, double estimatedCarbonGramsCO2e, CancellationToken ct = default);

        /// <summary>
        /// Records actual carbon usage for a completed operation against the tenant's budget.
        /// </summary>
        /// <param name="tenantId">Tenant identifier.</param>
        /// <param name="carbonGramsCO2e">Actual carbon emissions in grams of CO2 equivalent.</param>
        /// <param name="operationType">Type of operation: "read", "write", "delete", or "list".</param>
        /// <param name="ct">Cancellation token.</param>
        Task RecordUsageAsync(string tenantId, double carbonGramsCO2e, string operationType, CancellationToken ct = default);

        /// <summary>
        /// Evaluates the current throttle state for a tenant based on budget usage vs thresholds.
        /// Returns a <see cref="CarbonThrottleDecision"/> with the throttle level and recommended delay.
        /// </summary>
        /// <param name="tenantId">Tenant identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Throttle decision with level, usage percentage, delay, and message.</returns>
        Task<CarbonThrottleDecision> EvaluateThrottleAsync(string tenantId, CancellationToken ct = default);
    }
}
