using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags;

/// <summary>
/// Contract for tag indexing and querying. Provides an inverted index that maps
/// tag keys to object keys for O(1) amortized tag-to-object lookups at scale.
/// </summary>
/// <remarks>
/// The inverted index is the foundation for the Tag Query API (Plan 09).
/// Implementations should be optimized for:
/// <list type="bullet">
/// <item><description>Fast tag-to-object lookups (billions of objects)</description></item>
/// <item><description>Concurrent read/write access</description></item>
/// <item><description>Bounded memory via sharding</description></item>
/// <item><description>Full rebuild from external sources</description></item>
/// </list>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag index types")]
public interface ITagIndex
{
    /// <summary>
    /// Indexes a tag for the given object. If the object already has an index entry
    /// for this tag key, it is updated.
    /// </summary>
    /// <param name="objectKey">The storage key of the object.</param>
    /// <param name="tag">The tag to index.</param>
    /// <param name="ct">Cancellation token.</param>
    Task IndexAsync(string objectKey, Tag tag, CancellationToken ct = default);

    /// <summary>
    /// Removes a tag index entry for the given object and tag key.
    /// No-op if the entry does not exist.
    /// </summary>
    /// <param name="objectKey">The storage key of the object.</param>
    /// <param name="tagKey">The tag key to remove from the index.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveAsync(string objectKey, TagKey tagKey, CancellationToken ct = default);

    /// <summary>
    /// Queries the index using the specified filters, pagination, and options.
    /// All filters are combined with AND semantics.
    /// </summary>
    /// <param name="query">The query descriptor containing filters and pagination.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The query result with matching object keys and metadata.</returns>
    Task<TagIndexResult> QueryAsync(TagIndexQuery query, CancellationToken ct = default);

    /// <summary>
    /// Counts the total number of objects indexed under the specified tag key.
    /// </summary>
    /// <param name="tagKey">The tag key to count.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of distinct objects tagged with the given key.</returns>
    Task<long> CountByTagAsync(TagKey tagKey, CancellationToken ct = default);

    /// <summary>
    /// Rebuilds the entire index from an external source, replacing all existing entries.
    /// Used for initial population, disaster recovery, or consistency repair.
    /// </summary>
    /// <param name="source">An async enumerable of (object key, tag collection) pairs.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RebuildAsync(
        IAsyncEnumerable<(string ObjectKey, TagCollection Tags)> source,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the top N most-used tag keys and their object counts.
    /// Useful for monitoring tag usage patterns and distribution.
    /// </summary>
    /// <param name="topN">Maximum number of tag keys to return. Defaults to 50.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A dictionary mapping tag keys to their object counts, ordered by count descending.</returns>
    Task<IReadOnlyDictionary<TagKey, long>> GetTagDistributionAsync(
        int topN = 50,
        CancellationToken ct = default);
}
