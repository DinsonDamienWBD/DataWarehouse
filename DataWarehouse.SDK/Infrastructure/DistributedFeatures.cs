using System.Collections.Concurrent;

namespace DataWarehouse.SDK.Infrastructure;

// ============================================================================
// DISTRIBUTED FEATURES - Locks, Caches, Reference Counting
// C7: Distributed Lock Handlers
// C8: Chunk Reference Counting
// C10: Lock TTL and Expiration
// C11: Bounded Caches with LRU Eviction
// C12: Race Condition Safe Pool Operations
// ============================================================================

#region C7 & C10: Distributed Lock Manager with TTL

/// <summary>
/// Distributed lock manager with TTL, expiration, and renewal support.
/// </summary>
public sealed class DistributedLockManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DistributedLock> _locks = new();
    private readonly Timer _expirationTimer;
    private readonly TimeSpan _defaultTtl;
    private readonly TimeSpan _cleanupInterval;
    private bool _disposed;

    public event Action<string, string>? OnLockExpired;
    public event Action<string, string>? OnLockAcquired;
    public event Action<string, string>? OnLockReleased;

    public DistributedLockManager(TimeSpan? defaultTtl = null, TimeSpan? cleanupInterval = null)
    {
        _defaultTtl = defaultTtl ?? TimeSpan.FromSeconds(30);
        _cleanupInterval = cleanupInterval ?? TimeSpan.FromSeconds(5);
        _expirationTimer = new Timer(CheckExpiredLocks, null, _cleanupInterval, _cleanupInterval);
    }

    /// <summary>
    /// Tries to acquire a distributed lock.
    /// </summary>
    public async Task<DistributedLockHandle?> TryAcquireAsync(
        string resourceId,
        string holderId,
        TimeSpan? ttl = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var lockTtl = ttl ?? _defaultTtl;
        var deadline = timeout.HasValue ? DateTime.UtcNow.Add(timeout.Value) : DateTime.MaxValue;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var @lock = new DistributedLock
            {
                ResourceId = resourceId,
                HolderId = holderId,
                AcquiredAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(lockTtl),
                Ttl = lockTtl,
                Version = 1
            };

            if (_locks.TryAdd(resourceId, @lock))
            {
                OnLockAcquired?.Invoke(resourceId, holderId);
                return new DistributedLockHandle(this, resourceId, holderId);
            }

            // Check if existing lock is expired
            if (_locks.TryGetValue(resourceId, out var existing))
            {
                if (existing.ExpiresAt <= DateTime.UtcNow)
                {
                    // Try to take over expired lock
                    if (_locks.TryUpdate(resourceId, @lock, existing))
                    {
                        OnLockExpired?.Invoke(resourceId, existing.HolderId);
                        OnLockAcquired?.Invoke(resourceId, holderId);
                        return new DistributedLockHandle(this, resourceId, holderId);
                    }
                }
            }

            // Wait and retry
            await Task.Delay(100, ct);
        }

        return null;
    }

    /// <summary>
    /// Releases a distributed lock.
    /// </summary>
    public bool Release(string resourceId, string holderId)
    {
        if (_locks.TryGetValue(resourceId, out var existing))
        {
            if (existing.HolderId == holderId)
            {
                if (_locks.TryRemove(resourceId, out _))
                {
                    OnLockReleased?.Invoke(resourceId, holderId);
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Renews a lock's TTL.
    /// </summary>
    public bool Renew(string resourceId, string holderId, TimeSpan? ttl = null)
    {
        if (_locks.TryGetValue(resourceId, out var existing))
        {
            if (existing.HolderId == holderId)
            {
                var newLock = existing with
                {
                    ExpiresAt = DateTime.UtcNow.Add(ttl ?? existing.Ttl),
                    Version = existing.Version + 1
                };
                return _locks.TryUpdate(resourceId, newLock, existing);
            }
        }
        return false;
    }

    /// <summary>
    /// Gets lock information.
    /// </summary>
    public DistributedLock? GetLock(string resourceId)
    {
        return _locks.TryGetValue(resourceId, out var @lock) ? @lock : null;
    }

    /// <summary>
    /// Gets all active locks.
    /// </summary>
    public IEnumerable<DistributedLock> GetAllLocks() => _locks.Values;

    private void CheckExpiredLocks(object? state)
    {
        var now = DateTime.UtcNow;
        var expired = _locks.Where(kvp => kvp.Value.ExpiresAt <= now).ToList();

        foreach (var kvp in expired)
        {
            if (_locks.TryRemove(kvp.Key, out var @lock))
            {
                OnLockExpired?.Invoke(kvp.Key, @lock.HolderId);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _expirationTimer.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Represents a distributed lock.
/// </summary>
public sealed record DistributedLock
{
    public required string ResourceId { get; init; }
    public required string HolderId { get; init; }
    public DateTime AcquiredAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public TimeSpan Ttl { get; init; }
    public long Version { get; init; }
    public bool IsExpired => ExpiresAt <= DateTime.UtcNow;
    public TimeSpan RemainingTime => IsExpired ? TimeSpan.Zero : ExpiresAt - DateTime.UtcNow;
}

/// <summary>
/// Handle for a held lock that auto-releases on dispose.
/// </summary>
public sealed class DistributedLockHandle : IAsyncDisposable
{
    private readonly DistributedLockManager _manager;
    private readonly string _resourceId;
    private readonly string _holderId;
    private readonly Timer? _renewalTimer;
    private bool _disposed;

    public string ResourceId => _resourceId;
    public string HolderId => _holderId;

    public DistributedLockHandle(DistributedLockManager manager, string resourceId, string holderId, TimeSpan? autoRenewInterval = null)
    {
        _manager = manager;
        _resourceId = resourceId;
        _holderId = holderId;

        if (autoRenewInterval.HasValue)
        {
            _renewalTimer = new Timer(_ => Renew(), null, autoRenewInterval.Value, autoRenewInterval.Value);
        }
    }

    public bool Renew(TimeSpan? ttl = null) => _manager.Renew(_resourceId, _holderId, ttl);

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _renewalTimer?.Dispose();
            _manager.Release(_resourceId, _holderId);
        }
        return ValueTask.CompletedTask;
    }
}

#endregion

#region C8: Chunk Reference Counting

/// <summary>
/// Reference counting for chunks with garbage collection support.
/// </summary>
public sealed class ChunkReferenceCounter
{
    private readonly ConcurrentDictionary<string, ChunkReference> _references = new();
    private readonly object _gcLock = new();
    private bool _gcInProgress;

    public event Action<string>? OnChunkUnreferenced;
    public event Action<string, int>? OnReferenceChanged;

    /// <summary>
    /// Adds a reference to a chunk.
    /// </summary>
    public int AddReference(string chunkId, string objectId)
    {
        var reference = _references.AddOrUpdate(
            chunkId,
            _ => new ChunkReference
            {
                ChunkId = chunkId,
                ReferenceCount = 1,
                ReferencingObjects = new HashSet<string> { objectId },
                CreatedAt = DateTime.UtcNow,
                LastAccessedAt = DateTime.UtcNow
            },
            (_, existing) =>
            {
                existing.ReferencingObjects.Add(objectId);
                existing.ReferenceCount = existing.ReferencingObjects.Count;
                existing.LastAccessedAt = DateTime.UtcNow;
                return existing;
            });

        OnReferenceChanged?.Invoke(chunkId, reference.ReferenceCount);
        return reference.ReferenceCount;
    }

    /// <summary>
    /// Removes a reference from a chunk.
    /// </summary>
    public int RemoveReference(string chunkId, string objectId)
    {
        if (_references.TryGetValue(chunkId, out var reference))
        {
            reference.ReferencingObjects.Remove(objectId);
            reference.ReferenceCount = reference.ReferencingObjects.Count;

            if (reference.ReferenceCount == 0)
            {
                if (_references.TryRemove(chunkId, out _))
                {
                    OnChunkUnreferenced?.Invoke(chunkId);
                }
                return 0;
            }

            OnReferenceChanged?.Invoke(chunkId, reference.ReferenceCount);
            return reference.ReferenceCount;
        }

        return -1; // Not found
    }

    /// <summary>
    /// Gets the reference count for a chunk.
    /// </summary>
    public int GetReferenceCount(string chunkId)
    {
        return _references.TryGetValue(chunkId, out var reference) ? reference.ReferenceCount : 0;
    }

    /// <summary>
    /// Gets all chunks with zero references (candidates for garbage collection).
    /// </summary>
    public IEnumerable<string> GetUnreferencedChunks()
    {
        return _references.Where(kvp => kvp.Value.ReferenceCount == 0).Select(kvp => kvp.Key);
    }

    /// <summary>
    /// Checks if a chunk is referenced by any object (for deduplication).
    /// </summary>
    public bool IsChunkDeduplicated(string chunkId)
    {
        return _references.TryGetValue(chunkId, out var reference) && reference.ReferenceCount > 1;
    }

    /// <summary>
    /// Runs garbage collection for unreferenced chunks.
    /// </summary>
    public async Task<GarbageCollectionResult> RunGarbageCollectionAsync(
        Func<string, Task<bool>> deleteChunk,
        TimeSpan? graceperiod = null,
        CancellationToken ct = default)
    {
        if (_gcInProgress)
            return new GarbageCollectionResult { Success = false, Error = "GC already in progress" };

        lock (_gcLock)
        {
            if (_gcInProgress)
                return new GarbageCollectionResult { Success = false, Error = "GC already in progress" };
            _gcInProgress = true;
        }

        try
        {
            var result = new GarbageCollectionResult { StartedAt = DateTime.UtcNow };
            var grace = graceperiod ?? TimeSpan.FromMinutes(5);
            var threshold = DateTime.UtcNow - grace;

            var candidates = _references
                .Where(kvp => kvp.Value.ReferenceCount == 0 && kvp.Value.LastAccessedAt < threshold)
                .ToList();

            result.CandidateCount = candidates.Count;

            foreach (var kvp in candidates)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    if (await deleteChunk(kvp.Key))
                    {
                        _references.TryRemove(kvp.Key, out _);
                        result.DeletedCount++;
                    }
                    else
                    {
                        result.FailedCount++;
                    }
                }
                catch
                {
                    result.FailedCount++;
                }
            }

            result.CompletedAt = DateTime.UtcNow;
            result.Success = true;
            return result;
        }
        finally
        {
            _gcInProgress = false;
        }
    }

    /// <summary>
    /// Gets all chunk references for debugging/monitoring.
    /// </summary>
    public IEnumerable<ChunkReference> GetAllReferences() => _references.Values;
}

/// <summary>
/// Represents a chunk reference.
/// </summary>
public sealed class ChunkReference
{
    public required string ChunkId { get; init; }
    public int ReferenceCount { get; set; }
    public HashSet<string> ReferencingObjects { get; init; } = new();
    public DateTime CreatedAt { get; init; }
    public DateTime LastAccessedAt { get; set; }
}

/// <summary>
/// Result of garbage collection.
/// </summary>
public sealed record GarbageCollectionResult
{
    public bool Success { get; set; }
    public int CandidateCount { get; set; }
    public int DeletedCount { get; set; }
    public int FailedCount { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public string? Error { get; set; }
    public TimeSpan Duration => CompletedAt - StartedAt;
}

#endregion

#region C11: Bounded LRU Cache

/// <summary>
/// Thread-safe LRU cache with configurable size bounds and eviction.
/// </summary>
public sealed class BoundedLruCache<TKey, TValue> : IDisposable where TKey : notnull
{
    private readonly int _maxSize;
    private readonly LinkedList<(TKey Key, TValue Value, DateTime LastAccess)> _list = new();
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value, DateTime LastAccess)>> _dict = new();
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly TimeSpan? _ttl;

    public event Action<TKey, TValue>? OnEvicted;

    public BoundedLruCache(int maxSize, TimeSpan? ttl = null)
    {
        _maxSize = maxSize > 0 ? maxSize : throw new ArgumentException("Max size must be positive");
        _ttl = ttl;
    }

    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try { return _dict.Count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    public int MaxSize => _maxSize;

    /// <summary>
    /// Gets or adds a value to the cache.
    /// </summary>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            if (_dict.TryGetValue(key, out var node))
            {
                // Move to front (most recently used)
                _lock.EnterWriteLock();
                try
                {
                    _list.Remove(node);
                    node.Value = (key, node.Value.Value, DateTime.UtcNow);
                    _list.AddFirst(node);
                    return node.Value.Value;
                }
                finally { _lock.ExitWriteLock(); }
            }

            // Not in cache, create new entry
            var value = valueFactory(key);

            _lock.EnterWriteLock();
            try
            {
                // Double-check after acquiring write lock
                if (_dict.TryGetValue(key, out node))
                    return node.Value.Value;

                // Evict if at capacity
                while (_dict.Count >= _maxSize)
                {
                    EvictLru();
                }

                var newNode = _list.AddFirst((key, value, DateTime.UtcNow));
                _dict[key] = newNode;
                return value;
            }
            finally { _lock.ExitWriteLock(); }
        }
        finally { _lock.ExitUpgradeableReadLock(); }
    }

    /// <summary>
    /// Tries to get a value from the cache.
    /// </summary>
    public bool TryGet(TKey key, out TValue? value)
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            if (_dict.TryGetValue(key, out var node))
            {
                // Check TTL
                if (_ttl.HasValue && DateTime.UtcNow - node.Value.LastAccess > _ttl.Value)
                {
                    _lock.EnterWriteLock();
                    try
                    {
                        _list.Remove(node);
                        _dict.Remove(key);
                        value = default;
                        return false;
                    }
                    finally { _lock.ExitWriteLock(); }
                }

                // Move to front
                _lock.EnterWriteLock();
                try
                {
                    _list.Remove(node);
                    node.Value = (key, node.Value.Value, DateTime.UtcNow);
                    _list.AddFirst(node);
                    value = node.Value.Value;
                    return true;
                }
                finally { _lock.ExitWriteLock(); }
            }

            value = default;
            return false;
        }
        finally { _lock.ExitUpgradeableReadLock(); }
    }

    /// <summary>
    /// Adds or updates a value in the cache.
    /// </summary>
    public void Set(TKey key, TValue value)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_dict.TryGetValue(key, out var node))
            {
                _list.Remove(node);
                node.Value = (key, value, DateTime.UtcNow);
                _list.AddFirst(node);
            }
            else
            {
                while (_dict.Count >= _maxSize)
                {
                    EvictLru();
                }

                var newNode = _list.AddFirst((key, value, DateTime.UtcNow));
                _dict[key] = newNode;
            }
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Removes a value from the cache.
    /// </summary>
    public bool Remove(TKey key)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_dict.TryGetValue(key, out var node))
            {
                _list.Remove(node);
                _dict.Remove(key);
                return true;
            }
            return false;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Clears the cache.
    /// </summary>
    public void Clear()
    {
        _lock.EnterWriteLock();
        try
        {
            _list.Clear();
            _dict.Clear();
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Removes expired entries (if TTL is set).
    /// </summary>
    public int RemoveExpired()
    {
        if (!_ttl.HasValue) return 0;

        _lock.EnterWriteLock();
        try
        {
            var threshold = DateTime.UtcNow - _ttl.Value;
            var removed = 0;

            var node = _list.Last;
            while (node != null && node.Value.LastAccess < threshold)
            {
                var prev = node.Previous;
                _list.Remove(node);
                _dict.Remove(node.Value.Key);
                removed++;
                node = prev;
            }

            return removed;
        }
        finally { _lock.ExitWriteLock(); }
    }

    private void EvictLru()
    {
        var last = _list.Last;
        if (last != null)
        {
            _list.RemoveLast();
            _dict.Remove(last.Value.Key);
            OnEvicted?.Invoke(last.Value.Key, last.Value.Value);
        }
    }

    public void Dispose()
    {
        _lock.Dispose();
    }
}

#endregion

#region C12: Thread-Safe Pool Operations

/// <summary>
/// Thread-safe pool with atomic operations to prevent race conditions.
/// </summary>
public sealed class ThreadSafePool<T> where T : class
{
    private readonly ConcurrentDictionary<string, PoolMember<T>> _members = new();
    private readonly ReaderWriterLockSlim _rebalanceLock = new();
    private long _version;

    public event Action<string, T>? OnMemberAdded;
    public event Action<string, T>? OnMemberRemoved;
    public event Action<long>? OnRebalanceComplete;

    public long Version => Interlocked.Read(ref _version);
    public int Count => _members.Count;

    /// <summary>
    /// Atomically adds a member to the pool.
    /// </summary>
    public bool TryAdd(string memberId, T member, PoolMemberMetadata? metadata = null)
    {
        var poolMember = new PoolMember<T>
        {
            MemberId = memberId,
            Member = member,
            Metadata = metadata ?? new PoolMemberMetadata(),
            AddedAt = DateTime.UtcNow,
            Version = Interlocked.Increment(ref _version)
        };

        if (_members.TryAdd(memberId, poolMember))
        {
            OnMemberAdded?.Invoke(memberId, member);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Atomically removes a member from the pool.
    /// </summary>
    public bool TryRemove(string memberId, out T? member)
    {
        if (_members.TryRemove(memberId, out var poolMember))
        {
            Interlocked.Increment(ref _version);
            member = poolMember.Member;
            OnMemberRemoved?.Invoke(memberId, poolMember.Member);
            return true;
        }

        member = default;
        return false;
    }

    /// <summary>
    /// Gets a member by ID.
    /// </summary>
    public bool TryGet(string memberId, out T? member)
    {
        if (_members.TryGetValue(memberId, out var poolMember))
        {
            member = poolMember.Member;
            return true;
        }

        member = default;
        return false;
    }

    /// <summary>
    /// Updates a member's metadata atomically.
    /// </summary>
    public bool TryUpdateMetadata(string memberId, Action<PoolMemberMetadata> update)
    {
        if (_members.TryGetValue(memberId, out var existing))
        {
            var updated = existing with { Version = Interlocked.Increment(ref _version) };
            update(updated.Metadata);
            return _members.TryUpdate(memberId, updated, existing);
        }
        return false;
    }

    /// <summary>
    /// Performs an atomic rebalance operation with exclusive access.
    /// </summary>
    public async Task<bool> RebalanceAsync(Func<IReadOnlyDictionary<string, PoolMember<T>>, Task<bool>> rebalanceAction)
    {
        _rebalanceLock.EnterWriteLock();
        try
        {
            // Create snapshot for rebalance logic
            var snapshot = _members.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            if (await rebalanceAction(snapshot))
            {
                var newVersion = Interlocked.Increment(ref _version);
                OnRebalanceComplete?.Invoke(newVersion);
                return true;
            }

            return false;
        }
        finally
        {
            _rebalanceLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Iterates over members safely with a consistent snapshot.
    /// </summary>
    public IEnumerable<(string Id, T Member)> EnumerateSafe()
    {
        _rebalanceLock.EnterReadLock();
        try
        {
            return _members.Select(kvp => (kvp.Key, kvp.Value.Member)).ToList();
        }
        finally
        {
            _rebalanceLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets all members.
    /// </summary>
    public IEnumerable<PoolMember<T>> GetAllMembers() => _members.Values;

    /// <summary>
    /// Selects members based on a predicate.
    /// </summary>
    public IEnumerable<T> Select(Func<PoolMember<T>, bool> predicate)
    {
        return _members.Values.Where(predicate).Select(m => m.Member);
    }
}

/// <summary>
/// Represents a member in a pool.
/// </summary>
public sealed record PoolMember<T>
{
    public required string MemberId { get; init; }
    public required T Member { get; init; }
    public PoolMemberMetadata Metadata { get; init; } = new();
    public DateTime AddedAt { get; init; }
    public long Version { get; init; }
}

/// <summary>
/// Metadata for pool members.
/// </summary>
public sealed class PoolMemberMetadata
{
    public int Weight { get; set; } = 100;
    public bool IsHealthy { get; set; } = true;
    public DateTime LastHealthCheck { get; set; } = DateTime.UtcNow;
    public long BytesUsed { get; set; }
    public long BytesCapacity { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new();
}

#endregion

#region Memory Pressure Integration

/// <summary>
/// Integrates caches with memory pressure monitoring for automatic eviction.
/// </summary>
public sealed class MemoryPressureAwareCache<TKey, TValue> : IDisposable where TKey : notnull
{
    private readonly BoundedLruCache<TKey, TValue> _cache;
    private readonly Timer _pressureCheckTimer;
    private readonly long _highPressureThreshold;
    private readonly float _evictionRatio;

    public MemoryPressureAwareCache(
        int maxSize,
        TimeSpan? ttl = null,
        long highPressureThresholdBytes = 0,
        float evictionRatio = 0.25f)
    {
        _cache = new BoundedLruCache<TKey, TValue>(maxSize, ttl);
        _highPressureThreshold = highPressureThresholdBytes > 0
            ? highPressureThresholdBytes
            : GC.GetGCMemoryInfo().HighMemoryLoadThresholdBytes;
        _evictionRatio = Math.Clamp(evictionRatio, 0.1f, 0.5f);

        // Check memory pressure every 10 seconds
        _pressureCheckTimer = new Timer(CheckMemoryPressure, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory) => _cache.GetOrAdd(key, valueFactory);
    public bool TryGet(TKey key, out TValue? value) => _cache.TryGet(key, out value);
    public void Set(TKey key, TValue value) => _cache.Set(key, value);
    public bool Remove(TKey key) => _cache.Remove(key);
    public void Clear() => _cache.Clear();
    public int Count => _cache.Count;

    private void CheckMemoryPressure(object? state)
    {
        var memInfo = GC.GetGCMemoryInfo();

        if (memInfo.MemoryLoadBytes > _highPressureThreshold)
        {
            // Under pressure - evict some entries
            var toEvict = (int)(_cache.Count * _evictionRatio);
            for (int i = 0; i < toEvict; i++)
            {
                _cache.RemoveExpired();
            }

            // Force GC if still under pressure
            if (memInfo.MemoryLoadBytes > _highPressureThreshold * 0.9)
            {
                GC.Collect(2, GCCollectionMode.Optimized, false);
            }
        }
    }

    public void Dispose()
    {
        _pressureCheckTimer.Dispose();
        _cache.Dispose();
    }
}

#endregion
