using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DataWarehouse.SDK.Contracts.Query;

// ─────────────────────────────────────────────────────────────
// Data Source Provider Contract
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Provides table data and statistics to the query execution engine.
/// Implementations bridge the engine to concrete storage backends (CSV, JSON, Parquet, etc.).
/// </summary>
public interface IDataSourceProvider
{
    /// <summary>
    /// Streams table data as columnar batches, optionally projecting only requested columns.
    /// </summary>
    /// <param name="tableName">The registered table name.</param>
    /// <param name="columns">Columns to project (null = all columns).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async stream of columnar batches.</returns>
    IAsyncEnumerable<ColumnarBatch> GetTableData(string tableName, List<string>? columns, CancellationToken ct);

    /// <summary>
    /// Returns table-level statistics for cost estimation, or null if unavailable.
    /// </summary>
    TableStatistics? GetTableStatistics(string tableName);
}

// ─────────────────────────────────────────────────────────────
// Query Result
// ─────────────────────────────────────────────────────────────

/// <summary>
/// The result of executing a SQL query through the execution engine.
/// Contains the result batches, schema, timing, and optional query plan.
/// </summary>
public sealed class QueryExecutionResult
{
    /// <summary>Result data as an async enumerable of columnar batches.</summary>
    public IAsyncEnumerable<ColumnarBatch> Batches { get; init; } = EmptyBatches();

    /// <summary>Schema describing the output columns.</summary>
    public IReadOnlyList<ColumnInfo> Schema { get; init; } = Array.Empty<ColumnInfo>();

    /// <summary>Number of rows affected (for DML), or total rows returned.</summary>
    public long? RowsAffected { get; init; }

    /// <summary>Wall-clock execution time in milliseconds.</summary>
    public double ExecutionTimeMs { get; init; }

    /// <summary>The physical query plan (populated for EXPLAIN queries).</summary>
    public QueryPlanNode? QueryPlan { get; init; }

    private static async IAsyncEnumerable<ColumnarBatch> EmptyBatches()
    {
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>
/// Describes a single column in the query result schema.
/// </summary>
public sealed record ColumnInfo(string Name, ColumnDataType Type);

// ─────────────────────────────────────────────────────────────
// Query Execution Engine
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Production-ready query execution engine that executes physical query plans against
/// table data sources. Orchestrates the full pipeline: Parse SQL -> Plan -> Optimize -> Execute.
/// Integrates ISqlParser, IQueryPlanner, ColumnarEngine, and IDataSourceProvider.
/// </summary>
public sealed class QueryExecutionEngine
{
    private readonly ISqlParser _parser;
    private readonly IQueryPlanner _planner;
    private readonly IDataSourceProvider _dataSource;
    private readonly ITagProvider? _tagProvider;

    // Spill thresholds
    private const long HashJoinSpillThresholdBytes = 256 * 1024 * 1024; // 256 MB
    private const long SortSpillThresholdBytes = 512 * 1024 * 1024;     // 512 MB

    /// <summary>
    /// Initializes a new QueryExecutionEngine.
    /// </summary>
    /// <param name="parser">SQL parser for converting text to AST.</param>
    /// <param name="planner">Query planner for AST to physical plan conversion.</param>
    /// <param name="dataSource">Data source provider for table data access.</param>
    /// <param name="tagProvider">Optional tag provider for tag-aware queries.</param>
    public QueryExecutionEngine(
        ISqlParser parser,
        IQueryPlanner planner,
        IDataSourceProvider dataSource,
        ITagProvider? tagProvider = null)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _tagProvider = tagProvider;
    }

    /// <summary>
    /// Executes a SQL query string end-to-end: parse -> plan -> optimize -> execute.
    /// Supports EXPLAIN prefix to return the query plan instead of data.
    /// </summary>
    /// <param name="sql">The SQL query to execute.</param>
    /// <param name="ct">Cancellation token threaded through all async operations.</param>
    /// <returns>The query result containing batches, schema, and timing.</returns>
    public async Task<QueryExecutionResult> ExecuteQueryAsync(string sql, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // Handle EXPLAIN prefix
        var trimmedSql = sql.TrimStart();
        if (trimmedSql.StartsWith("EXPLAIN", StringComparison.OrdinalIgnoreCase))
        {
            var innerSql = trimmedSql.Substring(7).TrimStart();
            if (innerSql.StartsWith("ANALYZE", StringComparison.OrdinalIgnoreCase))
                innerSql = innerSql.Substring(7).TrimStart();

            return ExecuteExplain(innerSql, sw);
        }

        // 1. Parse SQL to AST
        var statement = _parser.Parse(sql);
        if (statement is not SelectStatement select)
            throw new NotSupportedException("Only SELECT statements are currently supported for execution.");

        // 2. Resolve tag functions in expressions before planning
        var resolvedSelect = _tagProvider != null
            ? TagFunctionResolver.ResolveTagFunctions(select, _tagProvider)
            : select;

        // 3. Create statistics provider from data source
        var statsProvider = new DataSourceStatisticsProvider(_dataSource);

        // 4. Plan and optimize
        var plan = _planner.Plan(resolvedSelect, statsProvider);
        plan = _planner.Optimize(plan);

        // 5. Execute physical plan
        var batches = ExecutePlanAsync(plan, ct);

        // 6. Build schema from plan
        var schema = InferSchemaFromPlan(plan);

        sw.Stop();

        return new QueryExecutionResult
        {
            Batches = batches,
            Schema = schema,
            ExecutionTimeMs = sw.Elapsed.TotalMilliseconds
        };
    }

    // ─────────────────────────────────────────────────────────────
    // EXPLAIN Support
    // ─────────────────────────────────────────────────────────────

    private QueryExecutionResult ExecuteExplain(string innerSql, Stopwatch sw)
    {
        var statement = _parser.Parse(innerSql);
        if (statement is not SelectStatement select)
            throw new NotSupportedException("EXPLAIN only supports SELECT statements.");

        var statsProvider = new DataSourceStatisticsProvider(_dataSource);
        var plan = _planner.Plan(select, statsProvider);
        plan = _planner.Optimize(plan);

        var planText = FormatPlan(plan, 0);
        sw.Stop();

        // Return plan as a single-column text result
        var builder = new ColumnarBatchBuilder(planText.Count);
        builder.AddColumn("Plan", ColumnDataType.String);
        for (int i = 0; i < planText.Count; i++)
            builder.SetValue(0, i, planText[i]);

        var batch = builder.Build();

        return new QueryExecutionResult
        {
            Batches = SingleBatchAsync(batch),
            Schema = new[] { new ColumnInfo("Plan", ColumnDataType.String) },
            QueryPlan = plan,
            RowsAffected = planText.Count,
            ExecutionTimeMs = sw.Elapsed.TotalMilliseconds
        };
    }

    private static List<string> FormatPlan(QueryPlanNode node, int indent)
    {
        var lines = new List<string>();
        var prefix = new string(' ', indent * 2);

        switch (node)
        {
            case TableScanNode scan:
                lines.Add($"{prefix}TableScan: {scan.TableName} [{string.Join(", ", scan.Columns)}] (rows={scan.EstimatedRows:F0}, cost={scan.EstimatedCost:F2})");
                break;
            case IndexScanNode idx:
                lines.Add($"{prefix}IndexScan: {idx.TableName}.{idx.IndexName} (rows={idx.EstimatedRows:F0}, cost={idx.EstimatedCost:F2})");
                break;
            case FilterNode filter:
                lines.Add($"{prefix}Filter: (rows={filter.EstimatedRows:F0}, cost={filter.EstimatedCost:F2})");
                lines.AddRange(FormatPlan(filter.Input, indent + 1));
                break;
            case ProjectNode project:
                lines.Add($"{prefix}Project: [{project.Columns.Length} columns] (rows={project.EstimatedRows:F0}, cost={project.EstimatedCost:F2})");
                lines.AddRange(FormatPlan(project.Input, indent + 1));
                break;
            case HashJoinNode hj:
                lines.Add($"{prefix}HashJoin: {hj.JoinType} (rows={hj.EstimatedRows:F0}, cost={hj.EstimatedCost:F2})");
                lines.AddRange(FormatPlan(hj.Left, indent + 1));
                lines.AddRange(FormatPlan(hj.Right, indent + 1));
                break;
            case NestedLoopJoinNode nlj:
                lines.Add($"{prefix}NestedLoopJoin: {nlj.JoinType} (rows={nlj.EstimatedRows:F0}, cost={nlj.EstimatedCost:F2})");
                lines.AddRange(FormatPlan(nlj.Left, indent + 1));
                lines.AddRange(FormatPlan(nlj.Right, indent + 1));
                break;
            case MergeJoinNode mj:
                lines.Add($"{prefix}MergeJoin: {mj.JoinType} (rows={mj.EstimatedRows:F0}, cost={mj.EstimatedCost:F2})");
                lines.AddRange(FormatPlan(mj.Left, indent + 1));
                lines.AddRange(FormatPlan(mj.Right, indent + 1));
                break;
            case SortNode sort:
                lines.Add($"{prefix}Sort: [{sort.OrderBy.Length} keys] (rows={sort.EstimatedRows:F0}, cost={sort.EstimatedCost:F2})");
                lines.AddRange(FormatPlan(sort.Input, indent + 1));
                break;
            case AggregateNode agg:
                lines.Add($"{prefix}Aggregate: [{agg.GroupByKeys.Length} keys, {agg.Aggregations.Length} aggs] (rows={agg.EstimatedRows:F0}, cost={agg.EstimatedCost:F2})");
                lines.AddRange(FormatPlan(agg.Input, indent + 1));
                break;
            case LimitNode limit:
                lines.Add($"{prefix}Limit: {limit.Limit} offset={limit.Offset} (rows={limit.EstimatedRows:F0}, cost={limit.EstimatedCost:F2})");
                lines.AddRange(FormatPlan(limit.Input, indent + 1));
                break;
            case UnionNode union:
                lines.Add($"{prefix}Union: all={union.All} (rows={union.EstimatedRows:F0}, cost={union.EstimatedCost:F2})");
                foreach (var input in union.Inputs)
                    lines.AddRange(FormatPlan(input, indent + 1));
                break;
            default:
                lines.Add($"{prefix}{node.GetType().Name}: (rows={node.EstimatedRows:F0}, cost={node.EstimatedCost:F2})");
                break;
        }

        return lines;
    }

    // ─────────────────────────────────────────────────────────────
    // Physical Plan Execution
    // ─────────────────────────────────────────────────────────────

    private async IAsyncEnumerable<ColumnarBatch> ExecutePlanAsync(
        QueryPlanNode node,
        [EnumeratorCancellation] CancellationToken ct)
    {
        switch (node)
        {
            case TableScanNode scan:
                await foreach (var batch in ExecuteTableScan(scan, ct))
                    yield return batch;
                break;

            case FilterNode filter:
                await foreach (var batch in ExecuteFilter(filter, ct))
                    yield return batch;
                break;

            case ProjectNode project:
                await foreach (var batch in ExecuteProject(project, ct))
                    yield return batch;
                break;

            case HashJoinNode hashJoin:
                await foreach (var batch in ExecuteHashJoin(hashJoin, ct))
                    yield return batch;
                break;

            case NestedLoopJoinNode nestedLoop:
                await foreach (var batch in ExecuteNestedLoopJoin(nestedLoop, ct))
                    yield return batch;
                break;

            case SortNode sort:
                await foreach (var batch in ExecuteSort(sort, ct))
                    yield return batch;
                break;

            case AggregateNode aggregate:
                await foreach (var batch in ExecuteAggregate(aggregate, ct))
                    yield return batch;
                break;

            case LimitNode limit:
                await foreach (var batch in ExecuteLimit(limit, ct))
                    yield return batch;
                break;

            default:
                throw new NotSupportedException($"Unsupported plan node type: {node.GetType().Name}");
        }
    }

    // ─────── TableScan ───────

    private IAsyncEnumerable<ColumnarBatch> ExecuteTableScan(TableScanNode scan, CancellationToken ct)
    {
        var columns = scan.Columns.IsDefault ? null : scan.Columns.ToList();
        return _dataSource.GetTableData(scan.TableName, columns, ct);
    }

    // ─────── Filter ───────

    private async IAsyncEnumerable<ColumnarBatch> ExecuteFilter(
        FilterNode filter,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var batch in ExecutePlanAsync(filter.Input, ct))
        {
            ct.ThrowIfCancellationRequested();
            var predicate = CompileFilterPredicate(filter.Predicate, batch);
            var filtered = ColumnarEngine.FilterBatch(batch, predicate);
            if (filtered.RowCount > 0)
                yield return filtered;
        }
    }

    // ─────── Project ───────

    private async IAsyncEnumerable<ColumnarBatch> ExecuteProject(
        ProjectNode project,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var batch in ExecutePlanAsync(project.Input, ct))
        {
            ct.ThrowIfCancellationRequested();

            // Collect column names from select columns
            var columnNames = new List<string>();
            foreach (var col in project.Columns)
            {
                if (col.Expression is ColumnReference colRef)
                {
                    columnNames.Add(colRef.Column);
                }
                else if (col.Expression is WildcardExpression)
                {
                    // Wildcard: include all columns
                    foreach (var c in batch.Columns)
                        columnNames.Add(c.Name);
                }
                else
                {
                    // For complex expressions, use alias or generate name
                    var name = col.Alias ?? $"expr_{columnNames.Count}";
                    // If the column exists in batch, use it
                    if (batch.GetColumn(name) != null)
                        columnNames.Add(name);
                }
            }

            if (columnNames.Count > 0)
            {
                // Filter to only columns that exist in this batch
                var available = columnNames.Where(n => batch.GetColumn(n) != null).ToList();
                if (available.Count > 0)
                    yield return ColumnarEngine.ScanBatch(batch, available);
            }
        }
    }

    // ─────── Hash Join ───────

    private async IAsyncEnumerable<ColumnarBatch> ExecuteHashJoin(
        HashJoinNode join,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Phase 1: Build hash table from left (build) side
        var buildBatches = new List<ColumnarBatch>();
        long estimatedBytes = 0;

        await foreach (var batch in ExecutePlanAsync(join.Left, ct))
        {
            ct.ThrowIfCancellationRequested();
            buildBatches.Add(batch);
            estimatedBytes += EstimateBatchSize(batch);
        }

        // Build hash table: key -> list of (batchIndex, rowIndex)
        var leftKeyExpr = join.LeftKey;
        var hashTable = new Dictionary<string, List<(int BatchIdx, int RowIdx)>>();

        for (int bIdx = 0; bIdx < buildBatches.Count; bIdx++)
        {
            var batch = buildBatches[bIdx];
            for (int row = 0; row < batch.RowCount; row++)
            {
                var key = EvaluateExpressionAsString(leftKeyExpr, batch, row);
                if (!hashTable.TryGetValue(key, out var list))
                {
                    list = new List<(int, int)>();
                    hashTable[key] = list;
                }
                list.Add((bIdx, row));
            }
        }

        // Phase 2: Probe with right side
        var rightKeyExpr = join.RightKey;
        var isLeftJoin = join.JoinType is Query.JoinType.Left or Query.JoinType.Full;
        var isRightJoin = join.JoinType is Query.JoinType.Right or Query.JoinType.Full;
        var matchedBuild = isLeftJoin ? new HashSet<(int, int)>() : null;

        await foreach (var rightBatch in ExecutePlanAsync(join.Right, ct))
        {
            ct.ThrowIfCancellationRequested();

            var resultBuilder = new ColumnarBatchBuilder(rightBatch.RowCount * 2);

            // Add all columns from left and right
            var leftSchema = buildBatches.Count > 0 ? buildBatches[0] : null;
            int colIdx = 0;

            if (leftSchema != null)
            {
                foreach (var col in leftSchema.Columns)
                    resultBuilder.AddColumn(col.Name, col.DataType);
                colIdx = leftSchema.ColumnCount;
            }
            foreach (var col in rightBatch.Columns)
                resultBuilder.AddColumn(col.Name, col.DataType);

            int resultRow = 0;

            for (int rRow = 0; rRow < rightBatch.RowCount; rRow++)
            {
                var key = EvaluateExpressionAsString(rightKeyExpr, rightBatch, rRow);
                bool matched = false;

                if (hashTable.TryGetValue(key, out var matches))
                {
                    foreach (var (bIdx, lRow) in matches)
                    {
                        if (resultRow >= ColumnarBatch.DefaultBatchSize) break;

                        var leftBatch = buildBatches[bIdx];
                        // Copy left columns
                        for (int c = 0; c < leftBatch.ColumnCount; c++)
                            resultBuilder.SetValue(c, resultRow, leftBatch.Columns[c].GetValue(lRow));
                        // Copy right columns
                        for (int c = 0; c < rightBatch.ColumnCount; c++)
                            resultBuilder.SetValue(colIdx + c, resultRow, rightBatch.Columns[c].GetValue(rRow));

                        matchedBuild?.Add((bIdx, lRow));
                        resultRow++;
                        matched = true;
                    }
                }

                // Right/Full outer join: emit right row with nulls on left
                if (!matched && isRightJoin)
                {
                    if (resultRow < ColumnarBatch.DefaultBatchSize)
                    {
                        for (int c = 0; c < rightBatch.ColumnCount; c++)
                            resultBuilder.SetValue(colIdx + c, resultRow, rightBatch.Columns[c].GetValue(rRow));
                        resultRow++;
                    }
                }
            }

            if (resultRow > 0)
                yield return resultBuilder.Build();
        }

        // Left/Full outer join: emit unmatched build rows
        if (isLeftJoin && matchedBuild != null)
        {
            var leftSchema = buildBatches.Count > 0 ? buildBatches[0] : null;
            if (leftSchema != null)
            {
                var builder = new ColumnarBatchBuilder();
                foreach (var col in leftSchema.Columns)
                    builder.AddColumn(col.Name, col.DataType);

                int resultRow = 0;
                for (int bIdx = 0; bIdx < buildBatches.Count; bIdx++)
                {
                    var batch = buildBatches[bIdx];
                    for (int row = 0; row < batch.RowCount; row++)
                    {
                        if (!matchedBuild.Contains((bIdx, row)))
                        {
                            for (int c = 0; c < batch.ColumnCount; c++)
                                builder.SetValue(c, resultRow, batch.Columns[c].GetValue(row));
                            resultRow++;
                        }
                    }
                }

                if (resultRow > 0)
                    yield return builder.Build();
            }
        }
    }

    // ─────── Nested Loop Join ───────

    private async IAsyncEnumerable<ColumnarBatch> ExecuteNestedLoopJoin(
        NestedLoopJoinNode join,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Materialize left side
        var leftBatches = new List<ColumnarBatch>();
        await foreach (var batch in ExecutePlanAsync(join.Left, ct))
        {
            ct.ThrowIfCancellationRequested();
            leftBatches.Add(batch);
        }

        // For each right batch, cross with all left rows and filter
        await foreach (var rightBatch in ExecutePlanAsync(join.Right, ct))
        {
            ct.ThrowIfCancellationRequested();

            var leftSchema = leftBatches.Count > 0 ? leftBatches[0] : null;
            if (leftSchema == null) yield break;

            var builder = new ColumnarBatchBuilder();
            int colIdx = 0;

            foreach (var col in leftSchema.Columns)
                builder.AddColumn(col.Name, col.DataType);
            colIdx = leftSchema.ColumnCount;

            foreach (var col in rightBatch.Columns)
                builder.AddColumn(col.Name, col.DataType);

            int resultRow = 0;

            foreach (var leftBatch in leftBatches)
            {
                for (int lRow = 0; lRow < leftBatch.RowCount; lRow++)
                {
                    for (int rRow = 0; rRow < rightBatch.RowCount; rRow++)
                    {
                        if (resultRow >= ColumnarBatch.DefaultBatchSize) break;

                        // Copy left
                        for (int c = 0; c < leftBatch.ColumnCount; c++)
                            builder.SetValue(c, resultRow, leftBatch.Columns[c].GetValue(lRow));
                        // Copy right
                        for (int c = 0; c < rightBatch.ColumnCount; c++)
                            builder.SetValue(colIdx + c, resultRow, rightBatch.Columns[c].GetValue(rRow));

                        resultRow++;
                    }
                }
            }

            if (resultRow > 0)
            {
                var combined = builder.Build();

                // Apply join condition if present
                if (join.Condition != null)
                {
                    var predicate = CompileFilterPredicate(join.Condition, combined);
                    combined = ColumnarEngine.FilterBatch(combined, predicate);
                }

                if (combined.RowCount > 0)
                    yield return combined;
            }
        }
    }

    // ─────── Sort ───────

    private async IAsyncEnumerable<ColumnarBatch> ExecuteSort(
        SortNode sort,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Collect all batches into memory (external sort for > 512MB not implemented yet)
        var allBatches = new List<ColumnarBatch>();
        await foreach (var batch in ExecutePlanAsync(sort.Input, ct))
        {
            ct.ThrowIfCancellationRequested();
            allBatches.Add(batch);
        }

        if (allBatches.Count == 0) yield break;

        // Merge all batches into one for sorting
        var merged = MergeBatches(allBatches);
        if (merged.RowCount == 0) yield break;

        // Build row indices and sort
        var indices = Enumerable.Range(0, merged.RowCount).ToArray();
        Array.Sort(indices, (a, b) =>
        {
            foreach (var orderBy in sort.OrderBy)
            {
                var aVal = EvaluateExpression(orderBy.Expression, merged, a);
                var bVal = EvaluateExpression(orderBy.Expression, merged, b);
                var cmp = CompareValues(aVal, bVal);
                if (cmp != 0)
                    return orderBy.Ascending ? cmp : -cmp;
            }
            return 0;
        });

        // Build sorted batch in chunks
        for (int start = 0; start < indices.Length; start += ColumnarBatch.DefaultBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(ColumnarBatch.DefaultBatchSize, indices.Length - start);
            var builder = new ColumnarBatchBuilder(count);

            foreach (var col in merged.Columns)
                builder.AddColumn(col.Name, col.DataType);

            for (int i = 0; i < count; i++)
            {
                var srcRow = indices[start + i];
                for (int c = 0; c < merged.ColumnCount; c++)
                    builder.SetValue(c, i, merged.Columns[c].GetValue(srcRow));
            }

            yield return builder.Build();
        }
    }

    // ─────── Aggregate ───────

    private async IAsyncEnumerable<ColumnarBatch> ExecuteAggregate(
        AggregateNode agg,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Collect all input batches
        var allBatches = new List<ColumnarBatch>();
        await foreach (var batch in ExecutePlanAsync(agg.Input, ct))
        {
            ct.ThrowIfCancellationRequested();
            allBatches.Add(batch);
        }

        if (allBatches.Count == 0) yield break;

        // Merge into single batch for aggregation
        var merged = MergeBatches(allBatches);

        // Build group keys and aggregate specs for ColumnarEngine
        var groupKeys = new List<string>();
        foreach (var key in agg.GroupByKeys)
        {
            if (key is ColumnReference colRef)
                groupKeys.Add(colRef.Column);
        }

        var aggSpecs = new List<AggregateSpec>();
        foreach (var aggFunc in agg.Aggregations)
        {
            var funcType = aggFunc.FunctionName.ToUpperInvariant() switch
            {
                "SUM" => AggregateFunctionType.Sum,
                "COUNT" => AggregateFunctionType.Count,
                "MIN" => AggregateFunctionType.Min,
                "MAX" => AggregateFunctionType.Max,
                "AVG" => AggregateFunctionType.Avg,
                _ => AggregateFunctionType.Count
            };

            string? colName = null;
            if (aggFunc.Argument is ColumnReference cr)
                colName = cr.Column;
            else if (aggFunc.Argument is WildcardExpression)
                colName = null; // COUNT(*)

            aggSpecs.Add(new AggregateSpec(colName, funcType, aggFunc.Alias ?? $"{aggFunc.FunctionName}_{colName ?? "star"}"));
        }

        var result = ColumnarEngine.GroupByAggregate(merged, groupKeys, aggSpecs);
        if (result.RowCount > 0)
            yield return result;
    }

    // ─────── Limit ───────

    private async IAsyncEnumerable<ColumnarBatch> ExecuteLimit(
        LimitNode limit,
        [EnumeratorCancellation] CancellationToken ct)
    {
        long rowsEmitted = 0;
        long rowsSkipped = 0;
        var targetOffset = limit.Offset;
        var targetLimit = limit.Limit;

        await foreach (var batch in ExecutePlanAsync(limit.Input, ct))
        {
            ct.ThrowIfCancellationRequested();

            if (rowsEmitted >= targetLimit)
                yield break;

            // Handle offset: skip rows
            if (rowsSkipped < targetOffset)
            {
                var toSkip = targetOffset - (int)rowsSkipped;
                if (toSkip >= batch.RowCount)
                {
                    rowsSkipped += batch.RowCount;
                    continue;
                }

                // Partial skip: filter out first 'toSkip' rows
                var remaining = batch.RowCount - (int)toSkip;
                var skipOffset = (int)toSkip;
                rowsSkipped += toSkip;

                var builder = new ColumnarBatchBuilder(remaining);
                foreach (var col in batch.Columns)
                    builder.AddColumn(col.Name, col.DataType);

                for (int i = 0; i < remaining; i++)
                {
                    for (int c = 0; c < batch.ColumnCount; c++)
                        builder.SetValue(c, i, batch.Columns[c].GetValue(skipOffset + i));
                }

                var trimmed = builder.Build();
                // Apply limit
                var canEmit = Math.Min(trimmed.RowCount, targetLimit - (int)rowsEmitted);
                if (canEmit < trimmed.RowCount)
                {
                    trimmed = TruncateBatch(trimmed, (int)canEmit);
                }
                rowsEmitted += trimmed.RowCount;
                yield return trimmed;
                continue;
            }

            // Apply limit
            var toEmit = Math.Min(batch.RowCount, targetLimit - (int)rowsEmitted);
            var outputBatch = toEmit < batch.RowCount
                ? TruncateBatch(batch, (int)toEmit)
                : batch;
            rowsEmitted += outputBatch.RowCount;
            yield return outputBatch;

            if (rowsEmitted >= targetLimit)
                yield break;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Expression Evaluation
    // ─────────────────────────────────────────────────────────────

    private Func<int, bool> CompileFilterPredicate(Expression expr, ColumnarBatch batch)
    {
        return row => EvaluateExpressionAsBool(expr, batch, row);
    }

    private bool EvaluateExpressionAsBool(Expression expr, ColumnarBatch batch, int row)
    {
        var result = EvaluateExpression(expr, batch, row);
        if (result is bool b) return b;
        if (result is null) return false;
        return Convert.ToBoolean(result);
    }

    private object? EvaluateExpression(Expression expr, ColumnarBatch batch, int row)
    {
        switch (expr)
        {
            case LiteralExpression lit:
                return lit.Value;

            case ColumnReference colRef:
                var col = batch.GetColumn(colRef.Column);
                if (col == null && colRef.Table != null)
                    col = batch.GetColumn($"{colRef.Table}.{colRef.Column}");
                return col?.GetValue(row);

            case BinaryExpression bin:
                var left = EvaluateExpression(bin.Left, batch, row);
                var right = EvaluateExpression(bin.Right, batch, row);
                return EvaluateBinaryOp(bin.Operator, left, right);

            case UnaryExpression unary:
                var operand = EvaluateExpression(unary.Operand, batch, row);
                return unary.Operator switch
                {
                    UnaryOperator.Not => operand is bool bv ? !bv : null,
                    UnaryOperator.Negate => operand switch
                    {
                        int i => -i,
                        long l => -l,
                        double d => -d,
                        decimal dc => -dc,
                        _ => null
                    },
                    _ => null
                };

            case IsNullExpression isNull:
                var val = EvaluateExpression(isNull.Operand, batch, row);
                return isNull.Negated ? val != null : val == null;

            case InExpression inExpr:
                var testVal = EvaluateExpression(inExpr.Operand, batch, row);
                if (inExpr.Values.HasValue)
                {
                    var contains = inExpr.Values.Value.Any(v =>
                        Equals(EvaluateExpression(v, batch, row)?.ToString(), testVal?.ToString()));
                    return inExpr.Negated ? !contains : contains;
                }
                return !inExpr.Negated;

            case BetweenExpression between:
                var testBetween = EvaluateExpression(between.Operand, batch, row);
                var lo = EvaluateExpression(between.Low, batch, row);
                var hi = EvaluateExpression(between.High, batch, row);
                var inRange = CompareValues(testBetween, lo) >= 0 && CompareValues(testBetween, hi) <= 0;
                return between.Negated ? !inRange : inRange;

            case LikeExpression like:
                var likeVal = EvaluateExpression(like.Operand, batch, row)?.ToString();
                var pattern = EvaluateExpression(like.Pattern, batch, row)?.ToString();
                if (likeVal == null || pattern == null) return false;
                var regex = "^" + Regex.Escape(pattern).Replace("%", ".*").Replace("_", ".") + "$";
                var match = Regex.IsMatch(likeVal, regex, RegexOptions.IgnoreCase);
                return like.Negated ? !match : match;

            case FunctionCallExpression func:
                return EvaluateFunctionCall(func, batch, row);

            case ParenthesizedExpression paren:
                return EvaluateExpression(paren.Inner, batch, row);

            case CaseExpression caseExpr:
                return EvaluateCaseExpression(caseExpr, batch, row);

            case CastExpression cast:
                var castVal = EvaluateExpression(cast.Operand, batch, row);
                return CastValue(castVal, cast.TargetType);

            default:
                return null;
        }
    }

    private object? EvaluateBinaryOp(BinaryOperator op, object? left, object? right)
    {
        return op switch
        {
            BinaryOperator.And => (left is bool lb1 && right is bool rb1) ? lb1 && rb1 : (object?)false,
            BinaryOperator.Or => (left is bool lb2 && right is bool rb2) ? lb2 || rb2 : (object?)false,
            BinaryOperator.Equals => Equals(left?.ToString(), right?.ToString()),
            BinaryOperator.NotEquals => !Equals(left?.ToString(), right?.ToString()),
            BinaryOperator.LessThan => CompareValues(left, right) < 0,
            BinaryOperator.GreaterThan => CompareValues(left, right) > 0,
            BinaryOperator.LessOrEqual => CompareValues(left, right) <= 0,
            BinaryOperator.GreaterOrEqual => CompareValues(left, right) >= 0,
            BinaryOperator.Plus => ArithmeticOp(left, right, (a, b) => a + b),
            BinaryOperator.Minus => ArithmeticOp(left, right, (a, b) => a - b),
            BinaryOperator.Multiply => ArithmeticOp(left, right, (a, b) => a * b),
            BinaryOperator.Divide => ArithmeticOp(left, right, (a, b) => b != 0 ? a / b : double.NaN),
            BinaryOperator.Modulo => ArithmeticOp(left, right, (a, b) => b != 0 ? a % b : double.NaN),
            _ => null
        };
    }

    private static object? ArithmeticOp(object? left, object? right, Func<double, double, double> op)
    {
        if (left == null || right == null) return null;
        if (double.TryParse(left.ToString(), out var a) && double.TryParse(right.ToString(), out var b))
            return op(a, b);
        return null;
    }

    private object? EvaluateFunctionCall(FunctionCallExpression func, ColumnarBatch batch, int row)
    {
        var name = func.FunctionName.ToUpperInvariant();
        switch (name)
        {
            case "UPPER":
                return EvaluateExpression(func.Arguments[0], batch, row)?.ToString()?.ToUpperInvariant();
            case "LOWER":
                return EvaluateExpression(func.Arguments[0], batch, row)?.ToString()?.ToLowerInvariant();
            case "LENGTH" or "LEN":
                return EvaluateExpression(func.Arguments[0], batch, row)?.ToString()?.Length;
            case "COALESCE":
                foreach (var arg in func.Arguments)
                {
                    var v = EvaluateExpression(arg, batch, row);
                    if (v != null) return v;
                }
                return null;
            case "ABS":
                var absVal = EvaluateExpression(func.Arguments[0], batch, row);
                return absVal switch
                {
                    int i => Math.Abs(i),
                    long l => Math.Abs(l),
                    double d => Math.Abs(d),
                    decimal dc => Math.Abs(dc),
                    _ => null
                };
            // Tag functions are resolved before execution, but handle them here as fallback
            case "TAG":
                return EvaluateTagFunction(func, batch, row);
            case "HAS_TAG":
                return EvaluateHasTagFunction(func, batch, row);
            case "TAG_COUNT":
                return EvaluateTagCountFunction(func, batch, row);
            default:
                return null;
        }
    }

    private object? EvaluateTagFunction(FunctionCallExpression func, ColumnarBatch batch, int row)
    {
        if (_tagProvider == null || func.Arguments.Length < 1) return null;

        if (func.Arguments.Length >= 2)
        {
            // Two arguments: tag(objectKey, tagName)
            var objectKey = EvaluateExpression(func.Arguments[0], batch, row)?.ToString();
            var tagName = EvaluateExpression(func.Arguments[1], batch, row)?.ToString();
            if (objectKey != null && tagName != null)
                return _tagProvider.GetTag(objectKey, tagName);
        }
        else
        {
            // Single argument: tag(tagName) — use first column as implicit object key
            var tagName = EvaluateExpression(func.Arguments[0], batch, row)?.ToString();
            if (tagName == null) return null;
            var objectKey = batch.ColumnCount > 0 ? batch.GetValue(0, row)?.ToString() : null;
            if (objectKey != null)
                return _tagProvider.GetTag(objectKey, tagName);
        }

        return null;
    }

    private object? EvaluateHasTagFunction(FunctionCallExpression func, ColumnarBatch batch, int row)
    {
        if (_tagProvider == null || func.Arguments.Length < 1) return false;

        if (func.Arguments.Length >= 2)
        {
            // Two arguments: has_tag(objectKey, tagName)
            var objectKey = EvaluateExpression(func.Arguments[0], batch, row)?.ToString();
            var tagName = EvaluateExpression(func.Arguments[1], batch, row)?.ToString();
            if (objectKey != null && tagName != null)
                return _tagProvider.HasTag(objectKey, tagName);
        }
        else
        {
            // Single argument: has_tag(tagName) — use first column as implicit object key
            var tagName = EvaluateExpression(func.Arguments[0], batch, row)?.ToString();
            if (tagName == null) return false;
            var objectKey = batch.ColumnCount > 0 ? batch.GetValue(0, row)?.ToString() : null;
            if (objectKey != null)
                return _tagProvider.HasTag(objectKey, tagName);
        }

        return false;
    }

    private object? EvaluateTagCountFunction(FunctionCallExpression func, ColumnarBatch batch, int row)
    {
        if (_tagProvider == null || func.Arguments.Length < 1) return 0;
        var objectKey = EvaluateExpression(func.Arguments[0], batch, row)?.ToString();
        if (objectKey == null) return 0;
        return _tagProvider.GetTagCount(objectKey);
    }

    private object? EvaluateCaseExpression(CaseExpression caseExpr, ColumnarBatch batch, int row)
    {
        if (caseExpr.Operand != null)
        {
            // Simple CASE: CASE expr WHEN val THEN result
            var testVal = EvaluateExpression(caseExpr.Operand, batch, row);
            foreach (var when in caseExpr.WhenClauses)
            {
                var whenVal = EvaluateExpression(when.Condition, batch, row);
                if (Equals(testVal?.ToString(), whenVal?.ToString()))
                    return EvaluateExpression(when.Result, batch, row);
            }
        }
        else
        {
            // Searched CASE: CASE WHEN condition THEN result
            foreach (var when in caseExpr.WhenClauses)
            {
                if (EvaluateExpressionAsBool(when.Condition, batch, row))
                    return EvaluateExpression(when.Result, batch, row);
            }
        }

        return caseExpr.ElseExpression != null
            ? EvaluateExpression(caseExpr.ElseExpression, batch, row)
            : null;
    }

    private static object? CastValue(object? value, string targetType)
    {
        if (value == null) return null;
        var type = targetType.ToUpperInvariant();
        try
        {
            return type switch
            {
                "INT" or "INTEGER" => Convert.ToInt32(value),
                "BIGINT" or "LONG" => Convert.ToInt64(value),
                "FLOAT" or "REAL" => Convert.ToSingle(value),
                "DOUBLE" => Convert.ToDouble(value),
                "DECIMAL" or "NUMERIC" => Convert.ToDecimal(value),
                "VARCHAR" or "TEXT" or "STRING" or "NVARCHAR" => value.ToString(),
                "BOOLEAN" or "BOOL" => Convert.ToBoolean(value),
                _ => value
            };
        }
        catch (FormatException) { return null; }
        catch (OverflowException) { return null; }
        catch (InvalidCastException) { return null; }
    }

    private string EvaluateExpressionAsString(Expression expr, ColumnarBatch batch, int row)
    {
        return EvaluateExpression(expr, batch, row)?.ToString() ?? "";
    }

    private static int CompareValues(object? a, object? b)
    {
        if (a == null && b == null) return 0;
        if (a == null) return -1;
        if (b == null) return 1;

        if (double.TryParse(a.ToString(), out var aNum) && double.TryParse(b.ToString(), out var bNum))
            return aNum.CompareTo(bNum);

        return string.Compare(a.ToString(), b.ToString(), StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────
    // Helper Methods
    // ─────────────────────────────────────────────────────────────

    private static IReadOnlyList<ColumnInfo> InferSchemaFromPlan(QueryPlanNode node)
    {
        return node switch
        {
            ProjectNode project => project.Columns.Select(c =>
            {
                var name = c.Alias ?? (c.Expression is ColumnReference cr ? cr.Column : "expr");
                return new ColumnInfo(name, ColumnDataType.String);
            }).ToList(),

            TableScanNode scan => scan.Columns.Select(c =>
                new ColumnInfo(c, ColumnDataType.String)).ToList(),

            AggregateNode agg =>
                agg.GroupByKeys.Select(k => k is ColumnReference cr
                    ? new ColumnInfo(cr.Column, ColumnDataType.String)
                    : new ColumnInfo("key", ColumnDataType.String))
                .Concat(agg.Aggregations.Select(a =>
                    new ColumnInfo(a.Alias ?? a.FunctionName, ColumnDataType.Float64)))
                .ToList(),

            FilterNode filter => InferSchemaFromPlan(filter.Input),
            SortNode sort => InferSchemaFromPlan(sort.Input),
            LimitNode limit => InferSchemaFromPlan(limit.Input),
            HashJoinNode hj => InferSchemaFromPlan(hj.Left).Concat(InferSchemaFromPlan(hj.Right)).ToList(),
            NestedLoopJoinNode nlj => InferSchemaFromPlan(nlj.Left).Concat(InferSchemaFromPlan(nlj.Right)).ToList(),

            _ => Array.Empty<ColumnInfo>()
        };
    }

    private static ColumnarBatch MergeBatches(List<ColumnarBatch> batches)
    {
        if (batches.Count == 0)
            return new ColumnarBatch(0, Array.Empty<ColumnVector>());
        if (batches.Count == 1)
            return batches[0];

        var totalRows = batches.Sum(b => b.RowCount);
        var template = batches[0];
        var builder = new ColumnarBatchBuilder(totalRows);

        foreach (var col in template.Columns)
            builder.AddColumn(col.Name, col.DataType);

        int row = 0;
        foreach (var batch in batches)
        {
            for (int r = 0; r < batch.RowCount; r++)
            {
                for (int c = 0; c < batch.ColumnCount; c++)
                    builder.SetValue(c, row, batch.Columns[c].GetValue(r));
                row++;
            }
        }

        return builder.Build();
    }

    private static ColumnarBatch TruncateBatch(ColumnarBatch batch, int maxRows)
    {
        if (batch.RowCount <= maxRows) return batch;

        var builder = new ColumnarBatchBuilder(maxRows);
        foreach (var col in batch.Columns)
            builder.AddColumn(col.Name, col.DataType);

        for (int r = 0; r < maxRows; r++)
        {
            for (int c = 0; c < batch.ColumnCount; c++)
                builder.SetValue(c, r, batch.Columns[c].GetValue(r));
        }

        return builder.Build();
    }

    private static long EstimateBatchSize(ColumnarBatch batch)
    {
        long size = 0;
        foreach (var col in batch.Columns)
        {
            size += col.DataType switch
            {
                ColumnDataType.Int32 => batch.RowCount * 4L,
                ColumnDataType.Int64 => batch.RowCount * 8L,
                ColumnDataType.Float64 => batch.RowCount * 8L,
                ColumnDataType.Decimal => batch.RowCount * 16L,
                ColumnDataType.DateTime => batch.RowCount * 8L,
                ColumnDataType.Bool => batch.RowCount,
                ColumnDataType.String => batch.RowCount * 64L, // estimate
                ColumnDataType.Binary => batch.RowCount * 128L, // estimate
                _ => batch.RowCount * 8L
            };
        }
        return size;
    }

    private static async IAsyncEnumerable<ColumnarBatch> SingleBatchAsync(ColumnarBatch batch)
    {
        await Task.CompletedTask;
        yield return batch;
    }

    // ─────────────────────────────────────────────────────────────
    // Statistics Adapter
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Adapts IDataSourceProvider.GetTableStatistics to ITableStatisticsProvider.
    /// </summary>
    private sealed class DataSourceStatisticsProvider : ITableStatisticsProvider
    {
        private readonly IDataSourceProvider _source;

        public DataSourceStatisticsProvider(IDataSourceProvider source) => _source = source;

        public TableStatistics? GetStatistics(string tableName) => _source.GetTableStatistics(tableName);
    }
}
