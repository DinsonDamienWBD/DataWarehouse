using System;
using System.Collections.Generic;
using System.Linq;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.SemanticSync;

namespace DataWarehouse.Plugins.SemanticSync.Strategies.Fidelity;

/// <summary>
/// Policy enforcement engine that applies fidelity constraints based on semantic classification,
/// compliance requirements, and bandwidth thresholds. Ensures critical and compliance-tagged data
/// never drops below configured minimum fidelity levels regardless of bandwidth pressure.
/// </summary>
/// <remarks>
/// <para>
/// The engine applies a layered policy model:
/// </para>
/// <list type="number">
///   <item>Critical importance data enforces <see cref="FidelityPolicy.MinFidelityForCritical"/>.</item>
///   <item>Compliance/audit tagged data enforces minimum <see cref="SyncFidelity.Standard"/>.</item>
///   <item>Security/encryption tagged data enforces minimum <see cref="SyncFidelity.Detailed"/>.</item>
///   <item>All other data uses the lower of proposed fidelity or <see cref="FidelityPolicy.DefaultFidelity"/>.</item>
///   <item>Policy never upgrades fidelity above the proposed level (it only constrains downward degradation).</item>
/// </list>
/// <para>
/// Bandwidth threshold lookup scans the <see cref="FidelityPolicy.BandwidthThresholds"/> dictionary
/// in descending order, returning the fidelity for the first threshold the measured bandwidth exceeds.
/// If bandwidth is below all thresholds, <see cref="SyncFidelity.Metadata"/> is returned.
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic conflict resolution")]
internal sealed class FidelityPolicyEngine
{
    private readonly FidelityPolicy _policy;

    /// <summary>
    /// Bandwidth thresholds sorted in descending order for efficient lookup.
    /// The first threshold the current bandwidth exceeds determines the fidelity level.
    /// </summary>
    private readonly IReadOnlyList<KeyValuePair<long, SyncFidelity>> _sortedThresholds;

    /// <summary>
    /// Initializes a new instance of the <see cref="FidelityPolicyEngine"/> class
    /// with the specified fidelity policy.
    /// </summary>
    /// <param name="policy">The fidelity policy to enforce. Must not be null.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="policy"/> is null.</exception>
    public FidelityPolicyEngine(FidelityPolicy policy)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _sortedThresholds = policy.BandwidthThresholds
            .OrderByDescending(kvp => kvp.Key)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Applies policy constraints to a proposed fidelity level based on the data's semantic classification.
    /// </summary>
    /// <param name="classification">The semantic classification of the data item.</param>
    /// <param name="proposedFidelity">The fidelity level proposed by the bandwidth-based calculation.</param>
    /// <returns>
    /// The policy-adjusted fidelity level. This will never be higher than <paramref name="proposedFidelity"/>
    /// (policy only constrains downward degradation, not upgrades), but may be higher than what bandwidth
    /// alone would suggest if the data is classified as critical, compliance, or security.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="classification"/> is null.</exception>
    /// <remarks>
    /// <para>Policy rules are applied in priority order:</para>
    /// <list type="number">
    ///   <item>Critical importance: enforce <see cref="FidelityPolicy.MinFidelityForCritical"/> minimum.</item>
    ///   <item>Compliance/audit tags: enforce <see cref="SyncFidelity.Standard"/> minimum.</item>
    ///   <item>Security/encryption tags: enforce <see cref="SyncFidelity.Detailed"/> minimum.</item>
    ///   <item>Default: use the lower fidelity of proposed vs. <see cref="FidelityPolicy.DefaultFidelity"/>.</item>
    /// </list>
    /// <para>
    /// "Lower fidelity" means a higher enum value (Full=0 is highest fidelity, Metadata=4 is lowest).
    /// The final result is clamped so it never exceeds the proposed fidelity (lower enum value).
    /// </para>
    /// </remarks>
    public SyncFidelity ApplyPolicy(SemanticClassification classification, SyncFidelity proposedFidelity)
    {
        ArgumentNullException.ThrowIfNull(classification);

        SyncFidelity minimumFidelity;

        // Rule 1: Critical importance enforces configured minimum
        if (classification.Importance == SemanticImportance.Critical)
        {
            minimumFidelity = _policy.MinFidelityForCritical;
        }
        // Rule 2: Compliance/audit tagged data enforces Standard minimum
        else if (HasTag(classification, "compliance") || HasTag(classification, "audit"))
        {
            minimumFidelity = SyncFidelity.Standard;
        }
        // Rule 3: Security/encryption tagged data enforces Detailed minimum
        else if (HasTag(classification, "security") || HasTag(classification, "encryption"))
        {
            minimumFidelity = SyncFidelity.Detailed;
        }
        // Rule 4: Default - use the lower fidelity of proposed vs. default (budget-conscious)
        else
        {
            // Higher enum value = lower fidelity. Use whichever is lower fidelity (higher value).
            minimumFidelity = (SyncFidelity)Math.Max((int)proposedFidelity, (int)_policy.DefaultFidelity);
            // Rule 5: Never return higher than proposed
            return minimumFidelity;
        }

        // Rule 5: Policy can only constrain downward degradation, not upgrade.
        // "Higher fidelity" = lower enum value. If proposed is already at or above the minimum,
        // return proposed. If proposed is below minimum (higher enum value), enforce minimum.
        // But never go above (lower enum value than) proposed.
        if ((int)proposedFidelity <= (int)minimumFidelity)
        {
            // Proposed is at or above minimum -- return proposed as-is
            return proposedFidelity;
        }
        else
        {
            // Proposed is below minimum -- enforce the minimum (upgrade to minimum)
            return minimumFidelity;
        }
    }

    /// <summary>
    /// Determines the appropriate fidelity level based solely on available bandwidth
    /// by looking up the first matching threshold in the policy's bandwidth thresholds.
    /// </summary>
    /// <param name="bandwidthBps">The currently available bandwidth in bytes per second.</param>
    /// <returns>
    /// The <see cref="SyncFidelity"/> level for the first threshold the bandwidth exceeds (sorted descending).
    /// Returns <see cref="SyncFidelity.Metadata"/> if bandwidth is below all configured thresholds.
    /// </returns>
    public SyncFidelity GetFidelityForBandwidth(long bandwidthBps)
    {
        foreach (var threshold in _sortedThresholds)
        {
            if (bandwidthBps >= threshold.Key)
            {
                return threshold.Value;
            }
        }

        return SyncFidelity.Metadata;
    }

    /// <summary>
    /// Determines whether a sync operation should be deferred based on budget pressure
    /// and data importance. Low-value data is deferred when the budget is nearly exhausted
    /// to preserve capacity for higher-importance items.
    /// </summary>
    /// <param name="classification">The semantic classification of the data item.</param>
    /// <param name="budget">The current bandwidth budget state.</param>
    /// <returns>
    /// True if the sync should be deferred (budget utilization exceeds 90% and importance
    /// is <see cref="SemanticImportance.Negligible"/> or <see cref="SemanticImportance.Low"/>);
    /// false otherwise.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="classification"/> or <paramref name="budget"/> is null.
    /// </exception>
    public bool ShouldDefer(SemanticClassification classification, FidelityBudget budget)
    {
        ArgumentNullException.ThrowIfNull(classification);
        ArgumentNullException.ThrowIfNull(budget);

        return budget.BudgetUtilizationPercent > 90.0
            && (classification.Importance == SemanticImportance.Negligible
                || classification.Importance == SemanticImportance.Low);
    }

    /// <summary>
    /// Checks whether the classification's semantic tags contain the specified tag (case-insensitive).
    /// </summary>
    private static bool HasTag(SemanticClassification classification, string tag)
    {
        if (classification.SemanticTags is null || classification.SemanticTags.Length == 0)
            return false;

        foreach (var t in classification.SemanticTags)
        {
            if (string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
