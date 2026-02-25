using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.SemanticSync;

/// <summary>
/// Interface for bandwidth-aware fidelity control. Implementations decide what
/// fidelity level to use for each sync operation based on semantic classification,
/// current bandwidth budget, and configured policies.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync types")]
public interface ISyncFidelityController
{
    /// <summary>
    /// Decides the appropriate sync fidelity for a data item given its semantic classification
    /// and the current bandwidth budget.
    /// </summary>
    /// <param name="classification">The semantic classification of the data item.</param>
    /// <param name="budget">The current bandwidth budget state.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="SyncDecision"/> describing the chosen fidelity, reason, and timing.</returns>
    Task<SyncDecision> DecideFidelityAsync(
        SemanticClassification classification,
        FidelityBudget budget,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the current bandwidth budget state, including available bandwidth,
    /// pending sync count, and fidelity distribution.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current <see cref="FidelityBudget"/>.</returns>
    Task<FidelityBudget> GetCurrentBudgetAsync(CancellationToken ct = default);

    /// <summary>
    /// Feeds a bandwidth measurement to the controller, allowing it to adjust
    /// fidelity decisions based on current network conditions.
    /// </summary>
    /// <param name="currentBandwidthBps">The measured bandwidth in bytes per second.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateBandwidthAsync(long currentBandwidthBps, CancellationToken ct = default);

    /// <summary>
    /// Configures the fidelity policy that governs how classifications map to fidelity levels.
    /// </summary>
    /// <param name="policy">The fidelity policy to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SetPolicyAsync(FidelityPolicy policy, CancellationToken ct = default);
}

/// <summary>
/// Policy governing how semantic classifications and bandwidth thresholds map to sync fidelity levels.
/// </summary>
/// <param name="MinFidelityForCritical">The minimum fidelity level for data classified as <see cref="SemanticImportance.Critical"/>.</param>
/// <param name="DefaultFidelity">The default fidelity level when no specific rule applies.</param>
/// <param name="BandwidthThresholds">Map of bandwidth thresholds (bytes/sec) to fidelity levels. When available bandwidth drops below a threshold, the corresponding fidelity is used.</param>
/// <param name="MaxDeferDuration">Maximum duration that a sync operation can be deferred before it must be executed regardless of bandwidth.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync types")]
public sealed record FidelityPolicy(
    SyncFidelity MinFidelityForCritical,
    SyncFidelity DefaultFidelity,
    Dictionary<long, SyncFidelity> BandwidthThresholds,
    TimeSpan MaxDeferDuration);
