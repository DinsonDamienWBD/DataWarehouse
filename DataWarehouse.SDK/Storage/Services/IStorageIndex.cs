using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Storage;

namespace DataWarehouse.SDK.Storage.Services;

/// <summary>
/// Composable storage indexing service (AD-03).
/// Extracted from IndexableStoragePluginBase to enable composition without inheritance.
/// Any storage plugin can use indexing by accepting an IStorageIndex dependency.
/// </summary>
public interface IStorageIndex
{
    /// <summary>Indexes metadata for a storage object.</summary>
    /// <param name="key">The storage key of the object.</param>
    /// <param name="metadata">The metadata to index.</param>
    /// <param name="ct">Cancellation token.</param>
    Task IndexAsync(string key, StorageObjectMetadata metadata, CancellationToken ct = default);

    /// <summary>Searches the index with a query string.</summary>
    /// <param name="query">The search query (matched against key and content type).</param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Matching storage object metadata entries.</returns>
    Task<IReadOnlyList<StorageObjectMetadata>> SearchAsync(string query, int maxResults = 100, CancellationToken ct = default);

    /// <summary>Removes an entry from the index.</summary>
    /// <param name="key">The storage key to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveAsync(string key, CancellationToken ct = default);

    /// <summary>Gets the total number of indexed entries.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of entries in the index.</returns>
    Task<long> GetIndexCountAsync(CancellationToken ct = default);

    /// <summary>Rebuilds the index from scratch using the provided objects.</summary>
    /// <param name="allObjects">All objects to re-index.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RebuildAsync(IAsyncEnumerable<StorageObjectMetadata> allObjects, CancellationToken ct = default);
}
