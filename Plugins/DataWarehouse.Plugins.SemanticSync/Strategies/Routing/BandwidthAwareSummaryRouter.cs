using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.SemanticSync;

namespace DataWarehouse.Plugins.SemanticSync.Strategies.Routing;

/// <summary>
/// Bandwidth-aware routing strategy that decides whether to sync data as raw, summarized,
/// or metadata-only based on the data's semantic importance and available network bandwidth.
/// </summary>
/// <remarks>
/// <para>
/// The router uses a 5x4 decision matrix mapping <see cref="SemanticImportance"/> and bandwidth tier
/// to a <see cref="SyncFidelity"/> level. Critical data always syncs at Full or near-Full fidelity
/// even on constrained links. Negligible data is deferred or reduced to metadata on low-bandwidth channels.
/// </para>
/// <para>
/// Bandwidth tiers are derived from <see cref="FidelityBudget.AvailableBandwidthBps"/>:
/// High (&gt;100 Mbps), Medium (10-100 Mbps), Low (1-10 Mbps), VeryLow (&lt;1 Mbps).
/// All thresholds are in bytes per second.
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Summary routing")]
public sealed class BandwidthAwareSummaryRouter : SemanticSyncStrategyBase, ISummaryRouter
{
    private readonly SummaryGenerator _summaryGenerator;
    private readonly FidelityDownsampler _downsampler;

    /// <summary>
    /// Bandwidth tier thresholds in bytes per second.
    /// High: &gt;12.5 MB/s (100 Mbps), Medium: 1.25-12.5 MB/s (10-100 Mbps),
    /// Low: 125 KB-1.25 MB/s (1-10 Mbps), VeryLow: &lt;125 KB/s (&lt;1 Mbps).
    /// </summary>
    private const long HighBandwidthThreshold = 12_500_000;   // 100 Mbps in bytes/sec
    private const long MediumBandwidthThreshold = 1_250_000;  // 10 Mbps in bytes/sec
    private const long LowBandwidthThreshold = 125_000;       // 1 Mbps in bytes/sec

    /// <summary>
    /// Duration to defer negligible data on very-low bandwidth links before re-evaluation.
    /// </summary>
    private static readonly TimeSpan DeferDuration = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The 5x4 decision matrix: (Importance, BandwidthTier) -> SyncFidelity.
    /// Covers all 20 combinations of importance and bandwidth tier.
    /// </summary>
    private static readonly Dictionary<(SemanticImportance Importance, BandwidthTier Tier), SyncFidelity> DecisionMatrix = new()
    {
        // Critical row: always Full or near-Full
        [(SemanticImportance.Critical, BandwidthTier.High)]     = SyncFidelity.Full,
        [(SemanticImportance.Critical, BandwidthTier.Medium)]   = SyncFidelity.Full,
        [(SemanticImportance.Critical, BandwidthTier.Low)]      = SyncFidelity.Detailed,
        [(SemanticImportance.Critical, BandwidthTier.VeryLow)]  = SyncFidelity.Standard,

        // High row
        [(SemanticImportance.High, BandwidthTier.High)]     = SyncFidelity.Full,
        [(SemanticImportance.High, BandwidthTier.Medium)]   = SyncFidelity.Detailed,
        [(SemanticImportance.High, BandwidthTier.Low)]      = SyncFidelity.Standard,
        [(SemanticImportance.High, BandwidthTier.VeryLow)]  = SyncFidelity.Summarized,

        // Normal row
        [(SemanticImportance.Normal, BandwidthTier.High)]     = SyncFidelity.Detailed,
        [(SemanticImportance.Normal, BandwidthTier.Medium)]   = SyncFidelity.Standard,
        [(SemanticImportance.Normal, BandwidthTier.Low)]      = SyncFidelity.Summarized,
        [(SemanticImportance.Normal, BandwidthTier.VeryLow)]  = SyncFidelity.Summarized,

        // Low row
        [(SemanticImportance.Low, BandwidthTier.High)]     = SyncFidelity.Standard,
        [(SemanticImportance.Low, BandwidthTier.Medium)]   = SyncFidelity.Summarized,
        [(SemanticImportance.Low, BandwidthTier.Low)]      = SyncFidelity.Metadata,
        [(SemanticImportance.Low, BandwidthTier.VeryLow)]  = SyncFidelity.Metadata,

        // Negligible row
        [(SemanticImportance.Negligible, BandwidthTier.High)]     = SyncFidelity.Summarized,
        [(SemanticImportance.Negligible, BandwidthTier.Medium)]   = SyncFidelity.Metadata,
        [(SemanticImportance.Negligible, BandwidthTier.Low)]      = SyncFidelity.Metadata,
        [(SemanticImportance.Negligible, BandwidthTier.VeryLow)]  = SyncFidelity.Metadata, // with defer
    };

    /// <inheritdoc />
    public override string StrategyId => "summary-router-bandwidth-aware";

    /// <inheritdoc />
    public override string Name => "Bandwidth-Aware Summary Router";

    /// <inheritdoc />
    public override string Description =>
        "Routes sync decisions between raw, summarized, and metadata-only based on semantic importance and available bandwidth.";

    /// <inheritdoc />
    public override string SemanticDomain => "general";

    /// <summary>
    /// Gets the capabilities of this summary router.
    /// </summary>
    public SummaryRouterCapabilities Capabilities { get; } = new(
        SupportedFidelities: new[]
        {
            SyncFidelity.Full,
            SyncFidelity.Detailed,
            SyncFidelity.Standard,
            SyncFidelity.Summarized,
            SyncFidelity.Metadata
        },
        MaxSummarySizeBytes: 10 * 1024 * 1024, // 10 MB
        SupportsReconstruction: true);

    /// <summary>
    /// Initializes a new instance of <see cref="BandwidthAwareSummaryRouter"/>.
    /// </summary>
    /// <param name="summaryGenerator">The summary generator for producing AI/extraction summaries.</param>
    /// <param name="downsampler">The fidelity downsampler for progressive field stripping.</param>
    internal BandwidthAwareSummaryRouter(SummaryGenerator summaryGenerator, FidelityDownsampler downsampler)
    {
        ArgumentNullException.ThrowIfNull(summaryGenerator);
        ArgumentNullException.ThrowIfNull(downsampler);

        _summaryGenerator = summaryGenerator;
        _downsampler = downsampler;
    }

    /// <summary>
    /// Decides the sync fidelity for a data item based on its semantic classification
    /// and the current bandwidth budget using the importance-bandwidth decision matrix.
    /// </summary>
    /// <param name="dataId">Unique identifier of the data item.</param>
    /// <param name="classification">Semantic classification including importance level.</param>
    /// <param name="budget">Current bandwidth budget state.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A routing decision specifying fidelity level, reason, estimated size, and optional deferral.</returns>
    public Task<SyncDecision> RouteAsync(
        string dataId,
        SemanticClassification classification,
        FidelityBudget budget,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataId);
        ValidateClassification(classification);
        ArgumentNullException.ThrowIfNull(budget);

        var tier = ClassifyBandwidthTier(budget.AvailableBandwidthBps);
        var key = (classification.Importance, tier);

        SyncFidelity fidelity = DecisionMatrix.TryGetValue(key, out var resolved)
            ? resolved
            : SyncFidelity.Standard; // safe default for any unmapped combination

        // Determine the sync decision reason
        SyncDecisionReason reason = DetermineReason(classification.Importance, tier);

        // Compute estimated size using target ratios
        // We assume a notional "raw size" derived from pending budget context.
        // Since we don't have the actual raw data here, estimate based on budget distribution.
        long estimatedSize = EstimateSizeForFidelity(fidelity, budget);

        // Determine if summary generation is required
        bool requiresSummary = fidelity is SyncFidelity.Summarized or SyncFidelity.Metadata;

        // Check for defer condition: Negligible importance + VeryLow bandwidth
        TimeSpan? deferUntil = null;
        if (classification.Importance == SemanticImportance.Negligible && tier == BandwidthTier.VeryLow)
        {
            deferUntil = DeferDuration;
        }

        var decision = new SyncDecision(
            DataId: dataId,
            Fidelity: fidelity,
            Reason: reason,
            RequiresSummary: requiresSummary,
            EstimatedSizeBytes: estimatedSize,
            DeferUntil: deferUntil);

        return Task.FromResult(decision);
    }

    /// <summary>
    /// Generates an AI-driven or extraction-based summary of the raw data at the target fidelity.
    /// Delegates to the internal <see cref="SummaryGenerator"/>.
    /// </summary>
    public Task<DataSummary> GenerateSummaryAsync(
        string dataId,
        ReadOnlyMemory<byte> rawData,
        SyncFidelity targetFidelity,
        CancellationToken ct = default)
    {
        return _summaryGenerator.GenerateAsync(dataId, rawData, targetFidelity, ct);
    }

    /// <summary>
    /// Attempts best-effort reconstruction of raw data from a summary.
    /// Delegates to the internal <see cref="SummaryGenerator"/>.
    /// </summary>
    public Task<ReadOnlyMemory<byte>> ReconstructFromSummaryAsync(DataSummary summary, CancellationToken ct = default)
    {
        return _summaryGenerator.ReconstructAsync(summary, ct);
    }

    /// <summary>
    /// Classifies the available bandwidth into a discrete tier for decision matrix lookup.
    /// </summary>
    private static BandwidthTier ClassifyBandwidthTier(long availableBandwidthBps)
    {
        if (availableBandwidthBps >= HighBandwidthThreshold) return BandwidthTier.High;
        if (availableBandwidthBps >= MediumBandwidthThreshold) return BandwidthTier.Medium;
        if (availableBandwidthBps >= LowBandwidthThreshold) return BandwidthTier.Low;
        return BandwidthTier.VeryLow;
    }

    /// <summary>
    /// Determines the primary reason for the sync decision based on importance and bandwidth tier.
    /// </summary>
    private static SyncDecisionReason DetermineReason(SemanticImportance importance, BandwidthTier tier)
    {
        // If importance is the dominant factor
        if (importance <= SemanticImportance.High && tier <= BandwidthTier.Medium)
            return SyncDecisionReason.HighImportance;

        if (importance >= SemanticImportance.Low)
            return SyncDecisionReason.LowImportance;

        // Otherwise bandwidth is the constraining factor
        return SyncDecisionReason.BandwidthConstrained;
    }

    /// <summary>
    /// Estimates the sync payload size for a given fidelity level using the downsampler's target ratios.
    /// </summary>
    private static long EstimateSizeForFidelity(SyncFidelity fidelity, FidelityBudget budget)
    {
        // Use a heuristic: average data size based on pending items and bandwidth
        // If no pending items, use a 1 MB baseline for estimation
        const long BaselineDataSize = 1_048_576; // 1 MB

        long estimatedRawSize = budget.PendingSyncCount > 0
            ? budget.AvailableBandwidthBps / Math.Max(1, budget.PendingSyncCount)
            : BaselineDataSize;

        double ratio = FidelityDownsampler.TargetRatios.TryGetValue(fidelity, out var r) ? r : 1.0;

        return Math.Max(1, (long)(estimatedRawSize * ratio));
    }

    /// <summary>
    /// Discrete bandwidth tier for decision matrix lookup.
    /// </summary>
    private enum BandwidthTier
    {
        /// <summary>Greater than 100 Mbps (12.5 MB/s).</summary>
        High = 0,

        /// <summary>10-100 Mbps (1.25-12.5 MB/s).</summary>
        Medium = 1,

        /// <summary>1-10 Mbps (125 KB-1.25 MB/s).</summary>
        Low = 2,

        /// <summary>Less than 1 Mbps (less than 125 KB/s).</summary>
        VeryLow = 3
    }
}
