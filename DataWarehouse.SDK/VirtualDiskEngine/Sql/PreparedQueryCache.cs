using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Sql;

/// <summary>
/// Statistics for the prepared query cache.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-15 prepared query cache statistics")]
public readonly struct PreparedQueryCacheStats
{
    /// <summary>Number of cache hits.</summary>
    public long Hits { get; init; }

    /// <summary>Number of cache misses.</summary>
    public long Misses { get; init; }

    /// <summary>Number of entries evicted due to capacity.</summary>
    public long Evictions { get; init; }

    /// <summary>Current number of cached entries.</summary>
    public int EntryCount { get; init; }

    /// <summary>Hit ratio (Hits / (Hits + Misses)), or 0 if no requests.</summary>
    public double HitRatio
    {
        get
        {
            long total = Hits + Misses;
            return total == 0 ? 0.0 : (double)Hits / total;
        }
    }
}

/// <summary>
/// Execution context provided to query plan nodes during execution.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-15 query execution context")]
public sealed class QueryContext
{
    /// <summary>Block size in bytes for the underlying VDE.</summary>
    public int BlockSize { get; }

    /// <summary>Cancellation token for the query.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Optional parameter values for prepared queries (positional).</summary>
    public IReadOnlyList<object?> Parameters { get; }

    /// <summary>
    /// Initializes a new query context.
    /// </summary>
    public QueryContext(int blockSize, CancellationToken ct = default, IReadOnlyList<object?>? parameters = null)
    {
        BlockSize = blockSize;
        CancellationToken = ct;
        Parameters = parameters ?? Array.Empty<object?>();
    }
}

/// <summary>
/// Abstract base class for query execution plan nodes.
/// Each node represents an operation in the query plan tree (scan, join, filter, etc.).
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-15 query plan node abstraction")]
public abstract class QueryPlanNode
{
    /// <summary>
    /// The type of this plan node (TableScan, IndexScan, MergeJoin, Filter, Project, Sort, Aggregate).
    /// </summary>
    public string NodeType { get; }

    /// <summary>
    /// Child nodes in the plan tree, or null for leaf nodes.
    /// </summary>
    public QueryPlanNode[]? Children { get; }

    /// <summary>
    /// Estimated number of rows this node will produce.
    /// </summary>
    public long EstimatedRows { get; }

    /// <summary>
    /// Initializes a new query plan node.
    /// </summary>
    protected QueryPlanNode(string nodeType, long estimatedRows, QueryPlanNode[]? children = null)
    {
        NodeType = nodeType;
        EstimatedRows = estimatedRows;
        Children = children;
    }

    /// <summary>
    /// Executes this plan node, producing a stream of result rows.
    /// </summary>
    /// <param name="ctx">The query execution context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of result row byte arrays.</returns>
    public abstract IAsyncEnumerable<byte[]> ExecuteAsync(QueryContext ctx, CancellationToken ct);
}

/// <summary>
/// A compiled query plan cached for reuse. Stores the execution plan tree,
/// fingerprint, original SQL, and execution statistics.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-15 prepared query plan")]
public sealed class PreparedQuery
{
    /// <summary>Normalized SQL fingerprint used as the cache key.</summary>
    public string Fingerprint { get; }

    /// <summary>Original SQL text before normalization.</summary>
    public string OriginalSql { get; }

    /// <summary>Root node of the execution plan tree.</summary>
    public QueryPlanNode RootNode { get; }

    /// <summary>When this plan was first cached.</summary>
    public DateTimeOffset CachedUtc { get; }

    /// <summary>How many times this cached plan has been reused.</summary>
    public long ExecutionCount => Interlocked.Read(ref _executionCount);
    private long _executionCount;

    /// <summary>Rolling average execution time across all uses.</summary>
    public TimeSpan AverageExecutionTime => TimeSpan.FromTicks(Interlocked.Read(ref _averageExecutionTimeTicks));
    private long _averageExecutionTimeTicks;

    /// <summary>
    /// Initializes a new prepared query.
    /// </summary>
    public PreparedQuery(string fingerprint, string originalSql, QueryPlanNode rootNode)
    {
        Fingerprint = fingerprint;
        OriginalSql = originalSql;
        RootNode = rootNode;
        CachedUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Records an execution, updating count and rolling average time.
    /// </summary>
    /// <param name="elapsed">The elapsed time for this execution.</param>
    public void RecordExecution(TimeSpan elapsed)
    {
        // Atomic rolling average update using compare-and-swap loop
        long count = Interlocked.Read(ref _executionCount);
        if (count == 0)
        {
            Interlocked.Exchange(ref _averageExecutionTimeTicks, elapsed.Ticks);
        }
        else
        {
            // Rolling average: avg = avg + (new - avg) / (count + 1)
            long currentTicks;
            long newTicks;
            do
            {
                currentTicks = Interlocked.Read(ref _averageExecutionTimeTicks);
                newTicks = (long)(currentTicks + (double)(elapsed.Ticks - currentTicks) / (count + 1));
            } while (Interlocked.CompareExchange(ref _averageExecutionTimeTicks, newTicks, currentTicks) != currentTicks);
        }

        Interlocked.Increment(ref _executionCount);
    }
}

/// <summary>
/// LRU cache of parsed and planned SQL queries keyed by SQL fingerprint.
/// Fingerprinting normalizes SQL text by replacing literal values with placeholders,
/// allowing structurally identical queries to share cached execution plans.
/// </summary>
/// <remarks>
/// <para>
/// The cache uses a <see cref="LinkedList{T}"/> for LRU ordering with a
/// <see cref="Dictionary{TKey,TValue}"/> for O(1) lookup by fingerprint.
/// When capacity is exceeded, the least-recently-used entry is evicted.
/// </para>
/// <para>
/// Table-based invalidation removes all cached plans that reference a given table,
/// supporting DDL changes that alter table structure.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-15 prepared query cache")]
public sealed class PreparedQueryCache
{
    private readonly int _maxEntries;
    private readonly LinkedList<(string Fingerprint, PreparedQuery Plan)> _lruList;
    private readonly Dictionary<string, LinkedListNode<(string Fingerprint, PreparedQuery Plan)>> _lookup;
    private readonly object _lock = new();

    private long _hits;
    private long _misses;
    private long _evictions;

    // Regex patterns for fingerprinting
    private static readonly Regex NumericLiteralPattern = new(
        @"\b\d+(\.\d+)?\b",
        RegexOptions.Compiled);

    private static readonly Regex StringLiteralPattern = new(
        @"'[^']*'",
        RegexOptions.Compiled);

    private static readonly Regex WhitespacePattern = new(
        @"\s+",
        RegexOptions.Compiled);

    /// <summary>
    /// Initializes a new prepared query cache.
    /// </summary>
    /// <param name="maxEntries">Maximum number of cached plans. Default: 1024.</param>
    public PreparedQueryCache(int maxEntries = 1024)
    {
        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "Max entries must be positive.");

        _maxEntries = maxEntries;
        _lruList = new LinkedList<(string Fingerprint, PreparedQuery Plan)>();
        _lookup = new Dictionary<string, LinkedListNode<(string Fingerprint, PreparedQuery Plan)>>(
            maxEntries, StringComparer.Ordinal);
    }

    /// <summary>
    /// Normalizes SQL text into a fingerprint by stripping whitespace,
    /// replacing numeric and string literals with <c>?</c> placeholders,
    /// and lowercasing keywords.
    /// </summary>
    /// <param name="sql">The SQL text to fingerprint.</param>
    /// <returns>The normalized fingerprint string.</returns>
    /// <example>
    /// <code>
    /// Fingerprint("SELECT * FROM t WHERE id = 42")
    /// // Returns: "select * from t where id = ?"
    /// </code>
    /// </example>
    public string Fingerprint(string sql)
    {
        if (string.IsNullOrEmpty(sql))
            return string.Empty;

        // Replace string literals first (before numeric, to avoid matching numbers inside strings)
        string result = StringLiteralPattern.Replace(sql, "?");

        // Replace numeric literals
        result = NumericLiteralPattern.Replace(result, "?");

        // Normalize whitespace
        result = WhitespacePattern.Replace(result.Trim(), " ");

        // Lowercase for case-insensitive matching
        return result.ToLowerInvariant();
    }

    /// <summary>
    /// Attempts to retrieve a cached query plan for the given SQL.
    /// </summary>
    /// <param name="sql">The SQL text to look up.</param>
    /// <returns>The cached plan, or null if not found.</returns>
    public PreparedQuery? TryGet(string sql)
    {
        string fp = Fingerprint(sql);

        lock (_lock)
        {
            if (_lookup.TryGetValue(fp, out var node))
            {
                // Move to front (most recently used)
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                _hits++;
                return node.Value.Plan;
            }

            _misses++;
            return null;
        }
    }

    /// <summary>
    /// Caches a query plan, evicting the least-recently-used entry if at capacity.
    /// </summary>
    /// <param name="sql">The original SQL text.</param>
    /// <param name="plan">The compiled query plan to cache.</param>
    public void Put(string sql, PreparedQuery plan)
    {
        string fp = Fingerprint(sql);

        lock (_lock)
        {
            // If already cached, update and move to front
            if (_lookup.TryGetValue(fp, out var existing))
            {
                _lruList.Remove(existing);
                existing.Value = (fp, plan);
                _lruList.AddFirst(existing);
                return;
            }

            // Evict LRU if at capacity
            while (_lruList.Count >= _maxEntries)
            {
                var last = _lruList.Last;
                if (last is not null)
                {
                    _lookup.Remove(last.Value.Fingerprint);
                    _lruList.RemoveLast();
                    _evictions++;
                }
            }

            // Add new entry at front
            var node = _lruList.AddFirst((fp, plan));
            _lookup[fp] = node;
        }
    }

    /// <summary>
    /// Invalidates all cached plans that reference the given table name.
    /// Used when DDL changes (CREATE, ALTER, DROP) modify table structure.
    /// </summary>
    /// <param name="tableName">The table name whose plans should be removed.</param>
    public void Invalidate(string tableName)
    {
        if (string.IsNullOrEmpty(tableName))
            return;

        string lowerTable = tableName.ToLowerInvariant();

        lock (_lock)
        {
            var toRemove = new List<string>();

            foreach (var kvp in _lookup)
            {
                // Check if the fingerprint or original SQL references this table
                if (kvp.Value.Value.Plan.OriginalSql.Contains(tableName, StringComparison.OrdinalIgnoreCase) ||
                    kvp.Key.Contains(lowerTable, StringComparison.Ordinal))
                {
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (string key in toRemove)
            {
                if (_lookup.TryGetValue(key, out var node))
                {
                    _lruList.Remove(node);
                    _lookup.Remove(key);
                }
            }
        }
    }

    /// <summary>
    /// Removes all cached plans.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _lruList.Clear();
            _lookup.Clear();
        }
    }

    /// <summary>
    /// Returns current cache statistics.
    /// </summary>
    public PreparedQueryCacheStats GetStats()
    {
        lock (_lock)
        {
            return new PreparedQueryCacheStats
            {
                Hits = _hits,
                Misses = _misses,
                Evictions = _evictions,
                EntryCount = _lruList.Count,
            };
        }
    }
}
