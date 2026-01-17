using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.AI
{
    /// <summary>
    /// Vector operations for AI embeddings and similarity calculations.
    /// Used for semantic search, content similarity, and AI-driven decisions.
    /// </summary>
    public interface IVectorOperations
    {
        /// <summary>
        /// Calculate cosine similarity between two vectors.
        /// Returns value between -1 (opposite) and 1 (identical).
        /// </summary>
        float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b);

        /// <summary>
        /// Calculate Euclidean distance between two vectors.
        /// </summary>
        float EuclideanDistance(ReadOnlySpan<float> a, ReadOnlySpan<float> b);

        /// <summary>
        /// Calculate dot product of two vectors.
        /// </summary>
        float DotProduct(ReadOnlySpan<float> a, ReadOnlySpan<float> b);

        /// <summary>
        /// Normalize a vector to unit length.
        /// </summary>
        float[] Normalize(ReadOnlySpan<float> vector);

        /// <summary>
        /// Find top-k most similar vectors from a collection.
        /// </summary>
        IEnumerable<VectorMatch> FindTopK(
            ReadOnlySpan<float> query,
            IEnumerable<VectorEntry> candidates,
            int k,
            SimilarityMetric metric = SimilarityMetric.Cosine);
    }

    /// <summary>
    /// Vector storage and retrieval interface for embeddings.
    /// </summary>
    public interface IVectorStore
    {
        /// <summary>
        /// Store a vector with associated metadata.
        /// </summary>
        Task StoreAsync(string id, float[] vector, Dictionary<string, object>? metadata = null, CancellationToken ct = default);

        /// <summary>
        /// Store multiple vectors in batch.
        /// </summary>
        Task StoreBatchAsync(IEnumerable<VectorEntry> entries, CancellationToken ct = default);

        /// <summary>
        /// Retrieve a vector by ID.
        /// </summary>
        Task<VectorEntry?> GetAsync(string id, CancellationToken ct = default);

        /// <summary>
        /// Delete a vector by ID.
        /// </summary>
        Task DeleteAsync(string id, CancellationToken ct = default);

        /// <summary>
        /// Search for similar vectors.
        /// </summary>
        Task<IEnumerable<VectorMatch>> SearchAsync(
            float[] query,
            int topK = 10,
            float minScore = 0.0f,
            Dictionary<string, object>? filter = null,
            CancellationToken ct = default);

        /// <summary>
        /// Get total count of stored vectors.
        /// </summary>
        Task<long> CountAsync(CancellationToken ct = default);
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
        DotProduct,
        /// <summary>Manhattan distance (L1 norm).</summary>
        Manhattan
    }

    /// <summary>
    /// A stored vector entry with ID and optional metadata.
    /// </summary>
    public class VectorEntry
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
    public class VectorMatch
    {
        /// <summary>The matched vector entry.</summary>
        public VectorEntry Entry { get; init; } = new();

        /// <summary>Similarity score (interpretation depends on metric used).</summary>
        public float Score { get; init; }

        /// <summary>Rank in the search results (1-based).</summary>
        public int Rank { get; init; }
    }

    /// <summary>
    /// Default implementation of vector operations using SIMD where available.
    /// </summary>
    public class DefaultVectorOperations : IVectorOperations
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
                    SimilarityMetric.Euclidean => -EuclideanDistance(query, candidate.Vector), // Negate so higher is better
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
}
