// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.SDK.AI;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using IExtendedAIProvider = DataWarehouse.Plugins.AIAgents.IExtendedAIProvider;

namespace DataWarehouse.Plugins.AIAgents.Capabilities.Semantics;

/// <summary>
/// Engine for detecting duplicate and near-duplicate content using multiple algorithms:
/// exact hash matching, MinHash for Jaccard similarity, SimHash for locality-sensitive hashing,
/// and semantic similarity via embeddings.
/// </summary>
public sealed class DuplicateDetectionEngine
{
    private IExtendedAIProvider? _aiProvider;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly ConcurrentDictionary<string, DocumentFingerprint> _fingerprintCache;
    private readonly int _shingleSize;
    private readonly int _numHashFunctions;
    private readonly int _simHashBits;

    /// <summary>
    /// Initializes a new instance of the <see cref="DuplicateDetectionEngine"/> class.
    /// </summary>
    /// <param name="aiProvider">Optional AI provider for semantic comparison.</param>
    /// <param name="embeddingProvider">Optional embedding provider for semantic similarity.</param>
    /// <param name="shingleSize">Size of shingles (n-grams) for MinHash (default: 3).</param>
    /// <param name="numHashFunctions">Number of hash functions for MinHash (default: 100).</param>
    /// <param name="simHashBits">Number of bits for SimHash fingerprint (default: 64).</param>
    public DuplicateDetectionEngine(
        IExtendedAIProvider? aiProvider = null,
        IEmbeddingProvider? embeddingProvider = null,
        int shingleSize = 3,
        int numHashFunctions = 100,
        int simHashBits = 64)
    {
        _aiProvider = aiProvider;
        _embeddingProvider = embeddingProvider;
        _fingerprintCache = new ConcurrentDictionary<string, DocumentFingerprint>();
        _shingleSize = shingleSize;
        _numHashFunctions = numHashFunctions;
        _simHashBits = simHashBits;
    }

    /// <summary>
    /// Sets or updates the AI provider.
    /// </summary>
    /// <param name="provider">The AI provider to use.</param>
    public void SetProvider(IExtendedAIProvider? provider)
    {
        _aiProvider = provider;
    }

    /// <summary>
    /// Gets whether an AI provider is available.
    /// </summary>
    public bool IsAIAvailable => _aiProvider?.IsAvailable ?? false;

    /// <summary>
    /// Gets whether an embedding provider is available.
    /// </summary>
    public bool IsEmbeddingAvailable => _embeddingProvider?.IsAvailable ?? false;

    /// <summary>
    /// Checks if two documents are duplicates or near-duplicates.
    /// </summary>
    /// <param name="text1">First document text.</param>
    /// <param name="text2">Second document text.</param>
    /// <param name="options">Detection options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Duplicate detection result.</returns>
    public async Task<DuplicatePairResult> CompareAsync(
        string text1,
        string text2,
        DuplicateDetectionOptions? options = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        options ??= new DuplicateDetectionOptions();

        var result = new DuplicatePairResult
        {
            Document1Id = options.Document1Id ?? "doc1",
            Document2Id = options.Document2Id ?? "doc2"
        };

        // Quick exact match check via hash
        var hash1 = ComputeContentHash(text1);
        var hash2 = ComputeContentHash(text2);

        if (hash1 == hash2)
        {
            sw.Stop();
            return result with
            {
                IsDuplicate = true,
                DuplicateType = DuplicateType.Exact,
                Confidence = 1.0f,
                HashSimilarity = 1.0f,
                JaccardSimilarity = 1.0f,
                Duration = sw.Elapsed
            };
        }

        // Normalize texts for comparison
        var normalized1 = NormalizeText(text1, options);
        var normalized2 = NormalizeText(text2, options);

        // MinHash for Jaccard similarity
        float jaccardSim = 0;
        if (options.EnableMinHash)
        {
            jaccardSim = ComputeMinHashSimilarity(normalized1, normalized2);
            result = result with { JaccardSimilarity = jaccardSim };
        }

        // SimHash for near-duplicate detection
        float simHashSim = 0;
        if (options.EnableSimHash)
        {
            var simHash1 = ComputeSimHash(normalized1);
            var simHash2 = ComputeSimHash(normalized2);
            simHashSim = ComputeSimHashSimilarity(simHash1, simHash2);
            result = result with { SimHashSimilarity = simHashSim };
        }

        // Semantic similarity via embeddings
        float semanticSim = 0;
        if (options.EnableSemanticSimilarity && IsEmbeddingAvailable)
        {
            try
            {
                semanticSim = await ComputeSemanticSimilarityAsync(text1, text2, ct);
                result = result with { SemanticSimilarity = semanticSim };
            }
            catch { /* Fallback to other methods */ }
        }

        // Determine duplicate status
        var (isDuplicate, duplicateType, confidence) = DetermineDuplicateStatus(
            jaccardSim, simHashSim, semanticSim, options);

        sw.Stop();

        return result with
        {
            IsDuplicate = isDuplicate,
            DuplicateType = duplicateType,
            Confidence = confidence,
            Duration = sw.Elapsed
        };
    }

    /// <summary>
    /// Finds duplicates in a collection of documents.
    /// </summary>
    /// <param name="documents">Documents to analyze.</param>
    /// <param name="options">Detection options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Batch duplicate detection result.</returns>
    public async Task<DuplicateBatchResult> FindDuplicatesAsync(
        IEnumerable<DocumentForDuplication> documents,
        DuplicateDetectionOptions? options = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        options ??= new DuplicateDetectionOptions();

        var docList = documents.ToList();
        var duplicatePairs = new List<DuplicatePairResult>();
        var duplicateClusters = new List<DuplicateCluster>();

        // Compute fingerprints for all documents
        var fingerprints = new Dictionary<string, DocumentFingerprint>();
        foreach (var doc in docList)
        {
            ct.ThrowIfCancellationRequested();

            var fingerprint = ComputeFingerprint(doc.Content, options);
            fingerprints[doc.Id] = fingerprint;
        }

        // Use LSH (Locality-Sensitive Hashing) for efficient candidate selection
        var candidates = FindCandidatePairs(fingerprints, options);

        // Compare candidate pairs
        foreach (var (id1, id2) in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var doc1 = docList.First(d => d.Id == id1);
            var doc2 = docList.First(d => d.Id == id2);

            var pairOptions = options with
            {
                Document1Id = id1,
                Document2Id = id2
            };

            var pairResult = await CompareAsync(doc1.Content, doc2.Content, pairOptions, ct);

            if (pairResult.IsDuplicate)
            {
                duplicatePairs.Add(pairResult);
            }
        }

        // Build duplicate clusters (union-find)
        duplicateClusters = BuildDuplicateClusters(duplicatePairs, docList);

        sw.Stop();

        return new DuplicateBatchResult
        {
            TotalDocuments = docList.Count,
            DuplicatePairs = duplicatePairs,
            DuplicateClusters = duplicateClusters,
            UniqueDocuments = docList.Count - duplicateClusters.Sum(c => c.DocumentIds.Count - 1),
            Duration = sw.Elapsed
        };
    }

    /// <summary>
    /// Computes a document fingerprint for caching and quick comparison.
    /// </summary>
    /// <param name="text">Document text.</param>
    /// <param name="documentId">Optional document ID for caching.</param>
    /// <returns>Document fingerprint.</returns>
    public DocumentFingerprint GetFingerprint(string text, string? documentId = null)
    {
        if (documentId != null && _fingerprintCache.TryGetValue(documentId, out var cached))
        {
            return cached;
        }

        var fingerprint = ComputeFingerprint(text, new DuplicateDetectionOptions());

        if (documentId != null)
        {
            _fingerprintCache[documentId] = fingerprint;
        }

        return fingerprint;
    }

    /// <summary>
    /// Clears the fingerprint cache.
    /// </summary>
    public void ClearCache()
    {
        _fingerprintCache.Clear();
    }

    /// <summary>
    /// Detects duplicates for a single document against a corpus.
    /// </summary>
    /// <param name="documentId">ID of document to check.</param>
    /// <param name="text">Text to check.</param>
    /// <param name="corpus">Corpus to check against.</param>
    /// <param name="options">Detection options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Duplicate detection result.</returns>
    public async Task<DuplicateDetectionResultEx> DetectDuplicatesAsync(
        string documentId,
        string text,
        IEnumerable<(string Id, string Text, float[]? Embedding)> corpus,
        DuplicateDetectionOptions? options = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        options ??= new DuplicateDetectionOptions();

        var duplicates = new List<DuplicateMatch>();
        var corpusList = corpus.ToList();

        foreach (var item in corpusList)
        {
            ct.ThrowIfCancellationRequested();

            if (item.Id == documentId)
                continue;

            var pairResult = await CompareAsync(text, item.Text, options with
            {
                Document1Id = documentId,
                Document2Id = item.Id
            }, ct);

            if (pairResult.IsDuplicate)
            {
                duplicates.Add(new DuplicateMatch
                {
                    DocumentId = item.Id,
                    Similarity = pairResult.Confidence,
                    DuplicateType = pairResult.DuplicateType
                });
            }
        }

        sw.Stop();

        return new DuplicateDetectionResultEx
        {
            DocumentId = documentId,
            HasDuplicates = duplicates.Count > 0,
            Duplicates = duplicates.OrderByDescending(d => d.Similarity).ToList(),
            Duration = sw.Elapsed
        };
    }

    /// <summary>
    /// Computes similarity between two texts.
    /// </summary>
    /// <param name="text1">First text.</param>
    /// <param name="text2">Second text.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Similarity score (0-1).</returns>
    public async Task<float> ComputeSimilarityAsync(
        string text1,
        string text2,
        CancellationToken ct = default)
    {
        var result = await CompareAsync(text1, text2, ct: ct);
        return result.Confidence;
    }

    #region Private Methods

    private string ComputeContentHash(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes);
    }

    private string NormalizeText(string text, DuplicateDetectionOptions options)
    {
        var normalized = text.ToLowerInvariant();

        if (options.IgnoreWhitespace)
        {
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        }

        if (options.IgnorePunctuation)
        {
            normalized = Regex.Replace(normalized, @"[^\w\s]", "");
        }

        if (options.IgnoreNumbers)
        {
            normalized = Regex.Replace(normalized, @"\d+", "");
        }

        return normalized;
    }

    private DocumentFingerprint ComputeFingerprint(string text, DuplicateDetectionOptions options)
    {
        var normalized = NormalizeText(text, options);

        return new DocumentFingerprint
        {
            ContentHash = ComputeContentHash(text),
            NormalizedHash = ComputeContentHash(normalized),
            MinHashSignature = ComputeMinHashSignature(normalized),
            SimHash = ComputeSimHash(normalized),
            TextLength = text.Length,
            WordCount = text.Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries).Length
        };
    }

    /// <summary>
    /// Computes MinHash signature for Jaccard similarity estimation.
    /// </summary>
    private uint[] ComputeMinHashSignature(string text)
    {
        var shingles = GetShingles(text, _shingleSize);
        var signature = new uint[_numHashFunctions];
        Array.Fill(signature, uint.MaxValue);

        foreach (var shingle in shingles)
        {
            for (int i = 0; i < _numHashFunctions; i++)
            {
                var hash = ComputeShingleHash(shingle, i);
                signature[i] = Math.Min(signature[i], hash);
            }
        }

        return signature;
    }

    /// <summary>
    /// Computes MinHash similarity between two texts.
    /// </summary>
    private float ComputeMinHashSimilarity(string text1, string text2)
    {
        var sig1 = ComputeMinHashSignature(text1);
        var sig2 = ComputeMinHashSignature(text2);

        var matches = 0;
        for (int i = 0; i < _numHashFunctions; i++)
        {
            if (sig1[i] == sig2[i])
                matches++;
        }

        return (float)matches / _numHashFunctions;
    }

    private HashSet<string> GetShingles(string text, int size)
    {
        var shingles = new HashSet<string>();
        var words = text.Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i <= words.Length - size; i++)
        {
            var shingle = string.Join(" ", words.Skip(i).Take(size));
            shingles.Add(shingle);
        }

        // Also add character n-grams for short texts
        if (text.Length < 100)
        {
            for (int i = 0; i <= text.Length - size; i++)
            {
                shingles.Add(text.Substring(i, size));
            }
        }

        return shingles;
    }

    private uint ComputeShingleHash(string shingle, int seed)
    {
        // MurmurHash3-like hash with seed
        var bytes = Encoding.UTF8.GetBytes(shingle);
        uint hash = (uint)seed;

        foreach (var b in bytes)
        {
            hash ^= b;
            hash *= 0x5bd1e995;
            hash ^= hash >> 15;
        }

        return hash;
    }

    /// <summary>
    /// Computes SimHash for locality-sensitive hashing.
    /// </summary>
    private ulong ComputeSimHash(string text)
    {
        var weights = new int[_simHashBits];
        var words = text.Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            var wordHash = ComputeWordHash(word);

            for (int i = 0; i < _simHashBits; i++)
            {
                if ((wordHash & (1UL << i)) != 0)
                    weights[i]++;
                else
                    weights[i]--;
            }
        }

        ulong simHash = 0;
        for (int i = 0; i < _simHashBits; i++)
        {
            if (weights[i] > 0)
                simHash |= 1UL << i;
        }

        return simHash;
    }

    private ulong ComputeWordHash(string word)
    {
        var bytes = Encoding.UTF8.GetBytes(word);
        var hashBytes = MD5.HashData(bytes);
        return BitConverter.ToUInt64(hashBytes, 0);
    }

    private float ComputeSimHashSimilarity(ulong hash1, ulong hash2)
    {
        var xor = hash1 ^ hash2;
        var hammingDistance = CountSetBits(xor);
        return 1.0f - (float)hammingDistance / _simHashBits;
    }

    private int CountSetBits(ulong n)
    {
        var count = 0;
        while (n != 0)
        {
            count += (int)(n & 1);
            n >>= 1;
        }
        return count;
    }

    private async Task<float> ComputeSemanticSimilarityAsync(
        string text1, string text2, CancellationToken ct)
    {
        if (_embeddingProvider == null || !_embeddingProvider.IsAvailable)
            return 0;

        var truncated1 = text1.Length > 2000 ? text1[..2000] : text1;
        var truncated2 = text2.Length > 2000 ? text2[..2000] : text2;

        var embeddings = await _embeddingProvider.GenerateEmbeddingsAsync(
            new[] { truncated1, truncated2 }, ct);

        if (embeddings.Count != 2)
            return 0;

        return VectorMath.CosineSimilarity(embeddings[0], embeddings[1]);
    }

    private (bool IsDuplicate, DuplicateType Type, float Confidence) DetermineDuplicateStatus(
        float jaccardSim, float simHashSim, float semanticSim, DuplicateDetectionOptions options)
    {
        // Weighted combination of similarity scores
        var weights = new[] { 0.4f, 0.3f, 0.3f };
        var scores = new[] { jaccardSim, simHashSim, semanticSim };

        // Adjust weights based on available methods
        if (semanticSim == 0) { weights[2] = 0; weights[0] = 0.6f; weights[1] = 0.4f; }
        if (simHashSim == 0) { weights[1] = 0; weights[0] = 0.7f; weights[2] = 0.3f; }
        if (jaccardSim == 0) { weights[0] = 0; weights[1] = 0.5f; weights[2] = 0.5f; }

        var totalWeight = weights.Sum();
        var combinedScore = totalWeight > 0
            ? scores.Zip(weights, (s, w) => s * w).Sum() / totalWeight
            : 0;

        // Determine type
        DuplicateType type;
        if (combinedScore >= options.ExactThreshold)
            type = DuplicateType.Exact;
        else if (combinedScore >= options.NearDuplicateThreshold)
            type = DuplicateType.NearDuplicate;
        else if (combinedScore >= options.SimilarThreshold)
            type = DuplicateType.Similar;
        else
            return (false, DuplicateType.None, combinedScore);

        return (true, type, combinedScore);
    }

    private List<(string, string)> FindCandidatePairs(
        Dictionary<string, DocumentFingerprint> fingerprints,
        DuplicateDetectionOptions options)
    {
        var candidates = new HashSet<(string, string)>();
        var docIds = fingerprints.Keys.ToList();

        // Band-based LSH for MinHash
        var bandsPerSignature = 20;
        var rowsPerBand = _numHashFunctions / bandsPerSignature;
        var buckets = new Dictionary<string, List<string>>();

        foreach (var (docId, fp) in fingerprints)
        {
            for (int band = 0; band < bandsPerSignature; band++)
            {
                var bandStart = band * rowsPerBand;
                var bandHash = string.Join("-", fp.MinHashSignature
                    .Skip(bandStart)
                    .Take(rowsPerBand)
                    .Select(v => v.ToString()));

                var bucketKey = $"{band}:{bandHash}";

                if (!buckets.ContainsKey(bucketKey))
                    buckets[bucketKey] = new List<string>();

                // Add candidate pairs with existing bucket members
                foreach (var existingDoc in buckets[bucketKey])
                {
                    var pair = string.Compare(docId, existingDoc, StringComparison.Ordinal) < 0
                        ? (docId, existingDoc)
                        : (existingDoc, docId);
                    candidates.Add(pair);
                }

                buckets[bucketKey].Add(docId);
            }
        }

        // SimHash-based candidates (hamming distance <= 3)
        if (options.EnableSimHash)
        {
            for (int i = 0; i < docIds.Count; i++)
            {
                for (int j = i + 1; j < docIds.Count; j++)
                {
                    var hash1 = fingerprints[docIds[i]].SimHash;
                    var hash2 = fingerprints[docIds[j]].SimHash;

                    if (CountSetBits(hash1 ^ hash2) <= 3)
                    {
                        var pair = string.Compare(docIds[i], docIds[j], StringComparison.Ordinal) < 0
                            ? (docIds[i], docIds[j])
                            : (docIds[j], docIds[i]);
                        candidates.Add(pair);
                    }
                }
            }
        }

        return candidates.ToList();
    }

    private List<DuplicateCluster> BuildDuplicateClusters(
        List<DuplicatePairResult> pairs,
        List<DocumentForDuplication> documents)
    {
        // Union-Find for clustering
        var parent = documents.ToDictionary(d => d.Id, d => d.Id);

        string Find(string x)
        {
            if (parent[x] != x)
                parent[x] = Find(parent[x]);
            return parent[x];
        }

        void Union(string x, string y)
        {
            var px = Find(x);
            var py = Find(y);
            if (px != py)
                parent[px] = py;
        }

        foreach (var pair in pairs)
        {
            Union(pair.Document1Id, pair.Document2Id);
        }

        // Build clusters
        var clusters = new Dictionary<string, List<string>>();
        foreach (var doc in documents)
        {
            var root = Find(doc.Id);
            if (!clusters.ContainsKey(root))
                clusters[root] = new List<string>();
            clusters[root].Add(doc.Id);
        }

        return clusters
            .Where(c => c.Value.Count > 1)
            .Select((c, i) => new DuplicateCluster
            {
                ClusterId = $"cluster_{i + 1}",
                DocumentIds = c.Value,
                RepresentativeId = c.Value.First(),
                Size = c.Value.Count
            })
            .ToList();
    }

    #endregion
}

#region Supporting Types

/// <summary>
/// Document fingerprint for duplicate detection.
/// </summary>
public sealed class DocumentFingerprint
{
    /// <summary>SHA-256 hash of original content.</summary>
    public string ContentHash { get; init; } = string.Empty;

    /// <summary>SHA-256 hash of normalized content.</summary>
    public string NormalizedHash { get; init; } = string.Empty;

    /// <summary>MinHash signature.</summary>
    public uint[] MinHashSignature { get; init; } = Array.Empty<uint>();

    /// <summary>SimHash value.</summary>
    public ulong SimHash { get; init; }

    /// <summary>Original text length.</summary>
    public int TextLength { get; init; }

    /// <summary>Word count.</summary>
    public int WordCount { get; init; }
}

/// <summary>
/// Document input for duplicate detection.
/// </summary>
public sealed class DocumentForDuplication
{
    /// <summary>Unique document identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Document content.</summary>
    public required string Content { get; init; }

    /// <summary>Optional metadata.</summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Result of comparing two documents.
/// </summary>
public sealed record DuplicatePairResult
{
    /// <summary>First document ID.</summary>
    public string Document1Id { get; init; } = string.Empty;

    /// <summary>Second document ID.</summary>
    public string Document2Id { get; init; } = string.Empty;

    /// <summary>Whether documents are considered duplicates.</summary>
    public bool IsDuplicate { get; init; }

    /// <summary>Type of duplicate relationship.</summary>
    public DuplicateType DuplicateType { get; init; }

    /// <summary>Overall confidence score (0-1).</summary>
    public float Confidence { get; init; }

    /// <summary>Content hash similarity (1.0 = identical bytes).</summary>
    public float HashSimilarity { get; init; }

    /// <summary>Jaccard similarity via MinHash.</summary>
    public float JaccardSimilarity { get; init; }

    /// <summary>SimHash similarity.</summary>
    public float SimHashSimilarity { get; init; }

    /// <summary>Semantic similarity via embeddings.</summary>
    public float SemanticSimilarity { get; init; }

    /// <summary>Processing duration.</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Result of batch duplicate detection.
/// </summary>
public sealed class DuplicateBatchResult
{
    /// <summary>Total documents analyzed.</summary>
    public int TotalDocuments { get; init; }

    /// <summary>Number of unique documents.</summary>
    public int UniqueDocuments { get; init; }

    /// <summary>Duplicate pairs found.</summary>
    public List<DuplicatePairResult> DuplicatePairs { get; init; } = new();

    /// <summary>Clusters of duplicate documents.</summary>
    public List<DuplicateCluster> DuplicateClusters { get; init; } = new();

    /// <summary>Processing duration.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Percentage of documents that are duplicates.</summary>
    public float DuplicatePercentage => TotalDocuments > 0
        ? 100f * (TotalDocuments - UniqueDocuments) / TotalDocuments
        : 0;
}

/// <summary>
/// A cluster of duplicate documents.
/// </summary>
public sealed class DuplicateCluster
{
    /// <summary>Unique cluster identifier.</summary>
    public required string ClusterId { get; init; }

    /// <summary>Document IDs in this cluster.</summary>
    public List<string> DocumentIds { get; init; } = new();

    /// <summary>Representative document ID (original or best quality).</summary>
    public string? RepresentativeId { get; init; }

    /// <summary>Number of documents in cluster.</summary>
    public int Size { get; init; }

    /// <summary>Average similarity within cluster.</summary>
    public float AverageSimilarity { get; init; }
}

/// <summary>
/// Type of duplicate relationship.
/// </summary>
public enum DuplicateType
{
    /// <summary>Not a duplicate.</summary>
    None = 0,

    /// <summary>Exact byte-for-byte duplicate.</summary>
    Exact,

    /// <summary>Near-duplicate (minor differences like whitespace, formatting).</summary>
    NearDuplicate,

    /// <summary>Similar content (paraphrased, partial overlap).</summary>
    Similar,

    /// <summary>Semantically equivalent (same meaning, different words).</summary>
    SemanticDuplicate
}

/// <summary>
/// Options for duplicate detection.
/// </summary>
public sealed record DuplicateDetectionOptions
{
    /// <summary>First document ID for comparison.</summary>
    public string? Document1Id { get; init; }

    /// <summary>Second document ID for comparison.</summary>
    public string? Document2Id { get; init; }

    /// <summary>Threshold for exact duplicate (default: 0.98).</summary>
    public float ExactThreshold { get; init; } = 0.98f;

    /// <summary>Threshold for near-duplicate (default: 0.85).</summary>
    public float NearDuplicateThreshold { get; init; } = 0.85f;

    /// <summary>Threshold for similar content (default: 0.70).</summary>
    public float SimilarThreshold { get; init; } = 0.70f;

    /// <summary>Enable MinHash for Jaccard similarity.</summary>
    public bool EnableMinHash { get; init; } = true;

    /// <summary>Enable SimHash for locality-sensitive hashing.</summary>
    public bool EnableSimHash { get; init; } = true;

    /// <summary>Enable semantic similarity via embeddings.</summary>
    public bool EnableSemanticSimilarity { get; init; } = true;

    /// <summary>Ignore whitespace differences.</summary>
    public bool IgnoreWhitespace { get; init; } = true;

    /// <summary>Ignore punctuation differences.</summary>
    public bool IgnorePunctuation { get; init; } = false;

    /// <summary>Ignore numeric differences.</summary>
    public bool IgnoreNumbers { get; init; } = false;

    /// <summary>Case-insensitive comparison.</summary>
    public bool IgnoreCase { get; init; } = true;
}

/// <summary>
/// Interface for embedding providers.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Whether the provider is available.</summary>
    bool IsAvailable { get; }

    /// <summary>Generates embeddings for multiple texts.</summary>
    Task<List<float[]>> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken ct = default);
}

/// <summary>
/// Extended duplicate detection result for corpus search.
/// </summary>
public class DuplicateDetectionResultEx
{
    /// <summary>ID of the source document.</summary>
    public required string DocumentId { get; init; }

    /// <summary>Whether any duplicates were found.</summary>
    public bool HasDuplicates { get; init; }

    /// <summary>List of duplicate matches.</summary>
    public List<DuplicateMatch> Duplicates { get; init; } = new();

    /// <summary>Processing duration.</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// A duplicate match in the corpus.
/// </summary>
public sealed class DuplicateMatch
{
    /// <summary>ID of the matching document.</summary>
    public required string DocumentId { get; init; }

    /// <summary>Similarity score (0-1).</summary>
    public float Similarity { get; init; }

    /// <summary>Type of duplicate relationship.</summary>
    public DuplicateType DuplicateType { get; init; }
}

/// <summary>
/// Duplicate detection result - alias for handler compatibility.
/// </summary>
public sealed class DuplicateDetectionResult : DuplicateDetectionResultEx
{
}

#endregion
