using System.Collections.Concurrent;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStreamingData.Strategies;

#region Redis Streams Consumer Group Infrastructure

/// <summary>
/// Redis Streams consumer group management implementing XREADGROUP, XPENDING, XACK,
/// and stream trimming (MAXLEN/MINID) semantics for production-grade stream processing.
/// </summary>
public sealed class RedisStreamsConsumerGroupManager
{
    private readonly BoundedDictionary<string, RedisStreamState> _streams = new BoundedDictionary<string, RedisStreamState>(1000);
    private readonly BoundedDictionary<string, BoundedDictionary<string, RedisConsumerGroupState>> _groups = new BoundedDictionary<string, BoundedDictionary<string, RedisConsumerGroupState>>(1000);
    private readonly BoundedDictionary<string, BoundedDictionary<string, PendingEntry>> _pendingEntries = new BoundedDictionary<string, BoundedDictionary<string, PendingEntry>>(1000);

    /// <summary>Creates a new consumer group for a stream at the given start ID.</summary>
    public void CreateGroup(string streamKey, string groupName, string startId = "$")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);

        var groups = _groups.GetOrAdd(streamKey, _ => new BoundedDictionary<string, RedisConsumerGroupState>(1000));
        if (!groups.TryAdd(groupName, new RedisConsumerGroupState
        {
            GroupName = groupName,
            LastDeliveredId = startId == "$" ? GetLastId(streamKey) : startId,
            CreatedAt = DateTime.UtcNow
        }))
        {
            throw new InvalidOperationException($"Consumer group '{groupName}' already exists for stream '{streamKey}'.");
        }
    }

    /// <summary>
    /// Reads new messages for a consumer in a group (XREADGROUP semantics).
    /// Messages are added to the consumer's pending entry list until acknowledged.
    /// </summary>
    public IReadOnlyList<RedisStreamEntry> ReadGroup(string streamKey, string groupName, string consumerName, int count = 10, string id = ">")
    {
        if (!_groups.TryGetValue(streamKey, out var groups) || !groups.TryGetValue(groupName, out var group))
            throw new InvalidOperationException($"Consumer group '{groupName}' not found for stream '{streamKey}'.");

        var stream = _streams.GetOrAdd(streamKey, _ => new RedisStreamState { StreamKey = streamKey });
        var results = new List<RedisStreamEntry>();
        var pelKey = $"{streamKey}:{groupName}";
        var pel = _pendingEntries.GetOrAdd(pelKey, _ => new BoundedDictionary<string, PendingEntry>(1000));

        if (id == ">")
        {
            // Deliver only new (undelivered) messages.
            // Finding 4344: take snapshot under lock to avoid concurrent mutation by Trim/AddEntry.
            List<RedisStreamEntry> snapshot;
            lock (stream.Lock)
            {
                snapshot = stream.Entries
                    .Where(e => string.Compare(e.Id, group.LastDeliveredId, StringComparison.Ordinal) > 0)
                    .Take(count).ToList();
            }

            foreach (var entry in snapshot)
            {
                pel[$"{entry.Id}:{consumerName}"] = new PendingEntry
                {
                    Id = entry.Id,
                    ConsumerName = consumerName,
                    DeliveredAt = DateTime.UtcNow,
                    DeliveryCount = 1
                };
                group.LastDeliveredId = entry.Id;
                group.TotalDelivered++;
                results.Add(entry);
            }
        }
        else
        {
            // Re-deliver pending messages for this consumer (id = "0" typically)
            var pending = pel.Values
                .Where(p => p.ConsumerName == consumerName)
                .OrderBy(p => p.Id)
                .Take(count)
                .ToList();

            foreach (var p in pending)
            {
                RedisStreamEntry? entry;
                lock (stream.Lock)
                {
                    entry = stream.Entries.FirstOrDefault(e => e.Id == p.Id);
                }

                if (entry != null)
                {
                    p.DeliveryCount++;
                    results.Add(entry);
                }
            }
        }

        return results;
    }

    /// <summary>Acknowledges processed messages (XACK), removing them from the pending entry list.</summary>
    public int Acknowledge(string streamKey, string groupName, params string[] messageIds)
    {
        var pelKey = $"{streamKey}:{groupName}";
        if (!_pendingEntries.TryGetValue(pelKey, out var pel)) return 0;

        int acked = 0;
        foreach (var id in messageIds)
        {
            var keysToRemove = pel.Keys.Where(k => k.StartsWith($"{id}:")).ToList();
            foreach (var key in keysToRemove)
            {
                if (pel.TryRemove(key, out _)) acked++;
            }
        }

        if (_groups.TryGetValue(streamKey, out var groups) && groups.TryGetValue(groupName, out var group))
            group.TotalAcknowledged += acked;

        return acked;
    }

    /// <summary>Gets pending entries for a consumer group (XPENDING).</summary>
    public PendingInfo GetPending(string streamKey, string groupName, int count = 100, string? consumerFilter = null)
    {
        var pelKey = $"{streamKey}:{groupName}";
        if (!_pendingEntries.TryGetValue(pelKey, out var pel))
            return new PendingInfo { GroupName = groupName, TotalPending = 0, Entries = [] };

        var entries = pel.Values.AsEnumerable();
        if (consumerFilter != null)
            entries = entries.Where(e => e.ConsumerName == consumerFilter);

        var list = entries.OrderBy(e => e.Id).Take(count).ToList();
        return new PendingInfo
        {
            GroupName = groupName,
            TotalPending = pel.Count,
            MinId = list.FirstOrDefault()?.Id,
            MaxId = list.LastOrDefault()?.Id,
            Entries = list
        };
    }

    /// <summary>Trims the stream to a maximum length (MAXLEN) or minimum ID (MINID).</summary>
    public long Trim(string streamKey, long? maxLen = null, string? minId = null)
    {
        if (!_streams.TryGetValue(streamKey, out var stream)) return 0;

        // Finding 4344: all Entries mutations must be under stream.Lock.
        long trimmed = 0;
        lock (stream.Lock)
        {
            if (maxLen.HasValue)
            {
                while (stream.Entries.Count > maxLen.Value)
                {
                    stream.Entries.RemoveAt(0);
                    trimmed++;
                }
            }
            else if (minId != null)
            {
                trimmed = stream.Entries.RemoveAll(e =>
                    string.Compare(e.Id, minId, StringComparison.Ordinal) < 0);
            }
        }

        return trimmed;
    }

    /// <summary>Adds an entry to a stream.</summary>
    public string AddEntry(string streamKey, Dictionary<string, string> fields)
    {
        var stream = _streams.GetOrAdd(streamKey, _ => new RedisStreamState { StreamKey = streamKey });
        // Finding 4344: lock to prevent concurrent mutation with ReadGroup/Trim.
        lock (stream.Lock)
        {
            var id = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{stream.SequenceCounter++}";
            stream.Entries.Add(new RedisStreamEntry { Id = id, Fields = fields, Timestamp = DateTime.UtcNow });
            return id;
        }
    }

    private string GetLastId(string streamKey)
    {
        if (!_streams.TryGetValue(streamKey, out var s)) return "0-0";
        lock (s.Lock)
        {
            return s.Entries.Count > 0 ? s.Entries[^1].Id : "0-0";
        }
    }
}

/// <summary>Internal state for a Redis stream.</summary>
public sealed class RedisStreamState
{
    public required string StreamKey { get; init; }
    // Finding 4344: Entries is mutated by ReadGroup, Trim, and AddEntry concurrently.
    // All callers must acquire _lock before reading or writing Entries / SequenceCounter.
    public readonly object Lock = new();
    public List<RedisStreamEntry> Entries { get; } = [];
    public long SequenceCounter { get; set; }
}

/// <summary>A Redis stream entry (ID + field/value pairs).</summary>
public sealed record RedisStreamEntry
{
    public required string Id { get; init; }
    public required Dictionary<string, string> Fields { get; init; }
    public DateTime Timestamp { get; init; }
}

/// <summary>State for a consumer group.</summary>
public sealed class RedisConsumerGroupState
{
    public required string GroupName { get; init; }
    public string LastDeliveredId { get; set; } = "0-0";
    public long TotalDelivered { get; set; }
    public long TotalAcknowledged { get; set; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>A pending entry in a consumer group's PEL.</summary>
public sealed class PendingEntry
{
    public required string Id { get; init; }
    public required string ConsumerName { get; init; }
    public DateTime DeliveredAt { get; init; }
    public int DeliveryCount { get; set; }
}

/// <summary>Summary of pending entries for a consumer group.</summary>
public sealed record PendingInfo
{
    public required string GroupName { get; init; }
    public long TotalPending { get; init; }
    public string? MinId { get; init; }
    public string? MaxId { get; init; }
    public List<PendingEntry> Entries { get; init; } = [];
}

#endregion

#region Kinesis Enhanced Features

/// <summary>
/// KCL-style checkpoint manager for Kinesis using DynamoDB-compatible lease table semantics.
/// Tracks per-shard processing position with lease-based ownership.
/// </summary>
public sealed class KinesisCheckpointManager
{
    private readonly BoundedDictionary<string, KinesisLeaseEntry> _leaseTable = new BoundedDictionary<string, KinesisLeaseEntry>(1000);
    // Finding 4345: single lock for TakeLease read-modify-write atomicity.
    private readonly object _leaseLock = new();

    /// <summary>Takes a lease on a shard for checkpointing.</summary>
    public bool TakeLease(string streamName, string shardId, string workerId)
    {
        var key = $"{streamName}:{shardId}";
        // Finding 4345: GetOrAdd + check + mutate must be atomic to prevent two workers
        // both seeing an expired lease and both taking it simultaneously.
        lock (_leaseLock)
        {
            var lease = _leaseTable.GetOrAdd(key, _ => new KinesisLeaseEntry
            {
                StreamName = streamName,
                ShardId = shardId,
                WorkerId = workerId,
                LeaseCounter = 1,
                TakenAt = DateTime.UtcNow
            });

            // Check if lease is expired or owned by this worker
            if (lease.WorkerId == workerId) return true;
            if ((DateTime.UtcNow - lease.LastRenewed).TotalSeconds > 30)
            {
                lease.WorkerId = workerId;
                lease.LeaseCounter++;
                lease.TakenAt = DateTime.UtcNow;
                lease.LastRenewed = DateTime.UtcNow;
                return true;
            }
            return false;
        }
    }

    /// <summary>Checkpoints the processing position for a shard.</summary>
    public void Checkpoint(string streamName, string shardId, string sequenceNumber, long subSequenceNumber = 0)
    {
        var key = $"{streamName}:{shardId}";
        if (_leaseTable.TryGetValue(key, out var lease))
        {
            lease.Checkpoint = sequenceNumber;
            lease.SubSequenceNumber = subSequenceNumber;
            lease.CheckpointedAt = DateTime.UtcNow;
        }
    }

    /// <summary>Renews a lease to prevent expiry.</summary>
    public bool RenewLease(string streamName, string shardId, string workerId)
    {
        var key = $"{streamName}:{shardId}";
        if (_leaseTable.TryGetValue(key, out var lease) && lease.WorkerId == workerId)
        {
            lease.LastRenewed = DateTime.UtcNow;
            lease.LeaseCounter++;
            return true;
        }
        return false;
    }

    /// <summary>Gets the checkpoint for a shard.</summary>
    public string? GetCheckpoint(string streamName, string shardId)
    {
        var key = $"{streamName}:{shardId}";
        return _leaseTable.TryGetValue(key, out var lease) ? lease.Checkpoint : null;
    }

    /// <summary>Gets all leases for monitoring.</summary>
    public IReadOnlyList<KinesisLeaseEntry> GetAllLeases() => _leaseTable.Values.ToList();
}

/// <summary>A lease table entry for KCL-style checkpointing.</summary>
public sealed class KinesisLeaseEntry
{
    public required string StreamName { get; init; }
    public required string ShardId { get; init; }
    public string WorkerId { get; set; } = "";
    public string? Checkpoint { get; set; }
    public long SubSequenceNumber { get; set; }
    public long LeaseCounter { get; set; }
    public DateTime TakenAt { get; set; }
    public DateTime LastRenewed { get; set; } = DateTime.UtcNow;
    public DateTime? CheckpointedAt { get; set; }
}

/// <summary>
/// Kinesis shard iterator management supporting all iterator types:
/// AT_SEQUENCE_NUMBER, AFTER_SEQUENCE_NUMBER, TRIM_HORIZON, LATEST, AT_TIMESTAMP.
/// </summary>
public sealed class KinesisShardIteratorManager
{
    private readonly BoundedDictionary<string, ShardIteratorState> _iterators = new BoundedDictionary<string, ShardIteratorState>(1000);
    private long _iteratorIdCounter;

    /// <summary>Gets a shard iterator for the specified position.</summary>
    public string GetShardIterator(string streamName, string shardId, KinesisShardIteratorType type,
        string? startingSequenceNumber = null, DateTime? timestamp = null)
    {
        var iteratorId = $"shardIterator-{Interlocked.Increment(ref _iteratorIdCounter)}";
        _iterators[iteratorId] = new ShardIteratorState
        {
            IteratorId = iteratorId,
            StreamName = streamName,
            ShardId = shardId,
            Type = type,
            StartingSequenceNumber = startingSequenceNumber,
            Timestamp = timestamp,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5) // Kinesis iterators expire after 5 min
        };
        return iteratorId;
    }

    /// <summary>Advances the iterator and returns the next position.</summary>
    public ShardIteratorState? GetIterator(string iteratorId)
    {
        if (_iterators.TryGetValue(iteratorId, out var state))
        {
            if (DateTime.UtcNow > state.ExpiresAt)
            {
                _iterators.TryRemove(iteratorId, out _);
                return null; // Expired
            }
            return state;
        }
        return null;
    }
}

/// <summary>Kinesis shard iterator types.</summary>
public enum KinesisShardIteratorType
{
    TrimHorizon, Latest, AtSequenceNumber, AfterSequenceNumber, AtTimestamp
}

/// <summary>State for a shard iterator.</summary>
public sealed record ShardIteratorState
{
    public required string IteratorId { get; init; }
    public required string StreamName { get; init; }
    public required string ShardId { get; init; }
    public KinesisShardIteratorType Type { get; init; }
    public string? StartingSequenceNumber { get; init; }
    public DateTime? Timestamp { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
}

/// <summary>
/// Kinesis resharding detector monitoring for shard splits and merges.
/// </summary>
public sealed class KinesisReshardingDetector
{
    private readonly BoundedDictionary<string, List<string>> _shardHistory = new BoundedDictionary<string, List<string>>(1000);

    /// <summary>Records a shard split event.</summary>
    public ReshardingEvent RecordSplit(string streamName, string parentShardId, string childShardId1, string childShardId2)
    {
        var history = _shardHistory.GetOrAdd(streamName, _ => new List<string>());
        lock (history)
        {
            history.Add($"SPLIT:{parentShardId}->{childShardId1},{childShardId2}");
        }
        return new ReshardingEvent
        {
            StreamName = streamName,
            EventType = ReshardingEventType.Split,
            ParentShardId = parentShardId,
            ChildShardIds = [childShardId1, childShardId2],
            DetectedAt = DateTime.UtcNow
        };
    }

    /// <summary>Records a shard merge event.</summary>
    public ReshardingEvent RecordMerge(string streamName, string shardId1, string shardId2, string mergedShardId)
    {
        var history = _shardHistory.GetOrAdd(streamName, _ => new List<string>());
        lock (history)
        {
            history.Add($"MERGE:{shardId1}+{shardId2}->{mergedShardId}");
        }
        return new ReshardingEvent
        {
            StreamName = streamName,
            EventType = ReshardingEventType.Merge,
            ParentShardId = mergedShardId,
            ChildShardIds = [shardId1, shardId2],
            DetectedAt = DateTime.UtcNow
        };
    }

    /// <summary>Gets resharding history for a stream.</summary>
    public IReadOnlyList<string> GetHistory(string streamName) =>
        _shardHistory.TryGetValue(streamName, out var h) ? h.ToList() : [];
}

/// <summary>Resharding event types.</summary>
public enum ReshardingEventType { Split, Merge }

/// <summary>A resharding event record.</summary>
public sealed record ReshardingEvent
{
    public required string StreamName { get; init; }
    public ReshardingEventType EventType { get; init; }
    public required string ParentShardId { get; init; }
    public List<string> ChildShardIds { get; init; } = [];
    public DateTime DetectedAt { get; init; }
}

#endregion

#region Event Hubs Enhanced Features

/// <summary>
/// Event Hubs partition ownership balancing implementing the EventProcessorClient pattern.
/// Distributes partitions across processors with greedy/balanced algorithms.
/// </summary>
public sealed class EventHubsPartitionBalancer
{
    private readonly BoundedDictionary<string, EventHubsOwnership> _ownership = new BoundedDictionary<string, EventHubsOwnership>(1000);
    private readonly TimeSpan _ownershipExpiry = TimeSpan.FromSeconds(30);

    /// <summary>Claims ownership of a partition with optimistic concurrency.</summary>
    public bool ClaimOwnership(string eventHubName, string consumerGroup, string partitionId,
        string ownerId, string? existingETag = null)
    {
        var key = $"{eventHubName}:{consumerGroup}:{partitionId}";

        if (_ownership.TryGetValue(key, out var existing))
        {
            // Optimistic concurrency check
            if (existingETag != null && existing.ETag != existingETag) return false;

            // Check expiry
            if ((DateTime.UtcNow - existing.LastModified) < _ownershipExpiry && existing.OwnerId != ownerId)
                return false;
        }

        _ownership[key] = new EventHubsOwnership
        {
            EventHubName = eventHubName,
            ConsumerGroup = consumerGroup,
            PartitionId = partitionId,
            OwnerId = ownerId,
            ETag = Guid.NewGuid().ToString("N"),
            LastModified = DateTime.UtcNow
        };
        return true;
    }

    /// <summary>
    /// Balances partitions across processors using greedy algorithm.
    /// Ensures each processor owns approximately equal partitions.
    /// </summary>
    public Dictionary<string, List<string>> Balance(string eventHubName, string consumerGroup,
        IReadOnlyList<string> partitionIds, IReadOnlyList<string> processorIds)
    {
        var assignments = new Dictionary<string, List<string>>();
        foreach (var pid in processorIds)
            assignments[pid] = new List<string>();

        if (processorIds.Count == 0) return assignments;

        // Greedy round-robin assignment
        for (int i = 0; i < partitionIds.Count; i++)
        {
            var processor = processorIds[i % processorIds.Count];
            assignments[processor].Add(partitionIds[i]);
            ClaimOwnership(eventHubName, consumerGroup, partitionIds[i], processor);
        }

        return assignments;
    }

    /// <summary>Gets all ownership entries for a consumer group.</summary>
    public IReadOnlyList<EventHubsOwnership> GetOwnership(string eventHubName, string consumerGroup)
    {
        var prefix = $"{eventHubName}:{consumerGroup}:";
        return _ownership.Where(kv => kv.Key.StartsWith(prefix))
            .Select(kv => kv.Value).ToList();
    }
}

/// <summary>Partition ownership record for Event Hubs.</summary>
public sealed record EventHubsOwnership
{
    public required string EventHubName { get; init; }
    public required string ConsumerGroup { get; init; }
    public required string PartitionId { get; init; }
    public required string OwnerId { get; init; }
    public string ETag { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime LastModified { get; init; }
}

/// <summary>
/// Event Hubs checkpoint store implementation with Azure Blob Storage-compatible semantics.
/// Stores consumer offsets with ETag-based optimistic concurrency.
/// </summary>
public sealed class EventHubsCheckpointStore
{
    private readonly BoundedDictionary<string, EventHubsCheckpointEntry> _checkpoints = new BoundedDictionary<string, EventHubsCheckpointEntry>(1000);

    /// <summary>Updates a checkpoint for a partition.</summary>
    public bool UpdateCheckpoint(string eventHubName, string consumerGroup, string partitionId,
        long sequenceNumber, string offset, string? expectedETag = null)
    {
        var key = $"{eventHubName}:{consumerGroup}:{partitionId}";

        if (expectedETag != null && _checkpoints.TryGetValue(key, out var existing) && existing.ETag != expectedETag)
            return false; // Concurrency conflict

        _checkpoints[key] = new EventHubsCheckpointEntry
        {
            EventHubName = eventHubName,
            ConsumerGroup = consumerGroup,
            PartitionId = partitionId,
            SequenceNumber = sequenceNumber,
            Offset = offset,
            ETag = Guid.NewGuid().ToString("N"),
            UpdatedAt = DateTime.UtcNow
        };
        return true;
    }

    /// <summary>Gets the checkpoint for a partition.</summary>
    public EventHubsCheckpointEntry? GetCheckpoint(string eventHubName, string consumerGroup, string partitionId)
    {
        var key = $"{eventHubName}:{consumerGroup}:{partitionId}";
        return _checkpoints.TryGetValue(key, out var cp) ? cp : null;
    }

    /// <summary>Lists all checkpoints for a consumer group.</summary>
    public IReadOnlyList<EventHubsCheckpointEntry> ListCheckpoints(string eventHubName, string consumerGroup)
    {
        var prefix = $"{eventHubName}:{consumerGroup}:";
        return _checkpoints.Where(kv => kv.Key.StartsWith(prefix))
            .Select(kv => kv.Value).ToList();
    }
}

/// <summary>An Event Hubs checkpoint entry.</summary>
public sealed record EventHubsCheckpointEntry
{
    public required string EventHubName { get; init; }
    public required string ConsumerGroup { get; init; }
    public required string PartitionId { get; init; }
    public long SequenceNumber { get; init; }
    public required string Offset { get; init; }
    public string ETag { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime UpdatedAt { get; init; }
}

#endregion

#region MQTT QoS 2 Protocol

/// <summary>
/// MQTT QoS 2 exactly-once delivery protocol implementing the full 4-way handshake:
/// PUBLISH -> PUBREC -> PUBREL -> PUBCOMP.
/// Tracks message state per packet ID to prevent duplicates.
/// </summary>
public sealed class MqttQoS2ProtocolHandler
{
    private readonly BoundedDictionary<int, MqttQoS2State> _pendingMessages = new BoundedDictionary<int, MqttQoS2State>(1000);
    // Finding 4362: monotonic counter wrapping at 65535 instead of Random to avoid packet ID collisions
    // under high QoS 2 load. MQTT spec requires packet IDs 1-65535.
    private int _packetIdCounter;

    /// <summary>Initiates QoS 2 publish (sender side). Returns packet ID.</summary>
    public int InitiatePublish(byte[] payload, string topic)
    {
        // Wrap in 1-65535 range (MQTT spec); Interlocked for thread safety.
        var raw = Interlocked.Increment(ref _packetIdCounter);
        var packetId = ((raw - 1) % 65535) + 1; // 1..65535
        _pendingMessages[packetId] = new MqttQoS2State
        {
            PacketId = packetId,
            Payload = payload,
            Topic = topic,
            Phase = MqttQoS2Phase.Published,
            CreatedAt = DateTime.UtcNow
        };
        return packetId;
    }

    /// <summary>Handles PUBREC receipt (sender side). Transitions to PUBREL phase.</summary>
    public bool HandlePubRec(int packetId)
    {
        if (_pendingMessages.TryGetValue(packetId, out var state) && state.Phase == MqttQoS2Phase.Published)
        {
            state.Phase = MqttQoS2Phase.PubRecReceived;
            return true; // Send PUBREL
        }
        return false;
    }

    /// <summary>Handles PUBREL receipt (receiver side). Transitions to PUBCOMP phase.</summary>
    public bool HandlePubRel(int packetId)
    {
        if (_pendingMessages.TryGetValue(packetId, out var state) && state.Phase == MqttQoS2Phase.PubRecReceived)
        {
            state.Phase = MqttQoS2Phase.PubRelReceived;
            return true; // Send PUBCOMP
        }
        return false;
    }

    /// <summary>Handles PUBCOMP receipt (sender side). Completes the handshake.</summary>
    public bool HandlePubComp(int packetId)
    {
        if (_pendingMessages.TryRemove(packetId, out var state))
        {
            return state.Phase == MqttQoS2Phase.PubRelReceived || state.Phase == MqttQoS2Phase.PubRecReceived;
        }
        return false;
    }

    /// <summary>Gets all pending QoS 2 messages for retry/cleanup.</summary>
    public IReadOnlyList<MqttQoS2State> GetPendingMessages() => _pendingMessages.Values.ToList();

    /// <summary>Cleans up expired pending messages.</summary>
    public int CleanupExpired(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var expired = _pendingMessages.Where(kv => kv.Value.CreatedAt < cutoff).ToList();
        foreach (var kv in expired) _pendingMessages.TryRemove(kv.Key, out _);
        return expired.Count;
    }
}

/// <summary>QoS 2 handshake phases.</summary>
public enum MqttQoS2Phase { Published, PubRecReceived, PubRelReceived }

/// <summary>State for a QoS 2 message in the handshake.</summary>
public sealed class MqttQoS2State
{
    public int PacketId { get; init; }
    public required byte[] Payload { get; init; }
    public required string Topic { get; init; }
    public MqttQoS2Phase Phase { get; set; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// MQTT retained message last-value cache for immediate delivery to new subscribers.
/// </summary>
public sealed class MqttRetainedMessageCache
{
    private readonly BoundedDictionary<string, MqttRetainedMessage> _cache = new BoundedDictionary<string, MqttRetainedMessage>(1000);

    /// <summary>Sets or updates a retained message for a topic.</summary>
    public void Set(string topic, byte[] payload, byte qos = 1)
    {
        if (payload.Length == 0)
        {
            // Empty payload removes retained message
            _cache.TryRemove(topic, out _);
            return;
        }

        _cache[topic] = new MqttRetainedMessage
        {
            Topic = topic,
            Payload = payload,
            QoS = qos,
            UpdatedAt = DateTime.UtcNow
        };
    }

    /// <summary>Gets retained messages matching a topic filter (supports +/# wildcards).</summary>
    public IReadOnlyList<MqttRetainedMessage> GetMatching(string topicFilter)
    {
        return _cache.Where(kv => MatchTopic(topicFilter, kv.Key))
            .Select(kv => kv.Value).ToList();
    }

    /// <summary>Removes a retained message.</summary>
    public bool Remove(string topic) => _cache.TryRemove(topic, out _);

    /// <summary>Gets total retained message count.</summary>
    public int Count => _cache.Count;

    private static bool MatchTopic(string filter, string topic)
    {
        if (filter == topic || filter == "#") return true;
        var fp = filter.Split('/');
        var tp = topic.Split('/');
        return MatchParts(fp, 0, tp, 0);
    }

    private static bool MatchParts(string[] f, int fi, string[] t, int ti)
    {
        if (fi == f.Length && ti == t.Length) return true;
        if (fi == f.Length) return false;
        if (f[fi] == "#") return fi == f.Length - 1;
        if (ti == t.Length) return false;
        return (f[fi] == "+" || f[fi] == t[ti]) && MatchParts(f, fi + 1, t, ti + 1);
    }
}

/// <summary>A retained MQTT message.</summary>
public sealed record MqttRetainedMessage
{
    public required string Topic { get; init; }
    public required byte[] Payload { get; init; }
    public byte QoS { get; init; }
    public DateTime UpdatedAt { get; init; }
}

/// <summary>
/// MQTT session persistence manager for clean session=false clients.
/// Stores subscriptions and undelivered QoS 1/2 messages across reconnections.
/// </summary>
public sealed class MqttSessionPersistenceManager
{
    private readonly BoundedDictionary<string, MqttPersistentSession> _sessions = new BoundedDictionary<string, MqttPersistentSession>(1000);

    /// <summary>Gets or creates a persistent session for a client.</summary>
    public MqttPersistentSession GetOrCreateSession(string clientId, bool cleanSession)
    {
        if (cleanSession)
        {
            _sessions.TryRemove(clientId, out _);
            return new MqttPersistentSession
            {
                ClientId = clientId,
                CleanSession = true,
                CreatedAt = DateTime.UtcNow
            };
        }

        return _sessions.GetOrAdd(clientId, _ => new MqttPersistentSession
        {
            ClientId = clientId,
            CleanSession = false,
            CreatedAt = DateTime.UtcNow
        });
    }

    /// <summary>Adds a subscription for a persistent session.</summary>
    public void AddSubscription(string clientId, string topicFilter, byte qos)
    {
        if (_sessions.TryGetValue(clientId, out var session))
            session.Subscriptions[topicFilter] = qos;
    }

    /// <summary>Queues an undelivered message for an offline client.</summary>
    public void QueueMessage(string clientId, byte[] payload, string topic, byte qos)
    {
        if (_sessions.TryGetValue(clientId, out var session))
        {
            session.PendingMessages.Enqueue(new MqttPendingMessage
            {
                Topic = topic,
                Payload = payload,
                QoS = qos,
                QueuedAt = DateTime.UtcNow
            });
        }
    }

    /// <summary>Drains pending messages for a reconnecting client.</summary>
    public IReadOnlyList<MqttPendingMessage> DrainPendingMessages(string clientId, int maxMessages = 1000)
    {
        if (!_sessions.TryGetValue(clientId, out var session)) return [];
        var messages = new List<MqttPendingMessage>();
        while (messages.Count < maxMessages && session.PendingMessages.TryDequeue(out var msg))
            messages.Add(msg);
        return messages;
    }

    /// <summary>Removes a session (disconnect with clean session).</summary>
    public bool RemoveSession(string clientId) => _sessions.TryRemove(clientId, out _);
}

/// <summary>A persistent MQTT session.</summary>
public sealed class MqttPersistentSession
{
    public required string ClientId { get; init; }
    public bool CleanSession { get; init; }
    public BoundedDictionary<string, byte> Subscriptions { get; } = new BoundedDictionary<string, byte>(1000);
    public ConcurrentQueue<MqttPendingMessage> PendingMessages { get; } = new();
    public DateTime CreatedAt { get; init; }
}

/// <summary>A pending message for an offline client.</summary>
public sealed record MqttPendingMessage
{
    public required string Topic { get; init; }
    public required byte[] Payload { get; init; }
    public byte QoS { get; init; }
    public DateTime QueuedAt { get; init; }
}

#endregion
