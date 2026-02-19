using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags;

/// <summary>
/// High-level tag query API supporting composable expression trees, pagination,
/// projection, counting, and value aggregation. Built on top of <see cref="ITagIndex"/>
/// for performant tag-to-object lookups.
/// </summary>
/// <remarks>
/// This is the primary query surface for consumers of the tag system. It compiles
/// <see cref="TagQueryExpression"/> trees into optimized <see cref="ITagIndex"/> calls
/// with AND/OR/NOT semantics, then enriches results from <see cref="ITagStore"/>.
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag query API")]
public interface ITagQueryApi
{
    /// <summary>
    /// Executes a tag query with the specified expression, pagination, sorting, and projection.
    /// </summary>
    /// <param name="request">The query request containing expression tree and options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The query result with matched items, total count, and timing.</returns>
    Task<TagQueryResult> QueryAsync(TagQueryRequest request, CancellationToken ct = default);

    /// <summary>
    /// Counts the number of objects matching the specified expression without loading results.
    /// More efficient than <see cref="QueryAsync"/> when only the count is needed.
    /// </summary>
    /// <param name="expression">The query expression to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The total number of matching objects.</returns>
    Task<long> CountAsync(TagQueryExpression expression, CancellationToken ct = default);

    /// <summary>
    /// Aggregates tag values for a specific tag key, returning the top N values and their counts.
    /// Useful for analytics dashboards and faceted search.
    /// </summary>
    /// <param name="tagKey">The tag key to aggregate values for.</param>
    /// <param name="topN">Maximum number of distinct values to return. Defaults to 50.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A dictionary mapping tag values to their occurrence counts, ordered by count descending.</returns>
    Task<IReadOnlyDictionary<TagValue, long>> AggregateAsync(
        TagKey tagKey,
        int topN = 50,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the distribution of tag keys across all indexed objects.
    /// Delegates to <see cref="ITagIndex.GetTagDistributionAsync"/>.
    /// </summary>
    /// <param name="topN">Maximum number of tag keys to return. Defaults to 50.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A dictionary mapping tag keys to their object counts, ordered by count descending.</returns>
    Task<IReadOnlyDictionary<TagKey, long>> GetTagDistributionAsync(
        int topN = 50,
        CancellationToken ct = default);
}
