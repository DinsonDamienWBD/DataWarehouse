using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStreamingData.Strategies.Windowing;

#region 111.5.1 Tumbling Window Strategy

/// <summary>
/// 111.5.1: Tumbling (fixed) window strategy for non-overlapping time-based windows.
/// Windows are aligned to epoch and do not overlap.
/// </summary>
public sealed class TumblingWindowStrategy : StreamingDataStrategyBase
{
    private readonly BoundedDictionary<string, WindowState> _windowStates = new BoundedDictionary<string, WindowState>(1000);

    public override string StrategyId => "windowing-tumbling";
    public override string DisplayName => "Tumbling Window";
    public override StreamingCategory Category => StreamingCategory.StreamWindowing;
    public override StreamingDataCapabilities Capabilities => new()
    {
        SupportsExactlyOnce = true,
        SupportsWindowing = true,
        SupportsStateManagement = true,
        SupportsCheckpointing = true,
        SupportsBackpressure = true,
        SupportsPartitioning = true,
        SupportsAutoScaling = true,
        SupportsDistributed = true,
        MaxThroughputEventsPerSec = 1000000,
        TypicalLatencyMs = 1.0
    };
    public override string SemanticDescription =>
        "Tumbling window strategy providing fixed-size, non-overlapping windows aligned to epoch. " +
        "Ideal for periodic aggregations like per-minute counts or hourly summaries.";
    public override string[] Tags => ["tumbling", "fixed-window", "non-overlapping", "aggregation", "periodic"];

    /// <summary>
    /// Creates a tumbling window specification.
    /// </summary>
    public TumblingWindowSpec CreateWindow(TimeSpan size, TimeSpan? allowedLateness = null)
    {
        return new TumblingWindowSpec
        {
            Size = size,
            AllowedLateness = allowedLateness ?? TimeSpan.Zero
        };
    }

    /// <summary>
    /// Assigns an event to its window.
    /// </summary>
    public WindowAssignment AssignWindow(TumblingWindowSpec spec, DateTimeOffset eventTime)
    {
        var windowStart = new DateTimeOffset(
            eventTime.Ticks - (eventTime.Ticks % spec.Size.Ticks),
            eventTime.Offset);
        var windowEnd = windowStart + spec.Size;

        return new WindowAssignment
        {
            WindowId = $"tumbling-{windowStart.ToUnixTimeMilliseconds()}",
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            EventTime = eventTime
        };
    }

    /// <summary>
    /// Processes events through a tumbling window with aggregation.
    /// </summary>
    public async IAsyncEnumerable<WindowResult<TResult>> ProcessAsync<TEvent, TResult>(
        IAsyncEnumerable<TEvent> events,
        TumblingWindowSpec spec,
        Func<TEvent, DateTimeOffset> eventTimeExtractor,
        Func<IEnumerable<TEvent>, TResult> aggregator,
        [EnumeratorCancellation] CancellationToken ct = default) where TEvent : class
    {
        ThrowIfNotInitialized();

        var windowBuffers = new BoundedDictionary<string, List<TEvent>>(1000);
        var watermark = DateTimeOffset.MinValue;

        await foreach (var evt in events.WithCancellation(ct))
        {
            var eventTime = eventTimeExtractor(evt);
            var assignment = AssignWindow(spec, eventTime);

            // Add to window buffer
            var buffer = windowBuffers.GetOrAdd(assignment.WindowId, _ => new List<TEvent>());
            lock (buffer)
            {
                buffer.Add(evt);
            }

            // Update watermark (simplified - in production use proper watermark generation)
            if (eventTime > watermark)
            {
                watermark = eventTime;
            }

            // Check if any windows should be closed
            foreach (var (windowId, windowBuffer) in windowBuffers.ToArray())
            {
                var windowEnd = ParseWindowEnd(windowId, spec.Size);
                if (watermark >= windowEnd + spec.AllowedLateness)
                {
                    List<TEvent> closedEvents;
                    lock (windowBuffer)
                    {
                        closedEvents = windowBuffer.ToList();
                    }
                    windowBuffers.TryRemove(windowId, out _);

                    var result = aggregator(closedEvents);
                    yield return new WindowResult<TResult>
                    {
                        WindowId = windowId,
                        WindowStart = windowEnd - spec.Size,
                        WindowEnd = windowEnd,
                        Result = result,
                        EventCount = closedEvents.Count,
                        EmittedAt = DateTimeOffset.UtcNow
                    };
                }
            }
        }

        // Emit remaining windows
        foreach (var (windowId, windowBuffer) in windowBuffers)
        {
            List<TEvent> remainingEvents;
            lock (windowBuffer)
            {
                remainingEvents = windowBuffer.ToList();
            }

            if (remainingEvents.Count > 0)
            {
                var windowEnd = ParseWindowEnd(windowId, spec.Size);
                var result = aggregator(remainingEvents);
                yield return new WindowResult<TResult>
                {
                    WindowId = windowId,
                    WindowStart = windowEnd - spec.Size,
                    WindowEnd = windowEnd,
                    Result = result,
                    EventCount = remainingEvents.Count,
                    EmittedAt = DateTimeOffset.UtcNow
                };
            }
        }
    }

    private static DateTimeOffset ParseWindowEnd(string windowId, TimeSpan size)
    {
        var startMs = long.Parse(windowId.Replace("tumbling-", ""));
        return DateTimeOffset.FromUnixTimeMilliseconds(startMs) + size;
    }
}

/// <summary>
/// Tumbling window specification.
/// </summary>
public sealed record TumblingWindowSpec
{
    public TimeSpan Size { get; init; }
    public TimeSpan AllowedLateness { get; init; }
}

#endregion

#region 111.5.2 Sliding Window Strategy

/// <summary>
/// 111.5.2: Sliding window strategy for overlapping time-based windows.
/// Windows slide at a specified interval, creating overlapping groups.
/// </summary>
public sealed class SlidingWindowStrategy : StreamingDataStrategyBase
{
    public override string StrategyId => "windowing-sliding";
    public override string DisplayName => "Sliding Window";
    public override StreamingCategory Category => StreamingCategory.StreamWindowing;
    public override StreamingDataCapabilities Capabilities => new()
    {
        SupportsExactlyOnce = true,
        SupportsWindowing = true,
        SupportsStateManagement = true,
        SupportsCheckpointing = true,
        SupportsBackpressure = true,
        SupportsPartitioning = true,
        SupportsAutoScaling = true,
        SupportsDistributed = true,
        MaxThroughputEventsPerSec = 500000,
        TypicalLatencyMs = 2.0
    };
    public override string SemanticDescription =>
        "Sliding window strategy providing overlapping windows that slide at a specified interval. " +
        "Ideal for moving averages, trend detection, and continuous monitoring.";
    public override string[] Tags => ["sliding", "overlapping", "moving-average", "trend", "continuous"];

    /// <summary>
    /// Creates a sliding window specification.
    /// </summary>
    public SlidingWindowSpec CreateWindow(TimeSpan size, TimeSpan slide, TimeSpan? allowedLateness = null)
    {
        if (slide > size)
            throw new ArgumentException("Slide interval cannot be greater than window size");

        return new SlidingWindowSpec
        {
            Size = size,
            Slide = slide,
            AllowedLateness = allowedLateness ?? TimeSpan.Zero
        };
    }

    /// <summary>
    /// Assigns an event to all windows it belongs to.
    /// </summary>
    public IReadOnlyList<WindowAssignment> AssignWindows(SlidingWindowSpec spec, DateTimeOffset eventTime)
    {
        var assignments = new List<WindowAssignment>();

        // Calculate the first window that contains this event.
        // Finding 4361: clamp to DateTimeOffset.MinValue to prevent negative-tick underflow when
        // Size > Slide and eventTime is near epoch (small Ticks value).
        var rawFirstTicks = eventTime.Ticks - (eventTime.Ticks % spec.Slide.Ticks) - spec.Size.Ticks + spec.Slide.Ticks;
        var firstTicks = Math.Max(rawFirstTicks, DateTimeOffset.MinValue.Ticks);
        var firstWindowStart = new DateTimeOffset(firstTicks, eventTime.Offset);

        // Find all windows containing this event
        for (var windowStart = firstWindowStart; windowStart <= eventTime; windowStart += spec.Slide)
        {
            var windowEnd = windowStart + spec.Size;
            if (eventTime >= windowStart && eventTime < windowEnd)
            {
                assignments.Add(new WindowAssignment
                {
                    WindowId = $"sliding-{windowStart.ToUnixTimeMilliseconds()}-{spec.Size.TotalMilliseconds}",
                    WindowStart = windowStart,
                    WindowEnd = windowEnd,
                    EventTime = eventTime
                });
            }
        }

        return assignments;
    }

    /// <summary>
    /// Processes events through sliding windows with aggregation.
    /// </summary>
    public async IAsyncEnumerable<WindowResult<TResult>> ProcessAsync<TEvent, TResult>(
        IAsyncEnumerable<TEvent> events,
        SlidingWindowSpec spec,
        Func<TEvent, DateTimeOffset> eventTimeExtractor,
        Func<IEnumerable<TEvent>, TResult> aggregator,
        [EnumeratorCancellation] CancellationToken ct = default) where TEvent : class
    {
        ThrowIfNotInitialized();

        var windowBuffers = new BoundedDictionary<string, List<TEvent>>(1000);
        var watermark = DateTimeOffset.MinValue;

        await foreach (var evt in events.WithCancellation(ct))
        {
            var eventTime = eventTimeExtractor(evt);
            var assignments = AssignWindows(spec, eventTime);

            // Add to all applicable window buffers
            foreach (var assignment in assignments)
            {
                var buffer = windowBuffers.GetOrAdd(assignment.WindowId, _ => new List<TEvent>());
                lock (buffer)
                {
                    buffer.Add(evt);
                }
            }

            // Update watermark
            if (eventTime > watermark)
            {
                watermark = eventTime;
            }

            // Check for closeable windows
            foreach (var (windowId, windowBuffer) in windowBuffers.ToArray())
            {
                var windowEnd = ParseWindowEnd(windowId);
                if (watermark >= windowEnd + spec.AllowedLateness)
                {
                    List<TEvent> closedEvents;
                    lock (windowBuffer)
                    {
                        closedEvents = windowBuffer.ToList();
                    }
                    windowBuffers.TryRemove(windowId, out _);

                    var windowStart = ParseWindowStart(windowId);
                    var result = aggregator(closedEvents);
                    yield return new WindowResult<TResult>
                    {
                        WindowId = windowId,
                        WindowStart = windowStart,
                        WindowEnd = windowEnd,
                        Result = result,
                        EventCount = closedEvents.Count,
                        EmittedAt = DateTimeOffset.UtcNow
                    };
                }
            }
        }

        // Emit remaining windows
        foreach (var (windowId, windowBuffer) in windowBuffers)
        {
            List<TEvent> remainingEvents;
            lock (windowBuffer)
            {
                remainingEvents = windowBuffer.ToList();
            }

            if (remainingEvents.Count > 0)
            {
                var windowStart = ParseWindowStart(windowId);
                var windowEnd = ParseWindowEnd(windowId);
                var result = aggregator(remainingEvents);
                yield return new WindowResult<TResult>
                {
                    WindowId = windowId,
                    WindowStart = windowStart,
                    WindowEnd = windowEnd,
                    Result = result,
                    EventCount = remainingEvents.Count,
                    EmittedAt = DateTimeOffset.UtcNow
                };
            }
        }
    }

    private static DateTimeOffset ParseWindowStart(string windowId)
    {
        var parts = windowId.Replace("sliding-", "").Split('-');
        return DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(parts[0]));
    }

    private static DateTimeOffset ParseWindowEnd(string windowId)
    {
        var parts = windowId.Replace("sliding-", "").Split('-');
        var startMs = long.Parse(parts[0]);
        var sizeMs = long.Parse(parts[1]);
        return DateTimeOffset.FromUnixTimeMilliseconds(startMs + (long)sizeMs);
    }
}

/// <summary>
/// Sliding window specification.
/// </summary>
public sealed record SlidingWindowSpec
{
    public TimeSpan Size { get; init; }
    public TimeSpan Slide { get; init; }
    public TimeSpan AllowedLateness { get; init; }
}

#endregion

#region 111.5.3 Session Window Strategy

/// <summary>
/// 111.5.3: Session window strategy for activity-based windowing.
/// Windows are created based on gaps in event activity.
/// </summary>
public sealed class SessionWindowStrategy : StreamingDataStrategyBase
{
    public override string StrategyId => "windowing-session";
    public override string DisplayName => "Session Window";
    public override StreamingCategory Category => StreamingCategory.StreamWindowing;
    public override StreamingDataCapabilities Capabilities => new()
    {
        SupportsExactlyOnce = true,
        SupportsWindowing = true,
        SupportsStateManagement = true,
        SupportsCheckpointing = true,
        SupportsBackpressure = true,
        SupportsPartitioning = true,
        SupportsAutoScaling = true,
        SupportsDistributed = true,
        MaxThroughputEventsPerSec = 300000,
        TypicalLatencyMs = 5.0
    };
    public override string SemanticDescription =>
        "Session window strategy providing activity-based windowing where windows close after a gap " +
        "of inactivity. Ideal for user sessions, clickstreams, and activity tracking.";
    public override string[] Tags => ["session", "activity", "gap", "user-session", "clickstream"];

    /// <summary>
    /// Creates a session window specification.
    /// </summary>
    public SessionWindowSpec CreateWindow(TimeSpan gap, TimeSpan? maxDuration = null, TimeSpan? allowedLateness = null)
    {
        return new SessionWindowSpec
        {
            Gap = gap,
            MaxDuration = maxDuration,
            AllowedLateness = allowedLateness ?? TimeSpan.Zero
        };
    }

    /// <summary>
    /// Processes events through session windows with aggregation.
    /// </summary>
    public async IAsyncEnumerable<WindowResult<TResult>> ProcessAsync<TEvent, TKey, TResult>(
        IAsyncEnumerable<TEvent> events,
        SessionWindowSpec spec,
        Func<TEvent, TKey> keyExtractor,
        Func<TEvent, DateTimeOffset> eventTimeExtractor,
        Func<IEnumerable<TEvent>, TResult> aggregator,
        [EnumeratorCancellation] CancellationToken ct = default)
        where TEvent : class
        where TKey : notnull
    {
        ThrowIfNotInitialized();

        var sessions = new BoundedDictionary<TKey, SessionState<TEvent>>(1000);
        var watermark = DateTimeOffset.MinValue;

        await foreach (var evt in events.WithCancellation(ct))
        {
            var key = keyExtractor(evt);
            var eventTime = eventTimeExtractor(evt);

            var session = sessions.GetOrAdd(key, _ => new SessionState<TEvent>
            {
                SessionId = Guid.NewGuid().ToString("N"),
                Key = key,
                Events = new List<TEvent>(),
                StartTime = eventTime,
                LastEventTime = eventTime
            });

            // Check if this event extends the session or starts a new one
            if (eventTime - session.LastEventTime > spec.Gap)
            {
                // Close current session and start new one
                var closedSession = session;
                session = new SessionState<TEvent>
                {
                    SessionId = Guid.NewGuid().ToString("N"),
                    Key = key,
                    Events = new List<TEvent>(),
                    StartTime = eventTime,
                    LastEventTime = eventTime
                };
                sessions[key] = session;

                // Emit closed session
                var result = aggregator(closedSession.Events);
                yield return new WindowResult<TResult>
                {
                    WindowId = $"session-{closedSession.SessionId}",
                    WindowStart = closedSession.StartTime,
                    WindowEnd = closedSession.LastEventTime,
                    Result = result,
                    EventCount = closedSession.Events.Count,
                    EmittedAt = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, object> { ["key"] = key! }
                };
            }

            // Add event to session
            lock (session.Events)
            {
                session.Events.Add(evt);
            }
            session.LastEventTime = eventTime > session.LastEventTime ? eventTime : session.LastEventTime;

            // Check max duration
            if (spec.MaxDuration.HasValue && (session.LastEventTime - session.StartTime) >= spec.MaxDuration.Value)
            {
                var closedSession = session;
                sessions.TryRemove(key, out _);

                var result = aggregator(closedSession.Events);
                yield return new WindowResult<TResult>
                {
                    WindowId = $"session-{closedSession.SessionId}",
                    WindowStart = closedSession.StartTime,
                    WindowEnd = closedSession.LastEventTime,
                    Result = result,
                    EventCount = closedSession.Events.Count,
                    EmittedAt = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, object> { ["key"] = key!, ["closedBy"] = "maxDuration" }
                };
            }

            // Update watermark
            if (eventTime > watermark)
            {
                watermark = eventTime;
            }

            // Check for timed-out sessions
            foreach (var (sessKey, sess) in sessions.ToArray())
            {
                if (watermark - sess.LastEventTime >= spec.Gap + spec.AllowedLateness)
                {
                    sessions.TryRemove(sessKey, out _);

                    var result = aggregator(sess.Events);
                    yield return new WindowResult<TResult>
                    {
                        WindowId = $"session-{sess.SessionId}",
                        WindowStart = sess.StartTime,
                        WindowEnd = sess.LastEventTime,
                        Result = result,
                        EventCount = sess.Events.Count,
                        EmittedAt = DateTimeOffset.UtcNow,
                        Metadata = new Dictionary<string, object> { ["key"] = sessKey! }
                    };
                }
            }
        }

        // Emit remaining sessions
        foreach (var (key, session) in sessions)
        {
            if (session.Events.Count > 0)
            {
                var result = aggregator(session.Events);
                yield return new WindowResult<TResult>
                {
                    WindowId = $"session-{session.SessionId}",
                    WindowStart = session.StartTime,
                    WindowEnd = session.LastEventTime,
                    Result = result,
                    EventCount = session.Events.Count,
                    EmittedAt = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, object> { ["key"] = key! }
                };
            }
        }
    }
}

/// <summary>
/// Session window specification.
/// </summary>
public sealed record SessionWindowSpec
{
    public TimeSpan Gap { get; init; }
    public TimeSpan? MaxDuration { get; init; }
    public TimeSpan AllowedLateness { get; init; }
}

internal sealed class SessionState<TEvent>
{
    public required string SessionId { get; init; }
    public required object Key { get; init; }
    public required List<TEvent> Events { get; init; }
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset LastEventTime { get; set; }
}

#endregion

#region 111.5.4 Global Window Strategy

/// <summary>
/// 111.5.4: Global window strategy for unbounded windows with custom triggers.
/// All events belong to a single global window per key.
/// </summary>
public sealed class GlobalWindowStrategy : StreamingDataStrategyBase
{
    public override string StrategyId => "windowing-global";
    public override string DisplayName => "Global Window";
    public override StreamingCategory Category => StreamingCategory.StreamWindowing;
    public override StreamingDataCapabilities Capabilities => new()
    {
        SupportsExactlyOnce = true,
        SupportsWindowing = true,
        SupportsStateManagement = true,
        SupportsCheckpointing = true,
        SupportsBackpressure = true,
        SupportsPartitioning = true,
        SupportsAutoScaling = true,
        SupportsDistributed = true,
        MaxThroughputEventsPerSec = 500000,
        TypicalLatencyMs = 1.0
    };
    public override string SemanticDescription =>
        "Global window strategy providing unbounded windows with custom triggers. " +
        "All events belong to a single global window, triggered by count, time, or custom conditions.";
    public override string[] Tags => ["global", "unbounded", "trigger", "custom", "accumulator"];

    /// <summary>
    /// Creates a global window specification with a count trigger.
    /// </summary>
    public GlobalWindowSpec CreateCountTriggeredWindow(int triggerCount)
    {
        return new GlobalWindowSpec
        {
            TriggerType = GlobalTriggerType.Count,
            TriggerCount = triggerCount
        };
    }

    /// <summary>
    /// Creates a global window specification with a time trigger.
    /// </summary>
    public GlobalWindowSpec CreateTimeTriggeredWindow(TimeSpan triggerInterval)
    {
        return new GlobalWindowSpec
        {
            TriggerType = GlobalTriggerType.ProcessingTime,
            TriggerInterval = triggerInterval
        };
    }

    /// <summary>
    /// Processes events through a global window with triggers.
    /// </summary>
    public async IAsyncEnumerable<WindowResult<TResult>> ProcessAsync<TEvent, TKey, TResult>(
        IAsyncEnumerable<TEvent> events,
        GlobalWindowSpec spec,
        Func<TEvent, TKey> keyExtractor,
        Func<IEnumerable<TEvent>, TResult> aggregator,
        [EnumeratorCancellation] CancellationToken ct = default)
        where TEvent : class
        where TKey : notnull
    {
        ThrowIfNotInitialized();

        var globalBuffers = new BoundedDictionary<TKey, GlobalWindowState<TEvent>>(1000);
        var triggerCount = 0L;

        await foreach (var evt in events.WithCancellation(ct))
        {
            var key = keyExtractor(evt);
            var state = globalBuffers.GetOrAdd(key, _ => new GlobalWindowState<TEvent>
            {
                Key = key,
                Events = new List<TEvent>(),
                WindowNumber = 0,
                LastTriggerTime = DateTimeOffset.UtcNow
            });

            lock (state.Events)
            {
                state.Events.Add(evt);
            }

            triggerCount++;

            // Check trigger conditions
            var shouldTrigger = spec.TriggerType switch
            {
                GlobalTriggerType.Count => state.Events.Count >= spec.TriggerCount,
                GlobalTriggerType.ProcessingTime =>
                    (DateTimeOffset.UtcNow - state.LastTriggerTime) >= spec.TriggerInterval,
                GlobalTriggerType.CountOrTime =>
                    state.Events.Count >= spec.TriggerCount ||
                    (DateTimeOffset.UtcNow - state.LastTriggerTime) >= spec.TriggerInterval,
                _ => false
            };

            if (shouldTrigger)
            {
                List<TEvent> triggeredEvents;
                lock (state.Events)
                {
                    triggeredEvents = state.Events.ToList();
                    state.Events.Clear();
                }
                state.WindowNumber++;
                state.LastTriggerTime = DateTimeOffset.UtcNow;

                var result = aggregator(triggeredEvents);
                yield return new WindowResult<TResult>
                {
                    WindowId = $"global-{key}-{state.WindowNumber}",
                    WindowStart = DateTimeOffset.MinValue,
                    WindowEnd = DateTimeOffset.MaxValue,
                    Result = result,
                    EventCount = triggeredEvents.Count,
                    EmittedAt = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, object>
                    {
                        ["key"] = key!,
                        ["windowNumber"] = state.WindowNumber,
                        ["triggerType"] = spec.TriggerType.ToString()
                    }
                };
            }
        }

        // Emit remaining events
        foreach (var (key, state) in globalBuffers)
        {
            if (state.Events.Count > 0)
            {
                state.WindowNumber++;
                var result = aggregator(state.Events);
                yield return new WindowResult<TResult>
                {
                    WindowId = $"global-{key}-{state.WindowNumber}",
                    WindowStart = DateTimeOffset.MinValue,
                    WindowEnd = DateTimeOffset.MaxValue,
                    Result = result,
                    EventCount = state.Events.Count,
                    EmittedAt = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, object>
                    {
                        ["key"] = key!,
                        ["windowNumber"] = state.WindowNumber,
                        ["final"] = true
                    }
                };
            }
        }
    }
}

/// <summary>
/// Global window specification.
/// </summary>
public sealed record GlobalWindowSpec
{
    public GlobalTriggerType TriggerType { get; init; }
    public int TriggerCount { get; init; }
    public TimeSpan? TriggerInterval { get; init; }
}

public enum GlobalTriggerType { Count, ProcessingTime, EventTime, CountOrTime, Custom }

internal sealed class GlobalWindowState<TEvent>
{
    public required object Key { get; init; }
    public required List<TEvent> Events { get; init; }
    public int WindowNumber { get; set; }
    public DateTimeOffset LastTriggerTime { get; set; }
}

#endregion

#region 111.5.5 Count-Based Window Strategy

/// <summary>
/// 111.5.5: Count-based window strategy for fixed element count windows.
/// Windows close after accumulating a specified number of events.
/// </summary>
public sealed class CountWindowStrategy : StreamingDataStrategyBase
{
    public override string StrategyId => "windowing-count";
    public override string DisplayName => "Count-Based Window";
    public override StreamingCategory Category => StreamingCategory.StreamWindowing;
    public override StreamingDataCapabilities Capabilities => new()
    {
        SupportsExactlyOnce = true,
        SupportsWindowing = true,
        SupportsStateManagement = true,
        SupportsCheckpointing = true,
        SupportsBackpressure = true,
        SupportsPartitioning = true,
        SupportsAutoScaling = true,
        SupportsDistributed = true,
        MaxThroughputEventsPerSec = 800000,
        TypicalLatencyMs = 0.5
    };
    public override string SemanticDescription =>
        "Count-based window strategy providing windows that close after accumulating a specified " +
        "number of events. Ideal for batch processing and fixed-size micro-batches.";
    public override string[] Tags => ["count", "fixed-count", "batch", "micro-batch", "elements"];

    /// <summary>
    /// Creates a count window specification.
    /// </summary>
    public CountWindowSpec CreateWindow(int count, int? slide = null)
    {
        return new CountWindowSpec
        {
            Count = count,
            Slide = slide ?? count
        };
    }

    /// <summary>
    /// Processes events through count-based windows.
    /// </summary>
    public async IAsyncEnumerable<WindowResult<TResult>> ProcessAsync<TEvent, TKey, TResult>(
        IAsyncEnumerable<TEvent> events,
        CountWindowSpec spec,
        Func<TEvent, TKey> keyExtractor,
        Func<IEnumerable<TEvent>, TResult> aggregator,
        [EnumeratorCancellation] CancellationToken ct = default)
        where TEvent : class
        where TKey : notnull
    {
        ThrowIfNotInitialized();

        var buffers = new BoundedDictionary<TKey, CountWindowState<TEvent>>(1000);

        await foreach (var evt in events.WithCancellation(ct))
        {
            var key = keyExtractor(evt);
            var state = buffers.GetOrAdd(key, _ => new CountWindowState<TEvent>
            {
                Key = key,
                Events = new List<TEvent>(),
                WindowNumber = 0
            });

            lock (state.Events)
            {
                state.Events.Add(evt);
            }

            // Check if window should close
            if (state.Events.Count >= spec.Count)
            {
                List<TEvent> windowEvents;
                lock (state.Events)
                {
                    windowEvents = state.Events.Take(spec.Count).ToList();
                    // Slide: remove the first 'slide' elements
                    state.Events.RemoveRange(0, Math.Min(spec.Slide, state.Events.Count));
                }
                state.WindowNumber++;

                var result = aggregator(windowEvents);
                yield return new WindowResult<TResult>
                {
                    WindowId = $"count-{key}-{state.WindowNumber}",
                    WindowStart = DateTimeOffset.MinValue,
                    WindowEnd = DateTimeOffset.MaxValue,
                    Result = result,
                    EventCount = windowEvents.Count,
                    EmittedAt = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, object>
                    {
                        ["key"] = key!,
                        ["windowNumber"] = state.WindowNumber
                    }
                };
            }
        }

        // Emit remaining partial windows
        foreach (var (key, state) in buffers)
        {
            if (state.Events.Count > 0)
            {
                state.WindowNumber++;
                var result = aggregator(state.Events);
                yield return new WindowResult<TResult>
                {
                    WindowId = $"count-{key}-{state.WindowNumber}",
                    WindowStart = DateTimeOffset.MinValue,
                    WindowEnd = DateTimeOffset.MaxValue,
                    Result = result,
                    EventCount = state.Events.Count,
                    EmittedAt = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, object>
                    {
                        ["key"] = key!,
                        ["windowNumber"] = state.WindowNumber,
                        ["partial"] = true
                    }
                };
            }
        }
    }
}

/// <summary>
/// Count window specification.
/// </summary>
public sealed record CountWindowSpec
{
    public int Count { get; init; }
    public int Slide { get; init; }
}

internal sealed class CountWindowState<TEvent>
{
    public required object Key { get; init; }
    public required List<TEvent> Events { get; init; }
    public int WindowNumber { get; set; }
}

#endregion

#region Common Window Types

/// <summary>
/// Window assignment result.
/// </summary>
public sealed record WindowAssignment
{
    public required string WindowId { get; init; }
    public DateTimeOffset WindowStart { get; init; }
    public DateTimeOffset WindowEnd { get; init; }
    public DateTimeOffset EventTime { get; init; }
}

/// <summary>
/// Window aggregation result.
/// </summary>
public sealed record WindowResult<TResult>
{
    public required string WindowId { get; init; }
    public DateTimeOffset WindowStart { get; init; }
    public DateTimeOffset WindowEnd { get; init; }
    public required TResult Result { get; init; }
    public int EventCount { get; init; }
    public DateTimeOffset EmittedAt { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Window state management helper.
/// </summary>
internal sealed class WindowState
{
    public required string WindowId { get; init; }
    public DateTimeOffset WindowStart { get; init; }
    public DateTimeOffset WindowEnd { get; init; }
    public List<object> Events { get; } = new();
    public bool IsClosed { get; set; }
}

#endregion
