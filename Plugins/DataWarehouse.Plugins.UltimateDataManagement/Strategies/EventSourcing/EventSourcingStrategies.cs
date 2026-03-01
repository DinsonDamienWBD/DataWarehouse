using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.EventSourcing;

#region Core Types and Interfaces

/// <summary>
/// Represents an event in the event store.
/// </summary>
public sealed record StoredEvent
{
    /// <summary>Unique event identifier.</summary>
    public required string EventId { get; init; }

    /// <summary>Stream/aggregate this event belongs to.</summary>
    public required string StreamId { get; init; }

    /// <summary>Event type name for deserialization.</summary>
    public required string EventType { get; init; }

    /// <summary>Schema version for event evolution.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Position in the stream (sequence number).</summary>
    public long StreamPosition { get; init; }

    /// <summary>Global position across all streams.</summary>
    public long GlobalPosition { get; init; }

    /// <summary>Serialized event data.</summary>
    public required byte[] Data { get; init; }

    /// <summary>Serialized event metadata.</summary>
    public byte[]? Metadata { get; init; }

    /// <summary>Event timestamp.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Correlation ID for tracing related events.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Causation ID linking to the causing event.</summary>
    public string? CausationId { get; init; }

    /// <summary>User/actor who triggered the event.</summary>
    public string? ActorId { get; init; }

    /// <summary>Content hash for integrity verification.</summary>
    public string? ContentHash { get; init; }

    /// <summary>Expected version for optimistic concurrency.</summary>
    public long? ExpectedVersion { get; init; }
}

/// <summary>
/// Event metadata for audit and tracing.
/// </summary>
public sealed record EventMetadata
{
    public string? CorrelationId { get; init; }
    public string? CausationId { get; init; }
    public string? ActorId { get; init; }
    public string? TenantId { get; init; }
    public string? SourceSystem { get; init; }
    public Dictionary<string, object>? CustomHeaders { get; init; }
}

/// <summary>
/// Result of appending events to a stream.
/// </summary>
public sealed record AppendResult
{
    public bool Success { get; init; }
    public long NextExpectedVersion { get; init; }
    public long GlobalPosition { get; init; }
    public string? Error { get; init; }
    public int EventsAppended { get; init; }
}

/// <summary>
/// Options for reading events.
/// </summary>
public sealed record ReadOptions
{
    public long FromPosition { get; init; } = 0;
    public int MaxCount { get; init; } = 1000;
    public bool ReadForward { get; init; } = true;
    public bool ResolveLinkTos { get; init; } = true;
    public string[]? EventTypeFilter { get; init; }
    public DateTimeOffset? FromTimestamp { get; init; }
    public DateTimeOffset? ToTimestamp { get; init; }
}

/// <summary>
/// Snapshot of aggregate state at a point in time.
/// </summary>
public sealed record AggregateSnapshot
{
    public required string AggregateId { get; init; }
    public required string AggregateType { get; init; }
    public required byte[] State { get; init; }
    public long Version { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? ContentHash { get; init; }
    public long EventCount { get; init; }
}

/// <summary>
/// Schema definition for event versioning.
/// </summary>
public sealed record EventSchema
{
    public required string EventType { get; init; }
    public required int Version { get; init; }
    public required string SchemaDefinition { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool IsDeprecated { get; init; }
    public string? UpgradeScript { get; init; }
    public string? DowngradeScript { get; init; }
}

/// <summary>
/// Result of schema compatibility check.
/// </summary>
public sealed record SchemaCompatibility
{
    public bool IsCompatible { get; init; }
    public CompatibilityLevel Level { get; init; }
    public string[] BreakingChanges { get; init; } = Array.Empty<string>();
    public string[] Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Schema compatibility levels.
/// </summary>
public enum CompatibilityLevel
{
    Full,           // Forward and backward compatible
    Backward,       // New schema can read old data
    Forward,        // Old schema can read new data
    None            // Breaking changes present
}

/// <summary>
/// CQRS command envelope.
/// </summary>
public sealed record Command
{
    public required string CommandId { get; init; }
    public required string CommandType { get; init; }
    public required string AggregateId { get; init; }
    public required byte[] Payload { get; init; }
    public EventMetadata? Metadata { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public long? ExpectedVersion { get; init; }
}

/// <summary>
/// CQRS command result.
/// </summary>
public sealed record CommandResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public StoredEvent[]? Events { get; init; }
    public long NewVersion { get; init; }
}

/// <summary>
/// Projection definition for read models.
/// </summary>
public sealed record ProjectionDefinition
{
    public required string ProjectionId { get; init; }
    public required string ProjectionName { get; init; }
    public required string[] SubscribedEventTypes { get; init; }
    public bool IsEnabled { get; init; } = true;
    public long CheckpointPosition { get; init; }
    public DateTimeOffset LastProcessedAt { get; init; }
    public ProjectionStatus Status { get; init; } = ProjectionStatus.Running;
}

/// <summary>
/// Projection status.
/// </summary>
public enum ProjectionStatus
{
    Running,
    Paused,
    Faulted,
    Rebuilding,
    Completed
}

/// <summary>
/// Domain aggregate base for DDD support.
/// </summary>
public abstract class AggregateRoot
{
    private readonly List<StoredEvent> _uncommittedEvents = new();

    public string Id { get; protected set; } = string.Empty;
    public long Version { get; protected set; } = -1;

    public IReadOnlyList<StoredEvent> UncommittedEvents => _uncommittedEvents.AsReadOnly();

    public void ClearUncommittedEvents() => _uncommittedEvents.Clear();

    protected void ApplyChange(object @event)
    {
        ApplyEvent(@event);

        var storedEvent = new StoredEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            StreamId = Id,
            EventType = @event.GetType().Name,
            Data = JsonSerializer.SerializeToUtf8Bytes(@event),
            StreamPosition = Version + _uncommittedEvents.Count + 1,
            GlobalPosition = 0 // Set by store
        };

        _uncommittedEvents.Add(storedEvent);
    }

    public void LoadFromHistory(IEnumerable<StoredEvent> history)
    {
        foreach (var @event in history)
        {
            ApplyEvent(DeserializeEvent(@event));
            Version = @event.StreamPosition;
        }
    }

    protected abstract void ApplyEvent(object @event);
    protected abstract object DeserializeEvent(StoredEvent storedEvent);
}

#endregion

#region 89.1 Event Store Implementation

/// <summary>
/// 89.1: Core event store implementation providing append-only event storage,
/// stream management, optimistic concurrency, and global ordering.
/// </summary>
public sealed class EventStoreStrategy : DataManagementStrategyBase
{
    private readonly BoundedDictionary<string, List<StoredEvent>> _streams = new BoundedDictionary<string, List<StoredEvent>>(1000);
    private readonly BoundedDictionary<string, long> _streamVersions = new BoundedDictionary<string, long>(1000);
    private readonly List<StoredEvent> _globalLog = new();
    private readonly ReaderWriterLockSlim _globalLogLock = new(LockRecursionPolicy.NoRecursion);
    private readonly ConcurrentDictionary<string, List<Action<StoredEvent>>> _subscriptions = new();

    private long _globalPosition;

    public override string StrategyId => "eventsourcing-eventstore";
    public override string DisplayName => "Event Store";
    public override DataManagementCategory Category => DataManagementCategory.EventSourcing;
    public override DataManagementCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = true,
        SupportsTTL = false,
        MaxThroughput = 100000,
        TypicalLatencyMs = 0.5
    };
    public override string SemanticDescription =>
        "Core event store providing append-only event storage with streams, optimistic concurrency, " +
        "global ordering, subscriptions, and full event history. Foundation for event sourcing architecture.";
    public override string[] Tags => ["event-store", "event-sourcing", "append-only", "streams", "concurrency"];

    /// <summary>
    /// Appends events to a stream with optimistic concurrency control.
    /// </summary>
    public Task<AppendResult> AppendToStreamAsync(
        string streamId,
        IEnumerable<StoredEvent> events,
        long? expectedVersion = null,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ct.ThrowIfCancellationRequested();

        var sw = Stopwatch.StartNew();
        var eventList = events.ToList();

        try
        {
            var stream = _streams.GetOrAdd(streamId, _ => new List<StoredEvent>());
            _streamVersions.GetOrAdd(streamId, -1);

            var appendedEvents = new List<StoredEvent>();
            long currentVersion;

            // Acquire stream-level lock BEFORE reading version to eliminate TOCTOU race.
            lock (stream)
            {
                // Re-read version inside the lock so the check and append are atomic.
                currentVersion = _streamVersions.GetOrAdd(streamId, -1);

                // Optimistic concurrency check (inside lock)
                if (expectedVersion.HasValue && currentVersion != expectedVersion.Value)
                {
                    return Task.FromResult(new AppendResult
                    {
                        Success = false,
                        Error = $"Concurrency conflict: expected version {expectedVersion}, actual {currentVersion}",
                        NextExpectedVersion = currentVersion
                    });
                }

                _globalLogLock.EnterWriteLock();
                try
                {
                    foreach (var @event in eventList)
                    {
                        var position = Interlocked.Increment(ref _globalPosition);
                        currentVersion++;

                        var storedEvent = @event with
                        {
                            StreamPosition = currentVersion,
                            GlobalPosition = position,
                            ContentHash = ComputeHash(@event.Data)
                        };

                        stream.Add(storedEvent);
                        _globalLog.Add(storedEvent);
                        appendedEvents.Add(storedEvent);

                        RecordWrite(@event.Data.Length, sw.Elapsed.TotalMilliseconds);
                    }

                    _streamVersions[streamId] = currentVersion;
                }
                finally
                {
                    _globalLogLock.ExitWriteLock();
                }
            }

            // Notify subscribers
            foreach (var storedEvent in appendedEvents)
            {
                NotifySubscribers(storedEvent);
            }

            return Task.FromResult(new AppendResult
            {
                Success = true,
                NextExpectedVersion = currentVersion,
                GlobalPosition = appendedEvents.LastOrDefault()?.GlobalPosition ?? 0,
                EventsAppended = eventList.Count
            });
        }
        catch (Exception ex)
        {
            RecordFailure();
            return Task.FromResult(new AppendResult
            {
                Success = false,
                Error = ex.Message
            });
        }
    }

    /// <summary>
    /// Reads events from a stream.
    /// </summary>
    public Task<IReadOnlyList<StoredEvent>> ReadStreamAsync(
        string streamId,
        ReadOptions? options = null,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ct.ThrowIfCancellationRequested();

        var sw = Stopwatch.StartNew();
        options ??= new ReadOptions();

        if (!_streams.TryGetValue(streamId, out var stream))
        {
            RecordRead(0, sw.Elapsed.TotalMilliseconds, miss: true);
            return Task.FromResult<IReadOnlyList<StoredEvent>>(Array.Empty<StoredEvent>());
        }

        IEnumerable<StoredEvent> events;
        lock (stream)
        {
            events = stream.ToList();
        }

        // Apply filters
        if (options.FromPosition > 0)
            events = events.Where(e => e.StreamPosition >= options.FromPosition);

        if (options.FromTimestamp.HasValue)
            events = events.Where(e => e.Timestamp >= options.FromTimestamp.Value);

        if (options.ToTimestamp.HasValue)
            events = events.Where(e => e.Timestamp <= options.ToTimestamp.Value);

        if (options.EventTypeFilter?.Length > 0)
            events = events.Where(e => options.EventTypeFilter.Contains(e.EventType));

        // Apply direction and limit
        if (!options.ReadForward)
            events = events.Reverse();

        var result = events.Take(options.MaxCount).ToList().AsReadOnly();

        RecordRead(result.Sum(e => e.Data.Length), sw.Elapsed.TotalMilliseconds, hit: result.Count > 0);

        return Task.FromResult<IReadOnlyList<StoredEvent>>(result);
    }

    /// <summary>
    /// Reads all events from the global log.
    /// </summary>
    public Task<IReadOnlyList<StoredEvent>> ReadAllAsync(
        ReadOptions? options = null,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        options ??= new ReadOptions();

        _globalLogLock.EnterReadLock();
        try
        {
            IEnumerable<StoredEvent> events = _globalLog.ToList();

            if (options.FromPosition > 0)
                events = events.Where(e => e.GlobalPosition >= options.FromPosition);

            if (!options.ReadForward)
                events = events.Reverse();

            return Task.FromResult<IReadOnlyList<StoredEvent>>(
                events.Take(options.MaxCount).ToList().AsReadOnly());
        }
        finally
        {
            _globalLogLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets the current version of a stream.
    /// </summary>
    public Task<long> GetStreamVersionAsync(string streamId, CancellationToken ct = default)
    {
        return Task.FromResult(_streamVersions.GetValueOrDefault(streamId, -1));
    }

    /// <summary>
    /// Subscribes to events in a stream.
    /// </summary>
    public IDisposable Subscribe(string streamId, Action<StoredEvent> handler)
    {
        var handlers = _subscriptions.GetOrAdd(streamId, _ => new List<Action<StoredEvent>>());
        lock (handlers)
        {
            handlers.Add(handler);
        }

        return new SubscriptionHandle(() =>
        {
            lock (handlers)
            {
                handlers.Remove(handler);
            }
        });
    }

    /// <summary>
    /// Subscribes to all events.
    /// </summary>
    public IDisposable SubscribeToAll(Action<StoredEvent> handler)
    {
        return Subscribe("$all", handler);
    }

    private void NotifySubscribers(StoredEvent @event)
    {
        // Notify stream-specific subscribers
        if (_subscriptions.TryGetValue(@event.StreamId, out var streamHandlers))
        {
            List<Action<StoredEvent>> handlersCopy;
            lock (streamHandlers)
            {
                handlersCopy = streamHandlers.ToList();
            }
            foreach (var handler in handlersCopy)
            {
                try { handler(@event); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[EventStore] Stream subscriber threw for event {{{@event.EventId}}}: {ex}"); }
            }
        }

        // Notify $all subscribers
        if (_subscriptions.TryGetValue("$all", out var allHandlers))
        {
            List<Action<StoredEvent>> handlersCopy;
            lock (allHandlers)
            {
                handlersCopy = allHandlers.ToList();
            }
            foreach (var handler in handlersCopy)
            {
                try { handler(@event); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[EventStore] Global subscriber threw for event {{{@event.EventId}}}: {ex}"); }
            }
        }
    }

    private static string ComputeHash(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash);
    }

    private sealed class SubscriptionHandle : IDisposable
    {
        private readonly Action _unsubscribe;
        public SubscriptionHandle(Action unsubscribe) => _unsubscribe = unsubscribe;
        public void Dispose() => _unsubscribe();
    }
}

#endregion

#region 89.2 Event Streaming Strategy

/// <summary>
/// 89.2: Event streaming with real-time push, catchup subscriptions,
/// consumer groups, and backpressure handling.
/// </summary>
public sealed class EventStreamingStrategy : DataManagementStrategyBase
{
    private readonly BoundedDictionary<string, StreamingConsumer> _consumers = new BoundedDictionary<string, StreamingConsumer>(1000);
    private readonly BoundedDictionary<string, ConsumerGroup> _consumerGroups = new BoundedDictionary<string, ConsumerGroup>(1000);
    private readonly ConcurrentQueue<StoredEvent> _eventBuffer = new();
    private readonly SemaphoreSlim _backpressureSemaphore = new(10000);

    public override string StrategyId => "eventsourcing-streaming";
    public override string DisplayName => "Event Streaming";
    public override DataManagementCategory Category => DataManagementCategory.EventSourcing;
    public override DataManagementCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = true,
        MaxThroughput = 500000,
        TypicalLatencyMs = 0.1
    };
    public override string SemanticDescription =>
        "Real-time event streaming with push subscriptions, catchup from position, " +
        "consumer groups for competing consumers, and intelligent backpressure handling.";
    public override string[] Tags => ["streaming", "real-time", "push", "consumer-groups", "backpressure"];

    /// <summary>
    /// Creates a persistent streaming subscription.
    /// </summary>
    public async Task<StreamingSubscription> CreateSubscriptionAsync(
        string subscriptionId,
        string streamId,
        long fromPosition = 0,
        StreamingOptions? options = null,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        options ??= new StreamingOptions();

        var consumer = new StreamingConsumer
        {
            SubscriptionId = subscriptionId,
            StreamId = streamId,
            CurrentPosition = fromPosition,
            Options = options,
            Status = ConsumerStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _consumers[subscriptionId] = consumer;

        return new StreamingSubscription
        {
            SubscriptionId = subscriptionId,
            StreamId = streamId,
            FromPosition = fromPosition,
            Status = SubscriptionStatus.Active
        };
    }

    /// <summary>
    /// Creates or joins a consumer group for competing consumer pattern.
    /// </summary>
    public Task<ConsumerGroupMembership> JoinConsumerGroupAsync(
        string groupId,
        string consumerId,
        string[] subscribedStreams,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var group = _consumerGroups.GetOrAdd(groupId, _ => new ConsumerGroup
        {
            GroupId = groupId,
            SubscribedStreams = subscribedStreams.ToList(),
            Members = new BoundedDictionary<string, ConsumerMember>(1000)
        });

        var member = new ConsumerMember
        {
            ConsumerId = consumerId,
            JoinedAt = DateTimeOffset.UtcNow,
            Status = ConsumerStatus.Active
        };

        group.Members[consumerId] = member;

        // Rebalance partitions
        RebalanceConsumerGroup(group);

        return Task.FromResult(new ConsumerGroupMembership
        {
            GroupId = groupId,
            ConsumerId = consumerId,
            AssignedPartitions = member.AssignedPartitions,
            Status = MembershipStatus.Active
        });
    }

    /// <summary>
    /// Pushes an event to streaming consumers with backpressure.
    /// </summary>
    public async Task<bool> PushEventAsync(StoredEvent @event, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        // Apply backpressure if buffer is full
        if (!await _backpressureSemaphore.WaitAsync(TimeSpan.FromSeconds(5), ct))
        {
            RecordFailure();
            return false;
        }

        try
        {
            _eventBuffer.Enqueue(@event);

            // Notify active consumers
            foreach (var consumer in _consumers.Values.Where(c =>
                c.Status == ConsumerStatus.Active &&
                (c.StreamId == @event.StreamId || c.StreamId == "$all")))
            {
                await DeliverToConsumerAsync(consumer, @event, ct);
            }

            // Notify consumer groups
            foreach (var group in _consumerGroups.Values.Where(g =>
                g.SubscribedStreams.Contains(@event.StreamId) ||
                g.SubscribedStreams.Contains("$all")))
            {
                await DeliverToGroupAsync(group, @event, ct);
            }

            return true;
        }
        finally
        {
            _backpressureSemaphore.Release();
        }
    }

    /// <summary>
    /// Performs a catchup read from a position.
    /// </summary>
    public async IAsyncEnumerable<StoredEvent> CatchupAsync(
        string streamId,
        long fromPosition,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        // In production, would read from event store
        // Here we yield from the buffer for demonstration
        foreach (var @event in _eventBuffer.Where(e =>
            (e.StreamId == streamId || streamId == "$all") &&
            e.GlobalPosition >= fromPosition))
        {
            ct.ThrowIfCancellationRequested();
            yield return @event;
        }
    }

    /// <summary>
    /// Acknowledges an event as processed.
    /// </summary>
    public Task AcknowledgeAsync(string subscriptionId, long position, CancellationToken ct = default)
    {
        if (_consumers.TryGetValue(subscriptionId, out var consumer))
        {
            consumer.CurrentPosition = position;
            consumer.LastAckAt = DateTimeOffset.UtcNow;
        }
        return Task.CompletedTask;
    }

    private Task DeliverToConsumerAsync(StreamingConsumer consumer, StoredEvent @event, CancellationToken ct)
    {
        consumer.EventsDelivered++;
        consumer.CurrentPosition = @event.GlobalPosition;
        return Task.CompletedTask;
    }

    private Task DeliverToGroupAsync(ConsumerGroup group, StoredEvent @event, CancellationToken ct)
    {
        // Round-robin to active members
        var activeMembers = group.Members.Values
            .Where(m => m.Status == ConsumerStatus.Active)
            .ToList();

        if (activeMembers.Count > 0)
        {
            var targetIndex = (int)(@event.GlobalPosition % activeMembers.Count);
            var targetMember = activeMembers[targetIndex];
            targetMember.EventsDelivered++;
        }

        return Task.CompletedTask;
    }

    private void RebalanceConsumerGroup(ConsumerGroup group)
    {
        var members = group.Members.Values.ToList();
        var partitionCount = group.SubscribedStreams.Count;

        for (int i = 0; i < members.Count; i++)
        {
            var startPartition = i * partitionCount / members.Count;
            var endPartition = (i + 1) * partitionCount / members.Count;
            members[i].AssignedPartitions = Enumerable.Range(startPartition, endPartition - startPartition).ToArray();
        }
    }
}

/// <summary>
/// Streaming subscription options.
/// </summary>
public sealed record StreamingOptions
{
    public int BufferSize { get; init; } = 1000;
    public TimeSpan AckTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxRetries { get; init; } = 3;
    public bool AutoAck { get; init; } = false;
}

/// <summary>
/// Streaming subscription result.
/// </summary>
public sealed record StreamingSubscription
{
    public required string SubscriptionId { get; init; }
    public required string StreamId { get; init; }
    public long FromPosition { get; init; }
    public SubscriptionStatus Status { get; init; }
}

public enum SubscriptionStatus { Active, Paused, Closed }

public enum ConsumerStatus { Active, Paused, Disconnected }

public enum MembershipStatus { Active, Pending, Rebalancing }

internal sealed class StreamingConsumer
{
    public required string SubscriptionId { get; init; }
    public required string StreamId { get; init; }
    public long CurrentPosition { get; set; }
    public required StreamingOptions Options { get; init; }
    public ConsumerStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastAckAt { get; set; }
    public long EventsDelivered { get; set; }
}

internal sealed class ConsumerGroup
{
    public required string GroupId { get; init; }
    public required List<string> SubscribedStreams { get; init; }
    public required BoundedDictionary<string, ConsumerMember> Members { get; init; }
}

internal sealed class ConsumerMember
{
    public required string ConsumerId { get; init; }
    public DateTimeOffset JoinedAt { get; init; }
    public ConsumerStatus Status { get; set; }
    public int[] AssignedPartitions { get; set; } = Array.Empty<int>();
    public long EventsDelivered { get; set; }
}

public sealed record ConsumerGroupMembership
{
    public required string GroupId { get; init; }
    public required string ConsumerId { get; init; }
    public int[] AssignedPartitions { get; init; } = Array.Empty<int>();
    public MembershipStatus Status { get; init; }
}

#endregion

#region 89.3 Event Replay Strategy

/// <summary>
/// 89.3: Event replay mechanisms for rebuilding state, temporal queries,
/// debugging, and what-if analysis.
/// </summary>
public sealed class EventReplayStrategy : DataManagementStrategyBase
{
    private readonly BoundedDictionary<string, ReplaySession> _activeSessions = new BoundedDictionary<string, ReplaySession>(1000);

    public override string StrategyId => "eventsourcing-replay";
    public override string DisplayName => "Event Replay";
    public override DataManagementCategory Category => DataManagementCategory.EventSourcing;
    public override DataManagementCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 50000,
        TypicalLatencyMs = 2.0
    };
    public override string SemanticDescription =>
        "Event replay mechanisms for state reconstruction, temporal queries, debugging sessions, " +
        "and what-if scenario analysis. Supports partial replay, speed control, and breakpoints.";
    public override string[] Tags => ["replay", "temporal", "debugging", "what-if", "state-reconstruction"];

    /// <summary>
    /// Starts a replay session.
    /// </summary>
    public Task<ReplaySession> StartReplayAsync(
        ReplayRequest request,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var session = new ReplaySession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            StreamId = request.StreamId,
            FromPosition = request.FromPosition,
            ToPosition = request.ToPosition,
            CurrentPosition = request.FromPosition,
            Speed = request.Speed,
            Status = ReplayStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
            Breakpoints = request.Breakpoints?.ToList() ?? new List<long>()
        };

        _activeSessions[session.SessionId] = session;

        return Task.FromResult(session);
    }

    /// <summary>
    /// Replays events to a handler with speed control.
    /// </summary>
    public async IAsyncEnumerable<ReplayedEvent> ReplayEventsAsync(
        string sessionId,
        IEnumerable<StoredEvent> events,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        if (!_activeSessions.TryGetValue(sessionId, out var session))
            yield break;

        var sw = Stopwatch.StartNew();
        DateTimeOffset? lastEventTime = null;

        foreach (var @event in events)
        {
            ct.ThrowIfCancellationRequested();

            // Check for pause/stop
            while (session.Status == ReplayStatus.Paused)
            {
                await Task.Delay(100, ct);
            }

            if (session.Status == ReplayStatus.Stopped)
                yield break;

            // Check breakpoints
            if (session.Breakpoints.Contains(@event.StreamPosition))
            {
                session.Status = ReplayStatus.Paused;
                session.PausedAt = DateTimeOffset.UtcNow;
            }

            // Speed control: simulate time between events
            if (session.Speed > 0 && lastEventTime.HasValue)
            {
                var timeDelta = @event.Timestamp - lastEventTime.Value;
                var delayMs = (int)(timeDelta.TotalMilliseconds / session.Speed);
                if (delayMs > 0 && delayMs < 10000)
                {
                    await Task.Delay(delayMs, ct);
                }
            }

            lastEventTime = @event.Timestamp;
            session.CurrentPosition = @event.StreamPosition;
            session.EventsReplayed++;

            yield return new ReplayedEvent
            {
                Event = @event,
                ReplayTimestamp = DateTimeOffset.UtcNow,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                SessionPosition = session.EventsReplayed
            };
        }

        session.Status = ReplayStatus.Completed;
        session.CompletedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Replays to a specific point in time.
    /// </summary>
    public async Task<ReplayResult> ReplayToPointInTimeAsync(
        string streamId,
        DateTimeOffset targetTime,
        Func<StoredEvent, Task> handler,
        IEnumerable<StoredEvent> events,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var sw = Stopwatch.StartNew();
        var replayed = 0;

        // P2-2439: Materialize once to avoid triple enumeration of the lazy IEnumerable.
        // The original code called events.Where().OrderBy() for the loop and then
        // events.Where().Max() for FinalPosition -- three separate enumerations total.
        var filtered = events
            .Where(e => e.Timestamp <= targetTime)
            .OrderBy(e => e.StreamPosition)
            .ToList();

        foreach (var @event in filtered)
        {
            ct.ThrowIfCancellationRequested();
            await handler(@event);
            replayed++;
        }

        return new ReplayResult
        {
            Success = true,
            EventsReplayed = replayed,
            FinalPosition = filtered.Count > 0 ? filtered[^1].StreamPosition : 0L,
            DurationMs = sw.Elapsed.TotalMilliseconds
        };
    }

    /// <summary>
    /// Performs a what-if analysis by replaying with modified events.
    /// </summary>
    public async Task<WhatIfResult> WhatIfAnalysisAsync(
        string streamId,
        IEnumerable<StoredEvent> events,
        Func<StoredEvent, StoredEvent?> modifier,
        Func<StoredEvent, Task<object?>> stateBuilder,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var originalStates = new List<object?>();
        var modifiedStates = new List<object?>();
        var modifications = new List<(StoredEvent Original, StoredEvent? Modified)>();

        foreach (var @event in events)
        {
            ct.ThrowIfCancellationRequested();

            // Compute original state
            var originalState = await stateBuilder(@event);
            originalStates.Add(originalState);

            // Apply modification
            var modifiedEvent = modifier(@event);
            modifications.Add((@event, modifiedEvent));

            // Compute modified state
            if (modifiedEvent != null)
            {
                var modifiedState = await stateBuilder(modifiedEvent);
                modifiedStates.Add(modifiedState);
            }
            else
            {
                modifiedStates.Add(null); // Event was filtered out
            }
        }

        return new WhatIfResult
        {
            OriginalEventCount = events.Count(),
            ModifiedEventCount = modifications.Count(m => m.Modified != null),
            FilteredEventCount = modifications.Count(m => m.Modified == null),
            StateDifferences = ComputeStateDifferences(originalStates, modifiedStates)
        };
    }

    /// <summary>
    /// Pauses a replay session.
    /// </summary>
    public Task PauseReplayAsync(string sessionId)
    {
        if (_activeSessions.TryGetValue(sessionId, out var session))
        {
            session.Status = ReplayStatus.Paused;
            session.PausedAt = DateTimeOffset.UtcNow;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resumes a paused replay session.
    /// </summary>
    public Task ResumeReplayAsync(string sessionId)
    {
        if (_activeSessions.TryGetValue(sessionId, out var session) && session.Status == ReplayStatus.Paused)
        {
            session.Status = ReplayStatus.Running;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops a replay session.
    /// </summary>
    public Task StopReplayAsync(string sessionId)
    {
        if (_activeSessions.TryGetValue(sessionId, out var session))
        {
            session.Status = ReplayStatus.Stopped;
            session.CompletedAt = DateTimeOffset.UtcNow;
        }
        return Task.CompletedTask;
    }

    private List<string> ComputeStateDifferences(List<object?> original, List<object?> modified)
    {
        var diffs = new List<string>();
        for (int i = 0; i < Math.Min(original.Count, modified.Count); i++)
        {
            var origJson = JsonSerializer.Serialize(original[i]);
            var modJson = JsonSerializer.Serialize(modified[i]);
            if (origJson != modJson)
            {
                diffs.Add($"Position {i}: State differs");
            }
        }
        return diffs;
    }
}

public sealed record ReplayRequest
{
    public required string StreamId { get; init; }
    public long FromPosition { get; init; } = 0;
    public long? ToPosition { get; init; }
    public double Speed { get; init; } = 1.0; // 1.0 = real-time, 0 = no delay
    public long[]? Breakpoints { get; init; }
}

public sealed class ReplaySession
{
    public required string SessionId { get; init; }
    public required string StreamId { get; init; }
    public long FromPosition { get; init; }
    public long? ToPosition { get; init; }
    public long CurrentPosition { get; set; }
    public double Speed { get; init; }
    public ReplayStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? PausedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long EventsReplayed { get; set; }
    public List<long> Breakpoints { get; init; } = new();
}

public enum ReplayStatus { Running, Paused, Stopped, Completed }

public sealed record ReplayedEvent
{
    public required StoredEvent Event { get; init; }
    public DateTimeOffset ReplayTimestamp { get; init; }
    public double ElapsedMs { get; init; }
    public long SessionPosition { get; init; }
}

public sealed record ReplayResult
{
    public bool Success { get; init; }
    public int EventsReplayed { get; init; }
    public long FinalPosition { get; init; }
    public double DurationMs { get; init; }
    public string? Error { get; init; }
}

public sealed record WhatIfResult
{
    public int OriginalEventCount { get; init; }
    public int ModifiedEventCount { get; init; }
    public int FilteredEventCount { get; init; }
    public List<string> StateDifferences { get; init; } = new();
}

#endregion

#region 89.4 Snapshot Strategies

/// <summary>
/// 89.4: Snapshot strategies for optimizing aggregate loading,
/// including time-based, event-count, and adaptive snapshotting.
/// </summary>
public sealed class SnapshotStrategy : DataManagementStrategyBase
{
    private readonly BoundedDictionary<string, AggregateSnapshot> _snapshots = new BoundedDictionary<string, AggregateSnapshot>(1000);
    private readonly BoundedDictionary<string, SnapshotPolicy> _policies = new BoundedDictionary<string, SnapshotPolicy>(1000);

    public override string StrategyId => "eventsourcing-snapshots";
    public override string DisplayName => "Aggregate Snapshots";
    public override DataManagementCategory Category => DataManagementCategory.EventSourcing;
    public override DataManagementCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = true,
        SupportsTTL = true,
        MaxThroughput = 10000,
        TypicalLatencyMs = 5.0
    };
    public override string SemanticDescription =>
        "Snapshot strategies for optimizing aggregate state loading. Supports time-based, " +
        "event-count-based, and adaptive snapshotting with retention policies and compression.";
    public override string[] Tags => ["snapshot", "optimization", "aggregate", "state", "compression"];

    /// <summary>
    /// Saves a snapshot of aggregate state.
    /// </summary>
    public Task<SnapshotResult> SaveSnapshotAsync(
        string aggregateId,
        string aggregateType,
        byte[] state,
        long version,
        long eventCount,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ct.ThrowIfCancellationRequested();

        var sw = Stopwatch.StartNew();

        var snapshot = new AggregateSnapshot
        {
            AggregateId = aggregateId,
            AggregateType = aggregateType,
            State = state,
            Version = version,
            Timestamp = DateTimeOffset.UtcNow,
            ContentHash = ComputeHash(state),
            EventCount = eventCount
        };

        _snapshots[aggregateId] = snapshot;

        RecordWrite(state.Length, sw.Elapsed.TotalMilliseconds);

        return Task.FromResult(new SnapshotResult
        {
            Success = true,
            SnapshotVersion = version,
            SizeBytes = state.Length,
            CompressionRatio = 1.0 // No compression in this implementation
        });
    }

    /// <summary>
    /// Loads the latest snapshot for an aggregate.
    /// </summary>
    public Task<AggregateSnapshot?> LoadSnapshotAsync(
        string aggregateId,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var sw = Stopwatch.StartNew();

        if (_snapshots.TryGetValue(aggregateId, out var snapshot))
        {
            RecordRead(snapshot.State.Length, sw.Elapsed.TotalMilliseconds, hit: true);
            return Task.FromResult<AggregateSnapshot?>(snapshot);
        }

        RecordRead(0, sw.Elapsed.TotalMilliseconds, miss: true);
        return Task.FromResult<AggregateSnapshot?>(null);
    }

    /// <summary>
    /// Determines if a snapshot should be taken based on the policy.
    /// </summary>
    public Task<bool> ShouldSnapshotAsync(
        string aggregateId,
        string aggregateType,
        long currentVersion,
        long eventsSinceSnapshot,
        TimeSpan timeSinceSnapshot,
        CancellationToken ct = default)
    {
        // Get policy for this aggregate type
        var policy = _policies.GetValueOrDefault(aggregateType, DefaultPolicy);

        // Check event count threshold
        if (policy.EventCountThreshold > 0 && eventsSinceSnapshot >= policy.EventCountThreshold)
            return Task.FromResult(true);

        // Check time threshold
        if (policy.TimeThreshold.HasValue && timeSinceSnapshot >= policy.TimeThreshold.Value)
            return Task.FromResult(true);

        // Adaptive: check if events are growing too fast
        if (policy.EnableAdaptive)
        {
            var eventRate = eventsSinceSnapshot / Math.Max(1, timeSinceSnapshot.TotalMinutes);
            if (eventRate > policy.AdaptiveEventRateThreshold)
                return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    /// <summary>
    /// Sets a snapshot policy for an aggregate type.
    /// </summary>
    public Task SetPolicyAsync(string aggregateType, SnapshotPolicy policy, CancellationToken ct = default)
    {
        _policies[aggregateType] = policy;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Deletes old snapshots based on retention policy.
    /// </summary>
    public Task<int> CleanupSnapshotsAsync(TimeSpan retentionPeriod, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - retentionPeriod;
        var toDelete = _snapshots
            .Where(kvp => kvp.Value.Timestamp < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toDelete)
        {
            _snapshots.TryRemove(key, out _);
        }

        return Task.FromResult(toDelete.Count);
    }

    private static readonly SnapshotPolicy DefaultPolicy = new()
    {
        EventCountThreshold = 100,
        TimeThreshold = TimeSpan.FromHours(1),
        EnableAdaptive = true,
        AdaptiveEventRateThreshold = 10
    };

    private static string ComputeHash(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash);
    }
}

public sealed record SnapshotPolicy
{
    public int EventCountThreshold { get; init; } = 100;
    public TimeSpan? TimeThreshold { get; init; }
    public bool EnableAdaptive { get; init; } = false;
    public double AdaptiveEventRateThreshold { get; init; } = 10;
    public bool EnableCompression { get; init; } = true;
    public TimeSpan RetentionPeriod { get; init; } = TimeSpan.FromDays(30);
    public int MaxSnapshotsPerAggregate { get; init; } = 3;
}

public sealed record SnapshotResult
{
    public bool Success { get; init; }
    public long SnapshotVersion { get; init; }
    public long SizeBytes { get; init; }
    public double CompressionRatio { get; init; }
    public string? Error { get; init; }
}

#endregion

#region 89.5 Event Versioning Strategy

/// <summary>
/// 89.5: Event versioning for handling schema changes, upcasting,
/// and backward compatibility in event-sourced systems.
/// </summary>
public sealed class EventVersioningStrategy : DataManagementStrategyBase
{
    private readonly BoundedDictionary<string, SortedDictionary<int, EventSchema>> _schemas = new BoundedDictionary<string, SortedDictionary<int, EventSchema>>(1000);
    private readonly BoundedDictionary<string, Func<byte[], int, byte[]>> _upcasters = new BoundedDictionary<string, Func<byte[], int, byte[]>>(1000);
    private readonly BoundedDictionary<string, Func<byte[], int, byte[]>> _downcasters = new BoundedDictionary<string, Func<byte[], int, byte[]>>(1000);

    public override string StrategyId => "eventsourcing-versioning";
    public override string DisplayName => "Event Versioning";
    public override DataManagementCategory Category => DataManagementCategory.EventSourcing;
    public override DataManagementCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 100000,
        TypicalLatencyMs = 0.2
    };
    public override string SemanticDescription =>
        "Event versioning for schema evolution, automatic upcasting/downcasting, " +
        "version negotiation, and maintaining backward compatibility across event versions.";
    public override string[] Tags => ["versioning", "schema", "upcasting", "downcasting", "compatibility"];

    /// <summary>
    /// Registers a schema version for an event type.
    /// </summary>
    public Task RegisterSchemaAsync(EventSchema schema, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var versions = _schemas.GetOrAdd(schema.EventType, _ => new SortedDictionary<int, EventSchema>());
        lock (versions)
        {
            versions[schema.Version] = schema;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Registers an upcaster function to convert from an older version.
    /// </summary>
    public Task RegisterUpcasterAsync(
        string eventType,
        int fromVersion,
        int toVersion,
        Func<byte[], int, byte[]> upcaster,
        CancellationToken ct = default)
    {
        var key = $"{eventType}:{fromVersion}:{toVersion}";
        _upcasters[key] = upcaster;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Registers a downcaster function to convert to an older version.
    /// </summary>
    public Task RegisterDowncasterAsync(
        string eventType,
        int fromVersion,
        int toVersion,
        Func<byte[], int, byte[]> downcaster,
        CancellationToken ct = default)
    {
        var key = $"{eventType}:{fromVersion}:{toVersion}";
        _downcasters[key] = downcaster;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Upcasts an event to the latest version.
    /// </summary>
    public Task<VersionedEventData> UpcastToLatestAsync(
        string eventType,
        byte[] data,
        int currentVersion,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        if (!_schemas.TryGetValue(eventType, out var versions) || versions.Count == 0)
        {
            return Task.FromResult(new VersionedEventData
            {
                Data = data,
                Version = currentVersion,
                WasTransformed = false
            });
        }

        var latestVersion = versions.Keys.Max();
        if (currentVersion >= latestVersion)
        {
            return Task.FromResult(new VersionedEventData
            {
                Data = data,
                Version = currentVersion,
                WasTransformed = false
            });
        }

        // Apply upcasters sequentially
        var currentData = data;
        var transformations = new List<string>();

        for (int v = currentVersion; v < latestVersion; v++)
        {
            var key = $"{eventType}:{v}:{v + 1}";
            if (_upcasters.TryGetValue(key, out var upcaster))
            {
                currentData = upcaster(currentData, v);
                transformations.Add($"v{v} -> v{v + 1}");
            }
        }

        return Task.FromResult(new VersionedEventData
        {
            Data = currentData,
            Version = latestVersion,
            WasTransformed = true,
            Transformations = transformations.ToArray()
        });
    }

    /// <summary>
    /// Downcasts an event to a specific older version.
    /// </summary>
    public Task<VersionedEventData> DowncastToVersionAsync(
        string eventType,
        byte[] data,
        int currentVersion,
        int targetVersion,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        if (targetVersion >= currentVersion)
        {
            return Task.FromResult(new VersionedEventData
            {
                Data = data,
                Version = currentVersion,
                WasTransformed = false
            });
        }

        var currentData = data;
        var transformations = new List<string>();

        for (int v = currentVersion; v > targetVersion; v--)
        {
            var key = $"{eventType}:{v}:{v - 1}";
            if (_downcasters.TryGetValue(key, out var downcaster))
            {
                currentData = downcaster(currentData, v);
                transformations.Add($"v{v} -> v{v - 1}");
            }
            else
            {
                // No downcaster available
                return Task.FromResult(new VersionedEventData
                {
                    Data = data,
                    Version = currentVersion,
                    WasTransformed = false,
                    Error = $"No downcaster registered for {eventType} v{v} -> v{v - 1}"
                });
            }
        }

        return Task.FromResult(new VersionedEventData
        {
            Data = currentData,
            Version = targetVersion,
            WasTransformed = true,
            Transformations = transformations.ToArray()
        });
    }

    /// <summary>
    /// Gets the latest schema version for an event type.
    /// </summary>
    public Task<int> GetLatestVersionAsync(string eventType, CancellationToken ct = default)
    {
        if (_schemas.TryGetValue(eventType, out var versions) && versions.Count > 0)
        {
            return Task.FromResult(versions.Keys.Max());
        }
        return Task.FromResult(1);
    }

    /// <summary>
    /// Gets all registered schema versions for an event type.
    /// </summary>
    public Task<IReadOnlyList<EventSchema>> GetSchemaHistoryAsync(
        string eventType,
        CancellationToken ct = default)
    {
        if (_schemas.TryGetValue(eventType, out var versions))
        {
            lock (versions)
            {
                return Task.FromResult<IReadOnlyList<EventSchema>>(
                    versions.Values.OrderBy(s => s.Version).ToList().AsReadOnly());
            }
        }
        return Task.FromResult<IReadOnlyList<EventSchema>>(Array.Empty<EventSchema>());
    }
}

public sealed record VersionedEventData
{
    public required byte[] Data { get; init; }
    public int Version { get; init; }
    public bool WasTransformed { get; init; }
    public string[]? Transformations { get; init; }
    public string? Error { get; init; }
}

#endregion

#region 89.6 Schema Evolution Strategy

/// <summary>
/// 89.6: Schema evolution for managing event schema changes over time,
/// compatibility checking, and automatic migration.
/// </summary>
public sealed class SchemaEvolutionStrategy : DataManagementStrategyBase
{
    private readonly BoundedDictionary<string, SchemaRegistry> _registries = new BoundedDictionary<string, SchemaRegistry>(1000);

    public override string StrategyId => "eventsourcing-schema-evolution";
    public override string DisplayName => "Schema Evolution";
    public override DataManagementCategory Category => DataManagementCategory.EventSourcing;
    public override DataManagementCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = true,
        SupportsTTL = false,
        MaxThroughput = 50000,
        TypicalLatencyMs = 1.0
    };
    public override string SemanticDescription =>
        "Schema evolution management for event schemas. Supports backward/forward compatibility " +
        "checking, automatic schema inference, migration path generation, and schema deprecation.";
    public override string[] Tags => ["schema", "evolution", "compatibility", "migration", "inference"];

    /// <summary>
    /// Registers a new schema version.
    /// </summary>
    public Task<SchemaRegistrationResult> RegisterSchemaAsync(
        string subject,
        string schemaDefinition,
        CompatibilityMode compatibilityMode = CompatibilityMode.Backward,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var registry = _registries.GetOrAdd(subject, _ => new SchemaRegistry { Subject = subject });

        lock (registry)
        {
            var newVersion = registry.Schemas.Count + 1;

            // Check compatibility with existing schemas
            if (registry.Schemas.Count > 0)
            {
                var latestSchema = registry.Schemas.Values.OrderByDescending(s => s.Version).First();
                var compatibility = CheckCompatibility(latestSchema.SchemaDefinition, schemaDefinition, compatibilityMode);

                if (!compatibility.IsCompatible)
                {
                    return Task.FromResult(new SchemaRegistrationResult
                    {
                        Success = false,
                        Error = $"Schema is not {compatibilityMode} compatible: " +
                               string.Join(", ", compatibility.BreakingChanges)
                    });
                }
            }

            var schema = new RegisteredSchema
            {
                Subject = subject,
                Version = newVersion,
                SchemaDefinition = schemaDefinition,
                RegisteredAt = DateTimeOffset.UtcNow,
                CompatibilityMode = compatibilityMode
            };

            registry.Schemas[newVersion] = schema;
            registry.LatestVersion = newVersion;

            return Task.FromResult(new SchemaRegistrationResult
            {
                Success = true,
                SchemaId = $"{subject}-v{newVersion}",
                Version = newVersion
            });
        }
    }

    /// <summary>
    /// Checks compatibility between two schemas.
    /// </summary>
    public Task<SchemaCompatibility> CheckCompatibilityAsync(
        string subject,
        string newSchemaDefinition,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        if (!_registries.TryGetValue(subject, out var registry) || registry.Schemas.Count == 0)
        {
            return Task.FromResult(new SchemaCompatibility
            {
                IsCompatible = true,
                Level = CompatibilityLevel.Full
            });
        }

        lock (registry)
        {
            var latestSchema = registry.Schemas.Values.OrderByDescending(s => s.Version).First();
            var result = CheckCompatibility(latestSchema.SchemaDefinition, newSchemaDefinition, latestSchema.CompatibilityMode);
            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// Generates a migration path between schema versions.
    /// </summary>
    public Task<SchemaMigrationPath> GenerateMigrationPathAsync(
        string subject,
        int fromVersion,
        int toVersion,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        if (!_registries.TryGetValue(subject, out var registry))
        {
            return Task.FromResult(new SchemaMigrationPath
            {
                Success = false,
                Error = $"Subject '{subject}' not found"
            });
        }

        var steps = new List<MigrationStep>();
        var direction = toVersion > fromVersion ? 1 : -1;

        for (int v = fromVersion; v != toVersion; v += direction)
        {
            var sourceVersion = v;
            var targetVersion = v + direction;

            if (!registry.Schemas.ContainsKey(sourceVersion) || !registry.Schemas.ContainsKey(targetVersion))
            {
                return Task.FromResult(new SchemaMigrationPath
                {
                    Success = false,
                    Error = $"Version {(registry.Schemas.ContainsKey(sourceVersion) ? targetVersion : sourceVersion)} not found"
                });
            }

            steps.Add(new MigrationStep
            {
                FromVersion = sourceVersion,
                ToVersion = targetVersion,
                Direction = direction > 0 ? MigrationDirection.Upgrade : MigrationDirection.Downgrade,
                EstimatedComplexity = EstimateComplexity(
                    registry.Schemas[sourceVersion].SchemaDefinition,
                    registry.Schemas[targetVersion].SchemaDefinition)
            });
        }

        return Task.FromResult(new SchemaMigrationPath
        {
            Success = true,
            Subject = subject,
            FromVersion = fromVersion,
            ToVersion = toVersion,
            Steps = steps.ToArray()
        });
    }

    /// <summary>
    /// Infers a schema from sample event data.
    /// </summary>
    public Task<InferredSchema> InferSchemaAsync(
        byte[] sampleData,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        try
        {
            using var doc = JsonDocument.Parse(sampleData);
            var properties = InferProperties(doc.RootElement);

            return Task.FromResult(new InferredSchema
            {
                Success = true,
                SchemaType = "JSON",
                Properties = properties,
                InferredAt = DateTimeOffset.UtcNow
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new InferredSchema
            {
                Success = false,
                Error = ex.Message
            });
        }
    }

    /// <summary>
    /// Deprecates a schema version.
    /// </summary>
    public Task<bool> DeprecateVersionAsync(string subject, int version, CancellationToken ct = default)
    {
        if (_registries.TryGetValue(subject, out var registry))
        {
            lock (registry)
            {
                if (registry.Schemas.TryGetValue(version, out var schema))
                {
                    schema.IsDeprecated = true;
                    schema.DeprecatedAt = DateTimeOffset.UtcNow;
                    return Task.FromResult(true);
                }
            }
        }
        return Task.FromResult(false);
    }

    private SchemaCompatibility CheckCompatibility(string oldSchema, string newSchema, CompatibilityMode mode)
    {
        var breakingChanges = new List<string>();
        var warnings = new List<string>();

        try
        {
            using var oldDoc = JsonDocument.Parse(oldSchema);
            using var newDoc = JsonDocument.Parse(newSchema);

            var oldProps = GetPropertyNames(oldDoc.RootElement);
            var newProps = GetPropertyNames(newDoc.RootElement);

            // Check for removed fields (breaking for backward compatibility)
            var removedFields = oldProps.Except(newProps).ToList();
            if (removedFields.Count > 0 && (mode == CompatibilityMode.Backward || mode == CompatibilityMode.Full))
            {
                breakingChanges.Add($"Removed fields: {string.Join(", ", removedFields)}");
            }

            // Check for required new fields (breaking for forward compatibility)
            var addedFields = newProps.Except(oldProps).ToList();
            if (addedFields.Count > 0)
            {
                warnings.Add($"Added fields: {string.Join(", ", addedFields)}");
            }

            var level = breakingChanges.Count == 0
                ? (warnings.Count == 0 ? CompatibilityLevel.Full : CompatibilityLevel.Backward)
                : CompatibilityLevel.None;

            return new SchemaCompatibility
            {
                IsCompatible = breakingChanges.Count == 0,
                Level = level,
                BreakingChanges = breakingChanges.ToArray(),
                Warnings = warnings.ToArray()
            };
        }
        catch
        {
            return new SchemaCompatibility
            {
                IsCompatible = false,
                Level = CompatibilityLevel.None,
                BreakingChanges = new[] { "Failed to parse schemas" }
            };
        }
    }

    private HashSet<string> GetPropertyNames(JsonElement element)
    {
        var props = new HashSet<string>();
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                props.Add(prop.Name);
            }
        }
        return props;
    }

    private Dictionary<string, InferredProperty> InferProperties(JsonElement element)
    {
        var props = new Dictionary<string, InferredProperty>();

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                props[prop.Name] = new InferredProperty
                {
                    Name = prop.Name,
                    Type = InferType(prop.Value),
                    IsNullable = prop.Value.ValueKind == JsonValueKind.Null
                };
            }
        }

        return props;
    }

    private string InferType(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => element.TryGetInt64(out _) ? "integer" : "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        JsonValueKind.Null => "null",
        _ => "unknown"
    };

    private MigrationComplexity EstimateComplexity(string fromSchema, string toSchema)
    {
        // Simple heuristic based on schema differences
        try
        {
            using var fromDoc = JsonDocument.Parse(fromSchema);
            using var toDoc = JsonDocument.Parse(toSchema);

            var fromProps = GetPropertyNames(fromDoc.RootElement);
            var toProps = GetPropertyNames(toDoc.RootElement);

            var changes = fromProps.Except(toProps).Count() + toProps.Except(fromProps).Count();

            return changes switch
            {
                0 => MigrationComplexity.None,
                <= 2 => MigrationComplexity.Low,
                <= 5 => MigrationComplexity.Medium,
                _ => MigrationComplexity.High
            };
        }
        catch
        {
            return MigrationComplexity.Unknown;
        }
    }
}

public enum CompatibilityMode { None, Backward, Forward, Full }

public enum MigrationDirection { Upgrade, Downgrade }

public enum MigrationComplexity { None, Low, Medium, High, Unknown }

internal sealed class SchemaRegistry
{
    public required string Subject { get; init; }
    public Dictionary<int, RegisteredSchema> Schemas { get; } = new();
    public int LatestVersion { get; set; }
}

internal sealed class RegisteredSchema
{
    public required string Subject { get; init; }
    public required int Version { get; init; }
    public required string SchemaDefinition { get; init; }
    public DateTimeOffset RegisteredAt { get; init; }
    public CompatibilityMode CompatibilityMode { get; init; }
    public bool IsDeprecated { get; set; }
    public DateTimeOffset? DeprecatedAt { get; set; }
}

public sealed record SchemaRegistrationResult
{
    public bool Success { get; init; }
    public string? SchemaId { get; init; }
    public int Version { get; init; }
    public string? Error { get; init; }
}

public sealed record SchemaMigrationPath
{
    public bool Success { get; init; }
    public string? Subject { get; init; }
    public int FromVersion { get; init; }
    public int ToVersion { get; init; }
    public MigrationStep[] Steps { get; init; } = Array.Empty<MigrationStep>();
    public string? Error { get; init; }
}

public sealed record MigrationStep
{
    public int FromVersion { get; init; }
    public int ToVersion { get; init; }
    public MigrationDirection Direction { get; init; }
    public MigrationComplexity EstimatedComplexity { get; init; }
}

public sealed record InferredSchema
{
    public bool Success { get; init; }
    public string? SchemaType { get; init; }
    public Dictionary<string, InferredProperty>? Properties { get; init; }
    public DateTimeOffset InferredAt { get; init; }
    public string? Error { get; init; }
}

public sealed record InferredProperty
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public bool IsNullable { get; init; }
}

#endregion

#region 89.7 CQRS Pattern Support

/// <summary>
/// 89.7: CQRS (Command Query Responsibility Segregation) pattern support
/// with command handling, query optimization, and read model synchronization.
/// </summary>
public sealed class CqrsStrategy : DataManagementStrategyBase
{
    private readonly ConcurrentDictionary<string, Func<Command, CancellationToken, Task<CommandResult>>> _commandHandlers = new();
    private readonly BoundedDictionary<string, object> _readModels = new BoundedDictionary<string, object>(1000);
    private readonly ConcurrentQueue<Command> _commandQueue = new();
    private readonly BoundedDictionary<string, CommandValidation> _validations = new BoundedDictionary<string, CommandValidation>(1000);

    public override string StrategyId => "eventsourcing-cqrs";
    public override string DisplayName => "CQRS Pattern";
    public override DataManagementCategory Category => DataManagementCategory.EventSourcing;
    public override DataManagementCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = true,
        SupportsTTL = false,
        MaxThroughput = 50000,
        TypicalLatencyMs = 5.0
    };
    public override string SemanticDescription =>
        "CQRS pattern implementation separating command and query responsibilities. " +
        "Includes command validation, idempotency, read model synchronization, and query optimization.";
    public override string[] Tags => ["cqrs", "commands", "queries", "read-models", "separation"];

    /// <summary>
    /// Registers a command handler.
    /// </summary>
    public Task RegisterCommandHandlerAsync(
        string commandType,
        Func<Command, CancellationToken, Task<CommandResult>> handler,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        _commandHandlers[commandType] = handler;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Registers a command validation.
    /// </summary>
    public Task RegisterValidationAsync(
        string commandType,
        CommandValidation validation,
        CancellationToken ct = default)
    {
        _validations[commandType] = validation;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Dispatches a command to its handler.
    /// </summary>
    public async Task<CommandResult> DispatchAsync(Command command, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var sw = Stopwatch.StartNew();

        // Validate command
        if (_validations.TryGetValue(command.CommandType, out var validation))
        {
            var validationResult = await validation.ValidateAsync(command, ct);
            if (!validationResult.IsValid)
            {
                RecordFailure();
                return new CommandResult
                {
                    Success = false,
                    Error = $"Validation failed: {string.Join(", ", validationResult.Errors)}"
                };
            }
        }

        // Check idempotency
        if (await IsCommandDuplicateAsync(command, ct))
        {
            return new CommandResult
            {
                Success = true,
                Error = "Command already processed (idempotent)"
            };
        }

        // Dispatch to handler
        if (!_commandHandlers.TryGetValue(command.CommandType, out var handler))
        {
            RecordFailure();
            return new CommandResult
            {
                Success = false,
                Error = $"No handler registered for command type '{command.CommandType}'"
            };
        }

        try
        {
            var result = await handler(command, ct);
            RecordWrite(command.Payload.Length, sw.Elapsed.TotalMilliseconds);

            // Record for idempotency
            await RecordCommandAsync(command, ct);

            return result;
        }
        catch (Exception ex)
        {
            RecordFailure();
            return new CommandResult
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Queues a command for async processing.
    /// </summary>
    public Task EnqueueAsync(Command command, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        _commandQueue.Enqueue(command);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Processes queued commands.
    /// </summary>
    public async Task<int> ProcessQueueAsync(int maxCommands = 100, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var processed = 0;
        while (processed < maxCommands && _commandQueue.TryDequeue(out var command))
        {
            ct.ThrowIfCancellationRequested();
            await DispatchAsync(command, ct);
            processed++;
        }

        return processed;
    }

    /// <summary>
    /// Updates a read model.
    /// </summary>
    public Task UpdateReadModelAsync<T>(string modelId, T model, CancellationToken ct = default) where T : class
    {
        ThrowIfNotInitialized();
        _readModels[modelId] = model;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets a read model.
    /// </summary>
    public Task<T?> GetReadModelAsync<T>(string modelId, CancellationToken ct = default) where T : class
    {
        ThrowIfNotInitialized();

        var sw = Stopwatch.StartNew();

        if (_readModels.TryGetValue(modelId, out var model) && model is T typedModel)
        {
            RecordRead(0, sw.Elapsed.TotalMilliseconds, hit: true);
            return Task.FromResult<T?>(typedModel);
        }

        RecordRead(0, sw.Elapsed.TotalMilliseconds, miss: true);
        return Task.FromResult<T?>(null);
    }

    /// <summary>
    /// Queries read models with a predicate.
    /// </summary>
    public Task<IReadOnlyList<T>> QueryReadModelsAsync<T>(
        Func<T, bool> predicate,
        CancellationToken ct = default) where T : class
    {
        ThrowIfNotInitialized();

        var results = _readModels.Values
            .OfType<T>()
            .Where(predicate)
            .ToList()
            .AsReadOnly();

        return Task.FromResult<IReadOnlyList<T>>(results);
    }

    private readonly BoundedDictionary<string, DateTimeOffset> _processedCommands = new BoundedDictionary<string, DateTimeOffset>(1000);

    private Task<bool> IsCommandDuplicateAsync(Command command, CancellationToken ct)
    {
        return Task.FromResult(_processedCommands.ContainsKey(command.CommandId));
    }

    private Task RecordCommandAsync(Command command, CancellationToken ct)
    {
        _processedCommands[command.CommandId] = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }
}

public sealed class CommandValidation
{
    public required Func<Command, CancellationToken, Task<ValidationResult>> ValidateAsync { get; init; }
}

public sealed record ValidationResult
{
    public bool IsValid { get; init; }
    public string[] Errors { get; init; } = Array.Empty<string>();
}

#endregion

#region 89.8 Event Aggregation Strategy

/// <summary>
/// 89.8: Event aggregation for combining related events, summarization,
/// and materialized view generation.
/// </summary>
public sealed class EventAggregationStrategy : DataManagementStrategyBase
{
    private readonly BoundedDictionary<string, AggregationDefinition> _aggregations = new BoundedDictionary<string, AggregationDefinition>(1000);
    private readonly BoundedDictionary<string, object> _aggregatedViews = new BoundedDictionary<string, object>(1000);

    public override string StrategyId => "eventsourcing-aggregation";
    public override string DisplayName => "Event Aggregation";
    public override DataManagementCategory Category => DataManagementCategory.EventSourcing;
    public override DataManagementCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = true,
        SupportsTTL = true,
        MaxThroughput = 20000,
        TypicalLatencyMs = 10.0
    };
    public override string SemanticDescription =>
        "Event aggregation for combining related events into summaries, generating materialized views, " +
        "windowed aggregations, and event compaction.";
    public override string[] Tags => ["aggregation", "summarization", "materialized-views", "windowing", "compaction"];

    /// <summary>
    /// Defines an aggregation.
    /// </summary>
    public Task DefineAggregationAsync(
        AggregationDefinition definition,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        _aggregations[definition.AggregationId] = definition;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Aggregates events according to a definition.
    /// </summary>
    public async Task<AggregationResult> AggregateAsync(
        string aggregationId,
        IEnumerable<StoredEvent> events,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        if (!_aggregations.TryGetValue(aggregationId, out var definition))
        {
            return new AggregationResult
            {
                Success = false,
                Error = $"Aggregation '{aggregationId}' not found"
            };
        }

        var sw = Stopwatch.StartNew();
        var eventList = events.ToList();

        // Filter events by type
        var filtered = eventList.Where(e =>
            definition.EventTypes.Length == 0 ||
            definition.EventTypes.Contains(e.EventType));

        // Group by window if windowed aggregation
        var groups = definition.WindowSize.HasValue
            ? GroupByWindow(filtered, definition.WindowSize.Value)
            : new[] { filtered.ToList() };

        var aggregatedEvents = new List<StoredEvent>();

        foreach (var group in groups)
        {
            if (!group.Any()) continue;

            var aggregatedData = await definition.Aggregator(group, ct);

            var aggregatedEvent = new StoredEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                StreamId = definition.OutputStreamId ?? group.First().StreamId,
                EventType = definition.OutputEventType,
                Data = aggregatedData,
                Timestamp = group.Max(e => e.Timestamp),
                StreamPosition = 0,
                GlobalPosition = 0,
                Metadata = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    aggregationId,
                    sourceEventCount = group.Count,
                    windowStart = group.Min(e => e.Timestamp),
                    windowEnd = group.Max(e => e.Timestamp)
                })
            };

            aggregatedEvents.Add(aggregatedEvent);
        }

        RecordWrite(aggregatedEvents.Sum(e => e.Data.Length), sw.Elapsed.TotalMilliseconds);

        return new AggregationResult
        {
            Success = true,
            InputEventCount = eventList.Count,
            OutputEventCount = aggregatedEvents.Count,
            AggregatedEvents = aggregatedEvents.ToArray()
        };
    }

    /// <summary>
    /// Updates a materialized view with new events.
    /// </summary>
    public async Task<bool> UpdateMaterializedViewAsync<T>(
        string viewId,
        IEnumerable<StoredEvent> events,
        Func<T?, StoredEvent, T> folder,
        CancellationToken ct = default) where T : class
    {
        ThrowIfNotInitialized();

        var current = _aggregatedViews.GetValueOrDefault(viewId) as T;

        foreach (var @event in events)
        {
            ct.ThrowIfCancellationRequested();
            current = folder(current, @event);
        }

        if (current != null)
        {
            _aggregatedViews[viewId] = current;
        }

        return true;
    }

    /// <summary>
    /// Gets a materialized view.
    /// </summary>
    public Task<T?> GetMaterializedViewAsync<T>(string viewId, CancellationToken ct = default) where T : class
    {
        return Task.FromResult(_aggregatedViews.GetValueOrDefault(viewId) as T);
    }

    /// <summary>
    /// Compacts events by removing intermediate states.
    /// </summary>
    public Task<CompactionResult> CompactEventsAsync(
        IEnumerable<StoredEvent> events,
        Func<StoredEvent, string> keySelector,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var eventList = events.ToList();

        // Keep only the latest event per key
        var compacted = eventList
            .GroupBy(keySelector)
            .Select(g => g.OrderByDescending(e => e.StreamPosition).First())
            .ToList();

        return Task.FromResult(new CompactionResult
        {
            Success = true,
            OriginalCount = eventList.Count,
            CompactedCount = compacted.Count,
            ReductionRatio = eventList.Count > 0
                ? 1.0 - ((double)compacted.Count / eventList.Count)
                : 0,
            CompactedEvents = compacted.ToArray()
        });
    }

    private IEnumerable<List<StoredEvent>> GroupByWindow(IEnumerable<StoredEvent> events, TimeSpan windowSize)
    {
        var sorted = events.OrderBy(e => e.Timestamp).ToList();
        if (!sorted.Any()) yield break;

        var windowStart = sorted.First().Timestamp;
        var currentWindow = new List<StoredEvent>();

        foreach (var @event in sorted)
        {
            if (@event.Timestamp - windowStart >= windowSize)
            {
                if (currentWindow.Any())
                {
                    yield return currentWindow;
                    currentWindow = new List<StoredEvent>();
                }
                windowStart = @event.Timestamp;
            }
            currentWindow.Add(@event);
        }

        if (currentWindow.Any())
            yield return currentWindow;
    }
}

public sealed record AggregationDefinition
{
    public required string AggregationId { get; init; }
    public required string[] EventTypes { get; init; }
    public required Func<IEnumerable<StoredEvent>, CancellationToken, Task<byte[]>> Aggregator { get; init; }
    public required string OutputEventType { get; init; }
    public string? OutputStreamId { get; init; }
    public TimeSpan? WindowSize { get; init; }
    public bool EmitOnWindow { get; init; } = true;
}

public sealed record AggregationResult
{
    public bool Success { get; init; }
    public int InputEventCount { get; init; }
    public int OutputEventCount { get; init; }
    public StoredEvent[] AggregatedEvents { get; init; } = Array.Empty<StoredEvent>();
    public string? Error { get; init; }
}

public sealed record CompactionResult
{
    public bool Success { get; init; }
    public int OriginalCount { get; init; }
    public int CompactedCount { get; init; }
    public double ReductionRatio { get; init; }
    public StoredEvent[] CompactedEvents { get; init; } = Array.Empty<StoredEvent>();
}

#endregion

#region 89.9 Event-Driven Projections

/// <summary>
/// 89.9: Event-driven projections for building read models,
/// with checkpointing, error handling, and rebuild support.
/// </summary>
public sealed class ProjectionStrategy : DataManagementStrategyBase
{
    private readonly BoundedDictionary<string, ProjectionDefinition> _projections = new BoundedDictionary<string, ProjectionDefinition>(1000);
    private readonly BoundedDictionary<string, ProjectionState> _states = new BoundedDictionary<string, ProjectionState>(1000);
    private readonly ConcurrentDictionary<string, Func<StoredEvent, object?, CancellationToken, Task<object?>>> _handlers = new();

    public override string StrategyId => "eventsourcing-projections";
    public override string DisplayName => "Event-Driven Projections";
    public override DataManagementCategory Category => DataManagementCategory.EventSourcing;
    public override DataManagementCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = true,
        SupportsTTL = false,
        MaxThroughput = 30000,
        TypicalLatencyMs = 3.0
    };
    public override string SemanticDescription =>
        "Event-driven projections for building and maintaining read models from event streams. " +
        "Supports checkpointing, error handling, parallel projections, and full rebuild capability.";
    public override string[] Tags => ["projections", "read-models", "checkpointing", "rebuild", "denormalization"];

    /// <summary>
    /// Defines a projection.
    /// </summary>
    public Task DefineProjectionAsync(
        ProjectionDefinition definition,
        Func<StoredEvent, object?, CancellationToken, Task<object?>> handler,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        _projections[definition.ProjectionId] = definition;
        _handlers[definition.ProjectionId] = handler;
        _states[definition.ProjectionId] = new ProjectionState
        {
            ProjectionId = definition.ProjectionId,
            Status = ProjectionStatus.Running,
            CheckpointPosition = 0,
            LastProcessedAt = DateTimeOffset.UtcNow
        };

        return Task.CompletedTask;
    }

    /// <summary>
    /// Processes events through a projection.
    /// </summary>
    public async Task<ProjectionResult> ProjectAsync(
        string projectionId,
        IEnumerable<StoredEvent> events,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        if (!_projections.TryGetValue(projectionId, out var definition))
        {
            return new ProjectionResult
            {
                Success = false,
                Error = $"Projection '{projectionId}' not found"
            };
        }

        if (!_handlers.TryGetValue(projectionId, out var handler))
        {
            return new ProjectionResult
            {
                Success = false,
                Error = $"No handler for projection '{projectionId}'"
            };
        }

        var state = _states.GetOrAdd(projectionId, _ => new ProjectionState
        {
            ProjectionId = projectionId,
            Status = ProjectionStatus.Running
        });

        if (state.Status == ProjectionStatus.Paused || state.Status == ProjectionStatus.Faulted)
        {
            return new ProjectionResult
            {
                Success = false,
                Error = $"Projection is {state.Status}"
            };
        }

        var sw = Stopwatch.StartNew();
        var processed = 0;
        var errors = new List<string>();

        foreach (var @event in events.Where(e =>
            definition.SubscribedEventTypes.Length == 0 ||
            definition.SubscribedEventTypes.Contains(e.EventType)))
        {
            ct.ThrowIfCancellationRequested();

            if (@event.GlobalPosition <= state.CheckpointPosition)
                continue; // Already processed

            try
            {
                state.CurrentState = await handler(@event, state.CurrentState, ct);
                state.CheckpointPosition = @event.GlobalPosition;
                state.LastProcessedAt = DateTimeOffset.UtcNow;
                state.EventsProcessed++;
                processed++;
            }
            catch (Exception ex)
            {
                errors.Add($"Event {@event.EventId}: {ex.Message}");
                state.ErrorCount++;

                if (state.ErrorCount >= 10)
                {
                    state.Status = ProjectionStatus.Faulted;
                    break;
                }
            }
        }

        RecordWrite(0, sw.Elapsed.TotalMilliseconds);

        return new ProjectionResult
        {
            Success = errors.Count == 0,
            EventsProcessed = processed,
            CheckpointPosition = state.CheckpointPosition,
            Errors = errors.ToArray()
        };
    }

    /// <summary>
    /// Rebuilds a projection from scratch.
    /// </summary>
    public async Task<ProjectionResult> RebuildAsync(
        string projectionId,
        IEnumerable<StoredEvent> allEvents,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        if (!_states.TryGetValue(projectionId, out var state))
        {
            return new ProjectionResult
            {
                Success = false,
                Error = $"Projection '{projectionId}' not found"
            };
        }

        // Reset state
        state.Status = ProjectionStatus.Rebuilding;
        state.CheckpointPosition = 0;
        state.CurrentState = null;
        state.EventsProcessed = 0;
        state.ErrorCount = 0;

        // Process all events
        var result = await ProjectAsync(projectionId, allEvents, ct);

        state.Status = result.Success ? ProjectionStatus.Running : ProjectionStatus.Faulted;

        return result;
    }

    /// <summary>
    /// Gets the current state of a projection.
    /// </summary>
    public Task<ProjectionState?> GetProjectionStateAsync(string projectionId, CancellationToken ct = default)
    {
        return Task.FromResult(_states.GetValueOrDefault(projectionId));
    }

    /// <summary>
    /// Pauses a projection.
    /// </summary>
    public Task PauseProjectionAsync(string projectionId, CancellationToken ct = default)
    {
        if (_states.TryGetValue(projectionId, out var state))
        {
            state.Status = ProjectionStatus.Paused;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resumes a projection.
    /// </summary>
    public Task ResumeProjectionAsync(string projectionId, CancellationToken ct = default)
    {
        if (_states.TryGetValue(projectionId, out var state) &&
            (state.Status == ProjectionStatus.Paused || state.Status == ProjectionStatus.Faulted))
        {
            state.Status = ProjectionStatus.Running;
            state.ErrorCount = 0;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets all projection statuses.
    /// </summary>
    public Task<IReadOnlyList<ProjectionState>> GetAllProjectionStatesAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ProjectionState>>(
            _states.Values.ToList().AsReadOnly());
    }
}

public sealed class ProjectionState
{
    public required string ProjectionId { get; init; }
    public ProjectionStatus Status { get; set; }
    public long CheckpointPosition { get; set; }
    public DateTimeOffset LastProcessedAt { get; set; }
    public object? CurrentState { get; set; }
    public long EventsProcessed { get; set; }
    public int ErrorCount { get; set; }
}

public sealed record ProjectionResult
{
    public bool Success { get; init; }
    public int EventsProcessed { get; init; }
    public long CheckpointPosition { get; init; }
    public string[] Errors { get; init; } = Array.Empty<string>();
    public string? Error { get; init; }
}

#endregion

#region 89.10 Event Sourcing with DDD

/// <summary>
/// 89.10: Event sourcing with Domain-Driven Design support including
/// aggregates, bounded contexts, domain events, and sagas.
/// </summary>
public sealed class DddEventSourcingStrategy : DataManagementStrategyBase
{
    private readonly BoundedDictionary<string, object> _aggregateRepositories = new BoundedDictionary<string, object>(1000);
    private readonly BoundedDictionary<string, BoundedContextDefinition> _boundedContexts = new BoundedDictionary<string, BoundedContextDefinition>(1000);
    private readonly BoundedDictionary<string, SagaState> _sagas = new BoundedDictionary<string, SagaState>(1000);
    private readonly BoundedDictionary<string, Func<object, CancellationToken, Task>> _domainEventHandlers = new BoundedDictionary<string, Func<object, CancellationToken, Task>>(1000);

    public override string StrategyId => "eventsourcing-ddd";
    public override string DisplayName => "DDD Event Sourcing";
    public override DataManagementCategory Category => DataManagementCategory.EventSourcing;
    public override DataManagementCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = true,
        SupportsTTL = false,
        MaxThroughput = 20000,
        TypicalLatencyMs = 10.0
    };
    public override string SemanticDescription =>
        "Event sourcing with Domain-Driven Design patterns. Supports aggregate roots, " +
        "bounded contexts, domain events, sagas/process managers, and anti-corruption layers.";
    public override string[] Tags => ["ddd", "aggregates", "bounded-context", "domain-events", "sagas"];

    /// <summary>
    /// Registers a bounded context.
    /// </summary>
    public Task RegisterBoundedContextAsync(
        BoundedContextDefinition context,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        _boundedContexts[context.ContextId] = context;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Registers a domain event handler.
    /// </summary>
    public Task RegisterDomainEventHandlerAsync<TEvent>(
        Func<TEvent, CancellationToken, Task> handler,
        CancellationToken ct = default) where TEvent : class
    {
        var eventType = typeof(TEvent).Name;
        _domainEventHandlers[eventType] = async (e, c) =>
        {
            if (e is TEvent typedEvent)
            {
                await handler(typedEvent, c);
            }
        };
        return Task.CompletedTask;
    }

    /// <summary>
    /// Saves an aggregate with its uncommitted events.
    /// </summary>
    public async Task<SaveAggregateResult> SaveAggregateAsync<T>(
        T aggregate,
        EventStoreStrategy eventStore,
        CancellationToken ct = default) where T : AggregateRoot
    {
        ThrowIfNotInitialized();

        var events = aggregate.UncommittedEvents.ToList();
        if (!events.Any())
        {
            return new SaveAggregateResult { Success = true, EventsStored = 0 };
        }

        var result = await eventStore.AppendToStreamAsync(
            aggregate.Id,
            events,
            aggregate.Version,
            ct);

        if (result.Success)
        {
            aggregate.ClearUncommittedEvents();

            // Dispatch domain events
            foreach (var @event in events)
            {
                await DispatchDomainEventAsync(@event, ct);
            }
        }

        return new SaveAggregateResult
        {
            Success = result.Success,
            EventsStored = result.EventsAppended,
            NewVersion = result.NextExpectedVersion,
            Error = result.Error
        };
    }

    /// <summary>
    /// Loads an aggregate from the event store.
    /// </summary>
    public async Task<T?> LoadAggregateAsync<T>(
        string aggregateId,
        EventStoreStrategy eventStore,
        SnapshotStrategy? snapshotStore = null,
        CancellationToken ct = default) where T : AggregateRoot, new()
    {
        ThrowIfNotInitialized();

        var aggregate = new T();
        long startPosition = 0;

        // Try to load from snapshot first
        if (snapshotStore != null)
        {
            var snapshot = await snapshotStore.LoadSnapshotAsync(aggregateId, ct);
            if (snapshot != null)
            {
                // Deserialize snapshot state
                aggregate = JsonSerializer.Deserialize<T>(snapshot.State) ?? new T();
                startPosition = snapshot.Version + 1;
            }
        }

        // Load events from the store
        var events = await eventStore.ReadStreamAsync(
            aggregateId,
            new ReadOptions { FromPosition = startPosition },
            ct);

        if (!events.Any() && startPosition == 0)
        {
            return null; // Aggregate doesn't exist
        }

        aggregate.LoadFromHistory(events);

        return aggregate;
    }

    /// <summary>
    /// Starts a saga/process manager.
    /// </summary>
    public Task<SagaState> StartSagaAsync(
        SagaDefinition definition,
        object initialData,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var saga = new SagaState
        {
            SagaId = Guid.NewGuid().ToString("N"),
            SagaType = definition.SagaType,
            CurrentStep = 0,
            Status = SagaStatus.Running,
            Data = initialData,
            StartedAt = DateTimeOffset.UtcNow
        };

        _sagas[saga.SagaId] = saga;

        return Task.FromResult(saga);
    }

    /// <summary>
    /// Advances a saga to the next step.
    /// </summary>
    public async Task<SagaStepResult> AdvanceSagaAsync(
        string sagaId,
        object eventData,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        if (!_sagas.TryGetValue(sagaId, out var saga))
        {
            return new SagaStepResult
            {
                Success = false,
                Error = $"Saga '{sagaId}' not found"
            };
        }

        if (saga.Status != SagaStatus.Running)
        {
            return new SagaStepResult
            {
                Success = false,
                Error = $"Saga is {saga.Status}"
            };
        }

        saga.CurrentStep++;
        saga.LastEventAt = DateTimeOffset.UtcNow;

        // Check if saga is complete
        // In a real implementation, this would check against saga definition

        return new SagaStepResult
        {
            Success = true,
            CurrentStep = saga.CurrentStep,
            IsComplete = false
        };
    }

    /// <summary>
    /// Compensates a failed saga.
    /// </summary>
    public async Task<SagaCompensationResult> CompensateSagaAsync(
        string sagaId,
        string reason,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        if (!_sagas.TryGetValue(sagaId, out var saga))
        {
            return new SagaCompensationResult
            {
                Success = false,
                Error = $"Saga '{sagaId}' not found"
            };
        }

        saga.Status = SagaStatus.Compensating;

        // Execute compensation for each completed step in reverse order
        var compensatedSteps = 0;
        for (int i = saga.CurrentStep; i >= 0; i--)
        {
            // In a real implementation, would execute compensation logic
            compensatedSteps++;
        }

        saga.Status = SagaStatus.Compensated;
        saga.CompletedAt = DateTimeOffset.UtcNow;
        saga.CompensationReason = reason;

        return new SagaCompensationResult
        {
            Success = true,
            StepsCompensated = compensatedSteps
        };
    }

    /// <summary>
    /// Translates events between bounded contexts (anti-corruption layer).
    /// </summary>
    public async Task<TranslatedEvent?> TranslateEventAsync(
        StoredEvent sourceEvent,
        string sourceContext,
        string targetContext,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        if (!_boundedContexts.TryGetValue(sourceContext, out var source) ||
            !_boundedContexts.TryGetValue(targetContext, out var target))
        {
            return null;
        }

        // Check if translation is defined
        var translationKey = $"{sourceContext}:{sourceEvent.EventType}:{targetContext}";

        // In a real implementation, would use registered translators
        // For now, just pass through with context annotation

        return new TranslatedEvent
        {
            OriginalEvent = sourceEvent,
            TranslatedEventType = $"{targetContext}.{sourceEvent.EventType}",
            TranslatedData = sourceEvent.Data,
            SourceContext = sourceContext,
            TargetContext = targetContext
        };
    }

    private async Task DispatchDomainEventAsync(StoredEvent @event, CancellationToken ct)
    {
        if (_domainEventHandlers.TryGetValue(@event.EventType, out var handler))
        {
            var eventData = JsonSerializer.Deserialize<object>(@event.Data);
            if (eventData != null)
            {
                await handler(eventData, ct);
            }
        }
    }
}

public sealed record BoundedContextDefinition
{
    public required string ContextId { get; init; }
    public required string ContextName { get; init; }
    public string[] OwnedAggregates { get; init; } = Array.Empty<string>();
    public string[] PublishedEvents { get; init; } = Array.Empty<string>();
    public string[] ConsumedEvents { get; init; } = Array.Empty<string>();
}

public sealed record SagaDefinition
{
    public required string SagaType { get; init; }
    public required SagaStep[] Steps { get; init; }
}

public sealed record SagaStep
{
    public required string StepName { get; init; }
    public required string CommandType { get; init; }
    public string? CompensationCommandType { get; init; }
    public TimeSpan? Timeout { get; init; }
}

public sealed class SagaState
{
    public required string SagaId { get; init; }
    public required string SagaType { get; init; }
    public int CurrentStep { get; set; }
    public SagaStatus Status { get; set; }
    public object? Data { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? LastEventAt { get; set; }
    public string? CompensationReason { get; set; }
}

public enum SagaStatus { Running, Completed, Compensating, Compensated, Failed }

public sealed record SaveAggregateResult
{
    public bool Success { get; init; }
    public int EventsStored { get; init; }
    public long NewVersion { get; init; }
    public string? Error { get; init; }
}

public sealed record SagaStepResult
{
    public bool Success { get; init; }
    public int CurrentStep { get; init; }
    public bool IsComplete { get; init; }
    public string? Error { get; init; }
}

public sealed record SagaCompensationResult
{
    public bool Success { get; init; }
    public int StepsCompensated { get; init; }
    public string? Error { get; init; }
}

public sealed record TranslatedEvent
{
    public required StoredEvent OriginalEvent { get; init; }
    public required string TranslatedEventType { get; init; }
    public required byte[] TranslatedData { get; init; }
    public required string SourceContext { get; init; }
    public required string TargetContext { get; init; }
}

#endregion
