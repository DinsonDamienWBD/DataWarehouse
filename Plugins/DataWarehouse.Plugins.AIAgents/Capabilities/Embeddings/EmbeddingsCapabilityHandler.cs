// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using IExtendedAIProvider = DataWarehouse.Plugins.AIAgents.IExtendedAIProvider;

namespace DataWarehouse.Plugins.AIAgents.Capabilities.Embeddings;

/// <summary>
/// Handles embedding and vector operations including vector generation, semantic search,
/// similarity matching, and clustering.
/// </summary>
public sealed class EmbeddingsCapabilityHandler : ICapabilityHandler
{
    private readonly Func<string, IExtendedAIProvider?> _providerResolver;
    private readonly EmbeddingsConfig _config;
    private readonly IVectorOperations _vectorOps;

    /// <summary>
    /// Vector generation capability identifier.
    /// </summary>
    public const string VectorGeneration = "VectorGeneration";

    /// <summary>
    /// Semantic search capability identifier.
    /// </summary>
    public const string SemanticSearch = "SemanticSearch";

    /// <summary>
    /// Similarity matching capability identifier.
    /// </summary>
    public const string SimilarityMatching = "SimilarityMatching";

    /// <summary>
    /// Clustering capability identifier.
    /// </summary>
    public const string Clustering = "Clustering";

    /// <inheritdoc/>
    public string CapabilityDomain => "Embeddings";

    /// <inheritdoc/>
    public string DisplayName => "Vector Operations";

    /// <inheritdoc/>
    public IReadOnlyList<string> SupportedCapabilities => new[]
    {
        VectorGeneration,
        SemanticSearch,
        SimilarityMatching,
        Clustering
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddingsCapabilityHandler"/> class.
    /// </summary>
    /// <param name="providerResolver">Function to resolve providers by name.</param>
    /// <param name="config">Optional configuration settings.</param>
    /// <param name="vectorOps">Optional vector operations implementation.</param>
    public EmbeddingsCapabilityHandler(
        Func<string, IExtendedAIProvider?> providerResolver,
        EmbeddingsConfig? config = null,
        IVectorOperations? vectorOps = null)
    {
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _config = config ?? new EmbeddingsConfig();
        _vectorOps = vectorOps ?? new DefaultVectorOperations();
    }

    /// <inheritdoc/>
    public bool IsCapabilityEnabled(InstanceCapabilityConfig instanceConfig, string capability)
    {
        ArgumentNullException.ThrowIfNull(instanceConfig);

        // Check if explicitly disabled
        if (instanceConfig.DisabledCapabilities.Contains(capability))
            return false;

        // Check quota tier restrictions
        var limits = UsageLimits.GetDefaultLimits(instanceConfig.QuotaTier);

        // Embeddings are restricted on free tier
        if (!limits.EmbeddingsEnabled)
            return false;

        return true;
    }

    /// <inheritdoc/>
    public string? GetProviderForCapability(InstanceCapabilityConfig instanceConfig, string capability)
    {
        ArgumentNullException.ThrowIfNull(instanceConfig);

        if (instanceConfig.CapabilityProviderMappings.TryGetValue(capability, out var provider))
            return provider;

        // Fall back to domain-level mapping
        if (instanceConfig.CapabilityProviderMappings.TryGetValue(CapabilityDomain, out provider))
            return provider;

        // Default to OpenAI for embeddings (widely supported)
        return _config.DefaultProvider;
    }

    /// <summary>
    /// Generates embedding vectors for the provided text(s).
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The vector generation request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The vector generation result.</returns>
    public async Task<CapabilityResult<VectorGenerationResult>> GenerateVectorsAsync(
        InstanceCapabilityConfig instanceConfig,
        VectorGenerationRequest request,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, VectorGeneration))
            return CapabilityResult<VectorGenerationResult>.Disabled(VectorGeneration);

        var providerName = GetProviderForCapability(instanceConfig, VectorGeneration);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<VectorGenerationResult>.NoProvider(VectorGeneration);

        var provider = _providerResolver(providerName);
        if (provider == null)
            return CapabilityResult<VectorGenerationResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");

        if (!provider.SupportsEmbeddings)
            return CapabilityResult<VectorGenerationResult>.Fail($"Provider '{providerName}' does not support embeddings.", "EMBEDDINGS_NOT_SUPPORTED");

        try
        {
            var startTime = DateTime.UtcNow;

            var texts = request.Texts ?? (request.Text != null ? new[] { request.Text } : Array.Empty<string>());
            if (texts.Length == 0)
                return CapabilityResult<VectorGenerationResult>.Fail("No text provided for embedding.", "NO_INPUT");

            var embeddings = await provider.EmbedAsync(texts, request.Model, ct);
            var duration = DateTime.UtcNow - startTime;

            // Convert double[][] to float[][] for internal use
            var vectors = embeddings.Select(e => e.Select(d => (float)d).ToArray()).ToArray();

            // Optionally normalize vectors
            if (request.Normalize)
            {
                for (int i = 0; i < vectors.Length; i++)
                {
                    vectors[i] = _vectorOps.Normalize(vectors[i]);
                }
            }

            return CapabilityResult<VectorGenerationResult>.Ok(
                new VectorGenerationResult
                {
                    Vectors = vectors,
                    Dimensions = vectors.FirstOrDefault()?.Length ?? 0,
                    Count = vectors.Length,
                    Model = request.Model ?? _config.DefaultModel
                },
                providerName,
                request.Model ?? _config.DefaultModel,
                duration: duration);
        }
        catch (Exception ex)
        {
            return CapabilityResult<VectorGenerationResult>.Fail(ex.Message, "EMBEDDING_ERROR");
        }
    }

    /// <summary>
    /// Performs semantic search by finding the most similar items to a query.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The semantic search request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The semantic search result.</returns>
    public async Task<CapabilityResult<SemanticSearchResult>> SemanticSearchAsync(
        InstanceCapabilityConfig instanceConfig,
        SemanticSearchRequest request,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, SemanticSearch))
            return CapabilityResult<SemanticSearchResult>.Disabled(SemanticSearch);

        var providerName = GetProviderForCapability(instanceConfig, SemanticSearch);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<SemanticSearchResult>.NoProvider(SemanticSearch);

        var provider = _providerResolver(providerName);
        if (provider == null)
            return CapabilityResult<SemanticSearchResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");

        if (!provider.SupportsEmbeddings)
            return CapabilityResult<SemanticSearchResult>.Fail($"Provider '{providerName}' does not support embeddings.", "EMBEDDINGS_NOT_SUPPORTED");

        try
        {
            var startTime = DateTime.UtcNow;

            // Generate query embedding
            float[] queryVector;
            if (request.QueryVector != null)
            {
                queryVector = request.QueryVector;
            }
            else if (!string.IsNullOrEmpty(request.QueryText))
            {
                var embeddings = await provider.EmbedAsync(new[] { request.QueryText }, request.Model, ct);
                queryVector = embeddings[0].Select(d => (float)d).ToArray();
            }
            else
            {
                return CapabilityResult<SemanticSearchResult>.Fail("Either QueryText or QueryVector must be provided.", "NO_QUERY");
            }

            // Find top-k matches
            var matches = _vectorOps.FindTopK(
                queryVector,
                request.Candidates,
                request.TopK,
                request.Metric);

            // Apply minimum score filter
            var filteredMatches = request.MinScore > 0
                ? matches.Where(m => m.Score >= request.MinScore).ToList()
                : matches.ToList();

            var duration = DateTime.UtcNow - startTime;

            return CapabilityResult<SemanticSearchResult>.Ok(
                new SemanticSearchResult
                {
                    Matches = filteredMatches,
                    TotalCandidates = request.Candidates.Count(),
                    QueryDimensions = queryVector.Length
                },
                providerName,
                request.Model ?? _config.DefaultModel,
                duration: duration);
        }
        catch (Exception ex)
        {
            return CapabilityResult<SemanticSearchResult>.Fail(ex.Message, "SEARCH_ERROR");
        }
    }

    /// <summary>
    /// Calculates similarity between pairs of texts or vectors.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The similarity matching request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The similarity matching result.</returns>
    public async Task<CapabilityResult<SimilarityResult>> CalculateSimilarityAsync(
        InstanceCapabilityConfig instanceConfig,
        SimilarityRequest request,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, SimilarityMatching))
            return CapabilityResult<SimilarityResult>.Disabled(SimilarityMatching);

        var providerName = GetProviderForCapability(instanceConfig, SimilarityMatching);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<SimilarityResult>.NoProvider(SimilarityMatching);

        var provider = _providerResolver(providerName);
        if (provider == null)
            return CapabilityResult<SimilarityResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");

        try
        {
            var startTime = DateTime.UtcNow;

            float[] vectorA, vectorB;

            // Get vectors (either provided or generate from text)
            if (request.VectorA != null && request.VectorB != null)
            {
                vectorA = request.VectorA;
                vectorB = request.VectorB;
            }
            else if (!string.IsNullOrEmpty(request.TextA) && !string.IsNullOrEmpty(request.TextB))
            {
                if (!provider.SupportsEmbeddings)
                    return CapabilityResult<SimilarityResult>.Fail($"Provider '{providerName}' does not support embeddings for text similarity.", "EMBEDDINGS_NOT_SUPPORTED");

                var embeddings = await provider.EmbedAsync(new[] { request.TextA, request.TextB }, request.Model, ct);
                vectorA = embeddings[0].Select(d => (float)d).ToArray();
                vectorB = embeddings[1].Select(d => (float)d).ToArray();
            }
            else
            {
                return CapabilityResult<SimilarityResult>.Fail("Either (TextA, TextB) or (VectorA, VectorB) must be provided.", "INVALID_INPUT");
            }

            // Calculate similarity based on metric
            var score = request.Metric switch
            {
                SimilarityMetric.Cosine => _vectorOps.CosineSimilarity(vectorA, vectorB),
                SimilarityMetric.Euclidean => -_vectorOps.EuclideanDistance(vectorA, vectorB), // Negate so higher is better
                SimilarityMetric.DotProduct => _vectorOps.DotProduct(vectorA, vectorB),
                _ => _vectorOps.CosineSimilarity(vectorA, vectorB)
            };

            var duration = DateTime.UtcNow - startTime;

            return CapabilityResult<SimilarityResult>.Ok(
                new SimilarityResult
                {
                    Score = score,
                    Metric = request.Metric,
                    Interpretation = InterpretSimilarityScore(score, request.Metric),
                    VectorDimensions = vectorA.Length
                },
                providerName,
                request.Model ?? _config.DefaultModel,
                duration: duration);
        }
        catch (Exception ex)
        {
            return CapabilityResult<SimilarityResult>.Fail(ex.Message, "SIMILARITY_ERROR");
        }
    }

    /// <summary>
    /// Clusters vectors into groups based on similarity.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The clustering request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The clustering result.</returns>
    public async Task<CapabilityResult<ClusteringResult>> ClusterAsync(
        InstanceCapabilityConfig instanceConfig,
        ClusteringRequest request,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, Clustering))
            return CapabilityResult<ClusteringResult>.Disabled(Clustering);

        var providerName = GetProviderForCapability(instanceConfig, Clustering);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<ClusteringResult>.NoProvider(Clustering);

        var provider = _providerResolver(providerName);
        if (provider == null)
            return CapabilityResult<ClusteringResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");

        try
        {
            var startTime = DateTime.UtcNow;

            // Get or generate vectors
            var vectors = new List<VectorEntry>();
            if (request.Vectors != null && request.Vectors.Any())
            {
                vectors = request.Vectors.ToList();
            }
            else if (request.Texts != null && request.Texts.Any())
            {
                if (!provider.SupportsEmbeddings)
                    return CapabilityResult<ClusteringResult>.Fail($"Provider '{providerName}' does not support embeddings.", "EMBEDDINGS_NOT_SUPPORTED");

                var textsArray = request.Texts.ToArray();
                var embeddings = await provider.EmbedAsync(textsArray, request.Model, ct);

                for (int i = 0; i < textsArray.Length; i++)
                {
                    vectors.Add(new VectorEntry
                    {
                        Id = $"text_{i}",
                        Vector = embeddings[i].Select(d => (float)d).ToArray(),
                        Metadata = new Dictionary<string, object> { ["text"] = textsArray[i] }
                    });
                }
            }
            else
            {
                return CapabilityResult<ClusteringResult>.Fail("Either Vectors or Texts must be provided.", "INVALID_INPUT");
            }

            // Perform clustering using k-means algorithm
            var clusters = PerformKMeansClustering(
                vectors,
                request.NumClusters ?? EstimateClusterCount(vectors.Count),
                request.MaxIterations,
                request.Metric);

            var duration = DateTime.UtcNow - startTime;

            return CapabilityResult<ClusteringResult>.Ok(
                new ClusteringResult
                {
                    Clusters = clusters,
                    TotalItems = vectors.Count,
                    NumClusters = clusters.Count
                },
                providerName,
                request.Model ?? _config.DefaultModel,
                duration: duration);
        }
        catch (Exception ex)
        {
            return CapabilityResult<ClusteringResult>.Fail(ex.Message, "CLUSTERING_ERROR");
        }
    }

    #region Private Helpers

    private static string InterpretSimilarityScore(float score, SimilarityMetric metric)
    {
        if (metric == SimilarityMetric.Euclidean)
        {
            // For Euclidean, lower (more negative after negation) is more similar
            var distance = -score;
            return distance switch
            {
                < 0.5f => "Very similar",
                < 1.0f => "Similar",
                < 2.0f => "Moderately similar",
                < 5.0f => "Somewhat different",
                _ => "Very different"
            };
        }

        // For cosine and dot product
        return score switch
        {
            >= 0.9f => "Nearly identical",
            >= 0.8f => "Very similar",
            >= 0.6f => "Similar",
            >= 0.4f => "Moderately similar",
            >= 0.2f => "Somewhat different",
            _ => "Very different"
        };
    }

    private static int EstimateClusterCount(int itemCount)
    {
        // Rule of thumb: sqrt(n/2)
        return Math.Max(2, (int)Math.Sqrt(itemCount / 2.0));
    }

    private List<VectorCluster> PerformKMeansClustering(
        List<VectorEntry> vectors,
        int k,
        int maxIterations,
        SimilarityMetric metric)
    {
        if (vectors.Count == 0 || k <= 0)
            return new List<VectorCluster>();

        k = Math.Min(k, vectors.Count);
        var dimensions = vectors[0].Vector.Length;
        var random = new Random(42); // Deterministic seed for reproducibility

        // Initialize centroids randomly
        var centroids = vectors
            .OrderBy(_ => random.Next())
            .Take(k)
            .Select(v => v.Vector.ToArray())
            .ToList();

        var assignments = new int[vectors.Count];
        var changed = true;
        var iterations = 0;

        while (changed && iterations < maxIterations)
        {
            changed = false;
            iterations++;

            // Assign each point to nearest centroid
            for (int i = 0; i < vectors.Count; i++)
            {
                var nearestCluster = 0;
                var bestDistance = float.MaxValue;

                for (int j = 0; j < k; j++)
                {
                    var distance = metric == SimilarityMetric.Cosine
                        ? 1 - _vectorOps.CosineSimilarity(vectors[i].Vector, centroids[j])
                        : _vectorOps.EuclideanDistance(vectors[i].Vector, centroids[j]);

                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        nearestCluster = j;
                    }
                }

                if (assignments[i] != nearestCluster)
                {
                    assignments[i] = nearestCluster;
                    changed = true;
                }
            }

            // Update centroids
            for (int j = 0; j < k; j++)
            {
                var clusterPoints = vectors
                    .Where((_, idx) => assignments[idx] == j)
                    .Select(v => v.Vector)
                    .ToList();

                if (clusterPoints.Count > 0)
                {
                    var newCentroid = new float[dimensions];
                    foreach (var point in clusterPoints)
                    {
                        for (int d = 0; d < dimensions; d++)
                        {
                            newCentroid[d] += point[d];
                        }
                    }
                    for (int d = 0; d < dimensions; d++)
                    {
                        newCentroid[d] /= clusterPoints.Count;
                    }
                    centroids[j] = newCentroid;
                }
            }
        }

        // Build cluster results
        var clusters = new List<VectorCluster>();
        for (int j = 0; j < k; j++)
        {
            var clusterMembers = vectors
                .Select((v, idx) => (vector: v, index: idx))
                .Where(x => assignments[x.index] == j)
                .Select(x => new ClusterMember
                {
                    Entry = x.vector,
                    DistanceFromCentroid = _vectorOps.EuclideanDistance(x.vector.Vector, centroids[j])
                })
                .OrderBy(m => m.DistanceFromCentroid)
                .ToList();

            if (clusterMembers.Count > 0)
            {
                clusters.Add(new VectorCluster
                {
                    ClusterId = j,
                    Centroid = centroids[j],
                    Members = clusterMembers,
                    Size = clusterMembers.Count
                });
            }
        }

        return clusters;
    }

    #endregion
}

#region Vector Operations Interface and Implementation

/// <summary>
/// Interface for vector mathematical operations.
/// </summary>
public interface IVectorOperations
{
    /// <summary>
    /// Calculates cosine similarity between two vectors.
    /// </summary>
    float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b);

    /// <summary>
    /// Calculates Euclidean distance between two vectors.
    /// </summary>
    float EuclideanDistance(ReadOnlySpan<float> a, ReadOnlySpan<float> b);

    /// <summary>
    /// Calculates dot product of two vectors.
    /// </summary>
    float DotProduct(ReadOnlySpan<float> a, ReadOnlySpan<float> b);

    /// <summary>
    /// Normalizes a vector to unit length.
    /// </summary>
    float[] Normalize(ReadOnlySpan<float> vector);

    /// <summary>
    /// Finds the top-k most similar vectors from a collection.
    /// </summary>
    IEnumerable<VectorMatch> FindTopK(
        ReadOnlySpan<float> query,
        IEnumerable<VectorEntry> candidates,
        int k,
        SimilarityMetric metric = SimilarityMetric.Cosine);
}

/// <summary>
/// Default implementation of vector operations.
/// </summary>
public sealed class DefaultVectorOperations : IVectorOperations
{
    /// <inheritdoc/>
    public float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Vectors must have same dimension");

        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        float denominator = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denominator == 0 ? 0 : dot / denominator;
    }

    /// <inheritdoc/>
    public float EuclideanDistance(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Vectors must have same dimension");

        float sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            float diff = a[i] - b[i];
            sum += diff * diff;
        }
        return MathF.Sqrt(sum);
    }

    /// <inheritdoc/>
    public float DotProduct(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Vectors must have same dimension");

        float sum = 0;
        for (int i = 0; i < a.Length; i++)
            sum += a[i] * b[i];
        return sum;
    }

    /// <inheritdoc/>
    public float[] Normalize(ReadOnlySpan<float> vector)
    {
        float magnitude = 0;
        for (int i = 0; i < vector.Length; i++)
            magnitude += vector[i] * vector[i];
        magnitude = MathF.Sqrt(magnitude);

        float[] result = new float[vector.Length];
        if (magnitude > 0)
        {
            for (int i = 0; i < vector.Length; i++)
                result[i] = vector[i] / magnitude;
        }
        return result;
    }

    /// <inheritdoc/>
    public IEnumerable<VectorMatch> FindTopK(
        ReadOnlySpan<float> query,
        IEnumerable<VectorEntry> candidates,
        int k,
        SimilarityMetric metric = SimilarityMetric.Cosine)
    {
        var scores = new List<(VectorEntry entry, float score)>();

        foreach (var candidate in candidates)
        {
            float score = metric switch
            {
                SimilarityMetric.Cosine => CosineSimilarity(query, candidate.Vector),
                SimilarityMetric.Euclidean => -EuclideanDistance(query, candidate.Vector),
                SimilarityMetric.DotProduct => DotProduct(query, candidate.Vector),
                _ => CosineSimilarity(query, candidate.Vector)
            };
            scores.Add((candidate, score));
        }

        scores.Sort((a, b) => b.score.CompareTo(a.score));

        var results = new List<VectorMatch>();
        for (int i = 0; i < Math.Min(k, scores.Count); i++)
        {
            results.Add(new VectorMatch
            {
                Entry = scores[i].entry,
                Score = scores[i].score,
                Rank = i + 1
            });
        }
        return results;
    }
}

#endregion

#region Data Types

/// <summary>
/// Configuration for the embeddings capability handler.
/// </summary>
public sealed class EmbeddingsConfig
{
    /// <summary>Default provider for embeddings.</summary>
    public string DefaultProvider { get; init; } = "openai";

    /// <summary>Default embedding model.</summary>
    public string DefaultModel { get; init; } = "text-embedding-3-small";

    /// <summary>Default number of dimensions.</summary>
    public int DefaultDimensions { get; init; } = 1536;
}

/// <summary>
/// Similarity metric for vector comparisons.
/// </summary>
public enum SimilarityMetric
{
    /// <summary>Cosine similarity (angle-based, normalized).</summary>
    Cosine,
    /// <summary>Euclidean distance (L2 norm).</summary>
    Euclidean,
    /// <summary>Dot product (magnitude-sensitive).</summary>
    DotProduct
}

/// <summary>
/// A stored vector entry with ID and optional metadata.
/// </summary>
public sealed class VectorEntry
{
    /// <summary>Unique identifier for this vector.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The embedding vector.</summary>
    public float[] Vector { get; init; } = Array.Empty<float>();

    /// <summary>Optional metadata associated with this vector.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();

    /// <summary>Timestamp when this vector was stored.</summary>
    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>
/// Result of a vector similarity search.
/// </summary>
public sealed class VectorMatch
{
    /// <summary>The matched vector entry.</summary>
    public VectorEntry Entry { get; init; } = new();

    /// <summary>Similarity score.</summary>
    public float Score { get; init; }

    /// <summary>Rank in the search results (1-based).</summary>
    public int Rank { get; init; }
}

/// <summary>
/// Request for vector generation.
/// </summary>
public sealed class VectorGenerationRequest
{
    /// <summary>Single text to embed.</summary>
    public string? Text { get; init; }

    /// <summary>Multiple texts to embed.</summary>
    public string[]? Texts { get; init; }

    /// <summary>Specific embedding model to use.</summary>
    public string? Model { get; init; }

    /// <summary>Whether to normalize vectors.</summary>
    public bool Normalize { get; init; } = true;
}

/// <summary>
/// Result of vector generation.
/// </summary>
public sealed class VectorGenerationResult
{
    /// <summary>Generated vectors.</summary>
    public float[][] Vectors { get; init; } = Array.Empty<float[]>();

    /// <summary>Dimension of each vector.</summary>
    public int Dimensions { get; init; }

    /// <summary>Number of vectors generated.</summary>
    public int Count { get; init; }

    /// <summary>Model used for generation.</summary>
    public string Model { get; init; } = string.Empty;
}

/// <summary>
/// Request for semantic search.
/// </summary>
public sealed class SemanticSearchRequest
{
    /// <summary>Query text to search for.</summary>
    public string? QueryText { get; init; }

    /// <summary>Pre-computed query vector.</summary>
    public float[]? QueryVector { get; init; }

    /// <summary>Candidates to search through.</summary>
    public IEnumerable<VectorEntry> Candidates { get; init; } = Enumerable.Empty<VectorEntry>();

    /// <summary>Number of results to return.</summary>
    public int TopK { get; init; } = 10;

    /// <summary>Minimum similarity score threshold.</summary>
    public float MinScore { get; init; } = 0;

    /// <summary>Similarity metric to use.</summary>
    public SimilarityMetric Metric { get; init; } = SimilarityMetric.Cosine;

    /// <summary>Specific model for embedding the query.</summary>
    public string? Model { get; init; }
}

/// <summary>
/// Result of semantic search.
/// </summary>
public sealed class SemanticSearchResult
{
    /// <summary>Matching items ordered by relevance.</summary>
    public List<VectorMatch> Matches { get; init; } = new();

    /// <summary>Total number of candidates searched.</summary>
    public int TotalCandidates { get; init; }

    /// <summary>Dimensions of the query vector.</summary>
    public int QueryDimensions { get; init; }
}

/// <summary>
/// Request for similarity calculation.
/// </summary>
public sealed class SimilarityRequest
{
    /// <summary>First text to compare.</summary>
    public string? TextA { get; init; }

    /// <summary>Second text to compare.</summary>
    public string? TextB { get; init; }

    /// <summary>First pre-computed vector.</summary>
    public float[]? VectorA { get; init; }

    /// <summary>Second pre-computed vector.</summary>
    public float[]? VectorB { get; init; }

    /// <summary>Similarity metric to use.</summary>
    public SimilarityMetric Metric { get; init; } = SimilarityMetric.Cosine;

    /// <summary>Model for generating embeddings.</summary>
    public string? Model { get; init; }
}

/// <summary>
/// Result of similarity calculation.
/// </summary>
public sealed class SimilarityResult
{
    /// <summary>Similarity score.</summary>
    public float Score { get; init; }

    /// <summary>Metric used for calculation.</summary>
    public SimilarityMetric Metric { get; init; }

    /// <summary>Human-readable interpretation of the score.</summary>
    public string Interpretation { get; init; } = string.Empty;

    /// <summary>Dimensions of the vectors compared.</summary>
    public int VectorDimensions { get; init; }
}

/// <summary>
/// Request for clustering.
/// </summary>
public sealed class ClusteringRequest
{
    /// <summary>Texts to cluster (will be embedded).</summary>
    public IEnumerable<string>? Texts { get; init; }

    /// <summary>Pre-computed vectors to cluster.</summary>
    public IEnumerable<VectorEntry>? Vectors { get; init; }

    /// <summary>Number of clusters to create (null for auto).</summary>
    public int? NumClusters { get; init; }

    /// <summary>Maximum iterations for clustering algorithm.</summary>
    public int MaxIterations { get; init; } = 100;

    /// <summary>Similarity metric for clustering.</summary>
    public SimilarityMetric Metric { get; init; } = SimilarityMetric.Cosine;

    /// <summary>Model for generating embeddings.</summary>
    public string? Model { get; init; }
}

/// <summary>
/// Result of clustering.
/// </summary>
public sealed class ClusteringResult
{
    /// <summary>The resulting clusters.</summary>
    public List<VectorCluster> Clusters { get; init; } = new();

    /// <summary>Total items clustered.</summary>
    public int TotalItems { get; init; }

    /// <summary>Number of clusters created.</summary>
    public int NumClusters { get; init; }
}

/// <summary>
/// A cluster of vectors.
/// </summary>
public sealed class VectorCluster
{
    /// <summary>Cluster identifier.</summary>
    public int ClusterId { get; init; }

    /// <summary>Cluster centroid vector.</summary>
    public float[] Centroid { get; init; } = Array.Empty<float>();

    /// <summary>Members of this cluster.</summary>
    public List<ClusterMember> Members { get; init; } = new();

    /// <summary>Number of items in the cluster.</summary>
    public int Size { get; init; }
}

/// <summary>
/// A member of a cluster.
/// </summary>
public sealed class ClusterMember
{
    /// <summary>The vector entry.</summary>
    public VectorEntry Entry { get; init; } = new();

    /// <summary>Distance from the cluster centroid.</summary>
    public float DistanceFromCentroid { get; init; }
}

#endregion
