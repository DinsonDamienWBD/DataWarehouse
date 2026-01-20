using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.SDK.Contracts;

/// <summary>
/// Interface for storage providers that support indexing capabilities.
/// Enables storage plugins to also function as metadata indexes.
/// This interface combines IStorageProvider functionality with IMetadataIndex capabilities.
/// </summary>
public interface IIndexableStorage : ICacheableStorage, IMetadataIndex
{
    /// <summary>
    /// Index a document with custom metadata for search.
    /// This is a simpler alternative to IndexManifestAsync for basic use cases.
    /// </summary>
    /// <param name="id">Document identifier.</param>
    /// <param name="metadata">Searchable metadata key-value pairs.</param>
    /// <param name="ct">Cancellation token.</param>
    Task IndexDocumentAsync(string id, Dictionary<string, object> metadata, CancellationToken ct = default);

    /// <summary>
    /// Remove a document from the index.
    /// </summary>
    /// <param name="id">Document identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if removed, false if not found.</returns>
    Task<bool> RemoveFromIndexAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Search the index and return matching document IDs.
    /// </summary>
    /// <param name="query">Search query (format depends on implementation).</param>
    /// <param name="limit">Maximum results to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Array of matching document IDs.</returns>
    Task<string[]> SearchIndexAsync(string query, int limit = 100, CancellationToken ct = default);

    /// <summary>
    /// Query documents by metadata field values.
    /// </summary>
    /// <param name="criteria">Field-value pairs to match.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Array of matching document IDs.</returns>
    Task<string[]> QueryByMetadataAsync(Dictionary<string, object> criteria, CancellationToken ct = default);

    /// <summary>
    /// Get index statistics.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IndexStatistics> GetIndexStatisticsAsync(CancellationToken ct = default);

    /// <summary>
    /// Rebuild the entire index from stored data.
    /// Use sparingly as this can be resource-intensive.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of items reindexed.</returns>
    Task<int> RebuildIndexAsync(CancellationToken ct = default);

    /// <summary>
    /// Optimize the index for better query performance.
    /// Implementation-specific (e.g., compact, defragment, merge segments).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task OptimizeIndexAsync(CancellationToken ct = default);
}

/// <summary>
/// Statistics about the search index.
/// </summary>
public class IndexStatistics
{
    /// <summary>Total number of indexed documents.</summary>
    public long DocumentCount { get; set; }

    /// <summary>Total size of the index in bytes.</summary>
    public long IndexSizeBytes { get; set; }

    /// <summary>Number of unique terms in the index.</summary>
    public long TermCount { get; set; }

    /// <summary>When the index was last updated.</summary>
    public DateTime LastUpdated { get; set; }

    /// <summary>Whether the index is currently being rebuilt.</summary>
    public bool IsRebuilding { get; set; }

    /// <summary>Progress of rebuild if in progress (0.0 to 1.0).</summary>
    public double RebuildProgress { get; set; }

    /// <summary>Average query time in milliseconds.</summary>
    public double AverageQueryTimeMs { get; set; }

    /// <summary>Total queries executed.</summary>
    public long TotalQueries { get; set; }

    /// <summary>Index type (SQLite, InMemory, etc.).</summary>
    public string IndexType { get; set; } = "Unknown";
}

/// <summary>
/// Configuration for indexable storage behavior.
/// </summary>
public class IndexableStorageOptions
{
    /// <summary>Whether to auto-index documents on save.</summary>
    public bool AutoIndexOnSave { get; set; } = true;

    /// <summary>Whether to remove from index on delete.</summary>
    public bool AutoRemoveOnDelete { get; set; } = true;

    /// <summary>Fields to extract and index from document content.</summary>
    public string[] IndexableFields { get; set; } = Array.Empty<string>();

    /// <summary>Whether to enable full-text search.</summary>
    public bool EnableFullTextSearch { get; set; } = true;

    /// <summary>Whether to enable vector similarity search.</summary>
    public bool EnableVectorSearch { get; set; } = false;

    /// <summary>Maximum number of results per query.</summary>
    public int MaxQueryResults { get; set; } = 1000;

    /// <summary>Index storage path (for sidecar indexes).</summary>
    public string? IndexPath { get; set; }

    /// <summary>
    /// Default options for most use cases.
    /// </summary>
    public static IndexableStorageOptions Default => new()
    {
        AutoIndexOnSave = true,
        AutoRemoveOnDelete = true,
        EnableFullTextSearch = true,
        EnableVectorSearch = false,
        MaxQueryResults = 1000
    };
}
