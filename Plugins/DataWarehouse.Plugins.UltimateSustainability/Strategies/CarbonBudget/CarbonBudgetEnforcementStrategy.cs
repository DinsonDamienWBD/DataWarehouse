using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Carbon;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.CarbonBudgetEnforcement;

/// <summary>
/// Enforces per-tenant carbon budgets with progressive throttling.
/// Implements <see cref="ICarbonBudgetService"/> from the SDK and extends
/// <see cref="SustainabilityStrategyBase"/> for integration with the sustainability framework.
///
/// Behavior:
/// - Tenants below 80% budget: no throttling (ThrottleLevel.None)
/// - Tenants between 80-100%: soft throttling with increasing delays (ThrottleLevel.Soft)
/// - Tenants above 100%: hard block, operations rejected (ThrottleLevel.Hard)
/// - Budget periods auto-reset at boundaries (hourly through annual)
/// - All budget events published to message bus for observability
/// </summary>
public sealed class CarbonBudgetEnforcementStrategy : SustainabilityStrategyBase, ICarbonBudgetService
{
    private readonly CarbonBudgetStore _store;
    private Timer? _resetTimer;
    private Timer? _recommendationTimer;
    private IDisposable? _energySubscription;
    private IDisposable? _evaluateSubscription;
    private readonly ConcurrentDictionary<string, int> _throttleEventCounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Default soft threshold percentage. Operations are delayed above this.
    /// </summary>
    public double DefaultThrottleThresholdPercent { get; set; } = 80.0;

    /// <summary>
    /// Default hard limit percentage. Operations are rejected above this.
    /// </summary>
    public double DefaultHardLimitPercent { get; set; } = 100.0;

    /// <summary>
    /// Minimum delay in milliseconds at the soft threshold boundary.
    /// </summary>
    public int MinDelayMs { get; set; } = 100;

    /// <summary>
    /// Maximum delay in milliseconds at the hard limit boundary.
    /// </summary>
    public int MaxDelayMs { get; set; } = 2000;

    /// <summary>
    /// Default carbon grid intensity (gCO2e/kWh) used to convert energy to carbon
    /// when no real-time intensity data is available from the bus.
    /// </summary>
    public double DefaultCarbonIntensityGCO2ePerKwh { get; set; } = 400.0;

    /// <summary>
    /// Threshold for recommending budget increases -- if tenant is throttled this many times per period.
    /// </summary>
    public int ThrottleCountForRecommendation { get; set; } = 10;

    private const string PluginId = "com.datawarehouse.sustainability.ultimate";

    /// <inheritdoc/>
    public override string StrategyId => "carbon-budget-enforcement";

    /// <inheritdoc/>
    public override string DisplayName => "Carbon Budget Enforcement";

    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.CarbonAwareness;

    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.ActiveControl |
        SustainabilityCapabilities.CarbonCalculation |
        SustainabilityCapabilities.Alerting;

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Enforces per-tenant carbon emission budgets with progressive throttling. " +
        "Tracks budget allocation and usage, applies soft delays at 80% utilization, " +
        "and blocks operations at 100%. Publishes budget events for observability.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "carbon", "budget", "enforcement", "throttling", "tenant", "quota"
    };

    /// <summary>
    /// Creates a new CarbonBudgetEnforcementStrategy with the given budget store.
    /// </summary>
    public CarbonBudgetEnforcementStrategy(CarbonBudgetStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>
    /// Gets the underlying budget store for direct access.
    /// </summary>
    public CarbonBudgetStore Store => _store;

    #region Lifecycle

    /// <inheritdoc/>
    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        await _store.LoadAsync(ct).ConfigureAwait(false);

        // Background timer: reset expired budgets every 60 seconds
        _resetTimer = new Timer(
            async _ =>
            {
                try { await _store.ResetExpiredBudgetsAsync().ConfigureAwait(false); }
                catch { /* Swallow -- background maintenance should not crash */ }
            },
            null,
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(60));

        // Recommendation refresh timer: every 5 minutes, update throttle-based recommendations
        _recommendationTimer = new Timer(
            _ => UpdateThrottleRecommendations(),
            null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(5));

        // Subscribe to energy measurement events to auto-record usage
        SubscribeToEnergyEvents();

        // Subscribe to budget evaluate requests from throttling strategy
        SubscribeToEvaluateRequests();
    }

    /// <inheritdoc/>
    protected override async Task DisposeCoreAsync()
    {
        _resetTimer?.Dispose();
        _resetTimer = null;
        _recommendationTimer?.Dispose();
        _recommendationTimer = null;
        _energySubscription?.Dispose();
        _energySubscription = null;
        _evaluateSubscription?.Dispose();
        _evaluateSubscription = null;
        await _store.SaveAsync(CancellationToken.None).ConfigureAwait(false);
        _store.Dispose();
    }

    #endregion

    #region ICarbonBudgetService Implementation

    /// <inheritdoc/>
    public async Task<SDK.Contracts.Carbon.CarbonBudget> GetBudgetAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var budget = await _store.GetAsync(tenantId).ConfigureAwait(false);
        if (budget != null)
            return budget;

        // Return a default unlimited budget for unknown tenants
        return new SDK.Contracts.Carbon.CarbonBudget
        {
            TenantId = tenantId,
            BudgetPeriod = CarbonBudgetPeriod.Monthly,
            BudgetGramsCO2e = double.MaxValue,
            UsedGramsCO2e = 0,
            ThrottleThresholdPercent = DefaultThrottleThresholdPercent,
            HardLimitPercent = DefaultHardLimitPercent,
            PeriodStart = GetCurrentPeriodStart(CarbonBudgetPeriod.Monthly),
            PeriodEnd = CarbonBudgetStore.ComputePeriodEnd(
                GetCurrentPeriodStart(CarbonBudgetPeriod.Monthly), CarbonBudgetPeriod.Monthly)
        };
    }

    /// <inheritdoc/>
    public async Task SetBudgetAsync(string tenantId, double budgetGramsCO2e, CarbonBudgetPeriod period, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var periodStart = GetCurrentPeriodStart(period);
        var periodEnd = CarbonBudgetStore.ComputePeriodEnd(periodStart, period);

        // Check if tenant already has a budget -- preserve usage if within same period
        var existing = await _store.GetAsync(tenantId).ConfigureAwait(false);
        double usedGrams = 0;
        if (existing != null && existing.BudgetPeriod == period &&
            existing.PeriodStart == periodStart)
        {
            usedGrams = existing.UsedGramsCO2e;
        }

        var budget = new SDK.Contracts.Carbon.CarbonBudget
        {
            TenantId = tenantId,
            BudgetPeriod = period,
            BudgetGramsCO2e = budgetGramsCO2e,
            UsedGramsCO2e = usedGrams,
            ThrottleThresholdPercent = DefaultThrottleThresholdPercent,
            HardLimitPercent = DefaultHardLimitPercent,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd
        };

        await _store.SetAsync(tenantId, budget).ConfigureAwait(false);

        PublishBudgetEvent("sustainability.carbon.budget.set", tenantId, budget);
    }

    /// <inheritdoc/>
    public async Task<bool> CanProceedAsync(string tenantId, double estimatedCarbonGramsCO2e, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var budget = await _store.GetAsync(tenantId).ConfigureAwait(false);
        if (budget == null)
            return true; // No budget = unlimited

        // Check if estimated usage would exceed hard limit
        var projectedUsage = budget.UsedGramsCO2e + estimatedCarbonGramsCO2e;
        var projectedPercent = budget.BudgetGramsCO2e > 0
            ? (projectedUsage / budget.BudgetGramsCO2e) * 100.0
            : 0;

        return projectedPercent <= budget.HardLimitPercent;
    }

    /// <inheritdoc/>
    public async Task RecordUsageAsync(string tenantId, double carbonGramsCO2e, string operationType, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // Record to store (atomic)
        var recorded = await _store.RecordUsageAsync(tenantId, carbonGramsCO2e).ConfigureAwait(false);
        if (!recorded)
        {
            // Budget exhausted -- publish exhaustion event
            var exhaustedBudget = await _store.GetAsync(tenantId).ConfigureAwait(false);
            if (exhaustedBudget != null)
            {
                PublishBudgetEvent("sustainability.carbon.budget.exhausted", tenantId, exhaustedBudget);
            }
            return;
        }

        // Get updated budget to check thresholds
        var budget = await _store.GetAsync(tenantId).ConfigureAwait(false);
        if (budget == null) return;

        var usagePercent = budget.BudgetGramsCO2e > 0
            ? (budget.UsedGramsCO2e / budget.BudgetGramsCO2e) * 100.0
            : 0;

        // Publish usage event
        PublishMessage("sustainability.carbon.budget.usage", new Dictionary<string, object>
        {
            ["tenantId"] = tenantId,
            ["operationType"] = operationType,
            ["carbonGramsCO2e"] = carbonGramsCO2e,
            ["totalUsedGramsCO2e"] = budget.UsedGramsCO2e,
            ["remainingGramsCO2e"] = budget.RemainingGramsCO2e,
            ["usagePercent"] = usagePercent,
            ["timestamp"] = DateTimeOffset.UtcNow
        });

        // Check if threshold was crossed
        var throttle = await EvaluateThrottleAsync(tenantId, ct).ConfigureAwait(false);
        if (throttle.ThrottleLevel != ThrottleLevel.None)
        {
            TrackThrottleEvent(tenantId);

            PublishMessage("sustainability.carbon.budget.threshold", new Dictionary<string, object>
            {
                ["tenantId"] = tenantId,
                ["throttleLevel"] = throttle.ThrottleLevel.ToString(),
                ["usagePercent"] = throttle.CurrentUsagePercent,
                ["recommendedDelayMs"] = throttle.RecommendedDelayMs,
                ["message"] = throttle.Message,
                ["timestamp"] = DateTimeOffset.UtcNow
            });
        }

        // Track optimization action
        RecordOptimizationAction();
    }

    /// <inheritdoc/>
    public async Task<CarbonThrottleDecision> EvaluateThrottleAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var budget = await _store.GetAsync(tenantId).ConfigureAwait(false);
        if (budget == null)
        {
            return new CarbonThrottleDecision
            {
                ThrottleLevel = ThrottleLevel.None,
                CurrentUsagePercent = 0,
                RecommendedDelayMs = 0,
                Message = "No carbon budget configured for this tenant."
            };
        }

        var usagePercent = budget.BudgetGramsCO2e > 0
            ? (budget.UsedGramsCO2e / budget.BudgetGramsCO2e) * 100.0
            : 0;

        // Above hard limit: reject
        if (usagePercent >= budget.HardLimitPercent)
        {
            return new CarbonThrottleDecision
            {
                ThrottleLevel = ThrottleLevel.Hard,
                CurrentUsagePercent = usagePercent,
                RecommendedDelayMs = MaxDelayMs,
                Message = $"Carbon budget exhausted for tenant '{tenantId}'. " +
                          $"Usage: {usagePercent:F1}% of {budget.BudgetGramsCO2e:F0}g CO2e budget. " +
                          "Operations should be rejected."
            };
        }

        // Between soft threshold and hard limit: progressive delay
        if (usagePercent >= budget.ThrottleThresholdPercent)
        {
            // Linear scale: MinDelayMs at threshold, MaxDelayMs at hard limit
            var range = budget.HardLimitPercent - budget.ThrottleThresholdPercent;
            var progress = range > 0 ? (usagePercent - budget.ThrottleThresholdPercent) / range : 1.0;
            var delayMs = (int)(MinDelayMs + (progress * (MaxDelayMs - MinDelayMs)));
            delayMs = Math.Clamp(delayMs, MinDelayMs, MaxDelayMs);

            return new CarbonThrottleDecision
            {
                ThrottleLevel = ThrottleLevel.Soft,
                CurrentUsagePercent = usagePercent,
                RecommendedDelayMs = delayMs,
                Message = $"Carbon budget soft throttle for tenant '{tenantId}'. " +
                          $"Usage: {usagePercent:F1}% (threshold: {budget.ThrottleThresholdPercent}%). " +
                          $"Recommended delay: {delayMs}ms."
            };
        }

        // Below threshold: no throttle
        return new CarbonThrottleDecision
        {
            ThrottleLevel = ThrottleLevel.None,
            CurrentUsagePercent = usagePercent,
            RecommendedDelayMs = 0,
            Message = $"Carbon budget within limits for tenant '{tenantId}'. " +
                      $"Usage: {usagePercent:F1}% of {budget.BudgetGramsCO2e:F0}g CO2e budget."
        };
    }

    #endregion

    #region Energy Event Subscription

    /// <summary>
    /// Subscribes to energy measurement events on the message bus.
    /// When an energy measurement is received, converts Wh to gCO2e
    /// using current grid intensity and records it against the tenant's budget.
    /// </summary>
    private void SubscribeToEnergyEvents()
    {
        if (MessageBus == null) return;

        _energySubscription = MessageBus.Subscribe("sustainability.energy.measured", async (PluginMessage message) =>
        {
            try
            {
                var payload = message.Payload;

                var tenantId = payload.TryGetValue("tenantId", out var tid) ? tid?.ToString() : null;
                if (string.IsNullOrWhiteSpace(tenantId)) return;

                var energyWh = payload.TryGetValue("energyWh", out var ewh) && ewh is double e ? e : 0;
                if (energyWh <= 0) return;

                var operationType = payload.TryGetValue("operationType", out var ot) ? ot?.ToString() ?? "unknown" : "unknown";

                // Try to get current grid intensity from the bus via request-response
                double carbonIntensity = DefaultCarbonIntensityGCO2ePerKwh;
                try
                {
                    var response = await MessageBus.SendAsync(
                        "sustainability.carbon.intensity.current",
                        new PluginMessage
                        {
                            Type = "sustainability.carbon.intensity.current",
                            SourcePluginId = PluginId,
                            Payload = new Dictionary<string, object>()
                        },
                        TimeSpan.FromSeconds(2)).ConfigureAwait(false);

                    if (response.Success && response.Payload is IDictionary<string, object> intensityData &&
                        intensityData.TryGetValue("carbonIntensityGCO2ePerKwh", out var ci) &&
                        ci is double ciValue && ciValue > 0)
                    {
                        carbonIntensity = ciValue;
                    }
                }
                catch
                {
                    // Use default intensity if request fails
                }

                // Convert Wh to gCO2e: gCO2e = Wh * (gCO2e/kWh) / 1000
                var carbonGrams = energyWh * carbonIntensity / 1000.0;

                await RecordUsageAsync(tenantId, carbonGrams, operationType).ConfigureAwait(false);
            }
            catch
            {
                // Swallow errors in event handler to avoid disrupting the bus
            }
        });
    }

    /// <summary>
    /// Subscribes to throttle evaluation requests from the throttling strategy.
    /// This enables the CarbonThrottlingStrategy to query throttle state via the bus.
    /// </summary>
    private void SubscribeToEvaluateRequests()
    {
        if (MessageBus == null) return;

        _evaluateSubscription = MessageBus.Subscribe("sustainability.carbon.budget.evaluate",
            async (PluginMessage message) =>
            {
                var tenantId = message.Payload.TryGetValue("tenantId", out var tid) ? tid?.ToString() : null;
                if (string.IsNullOrWhiteSpace(tenantId))
                    return MessageResponse.Error("Missing tenantId in evaluate request");

                var decision = await EvaluateThrottleAsync(tenantId).ConfigureAwait(false);
                return MessageResponse.Ok(new Dictionary<string, object>
                {
                    ["throttleLevel"] = decision.ThrottleLevel.ToString(),
                    ["currentUsagePercent"] = decision.CurrentUsagePercent,
                    ["recommendedDelayMs"] = decision.RecommendedDelayMs,
                    ["message"] = decision.Message
                });
            });
    }

    #endregion

    #region Recommendations

    /// <summary>
    /// Updates throttle-based recommendations for tenants that are frequently throttled.
    /// </summary>
    private void UpdateThrottleRecommendations()
    {
        ClearRecommendations();

        foreach (var tenantId in _store.GetAllTenantIds())
        {
            var usagePercent = _store.GetUsagePercent(tenantId);
            var throttleCount = _throttleEventCounts.GetValueOrDefault(tenantId, 0);

            if (throttleCount >= ThrottleCountForRecommendation)
            {
                AddRecommendation(new SustainabilityRecommendation
                {
                    RecommendationId = $"{StrategyId}-increase-budget-{tenantId}",
                    Type = "IncreaseBudget",
                    Priority = usagePercent > 90 ? 9 : 7,
                    Description = $"Tenant '{tenantId}' has been throttled {throttleCount} times this period " +
                                  $"(usage: {usagePercent:F1}%). Consider increasing their carbon budget or " +
                                  "optimizing their workloads for lower energy consumption.",
                    EstimatedCarbonReductionGrams = 0,
                    CanAutoApply = false,
                    Action = "increase-budget",
                    ActionParameters = new Dictionary<string, object>
                    {
                        ["tenantId"] = tenantId,
                        ["throttleCount"] = throttleCount,
                        ["currentUsagePercent"] = usagePercent
                    }
                });
            }
        }
    }

    /// <summary>
    /// Tracks a throttle event for the tenant (used for recommendation generation).
    /// </summary>
    internal void TrackThrottleEvent(string tenantId)
    {
        _throttleEventCounts.AddOrUpdate(tenantId, 1, (_, count) => count + 1);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Computes the start of the current period for the given granularity.
    /// </summary>
    private static DateTimeOffset GetCurrentPeriodStart(CarbonBudgetPeriod period)
    {
        var now = DateTimeOffset.UtcNow;
        return period switch
        {
            CarbonBudgetPeriod.Hourly => new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero),
            CarbonBudgetPeriod.Daily => new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero),
            CarbonBudgetPeriod.Weekly => new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero)
                .AddDays(-(int)now.DayOfWeek),
            CarbonBudgetPeriod.Monthly => new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero),
            CarbonBudgetPeriod.Quarterly => new DateTimeOffset(now.Year, ((now.Month - 1) / 3) * 3 + 1, 1, 0, 0, 0, TimeSpan.Zero),
            CarbonBudgetPeriod.Annual => new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, TimeSpan.Zero),
            _ => new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero)
        };
    }

    /// <summary>
    /// Publishes a carbon budget event to the message bus.
    /// </summary>
    private void PublishBudgetEvent(string topic, string tenantId, SDK.Contracts.Carbon.CarbonBudget budget)
    {
        PublishMessage(topic, new Dictionary<string, object>
        {
            ["tenantId"] = tenantId,
            ["budgetGramsCO2e"] = budget.BudgetGramsCO2e,
            ["usedGramsCO2e"] = budget.UsedGramsCO2e,
            ["remainingGramsCO2e"] = budget.RemainingGramsCO2e,
            ["budgetPeriod"] = budget.BudgetPeriod.ToString(),
            ["periodStart"] = budget.PeriodStart,
            ["periodEnd"] = budget.PeriodEnd,
            ["isThrottled"] = budget.IsThrottled,
            ["isExhausted"] = budget.IsExhausted,
            ["timestamp"] = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// Publishes a message to the message bus if available.
    /// </summary>
    private void PublishMessage(string topic, Dictionary<string, object> payload)
    {
        if (MessageBus == null) return;
        try
        {
            _ = MessageBus.PublishAsync(topic, new PluginMessage
            {
                Type = topic,
                SourcePluginId = PluginId,
                Payload = payload
            });
        }
        catch
        {
            // Non-critical: don't let bus errors disrupt budget operations
        }
    }

    #endregion
}
