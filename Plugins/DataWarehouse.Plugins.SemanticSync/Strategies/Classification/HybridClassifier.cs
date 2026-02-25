// <copyright file="HybridClassifier.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.SemanticSync;

namespace DataWarehouse.Plugins.SemanticSync.Strategies.Classification;

/// <summary>
/// Hybrid semantic classifier that combines embedding-based and rule-based classification
/// using configurable weighted fusion. This is the DEFAULT classifier for production deployments
/// because it gracefully degrades from AI-powered to rule-based-only when the AI provider
/// is unavailable, which is critical for edge and air-gapped environments.
/// </summary>
/// <remarks>
/// <para>
/// Weighted fusion works by running both classifiers in parallel, converting their importance
/// levels to ordinal values (Critical=4 through Negligible=0), computing a weighted average,
/// and mapping back to the nearest enum value. Confidence scores are similarly combined.
/// </para>
/// <para>
/// When the embedding classifier throws (AI unavailable), the hybrid classifier seamlessly
/// falls back to rule-based-only classification with the rule-based confidence score,
/// ensuring zero downtime across all deployment scenarios.
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic classification")]
public sealed class HybridClassifier : SemanticSyncStrategyBase, ISemanticClassifier
{
    private readonly ISemanticClassifier _embeddingClassifier;
    private readonly ISemanticClassifier _ruleBasedClassifier;
    private readonly double _embeddingWeight;

    /// <summary>
    /// Ordinal mapping for weighted averaging of importance levels.
    /// Critical=4, High=3, Normal=2, Low=1, Negligible=0.
    /// </summary>
    private static readonly Dictionary<SemanticImportance, int> ImportanceOrdinals = new()
    {
        [SemanticImportance.Critical] = 4,
        [SemanticImportance.High] = 3,
        [SemanticImportance.Normal] = 2,
        [SemanticImportance.Low] = 1,
        [SemanticImportance.Negligible] = 0
    };

    /// <summary>
    /// Reverse mapping from ordinal values back to importance levels.
    /// </summary>
    private static readonly SemanticImportance[] OrdinalToImportance =
    {
        SemanticImportance.Negligible, // 0
        SemanticImportance.Low,        // 1
        SemanticImportance.Normal,     // 2
        SemanticImportance.High,       // 3
        SemanticImportance.Critical    // 4
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="HybridClassifier"/> class.
    /// </summary>
    /// <param name="embeddingClassifier">The AI embedding-based classifier.</param>
    /// <param name="ruleBasedClassifier">The metadata heuristic-based classifier.</param>
    /// <param name="embeddingWeight">
    /// Weight for the embedding classifier's result (0.0 to 1.0). Default is 0.7.
    /// The rule-based classifier gets weight (1.0 - embeddingWeight).
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="embeddingClassifier"/> or <paramref name="ruleBasedClassifier"/> is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="embeddingWeight"/> is not between 0.0 and 1.0.
    /// </exception>
    public HybridClassifier(
        ISemanticClassifier embeddingClassifier,
        ISemanticClassifier ruleBasedClassifier,
        double embeddingWeight = 0.7)
    {
        _embeddingClassifier = embeddingClassifier ?? throw new ArgumentNullException(nameof(embeddingClassifier));
        _ruleBasedClassifier = ruleBasedClassifier ?? throw new ArgumentNullException(nameof(ruleBasedClassifier));

        if (embeddingWeight < 0.0 || embeddingWeight > 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(embeddingWeight),
                embeddingWeight,
                "Embedding weight must be between 0.0 and 1.0.");
        }

        _embeddingWeight = embeddingWeight;
    }

    /// <inheritdoc/>
    public override string StrategyId => "semantic-classifier-hybrid";

    /// <inheritdoc/>
    public override string Name => "Hybrid Semantic Classifier";

    /// <inheritdoc/>
    public override string Description =>
        "Combines AI embedding-based and rule-based classification with configurable weighted fusion. " +
        "Gracefully degrades to rule-based-only when the AI provider is unavailable.";

    /// <inheritdoc/>
    public override string SemanticDomain => "universal";

    /// <inheritdoc/>
    public override bool SupportsLocalInference => true;

    /// <summary>
    /// Gets the combined capabilities of both underlying classifiers (union of capabilities).
    /// </summary>
    public SemanticClassifierCapabilities Capabilities => new(
        SupportsEmbeddings: _embeddingClassifier.Capabilities.SupportsEmbeddings ||
                            _ruleBasedClassifier.Capabilities.SupportsEmbeddings,
        SupportsLocalInference: _embeddingClassifier.Capabilities.SupportsLocalInference ||
                                _ruleBasedClassifier.Capabilities.SupportsLocalInference,
        SupportsDomainHints: _embeddingClassifier.Capabilities.SupportsDomainHints ||
                             _ruleBasedClassifier.Capabilities.SupportsDomainHints,
        MaxBatchSize: Math.Max(
            _embeddingClassifier.Capabilities.MaxBatchSize,
            _ruleBasedClassifier.Capabilities.MaxBatchSize));

    /// <summary>
    /// Classifies a single data item by running both classifiers in parallel and fusing their
    /// results with configurable weighting. Falls back to rule-based-only if the embedding
    /// classifier fails (e.g., AI provider unavailable).
    /// </summary>
    /// <param name="data">The raw data payload to classify.</param>
    /// <param name="metadata">Optional metadata key-value pairs to assist classification.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A fused classification combining both classifier outputs.</returns>
    public async Task<SemanticClassification> ClassifyAsync(
        ReadOnlyMemory<byte> data,
        IDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        EnsureNotDisposed();

        // Run both classifiers in parallel
        var embeddingTask = _embeddingClassifier.ClassifyAsync(data, metadata, ct);
        var ruleTask = _ruleBasedClassifier.ClassifyAsync(data, metadata, ct);

        SemanticClassification? embeddingResult = null;
        SemanticClassification ruleResult;

        try
        {
            await Task.WhenAll(embeddingTask, ruleTask).ConfigureAwait(false);
            embeddingResult = embeddingTask.Result;
            ruleResult = ruleTask.Result;
        }
        catch
        {
            // If embedding classifier threw (AI unavailable), fall back to rule-based only
            if (embeddingTask.IsFaulted)
            {
                // Ensure rule task completed
                ruleResult = ruleTask.IsCompletedSuccessfully
                    ? ruleTask.Result
                    : await _ruleBasedClassifier.ClassifyAsync(data, metadata, ct).ConfigureAwait(false);

                return CreateFallbackResult(ruleResult);
            }

            // If rule-based threw (unexpected), use embedding result
            if (ruleTask.IsFaulted && embeddingTask.IsCompletedSuccessfully)
            {
                return embeddingTask.Result;
            }

            // Both failed: rethrow
            throw;
        }

        // Weighted fusion
        return FuseClassifications(embeddingResult, ruleResult);
    }

    /// <summary>
    /// Classifies a batch of data items. Delegates batch processing to the embedding classifier
    /// (which benefits from batched API calls) and merges with per-item rule-based classification.
    /// Falls back to rule-based sequential classification if embedding batching fails.
    /// </summary>
    /// <param name="items">An asynchronous stream of (DataId, Data) tuples to classify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An asynchronous stream of fused classification results.</returns>
    public async IAsyncEnumerable<SemanticClassification> ClassifyBatchAsync(
        IAsyncEnumerable<(string DataId, ReadOnlyMemory<byte> Data)> items,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureNotDisposed();

        // Collect items so we can process them through both classifiers
        var collectedItems = new List<(string DataId, ReadOnlyMemory<byte> Data)>();
        await foreach (var item in items.WithCancellation(ct).ConfigureAwait(false))
        {
            collectedItems.Add(item);
        }

        if (collectedItems.Count == 0)
            yield break;

        // Try to get embedding batch results
        Dictionary<string, SemanticClassification>? embeddingResults = null;
        try
        {
            embeddingResults = new Dictionary<string, SemanticClassification>();
            var embeddingStream = _embeddingClassifier.ClassifyBatchAsync(
                ToAsyncEnumerable(collectedItems), ct);

            await foreach (var result in embeddingStream.WithCancellation(ct).ConfigureAwait(false))
            {
                embeddingResults[result.DataId] = result;
            }
        }
        catch
        {
            // AI unavailable: fall back to rule-based only
            embeddingResults = null;
        }

        // Get rule-based results for each item and fuse
        foreach (var item in collectedItems)
        {
            var metadata = new Dictionary<string, string> { ["data_id"] = item.DataId };
            var ruleResult = await _ruleBasedClassifier.ClassifyAsync(item.Data, metadata, ct)
                .ConfigureAwait(false);

            if (embeddingResults != null &&
                embeddingResults.TryGetValue(item.DataId, out var embeddingResult))
            {
                yield return FuseClassifications(embeddingResult, ruleResult);
            }
            else
            {
                yield return CreateFallbackResult(ruleResult);
            }
        }
    }

    /// <summary>
    /// Computes semantic similarity using the embedding classifier if available,
    /// otherwise falls back to the rule-based classifier's Jaccard similarity.
    /// </summary>
    /// <param name="data1">The first data payload.</param>
    /// <param name="data2">The second data payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A similarity score between 0.0 and 1.0.</returns>
    public async Task<double> ComputeSemanticSimilarityAsync(
        ReadOnlyMemory<byte> data1,
        ReadOnlyMemory<byte> data2,
        CancellationToken ct = default)
    {
        EnsureNotDisposed();

        try
        {
            return await _embeddingClassifier.ComputeSemanticSimilarityAsync(data1, data2, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // AI unavailable: fall back to rule-based Jaccard
            return await _ruleBasedClassifier.ComputeSemanticSimilarityAsync(data1, data2, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fuses two classification results using weighted averaging of importance ordinals
    /// and weighted combination of confidence scores.
    /// </summary>
    private SemanticClassification FuseClassifications(
        SemanticClassification embeddingResult,
        SemanticClassification ruleResult)
    {
        var ruleWeight = 1.0 - _embeddingWeight;

        // Weighted average of ordinal importance values
        var embeddingOrdinal = ImportanceOrdinals[embeddingResult.Importance];
        var ruleOrdinal = ImportanceOrdinals[ruleResult.Importance];
        var fusedOrdinal = _embeddingWeight * embeddingOrdinal + ruleWeight * ruleOrdinal;
        var roundedOrdinal = (int)Math.Round(fusedOrdinal);
        roundedOrdinal = Math.Clamp(roundedOrdinal, 0, 4);
        var fusedImportance = OrdinalToImportance[roundedOrdinal];

        // Combined confidence
        var fusedConfidence = _embeddingWeight * embeddingResult.Confidence +
                              ruleWeight * ruleResult.Confidence;
        fusedConfidence = Math.Clamp(fusedConfidence, 0.0, 1.0);

        // Merge semantic tags (deduplicated)
        var mergedTags = embeddingResult.SemanticTags
            .Concat(ruleResult.SemanticTags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Domain hint: prefer embedding classifier's if non-default, else rule-based
        var domainHint = !string.IsNullOrWhiteSpace(embeddingResult.DomainHint) &&
                         !string.Equals(embeddingResult.DomainHint, "universal", StringComparison.OrdinalIgnoreCase)
            ? embeddingResult.DomainHint
            : ruleResult.DomainHint;

        var classification = new SemanticClassification(
            DataId: embeddingResult.DataId,
            Importance: fusedImportance,
            Confidence: fusedConfidence,
            SemanticTags: mergedTags,
            DomainHint: domainHint,
            ClassifiedAt: DateTimeOffset.UtcNow);

        ValidateClassification(classification);
        IncrementCounter("classifications");

        return classification;
    }

    /// <summary>
    /// Creates a fallback result when the embedding classifier is unavailable.
    /// Uses the rule-based result directly with its original confidence.
    /// </summary>
    private SemanticClassification CreateFallbackResult(SemanticClassification ruleResult)
    {
        IncrementCounter("fallback_classifications");
        IncrementCounter("classifications");
        return ruleResult;
    }

    /// <summary>
    /// Converts a list to an async enumerable for passing to batch classification methods.
    /// </summary>
    private static async IAsyncEnumerable<(string DataId, ReadOnlyMemory<byte> Data)> ToAsyncEnumerable(
        List<(string DataId, ReadOnlyMemory<byte> Data)> items)
    {
        foreach (var item in items)
        {
            yield return item;
        }

        await Task.CompletedTask; // Satisfy async requirement
    }
}
