using System.Collections.Concurrent;

namespace DataWarehouse.SDK.Infrastructure;

// ============================================================================
// PHASE 5 - E4: Distributed Services Tier 2
// Uses: QuorumReplicator, CrdtMetadataSync
// ============================================================================

#region Distributed Lock Service

/// <summary>
/// Distributed lock service with fencing tokens.
/// </summary>
public sealed class DistributedLockService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DistributedLockEntry> _locks = new();
    private readonly Timer _expiryTimer;
    private long _fencingTokenCounter;
    private volatile bool _disposed;

    public event EventHandler<LockEventArgs>? LockAcquired;
    public event EventHandler<LockEventArgs>? LockReleased;
    public event EventHandler<LockEventArgs>? LockExpired;

    public DistributedLockService()
    {
        _expiryTimer = new Timer(ExpireLocks, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Acquires a distributed lock with fencing token.
    /// </summary>
    public async Task<LockAcquisitionResult> AcquireLockAsync(string lockName, string holderId, TimeSpan ttl, CancellationToken ct = default)
    {
        var entry = _locks.GetOrAdd(lockName, _ => new DistributedLockEntry { LockName = lockName });

        lock (entry)
        {
            // Check if lock is held by someone else
            if (entry.HolderId != null && entry.HolderId != holderId && entry.ExpiresAt > DateTime.UtcNow)
            {
                return new LockAcquisitionResult
                {
                    Success = false,
                    Error = $"Lock held by {entry.HolderId}",
                    WaitTime = entry.ExpiresAt - DateTime.UtcNow
                };
            }

            // Grant the lock
            var fencingToken = Interlocked.Increment(ref _fencingTokenCounter);
            entry.HolderId = holderId;
            entry.FencingToken = fencingToken;
            entry.AcquiredAt = DateTime.UtcNow;
            entry.ExpiresAt = DateTime.UtcNow + ttl;
            entry.Ttl = ttl;

            LockAcquired?.Invoke(this, new LockEventArgs { LockName = lockName, HolderId = holderId, FencingToken = fencingToken });

            return new LockAcquisitionResult
            {
                Success = true,
                FencingToken = fencingToken,
                ExpiresAt = entry.ExpiresAt
            };
        }
    }

    /// <summary>
    /// Releases a distributed lock.
    /// </summary>
    public bool ReleaseLock(string lockName, string holderId, long? expectedFencingToken = null)
    {
        if (!_locks.TryGetValue(lockName, out var entry))
            return false;

        lock (entry)
        {
            if (entry.HolderId != holderId)
                return false;

            if (expectedFencingToken.HasValue && entry.FencingToken != expectedFencingToken)
                return false;

            var token = entry.FencingToken;
            entry.HolderId = null;
            entry.FencingToken = 0;

            LockReleased?.Invoke(this, new LockEventArgs { LockName = lockName, HolderId = holderId, FencingToken = token });
            return true;
        }
    }

    /// <summary>
    /// Extends a lock's TTL.
    /// </summary>
    public bool ExtendLock(string lockName, string holderId, TimeSpan extension)
    {
        if (!_locks.TryGetValue(lockName, out var entry))
            return false;

        lock (entry)
        {
            if (entry.HolderId != holderId)
                return false;

            entry.ExpiresAt = DateTime.UtcNow + extension;
            return true;
        }
    }

    /// <summary>
    /// Validates a fencing token is current for a lock.
    /// </summary>
    public bool ValidateFencingToken(string lockName, long fencingToken)
    {
        if (!_locks.TryGetValue(lockName, out var entry))
            return false;

        return entry.FencingToken == fencingToken && entry.ExpiresAt > DateTime.UtcNow;
    }

    /// <summary>
    /// Gets lock status.
    /// </summary>
    public LockStatus GetLockStatus(string lockName)
    {
        if (!_locks.TryGetValue(lockName, out var entry))
            return new LockStatus { LockName = lockName, IsHeld = false };

        lock (entry)
        {
            return new LockStatus
            {
                LockName = lockName,
                IsHeld = entry.HolderId != null && entry.ExpiresAt > DateTime.UtcNow,
                HolderId = entry.HolderId,
                FencingToken = entry.FencingToken,
                ExpiresAt = entry.ExpiresAt
            };
        }
    }

    private void ExpireLocks(object? state)
    {
        if (_disposed) return;

        foreach (var kvp in _locks)
        {
            lock (kvp.Value)
            {
                if (kvp.Value.HolderId != null && kvp.Value.ExpiresAt <= DateTime.UtcNow)
                {
                    var holderId = kvp.Value.HolderId;
                    var token = kvp.Value.FencingToken;
                    kvp.Value.HolderId = null;
                    kvp.Value.FencingToken = 0;

                    LockExpired?.Invoke(this, new LockEventArgs { LockName = kvp.Key, HolderId = holderId, FencingToken = token });
                }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _expiryTimer.Dispose();
        return ValueTask.CompletedTask;
    }
}

#endregion

#region Distributed Counter Service (CRDT G-Counter)

/// <summary>
/// Distributed counter using CRDT G-Counter.
/// </summary>
public sealed class DistributedCounterService
{
    private readonly ConcurrentDictionary<string, GCounter> _counters = new();
    private readonly string _nodeId;

    public DistributedCounterService(string nodeId)
    {
        _nodeId = nodeId;
    }

    /// <summary>
    /// Gets or creates a counter.
    /// </summary>
    public GCounter GetOrCreateCounter(string counterName)
    {
        return _counters.GetOrAdd(counterName, _ => new GCounter(counterName));
    }

    /// <summary>
    /// Increments a counter.
    /// </summary>
    public long Increment(string counterName, long delta = 1)
    {
        var counter = GetOrCreateCounter(counterName);
        return counter.Increment(_nodeId, delta);
    }

    /// <summary>
    /// Gets counter value.
    /// </summary>
    public long GetValue(string counterName)
    {
        return _counters.TryGetValue(counterName, out var counter) ? counter.Value : 0;
    }

    /// <summary>
    /// Merges counter state from another node.
    /// </summary>
    public void Merge(string counterName, Dictionary<string, long> remoteState)
    {
        var counter = GetOrCreateCounter(counterName);
        counter.Merge(remoteState);
    }

    /// <summary>
    /// Gets counter state for replication.
    /// </summary>
    public Dictionary<string, long>? GetState(string counterName)
    {
        return _counters.TryGetValue(counterName, out var counter) ? counter.GetState() : null;
    }

    /// <summary>
    /// Lists all counters.
    /// </summary>
    public IReadOnlyList<CounterInfo> ListCounters()
    {
        return _counters.Values.Select(c => new CounterInfo
        {
            Name = c.Name,
            Value = c.Value,
            NodeCount = c.GetState().Count
        }).ToList();
    }
}

/// <summary>
/// CRDT G-Counter implementation.
/// </summary>
public sealed class GCounter
{
    public string Name { get; }
    private readonly ConcurrentDictionary<string, long> _counts = new();

    public GCounter(string name) => Name = name;

    public long Value => _counts.Values.Sum();

    public long Increment(string nodeId, long delta = 1)
    {
        _counts.AddOrUpdate(nodeId, delta, (_, current) => current + delta);
        return Value;
    }

    public void Merge(Dictionary<string, long> remoteState)
    {
        foreach (var kvp in remoteState)
        {
            _counts.AddOrUpdate(kvp.Key, kvp.Value, (_, current) => Math.Max(current, kvp.Value));
        }
    }

    public Dictionary<string, long> GetState() => new Dictionary<string, long>(_counts);
}

#endregion

#region Distributed Queue Service

/// <summary>
/// Distributed queue service for work distribution.
/// </summary>
public sealed class DistributedQueueService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DistributedQueue> _queues = new();
    private readonly string _nodeId;
    private readonly Timer _cleanupTimer;
    private volatile bool _disposed;

    public DistributedQueueService(string nodeId)
    {
        _nodeId = nodeId;
        _cleanupTimer = new Timer(CleanupExpiredItems, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Gets or creates a queue.
    /// </summary>
    public DistributedQueue GetOrCreateQueue(string queueName)
    {
        return _queues.GetOrAdd(queueName, _ => new DistributedQueue(queueName));
    }

    /// <summary>
    /// Enqueues an item.
    /// </summary>
    public QueueItem Enqueue(string queueName, byte[] data, TimeSpan? visibilityTimeout = null)
    {
        var queue = GetOrCreateQueue(queueName);
        return queue.Enqueue(data, _nodeId, visibilityTimeout);
    }

    /// <summary>
    /// Dequeues an item.
    /// </summary>
    public QueueItem? Dequeue(string queueName, TimeSpan visibilityTimeout)
    {
        if (!_queues.TryGetValue(queueName, out var queue))
            return null;

        return queue.Dequeue(_nodeId, visibilityTimeout);
    }

    /// <summary>
    /// Acknowledges (completes) processing of an item.
    /// </summary>
    public bool Acknowledge(string queueName, string itemId, string receiptHandle)
    {
        if (!_queues.TryGetValue(queueName, out var queue))
            return false;

        return queue.Acknowledge(itemId, receiptHandle);
    }

    /// <summary>
    /// Gets queue depth.
    /// </summary>
    public int GetQueueDepth(string queueName)
    {
        return _queues.TryGetValue(queueName, out var queue) ? queue.Count : 0;
    }

    /// <summary>
    /// Gets queue statistics.
    /// </summary>
    public QueueStats GetQueueStats(string queueName)
    {
        if (!_queues.TryGetValue(queueName, out var queue))
            return new QueueStats { QueueName = queueName };

        return queue.GetStats();
    }

    private void CleanupExpiredItems(object? state)
    {
        if (_disposed) return;

        foreach (var queue in _queues.Values)
        {
            queue.CleanupExpired();
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _cleanupTimer.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Distributed queue implementation.
/// </summary>
public sealed class DistributedQueue
{
    public string Name { get; }
    private readonly ConcurrentDictionary<string, QueueItem> _items = new();
    private readonly ConcurrentQueue<string> _availableIds = new();

    public int Count => _availableIds.Count;

    public DistributedQueue(string name) => Name = name;

    public QueueItem Enqueue(byte[] data, string producerId, TimeSpan? visibilityTimeout = null)
    {
        var item = new QueueItem
        {
            ItemId = Guid.NewGuid().ToString("N"),
            Data = data,
            ProducerId = producerId,
            EnqueuedAt = DateTime.UtcNow,
            VisibleAt = DateTime.UtcNow
        };

        _items[item.ItemId] = item;
        _availableIds.Enqueue(item.ItemId);
        return item;
    }

    public QueueItem? Dequeue(string consumerId, TimeSpan visibilityTimeout)
    {
        while (_availableIds.TryDequeue(out var itemId))
        {
            if (_items.TryGetValue(itemId, out var item))
            {
                if (item.VisibleAt <= DateTime.UtcNow && item.ConsumerId == null)
                {
                    var receiptHandle = Guid.NewGuid().ToString("N");
                    item.ConsumerId = consumerId;
                    item.ReceiptHandle = receiptHandle;
                    item.VisibleAt = DateTime.UtcNow + visibilityTimeout;
                    item.DequeueCount++;
                    return item;
                }
                else if (item.ConsumerId == null)
                {
                    _availableIds.Enqueue(itemId);
                }
            }
        }
        return null;
    }

    public bool Acknowledge(string itemId, string receiptHandle)
    {
        if (_items.TryGetValue(itemId, out var item) && item.ReceiptHandle == receiptHandle)
        {
            _items.TryRemove(itemId, out _);
            return true;
        }
        return false;
    }

    public void CleanupExpired()
    {
        foreach (var kvp in _items)
        {
            if (kvp.Value.ConsumerId != null && kvp.Value.VisibleAt <= DateTime.UtcNow)
            {
                // Return to queue (visibility timeout expired)
                kvp.Value.ConsumerId = null;
                kvp.Value.ReceiptHandle = null;
                _availableIds.Enqueue(kvp.Key);
            }
        }
    }

    public QueueStats GetStats()
    {
        return new QueueStats
        {
            QueueName = Name,
            TotalItems = _items.Count,
            AvailableItems = _availableIds.Count,
            InFlightItems = _items.Values.Count(i => i.ConsumerId != null)
        };
    }
}

#endregion

#region Distributed Config Service

/// <summary>
/// Distributed configuration service for cluster-wide config.
/// </summary>
public sealed class DistributedConfigService
{
    private readonly ConcurrentDictionary<string, ConfigEntry> _configs = new();
    private readonly ConcurrentDictionary<string, List<Action<ConfigEntry>>> _watchers = new();
    private readonly string _nodeId;

    public event EventHandler<ConfigChangedEventArgs>? ConfigChanged;

    public DistributedConfigService(string nodeId)
    {
        _nodeId = nodeId;
    }

    /// <summary>
    /// Sets a configuration value.
    /// </summary>
    public void Set(string key, string value, Dictionary<string, string>? metadata = null)
    {
        var entry = new ConfigEntry
        {
            Key = key,
            Value = value,
            Version = DateTime.UtcNow.Ticks,
            UpdatedBy = _nodeId,
            UpdatedAt = DateTime.UtcNow,
            Metadata = metadata ?? new()
        };

        _configs[key] = entry;

        ConfigChanged?.Invoke(this, new ConfigChangedEventArgs { Entry = entry });

        if (_watchers.TryGetValue(key, out var handlers))
        {
            foreach (var handler in handlers)
                handler(entry);
        }
    }

    /// <summary>
    /// Gets a configuration value.
    /// </summary>
    public string? Get(string key)
    {
        return _configs.TryGetValue(key, out var entry) ? entry.Value : null;
    }

    /// <summary>
    /// Gets a configuration entry with metadata.
    /// </summary>
    public ConfigEntry? GetEntry(string key)
    {
        return _configs.TryGetValue(key, out var entry) ? entry : null;
    }

    /// <summary>
    /// Gets all configuration keys matching a prefix.
    /// </summary>
    public IReadOnlyList<ConfigEntry> GetByPrefix(string prefix)
    {
        return _configs.Values.Where(e => e.Key.StartsWith(prefix)).ToList();
    }

    /// <summary>
    /// Deletes a configuration key.
    /// </summary>
    public bool Delete(string key)
    {
        return _configs.TryRemove(key, out _);
    }

    /// <summary>
    /// Watches a configuration key for changes.
    /// </summary>
    public void Watch(string key, Action<ConfigEntry> handler)
    {
        var handlers = _watchers.GetOrAdd(key, _ => new List<Action<ConfigEntry>>());
        lock (handlers)
        {
            handlers.Add(handler);
        }
    }

    /// <summary>
    /// Unwatches a configuration key.
    /// </summary>
    public void Unwatch(string key, Action<ConfigEntry> handler)
    {
        if (_watchers.TryGetValue(key, out var handlers))
        {
            lock (handlers)
            {
                handlers.Remove(handler);
            }
        }
    }

    /// <summary>
    /// Merges configuration from another node.
    /// </summary>
    public void Merge(Dictionary<string, ConfigEntry> remoteConfig)
    {
        foreach (var kvp in remoteConfig)
        {
            _configs.AddOrUpdate(
                kvp.Key,
                kvp.Value,
                (_, existing) => kvp.Value.Version > existing.Version ? kvp.Value : existing
            );
        }
    }

    /// <summary>
    /// Gets all configuration for replication.
    /// </summary>
    public Dictionary<string, ConfigEntry> GetAllConfig()
    {
        return new Dictionary<string, ConfigEntry>(_configs);
    }
}

#endregion

#region Types

public sealed class DistributedLockEntry
{
    public string LockName { get; init; } = string.Empty;
    public string? HolderId { get; set; }
    public long FencingToken { get; set; }
    public DateTime AcquiredAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public TimeSpan Ttl { get; set; }
}

public sealed class LockAcquisitionResult
{
    public bool Success { get; init; }
    public long FencingToken { get; init; }
    public DateTime ExpiresAt { get; init; }
    public string? Error { get; init; }
    public TimeSpan? WaitTime { get; init; }
}

public sealed class LockStatus
{
    public string LockName { get; init; } = string.Empty;
    public bool IsHeld { get; init; }
    public string? HolderId { get; init; }
    public long FencingToken { get; init; }
    public DateTime? ExpiresAt { get; init; }
}

public sealed class LockEventArgs : EventArgs
{
    public string LockName { get; init; } = string.Empty;
    public string? HolderId { get; init; }
    public long FencingToken { get; init; }
}

public sealed class CounterInfo
{
    public string Name { get; init; } = string.Empty;
    public long Value { get; init; }
    public int NodeCount { get; init; }
}

public sealed class QueueItem
{
    public string ItemId { get; init; } = string.Empty;
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public string ProducerId { get; init; } = string.Empty;
    public string? ConsumerId { get; set; }
    public string? ReceiptHandle { get; set; }
    public DateTime EnqueuedAt { get; init; }
    public DateTime VisibleAt { get; set; }
    public int DequeueCount { get; set; }
}

public sealed class QueueStats
{
    public string QueueName { get; init; } = string.Empty;
    public int TotalItems { get; init; }
    public int AvailableItems { get; init; }
    public int InFlightItems { get; init; }
}

public sealed class ConfigEntry
{
    public string Key { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public long Version { get; init; }
    public string UpdatedBy { get; init; } = string.Empty;
    public DateTime UpdatedAt { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
}

public sealed class ConfigChangedEventArgs : EventArgs
{
    public ConfigEntry Entry { get; init; } = null!;
}

#endregion
