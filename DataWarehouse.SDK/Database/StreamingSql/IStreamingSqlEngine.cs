using System.Collections.Concurrent;

namespace DataWarehouse.SDK.Database.StreamingSql;

/// <summary>
/// Represents a single event in a data stream.
/// </summary>
/// <param name="Key">Partition/group key for the event.</param>
/// <param name="Value">Raw event payload.</param>
/// <param name="EventTime">When the event actually occurred (event-time semantics).</param>
/// <param name="ProcessingTime">When the event was ingested by the engine.</param>
/// <param name="Headers">Optional metadata headers.</param>
public sealed record StreamEvent(
    string Key,
    ReadOnlyMemory<byte> Value,
    DateTimeOffset EventTime,
    DateTimeOffset ProcessingTime,
    Dictionary<string, string>? Headers = null);

/// <summary>
/// Defines a continuous query with optional windowing and aggregation.
/// </summary>
/// <param name="QueryId">Unique query identifier (GUID).</param>
/// <param name="SqlText">The SQL-like query text.</param>
/// <param name="Window">Optional window specification for windowed aggregations.</param>
/// <param name="GroupByKey">Optional key expression for GROUP BY.</param>
/// <param name="Aggregations">Aggregation functions to apply.</param>
/// <param name="JoinTable">Optional static table name for stream-table joins.</param>
/// <param name="JoinKey">Optional key for stream-table join correlation.</param>
public sealed record ContinuousQuery(
    string QueryId,
    string SqlText,
    WindowSpec? Window = null,
    string? GroupByKey = null,
    AggregationKind[]? Aggregations = null,
    string? JoinTable = null,
    string? JoinKey = null)
{
    /// <summary>
    /// Creates a new ContinuousQuery with an auto-generated ID.
    /// </summary>
    public static ContinuousQuery Create(
        string sqlText,
        WindowSpec? window = null,
        string? groupByKey = null,
        AggregationKind[]? aggregations = null,
        string? joinTable = null,
        string? joinKey = null) =>
        new(Guid.NewGuid().ToString("N"), sqlText, window, groupByKey, aggregations, joinTable, joinKey);
}

/// <summary>
/// Kind of aggregation function.
/// </summary>
public enum AggregationKind
{
    Count,
    Sum,
    Avg,
    Min,
    Max,
    First,
    Last,
    CountDistinct
}

/// <summary>
/// Window specification for windowed aggregations.
/// </summary>
/// <param name="Type">The type of window.</param>
/// <param name="Size">Window size/duration.</param>
/// <param name="Advance">Advance interval for hopping windows.</param>
/// <param name="GapTimeout">Inactivity gap timeout for session windows.</param>
/// <param name="GracePeriod">Grace period for accepting late events after window closes.</param>
public sealed record WindowSpec(
    WindowType Type,
    TimeSpan Size,
    TimeSpan? Advance = null,
    TimeSpan? GapTimeout = null,
    TimeSpan? GracePeriod = null);

/// <summary>
/// Types of windowing supported by the streaming SQL engine.
/// </summary>
public enum WindowType
{
    /// <summary>Fixed-size, non-overlapping windows.</summary>
    Tumbling,

    /// <summary>Fixed-size, overlapping windows with a configurable advance interval.</summary>
    Hopping,

    /// <summary>Dynamic windows that close after a gap of inactivity.</summary>
    Session,

    /// <summary>Windows that emit results whenever contents change.</summary>
    Sliding
}

/// <summary>
/// A materialized view backed by a continuous query. Maintains the latest
/// aggregated state in a thread-safe concurrent dictionary.
/// </summary>
public sealed class MaterializedView
{
    private long _version;

    /// <summary>Unique view identifier.</summary>
    public string ViewId { get; }

    /// <summary>The continuous query that feeds this view.</summary>
    public ContinuousQuery SourceQuery { get; }

    /// <summary>Thread-safe state store for materialized results.</summary>
    public ConcurrentDictionary<string, object> State { get; } = new();

    /// <summary>Monotonically-increasing version, incremented on each update.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Timestamp of last state update.</summary>
    public DateTimeOffset LastUpdated { get; private set; }

    /// <summary>
    /// Creates a new materialized view for the given query.
    /// </summary>
    public MaterializedView(string viewId, ContinuousQuery sourceQuery)
    {
        ViewId = viewId ?? throw new ArgumentNullException(nameof(viewId));
        SourceQuery = sourceQuery ?? throw new ArgumentNullException(nameof(sourceQuery));
    }

    /// <summary>
    /// Thread-safe state update. Increments version and updates timestamp.
    /// </summary>
    public void Update(string key, object value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        State[key] = value;
        Interlocked.Increment(ref _version);
        LastUpdated = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Read a value from materialized state.
    /// </summary>
    public object? Get(string key)
    {
        return State.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>
    /// Returns a point-in-time snapshot of the materialized state.
    /// </summary>
    public IReadOnlyDictionary<string, object> Snapshot()
    {
        return new Dictionary<string, object>(State);
    }
}

/// <summary>
/// Output emitted by a continuous query when a window fires or an event is processed.
/// </summary>
/// <param name="QueryId">The query that produced this output.</param>
/// <param name="Key">The group key for the output.</param>
/// <param name="Values">Aggregated values.</param>
/// <param name="WindowStart">Start of the window period.</param>
/// <param name="WindowEnd">End of the window period.</param>
public sealed record QueryOutput(
    string QueryId,
    string Key,
    IReadOnlyDictionary<string, object> Values,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd);

/// <summary>
/// Contract for a streaming SQL engine supporting continuous queries,
/// windowed aggregations, materialized views, and stream-table joins.
/// </summary>
public interface IStreamingSqlEngine
{
    /// <summary>
    /// Registers a continuous query. Returns the query ID.
    /// </summary>
    ValueTask<string> RegisterQueryAsync(ContinuousQuery query, CancellationToken ct = default);

    /// <summary>
    /// Unregisters and stops a continuous query.
    /// </summary>
    ValueTask UnregisterQueryAsync(string queryId, CancellationToken ct = default);

    /// <summary>
    /// Feeds a single event to all matching queries.
    /// May block under backpressure if output channels are full.
    /// </summary>
    ValueTask IngestAsync(StreamEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Batch ingest for higher throughput.
    /// </summary>
    ValueTask IngestBatchAsync(IReadOnlyList<StreamEvent> events, CancellationToken ct = default);

    /// <summary>
    /// Subscribes to the output of a continuous query.
    /// </summary>
    IAsyncEnumerable<QueryOutput> SubscribeAsync(string queryId, CancellationToken ct = default);

    /// <summary>
    /// Gets the materialized view for a query, if it exists.
    /// </summary>
    MaterializedView? GetMaterializedView(string queryId);

    /// <summary>
    /// Returns current engine statistics.
    /// </summary>
    StreamingEngineStats GetStats();
}

/// <summary>
/// Statistics for the streaming SQL engine.
/// </summary>
/// <param name="TotalEventsIngested">Total events fed into the engine.</param>
/// <param name="TotalOutputsEmitted">Total query outputs produced.</param>
/// <param name="LateEventsDropped">Events dropped because they arrived after watermark + grace period.</param>
/// <param name="ActiveQueries">Number of currently registered queries.</param>
/// <param name="ActiveWindows">Total active window instances across all queries.</param>
public sealed record StreamingEngineStats(
    long TotalEventsIngested,
    long TotalOutputsEmitted,
    long LateEventsDropped,
    int ActiveQueries,
    int ActiveWindows);
