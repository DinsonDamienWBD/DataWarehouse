using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.SemanticSync;

/// <summary>
/// Interface for AI-driven semantic classification of data items.
/// Implementations classify data by semantic importance, compute similarity scores,
/// and support batch classification for throughput optimization.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync types")]
public interface ISemanticClassifier
{
    /// <summary>
    /// Classifies a single data item by semantic importance.
    /// </summary>
    /// <param name="data">The raw data payload to classify.</param>
    /// <param name="metadata">Optional metadata key-value pairs to assist classification (e.g., content-type, source).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="SemanticClassification"/> describing the data's importance, confidence, and semantic tags.</returns>
    Task<SemanticClassification> ClassifyAsync(
        ReadOnlyMemory<byte> data,
        IDictionary<string, string>? metadata = null,
        CancellationToken ct = default);

    /// <summary>
    /// Classifies a batch of data items as an asynchronous stream for throughput optimization.
    /// </summary>
    /// <param name="items">An asynchronous stream of (DataId, Data) tuples to classify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An asynchronous stream of <see cref="SemanticClassification"/> results, one per input item.</returns>
    IAsyncEnumerable<SemanticClassification> ClassifyBatchAsync(
        IAsyncEnumerable<(string DataId, ReadOnlyMemory<byte> Data)> items,
        CancellationToken ct = default);

    /// <summary>
    /// Computes the semantic similarity between two data payloads.
    /// </summary>
    /// <param name="data1">The first data payload.</param>
    /// <param name="data2">The second data payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A similarity score between 0.0 (completely different) and 1.0 (semantically identical).</returns>
    Task<double> ComputeSemanticSimilarityAsync(
        ReadOnlyMemory<byte> data1,
        ReadOnlyMemory<byte> data2,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the capabilities of this semantic classifier implementation.
    /// </summary>
    SemanticClassifierCapabilities Capabilities { get; }
}

/// <summary>
/// Describes the capabilities of a <see cref="ISemanticClassifier"/> implementation.
/// </summary>
/// <param name="SupportsEmbeddings">Whether the classifier can produce vector embeddings.</param>
/// <param name="SupportsLocalInference">Whether the classifier can run inference locally without cloud connectivity.</param>
/// <param name="SupportsDomainHints">Whether the classifier accepts domain hints to improve classification accuracy.</param>
/// <param name="MaxBatchSize">Maximum number of items that can be classified in a single batch call.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync types")]
public sealed record SemanticClassifierCapabilities(
    bool SupportsEmbeddings,
    bool SupportsLocalInference,
    bool SupportsDomainHints,
    int MaxBatchSize);
