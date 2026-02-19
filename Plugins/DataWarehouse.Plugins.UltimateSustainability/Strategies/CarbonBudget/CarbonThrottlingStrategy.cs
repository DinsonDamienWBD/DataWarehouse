using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Carbon;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.CarbonBudgetEnforcement;

/// <summary>
/// Result of a throttle evaluation for a tenant's operation.
/// </summary>
public enum ThrottleResult
{
    /// <summary>Operation may proceed immediately -- within budget limits.</summary>
    Allowed,
    /// <summary>Operation was delayed -- tenant approaching budget limit.</summary>
    Delayed,
    /// <summary>Operation should be rejected -- tenant has exceeded budget.</summary>
    Rejected
}

/// <summary>
/// Provides the actual throttling mechanism that other plugins call to enforce
/// carbon budget limits. Communicates with <see cref="CarbonBudgetEnforcementStrategy"/>
/// via the message bus to evaluate throttle state, then applies the appropriate delay
/// or rejection.
///
/// Usage by other plugins:
/// <code>
/// var result = await throttlingStrategy.ThrottleIfNeededAsync(tenantId, ct);
/// if (result == ThrottleResult.Rejected)
///     throw new OperationRejectedException("Carbon budget exhausted");
/// // result is Allowed or Delayed (delay already applied)
/// </code>
/// </summary>
public sealed class CarbonThrottlingStrategy : SustainabilityStrategyBase
{
    private readonly CarbonBudgetEnforcementStrategy _enforcement;
    private readonly ConcurrentDictionary<string, ThrottleStats> _throttleStats = new(StringComparer.OrdinalIgnoreCase);

    private const string PluginId = "com.datawarehouse.sustainability.ultimate";

    /// <inheritdoc/>
    public override string StrategyId => "carbon-throttling";

    /// <inheritdoc/>
    public override string DisplayName => "Carbon Budget Throttling";

    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.CarbonAwareness;

    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.ActiveControl |
        SustainabilityCapabilities.Scheduling;

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Provides carbon budget throttling for operations. Delays operations when tenants " +
        "approach their carbon budget limit, and rejects operations when the budget is exhausted. " +
        "Tracks throttle events for observability and generates recommendations for frequently throttled tenants.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "carbon", "throttling", "rate-limiting", "budget", "enforcement"
    };

    /// <summary>
    /// Creates a new CarbonThrottlingStrategy backed by the given enforcement strategy.
    /// </summary>
    public CarbonThrottlingStrategy(CarbonBudgetEnforcementStrategy enforcement)
    {
        ArgumentNullException.ThrowIfNull(enforcement);
        _enforcement = enforcement;
    }

    /// <summary>
    /// Evaluates the carbon budget for the given tenant and applies throttling if needed.
    /// - If within budget: returns <see cref="ThrottleResult.Allowed"/> immediately.
    /// - If approaching limit (soft throttle): delays for the recommended duration, then returns <see cref="ThrottleResult.Delayed"/>.
    /// - If budget exhausted (hard throttle): returns <see cref="ThrottleResult.Rejected"/> without delay.
    /// </summary>
    /// <param name="tenantId">The tenant to evaluate.</param>
    /// <param name="ct">Cancellation token for the delay.</param>
    /// <returns>The throttle result indicating whether the operation should proceed.</returns>
    public async Task<ThrottleResult> ThrottleIfNeededAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ThrowIfNotInitialized();

        CarbonThrottleDecision decision;

        // Try to get throttle decision via message bus first (decoupled path)
        if (MessageBus != null)
        {
            try
            {
                var response = await MessageBus.SendAsync(
                    "sustainability.carbon.budget.evaluate",
                    new PluginMessage
                    {
                        Type = "sustainability.carbon.budget.evaluate",
                        SourcePluginId = PluginId,
                        Payload = new Dictionary<string, object> { ["tenantId"] = tenantId }
                    },
                    TimeSpan.FromSeconds(5),
                    ct).ConfigureAwait(false);

                if (response.Success && response.Payload is IDictionary<string, object> data)
                {
                    var levelStr = data.TryGetValue("throttleLevel", out var tl) ? tl?.ToString() : "None";
                    var usagePct = data.TryGetValue("currentUsagePercent", out var up) && up is double upv ? upv : 0;
                    var delayMs = data.TryGetValue("recommendedDelayMs", out var dm) && dm is double dmv ? (int)dmv : 0;
                    if (data.TryGetValue("recommendedDelayMs", out var dmi) && dmi is int dmiValue)
                        delayMs = dmiValue;
                    var msg = data.TryGetValue("message", out var m) ? m?.ToString() ?? "" : "";

                    Enum.TryParse<ThrottleLevel>(levelStr, out var level);
                    decision = new CarbonThrottleDecision
                    {
                        ThrottleLevel = level,
                        CurrentUsagePercent = usagePct,
                        RecommendedDelayMs = delayMs,
                        Message = msg
                    };
                }
                else
                {
                    // Fallback to direct call
                    decision = await _enforcement.EvaluateThrottleAsync(tenantId, ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // Fallback to direct call on bus failure
                decision = await _enforcement.EvaluateThrottleAsync(tenantId, ct).ConfigureAwait(false);
            }
        }
        else
        {
            // No bus available: call enforcement directly
            decision = await _enforcement.EvaluateThrottleAsync(tenantId, ct).ConfigureAwait(false);
        }

        // Apply the throttle decision
        switch (decision.ThrottleLevel)
        {
            case ThrottleLevel.None:
                TrackThrottleResult(tenantId, ThrottleResult.Allowed);
                return ThrottleResult.Allowed;

            case ThrottleLevel.Soft:
                // Apply the recommended delay
                if (decision.RecommendedDelayMs > 0)
                {
                    await Task.Delay(decision.RecommendedDelayMs, ct).ConfigureAwait(false);
                }

                TrackThrottleResult(tenantId, ThrottleResult.Delayed);
                RecordOptimizationAction();

                // Publish throttle applied event
                PublishThrottleApplied(tenantId, decision, ThrottleResult.Delayed);

                return ThrottleResult.Delayed;

            case ThrottleLevel.Hard:
                TrackThrottleResult(tenantId, ThrottleResult.Rejected);
                RecordOptimizationAction();

                // Publish throttle applied event
                PublishThrottleApplied(tenantId, decision, ThrottleResult.Rejected);

                return ThrottleResult.Rejected;

            default:
                return ThrottleResult.Allowed;
        }
    }

    /// <summary>
    /// Gets throttle statistics for a specific tenant.
    /// </summary>
    public ThrottleStats? GetThrottleStats(string tenantId)
    {
        return _throttleStats.TryGetValue(tenantId, out var stats) ? stats : null;
    }

    /// <summary>
    /// Gets throttle statistics for all tracked tenants.
    /// </summary>
    public IReadOnlyDictionary<string, ThrottleStats> GetAllThrottleStats()
    {
        return new Dictionary<string, ThrottleStats>(_throttleStats);
    }

    #region Private Helpers

    /// <summary>
    /// Tracks a throttle result for statistics.
    /// </summary>
    private void TrackThrottleResult(string tenantId, ThrottleResult result)
    {
        var stats = _throttleStats.GetOrAdd(tenantId, _ => new ThrottleStats());
        lock (stats)
        {
            stats.TotalEvaluations++;
            switch (result)
            {
                case ThrottleResult.Allowed:
                    stats.AllowedCount++;
                    break;
                case ThrottleResult.Delayed:
                    stats.DelayedCount++;
                    break;
                case ThrottleResult.Rejected:
                    stats.RejectedCount++;
                    break;
            }
            stats.LastEvaluated = DateTimeOffset.UtcNow;
        }

        // Generate recommendations for frequently throttled tenants
        if (stats.DelayedCount + stats.RejectedCount > 0 &&
            (stats.DelayedCount + stats.RejectedCount) % 5 == 0)
        {
            GenerateThrottleRecommendation(tenantId, stats);
        }
    }

    /// <summary>
    /// Generates a recommendation when a tenant is frequently throttled.
    /// </summary>
    private void GenerateThrottleRecommendation(string tenantId, ThrottleStats stats)
    {
        var totalThrottled = stats.DelayedCount + stats.RejectedCount;
        var throttleRate = stats.TotalEvaluations > 0
            ? (double)totalThrottled / stats.TotalEvaluations * 100.0
            : 0;

        if (throttleRate > 20) // More than 20% of operations throttled
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-frequent-throttle-{tenantId}",
                Type = "FrequentThrottling",
                Priority = stats.RejectedCount > 0 ? 9 : 7,
                Description = $"Tenant '{tenantId}' is being throttled frequently: " +
                              $"{totalThrottled}/{stats.TotalEvaluations} operations ({throttleRate:F1}%). " +
                              $"Delayed: {stats.DelayedCount}, Rejected: {stats.RejectedCount}. " +
                              "Consider increasing budget or optimizing workloads.",
                EstimatedCarbonReductionGrams = 0,
                CanAutoApply = false,
                Action = "review-tenant-budget",
                ActionParameters = new Dictionary<string, object>
                {
                    ["tenantId"] = tenantId,
                    ["totalThrottled"] = totalThrottled,
                    ["throttleRate"] = throttleRate,
                    ["rejectedCount"] = stats.RejectedCount
                }
            });
        }
    }

    /// <summary>
    /// Publishes a throttle-applied event to the message bus.
    /// </summary>
    private void PublishThrottleApplied(string tenantId, CarbonThrottleDecision decision, ThrottleResult result)
    {
        if (MessageBus == null) return;
        try
        {
            _ = MessageBus.PublishAsync("sustainability.carbon.throttle.applied", new PluginMessage
            {
                Type = "sustainability.carbon.throttle.applied",
                SourcePluginId = PluginId,
                Payload = new Dictionary<string, object>
                {
                    ["tenantId"] = tenantId,
                    ["throttleLevel"] = decision.ThrottleLevel.ToString(),
                    ["throttleResult"] = result.ToString(),
                    ["currentUsagePercent"] = decision.CurrentUsagePercent,
                    ["delayAppliedMs"] = decision.RecommendedDelayMs,
                    ["message"] = decision.Message,
                    ["timestamp"] = DateTimeOffset.UtcNow
                }
            });
        }
        catch
        {
            // Non-critical
        }
    }

    #endregion
}

/// <summary>
/// Statistics for throttle events per tenant.
/// </summary>
public sealed class ThrottleStats
{
    /// <summary>Total number of throttle evaluations.</summary>
    public long TotalEvaluations { get; set; }

    /// <summary>Number of evaluations that resulted in Allowed.</summary>
    public long AllowedCount { get; set; }

    /// <summary>Number of evaluations that resulted in Delayed (soft throttle).</summary>
    public long DelayedCount { get; set; }

    /// <summary>Number of evaluations that resulted in Rejected (hard throttle).</summary>
    public long RejectedCount { get; set; }

    /// <summary>Timestamp of the last evaluation.</summary>
    public DateTimeOffset LastEvaluated { get; set; }
}
