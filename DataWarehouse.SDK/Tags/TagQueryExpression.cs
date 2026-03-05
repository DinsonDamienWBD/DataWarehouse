using System.Diagnostics;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags;

// ══════════════════════════════════════════════════════════════════════════════
// Tag Query Expression Tree
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Abstract base for composable tag query expressions. Forms a sealed expression tree
/// with AND, OR, NOT combinators and leaf tag filters. Supports fluent builder pattern:
/// <code>
/// var expr = TagQueryExpression
///     .HasTag("compliance", "gdpr")
///     .And(TagQueryExpression.TagEquals(priorityKey, TagValue.String("high")));
/// </code>
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag query expressions")]
public abstract record TagQueryExpression
{
    // ── Static builder methods ───────────────────────────────────────

    /// <summary>
    /// Creates a leaf filter expression with the specified key, operator, and optional value.
    /// </summary>
    /// <param name="key">The tag key to filter on.</param>
    /// <param name="op">The filter operator.</param>
    /// <param name="value">Optional comparison value (null for Exists/NotExists).</param>
    public static TagQueryExpression Where(TagKey key, TagFilterOperator op, TagValue? value = null)
        => new TagFilterExpression(new TagFilter
        {
            TagKey = key,
            Operator = op,
            Value = value
        });

    /// <summary>
    /// Creates an existence check expression: does the object have a tag with this key?
    /// </summary>
    /// <param name="key">The tag key to check.</param>
    public static TagQueryExpression HasTag(TagKey key)
        => Where(key, TagFilterOperator.Exists);

    /// <summary>
    /// Creates an existence check expression from namespace and name.
    /// </summary>
    /// <param name="ns">The tag namespace.</param>
    /// <param name="name">The tag name.</param>
    public static TagQueryExpression HasTag(string ns, string name)
        => HasTag(new TagKey(ns, name));

    /// <summary>
    /// Creates an equality check expression: does the object have a tag with this key and exact value?
    /// </summary>
    /// <param name="key">The tag key to match.</param>
    /// <param name="value">The expected tag value.</param>
    public static TagQueryExpression TagEquals(TagKey key, TagValue value)
        => Where(key, TagFilterOperator.Equals, value);

    // ── Instance combinators ─────────────────────────────────────────

    /// <summary>
    /// Combines this expression with another using AND semantics.
    /// Both expressions must match for an object to be included.
    /// </summary>
    /// <param name="other">The other expression to combine with.</param>
    /// <returns>An <see cref="AndExpression"/> combining both.</returns>
    public TagQueryExpression And(TagQueryExpression other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new AndExpression(new TagQueryExpression[] { this, other });
    }

    /// <summary>
    /// Combines this expression with another using OR semantics.
    /// Either expression matching is sufficient for an object to be included.
    /// </summary>
    /// <param name="other">The other expression to combine with.</param>
    /// <returns>An <see cref="OrExpression"/> combining both.</returns>
    public TagQueryExpression Or(TagQueryExpression other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new OrExpression(new TagQueryExpression[] { this, other });
    }

    /// <summary>
    /// Negates this expression. Objects that match this expression will be excluded,
    /// and vice versa.
    /// </summary>
    /// <returns>A <see cref="NotExpression"/> wrapping this expression.</returns>
    public TagQueryExpression Negate()
        => new NotExpression(this);
}

/// <summary>
/// Leaf expression node wrapping a single <see cref="TagFilter"/>.
/// </summary>
/// <param name="Filter">The tag filter to evaluate.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag query expressions")]
public sealed record TagFilterExpression(TagFilter Filter) : TagQueryExpression
{
    /// <inheritdoc />
    public override string ToString() => $"Filter({Filter.TagKey} {Filter.Operator} {Filter.Value})";
}

/// <summary>
/// Conjunction expression: all children must match for an object to be included.
/// </summary>
/// <param name="Children">The child expressions that must all match.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag query expressions")]
public sealed record AndExpression(IReadOnlyList<TagQueryExpression> Children) : TagQueryExpression
{
    /// <inheritdoc />
    public override string ToString() => $"AND({string.Join(", ", Children)})";
}

/// <summary>
/// Disjunction expression: any child matching is sufficient for an object to be included.
/// </summary>
/// <param name="Children">The child expressions, any of which may match.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag query expressions")]
public sealed record OrExpression(IReadOnlyList<TagQueryExpression> Children) : TagQueryExpression
{
    /// <inheritdoc />
    public override string ToString() => $"OR({string.Join(", ", Children)})";
}

/// <summary>
/// Negation expression: objects matching the inner expression are excluded.
/// </summary>
/// <param name="Inner">The expression to negate.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag query expressions")]
public sealed record NotExpression(TagQueryExpression Inner) : TagQueryExpression
{
    /// <inheritdoc />
    public override string ToString() => $"NOT({Inner})";
}

// ══════════════════════════════════════════════════════════════════════════════
// Query Sort, Request, and Result Types
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Enumerates sort fields available for tag query results.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag query expressions")]
public enum TagQuerySortField
{
    /// <summary>Sort by object key (default, lexicographic).</summary>
    ObjectKey,

    /// <summary>Sort by tag key (first matched).</summary>
    TagKey,

    /// <summary>Sort by tag value (first matched, numeric or lexicographic).</summary>
    TagValue,

    /// <summary>Sort by the most recent tag modification timestamp.</summary>
    ModifiedUtc,

    /// <summary>Sort by the earliest tag creation timestamp.</summary>
    CreatedUtc
}

/// <summary>
/// Encapsulates a full tag query request with expression, pagination, sorting, and projection.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag query expressions")]
public sealed record TagQueryRequest
{
    /// <summary>The query expression tree to evaluate.</summary>
    public required TagQueryExpression Expression { get; init; }

    /// <summary>
    /// Number of results to skip (for pagination). Defaults to 0.
    /// Must be non-negative; negative values are clamped to 0.
    /// </summary>
    public int Skip { get; init; }

    /// <summary>Maximum number of results to return. Defaults to 100.</summary>
    public int Take { get; init; } = 100;

    /// <summary>The field to sort results by. Defaults to <see cref="TagQuerySortField.ObjectKey"/>.</summary>
    public TagQuerySortField SortBy { get; init; } = TagQuerySortField.ObjectKey;

    /// <summary>When true, sorts in descending order. Defaults to false.</summary>
    public bool Descending { get; init; }

    /// <summary>
    /// When true, includes full tag values for matched objects.
    /// When false, returns only object keys (faster, lower bandwidth).
    /// Defaults to true.
    /// </summary>
    public bool IncludeTagValues { get; init; } = true;

    /// <summary>
    /// When set, only these tag keys are returned per object (tag projection).
    /// Null means return all tags. Ignored if <see cref="IncludeTagValues"/> is false.
    /// </summary>
    public IReadOnlySet<TagKey>? ProjectTags { get; init; }

    /// <summary>Maximum allowed Take value. Requests exceeding this are clamped.</summary>
    internal const int MaxTake = 10_000;

    /// <summary>Returns the effective Take value, clamped to <see cref="MaxTake"/>.</summary>
    internal int EffectiveTake => Math.Min(Math.Max(Take, 0), MaxTake);

    /// <summary>Returns the effective Skip value, clamped to non-negative. Cat 14 (finding 685).</summary>
    internal int EffectiveSkip => Math.Max(Skip, 0);
}

/// <summary>
/// The result of a tag query, containing matched items, total count, and execution timing.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag query expressions")]
public sealed record TagQueryResult
{
    /// <summary>The matched result items, respecting Skip/Take pagination.</summary>
    public required IReadOnlyList<TagQueryResultItem> Items { get; init; }

    /// <summary>Total number of objects matching the query (may exceed Items.Count due to pagination).</summary>
    public required long TotalCount { get; init; }

    /// <summary>Wall-clock duration of query execution.</summary>
    public required TimeSpan QueryDuration { get; init; }
}

/// <summary>
/// A single item in a tag query result, representing one matched object.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag query expressions")]
public sealed record TagQueryResultItem
{
    /// <summary>The storage key of the matched object.</summary>
    public required string ObjectKey { get; init; }

    /// <summary>
    /// Full tag collection for the object. Null if <see cref="TagQueryRequest.IncludeTagValues"/> was false.
    /// </summary>
    public TagCollection? Tags { get; init; }

    /// <summary>
    /// Projected subset of tags when <see cref="TagQueryRequest.ProjectTags"/> was specified.
    /// Null if no projection was requested.
    /// </summary>
    public IReadOnlyDictionary<TagKey, TagValue>? ProjectedTags { get; init; }
}
