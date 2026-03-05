// <copyright file="EmbeddingSimilarityDetector.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.SemanticSync;

namespace DataWarehouse.Plugins.SemanticSync.Strategies.ConflictResolution;

/// <summary>
/// Detects semantic conflicts between local and remote data versions using embedding-based
/// similarity analysis with structural fallback when AI is unavailable.
/// </summary>
/// <remarks>
/// <para>
/// When an <see cref="IAIProvider"/> is available, generates embeddings for both data versions
/// and computes cosine similarity to classify conflicts. Thresholds:
/// >= 0.95 = no conflict (semantically equivalent), 0.7-0.95 = partial overlap, &lt; 0.7 = divergent.
/// </para>
/// <para>
/// When AI is unavailable, falls back to structural comparison: JSON top-level key set analysis
/// or binary header comparison (first 64 bytes).
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Sync fidelity control")]
internal sealed class EmbeddingSimilarityDetector
{
    private readonly IAIProvider? _aiProvider;

    /// <summary>Cosine similarity threshold above which two versions are semantically equivalent.</summary>
    private const double EquivalenceThreshold = 0.95;

    /// <summary>Cosine similarity threshold above which two versions partially overlap.</summary>
    private const double OverlapThreshold = 0.70;

    /// <summary>Number of header bytes to compare for binary data structural analysis.</summary>
    private const int BinaryHeaderSize = 64;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddingSimilarityDetector"/> class.
    /// </summary>
    /// <param name="aiProvider">
    /// Optional AI provider for embedding-based comparison. When null, falls back to structural comparison.
    /// </param>
    public EmbeddingSimilarityDetector(IAIProvider? aiProvider = null)
    {
        _aiProvider = aiProvider;
    }

    // P2-1015: Use volatile reference to ensure cross-thread visibility when LastSimilarityScore
    // is written by one DetectAsync call and read by a concurrent caller.
    private volatile object? _lastSimilarityScore; // boxed double or null

    /// <summary>
    /// Gets the last computed similarity score from <see cref="DetectAsync"/>.
    /// Returns null if no detection has been performed or if structural fallback was used.
    /// Thread-safe: reads always see the latest published value from any writer thread.
    /// </summary>
    public double? LastSimilarityScore
    {
        get => _lastSimilarityScore is double d ? d : null;
        private set => _lastSimilarityScore = value.HasValue ? (object)value.Value : null;
    }

    /// <summary>
    /// Detects whether a semantic conflict exists between local and remote data versions.
    /// Returns null if data is identical or semantically equivalent (similarity >= 0.95).
    /// </summary>
    /// <param name="dataId">Unique identifier of the data item.</param>
    /// <param name="localData">The local version of the data payload.</param>
    /// <param name="remoteData">The remote version of the data payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="SemanticConflict"/> if a conflict exists, or null if versions are compatible.</returns>
    public async Task<SemanticConflict?> DetectAsync(
        string dataId,
        ReadOnlyMemory<byte> localData,
        ReadOnlyMemory<byte> remoteData,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataId);
        LastSimilarityScore = null;

        // Step 1: Byte-identical check — no conflict
        if (localData.Span.SequenceEqual(remoteData.Span))
        {
            return null;
        }

        // Step 2: Try embedding-based comparison if AI available
        if (_aiProvider is { IsAvailable: true } &&
            _aiProvider.Capabilities.HasFlag(AICapabilities.Embeddings))
        {
            return await DetectWithEmbeddingsAsync(dataId, localData, remoteData, ct)
                .ConfigureAwait(false);
        }

        // Step 3: Structural fallback
        return DetectWithStructuralComparison(dataId, localData, remoteData);
    }

    /// <summary>
    /// Detects conflicts using embedding cosine similarity from the AI provider.
    /// </summary>
    private async Task<SemanticConflict?> DetectWithEmbeddingsAsync(
        string dataId,
        ReadOnlyMemory<byte> localData,
        ReadOnlyMemory<byte> remoteData,
        CancellationToken ct)
    {
        var localText = ConvertToText(localData);
        var remoteText = ConvertToText(remoteData);

        var embeddings = await _aiProvider!.GetEmbeddingsBatchAsync(
            new[] { localText, remoteText }, ct).ConfigureAwait(false);

        var similarity = ComputeCosineSimilarity(embeddings[0], embeddings[1]);
        // Normalize from [-1, 1] to [0, 1]
        var normalizedSimilarity = Math.Clamp((similarity + 1.0) / 2.0, 0.0, 1.0);
        LastSimilarityScore = normalizedSimilarity;

        // Semantically equivalent — no conflict
        if (normalizedSimilarity >= EquivalenceThreshold)
        {
            return null;
        }

        // Determine conflict type from similarity
        var conflictType = normalizedSimilarity >= OverlapThreshold
            ? ConflictType.PartialOverlap
            : ConflictType.SemanticDivergent;

        var localClassification = CreateClassification(dataId, "local", normalizedSimilarity);
        var remoteClassification = CreateClassification(dataId, "remote", normalizedSimilarity);

        return new SemanticConflict(
            DataId: dataId,
            Type: conflictType,
            LocalData: localData,
            RemoteData: remoteData,
            LocalClassification: localClassification,
            RemoteClassification: remoteClassification);
    }

    /// <summary>
    /// Falls back to structural comparison when AI is unavailable.
    /// For JSON: compares top-level key sets. For binary: compares first 64-byte headers.
    /// </summary>
    private SemanticConflict? DetectWithStructuralComparison(
        string dataId,
        ReadOnlyMemory<byte> localData,
        ReadOnlyMemory<byte> remoteData)
    {
        // Try JSON structural comparison
        var localJson = TryParseJson(localData);
        var remoteJson = TryParseJson(remoteData);

        if (localJson is not null && remoteJson is not null)
        {
            return DetectJsonStructuralConflict(dataId, localData, remoteData, localJson, remoteJson);
        }

        // Binary header comparison
        return DetectBinaryStructuralConflict(dataId, localData, remoteData);
    }

    /// <summary>
    /// Compares top-level JSON key sets to determine structural conflict type.
    /// Same keys = PartialOverlap, different keys = SchemaEvolution.
    /// </summary>
    private SemanticConflict? DetectJsonStructuralConflict(
        string dataId,
        ReadOnlyMemory<byte> localData,
        ReadOnlyMemory<byte> remoteData,
        JsonDocument localJson,
        JsonDocument remoteJson)
    {
        using (localJson)
        using (remoteJson)
        {
            var localKeys = GetTopLevelKeys(localJson);
            var remoteKeys = GetTopLevelKeys(remoteJson);

            // Compute Jaccard similarity for scoring
            var intersection = localKeys.Intersect(remoteKeys).Count();
            var union = localKeys.Union(remoteKeys).Count();
            var jaccard = union > 0 ? (double)intersection / union : 1.0;
            LastSimilarityScore = jaccard;

            var conflictType = localKeys.SetEquals(remoteKeys)
                ? ConflictType.PartialOverlap
                : ConflictType.SchemaEvolution;

            var localClassification = CreateClassification(dataId, "local", jaccard);
            var remoteClassification = CreateClassification(dataId, "remote", jaccard);

            return new SemanticConflict(
                DataId: dataId,
                Type: conflictType,
                LocalData: localData,
                RemoteData: remoteData,
                LocalClassification: localClassification,
                RemoteClassification: remoteClassification);
        }
    }

    /// <summary>
    /// Compares binary headers (first 64 bytes). Same header = PartialOverlap, different = SemanticDivergent.
    /// </summary>
    private SemanticConflict DetectBinaryStructuralConflict(
        string dataId,
        ReadOnlyMemory<byte> localData,
        ReadOnlyMemory<byte> remoteData)
    {
        var localHeaderLen = Math.Min(localData.Length, BinaryHeaderSize);
        var remoteHeaderLen = Math.Min(remoteData.Length, BinaryHeaderSize);

        var localHeader = localData.Span[..localHeaderLen];
        var remoteHeader = remoteData.Span[..remoteHeaderLen];

        var headersMatch = localHeaderLen == remoteHeaderLen && localHeader.SequenceEqual(remoteHeader);
        LastSimilarityScore = headersMatch ? 0.8 : 0.3;

        var conflictType = headersMatch
            ? ConflictType.PartialOverlap
            : ConflictType.SemanticDivergent;

        var localClassification = CreateClassification(dataId, "local", LastSimilarityScore.Value);
        var remoteClassification = CreateClassification(dataId, "remote", LastSimilarityScore.Value);

        return new SemanticConflict(
            DataId: dataId,
            Type: conflictType,
            LocalData: localData,
            RemoteData: remoteData,
            LocalClassification: localClassification,
            RemoteClassification: remoteClassification);
    }

    /// <summary>
    /// Creates a default <see cref="SemanticClassification"/> with Normal importance
    /// for conflict detection context.
    /// </summary>
    private static SemanticClassification CreateClassification(
        string dataId, string side, double confidence)
    {
        return new SemanticClassification(
            DataId: dataId,
            Importance: SemanticImportance.Normal,
            Confidence: Math.Clamp(confidence, 0.0, 1.0),
            SemanticTags: new[] { $"conflict-side:{side}" },
            DomainHint: "universal",
            ClassifiedAt: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Attempts to parse raw bytes as a UTF-8 JSON document.
    /// Returns null if the data is not valid JSON.
    /// </summary>
    private static JsonDocument? TryParseJson(ReadOnlyMemory<byte> data)
    {
        if (data.Length == 0) return null;

        try
        {
            return JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts the set of top-level property names from a JSON document.
    /// </summary>
    private static HashSet<string> GetTopLevelKeys(JsonDocument doc)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                keys.Add(property.Name);
            }
        }
        return keys;
    }

    /// <summary>
    /// Converts raw byte data to a text representation for embedding generation.
    /// Attempts UTF-8 decoding first; falls back to base64 for binary data.
    /// </summary>
    private static string ConvertToText(ReadOnlyMemory<byte> data)
    {
        if (data.Length == 0) return "[empty]";

        try
        {
            var text = Encoding.UTF8.GetString(data.Span);
            return text.Length > 2048 ? text[..2048] : text;
        }
        catch
        {
            var base64 = Convert.ToBase64String(data.Span[..Math.Min(data.Length, 512)]);
            return $"[binary:{base64[..Math.Min(base64.Length, 256)]}]";
        }
    }

    /// <summary>
    /// Computes cosine similarity between two float vectors.
    /// Returns a value between -1.0 and 1.0.
    /// </summary>
    private static double ComputeCosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length)
            return 0.0;

        double dotProduct = 0.0, magnitudeA = 0.0, magnitudeB = 0.0;

        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += (double)a[i] * b[i];
            magnitudeA += (double)a[i] * a[i];
            magnitudeB += (double)b[i] * b[i];
        }

        magnitudeA = Math.Sqrt(magnitudeA);
        magnitudeB = Math.Sqrt(magnitudeB);

        return (magnitudeA == 0.0 || magnitudeB == 0.0) ? 0.0 : dotProduct / (magnitudeA * magnitudeB);
    }
}
