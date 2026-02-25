using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.SemanticSync;

/// <summary>
/// Interface for routing sync operations between summary and raw data paths.
/// Implementations decide whether to sync full raw data or an AI-generated summary,
/// and can generate summaries and attempt reconstruction from summaries.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync types")]
public interface ISummaryRouter
{
    /// <summary>
    /// Decides whether to sync raw data or a summary for the given data item,
    /// based on its semantic classification and the current bandwidth budget.
    /// </summary>
    /// <param name="dataId">Unique identifier of the data item.</param>
    /// <param name="classification">The semantic classification of the data item.</param>
    /// <param name="budget">The current bandwidth budget state.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="SyncDecision"/> indicating the routing choice.</returns>
    Task<SyncDecision> RouteAsync(
        string dataId,
        SemanticClassification classification,
        FidelityBudget budget,
        CancellationToken ct = default);

    /// <summary>
    /// Generates an AI-driven summary of the raw data at the specified target fidelity level.
    /// </summary>
    /// <param name="dataId">Unique identifier of the data item.</param>
    /// <param name="rawData">The full raw data payload to summarize.</param>
    /// <param name="targetFidelity">The target fidelity level for the summary.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="DataSummary"/> containing the summary text, embedding, and metadata.</returns>
    Task<DataSummary> GenerateSummaryAsync(
        string dataId,
        ReadOnlyMemory<byte> rawData,
        SyncFidelity targetFidelity,
        CancellationToken ct = default);

    /// <summary>
    /// Attempts best-effort reconstruction of raw data from a summary representation.
    /// The result may be lossy depending on the summary fidelity level.
    /// </summary>
    /// <param name="summary">The summary to reconstruct from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A best-effort reconstruction of the original data payload.</returns>
    Task<ReadOnlyMemory<byte>> ReconstructFromSummaryAsync(
        DataSummary summary,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the capabilities of this summary router implementation.
    /// </summary>
    SummaryRouterCapabilities Capabilities { get; }
}

/// <summary>
/// Describes the capabilities of an <see cref="ISummaryRouter"/> implementation.
/// </summary>
/// <param name="SupportedFidelities">The fidelity levels this router can generate summaries for.</param>
/// <param name="MaxSummarySizeBytes">Maximum size of a generated summary in bytes.</param>
/// <param name="SupportsReconstruction">Whether this router supports reconstructing data from summaries.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync types")]
public sealed record SummaryRouterCapabilities(
    SyncFidelity[] SupportedFidelities,
    long MaxSummarySizeBytes,
    bool SupportsReconstruction);
