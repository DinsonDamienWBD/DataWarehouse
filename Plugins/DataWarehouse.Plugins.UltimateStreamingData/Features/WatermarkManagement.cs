using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStreamingData.Features;

#region Watermark Types

/// <summary>
/// Watermark generation strategy.
/// </summary>
public enum WatermarkStrategy
{
    /// <summary>Watermark tracks the maximum observed event time minus a fixed delay.</summary>
    BoundedOutOfOrderness,

    /// <summary>Watermark follows processing time (system clock).</summary>
    ProcessingTime,

    /// <summary>Watermark is generated periodically based on observed event-time progress.</summary>
    Periodic,

    /// <summary>Watermark is punctuated by special marker events in the stream.</summary>
    Punctuated
}

/// <summary>
/// Window trigger strategy determining when window results are emitted.
/// </summary>
public enum WindowTriggerStrategy
{
    /// <summary>Emit results when the watermark passes the window end time.</summary>
    OnWatermark,

    /// <summary>Emit results when the event count threshold is reached.</summary>
    OnCount,

    /// <summary>Emit results after a processing-time delay since the first event.</summary>
    OnProcessingTime,

    /// <summary>Emit early results before the window closes, then final at watermark.</summary>
    EarlyAndLate
}

/// <summary>
/// Configuration for watermark generation and late event handling.
/// </summary>
public sealed record WatermarkConfig
{
    /// <summary>Gets the watermark generation strategy.</summary>
    public WatermarkStrategy Strategy { get; init; } = WatermarkStrategy.BoundedOutOfOrderness;

    /// <summary>Gets the maximum allowed out-of-orderness (bounded delay).</summary>
    public TimeSpan MaxOutOfOrderness { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets the allowed lateness beyond the watermark before events are discarded.</summary>
    public TimeSpan AllowedLateness { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets the periodic watermark generation interval.</summary>
    public TimeSpan PeriodicInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the window trigger strategy.</summary>
    public WindowTriggerStrategy TriggerStrategy { get; init; } = WindowTriggerStrategy.OnWatermark;

    /// <summary>Gets the event count threshold for count-based triggers.</summary>
    public int CountTriggerThreshold { get; init; } = 100;

    /// <summary>Gets the processing-time delay for time-based triggers.</summary>
    public TimeSpan ProcessingTimeDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets whether to emit results on each early firing.</summary>
    public bool EmitEarlyResults { get; init; }

    /// <summary>Gets the early firing interval for EarlyAndLate trigger strategy.</summary>
    public TimeSpan EarlyFiringInterval { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Represents the current watermark state for a stream partition.
/// </summary>
public sealed record WatermarkState
{
    /// <summary>Gets the partition identifier.</summary>
    public required string PartitionId { get; init; }

    /// <summary>Gets the current watermark timestamp (event time).</summary>
    public DateTimeOffset CurrentWatermark { get; init; }

    /// <summary>Gets the maximum observed event time.</summary>
    public DateTimeOffset MaxObservedEventTime { get; init; }

    /// <summary>Gets the total number of events processed.</summary>
    public long EventsProcessed { get; init; }

    /// <summary>Gets the number of late events received (after watermark but within allowed lateness).</summary>
    public long LateEventsAccepted { get; init; }

    /// <summary>Gets the number of events discarded as too late.</summary>
    public long EventsDiscarded { get; init; }

    /// <summary>Gets when this state was last updated.</summary>
    public DateTimeOffset LastUpdated { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Classification result for an event relative to the watermark.
/// </summary>
public enum EventTimeliness
{
    /// <summary>Event arrived before the watermark -- on time.</summary>
    OnTime,

    /// <summary>Event arrived after the watermark but within allowed lateness.</summary>
    Late,

    /// <summary>Event arrived after the watermark and beyond allowed lateness -- discarded.</summary>
    TooLate
}

/// <summary>
/// Result of processing an event through the watermark manager.
/// </summary>
public sealed record WatermarkEventResult
{
    /// <summary>Gets the event that was classified.</summary>
    public required StreamEvent Event { get; init; }

    /// <summary>Gets the timeliness classification.</summary>
    public required EventTimeliness Timeliness { get; init; }

    /// <summary>Gets the watermark at the time of classification.</summary>
    public DateTimeOffset WatermarkAtClassification { get; init; }

    /// <summary>Gets whether a watermark advance occurred after this event.</summary>
    public bool WatermarkAdvanced { get; init; }

    /// <summary>Gets any windows that were triggered by this watermark advance.</summary>
    public IReadOnlyList<string>? TriggeredWindows { get; init; }
}

#endregion

/// <summary>
/// Manages event-time watermarks for out-of-order event stream processing.
/// Handles watermark generation, late event acceptance/rejection, allowed lateness
/// configuration, and window trigger strategies.
/// </summary>
/// <remarks>
/// <b>MESSAGE BUS:</b> Publishes watermark advances to "streaming.watermark.advance" topic
/// and late event notifications to "streaming.watermark.late" topic.
/// Thread-safe for concurrent partition processing.
/// </remarks>
internal sealed class WatermarkManagement : IDisposable
{
    private readonly BoundedDictionary<string, PartitionWatermarkState> _partitions = new BoundedDictionary<string, PartitionWatermarkState>(1000);
    private readonly WatermarkConfig _config;
    private readonly IMessageBus? _messageBus;
    private readonly Timer? _periodicTimer;
    private long _totalEventsProcessed;
    private long _totalLateEvents;
    private long _totalDiscardedEvents;
    private long _watermarkAdvances;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatermarkManagement"/> class.
    /// </summary>
    /// <param name="config">Watermark configuration.</param>
    /// <param name="messageBus">Optional message bus for watermark event notifications.</param>
    public WatermarkManagement(WatermarkConfig? config = null, IMessageBus? messageBus = null)
    {
        _config = config ?? new WatermarkConfig();
        _messageBus = messageBus;

        if (_config.Strategy == WatermarkStrategy.Periodic)
        {
            _periodicTimer = new Timer(
                AdvancePeriodicWatermarks,
                null,
                _config.PeriodicInterval,
                _config.PeriodicInterval);
        }
    }

    /// <summary>Gets the total events processed across all partitions.</summary>
    public long TotalEventsProcessed => Interlocked.Read(ref _totalEventsProcessed);

    /// <summary>Gets the total late events accepted across all partitions.</summary>
    public long TotalLateEvents => Interlocked.Read(ref _totalLateEvents);

    /// <summary>Gets the total discarded events across all partitions.</summary>
    public long TotalDiscardedEvents => Interlocked.Read(ref _totalDiscardedEvents);

    /// <summary>Gets the total watermark advance count.</summary>
    public long WatermarkAdvances => Interlocked.Read(ref _watermarkAdvances);

    /// <summary>
    /// Processes an event through watermark management, classifying its timeliness
    /// and potentially advancing the watermark.
    /// </summary>
    /// <param name="partitionId">The partition this event belongs to.</param>
    /// <param name="evt">The stream event to process.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The watermark event result with timeliness classification.</returns>
    public async Task<WatermarkEventResult> ProcessEventAsync(
        string partitionId,
        StreamEvent evt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(partitionId);
        ArgumentNullException.ThrowIfNull(evt);

        Interlocked.Increment(ref _totalEventsProcessed);

        var state = _partitions.GetOrAdd(partitionId, _ => new PartitionWatermarkState
        {
            CurrentWatermark = DateTimeOffset.MinValue,
            MaxObservedEventTime = DateTimeOffset.MinValue
        });

        var eventTime = evt.EventTime ?? evt.Timestamp;
        bool watermarkAdvanced = false;
        List<string>? triggeredWindows = null;
        EventTimeliness timeliness;

        DateTimeOffset watermarkSnapshot = default;
        lock (state.Lock)
        {
            timeliness = ClassifyEvent(state, eventTime);
            switch (timeliness)
            {
                case EventTimeliness.OnTime:
                    if (eventTime > state.MaxObservedEventTime)
                    {
                        state.MaxObservedEventTime = eventTime;
                    }
                    Interlocked.Increment(ref state.EventsProcessed);

                    watermarkAdvanced = TryAdvanceWatermark(state, out var newWatermark);
                    if (watermarkAdvanced)
                    {
                        state.CurrentWatermark = newWatermark;
                        Interlocked.Increment(ref _watermarkAdvances);
                        triggeredWindows = CheckWindowTriggers(state);
                        watermarkSnapshot = newWatermark;
                    }
                    break;

                case EventTimeliness.Late:
                    Interlocked.Increment(ref _totalLateEvents);
                    Interlocked.Increment(ref state.LateEventsAccepted);
                    Interlocked.Increment(ref state.EventsProcessed);
                    watermarkSnapshot = state.CurrentWatermark;
                    break;

                case EventTimeliness.TooLate:
                    Interlocked.Increment(ref _totalDiscardedEvents);
                    Interlocked.Increment(ref state.EventsDiscarded);
                    break;
            }
            state.LastUpdated = DateTimeOffset.UtcNow;
        }

        // Publish notifications outside the lock to avoid holding it during async I/O
        if (watermarkAdvanced && _messageBus != null)
        {
            await PublishWatermarkAdvanceAsync(partitionId, watermarkSnapshot, ct);
        }
        else if (timeliness == EventTimeliness.Late && _messageBus != null)
        {
            await PublishLateEventAsync(partitionId, evt, eventTime, watermarkSnapshot, ct);
        }

        return new WatermarkEventResult
        {
            Event = evt,
            Timeliness = timeliness,
            WatermarkAtClassification = state.CurrentWatermark,
            WatermarkAdvanced = watermarkAdvanced,
            TriggeredWindows = triggeredWindows
        };
    }

    /// <summary>
    /// Gets the current watermark state for a partition.
    /// </summary>
    /// <param name="partitionId">The partition identifier.</param>
    /// <returns>The watermark state, or null if the partition has not been seen.</returns>
    public WatermarkState? GetPartitionState(string partitionId)
    {
        if (!_partitions.TryGetValue(partitionId, out var state))
            return null;

        lock (state.Lock)
        {
            return new WatermarkState
            {
                PartitionId = partitionId,
                CurrentWatermark = state.CurrentWatermark,
                MaxObservedEventTime = state.MaxObservedEventTime,
                EventsProcessed = Interlocked.Read(ref state.EventsProcessed),
                LateEventsAccepted = Interlocked.Read(ref state.LateEventsAccepted),
                EventsDiscarded = Interlocked.Read(ref state.EventsDiscarded),
                LastUpdated = state.LastUpdated
            };
        }
    }

    /// <summary>
    /// Gets watermark state for all tracked partitions.
    /// </summary>
    /// <returns>A read-only collection of partition watermark states.</returns>
    public IReadOnlyCollection<WatermarkState> GetAllPartitionStates()
    {
        return _partitions.Select(kv => new WatermarkState
        {
            PartitionId = kv.Key,
            CurrentWatermark = kv.Value.CurrentWatermark,
            MaxObservedEventTime = kv.Value.MaxObservedEventTime,
            EventsProcessed = Interlocked.Read(ref kv.Value.EventsProcessed),
            LateEventsAccepted = Interlocked.Read(ref kv.Value.LateEventsAccepted),
            EventsDiscarded = Interlocked.Read(ref kv.Value.EventsDiscarded),
            LastUpdated = kv.Value.LastUpdated
        }).ToArray();
    }

    /// <summary>
    /// Registers a named window for trigger tracking.
    /// </summary>
    /// <param name="partitionId">The partition identifier.</param>
    /// <param name="windowId">The window identifier.</param>
    /// <param name="windowEnd">The window end time (event time).</param>
    public void RegisterWindow(string partitionId, string windowId, DateTimeOffset windowEnd)
    {
        var state = _partitions.GetOrAdd(partitionId, _ => new PartitionWatermarkState
        {
            CurrentWatermark = DateTimeOffset.MinValue,
            MaxObservedEventTime = DateTimeOffset.MinValue
        });

        state.PendingWindows[windowId] = windowEnd;
    }

    private EventTimeliness ClassifyEvent(PartitionWatermarkState state, DateTimeOffset eventTime)
    {
        if (state.CurrentWatermark == DateTimeOffset.MinValue)
            return EventTimeliness.OnTime;

        if (eventTime >= state.CurrentWatermark)
            return EventTimeliness.OnTime;

        // Event is behind watermark
        var lateness = state.CurrentWatermark - eventTime;

        if (lateness <= _config.AllowedLateness)
            return EventTimeliness.Late;

        return EventTimeliness.TooLate;
    }

    private bool TryAdvanceWatermark(PartitionWatermarkState state, out DateTimeOffset newWatermark)
    {
        newWatermark = state.CurrentWatermark;

        DateTimeOffset proposedWatermark = _config.Strategy switch
        {
            WatermarkStrategy.BoundedOutOfOrderness =>
                state.MaxObservedEventTime - _config.MaxOutOfOrderness,
            WatermarkStrategy.ProcessingTime =>
                DateTimeOffset.UtcNow,
            WatermarkStrategy.Periodic =>
                state.MaxObservedEventTime - _config.MaxOutOfOrderness,
            WatermarkStrategy.Punctuated =>
                state.MaxObservedEventTime,
            _ => state.CurrentWatermark
        };

        // Watermark must be monotonically increasing
        if (proposedWatermark > state.CurrentWatermark)
        {
            newWatermark = proposedWatermark;
            return true;
        }

        return false;
    }

    private List<string> CheckWindowTriggers(PartitionWatermarkState state)
    {
        var triggered = new List<string>();

        if (_config.TriggerStrategy == WindowTriggerStrategy.OnWatermark ||
            _config.TriggerStrategy == WindowTriggerStrategy.EarlyAndLate)
        {
            var completedWindows = state.PendingWindows
                .Where(kv => kv.Value <= state.CurrentWatermark)
                .Select(kv => kv.Key)
                .ToArray();

            foreach (var windowId in completedWindows)
            {
                state.PendingWindows.TryRemove(windowId, out _);
                triggered.Add(windowId);
            }
        }

        return triggered;
    }

    private void AdvancePeriodicWatermarks(object? _)
    {
        foreach (var (partitionId, state) in _partitions)
        {
            lock (state.Lock)
            {
                if (TryAdvanceWatermark(state, out var newWatermark))
                {
                    state.CurrentWatermark = newWatermark;
                    Interlocked.Increment(ref _watermarkAdvances);
                    state.LastUpdated = DateTimeOffset.UtcNow;
                }
            }
        }
    }

    private async Task PublishWatermarkAdvanceAsync(
        string partitionId,
        DateTimeOffset newWatermark,
        CancellationToken ct)
    {
        if (_messageBus == null) return;

        var message = new PluginMessage
        {
            Type = "watermark.advance",
            SourcePluginId = "com.datawarehouse.streaming.ultimate",
            Source = "WatermarkManagement",
            Payload = new Dictionary<string, object>
            {
                ["PartitionId"] = partitionId,
                ["NewWatermark"] = newWatermark.ToString("O"),
                ["Strategy"] = _config.Strategy.ToString(),
                ["TotalAdvances"] = Interlocked.Read(ref _watermarkAdvances)
            }
        };

        try
        {
            await _messageBus.PublishAsync("streaming.watermark.advance", message, ct);
        }
        catch (Exception ex)
        {

            // Non-critical notification
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task PublishLateEventAsync(
        string partitionId,
        StreamEvent evt,
        DateTimeOffset eventTime,
        DateTimeOffset watermark,
        CancellationToken ct)
    {
        if (_messageBus == null) return;

        var message = new PluginMessage
        {
            Type = "watermark.late-event",
            SourcePluginId = "com.datawarehouse.streaming.ultimate",
            Source = "WatermarkManagement",
            Payload = new Dictionary<string, object>
            {
                ["PartitionId"] = partitionId,
                ["EventId"] = evt.EventId,
                ["EventTime"] = eventTime.ToString("O"),
                ["Watermark"] = watermark.ToString("O"),
                ["Lateness"] = (watermark - eventTime).TotalMilliseconds
            }
        };

        try
        {
            await _messageBus.PublishAsync("streaming.watermark.late", message, ct);
        }
        catch (Exception ex)
        {

            // Non-critical notification
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _periodicTimer?.Dispose();
    }

    /// <summary>
    /// Internal mutable state for a single partition's watermark tracking.
    /// </summary>
    private sealed class PartitionWatermarkState
    {
        /// <summary>Protects the 16-byte DateTimeOffset fields from torn reads on 32-bit platforms.</summary>
        public readonly object Lock = new();
        public DateTimeOffset CurrentWatermark;
        public DateTimeOffset MaxObservedEventTime;
        public long EventsProcessed;
        public long LateEventsAccepted;
        public long EventsDiscarded;
        public DateTimeOffset LastUpdated = DateTimeOffset.UtcNow;
        public readonly BoundedDictionary<string, DateTimeOffset> PendingWindows = new BoundedDictionary<string, DateTimeOffset>(1000);
    }
}
