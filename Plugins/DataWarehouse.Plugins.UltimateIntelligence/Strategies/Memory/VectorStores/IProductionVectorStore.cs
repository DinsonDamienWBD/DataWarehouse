using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.VectorStores;

/// <summary>
/// Distance metric for vector similarity calculations.
/// </summary>
public enum DistanceMetric
{
    /// <summary>Cosine similarity (angle-based, normalized). Most common for text embeddings.</summary>
    Cosine,

    /// <summary>Euclidean distance (L2 norm). Good for dense, low-dimensional vectors.</summary>
    Euclidean,

    /// <summary>Dot product (magnitude-sensitive). Efficient when vectors are normalized.</summary>
    DotProduct,

    /// <summary>Manhattan distance (L1 norm). Robust to outliers.</summary>
    Manhattan
}

/// <summary>
/// Record representing a vector with its ID and optional metadata.
/// </summary>
/// <param name="Id">Unique identifier for the vector.</param>
/// <param name="Vector">The embedding vector values.</param>
/// <param name="Metadata">Optional metadata associated with the vector.</param>
public record VectorRecord(string Id, float[] Vector, Dictionary<string, object>? Metadata = null);

/// <summary>
/// Result from a vector similarity search operation.
/// </summary>
/// <param name="Id">The matched vector's unique identifier.</param>
/// <param name="Score">Similarity score (higher is more similar for cosine/dot, lower for distance metrics).</param>
/// <param name="Vector">The matched vector values (may be null if not requested).</param>
/// <param name="Metadata">Metadata associated with the matched vector.</param>
public record VectorSearchResult(string Id, float Score, float[]? Vector, Dictionary<string, object>? Metadata);

/// <summary>
/// Statistics about a vector store's state and usage.
/// </summary>
public sealed class VectorStoreStatistics
{
    /// <summary>Total number of vectors stored.</summary>
    public long TotalVectors { get; init; }

    /// <summary>Number of dimensions per vector.</summary>
    public int Dimensions { get; init; }

    /// <summary>Total storage size in bytes (if available).</summary>
    public long? StorageSizeBytes { get; init; }

    /// <summary>Number of collections/indexes.</summary>
    public int CollectionCount { get; init; }

    /// <summary>Number of search queries performed.</summary>
    public long QueryCount { get; init; }

    /// <summary>Average query latency in milliseconds.</summary>
    public double AverageQueryLatencyMs { get; init; }

    /// <summary>Index type (e.g., "HNSW", "IVF_FLAT").</summary>
    public string? IndexType { get; init; }

    /// <summary>Distance metric used.</summary>
    public DistanceMetric Metric { get; init; }

    /// <summary>Additional provider-specific statistics.</summary>
    public Dictionary<string, object> ExtendedStats { get; init; } = new();
}

/// <summary>
/// Interface for embedding providers that convert text to vectors.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>
    /// Generate an embedding vector for the given text.
    /// </summary>
    /// <param name="text">Text to embed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Embedding vector.</returns>
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// Generate embedding vectors for multiple texts in batch.
    /// </summary>
    /// <param name="texts">Texts to embed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Array of embedding vectors.</returns>
    Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default);

    /// <summary>
    /// The dimensionality of vectors produced by this embedder.
    /// </summary>
    int Dimensions { get; }
}

/// <summary>
/// Production-ready vector store interface with full CRUD, search, and collection management.
/// Designed for tiered memory systems with support for multiple vector database backends.
/// </summary>
/// <remarks>
/// This interface provides a unified abstraction over various vector databases including
/// Pinecone, Qdrant, Weaviate, Milvus, Chroma, Redis, PostgreSQL/pgvector, Elasticsearch,
/// and Azure AI Search. Implementations must handle:
/// <list type="bullet">
/// <item>Connection pooling where applicable</item>
/// <item>Secure authentication handling</item>
/// <item>Retry with exponential backoff and circuit breaker patterns</item>
/// <item>Metrics collection (latency, query count, index size)</item>
/// <item>Cancellation token support</item>
/// <item>Pagination for large result sets</item>
/// </list>
/// </remarks>
public interface IProductionVectorStore : IAsyncDisposable
{
    /// <summary>
    /// Gets the unique identifier for this vector store instance.
    /// </summary>
    string StoreId { get; }

    /// <summary>
    /// Gets the human-readable display name for this store.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Gets the dimensionality of vectors stored (0 if variable or unknown).
    /// </summary>
    int VectorDimensions { get; }

    #region CRUD Operations

    /// <summary>
    /// Upsert (insert or update) a single vector with optional metadata.
    /// </summary>
    /// <param name="id">Unique identifier for the vector.</param>
    /// <param name="vector">The embedding vector values.</param>
    /// <param name="metadata">Optional metadata to associate with the vector.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpsertAsync(string id, float[] vector, Dictionary<string, object>? metadata = null, CancellationToken ct = default);

    /// <summary>
    /// Upsert multiple vectors in a single batch operation.
    /// </summary>
    /// <param name="records">Collection of vector records to upsert.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpsertBatchAsync(IEnumerable<VectorRecord> records, CancellationToken ct = default);

    /// <summary>
    /// Retrieve a vector by its unique identifier.
    /// </summary>
    /// <param name="id">The vector's unique identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The vector record if found, null otherwise.</returns>
    Task<VectorRecord?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Delete a vector by its unique identifier.
    /// </summary>
    /// <param name="id">The vector's unique identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Delete multiple vectors by their identifiers.
    /// </summary>
    /// <param name="ids">Collection of vector identifiers to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteBatchAsync(IEnumerable<string> ids, CancellationToken ct = default);

    #endregion

    #region Search Operations

    /// <summary>
    /// Search for similar vectors using a query vector.
    /// </summary>
    /// <param name="query">The query vector to find similar vectors for.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    /// <param name="minScore">Minimum similarity score threshold (0 to disable).</param>
    /// <param name="filter">Optional metadata filter for pre-filtering results.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ordered collection of search results by similarity.</returns>
    Task<IEnumerable<VectorSearchResult>> SearchAsync(
        float[] query,
        int topK = 10,
        float minScore = 0f,
        Dictionary<string, object>? filter = null,
        CancellationToken ct = default);

    /// <summary>
    /// Search for similar vectors using text that will be embedded.
    /// </summary>
    /// <param name="text">Text to embed and search with.</param>
    /// <param name="embedder">Embedding provider to convert text to vector.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ordered collection of search results by similarity.</returns>
    Task<IEnumerable<VectorSearchResult>> SearchByTextAsync(
        string text,
        IEmbeddingProvider embedder,
        int topK = 10,
        CancellationToken ct = default);

    #endregion

    #region Collection/Index Management

    /// <summary>
    /// Create a new collection/index for vectors.
    /// </summary>
    /// <param name="name">Name of the collection to create.</param>
    /// <param name="dimensions">Number of dimensions for vectors in this collection.</param>
    /// <param name="metric">Distance metric to use for similarity calculations.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CreateCollectionAsync(
        string name,
        int dimensions,
        DistanceMetric metric = DistanceMetric.Cosine,
        CancellationToken ct = default);

    /// <summary>
    /// Delete a collection/index and all its vectors.
    /// </summary>
    /// <param name="name">Name of the collection to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteCollectionAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Check if a collection/index exists.
    /// </summary>
    /// <param name="name">Name of the collection to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the collection exists, false otherwise.</returns>
    Task<bool> CollectionExistsAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Get statistics about the vector store's current state.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Statistics about the store.</returns>
    Task<VectorStoreStatistics> GetStatisticsAsync(CancellationToken ct = default);

    #endregion

    #region Health

    /// <summary>
    /// Check if the vector store is healthy and accepting connections.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if healthy, false otherwise.</returns>
    Task<bool> IsHealthyAsync(CancellationToken ct = default);

    #endregion
}

/// <summary>
/// Configuration options for production vector stores.
/// </summary>
public class VectorStoreOptions
{
    /// <summary>
    /// Maximum number of retry attempts for transient failures.
    /// </summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Initial delay between retry attempts in milliseconds.
    /// </summary>
    public int InitialRetryDelayMs { get; init; } = 100;

    /// <summary>
    /// Maximum delay between retry attempts in milliseconds.
    /// </summary>
    public int MaxRetryDelayMs { get; init; } = 5000;

    /// <summary>
    /// Number of failures before opening the circuit breaker.
    /// </summary>
    public int CircuitBreakerThreshold { get; init; } = 5;

    /// <summary>
    /// Duration in milliseconds before attempting to close the circuit.
    /// </summary>
    public int CircuitBreakerResetMs { get; init; } = 30000;

    /// <summary>
    /// Connection timeout in milliseconds.
    /// </summary>
    public int ConnectionTimeoutMs { get; init; } = 10000;

    /// <summary>
    /// Operation timeout in milliseconds.
    /// </summary>
    public int OperationTimeoutMs { get; init; } = 30000;

    /// <summary>
    /// Maximum batch size for bulk operations.
    /// </summary>
    public int MaxBatchSize { get; init; } = 100;

    /// <summary>
    /// Whether to include vectors in search results (can be expensive).
    /// </summary>
    public bool IncludeVectorsInSearch { get; init; } = false;

    /// <summary>
    /// Whether to enable metrics collection.
    /// </summary>
    public bool EnableMetrics { get; init; } = true;
}
