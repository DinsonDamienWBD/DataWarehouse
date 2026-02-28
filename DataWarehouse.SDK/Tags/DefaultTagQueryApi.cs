using System.Diagnostics;
using DataWarehouse.SDK.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.SDK.Tags;

/// <summary>
/// Default implementation of <see cref="ITagQueryApi"/> that compiles expression trees
/// into optimized <see cref="ITagIndex"/> queries with AND/OR/NOT semantics.
/// </summary>
/// <remarks>
/// <para><b>Optimization strategy:</b></para>
/// <list type="bullet">
/// <item><description>
///   AND queries: evaluates smallest-cardinality filter first (uses <see cref="ITagIndex.CountByTagAsync"/>
///   to estimate set sizes) and short-circuits on empty intermediate results.
/// </description></item>
/// <item><description>
///   OR queries: evaluates largest-cardinality filter first for early Take satisfaction.
/// </description></item>
/// <item><description>
///   NOT queries: logs a warning because they require a full-scan subtraction and are expensive.
/// </description></item>
/// </list>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag query API")]
public sealed class DefaultTagQueryApi : ITagQueryApi
{
    private readonly ITagIndex _index;
    private readonly ITagStore _store;
    private readonly ILogger<DefaultTagQueryApi> _logger;

    /// <summary>
    /// Initializes a new <see cref="DefaultTagQueryApi"/>.
    /// </summary>
    /// <param name="index">The inverted tag index for performant lookups.</param>
    /// <param name="store">The tag store for loading full tag values.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public DefaultTagQueryApi(ITagIndex index, ITagStore store, ILogger<DefaultTagQueryApi>? logger = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? NullLogger<DefaultTagQueryApi>.Instance;
    }

    /// <inheritdoc />
    public async Task<TagQueryResult> QueryAsync(TagQueryRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sw = Stopwatch.StartNew();

        // Step 1: Compile expression tree to a set of matching object keys
        var matchedKeys = await CompileExpressionAsync(request.Expression, ct).ConfigureAwait(false);

        var totalCount = matchedKeys.Count;
        var effectiveTake = request.EffectiveTake;

        // Step 2: Sort the matched keys
        var sortedKeys = SortObjectKeys(matchedKeys, request.SortBy, request.Descending);

        // Step 3: Apply pagination
        var pagedKeys = sortedKeys
            .Skip(request.Skip)
            .Take(effectiveTake)
            .ToList();

        // Step 4: Build result items with optional tag loading and projection
        var items = new List<TagQueryResultItem>(pagedKeys.Count);

        foreach (var objectKey in pagedKeys)
        {
            ct.ThrowIfCancellationRequested();

            TagCollection? tags = null;
            IReadOnlyDictionary<TagKey, TagValue>? projectedTags = null;

            if (request.IncludeTagValues)
            {
                tags = await _store.GetAllTagsAsync(objectKey, ct).ConfigureAwait(false);

                // Apply tag projection if requested
                if (request.ProjectTags is not null && tags.Count > 0)
                {
                    var projected = new Dictionary<TagKey, TagValue>();
                    foreach (var key in request.ProjectTags)
                    {
                        var tag = tags[key];
                        if (tag is not null)
                        {
                            projected[key] = tag.Value;
                        }
                    }

                    projectedTags = projected;
                }
            }

            items.Add(new TagQueryResultItem
            {
                ObjectKey = objectKey,
                Tags = request.IncludeTagValues ? tags : null,
                ProjectedTags = projectedTags
            });
        }

        sw.Stop();

        return new TagQueryResult
        {
            Items = items,
            TotalCount = totalCount,
            QueryDuration = sw.Elapsed
        };
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(TagQueryExpression expression, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expression);

        // For simple leaf filters, use CountOnly on the index for efficiency
        if (expression is TagFilterExpression leafFilter)
        {
            var countQuery = new TagIndexQuery
            {
                Filters = new[] { leafFilter.Filter },
                CountOnly = true
            };
            var result = await _index.QueryAsync(countQuery, ct).ConfigureAwait(false);
            return result.TotalCount;
        }

        // For complex expressions, compile to object set and count
        var matchedKeys = await CompileExpressionAsync(expression, ct).ConfigureAwait(false);
        return matchedKeys.Count;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<TagValue, long>> AggregateAsync(
        TagKey tagKey,
        int topN = 50,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tagKey);

        // Get all objects that have this tag
        var indexQuery = new TagIndexQuery
        {
            Filters = new[]
            {
                new TagFilter
                {
                    TagKey = tagKey,
                    Operator = TagFilterOperator.Exists
                }
            },
            Take = TagIndexQuery.MaxTake // Get as many as possible for aggregation
        };

        var indexResult = await _index.QueryAsync(indexQuery, ct).ConfigureAwait(false);

        // Group by value
        var valueCounts = new Dictionary<TagValue, long>();

        foreach (var objectKey in indexResult.ObjectKeys)
        {
            ct.ThrowIfCancellationRequested();

            var tag = await _store.GetTagAsync(objectKey, tagKey, ct).ConfigureAwait(false);
            if (tag is null) continue;

            var value = tag.Value;

            // For number values, use the raw value (consumers can bucketize if needed)
            if (!valueCounts.TryGetValue(value, out var count))
                count = 0;

            valueCounts[value] = count + 1;
        }

        // Return top N by count descending
        var topEntries = valueCounts
            .OrderByDescending(kvp => kvp.Value)
            .Take(topN)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        return topEntries;
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<TagKey, long>> GetTagDistributionAsync(
        int topN = 50,
        CancellationToken ct = default)
    {
        return _index.GetTagDistributionAsync(topN, ct);
    }

    // ── Expression compilation ───────────────────────────────────────

    /// <summary>
    /// Compiles a <see cref="TagQueryExpression"/> tree into a set of matching object keys
    /// by recursively evaluating sub-expressions against the inverted index.
    /// </summary>
    private async Task<HashSet<string>> CompileExpressionAsync(
        TagQueryExpression expression,
        CancellationToken ct)
    {
        return expression switch
        {
            TagFilterExpression leaf => await CompileLeafAsync(leaf, ct).ConfigureAwait(false),
            AndExpression and => await CompileAndAsync(and, ct).ConfigureAwait(false),
            OrExpression or => await CompileOrAsync(or, ct).ConfigureAwait(false),
            NotExpression not => await CompileNotAsync(not, ct).ConfigureAwait(false),
            _ => throw new NotSupportedException($"Unknown expression type: {expression.GetType().Name}")
        };
    }

    /// <summary>
    /// Compiles a leaf filter expression to a direct index query.
    /// </summary>
    private async Task<HashSet<string>> CompileLeafAsync(TagFilterExpression leaf, CancellationToken ct)
    {
        var indexQuery = new TagIndexQuery
        {
            Filters = new[] { leaf.Filter },
            Take = TagIndexQuery.MaxTake
        };

        var result = await _index.QueryAsync(indexQuery, ct).ConfigureAwait(false);
        return new HashSet<string>(result.ObjectKeys, StringComparer.Ordinal);
    }

    /// <summary>
    /// Compiles an AND expression by intersecting child result sets.
    /// Optimization: evaluates smallest-cardinality children first to minimize intermediate set sizes.
    /// </summary>
    private async Task<HashSet<string>> CompileAndAsync(AndExpression and, CancellationToken ct)
    {
        if (and.Children.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        // Optimization: estimate cardinality for leaf filters and sort smallest-first
        var orderedChildren = await OrderByCardinalityAscendingAsync(and.Children, ct).ConfigureAwait(false);

        HashSet<string>? result = null;

        foreach (var child in orderedChildren)
        {
            ct.ThrowIfCancellationRequested();

            var childResult = await CompileExpressionAsync(child, ct).ConfigureAwait(false);

            if (result is null)
            {
                result = childResult;
            }
            else
            {
                result.IntersectWith(childResult);
            }

            // Short-circuit: if intersection is empty, no further evaluation needed
            if (result.Count == 0)
                break;
        }

        return result ?? new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Compiles an OR expression by unioning child result sets.
    /// Optimization: evaluates largest-cardinality children first for early coverage.
    /// </summary>
    private async Task<HashSet<string>> CompileOrAsync(OrExpression or, CancellationToken ct)
    {
        if (or.Children.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        // Optimization: evaluate largest-cardinality children first
        var orderedChildren = await OrderByCardinalityDescendingAsync(or.Children, ct).ConfigureAwait(false);

        var result = new HashSet<string>(StringComparer.Ordinal);

        foreach (var child in orderedChildren)
        {
            ct.ThrowIfCancellationRequested();

            var childResult = await CompileExpressionAsync(child, ct).ConfigureAwait(false);
            result.UnionWith(childResult);
        }

        return result;
    }

    /// <summary>
    /// Compiles a NOT expression by subtracting the inner result set from all indexed objects.
    /// WARNING: This is an expensive operation requiring a full scan of all objects.
    /// </summary>
    private async Task<HashSet<string>> CompileNotAsync(NotExpression not, CancellationToken ct)
    {
        const int MaxNotScanKeys = 10_000;

        _logger.LogWarning(
            "NOT expression compilation requires full-scan subtraction. " +
            "Consider restructuring the query to avoid NOT for better performance. " +
            "Inner expression: {InnerExpression}", not.Inner);

        // Get known objects from the index distribution (bounded to prevent OOM)
        var distribution = await _index.GetTagDistributionAsync(MaxNotScanKeys, ct).ConfigureAwait(false);

        // Collect all object keys from the index by querying each known tag
        var allObjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tagKey in distribution.Keys)
        {
            ct.ThrowIfCancellationRequested();

            var query = new TagIndexQuery
            {
                Filters = new[]
                {
                    new TagFilter
                    {
                        TagKey = tagKey,
                        Operator = TagFilterOperator.Exists
                    }
                },
                Take = TagIndexQuery.MaxTake
            };

            var result = await _index.QueryAsync(query, ct).ConfigureAwait(false);
            foreach (var key in result.ObjectKeys)
                allObjects.Add(key);
        }

        // Subtract the inner expression results
        var innerResults = await CompileExpressionAsync(not.Inner, ct).ConfigureAwait(false);
        allObjects.ExceptWith(innerResults);

        return allObjects;
    }

    // ── Cardinality estimation ───────────────────────────────────────

    /// <summary>
    /// Orders child expressions by estimated ascending cardinality (smallest first).
    /// Leaf filters use <see cref="ITagIndex.CountByTagAsync"/> for estimation;
    /// compound expressions default to <see cref="long.MaxValue"/> (evaluated last).
    /// </summary>
    private async Task<IReadOnlyList<TagQueryExpression>> OrderByCardinalityAscendingAsync(
        IReadOnlyList<TagQueryExpression> children,
        CancellationToken ct)
    {
        if (children.Count <= 1) return children;

        var estimates = new (TagQueryExpression Expr, long Estimate)[children.Count];

        for (int i = 0; i < children.Count; i++)
        {
            estimates[i] = (children[i], await EstimateCardinalityAsync(children[i], ct).ConfigureAwait(false));
        }

        return estimates
            .OrderBy(e => e.Estimate)
            .Select(e => e.Expr)
            .ToList();
    }

    /// <summary>
    /// Orders child expressions by estimated descending cardinality (largest first).
    /// </summary>
    private async Task<IReadOnlyList<TagQueryExpression>> OrderByCardinalityDescendingAsync(
        IReadOnlyList<TagQueryExpression> children,
        CancellationToken ct)
    {
        if (children.Count <= 1) return children;

        var estimates = new (TagQueryExpression Expr, long Estimate)[children.Count];

        for (int i = 0; i < children.Count; i++)
        {
            estimates[i] = (children[i], await EstimateCardinalityAsync(children[i], ct).ConfigureAwait(false));
        }

        return estimates
            .OrderByDescending(e => e.Estimate)
            .Select(e => e.Expr)
            .ToList();
    }

    /// <summary>
    /// Estimates the cardinality of an expression. For leaf filters on tag existence,
    /// uses the fast <see cref="ITagIndex.CountByTagAsync"/>. For compound expressions,
    /// returns <see cref="long.MaxValue"/> as a conservative estimate.
    /// </summary>
    private async Task<long> EstimateCardinalityAsync(TagQueryExpression expression, CancellationToken ct)
    {
        if (expression is TagFilterExpression leaf &&
            leaf.Filter.Operator is TagFilterOperator.Exists or TagFilterOperator.Equals)
        {
            return await _index.CountByTagAsync(leaf.Filter.TagKey, ct).ConfigureAwait(false);
        }

        // Compound expressions: unknown cardinality, use max as conservative estimate
        return long.MaxValue;
    }

    // ── Sorting ──────────────────────────────────────────────────────

    /// <summary>
    /// Sorts matched object keys by the requested sort field. For non-ObjectKey sorts,
    /// this is a best-effort sort based on available metadata.
    /// </summary>
    private static IEnumerable<string> SortObjectKeys(
        HashSet<string> keys,
        TagQuerySortField sortBy,
        bool descending)
    {
        // ObjectKey sort is the most common and cheapest path
        // Other sort fields would require loading tag data; for now, fallback to ObjectKey
        // since we don't have the tag data loaded at this stage.
        // Future optimization: pass sort hints to the index layer.
        IOrderedEnumerable<string> ordered = sortBy switch
        {
            _ => descending
                ? keys.OrderByDescending(k => k, StringComparer.Ordinal)
                : keys.OrderBy(k => k, StringComparer.Ordinal)
        };

        return ordered;
    }
}
