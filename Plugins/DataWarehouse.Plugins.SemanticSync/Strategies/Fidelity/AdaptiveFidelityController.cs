using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.SemanticSync;

namespace DataWarehouse.Plugins.SemanticSync.Strategies.Fidelity;

/// <summary>
/// Bandwidth-aware fidelity controller that dynamically adjusts sync quality based on
/// real-time network conditions, semantic classification, and policy constraints.
/// Acts as the "throttle" for the entire sync protocol: when bandwidth drops, it automatically
/// reduces fidelity for lower-importance data while maintaining full fidelity for critical items.
/// </summary>
/// <remarks>
/// <para>
/// The controller integrates three subsystems:
/// </para>
/// <list type="bullet">
///   <item><see cref="BandwidthBudgetTracker"/>: real-time bandwidth accounting with lock-free concurrency.</item>
///   <item><see cref="FidelityPolicyEngine"/>: policy enforcement ensuring critical/compliance data never degrades.</item>
///   <item>Budget-aware degradation with three tiers based on remaining capacity (&gt;50%, 20-50%, &lt;20%).</item>
/// </list>
/// <para>
/// Decision flow for each sync item:
/// </para>
/// <list type="number">
///   <item>Compute bandwidth-based fidelity from current measurements.</item>
///   <item>Check if low-importance data should be deferred when budget is exhausted.</item>
///   <item>Apply policy constraints (critical/compliance/security minimums).</item>
///   <item>Estimate payload size at the chosen fidelity level.</item>
///   <item>Apply budget-aware degradation if remaining capacity is low.</item>
///   <item>Return a <see cref="SyncDecision"/> with fidelity, reason, size estimate, and optional deferral.</item>
/// </list>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic conflict resolution")]
public sealed class AdaptiveFidelityController : SemanticSyncStrategyBase, ISyncFidelityController
{
    /// <summary>
    /// Size ratios for each fidelity level relative to the full payload size.
    /// Used to estimate the sync payload size at each fidelity level.
    /// </summary>
    private static readonly Dictionary<SyncFidelity, double> FidelitySizeRatios = new()
    {
        [SyncFidelity.Full] = 1.0,
        [SyncFidelity.Detailed] = 0.8,
        [SyncFidelity.Standard] = 0.5,
        [SyncFidelity.Summarized] = 0.15,
        [SyncFidelity.Metadata] = 0.02
    };

    /// <summary>
    /// Default base size in bytes used when no size hint is available from classification.
    /// Represents a typical data payload of approximately 100 KB.
    /// </summary>
    private const long DefaultBaseSizeBytes = 102_400;

    private readonly BandwidthBudgetTracker _tracker;
    private volatile FidelityPolicyEngine _policyEngine;
    private volatile FidelityPolicy _currentPolicy;

    /// <inheritdoc />
    public override string StrategyId => "fidelity-controller-adaptive";

    /// <inheritdoc />
    public override string Name => "Adaptive Fidelity Controller";

    /// <inheritdoc />
    public override string Description =>
        "Dynamically adjusts sync fidelity based on real-time bandwidth, semantic classification, and policy constraints.";

    /// <inheritdoc />
    public override string SemanticDomain => "fidelity";

    /// <summary>
    /// Initializes a new instance of the <see cref="AdaptiveFidelityController"/> class
    /// with the specified budget tracker and policy engine.
    /// </summary>
    /// <param name="tracker">The bandwidth budget tracker for real-time accounting.</param>
    /// <param name="policyEngine">The policy engine for enforcing fidelity constraints.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="tracker"/> or <paramref name="policyEngine"/> is null.
    /// </exception>
    internal AdaptiveFidelityController(BandwidthBudgetTracker tracker, FidelityPolicyEngine policyEngine)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _policyEngine = policyEngine ?? throw new ArgumentNullException(nameof(policyEngine));
        _currentPolicy = CreateDefaultPolicy();
    }

    /// <summary>
    /// Decides the appropriate sync fidelity for a data item given its semantic classification
    /// and the current bandwidth budget. Applies bandwidth-based calculation, policy constraints,
    /// and budget-aware degradation in a layered decision process.
    /// </summary>
    /// <param name="classification">The semantic classification from the AI classifier pipeline.</param>
    /// <param name="budget">The current bandwidth budget state.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="SyncDecision"/> containing the chosen fidelity level, the reason for the decision,
    /// whether a summary is required, the estimated payload size, and optional deferral timing.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="classification"/> or <paramref name="budget"/> is null.
    /// </exception>
    public Task<SyncDecision> DecideFidelityAsync(
        SemanticClassification classification,
        FidelityBudget budget,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(classification);
        ArgumentNullException.ThrowIfNull(budget);
        ct.ThrowIfCancellationRequested();

        var policyEngine = _policyEngine;
        var currentPolicy = _currentPolicy;

        // Step 1: Get bandwidth-based fidelity from policy engine
        SyncFidelity bandwidthFidelity = policyEngine.GetFidelityForBandwidth(budget.AvailableBandwidthBps);

        // Step 2: Check if low-importance data should be deferred when budget exhausted
        if (policyEngine.ShouldDefer(classification, budget))
        {
            var deferDecision = new SyncDecision(
                DataId: classification.DataId,
                Fidelity: SyncFidelity.Metadata,
                Reason: SyncDecisionReason.BandwidthConstrained,
                RequiresSummary: false,
                EstimatedSizeBytes: EstimateSize(DefaultBaseSizeBytes, SyncFidelity.Metadata),
                DeferUntil: currentPolicy.MaxDeferDuration);

            IncrementCounter("decisions_deferred");
            return Task.FromResult(deferDecision);
        }

        // Step 3: Apply policy constraints (critical/compliance/security minimums)
        SyncFidelity policyAdjusted = policyEngine.ApplyPolicy(classification, bandwidthFidelity);

        // Step 4: Apply budget-aware degradation based on remaining capacity
        double remainingCapacity = _tracker.GetRemainingCapacityPercent();
        SyncFidelity finalFidelity = ApplyBudgetDegradation(policyAdjusted, remainingCapacity, classification, policyEngine);

        // Step 5: Estimate payload size at the chosen fidelity
        long estimatedSize = EstimateSize(DefaultBaseSizeBytes, finalFidelity);

        // Step 6: Determine reason and summary requirement
        SyncDecisionReason reason = DetermineReason(classification, bandwidthFidelity, finalFidelity);
        bool requiresSummary = finalFidelity != SyncFidelity.Full && finalFidelity != SyncFidelity.Metadata;

        var decision = new SyncDecision(
            DataId: classification.DataId,
            Fidelity: finalFidelity,
            Reason: reason,
            RequiresSummary: requiresSummary,
            EstimatedSizeBytes: estimatedSize,
            DeferUntil: null);

        IncrementCounter("decisions_made");
        return Task.FromResult(decision);
    }

    /// <summary>
    /// Gets the current bandwidth budget state by delegating to the budget tracker.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current <see cref="FidelityBudget"/> snapshot.</returns>
    public Task<FidelityBudget> GetCurrentBudgetAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_tracker.GetCurrentBudget());
    }

    /// <summary>
    /// Feeds a bandwidth measurement to the budget tracker, allowing subsequent fidelity
    /// decisions to reflect current network conditions.
    /// </summary>
    /// <param name="currentBandwidthBps">The measured bandwidth in bytes per second.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task UpdateBandwidthAsync(long currentBandwidthBps, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _tracker.UpdateBandwidth(currentBandwidthBps);
        IncrementCounter("bandwidth_updates");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Configures a new fidelity policy, replacing the current policy and recreating
    /// the policy engine with the new settings.
    /// </summary>
    /// <param name="policy">The new fidelity policy to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="policy"/> is null.</exception>
    public Task SetPolicyAsync(FidelityPolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ct.ThrowIfCancellationRequested();

        _currentPolicy = policy;
        _policyEngine = new FidelityPolicyEngine(policy);
        IncrementCounter("policy_updates");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Applies budget-aware degradation in three tiers based on remaining capacity:
    /// <list type="bullet">
    ///   <item><b>&gt;50% remaining:</b> Use policy-adjusted fidelity as-is.</item>
    ///   <item><b>20-50% remaining:</b> Drop one fidelity level (unless policy prevents it).</item>
    ///   <item><b>&lt;20% remaining:</b> Drop to the minimum fidelity that policy allows.</item>
    /// </list>
    /// </summary>
    private static SyncFidelity ApplyBudgetDegradation(
        SyncFidelity policyAdjusted,
        double remainingCapacityPercent,
        SemanticClassification classification,
        FidelityPolicyEngine policyEngine)
    {
        if (remainingCapacityPercent > 50.0)
        {
            // Tier 1: Plenty of budget -- use policy-adjusted fidelity
            return policyAdjusted;
        }

        if (remainingCapacityPercent >= 20.0)
        {
            // Tier 2: Budget under pressure -- drop one fidelity level
            SyncFidelity degraded = DropOneFidelityLevel(policyAdjusted);
            // Re-apply policy to ensure we don't violate minimums
            return policyEngine.ApplyPolicy(classification, degraded);
        }

        // Tier 3: Budget nearly exhausted -- drop to minimum policy allows
        SyncFidelity lowestAllowed = policyEngine.ApplyPolicy(classification, SyncFidelity.Metadata);
        return lowestAllowed;
    }

    /// <summary>
    /// Drops one fidelity level (e.g., Full -> Detailed, Detailed -> Standard).
    /// Cannot go below <see cref="SyncFidelity.Metadata"/>.
    /// </summary>
    private static SyncFidelity DropOneFidelityLevel(SyncFidelity current)
    {
        int next = (int)current + 1;
        int maxValue = (int)SyncFidelity.Metadata;
        return (SyncFidelity)Math.Min(next, maxValue);
    }

    /// <summary>
    /// Estimates the sync payload size in bytes at the given fidelity level.
    /// </summary>
    private static long EstimateSize(long baseSize, SyncFidelity fidelity)
    {
        double ratio = FidelitySizeRatios.GetValueOrDefault(fidelity, 0.5);
        return (long)(baseSize * ratio);
    }

    /// <summary>
    /// Determines the decision reason based on how the fidelity was adjusted.
    /// </summary>
    private static SyncDecisionReason DetermineReason(
        SemanticClassification classification,
        SyncFidelity bandwidthFidelity,
        SyncFidelity finalFidelity)
    {
        // If fidelity was upgraded from bandwidth suggestion, it was a policy override
        if ((int)finalFidelity < (int)bandwidthFidelity)
        {
            return classification.Importance == SemanticImportance.Critical
                ? SyncDecisionReason.HighImportance
                : SyncDecisionReason.PolicyOverride;
        }

        // If fidelity was degraded beyond bandwidth suggestion, bandwidth was the constraint
        if ((int)finalFidelity > (int)bandwidthFidelity)
        {
            return SyncDecisionReason.BandwidthConstrained;
        }

        // Fidelity matches bandwidth suggestion
        return classification.Importance <= SemanticImportance.High
            ? SyncDecisionReason.HighImportance
            : SyncDecisionReason.BandwidthConstrained;
    }

    /// <summary>
    /// Creates the default fidelity policy with sensible production defaults.
    /// </summary>
    private static FidelityPolicy CreateDefaultPolicy()
    {
        return new FidelityPolicy(
            MinFidelityForCritical: SyncFidelity.Standard,
            DefaultFidelity: SyncFidelity.Standard,
            BandwidthThresholds: new Dictionary<long, SyncFidelity>
            {
                [100_000_000] = SyncFidelity.Full,       // 100 MB/s -> Full fidelity
                [10_000_000] = SyncFidelity.Detailed,    // 10 MB/s  -> Detailed
                [1_000_000] = SyncFidelity.Standard,     // 1 MB/s   -> Standard
                [100_000] = SyncFidelity.Summarized,     // 100 KB/s -> Summarized
                [0] = SyncFidelity.Metadata               // 0        -> Metadata only
            },
            MaxDeferDuration: TimeSpan.FromMinutes(15));
    }
}
