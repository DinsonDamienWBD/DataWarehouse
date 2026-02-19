using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Storage.Placement;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.AiEnhanced;

/// <summary>
/// Extends data management with gravity-aware placement intelligence.
/// Integrates zero-gravity storage gravity scores into the data management
/// lifecycle decisions: tiering, archiving, deletion, and migration.
/// </summary>
/// <remarks>
/// <para>
/// This class bridges the gap between the zero-gravity storage optimizer
/// (which computes gravity scores) and the data management plugin (which
/// makes lifecycle decisions). All communication is through SDK interfaces;
/// no direct plugin-to-plugin references.
/// </para>
/// <para>
/// Subscribes to:
/// <list type="bullet">
///   <item><description>storage.zerogravity.gravity.scored - uses scores for tiering decisions</description></item>
///   <item><description>storage.zerogravity.cost.report - uses cost data for budget-aware decisions</description></item>
/// </list>
/// </para>
/// <para>
/// Publishes:
/// <list type="bullet">
///   <item><description>datamanagement.gravity.tiering - tier transition recommendations based on gravity</description></item>
/// </list>
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-gravity message bus wiring")]
public sealed class GravityAwarePlacementIntegration
{
    private readonly IPlacementOptimizer? _optimizer;
    private readonly GravityScoringWeights _weights;

    /// <summary>
    /// Initializes a new instance of the <see cref="GravityAwarePlacementIntegration"/> class.
    /// </summary>
    /// <param name="optimizer">
    /// The placement optimizer providing gravity scoring.
    /// If null, tiering recommendations return <see cref="TieringAction.None"/>.
    /// </param>
    /// <param name="weights">
    /// Custom gravity scoring weights. Defaults to <see cref="GravityScoringWeights.Default"/>.
    /// </param>
    public GravityAwarePlacementIntegration(
        IPlacementOptimizer? optimizer = null,
        GravityScoringWeights? weights = null)
    {
        _optimizer = optimizer;
        _weights = weights ?? GravityScoringWeights.Default;
    }

    /// <summary>
    /// Gets the gravity scoring weights used by this integration.
    /// </summary>
    public GravityScoringWeights Weights => _weights;

    /// <summary>
    /// Computes a tiering recommendation based on gravity score.
    /// Low gravity + low access = candidate for cold/archive tier.
    /// High gravity + high access = must stay on hot tier.
    /// </summary>
    /// <param name="objectKey">The key of the object to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tiering recommendation with action, score, and human-readable reason.</returns>
    public async Task<TieringRecommendation> ComputeTieringRecommendationAsync(
        string objectKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        if (_optimizer == null)
        {
            return new TieringRecommendation
            {
                ObjectKey = objectKey,
                RecommendedAction = TieringAction.None,
                Reason = "Gravity optimizer not configured"
            };
        }

        var gravity = await _optimizer.ComputeGravityAsync(objectKey, ct);

        TieringAction action;
        string reason;

        if (gravity.CompositeScore < 0.2 && gravity.AccessFrequency < 1.0)
        {
            action = TieringAction.MoveToArchive;
            reason = $"Very low gravity ({gravity.CompositeScore:F2}) and access ({gravity.AccessFrequency:F1}/hr). Safe to archive.";
        }
        else if (gravity.CompositeScore < 0.4 && gravity.AccessFrequency < 10.0)
        {
            action = TieringAction.MoveToCold;
            reason = $"Low gravity ({gravity.CompositeScore:F2}). Infrequent access suitable for cold tier.";
        }
        else if (gravity.CompositeScore > 0.8)
        {
            action = TieringAction.KeepOnHot;
            reason = $"High gravity ({gravity.CompositeScore:F2}). Data is actively used and colocated.";
        }
        else
        {
            action = TieringAction.None;
            reason = $"Medium gravity ({gravity.CompositeScore:F2}). Current placement is acceptable.";
        }

        return new TieringRecommendation
        {
            ObjectKey = objectKey,
            GravityScore = gravity.CompositeScore,
            AccessFrequency = gravity.AccessFrequency,
            RecommendedAction = action,
            Reason = reason
        };
    }

    /// <summary>
    /// Batch analysis: computes tiering recommendations for multiple objects concurrently.
    /// </summary>
    /// <param name="objectKeys">The keys of the objects to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tiering recommendations for all requested objects.</returns>
    public async Task<IReadOnlyList<TieringRecommendation>> ComputeBatchTieringAsync(
        IReadOnlyList<string> objectKeys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(objectKeys);

        if (objectKeys.Count == 0)
            return Array.Empty<TieringRecommendation>();

        var tasks = objectKeys.Select(key => ComputeTieringRecommendationAsync(key, ct));
        return (await Task.WhenAll(tasks)).ToList();
    }

    /// <summary>
    /// Determines if an object should be protected from deletion based on its gravity score.
    /// Objects with high gravity or compliance constraints are protected.
    /// </summary>
    /// <param name="objectKey">The key of the object to evaluate.</param>
    /// <param name="protectionThreshold">Minimum composite gravity score for protection (default 0.7).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the object should be protected from deletion.</returns>
    public async Task<bool> ShouldProtectFromDeletionAsync(
        string objectKey, double protectionThreshold = 0.7, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        if (_optimizer == null)
            return false;

        var gravity = await _optimizer.ComputeGravityAsync(objectKey, ct);
        return gravity.CompositeScore >= protectionThreshold || gravity.ComplianceWeight > 0;
    }

    /// <summary>
    /// Computes a migration urgency score for an object. Higher values indicate the object
    /// would benefit more from being relocated according to its gravity profile.
    /// </summary>
    /// <param name="objectKey">The key of the object to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Migration urgency from 0.0 (no migration needed) to 1.0 (critical migration recommended).
    /// Returns 0.0 if the optimizer is not configured.
    /// </returns>
    public async Task<double> ComputeMigrationUrgencyAsync(
        string objectKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        if (_optimizer == null)
            return 0.0;

        var gravity = await _optimizer.ComputeGravityAsync(objectKey, ct);

        // Low gravity with high access = data is in the wrong place (high urgency)
        // High gravity with low access = data is fine where it is (low urgency)
        // High gravity with high access = data is in the right place (low urgency)
        if (gravity.CompositeScore > 0.7)
            return 0.0; // Already well-placed

        if (gravity.AccessFrequency > 50.0 && gravity.CompositeScore < 0.3)
            return 1.0; // Heavily accessed but poorly placed

        // Linear interpolation for middle ranges
        return Math.Clamp(1.0 - gravity.CompositeScore, 0.0, 1.0) *
               Math.Min(gravity.AccessFrequency / 100.0, 1.0);
    }
}

/// <summary>
/// Tiering action recommendations produced by gravity-aware analysis.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-gravity message bus wiring")]
public enum TieringAction
{
    /// <summary>No change recommended.</summary>
    None,

    /// <summary>Move to hot storage tier.</summary>
    MoveToHot,

    /// <summary>Move to warm storage tier.</summary>
    MoveToWarm,

    /// <summary>Move to cold storage tier.</summary>
    MoveToCold,

    /// <summary>Move to archive storage tier.</summary>
    MoveToArchive,

    /// <summary>Keep on hot storage tier (confirmed correct placement).</summary>
    KeepOnHot,

    /// <summary>Delete the object (gravity indicates it is no longer needed).</summary>
    Delete
}

/// <summary>
/// Tiering recommendation based on gravity analysis.
/// Provides the gravity score, access frequency, recommended action, and a human-readable reason.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-gravity message bus wiring")]
public sealed record TieringRecommendation
{
    /// <summary>Object key that was evaluated.</summary>
    public string ObjectKey { get; init; } = "";

    /// <summary>Computed gravity score (0.0-1.0).</summary>
    public double GravityScore { get; init; }

    /// <summary>Access frequency in operations per hour.</summary>
    public double AccessFrequency { get; init; }

    /// <summary>Recommended tiering action.</summary>
    public TieringAction RecommendedAction { get; init; }

    /// <summary>Human-readable explanation of the recommendation.</summary>
    public string Reason { get; init; } = "";
}
