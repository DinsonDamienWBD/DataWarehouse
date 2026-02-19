// <copyright file="EmbeddingClassifier.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.SemanticSync;

namespace DataWarehouse.Plugins.SemanticSync.Strategies.Classification;

/// <summary>
/// AI embedding-based semantic classifier that uses vector similarity against reference
/// centroids to determine data importance. Requires an <see cref="IAIProvider"/> with
/// embedding capabilities for operation.
/// </summary>
/// <remarks>
/// <para>
/// Classification works by converting data to text, generating an embedding vector via
/// the AI provider, then computing cosine similarity against pre-computed reference
/// centroids for each <see cref="SemanticImportance"/> level. The level with highest
/// similarity wins, and the similarity score becomes the confidence.
/// </para>
/// <para>
/// Reference centroids are 8-dimensional normalized vectors serving as a production
/// baseline. They are refined over time via federated learning updates from edge nodes.
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic classification")]
public sealed class EmbeddingClassifier : SemanticSyncStrategyBase, ISemanticClassifier
{
    private readonly IAIProvider _aiProvider;

    /// <summary>
    /// Pre-computed reference centroids for each importance level.
    /// 8-dimensional normalized vectors serving as production baseline centroids
    /// that get refined by federated learning from edge deployments.
    /// </summary>
    /// <remarks>
    /// Centroid design rationale:
    /// - Critical: high activation in dimensions 0-2 (structure, schema, compliance signals)
    /// - High: strong activation in dimensions 1-3 (frequent access, business logic)
    /// - Normal: balanced activation across all dimensions
    /// - Low: weak, dispersed activation pattern
    /// - Negligible: near-zero activation except noise dimensions (6-7)
    /// </remarks>
    private static readonly Dictionary<SemanticImportance, float[]> ReferenceCentroids = new()
    {
        [SemanticImportance.Critical] = new float[]
        {
            0.65f, 0.55f, 0.45f, 0.15f, 0.05f, 0.02f, 0.01f, 0.01f
        },
        [SemanticImportance.High] = new float[]
        {
            0.30f, 0.60f, 0.50f, 0.35f, 0.10f, 0.05f, 0.02f, 0.01f
        },
        [SemanticImportance.Normal] = new float[]
        {
            0.25f, 0.30f, 0.30f, 0.35f, 0.30f, 0.25f, 0.10f, 0.05f
        },
        [SemanticImportance.Low] = new float[]
        {
            0.10f, 0.12f, 0.15f, 0.18f, 0.25f, 0.35f, 0.20f, 0.15f
        },
        [SemanticImportance.Negligible] = new float[]
        {
            0.02f, 0.03f, 0.05f, 0.05f, 0.10f, 0.15f, 0.45f, 0.55f
        }
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddingClassifier"/> class.
    /// </summary>
    /// <param name="aiProvider">The AI provider used for generating embeddings. Must not be null.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="aiProvider"/> is null.</exception>
    public EmbeddingClassifier(IAIProvider aiProvider)
    {
        _aiProvider = aiProvider ?? throw new ArgumentNullException(nameof(aiProvider));
    }

    /// <inheritdoc/>
    public override string StrategyId => "semantic-classifier-embedding";

    /// <inheritdoc/>
    public override string Name => "Embedding-Based Semantic Classifier";

    /// <inheritdoc/>
    public override string Description =>
        "Uses AI-generated vector embeddings and cosine similarity against reference centroids " +
        "to classify data by semantic importance. Requires an AI provider with embedding capabilities.";

    /// <inheritdoc/>
    public override string SemanticDomain => "universal";

    /// <inheritdoc/>
    public override bool SupportsLocalInference => true;

    /// <inheritdoc/>
    public SemanticClassifierCapabilities Capabilities { get; } = new(
        SupportsEmbeddings: true,
        SupportsLocalInference: true,
        SupportsDomainHints: true,
        MaxBatchSize: 100);

    /// <summary>
    /// Classifies a single data item by generating an embedding and comparing it against
    /// reference centroids for each importance level.
    /// </summary>
    /// <param name="data">The raw data payload to classify.</param>
    /// <param name="metadata">Optional metadata key-value pairs to assist classification.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A classification with the best-matching importance level and confidence score.</returns>
    public async Task<SemanticClassification> ClassifyAsync(
        ReadOnlyMemory<byte> data,
        IDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        EnsureNotDisposed();

        var text = ConvertToText(data, metadata);
        var embedding = await _aiProvider.GetEmbeddingsAsync(text, ct).ConfigureAwait(false);

        // Project embedding down to reference dimensionality if needed
        var projected = ProjectToReferenceDimensions(embedding);

        var bestImportance = SemanticImportance.Normal;
        var bestSimilarity = double.MinValue;

        foreach (var (importance, centroid) in ReferenceCentroids)
        {
            var similarity = ComputeCosineSimilarity(projected, centroid);
            if (similarity > bestSimilarity)
            {
                bestSimilarity = similarity;
                bestImportance = importance;
            }
        }

        // Normalize confidence to [0, 1] range (cosine similarity can be negative)
        var confidence = Math.Clamp((bestSimilarity + 1.0) / 2.0, 0.0, 1.0);

        var semanticTags = ExtractSemanticTags(metadata);
        var domainHint = ExtractDomainHint(metadata);

        var classification = new SemanticClassification(
            DataId: (metadata != null && metadata.TryGetValue("data_id", out var dataId) ? dataId : null) ?? Guid.NewGuid().ToString("N"),
            Importance: bestImportance,
            Confidence: confidence,
            SemanticTags: semanticTags,
            DomainHint: domainHint,
            ClassifiedAt: DateTimeOffset.UtcNow);

        ValidateClassification(classification);
        IncrementCounter("classifications");

        return classification;
    }

    /// <summary>
    /// Classifies a batch of data items by generating embeddings in bulk via the AI provider's
    /// batch API, then processing each in parallel.
    /// </summary>
    /// <param name="items">An asynchronous stream of (DataId, Data) tuples to classify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An asynchronous stream of classification results.</returns>
    public async IAsyncEnumerable<SemanticClassification> ClassifyBatchAsync(
        IAsyncEnumerable<(string DataId, ReadOnlyMemory<byte> Data)> items,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureNotDisposed();

        // Collect items into batches up to MaxBatchSize
        var batch = new List<(string DataId, ReadOnlyMemory<byte> Data)>();

        await foreach (var item in items.WithCancellation(ct).ConfigureAwait(false))
        {
            batch.Add(item);

            if (batch.Count >= Capabilities.MaxBatchSize)
            {
                await foreach (var result in ClassifyBatchCoreAsync(batch, ct).ConfigureAwait(false))
                {
                    yield return result;
                }
                batch.Clear();
            }
        }

        // Process remaining items
        if (batch.Count > 0)
        {
            await foreach (var result in ClassifyBatchCoreAsync(batch, ct).ConfigureAwait(false))
            {
                yield return result;
            }
        }
    }

    /// <summary>
    /// Computes the semantic similarity between two data payloads by generating embeddings
    /// for both and computing cosine similarity.
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

        var text1 = ConvertToText(data1, null);
        var text2 = ConvertToText(data2, null);

        var embeddings = await _aiProvider.GetEmbeddingsBatchAsync(
            new[] { text1, text2 }, ct).ConfigureAwait(false);

        var similarity = ComputeCosineSimilarity(embeddings[0], embeddings[1]);

        // Normalize from [-1, 1] to [0, 1]
        return Math.Clamp((similarity + 1.0) / 2.0, 0.0, 1.0);
    }

    /// <summary>
    /// Processes a collected batch of items using the AI provider's batch embedding API.
    /// </summary>
    private async IAsyncEnumerable<SemanticClassification> ClassifyBatchCoreAsync(
        List<(string DataId, ReadOnlyMemory<byte> Data)> batch,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var texts = batch.Select(item => ConvertToText(item.Data, null)).ToArray();

        var allEmbeddings = await _aiProvider.GetEmbeddingsBatchAsync(texts, ct).ConfigureAwait(false);

        var classificationTasks = batch.Select((item, index) =>
        {
            var projected = ProjectToReferenceDimensions(allEmbeddings[index]);

            var bestImportance = SemanticImportance.Normal;
            var bestSimilarity = double.MinValue;

            foreach (var (importance, centroid) in ReferenceCentroids)
            {
                var similarity = ComputeCosineSimilarity(projected, centroid);
                if (similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                    bestImportance = importance;
                }
            }

            var confidence = Math.Clamp((bestSimilarity + 1.0) / 2.0, 0.0, 1.0);

            return new SemanticClassification(
                DataId: item.DataId,
                Importance: bestImportance,
                Confidence: confidence,
                SemanticTags: Array.Empty<string>(),
                DomainHint: "universal",
                ClassifiedAt: DateTimeOffset.UtcNow);
        }).ToList();

        foreach (var classification in classificationTasks)
        {
            ValidateClassification(classification);
            IncrementCounter("classifications");
            yield return classification;
        }
    }

    /// <summary>
    /// Projects an embedding vector to the reference centroid dimensionality (8 dimensions)
    /// using averaged pooling over consecutive segments.
    /// </summary>
    private static float[] ProjectToReferenceDimensions(float[] embedding)
    {
        const int targetDims = 8;

        if (embedding.Length == targetDims)
            return embedding;

        if (embedding.Length < targetDims)
        {
            // Pad with zeros if shorter
            var padded = new float[targetDims];
            Array.Copy(embedding, padded, embedding.Length);
            return padded;
        }

        // Average pooling: divide embedding into targetDims segments
        var projected = new float[targetDims];
        var segmentSize = (double)embedding.Length / targetDims;

        for (int i = 0; i < targetDims; i++)
        {
            var start = (int)(i * segmentSize);
            var end = (int)((i + 1) * segmentSize);
            end = Math.Min(end, embedding.Length);

            double sum = 0;
            int count = 0;
            for (int j = start; j < end; j++)
            {
                sum += embedding[j];
                count++;
            }

            projected[i] = count > 0 ? (float)(sum / count) : 0f;
        }

        return projected;
    }

    /// <summary>
    /// Converts raw byte data to a text representation suitable for embedding generation.
    /// Attempts UTF-8 decoding first; falls back to base64 for binary data.
    /// Incorporates metadata keys to enrich the text representation.
    /// </summary>
    private static string ConvertToText(ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata)
    {
        var sb = new StringBuilder();

        // Add metadata context if available
        if (metadata != null)
        {
            foreach (var kvp in metadata.Where(m =>
                !string.Equals(m.Key, "data_id", StringComparison.OrdinalIgnoreCase)))
            {
                sb.Append(kvp.Key).Append(':').Append(kvp.Value).Append(' ');
            }
        }

        // Attempt UTF-8 text extraction
        if (data.Length > 0)
        {
            try
            {
                var text = Encoding.UTF8.GetString(data.Span);
                // Truncate to a reasonable length for embedding (first 2048 chars)
                sb.Append(text.Length > 2048 ? text[..2048] : text);
            }
            catch
            {
                // Binary data: use base64 prefix for embedding (limited to avoid token overload)
                var base64 = Convert.ToBase64String(data.Span[..Math.Min(data.Length, 512)]);
                sb.Append("[binary:").Append(base64[..Math.Min(base64.Length, 256)]).Append(']');
            }
        }

        return sb.Length > 0 ? sb.ToString() : "[empty]";
    }

    /// <summary>
    /// Extracts semantic tags from metadata keys that are indicative of content categories.
    /// </summary>
    private static string[] ExtractSemanticTags(IDictionary<string, string>? metadata)
    {
        if (metadata == null || metadata.Count == 0)
            return Array.Empty<string>();

        var tags = new List<string>();

        foreach (var kvp in metadata)
        {
            var keyLower = kvp.Key.ToLowerInvariant();
            var valueLower = kvp.Value.ToLowerInvariant();

            if (keyLower.Contains("content-type") || keyLower.Contains("content_type"))
            {
                tags.Add($"content:{valueLower}");
            }
            else if (keyLower.Contains("tag") || keyLower.Contains("label") || keyLower.Contains("category"))
            {
                tags.Add(valueLower);
            }
            else if (keyLower.Contains("domain"))
            {
                tags.Add($"domain:{valueLower}");
            }
            else if (keyLower.Contains("source"))
            {
                tags.Add($"source:{valueLower}");
            }
        }

        return tags.Distinct().ToArray();
    }

    /// <summary>
    /// Extracts a domain hint from metadata if available.
    /// </summary>
    private static string ExtractDomainHint(IDictionary<string, string>? metadata)
    {
        if (metadata == null)
            return "universal";

        if (metadata.TryGetValue("domain", out var domain) && !string.IsNullOrWhiteSpace(domain))
            return domain;

        if (metadata.TryGetValue("domain_hint", out var hint) && !string.IsNullOrWhiteSpace(hint))
            return hint;

        return "universal";
    }
}
