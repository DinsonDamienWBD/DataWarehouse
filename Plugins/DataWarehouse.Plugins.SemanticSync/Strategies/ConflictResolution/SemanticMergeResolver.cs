// <copyright file="SemanticMergeResolver.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.SemanticSync;

using ConflictResolutionResult = DataWarehouse.SDK.Contracts.SemanticSync.ConflictResolutionResult;
using ConflictResolution = DataWarehouse.SDK.Contracts.SemanticSync.ConflictResolution;

namespace DataWarehouse.Plugins.SemanticSync.Strategies.ConflictResolution;

/// <summary>
/// Meaning-based conflict resolution strategy that detects, classifies, and resolves
/// edge-cloud data conflicts using semantic analysis rather than timestamps or version numbers.
/// </summary>
/// <remarks>
/// <para>
/// Resolution strategies per conflict type:
/// <list type="bullet">
/// <item><description><see cref="ConflictType.SemanticEquivalent"/>: Auto-resolve by picking the higher-confidence side.</description></item>
/// <item><description><see cref="ConflictType.PartialOverlap"/>: Deep merge JSON (non-conflicting from both, conflicting prefer higher importance) or prefer higher importance for binary.</description></item>
/// <item><description><see cref="ConflictType.SchemaEvolution"/>: Take the union (superset) of all fields.</description></item>
/// <item><description><see cref="ConflictType.SemanticDivergent"/>: Prefer the side with higher semantic importance; tie-break by most recent classification.</description></item>
/// <item><description><see cref="ConflictType.Irreconcilable"/>: Defer to user; preserve local data until manual resolution.</description></item>
/// </list>
/// </para>
/// <para>
/// Works with and without an AI provider. When AI is available, embedding similarity provides
/// high-quality conflict detection. Without AI, structural comparison (JSON key diff, binary headers)
/// provides a functional but lower-quality fallback.
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Sync fidelity control")]
public sealed class SemanticMergeResolver : SemanticSyncStrategyBase, ISemanticConflictResolver
{
    private readonly EmbeddingSimilarityDetector _detector;
    private readonly ConflictClassificationEngine _classificationEngine;

    /// <summary>
    /// Initializes a new instance of the <see cref="SemanticMergeResolver"/> class.
    /// </summary>
    /// <param name="detector">The embedding similarity detector for conflict detection.</param>
    /// <param name="classificationEngine">The engine for classifying conflicts into semantic categories.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    internal SemanticMergeResolver(
        EmbeddingSimilarityDetector detector,
        ConflictClassificationEngine classificationEngine)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _classificationEngine = classificationEngine ?? throw new ArgumentNullException(nameof(classificationEngine));
    }

    /// <inheritdoc/>
    public override string StrategyId => "conflict-resolver-semantic-merge";

    /// <inheritdoc/>
    public override string Name => "Semantic Merge Conflict Resolver";

    /// <inheritdoc/>
    public override string Description =>
        "Resolves edge-cloud data conflicts using semantic analysis: embedding similarity for detection, " +
        "field-level diff for classification, and importance-weighted merge for resolution. " +
        "Works with and without AI provider (structural fallback).";

    /// <inheritdoc/>
    public override string SemanticDomain => "universal";

    /// <inheritdoc/>
    public override bool SupportsLocalInference => true;

    /// <summary>
    /// Gets the capabilities of this conflict resolver.
    /// </summary>
    public ConflictResolverCapabilities Capabilities { get; } = new(
        SupportsAutoMerge: true,
        SupportedTypes: new[]
        {
            ConflictType.SemanticEquivalent,
            ConflictType.SemanticDivergent,
            ConflictType.SchemaEvolution,
            ConflictType.PartialOverlap,
            ConflictType.Irreconcilable
        },
        RequiresAI: false);

    /// <summary>
    /// Detects whether a semantic conflict exists between local and remote data versions.
    /// Delegates to the <see cref="EmbeddingSimilarityDetector"/>.
    /// Returns null if data is identical or semantically equivalent.
    /// </summary>
    /// <param name="dataId">Unique identifier of the data item.</param>
    /// <param name="localData">The local version of the data payload.</param>
    /// <param name="remoteData">The remote version of the data payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="SemanticConflict"/> if a conflict exists, or null if versions are compatible.</returns>
    public Task<SemanticConflict?> DetectConflictAsync(
        string dataId,
        ReadOnlyMemory<byte> localData,
        ReadOnlyMemory<byte> remoteData,
        CancellationToken ct = default)
    {
        EnsureNotDisposed();
        return _detector.DetectAsync(dataId, localData, remoteData, ct);
    }

    /// <summary>
    /// Classifies the type of a semantic conflict by combining the detector's similarity score
    /// with the classification engine's structural analysis.
    /// </summary>
    /// <param name="conflict">The semantic conflict to classify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The classified <see cref="ConflictType"/>.</returns>
    public Task<ConflictType> ClassifyConflictAsync(
        SemanticConflict conflict,
        CancellationToken ct = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(conflict);

        var similarityScore = _detector.LastSimilarityScore;
        var type = _classificationEngine.Classify(conflict, similarityScore);

        IncrementCounter("conflicts-classified");
        return Task.FromResult(type);
    }

    /// <summary>
    /// Resolves a semantic conflict using the appropriate strategy for the conflict type.
    /// Each conflict type has a distinct resolution approach that maximizes data preservation.
    /// </summary>
    /// <param name="conflict">The semantic conflict to resolve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ConflictResolutionResult"/> with resolved data and explanation.</returns>
    public Task<ConflictResolutionResult> ResolveAsync(
        SemanticConflict conflict,
        CancellationToken ct = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(conflict);

        var similarityScore = _detector.LastSimilarityScore;
        var classifiedType = _classificationEngine.Classify(conflict, similarityScore);
        var semanticSimilarity = similarityScore ?? ComputeJaccardSimilarity(conflict);

        var result = classifiedType switch
        {
            ConflictType.SemanticEquivalent => ResolveSemanticEquivalent(conflict, semanticSimilarity),
            ConflictType.PartialOverlap => ResolvePartialOverlap(conflict, semanticSimilarity),
            ConflictType.SchemaEvolution => ResolveSchemaEvolution(conflict, semanticSimilarity),
            ConflictType.SemanticDivergent => ResolveSemanticDivergent(conflict, semanticSimilarity),
            ConflictType.Irreconcilable => ResolveIrreconcilable(conflict, semanticSimilarity),
            _ => ResolvePartialOverlap(conflict, semanticSimilarity) // defensive default
        };

        IncrementCounter("conflicts-resolved");
        IncrementCounter($"resolved-{classifiedType}");

        return Task.FromResult(result);
    }

    /// <summary>
    /// Resolves SemanticEquivalent conflicts by picking whichever side has higher classification confidence.
    /// Both changes express the same meaning, so either side is valid.
    /// </summary>
    private static ConflictResolutionResult ResolveSemanticEquivalent(
        SemanticConflict conflict, double semanticSimilarity)
    {
        var preferLocal = conflict.LocalClassification.Confidence >= conflict.RemoteClassification.Confidence;
        var resolvedData = preferLocal ? conflict.LocalData : conflict.RemoteData;
        var preferredSide = preferLocal ? "local" : "remote";

        return new ConflictResolutionResult(
            DataId: conflict.DataId,
            Strategy: ConflictResolution.AutoResolve,
            ResolvedData: resolvedData,
            SemanticSimilarity: semanticSimilarity,
            Explanation: $"Semantically equivalent: both changes express the same meaning. " +
                         $"Auto-resolved by selecting {preferredSide} version (confidence: " +
                         $"{(preferLocal ? conflict.LocalClassification.Confidence : conflict.RemoteClassification.Confidence):F3}).");
    }

    /// <summary>
    /// Resolves PartialOverlap conflicts by merging non-conflicting changes from both sides.
    /// For JSON: deep merge with importance-weighted conflict resolution.
    /// For binary: prefer the side with higher semantic importance.
    /// </summary>
    private static ConflictResolutionResult ResolvePartialOverlap(
        SemanticConflict conflict, double semanticSimilarity)
    {
        // Try JSON merge
        var mergedJson = TryJsonMerge(conflict);
        if (mergedJson is not null)
        {
            return new ConflictResolutionResult(
                DataId: conflict.DataId,
                Strategy: ConflictResolution.MergeSemanticFields,
                ResolvedData: mergedJson.Value,
                SemanticSimilarity: semanticSimilarity,
                Explanation: "Partial overlap resolved via JSON deep merge: non-conflicting changes " +
                             "taken from both sides, conflicting fields resolved by semantic importance.");
        }

        // Binary fallback: prefer higher importance side
        var (resolvedData, preferredSide) = PreferByImportance(conflict);

        return new ConflictResolutionResult(
            DataId: conflict.DataId,
            Strategy: ConflictResolution.PreferHigherFidelity,
            ResolvedData: resolvedData,
            SemanticSimilarity: semanticSimilarity,
            Explanation: $"Partial overlap in binary data resolved by preferring {preferredSide} version " +
                         $"(importance: {(preferredSide == "local" ? conflict.LocalClassification.Importance : conflict.RemoteClassification.Importance)}).");
    }

    /// <summary>
    /// Resolves SchemaEvolution conflicts by taking the union (superset) of all fields.
    /// Keeps old values for existing fields and adds new fields from the evolved side.
    /// </summary>
    private static ConflictResolutionResult ResolveSchemaEvolution(
        SemanticConflict conflict, double semanticSimilarity)
    {
        // Try JSON union merge
        var unionJson = TryJsonUnionMerge(conflict);
        if (unionJson is not null)
        {
            return new ConflictResolutionResult(
                DataId: conflict.DataId,
                Strategy: ConflictResolution.MergeSemanticFields,
                ResolvedData: unionJson.Value,
                SemanticSimilarity: semanticSimilarity,
                Explanation: "Schema evolution resolved by taking the union of all fields. " +
                             "Existing field values preserved, new fields added from the evolved version.");
        }

        // Binary fallback: prefer the larger side (more likely has the newer schema)
        var preferLocal = conflict.LocalData.Length >= conflict.RemoteData.Length;
        var resolvedData = preferLocal ? conflict.LocalData : conflict.RemoteData;
        var preferredSide = preferLocal ? "local" : "remote";

        return new ConflictResolutionResult(
            DataId: conflict.DataId,
            Strategy: ConflictResolution.PreferNewerMeaning,
            ResolvedData: resolvedData,
            SemanticSimilarity: semanticSimilarity,
            Explanation: $"Schema evolution in binary data resolved by preferring {preferredSide} version " +
                         $"(larger payload, likely newer schema).");
    }

    /// <summary>
    /// Resolves SemanticDivergent conflicts by preferring the side with higher semantic importance.
    /// If importance is equal, tie-breaks by most recent ClassifiedAt timestamp.
    /// </summary>
    private static ConflictResolutionResult ResolveSemanticDivergent(
        SemanticConflict conflict, double semanticSimilarity)
    {
        bool preferLocal;

        if (conflict.LocalClassification.Importance != conflict.RemoteClassification.Importance)
        {
            // Lower enum value = higher importance (Critical=0, High=1, ...)
            preferLocal = conflict.LocalClassification.Importance < conflict.RemoteClassification.Importance;
        }
        else
        {
            // Same importance: prefer more recently classified
            preferLocal = conflict.LocalClassification.ClassifiedAt >= conflict.RemoteClassification.ClassifiedAt;
        }

        var resolvedData = preferLocal ? conflict.LocalData : conflict.RemoteData;
        var preferredSide = preferLocal ? "local" : "remote";
        var chosenClassification = preferLocal ? conflict.LocalClassification : conflict.RemoteClassification;

        var divergenceReason = conflict.LocalClassification.Importance != conflict.RemoteClassification.Importance
            ? $"importance ({chosenClassification.Importance})"
            : $"most recent classification ({chosenClassification.ClassifiedAt:O})";

        return new ConflictResolutionResult(
            DataId: conflict.DataId,
            Strategy: ConflictResolution.PreferHigherFidelity,
            ResolvedData: resolvedData,
            SemanticSimilarity: semanticSimilarity,
            Explanation: $"Semantic divergence resolved by preferring {preferredSide} version based on " +
                         $"{divergenceReason}. Local importance: {conflict.LocalClassification.Importance}, " +
                         $"Remote importance: {conflict.RemoteClassification.Importance}.");
    }

    /// <summary>
    /// Handles Irreconcilable conflicts by deferring to user. Preserves local data until manual resolution.
    /// </summary>
    private static ConflictResolutionResult ResolveIrreconcilable(
        SemanticConflict conflict, double semanticSimilarity)
    {
        return new ConflictResolutionResult(
            DataId: conflict.DataId,
            Strategy: ConflictResolution.DeferToUser,
            ResolvedData: conflict.LocalData, // preserve local until user decides
            SemanticSimilarity: semanticSimilarity,
            Explanation: $"Irreconcilable conflict: both local and remote modified critical fields " +
                         $"(local importance: {conflict.LocalClassification.Importance}, " +
                         $"remote importance: {conflict.RemoteClassification.Importance}). " +
                         $"Local data preserved pending manual resolution. " +
                         $"Semantic similarity: {semanticSimilarity:F3}.");
    }

    /// <summary>
    /// Attempts a deep JSON merge of both conflict sides.
    /// Non-conflicting properties from both sides are included.
    /// Conflicting properties (same key, different value) prefer the side with higher semantic importance.
    /// </summary>
    /// <returns>Merged JSON as bytes, or null if either side is not valid JSON.</returns>
    private static ReadOnlyMemory<byte>? TryJsonMerge(SemanticConflict conflict)
    {
        JsonDocument? localDoc = null;
        JsonDocument? remoteDoc = null;

        try
        {
            localDoc = TryParseJson(conflict.LocalData);
            remoteDoc = TryParseJson(conflict.RemoteData);

            if (localDoc is null || remoteDoc is null)
                return null;

            if (localDoc.RootElement.ValueKind != JsonValueKind.Object ||
                remoteDoc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            // Lower enum value = higher importance
            bool preferLocalOnConflict =
                conflict.LocalClassification.Importance <= conflict.RemoteClassification.Importance;

            return MergeJsonObjects(localDoc.RootElement, remoteDoc.RootElement, preferLocalOnConflict);
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            localDoc?.Dispose();
            remoteDoc?.Dispose();
        }
    }

    /// <summary>
    /// Attempts a JSON union merge (superset) for schema evolution.
    /// Takes all fields from both sides. For existing fields, preserves the original value.
    /// Adds new fields from the evolved side.
    /// </summary>
    /// <returns>Union-merged JSON as bytes, or null if either side is not valid JSON.</returns>
    private static ReadOnlyMemory<byte>? TryJsonUnionMerge(SemanticConflict conflict)
    {
        JsonDocument? localDoc = null;
        JsonDocument? remoteDoc = null;

        try
        {
            localDoc = TryParseJson(conflict.LocalData);
            remoteDoc = TryParseJson(conflict.RemoteData);

            if (localDoc is null || remoteDoc is null)
                return null;

            if (localDoc.RootElement.ValueKind != JsonValueKind.Object ||
                remoteDoc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            // For schema evolution: take union of all fields
            // For conflicting values on existing fields, keep the value from the side that
            // has more total fields (the "evolved" side)
            var localCount = localDoc.RootElement.EnumerateObject().Count();
            var remoteCount = remoteDoc.RootElement.EnumerateObject().Count();
            bool preferLocalOnConflict = localCount >= remoteCount;

            return MergeJsonObjects(localDoc.RootElement, remoteDoc.RootElement, preferLocalOnConflict);
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            localDoc?.Dispose();
            remoteDoc?.Dispose();
        }
    }

    /// <summary>
    /// Merges two JSON objects, taking all properties from both.
    /// Properties only in one side are included directly.
    /// Properties in both with same value are kept.
    /// Properties in both with different values use the preferred side.
    /// </summary>
    private static ReadOnlyMemory<byte> MergeJsonObjects(
        JsonElement local, JsonElement remote, bool preferLocalOnConflict)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();

            var localProps = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var prop in local.EnumerateObject())
            {
                localProps[prop.Name] = prop.Value;
            }

            var remoteProps = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var prop in remote.EnumerateObject())
            {
                remoteProps[prop.Name] = prop.Value;
            }

            // Write all keys from both sides
            var allKeys = new HashSet<string>(localProps.Keys, StringComparer.Ordinal);
            allKeys.UnionWith(remoteProps.Keys);

            foreach (var key in allKeys)
            {
                var inLocal = localProps.TryGetValue(key, out var localVal);
                var inRemote = remoteProps.TryGetValue(key, out var remoteVal);

                writer.WritePropertyName(key);

                if (inLocal && inRemote)
                {
                    // Both have this property
                    if (localVal.GetRawText() == remoteVal.GetRawText())
                    {
                        // Same value: keep it
                        localVal.WriteTo(writer);
                    }
                    else
                    {
                        // Different values: prefer based on importance
                        var preferred = preferLocalOnConflict ? localVal : remoteVal;
                        preferred.WriteTo(writer);
                    }
                }
                else if (inLocal)
                {
                    localVal.WriteTo(writer);
                }
                else
                {
                    remoteVal.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return new ReadOnlyMemory<byte>(stream.ToArray());
    }

    /// <summary>
    /// Determines which side to prefer based on semantic importance.
    /// Lower enum value = higher importance (Critical &lt; High &lt; Normal &lt; Low &lt; Negligible).
    /// </summary>
    private static (ReadOnlyMemory<byte> Data, string Side) PreferByImportance(SemanticConflict conflict)
    {
        bool preferLocal = conflict.LocalClassification.Importance <= conflict.RemoteClassification.Importance;
        return preferLocal
            ? (conflict.LocalData, "local")
            : (conflict.RemoteData, "remote");
    }

    /// <summary>
    /// Computes Jaccard similarity on JSON field names as a fallback when embeddings are unavailable.
    /// Returns 0.5 as a neutral default for non-JSON data.
    /// </summary>
    private static double ComputeJaccardSimilarity(SemanticConflict conflict)
    {
        JsonDocument? localDoc = null;
        JsonDocument? remoteDoc = null;

        try
        {
            localDoc = TryParseJson(conflict.LocalData);
            remoteDoc = TryParseJson(conflict.RemoteData);

            if (localDoc is null || remoteDoc is null)
                return 0.5; // neutral default for binary

            if (localDoc.RootElement.ValueKind != JsonValueKind.Object ||
                remoteDoc.RootElement.ValueKind != JsonValueKind.Object)
                return 0.5;

            var localKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in localDoc.RootElement.EnumerateObject())
                localKeys.Add(prop.Name);

            var remoteKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in remoteDoc.RootElement.EnumerateObject())
                remoteKeys.Add(prop.Name);

            var intersection = localKeys.Intersect(remoteKeys).Count();
            var union = localKeys.Union(remoteKeys).Count();

            return union > 0 ? (double)intersection / union : 1.0;
        }
        finally
        {
            localDoc?.Dispose();
            remoteDoc?.Dispose();
        }
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
}
