using System.Collections.Immutable;

namespace DataWarehouse.SDK.Contracts.Query;

// ---------------------------------------------------------------
// FederatedTableScanNode
// ---------------------------------------------------------------

/// <summary>
/// Extended table scan node that carries federated source information.
/// The federated engine uses this to route scans to remote sources
/// instead of local storage.
/// </summary>
public sealed record FederatedTableScanNode(
    /// <summary>Table name on the remote source.</summary>
    string TableName,

    /// <summary>Columns to project (empty = all).</summary>
    ImmutableArray<string> Columns,

    /// <summary>Estimated row count after pushed filters.</summary>
    double EstimatedRows,

    /// <summary>Estimated cost including network overhead.</summary>
    double EstimatedCost,

    /// <summary>Federated source identifier that owns this table.</summary>
    string SourceId,

    /// <summary>Filter predicates pushed down to the remote source.</summary>
    ImmutableArray<Expression> PushedFilters,

    /// <summary>Column names pushed down for remote projection.</summary>
    ImmutableArray<string> PushedProjections
) : QueryPlanNode(EstimatedRows, EstimatedCost);

/// <summary>
/// Extended aggregate node for remote aggregation pushdown.
/// When a federated source supports aggregation, the entire aggregate
/// operation can be executed remotely to avoid transferring raw data.
/// </summary>
public sealed record FederatedAggregateNode(
    /// <summary>Federated source to push the aggregation to.</summary>
    string SourceId,

    /// <summary>Table to aggregate on the remote source.</summary>
    string TableName,

    /// <summary>GROUP BY key expressions.</summary>
    ImmutableArray<Expression> GroupByKeys,

    /// <summary>Aggregate functions to compute remotely.</summary>
    ImmutableArray<AggregateFunction> Aggregations,

    /// <summary>Filter predicates to apply before aggregation.</summary>
    ImmutableArray<Expression> PushedFilters,

    /// <summary>Estimated number of result groups.</summary>
    double EstimatedRows,

    /// <summary>Estimated cost including network overhead.</summary>
    double EstimatedCost
) : QueryPlanNode(EstimatedRows, EstimatedCost);

// ---------------------------------------------------------------
// FederatedQueryPlanner
// ---------------------------------------------------------------

/// <summary>
/// Query planner that extends the cost-based planner with network-aware
/// cost estimation and remote operation pushdown. Produces
/// FederatedTableScanNode and FederatedAggregateNode plan nodes
/// that the FederatedQueryEngine executes across multiple backends.
///
/// Cost model:
///   remote_cost = base_cost * source.CostMultiplier
///               + estimated_rows * bytes_per_row / bandwidth_bytes_per_sec
///               + latency_sec
///
/// Pushdown rules:
///   - Filters pushed when SupportsFilterPushdown is true
///   - Projections pushed when SupportsProjectionPushdown is true
///   - Aggregations pushed when SupportsAggregationPushdown is true AND is final operation
///   - Cross-source joins always pull data locally (no distributed join in v1)
/// </summary>
public sealed class FederatedQueryPlanner : IQueryPlanner
{
    private const double DefaultBytesPerRow = 256.0;
    private const double MbpsToBytesPerSec = 125_000.0; // 1 Mbps = 125,000 bytes/sec
    private const double MsToSec = 0.001;

    private readonly CostBasedQueryPlanner _basePlanner = new();
    private readonly IFederatedDataSourceRegistry _registry;

    /// <summary>
    /// Creates a federated query planner that accounts for network costs
    /// and pushes operations to remote sources based on their capabilities.
    /// </summary>
    /// <param name="registry">Registry of available federated data sources.</param>
    public FederatedQueryPlanner(IFederatedDataSourceRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <inheritdoc />
    public QueryPlanNode Plan(SelectStatement statement, ITableStatisticsProvider? stats = null)
    {
        // Step 1: Build the base plan using the cost-based planner
        var basePlan = _basePlanner.Plan(statement, stats);

        // Step 2: Rewrite plan nodes to use federated sources where applicable
        var federatedPlan = RewriteForFederation(basePlan, statement, stats);

        // Step 3: Try aggregation pushdown if the plan is a simple aggregate over a single source
        federatedPlan = TryAggregationPushdown(federatedPlan, statement);

        return federatedPlan;
    }

    /// <inheritdoc />
    public QueryPlanNode Optimize(QueryPlanNode plan)
    {
        return _basePlanner.Optimize(plan);
    }

    // ---------------------------------------------------------------
    // Federation Rewrite
    // ---------------------------------------------------------------

    private QueryPlanNode RewriteForFederation(
        QueryPlanNode node,
        SelectStatement statement,
        ITableStatisticsProvider? stats)
    {
        return node switch
        {
            TableScanNode scan => RewriteTableScan(scan, statement),
            FilterNode filter => RewriteFilter(filter, statement, stats),
            ProjectNode project => RewriteProject(project, statement, stats),
            HashJoinNode hash => RewriteHashJoin(hash, statement, stats),
            NestedLoopJoinNode nested => RewriteNestedLoopJoin(nested, statement, stats),
            MergeJoinNode merge => RewriteMergeJoin(merge, statement, stats),
            SortNode sort => new SortNode(
                RewriteForFederation(sort.Input, statement, stats),
                sort.OrderBy, sort.EstimatedRows, sort.EstimatedCost),
            AggregateNode agg => new AggregateNode(
                RewriteForFederation(agg.Input, statement, stats),
                agg.GroupByKeys, agg.Aggregations, agg.EstimatedRows, agg.EstimatedCost),
            LimitNode limit => new LimitNode(
                RewriteForFederation(limit.Input, statement, stats),
                limit.Limit, limit.Offset, limit.EstimatedRows, limit.EstimatedCost),
            _ => node
        };
    }

    private QueryPlanNode RewriteTableScan(TableScanNode scan, SelectStatement statement)
    {
        var resolved = _registry.ResolveTable(scan.TableName);
        if (resolved == null)
        {
            // Not a federated table — keep the local scan
            return scan;
        }

        var (source, remoteTable) = resolved.Value;
        var info = source.GetInfo();

        // Calculate network-aware cost
        var networkCost = EstimateNetworkCost(info, scan.EstimatedRows);
        var federatedCost = scan.EstimatedCost * info.CostMultiplier + networkCost;

        return new FederatedTableScanNode(
            remoteTable,
            scan.Columns,
            scan.EstimatedRows,
            federatedCost,
            info.SourceId,
            ImmutableArray<Expression>.Empty,
            ImmutableArray<string>.Empty);
    }

    private QueryPlanNode RewriteFilter(
        FilterNode filter,
        SelectStatement statement,
        ITableStatisticsProvider? stats)
    {
        var rewrittenInput = RewriteForFederation(filter.Input, statement, stats);

        // Try to push filter down to federated source
        if (rewrittenInput is FederatedTableScanNode fedScan)
        {
            var source = _registry.GetSource(fedScan.SourceId);
            if (source != null)
            {
                var info = source.GetInfo();
                if (info.SupportsFilterPushdown)
                {
                    // Push the filter to the remote source
                    var pushedFilters = fedScan.PushedFilters.Add(filter.Predicate);

                    // Recalculate cost: fewer rows transferred
                    var filteredRows = filter.EstimatedRows;
                    var networkCost = EstimateNetworkCost(info, filteredRows);
                    var federatedCost = filter.EstimatedCost * info.CostMultiplier + networkCost;

                    return new FederatedTableScanNode(
                        fedScan.TableName,
                        fedScan.Columns,
                        filteredRows,
                        federatedCost,
                        fedScan.SourceId,
                        pushedFilters,
                        fedScan.PushedProjections);
                }
            }
        }

        // Cannot push down — apply filter locally
        return new FilterNode(rewrittenInput, filter.Predicate,
            filter.EstimatedRows, filter.EstimatedCost);
    }

    private QueryPlanNode RewriteProject(
        ProjectNode project,
        SelectStatement statement,
        ITableStatisticsProvider? stats)
    {
        var rewrittenInput = RewriteForFederation(project.Input, statement, stats);

        // Try to push projection down to federated source
        if (rewrittenInput is FederatedTableScanNode fedScan)
        {
            var source = _registry.GetSource(fedScan.SourceId);
            if (source != null)
            {
                var info = source.GetInfo();
                if (info.SupportsProjectionPushdown)
                {
                    // Extract column names from the projection
                    var columnNames = ExtractColumnNames(project.Columns);
                    if (columnNames.Length > 0)
                    {
                        // Push projection — fewer bytes per row
                        var projectedCost = fedScan.EstimatedCost * 0.5; // Rough: half the columns = half the transfer
                        return new FederatedTableScanNode(
                            fedScan.TableName,
                            fedScan.Columns,
                            fedScan.EstimatedRows,
                            projectedCost,
                            fedScan.SourceId,
                            fedScan.PushedFilters,
                            columnNames);
                    }
                }
            }
        }

        // Cannot push down — apply projection locally
        return new ProjectNode(rewrittenInput, project.Columns,
            project.EstimatedRows, project.EstimatedCost);
    }

    private QueryPlanNode RewriteHashJoin(
        HashJoinNode join,
        SelectStatement statement,
        ITableStatisticsProvider? stats)
    {
        var left = RewriteForFederation(join.Left, statement, stats);
        var right = RewriteForFederation(join.Right, statement, stats);

        // Cross-source join: always pull data locally.
        // Choose which side to pull based on estimated row counts
        // (smaller side gets pulled to where the larger side lives).
        return new HashJoinNode(left, right, join.LeftKey, join.RightKey,
            join.JoinType, join.EstimatedRows,
            join.EstimatedCost + EstimateCrossSourceJoinCost(left, right));
    }

    private QueryPlanNode RewriteNestedLoopJoin(
        NestedLoopJoinNode join,
        SelectStatement statement,
        ITableStatisticsProvider? stats)
    {
        var left = RewriteForFederation(join.Left, statement, stats);
        var right = RewriteForFederation(join.Right, statement, stats);

        return new NestedLoopJoinNode(left, right, join.Condition,
            join.JoinType, join.EstimatedRows,
            join.EstimatedCost + EstimateCrossSourceJoinCost(left, right));
    }

    private QueryPlanNode RewriteMergeJoin(
        MergeJoinNode join,
        SelectStatement statement,
        ITableStatisticsProvider? stats)
    {
        var left = RewriteForFederation(join.Left, statement, stats);
        var right = RewriteForFederation(join.Right, statement, stats);

        return new MergeJoinNode(left, right, join.LeftKey, join.RightKey,
            join.JoinType, join.EstimatedRows,
            join.EstimatedCost + EstimateCrossSourceJoinCost(left, right));
    }

    // ---------------------------------------------------------------
    // Aggregation Pushdown
    // ---------------------------------------------------------------

    /// <summary>
    /// Attempts to push aggregation to a remote source when:
    /// 1. The aggregation is the final operation (before projection/limit)
    /// 2. The input is a single federated source
    /// 3. The source supports aggregation pushdown
    /// </summary>
    private QueryPlanNode TryAggregationPushdown(QueryPlanNode plan, SelectStatement statement)
    {
        // Look for pattern: Project? -> Limit? -> Aggregate -> FederatedTableScan
        var current = plan;
        ProjectNode? outerProject = null;
        LimitNode? outerLimit = null;

        if (current is ProjectNode proj)
        {
            outerProject = proj;
            current = proj.Input;
        }

        if (current is LimitNode lim)
        {
            outerLimit = lim;
            current = lim.Input;
        }

        if (current is not AggregateNode agg)
            return plan;

        // The aggregate's input must be a single federated source
        var fedScan = FindFederatedScan(agg.Input);
        if (fedScan == null)
            return plan;

        var source = _registry.GetSource(fedScan.SourceId);
        if (source == null)
            return plan;

        var info = source.GetInfo();
        if (!info.SupportsAggregationPushdown)
            return plan;

        // Push the entire aggregation to the remote source
        var networkCost = EstimateNetworkCost(info, agg.EstimatedRows);
        var remoteCost = agg.EstimatedCost * info.CostMultiplier * 0.3 + networkCost; // Remote agg is cheaper: fewer rows transferred

        QueryPlanNode result = new FederatedAggregateNode(
            fedScan.SourceId,
            fedScan.TableName,
            agg.GroupByKeys,
            agg.Aggregations,
            fedScan.PushedFilters,
            agg.EstimatedRows,
            remoteCost);

        // Re-wrap with limit and project if they existed
        if (outerLimit != null)
        {
            result = new LimitNode(result, outerLimit.Limit, outerLimit.Offset,
                outerLimit.EstimatedRows, outerLimit.EstimatedCost);
        }

        if (outerProject != null)
        {
            result = new ProjectNode(result, outerProject.Columns,
                outerProject.EstimatedRows, outerProject.EstimatedCost);
        }

        return result;
    }

    private static FederatedTableScanNode? FindFederatedScan(QueryPlanNode node)
    {
        return node switch
        {
            FederatedTableScanNode fed => fed,
            FilterNode filter => FindFederatedScan(filter.Input),
            ProjectNode project => FindFederatedScan(project.Input),
            _ => null
        };
    }

    // ---------------------------------------------------------------
    // Cost Estimation Helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Estimates the network transfer cost for fetching rows from a remote source.
    /// Cost = (rows * bytesPerRow) / bandwidthBytesPerSec + latencySec
    /// </summary>
    private static double EstimateNetworkCost(FederatedDataSourceInfo info, double estimatedRows)
    {
        if (info.SourceType == FederatedSourceType.Local)
            return 0.0;

        var bandwidthBytesPerSec = info.BandwidthMbps * MbpsToBytesPerSec;
        if (bandwidthBytesPerSec <= 0)
            bandwidthBytesPerSec = 1.0; // Avoid division by zero

        var transferTimeSec = estimatedRows * DefaultBytesPerRow / bandwidthBytesPerSec;
        var latencySec = info.LatencyMs * MsToSec;

        return transferTimeSec + latencySec;
    }

    /// <summary>
    /// Estimates the additional cost of a cross-source join where data
    /// from at least one remote source must be pulled locally.
    /// </summary>
    private static double EstimateCrossSourceJoinCost(QueryPlanNode left, QueryPlanNode right)
    {
        double extraCost = 0;

        // If both sides are federated, account for pulling the smaller side
        var leftFed = FindFederatedScan(left);
        var rightFed = FindFederatedScan(right);

        if (leftFed != null && rightFed != null)
        {
            // Pull the smaller side to where the larger side is
            var smallerRows = Math.Min(left.EstimatedRows, right.EstimatedRows);
            extraCost = smallerRows * DefaultBytesPerRow * 0.001; // Transfer penalty
        }
        else if (leftFed != null || rightFed != null)
        {
            // One side is remote — add transfer cost for the remote side
            var remoteSide = leftFed != null ? left : right;
            extraCost = remoteSide.EstimatedRows * DefaultBytesPerRow * 0.0005;
        }

        return extraCost;
    }

    /// <summary>
    /// Extracts column names from a SELECT column list for projection pushdown.
    /// Only includes simple column references (not computed expressions).
    /// </summary>
    private static ImmutableArray<string> ExtractColumnNames(ImmutableArray<SelectColumn> columns)
    {
        var builder = ImmutableArray.CreateBuilder<string>();

        foreach (var col in columns)
        {
            if (col.Expression is ColumnReference colRef)
            {
                builder.Add(colRef.Column);
            }
            else if (col.Expression is WildcardExpression)
            {
                // Wildcard = all columns, no projection pushdown
                return ImmutableArray<string>.Empty;
            }
        }

        return builder.ToImmutable();
    }
}
