using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.SDK.Database.StreamingSql;

/// <summary>
/// Configuration for the streaming SQL engine.
/// </summary>
/// <param name="MaxActiveQueries">Maximum number of concurrent queries.</param>
/// <param name="MaxBufferedEvents">Backpressure threshold: max buffered outputs per query channel.</param>
/// <param name="WatermarkInterval">Watermark lag behind max observed event time.</param>
/// <param name="EnableBackpressure">Whether to apply backpressure when output channels are full.</param>
public sealed record StreamingSqlEngineConfig(
    int MaxActiveQueries = 1000,
    int MaxBufferedEvents = 100_000,
    TimeSpan? WatermarkInterval = null,
    bool EnableBackpressure = true)
{
    /// <summary>Effective watermark interval (defaults to 1 second).</summary>
    public TimeSpan EffectiveWatermarkInterval => WatermarkInterval ?? TimeSpan.FromSeconds(1);
}

/// <summary>
/// Streaming SQL engine implementation with continuous queries, watermark-based
/// late event handling, backpressure via bounded channels, materialized views,
/// and stream-table joins.
/// </summary>
public sealed class StreamingSqlEngine : IStreamingSqlEngine, IDisposable
{
    private readonly ILogger _logger;
    private readonly StreamingSqlEngineConfig _config;
    private readonly ConcurrentDictionary<string, ActiveQuery> _queries = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, object>> _tables = new();

    private long _totalEventsIngested;
    private long _totalOutputsEmitted;
    private long _lateEventsDropped;
    private bool _disposed;

    /// <summary>
    /// Creates a new streaming SQL engine.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="config">Optional configuration.</param>
    public StreamingSqlEngine(ILogger? logger = null, StreamingSqlEngineConfig? config = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _config = config ?? new StreamingSqlEngineConfig();
    }

    /// <inheritdoc />
    public ValueTask<string> RegisterQueryAsync(ContinuousQuery query, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(query);

        if (_queries.Count >= _config.MaxActiveQueries)
        {
            throw new InvalidOperationException(
                $"Maximum active queries ({_config.MaxActiveQueries}) exceeded.");
        }

        var channelOptions = new BoundedChannelOptions(_config.MaxBufferedEvents)
        {
            FullMode = _config.EnableBackpressure
                ? BoundedChannelFullMode.Wait
                : BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = false
        };

        var windowOp = CreateWindowOperator(query);
        var activeQuery = new ActiveQuery(
            query,
            windowOp,
            new MaterializedView(query.QueryId, query),
            Channel.CreateBounded<QueryOutput>(channelOptions));

        if (!_queries.TryAdd(query.QueryId, activeQuery))
        {
            throw new InvalidOperationException($"Query '{query.QueryId}' is already registered.");
        }

        _logger.LogInformation("Registered streaming query {QueryId}: {Sql}",
            query.QueryId, query.SqlText);

        return ValueTask.FromResult(query.QueryId);
    }

    /// <inheritdoc />
    public ValueTask UnregisterQueryAsync(string queryId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(queryId);

        if (_queries.TryRemove(queryId, out var active))
        {
            active.OutputChannel.Writer.TryComplete();
            _logger.LogInformation("Unregistered streaming query {QueryId}", queryId);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask IngestAsync(StreamEvent evt, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(evt);

        Interlocked.Increment(ref _totalEventsIngested);

        foreach (var kvp in _queries)
        {
            var active = kvp.Value;
            await ProcessEventForQueryAsync(active, evt, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask IngestBatchAsync(IReadOnlyList<StreamEvent> events, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(events);

        for (int i = 0; i < events.Count; i++)
        {
            await IngestAsync(events[i], ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryOutput> SubscribeAsync(
        string queryId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(queryId);

        if (!_queries.TryGetValue(queryId, out var active))
        {
            throw new InvalidOperationException($"Query '{queryId}' is not registered.");
        }

        var reader = active.OutputChannel.Reader;

        await foreach (var output in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return output;
        }
    }

    /// <inheritdoc />
    public MaterializedView? GetMaterializedView(string queryId)
    {
        return _queries.TryGetValue(queryId, out var active)
            ? active.View
            : null;
    }

    /// <inheritdoc />
    public StreamingEngineStats GetStats()
    {
        int activeWindows = 0;
        foreach (var kvp in _queries)
        {
            activeWindows += kvp.Value.WindowOperator?.ActiveWindowCount ?? 0;
        }

        return new StreamingEngineStats(
            TotalEventsIngested: Interlocked.Read(ref _totalEventsIngested),
            TotalOutputsEmitted: Interlocked.Read(ref _totalOutputsEmitted),
            LateEventsDropped: Interlocked.Read(ref _lateEventsDropped),
            ActiveQueries: _queries.Count,
            ActiveWindows: activeWindows);
    }

    /// <summary>
    /// Registers a static reference table for stream-table joins.
    /// </summary>
    /// <param name="tableName">Name of the table.</param>
    /// <param name="data">Key-value data for the table.</param>
    public void RegisterTable(string tableName, IReadOnlyDictionary<string, object> data)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(data);

        var dict = new ConcurrentDictionary<string, object>(data);
        _tables[tableName] = dict;

        _logger.LogInformation("Registered reference table '{Table}' with {Count} entries",
            tableName, data.Count);
    }

    /// <summary>
    /// Registers a static reference table for stream-table joins (async variant).
    /// </summary>
    public ValueTask RegisterTableAsync(string tableName, IReadOnlyDictionary<string, object> data, CancellationToken ct = default)
    {
        RegisterTable(tableName, data);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Disposes the engine and completes all output channels.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var kvp in _queries)
        {
            kvp.Value.OutputChannel.Writer.TryComplete();
        }

        _queries.Clear();
        _tables.Clear();
    }

    private async ValueTask ProcessEventForQueryAsync(
        ActiveQuery active, StreamEvent evt, CancellationToken ct)
    {
        var query = active.Definition;
        var gracePeriod = query.Window?.GracePeriod ?? TimeSpan.Zero;

        // Advance watermark: max(eventTime) - watermarkInterval
        var candidateWatermark = evt.EventTime - _config.EffectiveWatermarkInterval;
        if (candidateWatermark > active.Watermark)
        {
            active.Watermark = candidateWatermark;
        }

        // Late event detection
        if (evt.EventTime < active.Watermark - gracePeriod)
        {
            Interlocked.Increment(ref _lateEventsDropped);
            _logger.LogDebug("Dropped late event: eventTime={EventTime}, watermark={Watermark}",
                evt.EventTime, active.Watermark);
            return;
        }

        // Stream-table join enrichment
        Dictionary<string, object>? joinedData = null;
        if (query.JoinTable is not null && query.JoinKey is not null)
        {
            joinedData = ResolveJoin(query.JoinTable, evt.Key);
        }

        // Dispatch to window operator if present
        if (active.WindowOperator is not null)
        {
            active.WindowOperator.ProcessEvent(evt);

            // Check for expired windows
            var results = active.WindowOperator.FireExpiredWindows(active.Watermark);

            foreach (var result in results)
            {
                var values = new Dictionary<string, object>(result.Aggregations);
                if (joinedData is not null)
                {
                    foreach (var kv in joinedData)
                    {
                        values.TryAdd($"join_{kv.Key}", kv.Value);
                    }
                }

                var output = new QueryOutput(
                    query.QueryId,
                    result.GroupKey,
                    values,
                    result.WindowStart,
                    result.WindowEnd);

                await EmitOutputAsync(active, output, ct).ConfigureAwait(false);
            }
        }
        else
        {
            // No window: pass-through with optional join data
            var values = new Dictionary<string, object>
            {
                ["key"] = evt.Key,
                ["event_time"] = evt.EventTime,
                ["value_length"] = evt.Value.Length
            };

            if (joinedData is not null)
            {
                foreach (var kv in joinedData)
                {
                    values.TryAdd($"join_{kv.Key}", kv.Value);
                }
            }

            var output = new QueryOutput(
                query.QueryId,
                evt.Key,
                values,
                evt.EventTime,
                evt.EventTime);

            await EmitOutputAsync(active, output, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask EmitOutputAsync(ActiveQuery active, QueryOutput output, CancellationToken ct)
    {
        // Update materialized view
        active.View.Update(output.Key, output.Values);

        // Write to output channel (may block under backpressure)
        await active.OutputChannel.Writer.WriteAsync(output, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _totalOutputsEmitted);
    }

    private Dictionary<string, object>? ResolveJoin(string tableName, string key)
    {
        if (!_tables.TryGetValue(tableName, out var table))
        {
            return null;
        }

        if (!table.TryGetValue(key, out var value))
        {
            return null;
        }

        // If the joined value is itself a dictionary, use it directly
        if (value is IReadOnlyDictionary<string, object> dict)
        {
            return new Dictionary<string, object>(dict);
        }

        return new Dictionary<string, object> { [tableName] = value };
    }

    private static IWindowOperator? CreateWindowOperator(ContinuousQuery query)
    {
        if (query.Window is null) return null;

        var aggregations = query.Aggregations ?? [AggregationKind.Count];

        return query.Window.Type switch
        {
            WindowType.Tumbling => new TumblingWindow(
                query.Window.Size, query.GroupByKey, aggregations),
            WindowType.Hopping => new HoppingWindow(
                query.Window.Size,
                query.Window.Advance ?? TimeSpan.FromTicks(query.Window.Size.Ticks / 2),
                query.GroupByKey,
                aggregations),
            WindowType.Session => new SessionWindow(
                query.Window.GapTimeout ?? TimeSpan.FromMinutes(5),
                query.GroupByKey,
                aggregations),
            WindowType.Sliding => new SlidingWindow(
                query.Window.Size, query.GroupByKey, aggregations),
            _ => throw new ArgumentOutOfRangeException(
                nameof(query), $"Unsupported window type: {query.Window.Type}")
        };
    }

    /// <summary>
    /// Internal state for a registered active query.
    /// </summary>
    private sealed class ActiveQuery
    {
        public ContinuousQuery Definition { get; }
        public IWindowOperator? WindowOperator { get; }
        public MaterializedView View { get; }
        public Channel<QueryOutput> OutputChannel { get; }
        public DateTimeOffset Watermark { get; set; } = DateTimeOffset.MinValue;

        public ActiveQuery(
            ContinuousQuery definition,
            IWindowOperator? windowOperator,
            MaterializedView view,
            Channel<QueryOutput> outputChannel)
        {
            Definition = definition;
            WindowOperator = windowOperator;
            View = view;
            OutputChannel = outputChannel;
        }
    }
}
