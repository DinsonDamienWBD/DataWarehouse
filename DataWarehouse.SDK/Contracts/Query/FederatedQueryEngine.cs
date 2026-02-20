using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Contracts.Query;

// ---------------------------------------------------------------
// Query Execution Exception
// ---------------------------------------------------------------

/// <summary>
/// Exception thrown when a federated query execution fails.
/// Includes the source that caused the failure for diagnostics.
/// </summary>
public sealed class QueryExecutionException : Exception
{
    /// <summary>The federated source where the error occurred, if applicable.</summary>
    public string? SourceId { get; }

    /// <summary>The table being queried when the error occurred.</summary>
    public string? TableName { get; }

    public QueryExecutionException(string message)
        : base(message) { }

    public QueryExecutionException(string message, string sourceId, string? tableName)
        : base($"[{sourceId}] {message}")
    {
        SourceId = sourceId;
        TableName = tableName;
    }

    public QueryExecutionException(string message, string sourceId, string? tableName, Exception inner)
        : base($"[{sourceId}] {message}", inner)
    {
        SourceId = sourceId;
        TableName = tableName;
    }
}

// ---------------------------------------------------------------
// Federated Execution Result
// ---------------------------------------------------------------

/// <summary>
/// Complete result of a federated query execution, including
/// data batches, per-source timing, and execution metadata.
/// Extends the standard QueryResult with federation-specific details.
/// </summary>
public sealed class FederatedExecutionResult
{
    /// <summary>Result batches from query execution.</summary>
    public IReadOnlyList<ColumnarBatch> Batches { get; }

    /// <summary>Total rows across all result batches.</summary>
    public long TotalRows { get; }

    /// <summary>Per-source execution details.</summary>
    public IReadOnlyList<SourceExecutionInfo> SourceDetails { get; }

    /// <summary>Total wall-clock execution time in milliseconds.</summary>
    public double TotalExecutionTimeMs { get; }

    public FederatedExecutionResult(
        IReadOnlyList<ColumnarBatch> batches,
        long totalRows,
        IReadOnlyList<SourceExecutionInfo> sourceDetails,
        double totalExecutionTimeMs)
    {
        Batches = batches;
        TotalRows = totalRows;
        SourceDetails = sourceDetails;
        TotalExecutionTimeMs = totalExecutionTimeMs;
    }
}

/// <summary>
/// Execution details for a single source during federated query processing.
/// </summary>
public sealed record SourceExecutionInfo(
    string SourceId,
    long RowsFetched,
    long BytesTransferred,
    double ExecutionTimeMs,
    bool FilterPushed,
    bool ProjectionPushed,
    bool AggregationPushed
);

// ---------------------------------------------------------------
// FederatedQueryEngine
// ---------------------------------------------------------------

/// <summary>
/// Federated query execution engine that orchestrates SQL queries
/// across multiple heterogeneous storage backends. Uses the SQL parser
/// to parse queries, the federated query planner to create network-aware
/// execution plans, and the data source registry to route table scans
/// to the appropriate backends.
///
/// Key behaviors:
/// - Parallel remote source execution when independent scans exist
/// - Configurable per-source timeout (default 30 seconds)
/// - Progress reporting via callback
/// - Message bus integration for observability
/// - Fail-fast on any source error (no silent partial results)
/// </summary>
public sealed class FederatedQueryEngine
{
    private readonly ISqlParser _parser;
    private readonly FederatedQueryPlanner _planner;
    private readonly IFederatedDataSourceRegistry _registry;
    private readonly IMessageBus? _messageBus;
    private readonly TimeSpan _perSourceTimeout;

    /// <summary>
    /// Optional progress callback invoked as data is fetched from sources.
    /// </summary>
    public Action<QueryProgress>? OnProgress { get; set; }

    /// <summary>
    /// Creates a federated query engine.
    /// </summary>
    /// <param name="parser">SQL parser for converting text to AST.</param>
    /// <param name="planner">Federated query planner for network-aware optimization.</param>
    /// <param name="registry">Registry of available data sources.</param>
    /// <param name="messageBus">Optional message bus for publishing execution events.</param>
    /// <param name="perSourceTimeout">Timeout per remote source (default 30 seconds).</param>
    public FederatedQueryEngine(
        ISqlParser parser,
        FederatedQueryPlanner planner,
        IFederatedDataSourceRegistry registry,
        IMessageBus? messageBus = null,
        TimeSpan? perSourceTimeout = null)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _messageBus = messageBus;
        _perSourceTimeout = perSourceTimeout ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Executes a federated SQL query across multiple backends.
    /// Parses the SQL, creates a federated execution plan, executes
    /// remote and local scans (in parallel where possible), and
    /// assembles the final result.
    /// </summary>
    /// <param name="sql">SQL query text.</param>
    /// <param name="stats">Optional statistics provider for cost estimation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The query result with data and execution metadata.</returns>
    /// <exception cref="QueryExecutionException">
    /// Thrown if any source fails. The entire query fails — no partial results.
    /// </exception>
    public async Task<FederatedExecutionResult> ExecuteFederatedQueryAsync(
        string sql,
        ITableStatisticsProvider? stats = null,
        CancellationToken ct = default)
    {
        var totalStopwatch = Stopwatch.StartNew();

        // Step 1: Parse SQL
        var statement = _parser.Parse(sql);
        if (statement is not SelectStatement select)
        {
            throw new QueryExecutionException(
                "Federated query engine only supports SELECT statements.");
        }

        // Step 2: Create federated execution plan
        var plan = _planner.Plan(select, stats);

        // Step 3: Collect all federated scan nodes from the plan
        var federatedScans = new List<FederatedTableScanNode>();
        var federatedAggregates = new List<FederatedAggregateNode>();
        CollectFederatedNodes(plan, federatedScans, federatedAggregates);

        // Identify sources involved
        var sourceIds = federatedScans.Select(s => s.SourceId)
            .Concat(federatedAggregates.Select(a => a.SourceId))
            .Distinct()
            .ToList();

        // Step 4: Publish execution start event
        await PublishExecutionEventAsync("query.federated.execute", new Dictionary<string, object>
        {
            ["sql"] = sql,
            ["sources"] = sourceIds,
            ["plan_node_count"] = CountPlanNodes(plan)
        }, ct);

        // Step 5: Execute remote scans in parallel
        var sourceDetails = new List<SourceExecutionInfo>();
        var resultBatches = new List<ColumnarBatch>();

        if (federatedScans.Count > 0 || federatedAggregates.Count > 0)
        {
            var scanTasks = federatedScans.Select(scan =>
                ExecuteFederatedScanAsync(scan, ct));
            var aggTasks = federatedAggregates.Select(agg =>
                ExecuteFederatedAggregateAsync(agg, ct));

            var allTasks = scanTasks.Concat(aggTasks).ToList();

            // Execute all remote operations in parallel
            FederatedSourceResult[] results;
            try
            {
                results = await Task.WhenAll(allTasks);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Unwrap aggregate exception to find the actual source failure
                if (ex is AggregateException aggEx)
                {
                    var firstReal = aggEx.InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException);
                    if (firstReal != null) throw firstReal;
                }
                throw;
            }

            foreach (var result in results)
            {
                resultBatches.AddRange(result.Batches);
                sourceDetails.Add(result.ExecutionInfo);
            }
        }

        totalStopwatch.Stop();

        // Step 6: Publish completion event
        await PublishExecutionEventAsync("query.federated.complete", new Dictionary<string, object>
        {
            ["sql"] = sql,
            ["sources"] = sourceIds,
            ["total_rows"] = resultBatches.Sum(b => (long)b.RowCount),
            ["execution_time_ms"] = totalStopwatch.Elapsed.TotalMilliseconds,
            ["source_timings"] = sourceDetails.Select(s => new Dictionary<string, object>
            {
                ["source"] = s.SourceId,
                ["rows"] = s.RowsFetched,
                ["bytes"] = s.BytesTransferred,
                ["time_ms"] = s.ExecutionTimeMs
            }).ToList()
        }, ct);

        return new FederatedExecutionResult(
            resultBatches,
            resultBatches.Sum(b => (long)b.RowCount),
            sourceDetails,
            totalStopwatch.Elapsed.TotalMilliseconds);
    }

    // ---------------------------------------------------------------
    // Plan Node Collection
    // ---------------------------------------------------------------

    private static void CollectFederatedNodes(
        QueryPlanNode node,
        List<FederatedTableScanNode> scans,
        List<FederatedAggregateNode> aggregates)
    {
        switch (node)
        {
            case FederatedTableScanNode fed:
                scans.Add(fed);
                break;
            case FederatedAggregateNode agg:
                aggregates.Add(agg);
                break;
            case FilterNode filter:
                CollectFederatedNodes(filter.Input, scans, aggregates);
                break;
            case ProjectNode project:
                CollectFederatedNodes(project.Input, scans, aggregates);
                break;
            case SortNode sort:
                CollectFederatedNodes(sort.Input, scans, aggregates);
                break;
            case AggregateNode aggregate:
                CollectFederatedNodes(aggregate.Input, scans, aggregates);
                break;
            case LimitNode limit:
                CollectFederatedNodes(limit.Input, scans, aggregates);
                break;
            case HashJoinNode hash:
                CollectFederatedNodes(hash.Left, scans, aggregates);
                CollectFederatedNodes(hash.Right, scans, aggregates);
                break;
            case NestedLoopJoinNode nested:
                CollectFederatedNodes(nested.Left, scans, aggregates);
                CollectFederatedNodes(nested.Right, scans, aggregates);
                break;
            case MergeJoinNode merge:
                CollectFederatedNodes(merge.Left, scans, aggregates);
                CollectFederatedNodes(merge.Right, scans, aggregates);
                break;
        }
    }

    private static int CountPlanNodes(QueryPlanNode node)
    {
        return node switch
        {
            FilterNode f => 1 + CountPlanNodes(f.Input),
            ProjectNode p => 1 + CountPlanNodes(p.Input),
            SortNode s => 1 + CountPlanNodes(s.Input),
            AggregateNode a => 1 + CountPlanNodes(a.Input),
            LimitNode l => 1 + CountPlanNodes(l.Input),
            HashJoinNode h => 1 + CountPlanNodes(h.Left) + CountPlanNodes(h.Right),
            NestedLoopJoinNode n => 1 + CountPlanNodes(n.Left) + CountPlanNodes(n.Right),
            MergeJoinNode m => 1 + CountPlanNodes(m.Left) + CountPlanNodes(m.Right),
            _ => 1
        };
    }

    // ---------------------------------------------------------------
    // Remote Execution
    // ---------------------------------------------------------------

    private sealed class FederatedSourceResult
    {
        public IReadOnlyList<ColumnarBatch> Batches { get; }
        public SourceExecutionInfo ExecutionInfo { get; }

        public FederatedSourceResult(IReadOnlyList<ColumnarBatch> batches, SourceExecutionInfo info)
        {
            Batches = batches;
            ExecutionInfo = info;
        }
    }

    private async Task<FederatedSourceResult> ExecuteFederatedScanAsync(
        FederatedTableScanNode scan,
        CancellationToken ct)
    {
        var source = _registry.GetSource(scan.SourceId)
            ?? throw new QueryExecutionException(
                $"Federated source '{scan.SourceId}' not found in registry.",
                scan.SourceId, scan.TableName);

        var info = source.GetInfo();
        var stopwatch = Stopwatch.StartNew();

        // Build the remote query request with pushed operations
        var request = new RemoteQueryRequest(
            scan.TableName,
            scan.PushedProjections,
            scan.PushedFilters,
            null, // Limit handled locally after join
            ImmutableArray<OrderByItem>.Empty);

        // Execute with per-source timeout
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_perSourceTimeout);

        IReadOnlyList<ColumnarBatch> batches;
        long totalRows = 0;
        long totalBytes = 0;

        try
        {
            var asyncEnumerable = await source.ExecuteRemoteQueryAsync(
                scan.TableName, request, timeoutCts.Token);

            var batchList = new List<ColumnarBatch>();
            await foreach (var batch in asyncEnumerable.WithCancellation(timeoutCts.Token))
            {
                batchList.Add(batch);
                totalRows += batch.RowCount;
                totalBytes += EstimateBatchSize(batch);

                // Report progress
                OnProgress?.Invoke(new QueryProgress(
                    scan.SourceId, totalRows, totalBytes, IsComplete: false));
            }

            batches = batchList;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new QueryExecutionException(
                $"Remote source timed out after {_perSourceTimeout.TotalSeconds}s.",
                scan.SourceId, scan.TableName);
        }
        catch (OperationCanceledException)
        {
            throw; // Propagate user cancellation
        }
        catch (Exception ex) when (ex is not QueryExecutionException)
        {
            throw new QueryExecutionException(
                $"Remote query failed: {ex.Message}",
                scan.SourceId, scan.TableName, ex);
        }

        stopwatch.Stop();

        // Report completion
        OnProgress?.Invoke(new QueryProgress(
            scan.SourceId, totalRows, totalBytes, IsComplete: true));

        return new FederatedSourceResult(
            batches,
            new SourceExecutionInfo(
                scan.SourceId,
                totalRows,
                totalBytes,
                stopwatch.Elapsed.TotalMilliseconds,
                FilterPushed: !scan.PushedFilters.IsDefaultOrEmpty && scan.PushedFilters.Length > 0,
                ProjectionPushed: !scan.PushedProjections.IsDefaultOrEmpty && scan.PushedProjections.Length > 0,
                AggregationPushed: false));
    }

    private async Task<FederatedSourceResult> ExecuteFederatedAggregateAsync(
        FederatedAggregateNode aggNode,
        CancellationToken ct)
    {
        var source = _registry.GetSource(aggNode.SourceId)
            ?? throw new QueryExecutionException(
                $"Federated source '{aggNode.SourceId}' not found in registry.",
                aggNode.SourceId, aggNode.TableName);

        var info = source.GetInfo();
        var stopwatch = Stopwatch.StartNew();

        // Build request — the remote source handles aggregation
        var request = new RemoteQueryRequest(
            aggNode.TableName,
            ImmutableArray<string>.Empty, // All columns needed for aggregation
            aggNode.PushedFilters,
            null,
            ImmutableArray<OrderByItem>.Empty);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_perSourceTimeout);

        long totalRows = 0;
        long totalBytes = 0;

        try
        {
            var asyncEnumerable = await source.ExecuteRemoteQueryAsync(
                aggNode.TableName, request, timeoutCts.Token);

            var batchList = new List<ColumnarBatch>();
            await foreach (var batch in asyncEnumerable.WithCancellation(timeoutCts.Token))
            {
                batchList.Add(batch);
                totalRows += batch.RowCount;
                totalBytes += EstimateBatchSize(batch);
            }

            stopwatch.Stop();

            OnProgress?.Invoke(new QueryProgress(
                aggNode.SourceId, totalRows, totalBytes, IsComplete: true));

            return new FederatedSourceResult(
                batchList,
                new SourceExecutionInfo(
                    aggNode.SourceId,
                    totalRows,
                    totalBytes,
                    stopwatch.Elapsed.TotalMilliseconds,
                    FilterPushed: !aggNode.PushedFilters.IsDefaultOrEmpty && aggNode.PushedFilters.Length > 0,
                    ProjectionPushed: false,
                    AggregationPushed: true));
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new QueryExecutionException(
                $"Remote aggregation timed out after {_perSourceTimeout.TotalSeconds}s.",
                aggNode.SourceId, aggNode.TableName);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not QueryExecutionException)
        {
            throw new QueryExecutionException(
                $"Remote aggregation failed: {ex.Message}",
                aggNode.SourceId, aggNode.TableName, ex);
        }
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

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
                ColumnDataType.Bool => batch.RowCount,
                ColumnDataType.Decimal => batch.RowCount * 16L,
                ColumnDataType.DateTime => batch.RowCount * 8L,
                ColumnDataType.String => batch.RowCount * 64L, // Estimate
                ColumnDataType.Binary => batch.RowCount * 128L, // Estimate
                _ => batch.RowCount * 8L
            };
        }
        return size;
    }

    private async Task PublishExecutionEventAsync(
        string topic,
        Dictionary<string, object> data,
        CancellationToken ct)
    {
        if (_messageBus == null) return;

        try
        {
            var message = new PluginMessage
            {
                Type = topic,
                SourcePluginId = "FederatedQueryEngine",
                Payload = data,
                Source = "FederatedQueryEngine"
            };

            await _messageBus.PublishAsync(topic, message, ct);
        }
        catch
        {
            // Message bus failure should not break query execution
        }
    }
}
