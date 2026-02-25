// <copyright file="ConflictClassificationEngine.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.SemanticSync;

namespace DataWarehouse.Plugins.SemanticSync.Strategies.ConflictResolution;

/// <summary>
/// Classifies semantic conflicts into actionable conflict types using similarity scores,
/// field-level JSON diffing, and binary header analysis.
/// </summary>
/// <remarks>
/// <para>
/// Classification types:
/// <list type="bullet">
/// <item><description><see cref="ConflictType.SemanticEquivalent"/>: similarity >= 0.95</description></item>
/// <item><description><see cref="ConflictType.SchemaEvolution"/>: different structure where one side is a superset</description></item>
/// <item><description><see cref="ConflictType.PartialOverlap"/>: some fields changed independently on both sides</description></item>
/// <item><description><see cref="ConflictType.SemanticDivergent"/>: core meaning differs (similarity &lt; 0.5 or conflicting same-field values)</description></item>
/// <item><description><see cref="ConflictType.Irreconcilable"/>: both sides modified same critical fields to different values</description></item>
/// </list>
/// </para>
/// <para>
/// For JSON data, performs actual field-level diff by parsing both sides as <see cref="JsonDocument"/>,
/// comparing property names and values, and counting shared/unique/conflicting properties.
/// For binary data, compares header blocks and size patterns.
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Sync fidelity control")]
internal sealed class ConflictClassificationEngine
{
    /// <summary>Cosine similarity threshold for semantic equivalence.</summary>
    private const double EquivalenceThreshold = 0.95;

    /// <summary>Cosine similarity threshold below which data is semantically divergent.</summary>
    private const double DivergenceThreshold = 0.50;

    /// <summary>Number of header bytes to compare for binary format detection.</summary>
    private const int BinaryHeaderSize = 64;

    /// <summary>
    /// Classifies a semantic conflict into one of five conflict types based on the similarity
    /// score and structural analysis of the conflicting data.
    /// </summary>
    /// <param name="conflict">The semantic conflict to classify.</param>
    /// <param name="similarityScore">
    /// Optional similarity score from embedding comparison. When null, classification relies
    /// entirely on structural analysis.
    /// </param>
    /// <returns>The classified <see cref="ConflictType"/>.</returns>
    public ConflictType Classify(SemanticConflict conflict, double? similarityScore)
    {
        ArgumentNullException.ThrowIfNull(conflict);

        // Fast path: high similarity = semantic equivalent
        if (similarityScore.HasValue && similarityScore.Value >= EquivalenceThreshold)
        {
            return ConflictType.SemanticEquivalent;
        }

        // Try JSON field-level analysis
        var jsonClassification = TryClassifyJson(conflict, similarityScore);
        if (jsonClassification.HasValue)
        {
            return jsonClassification.Value;
        }

        // Try binary structural analysis
        var binaryClassification = TryClassifyBinary(conflict, similarityScore);
        if (binaryClassification.HasValue)
        {
            return binaryClassification.Value;
        }

        // Fallback: use similarity score alone
        return ClassifyBySimilarity(similarityScore);
    }

    /// <summary>
    /// Attempts to classify the conflict by parsing both sides as JSON and performing
    /// field-level diff analysis. Returns null if either side is not valid JSON.
    /// </summary>
    private static ConflictType? TryClassifyJson(SemanticConflict conflict, double? similarityScore)
    {
        JsonDocument? localDoc = null;
        JsonDocument? remoteDoc = null;

        try
        {
            localDoc = TryParseJson(conflict.LocalData);
            remoteDoc = TryParseJson(conflict.RemoteData);

            if (localDoc is null || remoteDoc is null)
                return null;

            return ClassifyJsonConflict(localDoc, remoteDoc, conflict, similarityScore);
        }
        finally
        {
            localDoc?.Dispose();
            remoteDoc?.Dispose();
        }
    }

    /// <summary>
    /// Performs field-level JSON diff to classify the conflict type.
    /// Counts shared (same value), unique (one side only), and conflicting (same key, different value) properties.
    /// </summary>
    private static ConflictType ClassifyJsonConflict(
        JsonDocument localDoc,
        JsonDocument remoteDoc,
        SemanticConflict conflict,
        double? similarityScore)
    {
        if (localDoc.RootElement.ValueKind != JsonValueKind.Object ||
            remoteDoc.RootElement.ValueKind != JsonValueKind.Object)
        {
            // Non-object JSON (arrays, primitives): fall back to similarity
            return ClassifyBySimilarity(similarityScore);
        }

        var localProps = GetPropertyMap(localDoc.RootElement);
        var remoteProps = GetPropertyMap(remoteDoc.RootElement);

        var allKeys = new HashSet<string>(localProps.Keys, StringComparer.Ordinal);
        allKeys.UnionWith(remoteProps.Keys);

        int sharedSameValue = 0;
        int sharedDifferentValue = 0;
        int localOnly = 0;
        int remoteOnly = 0;

        foreach (var key in allKeys)
        {
            var inLocal = localProps.TryGetValue(key, out var localVal);
            var inRemote = remoteProps.TryGetValue(key, out var remoteVal);

            if (inLocal && inRemote)
            {
                if (string.Equals(localVal, remoteVal, StringComparison.Ordinal))
                    sharedSameValue++;
                else
                    sharedDifferentValue++;
            }
            else if (inLocal)
            {
                localOnly++;
            }
            else
            {
                remoteOnly++;
            }
        }

        int totalKeys = allKeys.Count;

        // Check for Irreconcilable: both sides modified same critical fields to different values
        // AND both classifications indicate Critical importance
        if (sharedDifferentValue > 0 &&
            conflict.LocalClassification.Importance == SemanticImportance.Critical &&
            conflict.RemoteClassification.Importance == SemanticImportance.Critical)
        {
            return ConflictType.Irreconcilable;
        }

        // Schema evolution: one side has additional fields but the other side's fields are a subset
        if ((localOnly > 0 && remoteOnly == 0 && sharedDifferentValue == 0) ||
            (remoteOnly > 0 && localOnly == 0 && sharedDifferentValue == 0))
        {
            return ConflictType.SchemaEvolution;
        }

        // Schema evolution: both have unique fields but no value conflicts (additive changes)
        if (localOnly > 0 && remoteOnly > 0 && sharedDifferentValue == 0)
        {
            return ConflictType.SchemaEvolution;
        }

        // Semantic divergent: many conflicting fields relative to total, or low similarity
        if (totalKeys > 0 && (double)sharedDifferentValue / totalKeys > 0.5)
        {
            return ConflictType.SemanticDivergent;
        }

        if (similarityScore.HasValue && similarityScore.Value < DivergenceThreshold)
        {
            return ConflictType.SemanticDivergent;
        }

        // Partial overlap: some fields changed independently, can potentially be merged
        if (sharedDifferentValue > 0 || (localOnly > 0 && remoteOnly > 0))
        {
            return ConflictType.PartialOverlap;
        }

        // All keys present with same values but bytes differ (whitespace, ordering)
        if (sharedSameValue == totalKeys && totalKeys > 0)
        {
            return ConflictType.SemanticEquivalent;
        }

        return ClassifyBySimilarity(similarityScore);
    }

    /// <summary>
    /// Attempts to classify the conflict by comparing binary headers and size patterns.
    /// Returns null if not applicable (data too small for meaningful comparison).
    /// </summary>
    private static ConflictType? TryClassifyBinary(SemanticConflict conflict, double? similarityScore)
    {
        if (conflict.LocalData.Length == 0 && conflict.RemoteData.Length == 0)
            return null;

        var localHeaderLen = Math.Min(conflict.LocalData.Length, BinaryHeaderSize);
        var remoteHeaderLen = Math.Min(conflict.RemoteData.Length, BinaryHeaderSize);

        // Need at least some data for header comparison
        if (localHeaderLen == 0 || remoteHeaderLen == 0)
            return ClassifyBySimilarity(similarityScore);

        var localHeader = conflict.LocalData.Span[..localHeaderLen];
        var remoteHeader = conflict.RemoteData.Span[..remoteHeaderLen];

        bool headersMatch = localHeaderLen == remoteHeaderLen && localHeader.SequenceEqual(remoteHeader);

        // Check for Irreconcilable: headers differ and both sides are Critical
        if (!headersMatch &&
            conflict.LocalClassification.Importance == SemanticImportance.Critical &&
            conflict.RemoteClassification.Importance == SemanticImportance.Critical)
        {
            return ConflictType.Irreconcilable;
        }

        // Same header (format), different body
        if (headersMatch)
        {
            // Size pattern analysis: significant size difference suggests schema evolution
            var sizeDiff = Math.Abs(conflict.LocalData.Length - conflict.RemoteData.Length);
            var maxSize = Math.Max(conflict.LocalData.Length, conflict.RemoteData.Length);
            var sizeRatio = maxSize > 0 ? (double)sizeDiff / maxSize : 0.0;

            if (sizeRatio > 0.3)
                return ConflictType.SchemaEvolution;

            return ConflictType.PartialOverlap;
        }

        // Different headers = different format or major structural change
        return ConflictType.SemanticDivergent;
    }

    /// <summary>
    /// Classifies conflict type based purely on the similarity score when structural
    /// analysis is not possible.
    /// </summary>
    private static ConflictType ClassifyBySimilarity(double? similarityScore)
    {
        if (!similarityScore.HasValue)
            return ConflictType.PartialOverlap; // conservative default

        if (similarityScore.Value >= EquivalenceThreshold)
            return ConflictType.SemanticEquivalent;

        if (similarityScore.Value < DivergenceThreshold)
            return ConflictType.SemanticDivergent;

        return ConflictType.PartialOverlap;
    }

    /// <summary>
    /// Extracts a dictionary of property name to serialized value from a JSON object element.
    /// </summary>
    private static Dictionary<string, string> GetPropertyMap(JsonElement element)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject())
        {
            map[property.Name] = property.Value.GetRawText();
        }

        return map;
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
