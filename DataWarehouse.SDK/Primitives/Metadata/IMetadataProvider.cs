namespace DataWarehouse.SDK.Primitives.Metadata;

/// <summary>
/// Provides metadata management capabilities for data warehouse objects.
/// </summary>
/// <remarks>
/// Metadata providers enable storage and retrieval of metadata associated with objects,
/// containers, or system-level entities. Metadata can include tags, custom attributes,
/// versioning information, lineage data, and other descriptive information.
/// </remarks>
public interface IMetadataProvider
{
    /// <summary>
    /// Gets the capabilities supported by this metadata provider.
    /// </summary>
    /// <value>
    /// A record describing the features and limits of this metadata provider.
    /// </value>
    MetadataCapabilities Capabilities { get; }

    /// <summary>
    /// Retrieves metadata for a specific object asynchronously.
    /// </summary>
    /// <param name="objectId">The unique identifier of the object.</param>
    /// <param name="scope">The scope of the metadata (object, container, or system).</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A dictionary of metadata key-value pairs, or null if no metadata exists.</returns>
    Task<IReadOnlyDictionary<string, object>?> GetAsync(
        string objectId,
        MetadataScope scope = MetadataScope.Object,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a specific metadata entry by key asynchronously.
    /// </summary>
    /// <param name="objectId">The unique identifier of the object.</param>
    /// <param name="key">The metadata key to retrieve.</param>
    /// <param name="scope">The scope of the metadata.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The metadata entry, or null if not found.</returns>
    Task<MetadataEntry?> GetAsync(
        string objectId,
        string key,
        MetadataScope scope = MetadataScope.Object,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets metadata for a specific object asynchronously.
    /// </summary>
    /// <param name="objectId">The unique identifier of the object.</param>
    /// <param name="metadata">The metadata key-value pairs to set.</param>
    /// <param name="scope">The scope of the metadata.</param>
    /// <param name="merge">If true, merge with existing metadata; if false, replace all metadata.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task SetAsync(
        string objectId,
        IReadOnlyDictionary<string, object> metadata,
        MetadataScope scope = MetadataScope.Object,
        bool merge = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a specific metadata entry asynchronously.
    /// </summary>
    /// <param name="objectId">The unique identifier of the object.</param>
    /// <param name="key">The metadata key.</param>
    /// <param name="value">The metadata value.</param>
    /// <param name="scope">The scope of the metadata.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task SetAsync(
        string objectId,
        string key,
        object value,
        MetadataScope scope = MetadataScope.Object,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes metadata for a specific object asynchronously.
    /// </summary>
    /// <param name="objectId">The unique identifier of the object.</param>
    /// <param name="scope">The scope of the metadata.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task DeleteAsync(
        string objectId,
        MetadataScope scope = MetadataScope.Object,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a specific metadata entry asynchronously.
    /// </summary>
    /// <param name="objectId">The unique identifier of the object.</param>
    /// <param name="key">The metadata key to delete.</param>
    /// <param name="scope">The scope of the metadata.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task DeleteAsync(
        string objectId,
        string key,
        MetadataScope scope = MetadataScope.Object,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries metadata across multiple objects asynchronously.
    /// </summary>
    /// <param name="query">The metadata query to execute.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>An enumerable of metadata query results.</returns>
    /// <remarks>
    /// Query syntax and capabilities depend on the specific provider implementation.
    /// Common query operations include filtering by tags, searching by values, and range queries.
    /// </remarks>
    IAsyncEnumerable<MetadataQueryResult> QueryAsync(
        MetadataQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the complete metadata history for an object (if versioning is supported).
    /// </summary>
    /// <param name="objectId">The unique identifier of the object.</param>
    /// <param name="scope">The scope of the metadata.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>An enumerable of historical metadata versions.</returns>
    /// <exception cref="NotSupportedException">Thrown if versioning is not supported by this provider.</exception>
    IAsyncEnumerable<MetadataVersion> GetHistoryAsync(
        string objectId,
        MetadataScope scope = MetadataScope.Object,
        CancellationToken cancellationToken = default);
}
