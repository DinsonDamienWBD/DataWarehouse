using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags;

/// <summary>
/// Represents a single entry in the inverted tag index, recording that a specific
/// object has a specific tag indexed at a given time.
/// </summary>
/// <param name="ObjectKey">The storage key of the indexed object.</param>
/// <param name="TagKey">The qualified tag key that was indexed.</param>
/// <param name="ValueKind">The kind of the tag value at indexing time.</param>
/// <param name="IndexedAt">UTC timestamp of when this entry was added to the index.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag index types")]
public sealed record TagIndexEntry(
    string ObjectKey,
    TagKey TagKey,
    TagValueKind ValueKind,
    DateTimeOffset IndexedAt);

/// <summary>
/// Enumerates comparison and matching operators supported by tag index queries.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag index types")]
public enum TagFilterOperator
{
    /// <summary>Tag value must equal the filter value exactly.</summary>
    Equals,

    /// <summary>Tag value must not equal the filter value.</summary>
    NotEquals,

    /// <summary>Tag value must be greater than the filter value (numeric/string comparison).</summary>
    GreaterThan,

    /// <summary>Tag value must be less than the filter value (numeric/string comparison).</summary>
    LessThan,

    /// <summary>Tag value must be greater than or equal to the filter value.</summary>
    GreaterOrEqual,

    /// <summary>Tag value must be less than or equal to the filter value.</summary>
    LessOrEqual,

    /// <summary>Tag value must contain the filter value as a substring (string values only).</summary>
    Contains,

    /// <summary>Tag value must start with the filter value (string values only).</summary>
    StartsWith,

    /// <summary>Tag key must exist on the object (value is ignored).</summary>
    Exists,

    /// <summary>Tag key must not exist on the object (value is ignored).</summary>
    NotExists,

    /// <summary>Tag value must be one of the values in the filter set.</summary>
    In,

    /// <summary>Tag value must be between Value (lower) and UpperBound (inclusive).</summary>
    Between
}

/// <summary>
/// A single filter criterion for querying the tag index.
/// Combines a tag key, an operator, and comparison values.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag index types")]
public sealed record TagFilter
{
    /// <summary>The tag key to filter on.</summary>
    public required TagKey TagKey { get; init; }

    /// <summary>The comparison operator to apply.</summary>
    public required TagFilterOperator Operator { get; init; }

    /// <summary>
    /// The comparison value for single-value operators (Equals, NotEquals, GreaterThan, etc.).
    /// Also serves as the lower bound for <see cref="TagFilterOperator.Between"/>.
    /// Null for <see cref="TagFilterOperator.Exists"/> and <see cref="TagFilterOperator.NotExists"/>.
    /// </summary>
    public TagValue? Value { get; init; }

    /// <summary>
    /// The upper bound value for <see cref="TagFilterOperator.Between"/> queries (inclusive).
    /// Null for all other operators.
    /// </summary>
    public TagValue? UpperBound { get; init; }

    /// <summary>
    /// The set of acceptable values for <see cref="TagFilterOperator.In"/> queries.
    /// Null for all other operators.
    /// </summary>
    public IReadOnlySet<TagValue>? Values { get; init; }
}

/// <summary>
/// Describes a query against the tag index, combining multiple filters with AND semantics,
/// optional object key prefix scoping, and pagination.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag index types")]
public sealed record TagIndexQuery
{
    /// <summary>
    /// The filters to apply. All filters are combined with AND semantics --
    /// an object must match every filter to appear in results.
    /// </summary>
    public required IReadOnlyList<TagFilter> Filters { get; init; }

    /// <summary>
    /// Number of matching results to skip (for pagination). Defaults to 0.
    /// </summary>
    public int Skip { get; init; }

    /// <summary>
    /// Maximum number of results to return. Defaults to 100, capped at 10,000.
    /// </summary>
    public int Take { get; init; } = 100;

    /// <summary>
    /// Optional prefix filter on object keys. When set, only objects whose key
    /// starts with this prefix are considered.
    /// </summary>
    public string? ObjectKeyPrefix { get; init; }

    /// <summary>
    /// When true, returns only the total count without populating <see cref="TagIndexResult.ObjectKeys"/>.
    /// Useful for dashboards and monitoring.
    /// </summary>
    public bool CountOnly { get; init; }

    /// <summary>
    /// Maximum allowed Take value. Requests exceeding this are clamped.
    /// </summary>
    internal const int MaxTake = 10_000;

    /// <summary>
    /// Returns the effective Take value, clamped to <see cref="MaxTake"/>.
    /// </summary>
    internal int EffectiveTake => Math.Min(Math.Max(Take, 0), MaxTake);
}

/// <summary>
/// The result of a tag index query, containing matching object keys, total count, and timing.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag index types")]
public sealed record TagIndexResult
{
    /// <summary>
    /// The object keys matching the query, respecting Skip/Take pagination.
    /// Empty when <see cref="TagIndexQuery.CountOnly"/> was true.
    /// </summary>
    public required IReadOnlyList<string> ObjectKeys { get; init; }

    /// <summary>
    /// Total number of objects matching the query filters (may exceed <see cref="ObjectKeys"/> count
    /// due to Take pagination).
    /// </summary>
    public required long TotalCount { get; init; }

    /// <summary>
    /// Wall-clock duration of the query execution.
    /// </summary>
    public required TimeSpan QueryDuration { get; init; }

    /// <summary>
    /// True if the count is an estimate (e.g., from bloom filter pre-filtering).
    /// False when the count is exact.
    /// </summary>
    public bool IsApproximate { get; init; }
}
