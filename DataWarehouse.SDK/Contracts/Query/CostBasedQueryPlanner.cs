using System.Collections.Immutable;

namespace DataWarehouse.SDK.Contracts.Query;

/// <summary>
/// Cost-based query planner that transforms SQL AST into optimized physical
/// execution plans. Uses table statistics (when available) for cardinality
/// estimation, join reordering, and algorithm selection.
/// </summary>
public sealed class CostBasedQueryPlanner : IQueryPlanner
{
    private const long DefaultRowCount = 1000;
    private const double HashBuildCost = 1.5;
    private const double HashProbeCost = 0.5;
    private const double NestedLoopCostPerRow = 1.0;
    private const double SortCostFactor = 2.0; // n * log2(n) approximated
    private const double FilterCostPerRow = 0.1;
    private const double ProjectCostPerRow = 0.05;

    private readonly QueryOptimizer _optimizer = new();
    private ITableStatisticsProvider? _stats;

    /// <inheritdoc />
    public QueryPlanNode Plan(SelectStatement statement, ITableStatisticsProvider? stats = null)
    {
        _stats = stats;

        // Step 1: Build initial logical plan from AST
        var plan = BuildInitialPlan(statement);

        // Step 2: Apply optimizer rules
        plan = _optimizer.Optimize(plan);

        return plan;
    }

    /// <inheritdoc />
    public QueryPlanNode Optimize(QueryPlanNode plan)
    {
        return _optimizer.Optimize(plan);
    }

    // ─────────────────────────────────────────────────────────────
    // Initial plan construction from AST
    // ─────────────────────────────────────────────────────────────

    private QueryPlanNode BuildInitialPlan(SelectStatement stmt)
    {
        // Start with FROM clause -> base scan
        QueryPlanNode? plan = null;

        if (stmt.From != null)
        {
            plan = BuildTableScan(stmt.From);
        }

        // Apply JOINs in declaration order (naive; optimizer will reorder)
        if (!stmt.Joins.IsDefaultOrEmpty)
        {
            plan ??= BuildTableScan(stmt.Joins[0].Table);

            foreach (var join in stmt.Joins)
            {
                if (plan is TableScanNode && stmt.From != null)
                {
                    // First join: plan is FROM table, join with the join table
                    var rightPlan = BuildTableScan(join.Table);
                    plan = BuildJoinNode(plan, rightPlan, join);
                }
                else
                {
                    var rightPlan = BuildTableScan(join.Table);
                    plan = BuildJoinNode(plan, rightPlan, join);
                }
            }
        }

        plan ??= new TableScanNode("dual", ImmutableArray<string>.Empty, 1, 0);

        // Apply WHERE filter
        if (stmt.Where != null)
        {
            var selectivity = EstimateSelectivity(stmt.Where, plan.EstimatedRows);
            var filteredRows = Math.Max(1, plan.EstimatedRows * selectivity);
            var filterCost = plan.EstimatedCost + plan.EstimatedRows * FilterCostPerRow;
            plan = new FilterNode(plan, stmt.Where, filteredRows, filterCost);
        }

        // Apply GROUP BY + aggregates
        if (!stmt.GroupBy.IsDefaultOrEmpty || HasAggregateFunctions(stmt.Columns))
        {
            var aggregations = ExtractAggregates(stmt.Columns);
            var groupKeys = stmt.GroupBy.IsDefaultOrEmpty
                ? ImmutableArray<Expression>.Empty
                : stmt.GroupBy;

            var estimatedGroups = groupKeys.Length > 0
                ? EstimateDistinctGroups(groupKeys, plan.EstimatedRows)
                : 1.0;
            var aggCost = plan.EstimatedCost + plan.EstimatedRows * 0.2;
            plan = new AggregateNode(plan, groupKeys, aggregations, estimatedGroups, aggCost);
        }

        // Apply HAVING filter (after aggregation)
        if (stmt.Having != null)
        {
            var selectivity = EstimateSelectivity(stmt.Having, plan.EstimatedRows);
            var filteredRows = Math.Max(1, plan.EstimatedRows * selectivity);
            var filterCost = plan.EstimatedCost + plan.EstimatedRows * FilterCostPerRow;
            plan = new FilterNode(plan, stmt.Having, filteredRows, filterCost);
        }

        // Apply ORDER BY
        if (!stmt.OrderBy.IsDefaultOrEmpty)
        {
            var sortCost = plan.EstimatedCost +
                           plan.EstimatedRows * SortCostFactor * Math.Log2(Math.Max(2, plan.EstimatedRows));
            plan = new SortNode(plan, stmt.OrderBy, plan.EstimatedRows, sortCost);
        }

        // Apply projection (SELECT columns)
        if (!stmt.Columns.IsDefaultOrEmpty && !IsWildcardOnly(stmt.Columns))
        {
            var projectCost = plan.EstimatedCost + plan.EstimatedRows * ProjectCostPerRow;
            plan = new ProjectNode(plan, stmt.Columns, plan.EstimatedRows, projectCost);
        }

        // Apply LIMIT/OFFSET
        if (stmt.Limit.HasValue)
        {
            var offset = stmt.Offset ?? 0;
            var limitRows = Math.Min(stmt.Limit.Value, plan.EstimatedRows);
            plan = new LimitNode(plan, stmt.Limit.Value, offset, limitRows, plan.EstimatedCost);
        }

        return plan;
    }

    private QueryPlanNode BuildTableScan(TableReference tableRef)
    {
        if (tableRef is NamedTableReference named)
        {
            var tableName = named.Alias ?? named.TableName;
            var sourceTable = named.TableName;
            var stats = _stats?.GetStatistics(sourceTable);
            var rowCount = (double)(stats?.RowCount ?? DefaultRowCount);
            var scanCost = rowCount; // Simple: cost proportional to rows
            return new TableScanNode(sourceTable, ImmutableArray<string>.Empty, rowCount, scanCost);
        }

        if (tableRef is SubqueryTableReference subquery)
        {
            // Recursively plan the subquery
            return BuildInitialPlan(subquery.Subquery);
        }

        return new TableScanNode("unknown", ImmutableArray<string>.Empty, DefaultRowCount, DefaultRowCount);
    }

    private QueryPlanNode BuildJoinNode(QueryPlanNode left, QueryPlanNode right, JoinClause join)
    {
        // Extract join keys for equi-joins
        if (join.OnCondition is BinaryExpression { Operator: BinaryOperator.Equals } eq)
        {
            var leftKey = eq.Left;
            var rightKey = eq.Right;

            // Estimate join cardinality
            var joinRows = EstimateJoinCardinality(left.EstimatedRows, right.EstimatedRows, leftKey, rightKey);

            // Choose join algorithm based on cost
            if (right.EstimatedRows < 100)
            {
                // Nested loop for small right side
                var nlCost = left.EstimatedCost + right.EstimatedCost +
                             left.EstimatedRows * right.EstimatedRows * NestedLoopCostPerRow;
                return new NestedLoopJoinNode(left, right, join.OnCondition, join.JoinType, joinRows, nlCost);
            }

            // Hash join: put smaller side as build (left)
            QueryPlanNode buildSide, probeSide;
            Expression buildKey, probeKey;

            if (left.EstimatedRows <= right.EstimatedRows)
            {
                buildSide = left;
                probeSide = right;
                buildKey = leftKey;
                probeKey = rightKey;
            }
            else
            {
                buildSide = right;
                probeSide = left;
                buildKey = rightKey;
                probeKey = leftKey;
            }

            var hashCost = buildSide.EstimatedCost + probeSide.EstimatedCost +
                           buildSide.EstimatedRows * HashBuildCost +
                           probeSide.EstimatedRows * HashProbeCost;

            return new HashJoinNode(buildSide, probeSide, buildKey, probeKey, join.JoinType, joinRows, hashCost);
        }

        // Non-equi join or CROSS join -> nested loop
        var crossRows = join.JoinType == JoinType.Cross
            ? left.EstimatedRows * right.EstimatedRows
            : left.EstimatedRows * right.EstimatedRows * 0.1; // guess 10% selectivity

        var crossCost = left.EstimatedCost + right.EstimatedCost +
                        left.EstimatedRows * right.EstimatedRows * NestedLoopCostPerRow;

        return new NestedLoopJoinNode(left, right, join.OnCondition, join.JoinType, crossRows, crossCost);
    }

    // ─────────────────────────────────────────────────────────────
    // Join reordering
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reorders joins to minimize cost. For 2-3 joins, enumerates all orderings.
    /// For 4+, uses greedy heuristic (join smallest intermediate result next).
    /// Called by QueryOptimizer during the optimization phase.
    /// </summary>
    internal QueryPlanNode ReorderJoins(
        IReadOnlyList<QueryPlanNode> tables,
        IReadOnlyList<JoinClause> joinClauses)
    {
        if (tables.Count <= 1)
            return tables[0];

        if (tables.Count <= 3)
            return EnumerateJoinOrders(tables, joinClauses);

        return GreedyJoinOrder(tables, joinClauses);
    }

    private QueryPlanNode EnumerateJoinOrders(
        IReadOnlyList<QueryPlanNode> tables,
        IReadOnlyList<JoinClause> joinClauses)
    {
        var permutations = Permute(Enumerable.Range(0, tables.Count).ToList());
        QueryPlanNode? bestPlan = null;
        var bestCost = double.MaxValue;

        foreach (var perm in permutations)
        {
            var plan = tables[perm[0]];
            var totalCost = plan.EstimatedCost;

            for (int i = 1; i < perm.Count; i++)
            {
                var right = tables[perm[i]];
                var join = i - 1 < joinClauses.Count ? joinClauses[i - 1] : null;
                var joined = join != null
                    ? BuildJoinNode(plan, right, join)
                    : BuildJoinNode(plan, right,
                        new JoinClause(JoinType.Cross, new NamedTableReference("unknown", null, null), null));
                plan = joined;
                totalCost = plan.EstimatedCost;
            }

            if (totalCost < bestCost)
            {
                bestCost = totalCost;
                bestPlan = plan;
            }
        }

        return bestPlan!;
    }

    private QueryPlanNode GreedyJoinOrder(
        IReadOnlyList<QueryPlanNode> tables,
        IReadOnlyList<JoinClause> joinClauses)
    {
        var remaining = new List<QueryPlanNode>(tables);

        // Start with the smallest table
        remaining.Sort((a, b) => a.EstimatedRows.CompareTo(b.EstimatedRows));
        var plan = remaining[0];
        remaining.RemoveAt(0);

        var joinIdx = 0;
        while (remaining.Count > 0)
        {
            // Pick the table that produces the smallest intermediate result
            var bestIdx = 0;
            var bestResultRows = double.MaxValue;
            QueryPlanNode? bestJoined = null;

            for (int i = 0; i < remaining.Count; i++)
            {
                var join = joinIdx < joinClauses.Count ? joinClauses[joinIdx] : null;
                var joined = join != null
                    ? BuildJoinNode(plan, remaining[i], join)
                    : BuildJoinNode(plan, remaining[i],
                        new JoinClause(JoinType.Cross, new NamedTableReference("unknown", null, null), null));

                if (joined.EstimatedRows < bestResultRows)
                {
                    bestResultRows = joined.EstimatedRows;
                    bestIdx = i;
                    bestJoined = joined;
                }
            }

            plan = bestJoined!;
            remaining.RemoveAt(bestIdx);
            joinIdx++;
        }

        return plan;
    }

    private static List<List<int>> Permute(List<int> items)
    {
        var result = new List<List<int>>();
        PermuteHelper(items, 0, result);
        return result;
    }

    private static void PermuteHelper(List<int> items, int start, List<List<int>> result)
    {
        if (start == items.Count)
        {
            result.Add(new List<int>(items));
            return;
        }
        for (int i = start; i < items.Count; i++)
        {
            (items[start], items[i]) = (items[i], items[start]);
            PermuteHelper(items, start + 1, result);
            (items[start], items[i]) = (items[i], items[start]);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Cardinality & selectivity estimation
    // ─────────────────────────────────────────────────────────────

    private double EstimateSelectivity(Expression predicate, double inputRows)
    {
        return predicate switch
        {
            BinaryExpression { Operator: BinaryOperator.Equals } eq =>
                EstimateEqualitySelectivity(eq),

            BinaryExpression { Operator: BinaryOperator.And } and =>
                EstimateSelectivity(and.Left, inputRows) * EstimateSelectivity(and.Right, inputRows),

            BinaryExpression { Operator: BinaryOperator.Or } or =>
                CombineOrSelectivity(
                    EstimateSelectivity(or.Left, inputRows),
                    EstimateSelectivity(or.Right, inputRows)),

            BinaryExpression { Operator: BinaryOperator.LessThan or BinaryOperator.GreaterThan
                or BinaryOperator.LessOrEqual or BinaryOperator.GreaterOrEqual } =>
                0.33,

            BinaryExpression { Operator: BinaryOperator.NotEquals } =>
                0.9,

            LikeExpression => 0.1,

            IsNullExpression { Negated: false } => 0.05,
            IsNullExpression { Negated: true } => 0.95,

            InExpression inExpr =>
                inExpr.Values != null ? Math.Min(1.0, inExpr.Values.Value.Length * 0.05) : 0.25,

            BetweenExpression => 0.25,

            UnaryExpression { Operator: UnaryOperator.Not } not =>
                1.0 - EstimateSelectivity(not.Operand, inputRows),

            ExistsExpression => 0.5,

            LiteralExpression { LiteralType: LiteralType.Boolean, Value: true } => 1.0,
            LiteralExpression { LiteralType: LiteralType.Boolean, Value: false } => 0.0,

            _ => 0.5 // Unknown: assume 50% selectivity
        };
    }

    private double EstimateEqualitySelectivity(BinaryExpression eq)
    {
        // If we have column stats, use 1/distinct_count
        if (eq.Left is ColumnReference colRef && _stats != null)
        {
            var tableName = colRef.Table ?? string.Empty;
            var tableStats = _stats.GetStatistics(tableName);
            if (tableStats?.Columns.TryGetValue(colRef.Column, out var colStats) == true && colStats.DistinctCount > 0)
            {
                return 1.0 / colStats.DistinctCount;
            }
        }

        // Literal = Literal -> constant true/false
        if (eq.Left is LiteralExpression leftLit && eq.Right is LiteralExpression rightLit)
        {
            return Equals(leftLit.Value, rightLit.Value) ? 1.0 : 0.0;
        }

        return 0.1; // Default equality selectivity
    }

    private static double CombineOrSelectivity(double s1, double s2)
    {
        return s1 + s2 - (s1 * s2); // P(A or B) = P(A) + P(B) - P(A and B)
    }

    private double EstimateJoinCardinality(double leftRows, double rightRows, Expression leftKey, Expression rightKey)
    {
        // If we have stats for the join key, use them
        if (leftKey is ColumnReference leftCol && _stats != null)
        {
            var leftStats = _stats.GetStatistics(leftCol.Table ?? string.Empty);
            if (leftStats != null && leftStats.Columns.TryGetValue(leftCol.Column, out var leftColStats) && leftColStats.DistinctCount > 0)
            {
                if (rightKey is ColumnReference rightCol)
                {
                    var rightStats = _stats.GetStatistics(rightCol.Table ?? string.Empty);
                    if (rightStats != null && rightStats.Columns.TryGetValue(rightCol.Column, out var rightColStats) && rightColStats.DistinctCount > 0)
                    {
                        // Standard join cardinality formula: |L| * |R| / max(distinct_L, distinct_R)
                        return leftRows * rightRows / Math.Max(leftColStats.DistinctCount, rightColStats.DistinctCount);
                    }
                }
            }
        }

        // Without stats: assume 1/10 of the cross product
        return leftRows * rightRows * 0.1;
    }

    private double EstimateDistinctGroups(ImmutableArray<Expression> groupKeys, double inputRows)
    {
        // Simple heuristic: assume each group key halves the groups
        var estimated = inputRows;
        foreach (var key in groupKeys)
        {
            if (key is ColumnReference colRef && _stats != null)
            {
                var tableStats = _stats.GetStatistics(colRef.Table ?? string.Empty);
                if (tableStats?.Columns.TryGetValue(colRef.Column, out var colStats) == true && colStats.DistinctCount > 0)
                {
                    estimated = Math.Min(estimated, colStats.DistinctCount);
                    continue;
                }
            }
            estimated = Math.Max(1, estimated * 0.5);
        }
        return Math.Max(1, estimated);
    }

    // ─────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────

    private static bool HasAggregateFunctions(ImmutableArray<SelectColumn> columns)
    {
        if (columns.IsDefaultOrEmpty) return false;
        return columns.Any(c => IsAggregateExpression(c.Expression));
    }

    private static bool IsAggregateExpression(Expression expr)
    {
        return expr is FunctionCallExpression func &&
               func.FunctionName.ToUpperInvariant() is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX";
    }

    private static ImmutableArray<AggregateFunction> ExtractAggregates(ImmutableArray<SelectColumn> columns)
    {
        var builder = ImmutableArray.CreateBuilder<AggregateFunction>();
        if (columns.IsDefaultOrEmpty) return builder.ToImmutable();

        foreach (var col in columns)
        {
            if (col.Expression is FunctionCallExpression func && IsAggregateExpression(func))
            {
                var arg = func.Arguments.IsDefaultOrEmpty ? null : func.Arguments[0];
                builder.Add(new AggregateFunction(func.FunctionName.ToUpperInvariant(), arg, col.Alias));
            }
        }

        return builder.ToImmutable();
    }

    private static bool IsWildcardOnly(ImmutableArray<SelectColumn> columns)
    {
        return columns.Length == 1 && columns[0].Expression is WildcardExpression { Table: null };
    }
}
