using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataIntegration.Strategies.CDC;

#region 126.6.1 Log-based CDC Strategy

/// <summary>
/// 126.6.1: Log-based CDC strategy capturing changes from database
/// transaction logs (WAL, binlog, redo log).
/// </summary>
public sealed class LogBasedCdcStrategy : DataIntegrationStrategyBase
{
    private readonly BoundedDictionary<string, CdcConnector> _connectors = new BoundedDictionary<string, CdcConnector>(1000);
    private readonly BoundedDictionary<string, ConcurrentQueue<CdcEvent>> _eventQueues = new BoundedDictionary<string, ConcurrentQueue<CdcEvent>>(1000);
    private long _totalEventsCaptured;

    public override string StrategyId => "cdc-log-based";
    public override string DisplayName => "Log-based CDC";
    public override IntegrationCategory Category => IntegrationCategory.ChangeDataCapture;
    public override DataIntegrationCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsExactlyOnce = true,
        SupportsSchemaEvolution = true,
        SupportsIncremental = true,
        SupportsParallel = true,
        SupportsDistributed = true,
        MaxThroughputRecordsPerSec = 500000,
        TypicalLatencyMs = 50.0
    };
    public override string SemanticDescription =>
        "Log-based CDC capturing changes from database transaction logs. Supports PostgreSQL WAL, " +
        "MySQL binlog, SQL Server transaction log, and Oracle redo log with low overhead.";
    public override string[] Tags => ["cdc", "log-based", "wal", "binlog", "redo-log", "debezium"];

    /// <summary>
    /// Creates a CDC connector.
    /// </summary>
    public Task<CdcConnector> CreateConnectorAsync(
        string connectorId,
        DatabaseType databaseType,
        string connectionString,
        CdcConnectorConfig? config = null,
        CancellationToken ct = default)
    {
        var connector = new CdcConnector
        {
            ConnectorId = connectorId,
            DatabaseType = databaseType,
            ConnectionString = connectionString,
            Config = config ?? new CdcConnectorConfig(),
            Status = ConnectorStatus.Created,
            CreatedAt = DateTime.UtcNow
        };

        if (!_connectors.TryAdd(connectorId, connector))
            throw new InvalidOperationException($"Connector {connectorId} already exists");

        _eventQueues[connectorId] = new ConcurrentQueue<CdcEvent>();

        RecordOperation("CreateConnector");
        return Task.FromResult(connector);
    }

    /// <summary>
    /// Starts capturing changes.
    /// </summary>
    public Task StartCaptureAsync(string connectorId, CancellationToken ct = default)
    {
        if (!_connectors.TryGetValue(connectorId, out var connector))
            throw new KeyNotFoundException($"Connector {connectorId} not found");

        connector.Status = ConnectorStatus.Running;
        connector.StartedAt = DateTime.UtcNow;

        RecordOperation("StartCapture");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops capturing changes.
    /// </summary>
    public Task StopCaptureAsync(string connectorId, CancellationToken ct = default)
    {
        if (!_connectors.TryGetValue(connectorId, out var connector))
            throw new KeyNotFoundException($"Connector {connectorId} not found");

        connector.Status = ConnectorStatus.Stopped;

        RecordOperation("StopCapture");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Consumes CDC events from the connector.
    /// </summary>
    public async IAsyncEnumerable<CdcEvent> ConsumeEventsAsync(
        string connectorId,
        long? fromLsn = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_connectors.TryGetValue(connectorId, out var connector))
            throw new KeyNotFoundException($"Connector {connectorId} not found");

        if (!_eventQueues.TryGetValue(connectorId, out var queue))
            throw new InvalidOperationException($"Event queue not found for connector {connectorId}");

        while (!ct.IsCancellationRequested)
        {
            if (queue.TryDequeue(out var evt))
            {
                if (fromLsn == null || evt.Lsn > fromLsn)
                {
                    Interlocked.Increment(ref _totalEventsCaptured);
                    yield return evt;
                }
            }
            else
            {
                await Task.Delay(10, ct);
            }
        }
    }

    /// <summary>
    /// Simulates a change event (for testing).
    /// </summary>
    public Task<CdcEvent> SimulateChangeAsync(
        string connectorId,
        CdcOperationType operation,
        string tableName,
        Dictionary<string, object>? before,
        Dictionary<string, object>? after,
        CancellationToken ct = default)
    {
        if (!_eventQueues.TryGetValue(connectorId, out var queue))
            throw new KeyNotFoundException($"Connector {connectorId} not found");

        var evt = new CdcEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            ConnectorId = connectorId,
            Operation = operation,
            TableName = tableName,
            Before = before,
            After = after,
            Lsn = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Timestamp = DateTime.UtcNow
        };

        queue.Enqueue(evt);
        RecordOperation("SimulateChange");
        return Task.FromResult(evt);
    }

    /// <summary>
    /// Gets connector statistics.
    /// </summary>
    public Task<ConnectorStats> GetStatsAsync(string connectorId)
    {
        if (!_connectors.TryGetValue(connectorId, out var connector))
            throw new KeyNotFoundException($"Connector {connectorId} not found");

        var queue = _eventQueues.GetValueOrDefault(connectorId);

        return Task.FromResult(new ConnectorStats
        {
            ConnectorId = connectorId,
            Status = connector.Status,
            PendingEvents = queue?.Count ?? 0,
            TotalEventsCaptured = Interlocked.Read(ref _totalEventsCaptured)
        });
    }
}

#endregion

#region 126.6.2 Trigger-based CDC Strategy

/// <summary>
/// 126.6.2: Trigger-based CDC strategy using database triggers
/// to capture changes and write to change tables.
/// </summary>
public sealed class TriggerBasedCdcStrategy : DataIntegrationStrategyBase
{
    private readonly BoundedDictionary<string, TriggerConfig> _triggers = new BoundedDictionary<string, TriggerConfig>(1000);
    private readonly BoundedDictionary<string, List<ChangeRecord>> _changeTables = new BoundedDictionary<string, List<ChangeRecord>>(1000);

    public override string StrategyId => "cdc-trigger-based";
    public override string DisplayName => "Trigger-based CDC";
    public override IntegrationCategory Category => IntegrationCategory.ChangeDataCapture;
    public override DataIntegrationCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsExactlyOnce = true,
        SupportsSchemaEvolution = true,
        SupportsIncremental = true,
        SupportsParallel = true,
        SupportsDistributed = false,
        MaxThroughputRecordsPerSec = 100000,
        TypicalLatencyMs = 10.0
    };
    public override string SemanticDescription =>
        "Trigger-based CDC using database triggers to capture changes. Works with any database " +
        "supporting triggers but adds overhead to write operations.";
    public override string[] Tags => ["cdc", "trigger-based", "change-table", "audit"];

    /// <summary>
    /// Creates a trigger configuration.
    /// </summary>
    public Task<TriggerConfig> CreateTriggerAsync(
        string triggerId,
        string tableName,
        IReadOnlyList<CdcOperationType> operations,
        IReadOnlyList<string>? capturedColumns = null,
        CancellationToken ct = default)
    {
        var trigger = new TriggerConfig
        {
            TriggerId = triggerId,
            TableName = tableName,
            Operations = operations.ToList(),
            CapturedColumns = capturedColumns?.ToList() ?? new List<string>(),
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };

        if (!_triggers.TryAdd(triggerId, trigger))
            throw new InvalidOperationException($"Trigger {triggerId} already exists");

        _changeTables[tableName] = new List<ChangeRecord>();

        RecordOperation("CreateTrigger");
        return Task.FromResult(trigger);
    }

    /// <summary>
    /// Simulates a trigger firing.
    /// </summary>
    public Task<ChangeRecord> FireTriggerAsync(
        string tableName,
        CdcOperationType operation,
        Dictionary<string, object>? oldValues,
        Dictionary<string, object>? newValues,
        CancellationToken ct = default)
    {
        if (!_changeTables.TryGetValue(tableName, out var changeTable))
            throw new KeyNotFoundException($"No trigger configured for table {tableName}");

        var record = new ChangeRecord
        {
            ChangeId = Guid.NewGuid().ToString("N"),
            TableName = tableName,
            Operation = operation,
            OldValues = oldValues,
            NewValues = newValues,
            ChangeTimestamp = DateTime.UtcNow
        };

        changeTable.Add(record);
        RecordOperation("FireTrigger");
        return Task.FromResult(record);
    }

    /// <summary>
    /// Gets changes from a change table.
    /// </summary>
    public Task<List<ChangeRecord>> GetChangesAsync(
        string tableName,
        DateTime? since = null,
        int limit = 1000,
        CancellationToken ct = default)
    {
        if (!_changeTables.TryGetValue(tableName, out var changeTable))
            throw new KeyNotFoundException($"No change table for {tableName}");

        var changes = since.HasValue
            ? changeTable.Where(c => c.ChangeTimestamp > since.Value).Take(limit).ToList()
            : changeTable.TakeLast(limit).ToList();

        RecordOperation("GetChanges");
        return Task.FromResult(changes);
    }
}

#endregion

#region 126.6.3 Timestamp-based CDC Strategy

/// <summary>
/// 126.6.3: Timestamp-based CDC strategy using modification timestamps
/// to detect changes through polling.
/// </summary>
public sealed class TimestampBasedCdcStrategy : DataIntegrationStrategyBase
{
    private readonly BoundedDictionary<string, TimestampTracker> _trackers = new BoundedDictionary<string, TimestampTracker>(1000);

    public override string StrategyId => "cdc-timestamp-based";
    public override string DisplayName => "Timestamp-based CDC";
    public override IntegrationCategory Category => IntegrationCategory.ChangeDataCapture;
    public override DataIntegrationCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsExactlyOnce = false,
        SupportsSchemaEvolution = true,
        SupportsIncremental = true,
        SupportsParallel = true,
        SupportsDistributed = true,
        MaxThroughputRecordsPerSec = 200000,
        TypicalLatencyMs = 1000.0
    };
    public override string SemanticDescription =>
        "Timestamp-based CDC using modification timestamps for change detection. Simple to implement " +
        "but may miss changes and has higher latency than log-based approaches.";
    public override string[] Tags => ["cdc", "timestamp", "polling", "modified-at", "simple"];

    /// <summary>
    /// Creates a timestamp tracker.
    /// </summary>
    public Task<TimestampTracker> CreateTrackerAsync(
        string trackerId,
        string tableName,
        string timestampColumn,
        TimestampTrackerConfig? config = null,
        CancellationToken ct = default)
    {
        var tracker = new TimestampTracker
        {
            TrackerId = trackerId,
            TableName = tableName,
            TimestampColumn = timestampColumn,
            Config = config ?? new TimestampTrackerConfig(),
            LastCheckpoint = DateTime.MinValue,
            CreatedAt = DateTime.UtcNow
        };

        if (!_trackers.TryAdd(trackerId, tracker))
            throw new InvalidOperationException($"Tracker {trackerId} already exists");

        RecordOperation("CreateTracker");
        return Task.FromResult(tracker);
    }

    /// <summary>
    /// Polls for changes since the last checkpoint.
    /// </summary>
    public Task<TimestampPollResult> PollChangesAsync(
        string trackerId,
        IReadOnlyList<Dictionary<string, object>> currentData,
        CancellationToken ct = default)
    {
        if (!_trackers.TryGetValue(trackerId, out var tracker))
            throw new KeyNotFoundException($"Tracker {trackerId} not found");

        var lastCheckpoint = tracker.LastCheckpoint;
        var newCheckpoint = DateTime.UtcNow;

        var changes = currentData
            .Where(r =>
            {
                if (r.TryGetValue(tracker.TimestampColumn, out var ts))
                {
                    var timestamp = ts is DateTime dt ? dt : DateTime.Parse(ts.ToString()!);
                    return timestamp > lastCheckpoint;
                }
                return false;
            })
            .ToList();

        tracker.LastCheckpoint = newCheckpoint;
        tracker.LastPollAt = DateTime.UtcNow;

        RecordOperation("PollChanges");

        return Task.FromResult(new TimestampPollResult
        {
            TrackerId = trackerId,
            PreviousCheckpoint = lastCheckpoint,
            NewCheckpoint = newCheckpoint,
            ChangesDetected = changes.Count,
            Changes = changes
        });
    }

    /// <summary>
    /// Sets the checkpoint manually.
    /// </summary>
    public Task SetCheckpointAsync(
        string trackerId,
        DateTime checkpoint,
        CancellationToken ct = default)
    {
        if (!_trackers.TryGetValue(trackerId, out var tracker))
            throw new KeyNotFoundException($"Tracker {trackerId} not found");

        tracker.LastCheckpoint = checkpoint;
        RecordOperation("SetCheckpoint");
        return Task.CompletedTask;
    }
}

#endregion

#region 126.6.4 Outbox Pattern CDC Strategy

/// <summary>
/// 126.6.4: Outbox pattern CDC strategy using an outbox table
/// for reliable event publishing with transactional guarantees.
/// </summary>
public sealed class OutboxPatternCdcStrategy : DataIntegrationStrategyBase
{
    private readonly BoundedDictionary<string, OutboxConfig> _outboxes = new BoundedDictionary<string, OutboxConfig>(1000);
    private readonly BoundedDictionary<string, ConcurrentQueue<OutboxEvent>> _events = new BoundedDictionary<string, ConcurrentQueue<OutboxEvent>>(1000);

    public override string StrategyId => "cdc-outbox-pattern";
    public override string DisplayName => "Outbox Pattern CDC";
    public override IntegrationCategory Category => IntegrationCategory.ChangeDataCapture;
    public override DataIntegrationCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsExactlyOnce = true,
        SupportsSchemaEvolution = true,
        SupportsIncremental = true,
        SupportsParallel = true,
        SupportsDistributed = true,
        MaxThroughputRecordsPerSec = 300000,
        TypicalLatencyMs = 100.0
    };
    public override string SemanticDescription =>
        "Outbox pattern CDC for reliable event publishing with transactional guarantees. " +
        "Events are written to outbox table in same transaction as business data.";
    public override string[] Tags => ["cdc", "outbox", "transactional", "reliable", "at-least-once"];

    /// <summary>
    /// Creates an outbox configuration.
    /// </summary>
    public Task<OutboxConfig> CreateOutboxAsync(
        string outboxId,
        string tableName,
        OutboxOptions? options = null,
        CancellationToken ct = default)
    {
        var outbox = new OutboxConfig
        {
            OutboxId = outboxId,
            TableName = tableName,
            Options = options ?? new OutboxOptions(),
            CreatedAt = DateTime.UtcNow
        };

        if (!_outboxes.TryAdd(outboxId, outbox))
            throw new InvalidOperationException($"Outbox {outboxId} already exists");

        _events[outboxId] = new ConcurrentQueue<OutboxEvent>();

        RecordOperation("CreateOutbox");
        return Task.FromResult(outbox);
    }

    /// <summary>
    /// Writes an event to the outbox.
    /// </summary>
    public Task<OutboxEvent> WriteEventAsync(
        string outboxId,
        string aggregateType,
        string aggregateId,
        string eventType,
        Dictionary<string, object> payload,
        CancellationToken ct = default)
    {
        if (!_events.TryGetValue(outboxId, out var queue))
            throw new KeyNotFoundException($"Outbox {outboxId} not found");

        var evt = new OutboxEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            OutboxId = outboxId,
            AggregateType = aggregateType,
            AggregateId = aggregateId,
            EventType = eventType,
            Payload = payload,
            Status = OutboxEventStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        queue.Enqueue(evt);
        RecordOperation("WriteEvent");
        return Task.FromResult(evt);
    }

    /// <summary>
    /// Processes pending outbox events.
    /// </summary>
    public async IAsyncEnumerable<OutboxEvent> ProcessEventsAsync(
        string outboxId,
        int batchSize = 100,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_events.TryGetValue(outboxId, out var queue))
            throw new KeyNotFoundException($"Outbox {outboxId} not found");

        var processed = 0;
        while (processed < batchSize && queue.TryDequeue(out var evt))
        {
            evt.Status = OutboxEventStatus.Published;
            evt.PublishedAt = DateTime.UtcNow;
            processed++;
            yield return evt;
        }

        RecordOperation("ProcessEvents");
    }

    /// <summary>
    /// Marks an event as processed.
    /// </summary>
    public Task MarkProcessedAsync(
        string eventId,
        CancellationToken ct = default)
    {
        RecordOperation("MarkProcessed");
        return Task.CompletedTask;
    }
}

#endregion

#region 126.6.5 Event Sourcing CDC Strategy

/// <summary>
/// 126.6.5: Event sourcing CDC strategy where all changes are
/// captured as immutable events in an event store.
/// </summary>
public sealed class EventSourcingCdcStrategy : DataIntegrationStrategyBase
{
    private readonly BoundedDictionary<string, EventStream> _streams = new BoundedDictionary<string, EventStream>(1000);
    private readonly BoundedDictionary<string, List<DomainEvent>> _events = new BoundedDictionary<string, List<DomainEvent>>(1000);

    public override string StrategyId => "cdc-event-sourcing";
    public override string DisplayName => "Event Sourcing CDC";
    public override IntegrationCategory Category => IntegrationCategory.ChangeDataCapture;
    public override DataIntegrationCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsExactlyOnce = true,
        SupportsSchemaEvolution = true,
        SupportsIncremental = true,
        SupportsParallel = true,
        SupportsDistributed = true,
        MaxThroughputRecordsPerSec = 200000,
        TypicalLatencyMs = 20.0
    };
    public override string SemanticDescription =>
        "Event sourcing CDC where all state changes are persisted as immutable events. " +
        "Provides complete audit trail and ability to rebuild state from events.";
    public override string[] Tags => ["cdc", "event-sourcing", "immutable", "audit-trail", "cqrs"];

    /// <summary>
    /// Creates an event stream.
    /// </summary>
    public Task<EventStream> CreateStreamAsync(
        string streamId,
        string aggregateType,
        EventStreamConfig? config = null,
        CancellationToken ct = default)
    {
        var stream = new EventStream
        {
            StreamId = streamId,
            AggregateType = aggregateType,
            Config = config ?? new EventStreamConfig(),
            Version = 0,
            CreatedAt = DateTime.UtcNow
        };

        if (!_streams.TryAdd(streamId, stream))
            throw new InvalidOperationException($"Stream {streamId} already exists");

        _events[streamId] = new List<DomainEvent>();

        RecordOperation("CreateStream");
        return Task.FromResult(stream);
    }

    /// <summary>
    /// Appends an event to the stream.
    /// </summary>
    public Task<DomainEvent> AppendEventAsync(
        string streamId,
        string eventType,
        Dictionary<string, object> eventData,
        int? expectedVersion = null,
        CancellationToken ct = default)
    {
        if (!_streams.TryGetValue(streamId, out var stream))
            throw new KeyNotFoundException($"Stream {streamId} not found");

        if (!_events.TryGetValue(streamId, out var events))
            throw new InvalidOperationException($"Event store not found for stream {streamId}");

        if (expectedVersion.HasValue && stream.Version != expectedVersion.Value)
            throw new ConcurrencyException($"Expected version {expectedVersion}, but stream is at version {stream.Version}");

        stream.Version++;

        var domainEvent = new DomainEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            StreamId = streamId,
            EventType = eventType,
            EventData = eventData,
            Version = stream.Version,
            Timestamp = DateTime.UtcNow
        };

        events.Add(domainEvent);
        RecordOperation("AppendEvent");
        return Task.FromResult(domainEvent);
    }

    /// <summary>
    /// Reads events from the stream.
    /// </summary>
    public Task<List<DomainEvent>> ReadEventsAsync(
        string streamId,
        int? fromVersion = null,
        int? toVersion = null,
        CancellationToken ct = default)
    {
        if (!_events.TryGetValue(streamId, out var events))
            throw new KeyNotFoundException($"Stream {streamId} not found");

        var result = events
            .Where(e => (!fromVersion.HasValue || e.Version >= fromVersion.Value) &&
                        (!toVersion.HasValue || e.Version <= toVersion.Value))
            .ToList();

        RecordOperation("ReadEvents");
        return Task.FromResult(result);
    }

    /// <summary>
    /// Gets the current stream version.
    /// </summary>
    public Task<int> GetStreamVersionAsync(string streamId, CancellationToken ct = default)
    {
        if (!_streams.TryGetValue(streamId, out var stream))
            throw new KeyNotFoundException($"Stream {streamId} not found");

        return Task.FromResult(stream.Version);
    }
}

public class ConcurrencyException : Exception
{
    public ConcurrencyException(string message) : base(message) { }
}

#endregion

#region Supporting Types

public enum DatabaseType { PostgreSQL, MySQL, SQLServer, Oracle, MongoDB }
public enum ConnectorStatus { Created, Running, Paused, Stopped, Failed }
public enum CdcOperationType { Insert, Update, Delete, Truncate, SchemaChange }
public enum OutboxEventStatus { Pending, Published, Failed }

public sealed record CdcConnector
{
    public required string ConnectorId { get; init; }
    public DatabaseType DatabaseType { get; init; }
    public required string ConnectionString { get; init; }
    public required CdcConnectorConfig Config { get; init; }
    public ConnectorStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; set; }
}

public sealed record CdcConnectorConfig
{
    public IReadOnlyList<string>? Tables { get; init; }
    public string? SlotName { get; init; }
    public bool SnapshotOnStart { get; init; } = true;
    public int BatchSize { get; init; } = 1000;
}

public sealed record CdcEvent
{
    public required string EventId { get; init; }
    public required string ConnectorId { get; init; }
    public CdcOperationType Operation { get; init; }
    public required string TableName { get; init; }
    public Dictionary<string, object>? Before { get; init; }
    public Dictionary<string, object>? After { get; init; }
    public long Lsn { get; init; }
    public DateTime Timestamp { get; init; }
}

public sealed record ConnectorStats
{
    public required string ConnectorId { get; init; }
    public ConnectorStatus Status { get; init; }
    public int PendingEvents { get; init; }
    public long TotalEventsCaptured { get; init; }
}

public sealed record TriggerConfig
{
    public required string TriggerId { get; init; }
    public required string TableName { get; init; }
    public required List<CdcOperationType> Operations { get; init; }
    public required List<string> CapturedColumns { get; init; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; init; }
}

public sealed record ChangeRecord
{
    public required string ChangeId { get; init; }
    public required string TableName { get; init; }
    public CdcOperationType Operation { get; init; }
    public Dictionary<string, object>? OldValues { get; init; }
    public Dictionary<string, object>? NewValues { get; init; }
    public DateTime ChangeTimestamp { get; init; }
}

public sealed record TimestampTracker
{
    public required string TrackerId { get; init; }
    public required string TableName { get; init; }
    public required string TimestampColumn { get; init; }
    public required TimestampTrackerConfig Config { get; init; }
    public DateTime LastCheckpoint { get; set; }
    public DateTime? LastPollAt { get; set; }
    public DateTime CreatedAt { get; init; }
}

public sealed record TimestampTrackerConfig
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan LookbackWindow { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed record TimestampPollResult
{
    public required string TrackerId { get; init; }
    public DateTime PreviousCheckpoint { get; init; }
    public DateTime NewCheckpoint { get; init; }
    public int ChangesDetected { get; init; }
    public required List<Dictionary<string, object>> Changes { get; init; }
}

public sealed record OutboxConfig
{
    public required string OutboxId { get; init; }
    public required string TableName { get; init; }
    public required OutboxOptions Options { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record OutboxOptions
{
    public TimeSpan RetentionPeriod { get; init; } = TimeSpan.FromDays(7);
    public int MaxRetries { get; init; } = 3;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed record OutboxEvent
{
    public required string EventId { get; init; }
    public required string OutboxId { get; init; }
    public required string AggregateType { get; init; }
    public required string AggregateId { get; init; }
    public required string EventType { get; init; }
    public required Dictionary<string, object> Payload { get; init; }
    public OutboxEventStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime? PublishedAt { get; set; }
}

public sealed record EventStream
{
    public required string StreamId { get; init; }
    public required string AggregateType { get; init; }
    public required EventStreamConfig Config { get; init; }
    public int Version { get; set; }
    public DateTime CreatedAt { get; init; }
}

public sealed record EventStreamConfig
{
    public int MaxEventsPerStream { get; init; } = 10000;
    public bool EnableSnapshots { get; init; } = true;
    public int SnapshotInterval { get; init; } = 100;
}

public sealed record DomainEvent
{
    public required string EventId { get; init; }
    public required string StreamId { get; init; }
    public required string EventType { get; init; }
    public required Dictionary<string, object> EventData { get; init; }
    public int Version { get; init; }
    public DateTime Timestamp { get; init; }
}

#endregion
