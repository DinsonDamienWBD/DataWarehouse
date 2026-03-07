using System.Collections.Concurrent;

namespace DataWarehouse.Hardening.Tests.Chaos.Federation;

/// <summary>
/// Simulates network partitions between replicas in a federation cluster.
/// Tracks messages blocked during partition and optionally queues them for
/// delivery when the partition heals.
///
/// Used by federation chaos tests to prove CRDT convergence after partition heal
/// and no data loss during partitioned operation.
/// </summary>
public sealed class PartitionSimulator
{
    private readonly Lock _lock = new();
    private readonly HashSet<(string, string)> _partitions = new();

    /// <summary>
    /// Messages blocked during partition. Key: (from, to), Value: list of payloads.
    /// </summary>
    private readonly ConcurrentDictionary<(string From, string To), List<byte[]>> _queuedMessages = new();

    /// <summary>Total messages blocked by partitions.</summary>
    public long MessagesBlocked => Interlocked.Read(ref _messagesBlocked);
    private long _messagesBlocked;

    /// <summary>Total messages delivered after heal.</summary>
    public long MessagesDeliveredOnHeal => Interlocked.Read(ref _messagesDeliveredOnHeal);
    private long _messagesDeliveredOnHeal;

    /// <summary>
    /// When true, messages sent during partition are queued and delivered on heal.
    /// When false, messages are permanently lost (dropped).
    /// Default: false (drop).
    /// </summary>
    public bool QueueDuringPartition { get; set; }

    /// <summary>
    /// Blocks all communication between two replicas (both directions).
    /// </summary>
    public void Partition(string replicaA, string replicaB)
    {
        lock (_lock)
        {
            _partitions.Add((replicaA, replicaB));
            _partitions.Add((replicaB, replicaA));
        }
    }

    /// <summary>
    /// Splits the cluster into two isolated groups. All cross-group communication is blocked.
    /// </summary>
    public void PartitionGroup(IReadOnlyList<string> group1, IReadOnlyList<string> group2)
    {
        lock (_lock)
        {
            foreach (var a in group1)
            {
                foreach (var b in group2)
                {
                    _partitions.Add((a, b));
                    _partitions.Add((b, a));
                }
            }
        }
    }

    /// <summary>
    /// Restores communication between two replicas (both directions).
    /// Returns any queued messages for delivery if <see cref="QueueDuringPartition"/> was true.
    /// </summary>
    public List<(string From, string To, byte[] Payload)> Heal(string replicaA, string replicaB)
    {
        var delivered = new List<(string From, string To, byte[] Payload)>();

        lock (_lock)
        {
            _partitions.Remove((replicaA, replicaB));
            _partitions.Remove((replicaB, replicaA));
        }

        // Deliver queued messages
        DeliverQueued(replicaA, replicaB, delivered);
        DeliverQueued(replicaB, replicaA, delivered);

        Interlocked.Add(ref _messagesDeliveredOnHeal, delivered.Count);
        return delivered;
    }

    /// <summary>
    /// Restores full connectivity between all replicas and delivers all queued messages.
    /// </summary>
    public List<(string From, string To, byte[] Payload)> HealAll()
    {
        var delivered = new List<(string From, string To, byte[] Payload)>();

        lock (_lock)
        {
            _partitions.Clear();
        }

        foreach (var key in _queuedMessages.Keys.ToList())
        {
            if (_queuedMessages.TryRemove(key, out var messages))
            {
                foreach (var msg in messages)
                {
                    delivered.Add((key.From, key.To, msg));
                }
            }
        }

        Interlocked.Add(ref _messagesDeliveredOnHeal, delivered.Count);
        return delivered;
    }

    /// <summary>
    /// Checks whether a message can be sent from one replica to another.
    /// Returns true if the message can pass, false if partitioned.
    /// If partitioned and <see cref="QueueDuringPartition"/> is true, the message is queued.
    /// </summary>
    public bool TrySend(string from, string to, byte[] payload)
    {
        bool isPartitioned;
        lock (_lock)
        {
            isPartitioned = _partitions.Contains((from, to));
        }

        if (!isPartitioned)
            return true;

        Interlocked.Increment(ref _messagesBlocked);

        if (QueueDuringPartition)
        {
            var queue = _queuedMessages.GetOrAdd((from, to), _ => new List<byte[]>());
            lock (queue)
            {
                queue.Add(payload);
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a partition exists between two replicas (directional).
    /// </summary>
    public bool IsPartitioned(string from, string to)
    {
        lock (_lock)
        {
            return _partitions.Contains((from, to));
        }
    }

    /// <summary>
    /// Returns the count of active partition links (directional).
    /// </summary>
    public int ActivePartitionCount
    {
        get
        {
            lock (_lock)
            {
                return _partitions.Count;
            }
        }
    }

    private void DeliverQueued(string from, string to, List<(string From, string To, byte[] Payload)> delivered)
    {
        if (_queuedMessages.TryRemove((from, to), out var messages))
        {
            lock (messages)
            {
                foreach (var msg in messages)
                {
                    delivered.Add((from, to, msg));
                }
            }
        }
    }
}
