// <copyright file="RuleBasedClassifier.cs" company="DataWarehouse">
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
/// Metadata and heuristic-based semantic classifier that operates without any AI provider dependency.
/// Classifies data by inspecting file size, content type, metadata tags, recency, and access frequency
/// to determine semantic importance.
/// </summary>
/// <remarks>
/// <para>
/// This classifier is designed for air-gapped, edge, and resource-constrained environments where
/// no AI provider is available. It uses a multi-signal scoring approach:
/// </para>
/// <list type="bullet">
///   <item>File size thresholds (large files are typically more important)</item>
///   <item>Content-type analysis (config/schema files vs. media vs. logs)</item>
///   <item>Metadata tag matching (compliance, security tags elevate; temp, cache tags demote)</item>
///   <item>Recency (recently modified data is boosted; stale data is demoted)</item>
///   <item>Access frequency (frequently accessed data is more important)</item>
/// </list>
/// <para>
/// Confidence scores are capped at 0.6-0.8 to honestly reflect the heuristic nature of
/// classification without AI-driven semantic understanding.
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic classification")]
public sealed class RuleBasedClassifier : SemanticSyncStrategyBase, ISemanticClassifier
{
    private static readonly HashSet<string> CriticalTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "compliance", "audit", "security", "regulatory", "hipaa", "gdpr",
        "pci", "sox", "encryption-key", "credential", "certificate"
    };

    private static readonly HashSet<string> NegligibleTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "log", "temp", "cache", "tmp", "thumbnail", "preview",
        "debug", "trace", "scratch", "draft"
    };

    private static readonly HashSet<string> CriticalContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/json", "application/xml", "application/yaml",
        "application/x-yaml", "text/yaml"
    };

    private static readonly HashSet<string> CriticalContentSubstrings = new(StringComparer.OrdinalIgnoreCase)
    {
        "schema", "config", "configuration", "manifest", "policy"
    };

    private static readonly HashSet<string> MediaContentPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/", "video/", "audio/"
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="RuleBasedClassifier"/> class.
    /// No AI provider is required.
    /// </summary>
    public RuleBasedClassifier()
    {
    }

    /// <inheritdoc/>
    public override string StrategyId => "semantic-classifier-rules";

    /// <inheritdoc/>
    public override string Name => "Rule-Based Semantic Classifier";

    /// <inheritdoc/>
    public override string Description =>
        "Classifies data by semantic importance using metadata heuristics including file size, " +
        "content type, tags, recency, and access frequency. No AI provider required.";

    /// <inheritdoc/>
    public override string SemanticDomain => "universal";

    /// <inheritdoc/>
    public override bool SupportsLocalInference => true;

    /// <inheritdoc/>
    public SemanticClassifierCapabilities Capabilities { get; } = new(
        SupportsEmbeddings: false,
        SupportsLocalInference: true,
        SupportsDomainHints: false,
        MaxBatchSize: 1000);

    /// <summary>
    /// Classifies a single data item using metadata-based heuristics.
    /// Evaluates file size, content type, metadata tags, recency, and access frequency
    /// to compute an importance score, then maps it to a <see cref="SemanticImportance"/> level.
    /// </summary>
    /// <param name="data">The raw data payload to classify.</param>
    /// <param name="metadata">Optional metadata key-value pairs used for heuristic analysis.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A classification with importance level and confidence in the 0.6-0.8 range.</returns>
    public Task<SemanticClassification> ClassifyAsync(
        ReadOnlyMemory<byte> data,
        IDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        EnsureNotDisposed();
        ct.ThrowIfCancellationRequested();

        // Score accumulates importance signals: positive = more important, negative = less important
        double score = 0.0;
        int signalCount = 0;
        var semanticTags = new List<string>();

        // Signal 1: File size
        var sizeScore = EvaluateFileSize(data.Length);
        score += sizeScore;
        signalCount++;

        // Signal 2: Content type
        if (metadata != null)
        {
            var contentTypeScore = EvaluateContentType(metadata, semanticTags);
            score += contentTypeScore;
            signalCount++;

            // Signal 3: Metadata tags
            var tagScore = EvaluateMetadataTags(metadata, semanticTags);
            score += tagScore;
            signalCount++;

            // Signal 4: Recency
            var recencyAdjustment = EvaluateRecency(metadata);
            score += recencyAdjustment;
            if (recencyAdjustment != 0) signalCount++;

            // Signal 5: Access frequency
            var accessScore = EvaluateAccessFrequency(metadata);
            score += accessScore;
            if (accessScore != 0) signalCount++;
        }

        // Normalize score to importance level
        var averageScore = signalCount > 0 ? score / signalCount : 0.0;
        var importance = MapScoreToImportance(averageScore);

        // Confidence is always 0.6-0.8 range for heuristic-based classification
        // More signals = higher confidence within the range
        var confidenceBase = 0.6;
        var confidenceBoost = Math.Min(signalCount * 0.04, 0.2);
        var confidence = confidenceBase + confidenceBoost;

        var classification = new SemanticClassification(
            DataId: (metadata != null && metadata.TryGetValue("data_id", out var dataId) ? dataId : null) ?? Guid.NewGuid().ToString("N"),
            Importance: importance,
            Confidence: confidence,
            SemanticTags: semanticTags.Distinct().ToArray(),
            DomainHint: "heuristic",
            ClassifiedAt: DateTimeOffset.UtcNow);

        ValidateClassification(classification);
        IncrementCounter("classifications");

        return Task.FromResult(classification);
    }

    /// <summary>
    /// Classifies a batch of data items sequentially. Rule-based classification does not
    /// benefit from batching since there are no AI API calls to amortize.
    /// </summary>
    /// <param name="items">An asynchronous stream of (DataId, Data) tuples to classify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An asynchronous stream of classification results.</returns>
    public async IAsyncEnumerable<SemanticClassification> ClassifyBatchAsync(
        IAsyncEnumerable<(string DataId, ReadOnlyMemory<byte> Data)> items,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureNotDisposed();

        await foreach (var (dataId, data) in items.WithCancellation(ct).ConfigureAwait(false))
        {
            var metadata = new Dictionary<string, string> { ["data_id"] = dataId };
            var result = await ClassifyAsync(data, metadata, ct).ConfigureAwait(false);
            yield return result;
        }
    }

    /// <summary>
    /// Computes semantic similarity between two data payloads using Jaccard similarity
    /// on their metadata key-value pairs. For raw data without metadata, falls back to
    /// byte-level overlap analysis.
    /// </summary>
    /// <param name="data1">The first data payload.</param>
    /// <param name="data2">The second data payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A similarity score between 0.0 and 1.0.</returns>
    public Task<double> ComputeSemanticSimilarityAsync(
        ReadOnlyMemory<byte> data1,
        ReadOnlyMemory<byte> data2,
        CancellationToken ct = default)
    {
        EnsureNotDisposed();
        ct.ThrowIfCancellationRequested();

        // Extract token sets from both data payloads for Jaccard similarity
        var tokens1 = ExtractTokens(data1);
        var tokens2 = ExtractTokens(data2);

        if (tokens1.Count == 0 && tokens2.Count == 0)
        {
            // Both empty: identical by default
            return Task.FromResult(1.0);
        }

        if (tokens1.Count == 0 || tokens2.Count == 0)
        {
            // One empty, one not: completely different
            return Task.FromResult(0.0);
        }

        // Jaccard similarity: |intersection| / |union|
        var intersection = tokens1.Intersect(tokens2).Count();
        var union = tokens1.Union(tokens2).Count();

        var similarity = union > 0 ? (double)intersection / union : 0.0;
        return Task.FromResult(similarity);
    }

    #region Evaluation Helpers

    /// <summary>
    /// Evaluates file size and returns an importance score.
    /// Large files (>100MB) are typically more important; tiny files (<1KB) are typically less.
    /// </summary>
    private static double EvaluateFileSize(int sizeBytes)
    {
        return sizeBytes switch
        {
            > 104_857_600 => 1.5,   // >100MB: High importance signal
            > 10_485_760 => 0.8,    // >10MB: Moderate importance
            > 1_048_576 => 0.3,     // >1MB: Slight positive
            > 1024 => 0.0,          // 1KB-1MB: Neutral
            _ => -0.8               // <1KB: Low importance signal
        };
    }

    /// <summary>
    /// Evaluates content type from metadata and returns an importance score.
    /// Config/schema files are critical; media files are normal.
    /// </summary>
    private static double EvaluateContentType(IDictionary<string, string> metadata, List<string> semanticTags)
    {
        if (!TryGetMetadataValue(metadata, "content-type", out var contentType) &&
            !TryGetMetadataValue(metadata, "content_type", out contentType) &&
            !TryGetMetadataValue(metadata, "Content-Type", out contentType))
        {
            return 0.0;
        }

        semanticTags.Add($"content:{contentType}");

        // Check if it's a critical content type with critical substrings
        if (CriticalContentTypes.Contains(contentType))
        {
            // Check for schema/config indicators in other metadata
            foreach (var kvp in metadata)
            {
                if (CriticalContentSubstrings.Any(sub =>
                    kvp.Value.Contains(sub, StringComparison.OrdinalIgnoreCase) ||
                    kvp.Key.Contains(sub, StringComparison.OrdinalIgnoreCase)))
                {
                    semanticTags.Add("critical-structure");
                    return 2.0; // Critical: config/schema JSON/XML
                }
            }

            return 0.5; // Structured data format, moderately important
        }

        // Check for media types
        if (MediaContentPrefixes.Any(prefix =>
            contentType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            semanticTags.Add("media");
            return 0.0; // Normal importance for media
        }

        return 0.0;
    }

    /// <summary>
    /// Evaluates metadata tags for compliance/security indicators (critical) or
    /// temporary/cache indicators (negligible).
    /// </summary>
    private static double EvaluateMetadataTags(IDictionary<string, string> metadata, List<string> semanticTags)
    {
        double score = 0.0;

        foreach (var kvp in metadata)
        {
            var value = kvp.Value;
            var key = kvp.Key;

            // Check for critical tags in both keys and values
            if (CriticalTags.Any(tag =>
                value.Contains(tag, StringComparison.OrdinalIgnoreCase) ||
                key.Contains(tag, StringComparison.OrdinalIgnoreCase)))
            {
                semanticTags.Add("compliance");
                score += 2.0;
            }

            // Check for negligible tags
            if (NegligibleTags.Any(tag =>
                value.Contains(tag, StringComparison.OrdinalIgnoreCase) ||
                key.Contains(tag, StringComparison.OrdinalIgnoreCase)))
            {
                semanticTags.Add("transient");
                score -= 1.5;
            }
        }

        return score;
    }

    /// <summary>
    /// Evaluates data recency from metadata timestamps.
    /// Modified less than 1 hour ago: bump importance. Modified more than 30 days ago: drop importance.
    /// </summary>
    private static double EvaluateRecency(IDictionary<string, string> metadata)
    {
        if (!TryGetMetadataValue(metadata, "modified_at", out var modifiedStr) &&
            !TryGetMetadataValue(metadata, "last_modified", out modifiedStr) &&
            !TryGetMetadataValue(metadata, "timestamp", out modifiedStr))
        {
            return 0.0;
        }

        if (!DateTimeOffset.TryParse(modifiedStr, out var modified))
            return 0.0;

        var age = DateTimeOffset.UtcNow - modified;

        if (age.TotalHours < 1)
            return 0.5;  // Recently modified: bump by ~1 level

        if (age.TotalDays > 30)
            return -0.5; // Stale data: drop by ~1 level

        return 0.0;
    }

    /// <summary>
    /// Evaluates access frequency from metadata.
    /// Access count >100 indicates high importance; <5 indicates low.
    /// </summary>
    private static double EvaluateAccessFrequency(IDictionary<string, string> metadata)
    {
        if (!TryGetMetadataValue(metadata, "access_count", out var countStr))
            return 0.0;

        if (!int.TryParse(countStr, out var count))
            return 0.0;

        return count switch
        {
            > 100 => 1.0,   // High access: important
            > 50 => 0.5,    // Moderate access
            > 10 => 0.0,    // Normal access
            < 5 => -0.5,    // Rarely accessed
            _ => 0.0
        };
    }

    /// <summary>
    /// Maps a normalized importance score to a <see cref="SemanticImportance"/> enum value.
    /// </summary>
    private static SemanticImportance MapScoreToImportance(double score)
    {
        return score switch
        {
            >= 1.5 => SemanticImportance.Critical,
            >= 0.5 => SemanticImportance.High,
            >= -0.3 => SemanticImportance.Normal,
            >= -0.8 => SemanticImportance.Low,
            _ => SemanticImportance.Negligible
        };
    }

    /// <summary>
    /// Extracts a set of whitespace-delimited tokens from data for Jaccard similarity computation.
    /// </summary>
    private static HashSet<string> ExtractTokens(ReadOnlyMemory<byte> data)
    {
        if (data.Length == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var text = System.Text.Encoding.UTF8.GetString(data.Span);
            var tokens = text.Split(
                new[] { ' ', '\t', '\n', '\r', ',', ';', ':', '{', '}', '[', ']', '(', ')', '"', '\'' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return new HashSet<string>(
                tokens.Where(t => t.Length >= 2 && t.Length <= 100),
                StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // Binary data: use byte n-gram fingerprint
            var ngrams = new HashSet<string>();
            var span = data.Span;
            var limit = Math.Min(span.Length - 3, 512);
            for (int i = 0; i < limit; i += 4)
            {
                ngrams.Add($"{span[i]:X2}{span[i + 1]:X2}{span[i + 2]:X2}{span[i + 3]:X2}");
            }
            return ngrams;
        }
    }

    /// <summary>
    /// Attempts to get a metadata value by key, case-insensitively.
    /// </summary>
    private static bool TryGetMetadataValue(IDictionary<string, string> metadata, string key, out string value)
    {
        if (metadata.TryGetValue(key, out var v))
        {
            value = v;
            return true;
        }

        // Case-insensitive fallback
        foreach (var kvp in metadata)
        {
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = kvp.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    #endregion
}
