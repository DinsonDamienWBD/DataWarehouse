using System.Diagnostics;
using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.AiEnhanced;

/// <summary>
/// Type of content for deduplication analysis.
/// </summary>
public enum ContentAnalysisType
{
    /// <summary>
    /// Text document content.
    /// </summary>
    Text,

    /// <summary>
    /// Image content.
    /// </summary>
    Image,

    /// <summary>
    /// Structured document (PDF, Office, etc.).
    /// </summary>
    Document,

    /// <summary>
    /// Binary data.
    /// </summary>
    Binary
}

/// <summary>
/// Result of a deduplication check.
/// </summary>
public sealed class DeduplicationResult
{
    /// <summary>
    /// Whether duplicates were found.
    /// </summary>
    public required bool HasDuplicates { get; init; }

    /// <summary>
    /// Whether semantic (meaning-based) duplicates were found.
    /// </summary>
    public bool HasSemanticDuplicates { get; init; }

    /// <summary>
    /// Whether exact (hash-based) duplicates were found.
    /// </summary>
    public bool HasExactDuplicates { get; init; }

    /// <summary>
    /// List of duplicate object IDs with similarity scores.
    /// </summary>
    public IReadOnlyList<(string ObjectId, double Similarity, bool IsExact)> Duplicates { get; init; } =
        Array.Empty<(string, double, bool)>();

    /// <summary>
    /// Best match if any duplicate found.
    /// </summary>
    public string? BestMatchId { get; init; }

    /// <summary>
    /// Similarity score of best match (0.0-1.0).
    /// </summary>
    public double BestMatchSimilarity { get; init; }

    /// <summary>
    /// Whether AI was used for analysis.
    /// </summary>
    public bool UsedAi { get; init; }

    /// <summary>
    /// Time taken for analysis.
    /// </summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Content signature for deduplication tracking.
/// </summary>
public sealed class ContentSignature
{
    /// <summary>
    /// Object identifier.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// SHA-256 hash of content.
    /// </summary>
    public required string ContentHash { get; init; }

    /// <summary>
    /// Content type.
    /// </summary>
    public ContentAnalysisType ContentType { get; init; }

    /// <summary>
    /// Semantic embedding vector (null if not computed).
    /// </summary>
    public float[]? Embedding { get; init; }

    /// <summary>
    /// Content size in bytes.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// When the signature was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Semantic deduplication strategy using AI embeddings.
/// Detects duplicates based on meaning, not just exact hash matching.
/// </summary>
/// <remarks>
/// Features:
/// - AI-powered semantic similarity detection
/// - Image similarity detection via embeddings
/// - Document similarity detection
/// - Hash-based exact duplicate detection
/// - Graceful fallback to hash-only matching
/// - Configurable similarity thresholds
/// </remarks>
public sealed class SemanticDeduplicationStrategy : AiEnhancedStrategyBase
{
    private readonly BoundedDictionary<string, ContentSignature> _signatures = new BoundedDictionary<string, ContentSignature>(1000);
    private readonly BoundedDictionary<string, List<string>> _hashIndex = new BoundedDictionary<string, List<string>>(1000); // hash -> objectIds
    private readonly object _indexLock = new();
    private readonly double _semanticThreshold;
    private readonly double _nearDuplicateThreshold;

    /// <summary>
    /// Initializes a new SemanticDeduplicationStrategy with default thresholds.
    /// </summary>
    public SemanticDeduplicationStrategy() : this(0.95, 0.85) { }

    /// <summary>
    /// Initializes a new SemanticDeduplicationStrategy with custom thresholds.
    /// </summary>
    /// <param name="semanticThreshold">Threshold for semantic duplicate detection (0.0-1.0).</param>
    /// <param name="nearDuplicateThreshold">Threshold for near-duplicate detection (0.0-1.0).</param>
    public SemanticDeduplicationStrategy(double semanticThreshold, double nearDuplicateThreshold)
    {
        _semanticThreshold = Math.Clamp(semanticThreshold, 0.5, 1.0);
        _nearDuplicateThreshold = Math.Clamp(nearDuplicateThreshold, 0.5, _semanticThreshold);
    }

    /// <inheritdoc/>
    public override string StrategyId => "ai.semantic-dedup";

    /// <inheritdoc/>
    public override string DisplayName => "Semantic Deduplication";

    /// <inheritdoc/>
    public override AiEnhancedCategory AiCategory => AiEnhancedCategory.Deduplication;

    /// <inheritdoc/>
    public override IntelligenceCapabilities RequiredCapabilities =>
        IntelligenceCapabilities.Embeddings | IntelligenceCapabilities.SemanticDeduplication;

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 1_000,
        TypicalLatencyMs = 50.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Semantic deduplication strategy using AI embeddings to detect duplicates based on meaning. " +
        "Finds semantically similar content even with textual differences, supporting text, images, and documents.";

    /// <inheritdoc/>
    public override string[] Tags => ["ai", "deduplication", "semantic", "embeddings", "similarity"];

    /// <summary>
    /// Gets the current semantic similarity threshold.
    /// </summary>
    public double SemanticThreshold => _semanticThreshold;

    /// <summary>
    /// Gets the current near-duplicate threshold.
    /// </summary>
    public double NearDuplicateThreshold => _nearDuplicateThreshold;

    /// <summary>
    /// Registers content for deduplication tracking.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="content">Content bytes.</param>
    /// <param name="textContent">Optional extracted text for semantic analysis.</param>
    /// <param name="contentType">Content type for analysis.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Content signature.</returns>
    public async Task<ContentSignature> RegisterContentAsync(
        string objectId,
        byte[] content,
        string? textContent = null,
        ContentAnalysisType contentType = ContentAnalysisType.Binary,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentNullException.ThrowIfNull(content);

        var sw = Stopwatch.StartNew();

        // Compute hash
        var hash = ComputeHash(content);

        // Try to get embedding
        float[]? embedding = null;
        if (IsAiAvailable && !string.IsNullOrWhiteSpace(textContent))
        {
            embedding = await GetEmbeddingAsync(textContent, ct);
        }

        var signature = new ContentSignature
        {
            ObjectId = objectId,
            ContentHash = hash,
            ContentType = contentType,
            Embedding = embedding,
            SizeBytes = content.Length
        };

        // Store signature
        _signatures[objectId] = signature;

        // Update hash index
        lock (_indexLock)
        {
            if (!_hashIndex.TryGetValue(hash, out var objectIds))
            {
                objectIds = new List<string>();
                _hashIndex[hash] = objectIds;
            }
            if (!objectIds.Contains(objectId))
            {
                objectIds.Add(objectId);
            }
        }

        sw.Stop();
        RecordAiOperation(embedding != null, false, embedding != null ? 1.0 : 0.0, sw.Elapsed.TotalMilliseconds);

        return signature;
    }

    /// <summary>
    /// Checks for duplicates of the given content.
    /// </summary>
    /// <param name="content">Content bytes.</param>
    /// <param name="textContent">Optional extracted text for semantic analysis.</param>
    /// <param name="excludeObjectId">Object ID to exclude from results (for self-comparison).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Deduplication result.</returns>
    public async Task<DeduplicationResult> CheckForDuplicatesAsync(
        byte[] content,
        string? textContent = null,
        string? excludeObjectId = null,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentNullException.ThrowIfNull(content);

        var sw = Stopwatch.StartNew();
        var duplicates = new List<(string ObjectId, double Similarity, bool IsExact)>();
        var usedAi = false;

        // Check for exact duplicates
        var hash = ComputeHash(content);
        if (_hashIndex.TryGetValue(hash, out var exactMatches))
        {
            foreach (var matchId in exactMatches)
            {
                if (matchId != excludeObjectId)
                {
                    duplicates.Add((matchId, 1.0, true));
                }
            }
        }

        // Check for semantic duplicates if AI available
        if (IsAiAvailable && !string.IsNullOrWhiteSpace(textContent))
        {
            var semanticDups = await FindSemanticDuplicatesAsync(textContent, excludeObjectId, ct);
            if (semanticDups != null)
            {
                usedAi = true;
                foreach (var (id, similarity) in semanticDups)
                {
                    if (!duplicates.Any(d => d.ObjectId == id))
                    {
                        duplicates.Add((id, similarity, false));
                    }
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(textContent))
        {
            // Fallback: simple text similarity
            var fallbackDups = FindFallbackDuplicates(textContent, excludeObjectId);
            foreach (var (id, similarity) in fallbackDups)
            {
                if (!duplicates.Any(d => d.ObjectId == id))
                {
                    duplicates.Add((id, similarity, false));
                }
            }
        }

        sw.Stop();

        var sortedDuplicates = duplicates
            .OrderByDescending(d => d.Similarity)
            .ToList();

        var result = new DeduplicationResult
        {
            HasDuplicates = sortedDuplicates.Count > 0,
            HasExactDuplicates = sortedDuplicates.Any(d => d.IsExact),
            HasSemanticDuplicates = sortedDuplicates.Any(d => !d.IsExact && d.Similarity >= _semanticThreshold),
            Duplicates = sortedDuplicates,
            BestMatchId = sortedDuplicates.FirstOrDefault().ObjectId,
            BestMatchSimilarity = sortedDuplicates.FirstOrDefault().Similarity,
            UsedAi = usedAi,
            Duration = sw.Elapsed
        };

        RecordAiOperation(usedAi, false, usedAi ? result.BestMatchSimilarity : 0, sw.Elapsed.TotalMilliseconds);

        return result;
    }

    /// <summary>
    /// Finds all duplicate groups in the registered content.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Groups of duplicate object IDs.</returns>
    public async Task<IReadOnlyList<IReadOnlyList<string>>> FindAllDuplicateGroupsAsync(CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var groups = new List<List<string>>();
        var processed = new HashSet<string>();

        // First, group exact duplicates
        foreach (var (hash, objectIds) in _hashIndex)
        {
            if (objectIds.Count > 1)
            {
                groups.Add(new List<string>(objectIds));
                foreach (var id in objectIds)
                {
                    processed.Add(id);
                }
            }
        }

        // Then, find semantic duplicate groups
        if (IsAiAvailable)
        {
            var signaturesWithEmbeddings = _signatures.Values
                .Where(s => s.Embedding != null && !processed.Contains(s.ObjectId))
                .ToList();

            foreach (var sig in signaturesWithEmbeddings)
            {
                if (processed.Contains(sig.ObjectId))
                    continue;

                var group = new List<string> { sig.ObjectId };
                processed.Add(sig.ObjectId);

                foreach (var other in signaturesWithEmbeddings)
                {
                    if (processed.Contains(other.ObjectId))
                        continue;

                    if (sig.Embedding != null && other.Embedding != null)
                    {
                        var similarity = CalculateCosineSimilarity(sig.Embedding, other.Embedding);
                        if (similarity >= _semanticThreshold)
                        {
                            group.Add(other.ObjectId);
                            processed.Add(other.ObjectId);
                        }
                    }
                }

                if (group.Count > 1)
                {
                    groups.Add(group);
                }
            }
        }

        return groups;
    }

    /// <summary>
    /// Removes a content signature from tracking.
    /// </summary>
    /// <param name="objectId">Object identifier to remove.</param>
    /// <returns>True if removed, false if not found.</returns>
    public bool RemoveContent(string objectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        if (_signatures.TryRemove(objectId, out var signature))
        {
            lock (_indexLock)
            {
                if (_hashIndex.TryGetValue(signature.ContentHash, out var objectIds))
                {
                    objectIds.Remove(objectId);
                    if (objectIds.Count == 0)
                    {
                        _hashIndex.TryRemove(signature.ContentHash, out _);
                    }
                }
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Gets statistics about registered content.
    /// </summary>
    /// <returns>Dictionary of statistics.</returns>
    public Dictionary<string, object> GetContentStatistics()
    {
        var withEmbeddings = _signatures.Values.Count(s => s.Embedding != null);
        var uniqueHashes = _hashIndex.Count;
        var duplicateGroups = _hashIndex.Values.Count(v => v.Count > 1);

        return new Dictionary<string, object>
        {
            ["TotalDocuments"] = _signatures.Count,
            ["DocumentsWithEmbeddings"] = withEmbeddings,
            ["DocumentsWithoutEmbeddings"] = _signatures.Count - withEmbeddings,
            ["UniqueHashes"] = uniqueHashes,
            ["ExactDuplicateGroups"] = duplicateGroups,
            ["SemanticThreshold"] = _semanticThreshold,
            ["NearDuplicateThreshold"] = _nearDuplicateThreshold
        };
    }

    private async Task<float[]?> GetEmbeddingAsync(string text, CancellationToken ct)
    {
        var embeddings = await RequestEmbeddingsAsync(new[] { text }, DefaultContext, ct);
        return embeddings?.FirstOrDefault();
    }

    private async Task<List<(string ObjectId, double Similarity)>?> FindSemanticDuplicatesAsync(
        string textContent,
        string? excludeObjectId,
        CancellationToken ct)
    {
        var embedding = await GetEmbeddingAsync(textContent, ct);
        if (embedding == null)
            return null;

        var results = new List<(string ObjectId, double Similarity)>();

        foreach (var sig in _signatures.Values)
        {
            if (sig.ObjectId == excludeObjectId)
                continue;

            if (sig.Embedding != null)
            {
                var similarity = CalculateCosineSimilarity(embedding, sig.Embedding);
                if (similarity >= _nearDuplicateThreshold)
                {
                    results.Add((sig.ObjectId, similarity));
                }
            }
        }

        return results;
    }

    private List<(string ObjectId, double Similarity)> FindFallbackDuplicates(string textContent, string? excludeObjectId)
    {
        // Simple Jaccard similarity based on word tokens
        var results = new List<(string ObjectId, double Similarity)>();
        var sourceTokens = TokenizeForFallback(textContent);

        // We'd need stored text content for this - for now, return empty
        // In a real implementation, you'd store extracted text alongside signatures
        return results;
    }

    private static HashSet<string> TokenizeForFallback(string text)
    {
        return text.ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 2)
            .ToHashSet();
    }

    private static string ComputeHash(byte[] content)
    {
        var hashBytes = SHA256.HashData(content);
        return Convert.ToHexString(hashBytes);
    }
}
