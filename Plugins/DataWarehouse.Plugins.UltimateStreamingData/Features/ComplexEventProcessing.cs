using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStreamingData.Features;

#region CEP Types

/// <summary>
/// Defines pattern types for complex event processing.
/// </summary>
public enum CepPatternType
{
    /// <summary>Events must occur in exact sequence order.</summary>
    Sequence,

    /// <summary>Events must all occur within the window regardless of order.</summary>
    Conjunction,

    /// <summary>At least one of the specified events must occur.</summary>
    Disjunction,

    /// <summary>An event pattern must NOT occur within the window.</summary>
    Negation,

    /// <summary>Events are aggregated over a window (count, sum, avg).</summary>
    Aggregation,

    /// <summary>Two event streams are joined on a shared key within a time window.</summary>
    TemporalJoin
}

/// <summary>
/// Aggregation functions for CEP aggregation patterns.
/// </summary>
public enum CepAggregateFunction
{
    /// <summary>Count of matching events.</summary>
    Count,

    /// <summary>Sum of a numeric field across events.</summary>
    Sum,

    /// <summary>Average of a numeric field across events.</summary>
    Average,

    /// <summary>Minimum value of a numeric field.</summary>
    Min,

    /// <summary>Maximum value of a numeric field.</summary>
    Max
}

/// <summary>
/// Defines a CEP event pattern to match against incoming streams.
/// </summary>
public sealed record CepPattern
{
    /// <summary>Gets the unique pattern identifier.</summary>
    public required string PatternId { get; init; }

    /// <summary>Gets the display name for this pattern.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the pattern type (sequence, conjunction, etc.).</summary>
    public required CepPatternType PatternType { get; init; }

    /// <summary>Gets the event type names that participate in this pattern.</summary>
    public required string[] EventTypes { get; init; }

    /// <summary>Gets the temporal constraint within which the pattern must complete.</summary>
    public required TimeSpan Window { get; init; }

    /// <summary>Gets optional filter predicates keyed by event type name.</summary>
    public Dictionary<string, Func<StreamEvent, bool>>? Filters { get; init; }

    /// <summary>Gets the join key extractor for temporal join patterns.</summary>
    public Func<StreamEvent, string>? JoinKeyExtractor { get; init; }

    /// <summary>Gets the aggregate function for aggregation patterns.</summary>
    public CepAggregateFunction? AggregateFunction { get; init; }

    /// <summary>Gets the numeric value extractor for aggregation patterns.</summary>
    public Func<StreamEvent, double>? ValueExtractor { get; init; }

    /// <summary>Gets the threshold for aggregate patterns (e.g., count > 10).</summary>
    public double? AggregateThreshold { get; init; }
}

/// <summary>
/// Result of a CEP pattern match containing the correlated events.
/// </summary>
public sealed record CepMatchResult
{
    /// <summary>Gets the pattern that was matched.</summary>
    public required string PatternId { get; init; }

    /// <summary>Gets the events that participated in the match.</summary>
    public required IReadOnlyList<StreamEvent> MatchedEvents { get; init; }

    /// <summary>Gets when the match was detected.</summary>
    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Gets the time span from first to last matched event.</summary>
    public TimeSpan MatchDuration { get; init; }

    /// <summary>Gets the aggregated value for aggregation patterns.</summary>
    public double? AggregateValue { get; init; }

    /// <summary>Gets the join key for temporal join matches.</summary>
    public string? JoinKey { get; init; }
}

#endregion

/// <summary>
/// Complex Event Processing (CEP) engine for detecting patterns across event streams.
/// Supports sequence detection, conjunctions, disjunctions, negation patterns,
/// aggregations, and temporal joins with configurable time windows.
/// </summary>
/// <remarks>
/// <b>MESSAGE BUS:</b> Publishes detected matches to "streaming.cep.match" topic.
/// Thread-safe for concurrent event ingestion and pattern evaluation.
/// </remarks>
internal sealed class ComplexEventProcessing : IDisposable
{
    private readonly BoundedDictionary<string, CepPattern> _patterns = new BoundedDictionary<string, CepPattern>(1000);
    private readonly BoundedDictionary<string, PatternState> _patternStates = new BoundedDictionary<string, PatternState>(1000);
    private readonly IMessageBus? _messageBus;
    private readonly TimeSpan _cleanupInterval;
    private readonly Timer _cleanupTimer;
    private long _totalEventsProcessed;
    private long _totalMatchesDetected;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ComplexEventProcessing"/> class.
    /// </summary>
    /// <param name="messageBus">Optional message bus for publishing match notifications.</param>
    /// <param name="cleanupInterval">Interval for cleaning expired window state. Defaults to 30 seconds.</param>
    public ComplexEventProcessing(IMessageBus? messageBus = null, TimeSpan? cleanupInterval = null)
    {
        _messageBus = messageBus;
        _cleanupInterval = cleanupInterval ?? TimeSpan.FromSeconds(30);
        _cleanupTimer = new Timer(CleanupExpiredState, null, _cleanupInterval, _cleanupInterval);
    }

    /// <summary>
    /// Gets the total number of events processed by the CEP engine.
    /// </summary>
    public long TotalEventsProcessed => Interlocked.Read(ref _totalEventsProcessed);

    /// <summary>
    /// Gets the total number of pattern matches detected.
    /// </summary>
    public long TotalMatchesDetected => Interlocked.Read(ref _totalMatchesDetected);

    /// <summary>
    /// Registers a pattern for detection.
    /// </summary>
    /// <param name="pattern">The CEP pattern to register.</param>
    /// <exception cref="ArgumentNullException">Thrown when pattern is null.</exception>
    /// <exception cref="ArgumentException">Thrown when pattern has invalid configuration.</exception>
    public void RegisterPattern(CepPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        if (string.IsNullOrWhiteSpace(pattern.PatternId))
            throw new ArgumentException("Pattern ID cannot be empty.", nameof(pattern));

        if (pattern.EventTypes == null || pattern.EventTypes.Length == 0)
            throw new ArgumentException("Pattern must specify at least one event type.", nameof(pattern));

        if (pattern.Window <= TimeSpan.Zero)
            throw new ArgumentException("Pattern window must be positive.", nameof(pattern));

        if (pattern.PatternType == CepPatternType.TemporalJoin && pattern.JoinKeyExtractor == null)
            throw new ArgumentException("Temporal join patterns require a JoinKeyExtractor.", nameof(pattern));

        if (pattern.PatternType == CepPatternType.Aggregation && pattern.ValueExtractor == null && pattern.AggregateFunction != CepAggregateFunction.Count)
            throw new ArgumentException("Aggregation patterns (non-count) require a ValueExtractor.", nameof(pattern));

        _patterns[pattern.PatternId] = pattern;
        _patternStates[pattern.PatternId] = new PatternState();
    }

    /// <summary>
    /// Removes a registered pattern.
    /// </summary>
    /// <param name="patternId">The pattern identifier to remove.</param>
    /// <returns>True if the pattern was removed; false if it was not found.</returns>
    public bool UnregisterPattern(string patternId)
    {
        _patternStates.TryRemove(patternId, out _);
        return _patterns.TryRemove(patternId, out _);
    }

    /// <summary>
    /// Processes an incoming event against all registered patterns.
    /// Returns any matches that were completed by this event.
    /// </summary>
    /// <param name="evt">The stream event to process.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of pattern matches triggered by this event.</returns>
    /// <exception cref="ArgumentNullException">Thrown when evt is null.</exception>
    public async Task<IReadOnlyList<CepMatchResult>> ProcessEventAsync(StreamEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        Interlocked.Increment(ref _totalEventsProcessed);

        var matches = new List<CepMatchResult>();
        var eventType = ExtractEventType(evt);

        foreach (var (patternId, pattern) in _patterns)
        {
            ct.ThrowIfCancellationRequested();

            if (!IsEventRelevant(pattern, eventType, evt))
                continue;

            if (!_patternStates.TryGetValue(patternId, out var state))
                continue;

            // Add event to the pattern state window
            state.AddEvent(eventType, evt, pattern.Window);

            // Evaluate pattern
            var match = EvaluatePattern(pattern, state);
            if (match != null)
            {
                matches.Add(match);
                Interlocked.Increment(ref _totalMatchesDetected);
                state.ClearMatched();

                // Publish match notification via message bus
                if (_messageBus != null)
                {
                    await PublishMatchAsync(match, ct);
                }
            }
        }

        return matches;
    }

    /// <summary>
    /// Processes a batch of events efficiently.
    /// </summary>
    /// <param name="events">The events to process.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All matches detected across the batch.</returns>
    public async Task<IReadOnlyList<CepMatchResult>> ProcessBatchAsync(
        IEnumerable<StreamEvent> events,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        var allMatches = new List<CepMatchResult>();
        foreach (var evt in events)
        {
            ct.ThrowIfCancellationRequested();
            var matches = await ProcessEventAsync(evt, ct);
            allMatches.AddRange(matches);
        }
        return allMatches;
    }

    /// <summary>
    /// Gets all registered patterns.
    /// </summary>
    /// <returns>A read-only collection of registered patterns.</returns>
    public IReadOnlyCollection<CepPattern> GetPatterns() => _patterns.Values.ToArray();

    private static string ExtractEventType(StreamEvent evt)
    {
        if (evt.Headers != null && evt.Headers.TryGetValue("EventType", out var type))
            return type;
        return evt.Key ?? "unknown";
    }

    private static bool IsEventRelevant(CepPattern pattern, string eventType, StreamEvent evt)
    {
        if (!pattern.EventTypes.Contains(eventType))
            return false;

        // Apply filter if defined
        if (pattern.Filters != null &&
            pattern.Filters.TryGetValue(eventType, out var filter) &&
            !filter(evt))
            return false;

        return true;
    }

    private static CepMatchResult? EvaluatePattern(CepPattern pattern, PatternState state)
    {
        return pattern.PatternType switch
        {
            CepPatternType.Sequence => EvaluateSequence(pattern, state),
            CepPatternType.Conjunction => EvaluateConjunction(pattern, state),
            CepPatternType.Disjunction => EvaluateDisjunction(pattern, state),
            CepPatternType.Negation => EvaluateNegation(pattern, state),
            CepPatternType.Aggregation => EvaluateAggregation(pattern, state),
            CepPatternType.TemporalJoin => EvaluateTemporalJoin(pattern, state),
            _ => null
        };
    }

    private static CepMatchResult? EvaluateSequence(CepPattern pattern, PatternState state)
    {
        // Check if we have events for each type in sequence order
        var matchedEvents = new List<StreamEvent>();
        var lastTimestamp = DateTimeOffset.MinValue;

        foreach (var eventType in pattern.EventTypes)
        {
            var events = state.GetEventsForType(eventType);
            var candidate = events.FirstOrDefault(e => e.Timestamp >= lastTimestamp);
            if (candidate == null) return null;

            matchedEvents.Add(candidate);
            lastTimestamp = candidate.Timestamp;
        }

        var first = matchedEvents.First().Timestamp;
        var last = matchedEvents.Last().Timestamp;
        var duration = last - first;

        if (duration > pattern.Window) return null;

        return new CepMatchResult
        {
            PatternId = pattern.PatternId,
            MatchedEvents = matchedEvents,
            MatchDuration = duration
        };
    }

    private static CepMatchResult? EvaluateConjunction(CepPattern pattern, PatternState state)
    {
        // All event types must be present within the window
        var matchedEvents = new List<StreamEvent>();
        foreach (var eventType in pattern.EventTypes)
        {
            var events = state.GetEventsForType(eventType);
            if (events.Count == 0) return null;
            matchedEvents.Add(events.Last());
        }

        var first = matchedEvents.Min(e => e.Timestamp);
        var last = matchedEvents.Max(e => e.Timestamp);

        return new CepMatchResult
        {
            PatternId = pattern.PatternId,
            MatchedEvents = matchedEvents,
            MatchDuration = last - first
        };
    }

    private static CepMatchResult? EvaluateDisjunction(CepPattern pattern, PatternState state)
    {
        // At least one event type must be present
        foreach (var eventType in pattern.EventTypes)
        {
            var events = state.GetEventsForType(eventType);
            if (events.Count > 0)
            {
                var latest = events.Last();
                return new CepMatchResult
                {
                    PatternId = pattern.PatternId,
                    MatchedEvents = new[] { latest },
                    MatchDuration = TimeSpan.Zero
                };
            }
        }
        return null;
    }

    private static CepMatchResult? EvaluateNegation(CepPattern pattern, PatternState state)
    {
        // First event type is the trigger, subsequent types must NOT occur
        if (pattern.EventTypes.Length < 2) return null;

        var triggerType = pattern.EventTypes[0];
        var triggerEvents = state.GetEventsForType(triggerType);
        if (triggerEvents.Count == 0) return null;

        // Check that negated types are absent
        for (int i = 1; i < pattern.EventTypes.Length; i++)
        {
            var negatedEvents = state.GetEventsForType(pattern.EventTypes[i]);
            if (negatedEvents.Count > 0) return null;
        }

        // The window has passed and negated events did not occur
        var trigger = triggerEvents.Last();
        var elapsed = DateTimeOffset.UtcNow - trigger.Timestamp;
        if (elapsed < pattern.Window) return null; // Window not yet expired

        return new CepMatchResult
        {
            PatternId = pattern.PatternId,
            MatchedEvents = new[] { trigger },
            MatchDuration = elapsed
        };
    }

    private static CepMatchResult? EvaluateAggregation(CepPattern pattern, PatternState state)
    {
        if (pattern.AggregateThreshold == null) return null;

        var allEvents = new List<StreamEvent>();
        foreach (var eventType in pattern.EventTypes)
        {
            allEvents.AddRange(state.GetEventsForType(eventType));
        }

        if (allEvents.Count == 0) return null;

        double aggregateValue = pattern.AggregateFunction switch
        {
            CepAggregateFunction.Count => allEvents.Count,
            CepAggregateFunction.Sum => allEvents.Sum(e => pattern.ValueExtractor!(e)),
            CepAggregateFunction.Average => allEvents.Average(e => pattern.ValueExtractor!(e)),
            CepAggregateFunction.Min => allEvents.Min(e => pattern.ValueExtractor!(e)),
            CepAggregateFunction.Max => allEvents.Max(e => pattern.ValueExtractor!(e)),
            _ => 0
        };

        if (aggregateValue < pattern.AggregateThreshold.Value) return null;

        return new CepMatchResult
        {
            PatternId = pattern.PatternId,
            MatchedEvents = allEvents,
            AggregateValue = aggregateValue,
            MatchDuration = allEvents.Count > 1
                ? allEvents.Max(e => e.Timestamp) - allEvents.Min(e => e.Timestamp)
                : TimeSpan.Zero
        };
    }

    private static CepMatchResult? EvaluateTemporalJoin(CepPattern pattern, PatternState state)
    {
        if (pattern.EventTypes.Length < 2 || pattern.JoinKeyExtractor == null) return null;

        var leftType = pattern.EventTypes[0];
        var rightType = pattern.EventTypes[1];

        var leftEvents = state.GetEventsForType(leftType);
        var rightEvents = state.GetEventsForType(rightType);

        if (leftEvents.Count == 0 || rightEvents.Count == 0) return null;

        // Group by join key and find matches
        var leftByKey = leftEvents.GroupBy(e => pattern.JoinKeyExtractor(e));

        foreach (var group in leftByKey)
        {
            var key = group.Key;
            var matchingRight = rightEvents
                .Where(r => pattern.JoinKeyExtractor(r) == key)
                .ToList();

            if (matchingRight.Count > 0)
            {
                var leftEvent = group.Last();
                var rightEvent = matchingRight.Last();
                return new CepMatchResult
                {
                    PatternId = pattern.PatternId,
                    MatchedEvents = new[] { leftEvent, rightEvent },
                    JoinKey = key,
                    MatchDuration = (leftEvent.Timestamp - rightEvent.Timestamp).Duration()
                };
            }
        }

        return null;
    }

    private async Task PublishMatchAsync(CepMatchResult match, CancellationToken ct)
    {
        if (_messageBus == null) return;

        var message = new PluginMessage
        {
            Type = "cep.match.detected",
            SourcePluginId = "com.datawarehouse.streaming.ultimate",
            Source = "ComplexEventProcessing",
            Payload = new Dictionary<string, object>
            {
                ["PatternId"] = match.PatternId,
                ["MatchedEventCount"] = match.MatchedEvents.Count,
                ["DetectedAt"] = match.DetectedAt.ToString("O"),
                ["MatchDuration"] = match.MatchDuration.TotalMilliseconds,
                ["AggregateValue"] = match.AggregateValue ?? 0.0,
                ["JoinKey"] = match.JoinKey ?? string.Empty
            }
        };

        try
        {
            await _messageBus.PublishAsync("streaming.cep.match", message, ct);
        }
        catch (Exception)
        {
            // Non-critical: match notification failure does not affect CEP processing
        }
    }

    private void CleanupExpiredState(object? state)
    {
        foreach (var (patternId, pattern) in _patterns)
        {
            if (_patternStates.TryGetValue(patternId, out var patternState))
            {
                patternState.PurgeExpired(pattern.Window);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cleanupTimer.Dispose();
    }

    /// <summary>
    /// Maintains windowed event state per pattern.
    /// </summary>
    private sealed class PatternState
    {
        private readonly BoundedDictionary<string, ConcurrentBag<TimestampedEvent>> _eventsByType = new BoundedDictionary<string, ConcurrentBag<TimestampedEvent>>(1000);
        private readonly object _lock = new();

        public void AddEvent(string eventType, StreamEvent evt, TimeSpan window)
        {
            var bag = _eventsByType.GetOrAdd(eventType, _ => new ConcurrentBag<TimestampedEvent>());
            bag.Add(new TimestampedEvent(evt, DateTimeOffset.UtcNow));
        }

        public IReadOnlyList<StreamEvent> GetEventsForType(string eventType)
        {
            if (!_eventsByType.TryGetValue(eventType, out var bag))
                return Array.Empty<StreamEvent>();

            return bag.Select(te => te.Event).ToArray();
        }

        public void ClearMatched()
        {
            lock (_lock)
            {
                _eventsByType.Clear();
            }
        }

        public void PurgeExpired(TimeSpan window)
        {
            var cutoff = DateTimeOffset.UtcNow - window;
            foreach (var (eventType, bag) in _eventsByType)
            {
                var remaining = bag.Where(te => te.AddedAt >= cutoff).ToArray();
                if (remaining.Length < bag.Count)
                {
                    var newBag = new ConcurrentBag<TimestampedEvent>(remaining);
                    _eventsByType[eventType] = newBag;
                }
            }
        }

        private readonly record struct TimestampedEvent(StreamEvent Event, DateTimeOffset AddedAt);
    }
}
