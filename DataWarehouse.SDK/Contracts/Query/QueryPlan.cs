using System.Collections.Immutable;

namespace DataWarehouse.SDK.Contracts.Query;

// ─────────────────────────────────────────────────────────────
// Physical query plan node hierarchy
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Base type for all physical query plan nodes. Each node carries estimated
/// row count and cost computed by the planner.
/// </summary>
public abstract record QueryPlanNode(double EstimatedRows, double EstimatedCost);

/// <summary>
/// Full table scan: reads all rows from a table, projecting specified columns.
/// </summary>
public sealed record TableScanNode(
    string TableName,
    ImmutableArray<string> Columns,
    double EstimatedRows,
    double EstimatedCost
) : QueryPlanNode(EstimatedRows, EstimatedCost);

/// <summary>
/// Index scan: reads rows from an index, optionally bounded by a key range.
/// </summary>
public sealed record IndexScanNode(
    string TableName,
    string IndexName,
    ImmutableArray<string> Columns,
    KeyRange? KeyRange,
    double EstimatedRows,
    double EstimatedCost
) : QueryPlanNode(EstimatedRows, EstimatedCost);

/// <summary>
/// Defines an inclusive key range for index scans.
/// </summary>
public sealed record KeyRange(object? LowerBound, object? UpperBound, bool LowerInclusive, bool UpperInclusive);

/// <summary>
/// Filters rows from its input using a predicate expression.
/// </summary>
public sealed record FilterNode(
    QueryPlanNode Input,
    Expression Predicate,
    double EstimatedRows,
    double EstimatedCost
) : QueryPlanNode(EstimatedRows, EstimatedCost);

/// <summary>
/// Projects (selects) a subset of columns from its input.
/// </summary>
public sealed record ProjectNode(
    QueryPlanNode Input,
    ImmutableArray<SelectColumn> Columns,
    double EstimatedRows,
    double EstimatedCost
) : QueryPlanNode(EstimatedRows, EstimatedCost);

/// <summary>
/// Hash join: builds a hash table on the build side (Left) and probes with the other.
/// </summary>
public sealed record HashJoinNode(
    QueryPlanNode Left,
    QueryPlanNode Right,
    Expression LeftKey,
    Expression RightKey,
    JoinType JoinType,
    double EstimatedRows,
    double EstimatedCost
) : QueryPlanNode(EstimatedRows, EstimatedCost);

/// <summary>
/// Nested loop join: for each row in Left, scans Right evaluating the condition.
/// Efficient when Right is small.
/// </summary>
public sealed record NestedLoopJoinNode(
    QueryPlanNode Left,
    QueryPlanNode Right,
    Expression? Condition,
    JoinType JoinType,
    double EstimatedRows,
    double EstimatedCost
) : QueryPlanNode(EstimatedRows, EstimatedCost);

/// <summary>
/// Merge join: merges two sorted inputs on their join keys.
/// Requires both inputs to be sorted on the join key.
/// </summary>
public sealed record MergeJoinNode(
    QueryPlanNode Left,
    QueryPlanNode Right,
    Expression LeftKey,
    Expression RightKey,
    JoinType JoinType,
    double EstimatedRows,
    double EstimatedCost
) : QueryPlanNode(EstimatedRows, EstimatedCost);

/// <summary>
/// Sorts its input according to the specified order-by items.
/// </summary>
public sealed record SortNode(
    QueryPlanNode Input,
    ImmutableArray<OrderByItem> OrderBy,
    double EstimatedRows,
    double EstimatedCost
) : QueryPlanNode(EstimatedRows, EstimatedCost);

/// <summary>
/// Groups input rows and computes aggregate functions.
/// </summary>
public sealed record AggregateNode(
    QueryPlanNode Input,
    ImmutableArray<Expression> GroupByKeys,
    ImmutableArray<AggregateFunction> Aggregations,
    double EstimatedRows,
    double EstimatedCost
) : QueryPlanNode(EstimatedRows, EstimatedCost);

/// <summary>
/// Limits the number of output rows, optionally with an offset.
/// </summary>
public sealed record LimitNode(
    QueryPlanNode Input,
    int Limit,
    int Offset,
    double EstimatedRows,
    double EstimatedCost
) : QueryPlanNode(EstimatedRows, EstimatedCost);

/// <summary>
/// Union of multiple inputs. When All is true, includes duplicates.
/// </summary>
public sealed record UnionNode(
    ImmutableArray<QueryPlanNode> Inputs,
    bool All,
    double EstimatedRows,
    double EstimatedCost
) : QueryPlanNode(EstimatedRows, EstimatedCost);

// ─────────────────────────────────────────────────────────────
// Aggregate function definition
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Describes an aggregate function invocation (COUNT, SUM, AVG, MIN, MAX).
/// </summary>
public sealed record AggregateFunction(
    string FunctionName,
    Expression? Argument,
    string? Alias
);
