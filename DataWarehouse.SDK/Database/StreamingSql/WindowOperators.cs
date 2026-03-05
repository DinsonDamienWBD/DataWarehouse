namespace DataWarehouse.SDK.Database.StreamingSql;

/// <summary>
/// Contract for window operators that accumulate events and fire results
/// when windows expire based on watermark progression.
/// </summary>
public interface IWindowOperator
{
    /// <summary>Processes an incoming event into the appropriate window(s).</summary>
    void ProcessEvent(StreamEvent evt);

    /// <summary>
    /// Returns results for all windows that should fire given the current watermark,
    /// and removes those windows from active tracking.
    /// </summary>
    IReadOnlyList<WindowResult> FireExpiredWindows(DateTimeOffset watermark);

    /// <summary>Number of currently active window instances.</summary>
    int ActiveWindowCount { get; }
}

/// <summary>
/// Result emitted when a window fires.
/// </summary>
/// <param name="GroupKey">The group key for this result.</param>
/// <param name="WindowStart">Window start time.</param>
/// <param name="WindowEnd">Window end time.</param>
/// <param name="Aggregations">Computed aggregation values.</param>
public sealed record WindowResult(
    string GroupKey,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    IReadOnlyDictionary<string, object> Aggregations);

/// <summary>
/// Accumulates running aggregations for a single window bucket.
/// Thread-safe via lock (windows are per-query, low contention).
/// </summary>
public sealed class WindowAggregator
{
    private readonly object _lock = new();
    private readonly AggregationKind[] _aggregations;

    private long _count;
    private double _sum;
    private double _min = double.MaxValue;
    private double _max = double.MinValue;
    private object? _first;
    private object? _last;
    private bool _hasFirst;
    private readonly HashSet<string> _distinct = [];

    /// <summary>Creates a new aggregator for the specified aggregation kinds.</summary>
    public WindowAggregator(AggregationKind[] aggregations)
    {
        _aggregations = aggregations ?? throw new ArgumentNullException(nameof(aggregations));
    }

    /// <summary>
    /// Adds an event's contribution to the running aggregates.
    /// </summary>
    /// <param name="evt">The stream event.</param>
    /// <param name="groupByKey">Optional group-by key expression.</param>
    public void Add(StreamEvent evt, string? groupByKey)
    {
        lock (_lock)
        {
            _count++;

            // Extract numeric value from event payload for sum/avg/min/max
            double numericValue = ExtractNumericValue(evt);

            _sum += numericValue;

            if (numericValue < _min) _min = numericValue;
            if (numericValue > _max) _max = numericValue;

            if (!_hasFirst)
            {
                _first = evt.Key;
                _hasFirst = true;
            }
            _last = evt.Key;

            _distinct.Add(evt.Key);
        }
    }

    /// <summary>
    /// Returns the final aggregation values as a dictionary.
    /// </summary>
    public IReadOnlyDictionary<string, object> GetResult()
    {
        lock (_lock)
        {
            var result = new Dictionary<string, object>();

            foreach (var agg in _aggregations)
            {
                var key = agg.ToString().ToLowerInvariant();
                result[key] = agg switch
                {
                    AggregationKind.Count => _count,
                    AggregationKind.Sum => _sum,
                    AggregationKind.Avg => _count > 0 ? _sum / _count : 0.0,
                    AggregationKind.Min => _min == double.MaxValue ? 0.0 : _min,
                    AggregationKind.Max => _max == double.MinValue ? 0.0 : _max,
                    AggregationKind.First => _first ?? string.Empty,
                    AggregationKind.Last => _last ?? string.Empty,
                    AggregationKind.CountDistinct => (long)_distinct.Count,
                    _ => 0L
                };
            }

            return result;
        }
    }

    private static double ExtractNumericValue(StreamEvent evt)
    {
        // Try to interpret the first 8 bytes of the value as a double,
        // falling back to the value length as a numeric proxy.
        if (evt.Value.Length >= sizeof(double))
        {
            try
            {
                return BitConverter.ToDouble(evt.Value.Span[..sizeof(double)]);
            }
            catch
            {
                // Fall through to length-based value
            }
        }

        return evt.Value.Length;
    }
}

/// <summary>
/// Fixed-size, non-overlapping tumbling window operator.
/// Events are bucketed by their event time into fixed-duration windows.
/// </summary>
public sealed class TumblingWindow : IWindowOperator
{
    private readonly TimeSpan _windowSize;
    private readonly string? _groupByKey;
    private readonly AggregationKind[] _aggregations;
    private readonly object _lock = new();

    // Keyed by window start ticks, then by group key
    private readonly SortedDictionary<long, Dictionary<string, WindowAggregator>> _buckets = new();

    /// <summary>Creates a tumbling window operator.</summary>
    /// <param name="windowSize">The fixed window duration.</param>
    /// <param name="groupByKey">Optional GROUP BY key.</param>
    /// <param name="aggregations">Aggregation functions to apply.</param>
    public TumblingWindow(TimeSpan windowSize, string? groupByKey, AggregationKind[] aggregations)
    {
        _windowSize = windowSize;
        _groupByKey = groupByKey;
        _aggregations = aggregations;
    }

    /// <inheritdoc />
    public int ActiveWindowCount
    {
        get
        {
            lock (_lock) return _buckets.Count;
        }
    }

    /// <inheritdoc />
    public void ProcessEvent(StreamEvent evt)
    {
        long windowStartTicks = evt.EventTime.Ticks / _windowSize.Ticks * _windowSize.Ticks;
        string groupKey = _groupByKey is not null ? evt.Key : "__global__";

        lock (_lock)
        {
            if (!_buckets.TryGetValue(windowStartTicks, out var groups))
            {
                groups = new Dictionary<string, WindowAggregator>();
                _buckets[windowStartTicks] = groups;
            }

            if (!groups.TryGetValue(groupKey, out var aggregator))
            {
                aggregator = new WindowAggregator(_aggregations);
                groups[groupKey] = aggregator;
            }

            aggregator.Add(evt, _groupByKey);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<WindowResult> FireExpiredWindows(DateTimeOffset watermark)
    {
        var results = new List<WindowResult>();

        lock (_lock)
        {
            var toRemove = new List<long>();

            foreach (var kvp in _buckets)
            {
                var windowStart = new DateTimeOffset(kvp.Key, TimeSpan.Zero);
                var windowEnd = windowStart + _windowSize;

                if (windowEnd <= watermark)
                {
                    foreach (var group in kvp.Value)
                    {
                        results.Add(new WindowResult(
                            group.Key,
                            windowStart,
                            windowEnd,
                            group.Value.GetResult()));
                    }
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var key in toRemove)
            {
                _buckets.Remove(key);
            }
        }

        return results;
    }
}

/// <summary>
/// Fixed-size, overlapping hopping window operator. Each event may belong
/// to multiple windows separated by the advance interval.
/// </summary>
public sealed class HoppingWindow : IWindowOperator
{
    private readonly TimeSpan _windowSize;
    private readonly TimeSpan _advance;
    private readonly string? _groupByKey;
    private readonly AggregationKind[] _aggregations;
    private readonly object _lock = new();

    private readonly SortedDictionary<long, Dictionary<string, WindowAggregator>> _buckets = new();

    /// <summary>Creates a hopping window operator.</summary>
    /// <param name="windowSize">The window duration.</param>
    /// <param name="advance">The advance/hop interval.</param>
    /// <param name="groupByKey">Optional GROUP BY key.</param>
    /// <param name="aggregations">Aggregation functions to apply.</param>
    public HoppingWindow(TimeSpan windowSize, TimeSpan advance, string? groupByKey, AggregationKind[] aggregations)
    {
        _windowSize = windowSize;
        _advance = advance;
        _groupByKey = groupByKey;
        _aggregations = aggregations;
    }

    /// <inheritdoc />
    public int ActiveWindowCount
    {
        get
        {
            lock (_lock) return _buckets.Count;
        }
    }

    /// <inheritdoc />
    public void ProcessEvent(StreamEvent evt)
    {
        string groupKey = _groupByKey is not null ? evt.Key : "__global__";

        // Compute all window starts that contain this event
        // Event belongs to windows where windowStart <= eventTime < windowStart + windowSize
        long advanceTicks = _advance.Ticks;
        long windowSizeTicks = _windowSize.Ticks;

        // Latest window start that could contain this event
        long latestStart = evt.EventTime.Ticks / advanceTicks * advanceTicks;

        // How many hops back does the window size cover?
        int hopsBack = (int)((windowSizeTicks + advanceTicks - 1) / advanceTicks);

        lock (_lock)
        {
            for (int i = 0; i < hopsBack; i++)
            {
                long windowStart = latestStart - (i * advanceTicks);
                if (windowStart < 0) continue;

                // Check that event actually falls within [windowStart, windowStart + windowSize)
                if (evt.EventTime.Ticks >= windowStart && evt.EventTime.Ticks < windowStart + windowSizeTicks)
                {
                    if (!_buckets.TryGetValue(windowStart, out var groups))
                    {
                        groups = new Dictionary<string, WindowAggregator>();
                        _buckets[windowStart] = groups;
                    }

                    if (!groups.TryGetValue(groupKey, out var aggregator))
                    {
                        aggregator = new WindowAggregator(_aggregations);
                        groups[groupKey] = aggregator;
                    }

                    aggregator.Add(evt, _groupByKey);
                }
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<WindowResult> FireExpiredWindows(DateTimeOffset watermark)
    {
        var results = new List<WindowResult>();

        lock (_lock)
        {
            var toRemove = new List<long>();

            foreach (var kvp in _buckets)
            {
                var windowStart = new DateTimeOffset(kvp.Key, TimeSpan.Zero);
                var windowEnd = windowStart + _windowSize;

                if (windowEnd <= watermark)
                {
                    foreach (var group in kvp.Value)
                    {
                        results.Add(new WindowResult(
                            group.Key,
                            windowStart,
                            windowEnd,
                            group.Value.GetResult()));
                    }
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var key in toRemove)
            {
                _buckets.Remove(key);
            }
        }

        return results;
    }
}

/// <summary>
/// Dynamic session window operator. Sessions are keyed by group key and close
/// after a configurable period of inactivity (gap timeout).
/// </summary>
public sealed class SessionWindow : IWindowOperator
{
    private readonly TimeSpan _gapTimeout;
    private readonly string? _groupByKey;
    private readonly AggregationKind[] _aggregations;
    private readonly object _lock = new();

    private readonly Dictionary<string, SessionState> _sessions = new();

    /// <summary>Creates a session window operator.</summary>
    /// <param name="gapTimeout">Inactivity gap after which the session closes.</param>
    /// <param name="groupByKey">Optional GROUP BY key.</param>
    /// <param name="aggregations">Aggregation functions to apply.</param>
    public SessionWindow(TimeSpan gapTimeout, string? groupByKey, AggregationKind[] aggregations)
    {
        _gapTimeout = gapTimeout;
        _groupByKey = groupByKey;
        _aggregations = aggregations;
    }

    /// <inheritdoc />
    public int ActiveWindowCount
    {
        get
        {
            lock (_lock) return _sessions.Count;
        }
    }

    /// <inheritdoc />
    public void ProcessEvent(StreamEvent evt)
    {
        string groupKey = _groupByKey is not null ? evt.Key : "__global__";

        lock (_lock)
        {
            if (_sessions.TryGetValue(groupKey, out var session))
            {
                // Check if event extends the existing session
                if (session.LastEventTime + _gapTimeout >= evt.EventTime)
                {
                    // Extend session
                    session.LastEventTime = evt.EventTime;
                    session.Aggregator.Add(evt, _groupByKey);
                    return;
                }
                else
                {
                    // Gap exceeded -- session is implicitly expired.
                    // It will be collected by FireExpiredWindows.
                    // Start a new session for this key.
                }
            }

            // Create new session
            var newSession = new SessionState
            {
                Start = evt.EventTime,
                LastEventTime = evt.EventTime,
                Aggregator = new WindowAggregator(_aggregations)
            };
            newSession.Aggregator.Add(evt, _groupByKey);
            _sessions[groupKey] = newSession;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<WindowResult> FireExpiredWindows(DateTimeOffset watermark)
    {
        var results = new List<WindowResult>();

        lock (_lock)
        {
            var toRemove = new List<string>();

            foreach (var kvp in _sessions)
            {
                if (kvp.Value.LastEventTime + _gapTimeout <= watermark)
                {
                    results.Add(new WindowResult(
                        kvp.Key,
                        kvp.Value.Start,
                        kvp.Value.LastEventTime + _gapTimeout,
                        kvp.Value.Aggregator.GetResult()));
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var key in toRemove)
            {
                _sessions.Remove(key);
            }
        }

        return results;
    }

    private sealed class SessionState
    {
        public DateTimeOffset Start { get; set; }
        public DateTimeOffset LastEventTime { get; set; }
        public WindowAggregator Aggregator { get; set; } = null!;
    }
}

/// <summary>
/// Sliding window operator that emits results whenever the window contents change.
/// The window covers [eventTime - windowSize, eventTime] for each new event.
/// </summary>
public sealed class SlidingWindow : IWindowOperator
{
    private readonly TimeSpan _windowSize;
    private readonly string? _groupByKey;
    private readonly AggregationKind[] _aggregations;
    private readonly object _lock = new();

    // Per-group event lists for sliding window computation
    private readonly Dictionary<string, LinkedList<StreamEvent>> _eventLists = new();

    // Pending results that will be returned on next FireExpiredWindows call
    private readonly List<WindowResult> _pendingResults = [];

    /// <summary>Creates a sliding window operator.</summary>
    /// <param name="windowSize">The sliding window duration.</param>
    /// <param name="groupByKey">Optional GROUP BY key.</param>
    /// <param name="aggregations">Aggregation functions to apply.</param>
    public SlidingWindow(TimeSpan windowSize, string? groupByKey, AggregationKind[] aggregations)
    {
        _windowSize = windowSize;
        _groupByKey = groupByKey;
        _aggregations = aggregations;
    }

    /// <inheritdoc />
    public int ActiveWindowCount
    {
        get
        {
            lock (_lock) return _eventLists.Count;
        }
    }

    /// <inheritdoc />
    public void ProcessEvent(StreamEvent evt)
    {
        string groupKey = _groupByKey is not null ? evt.Key : "__global__";

        lock (_lock)
        {
            if (!_eventLists.TryGetValue(groupKey, out var events))
            {
                events = new LinkedList<StreamEvent>();
                _eventLists[groupKey] = events;
            }

            events.AddLast(evt);

            // Compute window: [eventTime - windowSize, eventTime]
            var windowStart = evt.EventTime - _windowSize;
            var windowEnd = evt.EventTime;

            // Re-aggregate all events in the window for this group.
            // NOTE: This is O(N) per event where N = events in the current window.
            // For high-throughput streams (finding P2-248), consider replacing with
            // an incremental aggregator that tracks running state and only adjusts
            // on add/expire rather than full recompute. Current implementation is
            // correct but has O(N²) total complexity for a window of N events.
            var aggregator = new WindowAggregator(_aggregations);
            foreach (var e in events)
            {
                if (e.EventTime >= windowStart && e.EventTime <= windowEnd)
                {
                    aggregator.Add(e, _groupByKey);
                }
            }

            _pendingResults.Add(new WindowResult(
                groupKey,
                windowStart,
                windowEnd,
                aggregator.GetResult()));
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<WindowResult> FireExpiredWindows(DateTimeOffset watermark)
    {
        lock (_lock)
        {
            // Purge events older than watermark - windowSize
            var cutoff = watermark - _windowSize;

            foreach (var kvp in _eventLists)
            {
                var events = kvp.Value;
                while (events.First is not null && events.First.Value.EventTime < cutoff)
                {
                    events.RemoveFirst();
                }
            }

            // Remove empty groups
            var emptyGroups = new List<string>();
            foreach (var kvp in _eventLists)
            {
                if (kvp.Value.Count == 0)
                {
                    emptyGroups.Add(kvp.Key);
                }
            }
            foreach (var key in emptyGroups)
            {
                _eventLists.Remove(key);
            }

            // Return accumulated pending results
            var results = _pendingResults.ToList();
            _pendingResults.Clear();
            return results;
        }
    }
}
