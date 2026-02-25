using System.Collections.Immutable;

namespace DataWarehouse.SDK.Contracts.Query;

/// <summary>
/// Applies optimization rules to physical query plans in a fixed-point loop.
/// Rules are applied in sequence until no rule changes the plan, with a
/// safety limit of 10 iterations to prevent infinite loops.
/// </summary>
public sealed class QueryOptimizer
{
    private const int MaxIterations = 10;

    private readonly List<Func<QueryPlanNode, QueryPlanNode>> _rules;

    public QueryOptimizer()
    {
        _rules = new List<Func<QueryPlanNode, QueryPlanNode>>
        {
            PredicatePushdown,
            ProjectionPushdown,
            ConstantFolding,
            JoinTypeSelection,
            RedundantFilterElimination
        };
    }

    /// <summary>
    /// Applies all optimization rules repeatedly until fixed point or max iterations.
    /// </summary>
    public QueryPlanNode Optimize(QueryPlanNode plan)
    {
        for (int i = 0; i < MaxIterations; i++)
        {
            var optimized = plan;
            foreach (var rule in _rules)
            {
                optimized = rule(optimized);
            }

            // Fixed point: no rule changed the plan
            if (ReferenceEquals(optimized, plan) || optimized.Equals(plan))
                break;

            plan = optimized;
        }

        return plan;
    }

    // ─────────────────────────────────────────────────────────────
    // Rule 1: Predicate Pushdown
    // Moves filter nodes below joins when the filter references only
    // one side of the join.
    // ─────────────────────────────────────────────────────────────

    private QueryPlanNode PredicatePushdown(QueryPlanNode node)
    {
        return node switch
        {
            FilterNode { Input: HashJoinNode join } filter =>
                TryPushFilterBelowJoin(filter, join),

            FilterNode { Input: NestedLoopJoinNode nlJoin } filter =>
                TryPushFilterBelowNestedLoopJoin(filter, nlJoin),

            FilterNode { Input: MergeJoinNode mergeJoin } filter =>
                TryPushFilterBelowMergeJoin(filter, mergeJoin),

            // Recurse into children
            FilterNode f => f with { Input = PredicatePushdown(f.Input) },
            ProjectNode p => p with { Input = PredicatePushdown(p.Input) },
            SortNode s => s with { Input = PredicatePushdown(s.Input) },
            AggregateNode a => a with { Input = PredicatePushdown(a.Input) },
            LimitNode l => l with { Input = PredicatePushdown(l.Input) },
            HashJoinNode hj => hj with
            {
                Left = PredicatePushdown(hj.Left),
                Right = PredicatePushdown(hj.Right)
            },
            NestedLoopJoinNode nl => nl with
            {
                Left = PredicatePushdown(nl.Left),
                Right = PredicatePushdown(nl.Right)
            },
            MergeJoinNode mj => mj with
            {
                Left = PredicatePushdown(mj.Left),
                Right = PredicatePushdown(mj.Right)
            },
            _ => node
        };
    }

    private QueryPlanNode TryPushFilterBelowJoin(FilterNode filter, HashJoinNode join)
    {
        var leftTables = CollectTableNames(join.Left);
        var rightTables = CollectTableNames(join.Right);
        var filterTables = CollectReferencedTables(filter.Predicate);

        // Filter references only left side -> push to left
        if (filterTables.Count > 0 && filterTables.IsSubsetOf(leftTables))
        {
            var newLeft = new FilterNode(join.Left, filter.Predicate,
                Math.Max(1, join.Left.EstimatedRows * 0.5),
                join.Left.EstimatedCost + join.Left.EstimatedRows * 0.1);
            var newJoin = join with { Left = newLeft };
            return RecalculateJoinCost(newJoin);
        }

        // Filter references only right side -> push to right
        if (filterTables.Count > 0 && filterTables.IsSubsetOf(rightTables))
        {
            var newRight = new FilterNode(join.Right, filter.Predicate,
                Math.Max(1, join.Right.EstimatedRows * 0.5),
                join.Right.EstimatedCost + join.Right.EstimatedRows * 0.1);
            var newJoin = join with { Right = newRight };
            return RecalculateJoinCost(newJoin);
        }

        // Can't push: recurse into join children
        return filter with
        {
            Input = join with
            {
                Left = PredicatePushdown(join.Left),
                Right = PredicatePushdown(join.Right)
            }
        };
    }

    private QueryPlanNode TryPushFilterBelowNestedLoopJoin(FilterNode filter, NestedLoopJoinNode join)
    {
        var leftTables = CollectTableNames(join.Left);
        var rightTables = CollectTableNames(join.Right);
        var filterTables = CollectReferencedTables(filter.Predicate);

        if (filterTables.Count > 0 && filterTables.IsSubsetOf(leftTables))
        {
            var newLeft = new FilterNode(join.Left, filter.Predicate,
                Math.Max(1, join.Left.EstimatedRows * 0.5),
                join.Left.EstimatedCost + join.Left.EstimatedRows * 0.1);
            return join with { Left = newLeft };
        }

        if (filterTables.Count > 0 && filterTables.IsSubsetOf(rightTables))
        {
            var newRight = new FilterNode(join.Right, filter.Predicate,
                Math.Max(1, join.Right.EstimatedRows * 0.5),
                join.Right.EstimatedCost + join.Right.EstimatedRows * 0.1);
            return join with { Right = newRight };
        }

        return filter with
        {
            Input = join with
            {
                Left = PredicatePushdown(join.Left),
                Right = PredicatePushdown(join.Right)
            }
        };
    }

    private QueryPlanNode TryPushFilterBelowMergeJoin(FilterNode filter, MergeJoinNode join)
    {
        var leftTables = CollectTableNames(join.Left);
        var rightTables = CollectTableNames(join.Right);
        var filterTables = CollectReferencedTables(filter.Predicate);

        if (filterTables.Count > 0 && filterTables.IsSubsetOf(leftTables))
        {
            var newLeft = new FilterNode(join.Left, filter.Predicate,
                Math.Max(1, join.Left.EstimatedRows * 0.5),
                join.Left.EstimatedCost + join.Left.EstimatedRows * 0.1);
            return join with { Left = newLeft };
        }

        if (filterTables.Count > 0 && filterTables.IsSubsetOf(rightTables))
        {
            var newRight = new FilterNode(join.Right, filter.Predicate,
                Math.Max(1, join.Right.EstimatedRows * 0.5),
                join.Right.EstimatedCost + join.Right.EstimatedRows * 0.1);
            return join with { Right = newRight };
        }

        return filter with
        {
            Input = join with
            {
                Left = PredicatePushdown(join.Left),
                Right = PredicatePushdown(join.Right)
            }
        };
    }

    // ─────────────────────────────────────────────────────────────
    // Rule 2: Projection Pushdown
    // Pushes column projections down to table scans to avoid
    // materializing unused columns.
    // ─────────────────────────────────────────────────────────────

    private QueryPlanNode ProjectionPushdown(QueryPlanNode node)
    {
        if (node is ProjectNode { Input: TableScanNode scan } project)
        {
            var neededColumns = ExtractColumnNames(project.Columns);
            if (neededColumns.Length > 0 && (scan.Columns.IsDefaultOrEmpty || scan.Columns.Length == 0))
            {
                var newScan = scan with { Columns = neededColumns };
                return project with { Input = newScan };
            }
        }

        // Recurse
        return node switch
        {
            FilterNode f => f with { Input = ProjectionPushdown(f.Input) },
            ProjectNode p => p with { Input = ProjectionPushdown(p.Input) },
            SortNode s => s with { Input = ProjectionPushdown(s.Input) },
            AggregateNode a => a with { Input = ProjectionPushdown(a.Input) },
            LimitNode l => l with { Input = ProjectionPushdown(l.Input) },
            HashJoinNode hj => hj with
            {
                Left = ProjectionPushdown(hj.Left),
                Right = ProjectionPushdown(hj.Right)
            },
            NestedLoopJoinNode nl => nl with
            {
                Left = ProjectionPushdown(nl.Left),
                Right = ProjectionPushdown(nl.Right)
            },
            MergeJoinNode mj => mj with
            {
                Left = ProjectionPushdown(mj.Left),
                Right = ProjectionPushdown(mj.Right)
            },
            _ => node
        };
    }

    // ─────────────────────────────────────────────────────────────
    // Rule 3: Constant Folding
    // Evaluates constant expressions at plan time.
    // ─────────────────────────────────────────────────────────────

    private QueryPlanNode ConstantFolding(QueryPlanNode node)
    {
        return node switch
        {
            FilterNode f => f with
            {
                Predicate = FoldExpression(f.Predicate),
                Input = ConstantFolding(f.Input)
            },
            ProjectNode p => p with { Input = ConstantFolding(p.Input) },
            SortNode s => s with { Input = ConstantFolding(s.Input) },
            AggregateNode a => a with { Input = ConstantFolding(a.Input) },
            LimitNode l => l with { Input = ConstantFolding(l.Input) },
            HashJoinNode hj => hj with
            {
                Left = ConstantFolding(hj.Left),
                Right = ConstantFolding(hj.Right)
            },
            NestedLoopJoinNode nl => nl with
            {
                Left = ConstantFolding(nl.Left),
                Right = ConstantFolding(nl.Right)
            },
            MergeJoinNode mj => mj with
            {
                Left = ConstantFolding(mj.Left),
                Right = ConstantFolding(mj.Right)
            },
            _ => node
        };
    }

    private Expression FoldExpression(Expression expr)
    {
        return expr switch
        {
            // Arithmetic: literal op literal
            BinaryExpression { Left: LiteralExpression { LiteralType: LiteralType.Integer, Value: long lv },
                               Right: LiteralExpression { LiteralType: LiteralType.Integer, Value: long rv } } bin =>
                bin.Operator switch
                {
                    BinaryOperator.Plus => new LiteralExpression(lv + rv, LiteralType.Integer),
                    BinaryOperator.Minus => new LiteralExpression(lv - rv, LiteralType.Integer),
                    BinaryOperator.Multiply => new LiteralExpression(lv * rv, LiteralType.Integer),
                    BinaryOperator.Divide when rv != 0 => new LiteralExpression(lv / rv, LiteralType.Integer),
                    _ => bin
                },

            // Comparison: literal = literal
            BinaryExpression { Left: LiteralExpression left, Right: LiteralExpression right,
                               Operator: BinaryOperator.Equals } =>
                new LiteralExpression(Equals(left.Value, right.Value), LiteralType.Boolean),

            BinaryExpression { Left: LiteralExpression left, Right: LiteralExpression right,
                               Operator: BinaryOperator.NotEquals } =>
                new LiteralExpression(!Equals(left.Value, right.Value), LiteralType.Boolean),

            // Recurse into binary
            BinaryExpression bin => bin with
            {
                Left = FoldExpression(bin.Left),
                Right = FoldExpression(bin.Right)
            },

            // Recurse into unary
            UnaryExpression { Operator: UnaryOperator.Not,
                              Operand: LiteralExpression { LiteralType: LiteralType.Boolean, Value: bool bv } } =>
                new LiteralExpression(!bv, LiteralType.Boolean),

            UnaryExpression un => un with { Operand = FoldExpression(un.Operand) },

            _ => expr
        };
    }

    // ─────────────────────────────────────────────────────────────
    // Rule 4: Join Type Selection
    // Chooses between HashJoin, NestedLoopJoin, and MergeJoin
    // based on cost characteristics.
    // ─────────────────────────────────────────────────────────────

    private QueryPlanNode JoinTypeSelection(QueryPlanNode node)
    {
        return node switch
        {
            // If hash join has very small right side, convert to nested loop
            HashJoinNode hj when hj.Right.EstimatedRows < 100 =>
                new NestedLoopJoinNode(
                    JoinTypeSelection(hj.Left),
                    JoinTypeSelection(hj.Right),
                    new BinaryExpression(hj.LeftKey, BinaryOperator.Equals, hj.RightKey),
                    hj.JoinType,
                    hj.EstimatedRows,
                    hj.Left.EstimatedCost + hj.Right.EstimatedCost +
                        hj.Left.EstimatedRows * hj.Right.EstimatedRows * 0.01),

            // If both sides of a hash join have SortNode children, use merge join
            HashJoinNode { Left: SortNode, Right: SortNode } hj =>
                new MergeJoinNode(
                    JoinTypeSelection(hj.Left),
                    JoinTypeSelection(hj.Right),
                    hj.LeftKey,
                    hj.RightKey,
                    hj.JoinType,
                    hj.EstimatedRows,
                    hj.Left.EstimatedCost + hj.Right.EstimatedCost + hj.EstimatedRows * 0.5),

            // Recurse
            HashJoinNode hj => hj with
            {
                Left = JoinTypeSelection(hj.Left),
                Right = JoinTypeSelection(hj.Right)
            },
            NestedLoopJoinNode nl => nl with
            {
                Left = JoinTypeSelection(nl.Left),
                Right = JoinTypeSelection(nl.Right)
            },
            MergeJoinNode mj => mj with
            {
                Left = JoinTypeSelection(mj.Left),
                Right = JoinTypeSelection(mj.Right)
            },
            FilterNode f => f with { Input = JoinTypeSelection(f.Input) },
            ProjectNode p => p with { Input = JoinTypeSelection(p.Input) },
            SortNode s => s with { Input = JoinTypeSelection(s.Input) },
            AggregateNode a => a with { Input = JoinTypeSelection(a.Input) },
            LimitNode l => l with { Input = JoinTypeSelection(l.Input) },
            _ => node
        };
    }

    // ─────────────────────────────────────────────────────────────
    // Rule 5: Redundant Filter Elimination
    // Removes always-true filters and short-circuits always-false.
    // ─────────────────────────────────────────────────────────────

    private QueryPlanNode RedundantFilterElimination(QueryPlanNode node)
    {
        if (node is FilterNode filter)
        {
            var folded = FoldExpression(filter.Predicate);

            // Always-true filter: remove it entirely
            if (folded is LiteralExpression { LiteralType: LiteralType.Boolean, Value: true })
            {
                return RedundantFilterElimination(filter.Input);
            }

            // Always-false filter: return empty scan (0 rows)
            if (folded is LiteralExpression { LiteralType: LiteralType.Boolean, Value: false })
            {
                return new TableScanNode("empty", ImmutableArray<string>.Empty, 0, 0);
            }

            return filter with
            {
                Predicate = folded,
                Input = RedundantFilterElimination(filter.Input)
            };
        }

        // Recurse into other nodes
        return node switch
        {
            ProjectNode p => p with { Input = RedundantFilterElimination(p.Input) },
            SortNode s => s with { Input = RedundantFilterElimination(s.Input) },
            AggregateNode a => a with { Input = RedundantFilterElimination(a.Input) },
            LimitNode l => l with { Input = RedundantFilterElimination(l.Input) },
            HashJoinNode hj => hj with
            {
                Left = RedundantFilterElimination(hj.Left),
                Right = RedundantFilterElimination(hj.Right)
            },
            NestedLoopJoinNode nl => nl with
            {
                Left = RedundantFilterElimination(nl.Left),
                Right = RedundantFilterElimination(nl.Right)
            },
            MergeJoinNode mj => mj with
            {
                Left = RedundantFilterElimination(mj.Left),
                Right = RedundantFilterElimination(mj.Right)
            },
            _ => node
        };
    }

    // ─────────────────────────────────────────────────────────────
    // Utilities
    // ─────────────────────────────────────────────────────────────

    private static HashSet<string> CollectTableNames(QueryPlanNode node)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectTableNamesRecursive(node, tables);
        return tables;
    }

    private static void CollectTableNamesRecursive(QueryPlanNode node, HashSet<string> tables)
    {
        switch (node)
        {
            case TableScanNode scan:
                tables.Add(scan.TableName);
                break;
            case IndexScanNode idx:
                tables.Add(idx.TableName);
                break;
            case FilterNode f:
                CollectTableNamesRecursive(f.Input, tables);
                break;
            case ProjectNode p:
                CollectTableNamesRecursive(p.Input, tables);
                break;
            case SortNode s:
                CollectTableNamesRecursive(s.Input, tables);
                break;
            case AggregateNode a:
                CollectTableNamesRecursive(a.Input, tables);
                break;
            case LimitNode l:
                CollectTableNamesRecursive(l.Input, tables);
                break;
            case HashJoinNode hj:
                CollectTableNamesRecursive(hj.Left, tables);
                CollectTableNamesRecursive(hj.Right, tables);
                break;
            case NestedLoopJoinNode nl:
                CollectTableNamesRecursive(nl.Left, tables);
                CollectTableNamesRecursive(nl.Right, tables);
                break;
            case MergeJoinNode mj:
                CollectTableNamesRecursive(mj.Left, tables);
                CollectTableNamesRecursive(mj.Right, tables);
                break;
            case UnionNode u:
                foreach (var input in u.Inputs)
                    CollectTableNamesRecursive(input, tables);
                break;
        }
    }

    private static HashSet<string> CollectReferencedTables(Expression expr)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectReferencedTablesRecursive(expr, tables);
        return tables;
    }

    private static void CollectReferencedTablesRecursive(Expression expr, HashSet<string> tables)
    {
        switch (expr)
        {
            case ColumnReference col when col.Table != null:
                tables.Add(col.Table);
                break;
            case BinaryExpression bin:
                CollectReferencedTablesRecursive(bin.Left, tables);
                CollectReferencedTablesRecursive(bin.Right, tables);
                break;
            case UnaryExpression un:
                CollectReferencedTablesRecursive(un.Operand, tables);
                break;
            case FunctionCallExpression func:
                foreach (var arg in func.Arguments)
                    CollectReferencedTablesRecursive(arg, tables);
                break;
            case InExpression inExpr:
                CollectReferencedTablesRecursive(inExpr.Operand, tables);
                if (inExpr.Values != null)
                    foreach (var val in inExpr.Values)
                        CollectReferencedTablesRecursive(val, tables);
                break;
            case BetweenExpression between:
                CollectReferencedTablesRecursive(between.Operand, tables);
                CollectReferencedTablesRecursive(between.Low, tables);
                CollectReferencedTablesRecursive(between.High, tables);
                break;
            case LikeExpression like:
                CollectReferencedTablesRecursive(like.Operand, tables);
                CollectReferencedTablesRecursive(like.Pattern, tables);
                break;
            case IsNullExpression isNull:
                CollectReferencedTablesRecursive(isNull.Operand, tables);
                break;
            case ParenthesizedExpression paren:
                CollectReferencedTablesRecursive(paren.Inner, tables);
                break;
            case CaseExpression caseExpr:
                if (caseExpr.Operand != null)
                    CollectReferencedTablesRecursive(caseExpr.Operand, tables);
                foreach (var when in caseExpr.WhenClauses)
                {
                    CollectReferencedTablesRecursive(when.Condition, tables);
                    CollectReferencedTablesRecursive(when.Result, tables);
                }
                if (caseExpr.ElseExpression != null)
                    CollectReferencedTablesRecursive(caseExpr.ElseExpression, tables);
                break;
            case CastExpression cast:
                CollectReferencedTablesRecursive(cast.Operand, tables);
                break;
        }
    }

    private static ImmutableArray<string> ExtractColumnNames(ImmutableArray<SelectColumn> columns)
    {
        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (var col in columns)
        {
            ExtractColumnNamesFromExpression(col.Expression, builder);
        }
        return builder.ToImmutable();
    }

    private static void ExtractColumnNamesFromExpression(Expression expr, ImmutableArray<string>.Builder builder)
    {
        switch (expr)
        {
            case ColumnReference col:
                builder.Add(col.Column);
                break;
            case FunctionCallExpression func:
                foreach (var arg in func.Arguments)
                    ExtractColumnNamesFromExpression(arg, builder);
                break;
            case BinaryExpression bin:
                ExtractColumnNamesFromExpression(bin.Left, builder);
                ExtractColumnNamesFromExpression(bin.Right, builder);
                break;
            case UnaryExpression un:
                ExtractColumnNamesFromExpression(un.Operand, builder);
                break;
            case CaseExpression caseExpr:
                if (caseExpr.Operand != null)
                    ExtractColumnNamesFromExpression(caseExpr.Operand, builder);
                foreach (var when in caseExpr.WhenClauses)
                {
                    ExtractColumnNamesFromExpression(when.Condition, builder);
                    ExtractColumnNamesFromExpression(when.Result, builder);
                }
                if (caseExpr.ElseExpression != null)
                    ExtractColumnNamesFromExpression(caseExpr.ElseExpression, builder);
                break;
            case CastExpression cast:
                ExtractColumnNamesFromExpression(cast.Operand, builder);
                break;
            case ParenthesizedExpression paren:
                ExtractColumnNamesFromExpression(paren.Inner, builder);
                break;
        }
    }

    private static QueryPlanNode RecalculateJoinCost(HashJoinNode join)
    {
        var buildCost = join.Left.EstimatedRows * 1.5;
        var probeCost = join.Right.EstimatedRows * 0.5;
        var totalCost = join.Left.EstimatedCost + join.Right.EstimatedCost + buildCost + probeCost;
        var joinRows = Math.Max(1, join.Left.EstimatedRows * join.Right.EstimatedRows * 0.1);
        return join with { EstimatedRows = joinRows, EstimatedCost = totalCost };
    }
}
