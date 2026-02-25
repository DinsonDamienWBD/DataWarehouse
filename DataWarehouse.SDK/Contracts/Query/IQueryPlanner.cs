using System.Collections.Immutable;

namespace DataWarehouse.SDK.Contracts.Query;

// ─────────────────────────────────────────────────────────────
// Table statistics for cost estimation
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Statistics for a single column, used by the cost-based planner
/// to estimate selectivity and cardinality.
/// </summary>
public sealed record ColumnStatistics(
    long DistinctCount,
    long NullCount,
    object? MinValue,
    object? MaxValue,
    int AvgSize
);

/// <summary>
/// Statistics for a table, used by the cost-based planner
/// to estimate scan cost and join cardinality.
/// </summary>
public sealed record TableStatistics(
    string TableName,
    long RowCount,
    long SizeBytes,
    ImmutableDictionary<string, ColumnStatistics> Columns
);

/// <summary>
/// Provides table-level statistics to the query planner.
/// Implementations may read from catalog metadata, histograms, or sampling.
/// </summary>
public interface ITableStatisticsProvider
{
    /// <summary>
    /// Returns statistics for the given table, or null if unknown.
    /// </summary>
    TableStatistics? GetStatistics(string tableName);
}

// ─────────────────────────────────────────────────────────────
// Query planner contract
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Transforms a parsed SQL AST into a physical execution plan with cost estimates.
/// The planner uses table statistics (when available) to choose join order,
/// join algorithms, and predicate placement.
/// </summary>
public interface IQueryPlanner
{
    /// <summary>
    /// Transforms a SelectStatement AST into an optimized physical query plan.
    /// </summary>
    /// <param name="statement">The parsed SELECT statement.</param>
    /// <param name="stats">Optional statistics provider for cost estimation.</param>
    /// <returns>The root node of the optimized physical plan tree.</returns>
    QueryPlanNode Plan(SelectStatement statement, ITableStatisticsProvider? stats = null);

    /// <summary>
    /// Applies optimization rules to an existing physical plan.
    /// </summary>
    /// <param name="plan">The plan to optimize.</param>
    /// <returns>An optimized plan (may be the same reference if no optimization applies).</returns>
    QueryPlanNode Optimize(QueryPlanNode plan);
}
