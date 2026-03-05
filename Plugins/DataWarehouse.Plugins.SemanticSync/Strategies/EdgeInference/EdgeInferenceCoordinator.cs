// <copyright file="EdgeInferenceCoordinator.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.SemanticSync;

namespace DataWarehouse.Plugins.SemanticSync.Strategies.EdgeInference;

/// <summary>
/// Coordinates local AI inference on edge nodes for intelligent sync decisions without cloud connectivity.
/// Follows a three-tier degradation strategy:
/// <list type="number">
/// <item>Local model (centroid-based inference via <see cref="LocalModelManager"/>) -- fastest, no network</item>
/// <item>Cloud classifier fallback (via <see cref="ISemanticClassifier"/>) -- higher accuracy, requires connectivity</item>
/// <item>Rule-based defaults -- always available, lowest accuracy</item>
/// </list>
/// </summary>
/// <remarks>
/// The local model is a lightweight centroid-based classifier (not a neural network) that computes
/// cosine similarity between a data embedding and per-importance-level centroid vectors. This enables
/// sub-millisecond inference on any edge device without GPU or specialized hardware.
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Edge inference")]
public sealed class EdgeInferenceCoordinator : SemanticSyncStrategyBase
{
    private readonly LocalModelManager _modelManager;
    private readonly ISemanticClassifier? _cloudClassifier;
    private readonly IAIProvider? _aiProvider;

    /// <summary>
    /// Maps <see cref="SemanticImportance"/> to <see cref="SyncFidelity"/> for sync decision generation.
    /// Critical/High data gets full fidelity; Normal gets standard; Low gets summarized; Negligible gets metadata-only.
    /// </summary>
    private static readonly Dictionary<SemanticImportance, SyncFidelity> ImportanceToFidelity = new()
    {
        [SemanticImportance.Critical] = SyncFidelity.Full,
        [SemanticImportance.High] = SyncFidelity.Full,
        [SemanticImportance.Normal] = SyncFidelity.Standard,
        [SemanticImportance.Low] = SyncFidelity.Summarized,
        [SemanticImportance.Negligible] = SyncFidelity.Metadata
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="EdgeInferenceCoordinator"/> class.
    /// </summary>
    /// <param name="modelManager">Manager for the local inference model lifecycle.</param>
    /// <param name="cloudClassifier">
    /// Optional cloud-based semantic classifier (e.g., <see cref="HybridClassifier"/> from Plan 03)
    /// used as fallback when no local model is available and cloud connectivity exists.
    /// </param>
    /// <param name="aiProvider">
    /// Optional AI provider for generating real embeddings. When unavailable, a deterministic
    /// hash-based pseudo-embedding is used instead.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="modelManager"/> is null.</exception>
    internal EdgeInferenceCoordinator(
        LocalModelManager modelManager,
        ISemanticClassifier? cloudClassifier = null,
        IAIProvider? aiProvider = null)
    {
        _modelManager = modelManager ?? throw new ArgumentNullException(nameof(modelManager));
        _cloudClassifier = cloudClassifier;
        _aiProvider = aiProvider;
    }

    /// <inheritdoc/>
    public override string StrategyId => "edge-inference-coordinator";

    /// <inheritdoc/>
    public override string Name => "Edge Inference Coordinator";

    /// <inheritdoc/>
    public override string Description =>
        "Coordinates local AI inference on edge nodes for intelligent sync decisions. " +
        "Supports three-tier degradation: local model, cloud classifier, rule-based defaults.";

    /// <inheritdoc/>
    public override string SemanticDomain => "universal";

    /// <inheritdoc/>
    public override bool SupportsLocalInference => true;

    /// <summary>
    /// Performs edge-local inference to classify data and determine the optimal sync fidelity.
    /// Tries local model first, falls back to cloud classifier, then to rule-based defaults.
    /// </summary>
    /// <param name="dataId">Unique identifier of the data item to classify.</param>
    /// <param name="data">Raw data payload to classify.</param>
    /// <param name="metadata">Optional metadata key-value pairs to assist classification.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// An <see cref="EdgeInferenceResult"/> containing the classification, sync decision,
    /// inference latency, and whether the local model was used.
    /// </returns>
    public async Task<EdgeInferenceResult> InferAsync(
        string dataId,
        ReadOnlyMemory<byte> data,
        IDictionary<string, string>? metadata,
        CancellationToken ct)
    {
        EnsureNotDisposed();
        ct.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();

        // Tier 1: Try local model first
        var model = _modelManager.GetCurrentModel();
        if (model is not null)
        {
            var result = await InferWithLocalModelAsync(dataId, data, metadata, model, ct)
                .ConfigureAwait(false);

            stopwatch.Stop();
            IncrementCounter("local_inferences");
            return result with { InferenceLatencyMs = stopwatch.Elapsed.TotalMilliseconds };
        }

        // Tier 2: Cloud classifier fallback
        if (_cloudClassifier is not null)
        {
            try
            {
                var classification = await _cloudClassifier.ClassifyAsync(data, metadata, ct)
                    .ConfigureAwait(false);

                var fidelity = ImportanceToFidelity.GetValueOrDefault(
                    classification.Importance, SyncFidelity.Standard);

                var decision = new SyncDecision(
                    DataId: dataId,
                    Fidelity: fidelity,
                    Reason: SyncDecisionReason.HighImportance,
                    RequiresSummary: fidelity == SyncFidelity.Summarized,
                    EstimatedSizeBytes: data.Length,
                    DeferUntil: null);

                stopwatch.Stop();
                IncrementCounter("cloud_inferences");

                return new EdgeInferenceResult(
                    ModelId: "cloud-classifier",
                    Classification: classification,
                    Decision: decision,
                    InferenceLatencyMs: stopwatch.Elapsed.TotalMilliseconds,
                    UsedLocalModel: false);
            }
            catch
            {

                // Cloud unavailable -- fall through to rule-based defaults
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }
        }

        // Tier 3: Rule-based defaults
        stopwatch.Stop();
        IncrementCounter("fallback_inferences");
        return CreateRuleBasedDefault(dataId, data, stopwatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Performs inference using the local centroid-based model. Computes data embedding,
    /// then finds the importance level with the highest cosine similarity above threshold.
    /// </summary>
    private async Task<EdgeInferenceResult> InferWithLocalModelAsync(
        string dataId,
        ReadOnlyMemory<byte> data,
        IDictionary<string, string>? metadata,
        SyncInferenceModel model,
        CancellationToken ct)
    {
        // Compute embedding for the data
        var embedding = await ComputeEmbeddingAsync(data, ct).ConfigureAwait(false);

        // Find best matching importance level by cosine similarity
        var bestImportance = SemanticImportance.Normal;
        double bestSimilarity = -1.0;

        for (int i = 0; i < model.ImportanceCentroids.Length; i++)
        {
            var similarity = ComputeCosineSimilarity(embedding, model.ImportanceCentroids[i]);
            if (similarity > bestSimilarity)
            {
                bestSimilarity = similarity;
                bestImportance = (SemanticImportance)i;
            }
        }

        // Check against threshold -- if below minimum threshold, default to Normal
        var thresholdKey = bestImportance switch
        {
            SemanticImportance.Critical => "critical_min",
            SemanticImportance.High => "high_min",
            SemanticImportance.Normal => "normal_min",
            SemanticImportance.Low => "low_min",
            _ => "negligible_max"
        };

        if (model.ClassificationThresholds.TryGetValue(thresholdKey, out var threshold)
            && bestSimilarity < threshold)
        {
            bestImportance = SemanticImportance.Normal;
            bestSimilarity = 0.5;
        }

        // Build semantic tags from domain rules
        var tags = new List<string> { "edge-classified" };
        string domainHint = "general";
        if (metadata is not null && metadata.TryGetValue("domain", out var hint))
        {
            domainHint = hint;
        }

        if (model.DomainRules.TryGetValue(domainHint, out var domainTags))
        {
            tags.AddRange(domainTags);
        }

        var classification = new SemanticClassification(
            DataId: dataId,
            Importance: bestImportance,
            Confidence: Math.Clamp(bestSimilarity, 0.0, 1.0),
            SemanticTags: tags.ToArray(),
            DomainHint: domainHint,
            ClassifiedAt: DateTimeOffset.UtcNow);

        var fidelity = ImportanceToFidelity.GetValueOrDefault(bestImportance, SyncFidelity.Standard);

        var decision = new SyncDecision(
            DataId: dataId,
            Fidelity: fidelity,
            Reason: SyncDecisionReason.EdgeInferenceResult,
            RequiresSummary: fidelity == SyncFidelity.Summarized,
            EstimatedSizeBytes: data.Length,
            DeferUntil: bestImportance == SemanticImportance.Negligible
                ? TimeSpan.FromMinutes(30)
                : null);

        return new EdgeInferenceResult(
            ModelId: model.ModelId,
            Classification: classification,
            Decision: decision,
            InferenceLatencyMs: 0, // Set by caller
            UsedLocalModel: true);
    }

    /// <summary>
    /// Computes an embedding for the data. Uses the AI provider if available,
    /// otherwise falls back to a deterministic SHA256-based pseudo-embedding.
    /// </summary>
    private async Task<float[]> ComputeEmbeddingAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (_aiProvider is not null && _aiProvider.IsAvailable)
        {
            try
            {
                // Convert data to text representation for embedding
                var text = Convert.ToBase64String(data.Span);
                var embedding = await _aiProvider.GetEmbeddingsAsync(text, ct).ConfigureAwait(false);

                // If AI returns a different dimension, truncate or pad to 8
                if (embedding.Length >= LocalModelManager.ExpectedDimensions)
                {
                    return embedding[..LocalModelManager.ExpectedDimensions];
                }

                var padded = new float[LocalModelManager.ExpectedDimensions];
                Array.Copy(embedding, padded, embedding.Length);
                return padded;
            }
            catch
            {

                // AI provider failed -- fall through to hash-based embedding
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }
        }

        // Deterministic SHA256-based pseudo-embedding: hash data to float[8]
        return ComputeHashEmbedding(data.Span);
    }

    /// <summary>
    /// Produces a deterministic 8-dimensional embedding from raw data using SHA256.
    /// Each pair of bytes in the hash is converted to a float via BitConverter, then normalized.
    /// This provides consistent classification for the same data across edge nodes.
    /// </summary>
    private static float[] ComputeHashEmbedding(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);

        var embedding = new float[LocalModelManager.ExpectedDimensions];
        for (int i = 0; i < LocalModelManager.ExpectedDimensions; i++)
        {
            // Use 4 bytes per float for better distribution
            embedding[i] = BitConverter.ToSingle(hash.Slice(i * 4, 4)) / float.MaxValue;
        }

        // Normalize to unit vector
        double sumSquared = 0.0;
        for (int i = 0; i < embedding.Length; i++)
        {
            sumSquared += (double)embedding[i] * embedding[i];
        }

        var magnitude = Math.Sqrt(sumSquared);
        if (magnitude > 1e-10)
        {
            for (int i = 0; i < embedding.Length; i++)
            {
                embedding[i] = (float)(embedding[i] / magnitude);
            }
        }

        return embedding;
    }

    /// <summary>
    /// Creates a rule-based default result when neither local model nor cloud classifier is available.
    /// Defaults to Normal importance with Standard fidelity.
    /// </summary>
    private static EdgeInferenceResult CreateRuleBasedDefault(
        string dataId, ReadOnlyMemory<byte> data, double latencyMs)
    {
        var classification = new SemanticClassification(
            DataId: dataId,
            Importance: SemanticImportance.Normal,
            Confidence: 0.3,
            SemanticTags: new[] { "rule-based-default" },
            DomainHint: "unknown",
            ClassifiedAt: DateTimeOffset.UtcNow);

        var decision = new SyncDecision(
            DataId: dataId,
            Fidelity: SyncFidelity.Standard,
            Reason: SyncDecisionReason.LowImportance,
            RequiresSummary: false,
            EstimatedSizeBytes: data.Length,
            DeferUntil: null);

        return new EdgeInferenceResult(
            ModelId: "rule-based-default",
            Classification: classification,
            Decision: decision,
            InferenceLatencyMs: latencyMs,
            UsedLocalModel: false);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _modelManager.Dispose();
        }
        base.Dispose(disposing);
    }
}
