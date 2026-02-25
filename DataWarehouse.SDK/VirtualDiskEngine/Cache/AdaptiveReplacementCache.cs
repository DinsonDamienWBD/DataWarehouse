using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Cache;

/// <summary>
/// L1 in-process Adaptive Replacement Cache with T1/T2/B1/B2 ghost lists.
/// Implements the full ARC algorithm that self-tunes between recency and frequency
/// based on ghost list hits. Thread-safe via ReaderWriterLockSlim.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: L1 ARC cache with self-tuning (VOPT-03)")]
public sealed class AdaptiveReplacementCache : IArcCache, IDisposable
{
    private readonly int _maxEntries;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    // T1: pages seen only once recently (recency)
    private readonly LinkedList<(long BlockNumber, byte[] Data)> _t1 = new();
    private readonly Dictionary<long, LinkedListNode<(long BlockNumber, byte[] Data)>> _t1Map = new();

    // T2: pages seen at least twice recently (frequency)
    private readonly LinkedList<(long BlockNumber, byte[] Data)> _t2 = new();
    private readonly Dictionary<long, LinkedListNode<(long BlockNumber, byte[] Data)>> _t2Map = new();

    // B1: ghost list for recently evicted from T1 (stores only block numbers)
    private readonly LinkedList<long> _b1 = new();
    private readonly Dictionary<long, LinkedListNode<long>> _b1Map = new();

    // B2: ghost list for recently evicted from T2 (stores only block numbers)
    private readonly LinkedList<long> _b2 = new();
    private readonly Dictionary<long, LinkedListNode<long>> _b2Map = new();

    // Self-tuning parameter: target size for T1 (0 = favor frequency, _maxEntries = favor recency)
    private int _p;

    // Statistics
    private long _hits;
    private long _misses;
    private long _evictions;

    private bool _disposed;

    /// <summary>
    /// Creates a new L1 ARC cache.
    /// </summary>
    /// <param name="maxEntries">Maximum number of cached blocks. If 0, auto-sizes from available system RAM assuming 4096-byte blocks.</param>
    public AdaptiveReplacementCache(int maxEntries = 0)
    {
        if (maxEntries < 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "Max entries must be non-negative.");

        if (maxEntries == 0)
        {
            // Auto-size: use 25% of available memory, assuming 4096-byte blocks
            long availableBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            long targetBytes = availableBytes / 4;
            const int defaultBlockSize = 4096;
            maxEntries = (int)Math.Min(targetBytes / defaultBlockSize, int.MaxValue);
            maxEntries = Math.Max(maxEntries, 64); // minimum 64 entries
        }

        _maxEntries = maxEntries;
    }

    /// <inheritdoc />
    public long Capacity => _maxEntries;

    /// <inheritdoc />
    public long Count
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _t1Map.Count + _t2Map.Count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<byte[]?> GetAsync(long blockNumber, CancellationToken ct = default)
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            // Case 1: Hit in T1 -- promote to T2 (now seen twice)
            if (_t1Map.TryGetValue(blockNumber, out var t1Node))
            {
                byte[] data = t1Node.Value.Data;
                _lock.EnterWriteLock();
                try
                {
                    _t1.Remove(t1Node);
                    _t1Map.Remove(blockNumber);

                    var t2Node = _t2.AddFirst((blockNumber, data));
                    _t2Map[blockNumber] = t2Node;
                }
                finally
                {
                    _lock.ExitWriteLock();
                }

                Interlocked.Increment(ref _hits);
                return new ValueTask<byte[]?>(data);
            }

            // Case 2: Hit in T2 -- move to MRU of T2
            if (_t2Map.TryGetValue(blockNumber, out var t2Hit))
            {
                byte[] data = t2Hit.Value.Data;
                _lock.EnterWriteLock();
                try
                {
                    _t2.Remove(t2Hit);
                    var newNode = _t2.AddFirst((blockNumber, data));
                    _t2Map[blockNumber] = newNode;
                }
                finally
                {
                    _lock.ExitWriteLock();
                }

                Interlocked.Increment(ref _hits);
                return new ValueTask<byte[]?>(data);
            }

            // Cache miss
            Interlocked.Increment(ref _misses);
            return new ValueTask<byte[]?>((byte[]?)null);
        }
        finally
        {
            _lock.ExitUpgradeableReadLock();
        }
    }

    /// <inheritdoc />
    public ValueTask PutAsync(long blockNumber, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        byte[] dataCopy = data.ToArray();

        _lock.EnterWriteLock();
        try
        {
            // Already in T1: promote to T2
            if (_t1Map.TryGetValue(blockNumber, out var t1Node))
            {
                _t1.Remove(t1Node);
                _t1Map.Remove(blockNumber);
                var node = _t2.AddFirst((blockNumber, dataCopy));
                _t2Map[blockNumber] = node;
                return default;
            }

            // Already in T2: update data, move to MRU
            if (_t2Map.TryGetValue(blockNumber, out var t2Node))
            {
                _t2.Remove(t2Node);
                var node = _t2.AddFirst((blockNumber, dataCopy));
                _t2Map[blockNumber] = node;
                return default;
            }

            // Case: block in B1 ghost list -- increase p (favor recency)
            if (_b1Map.TryGetValue(blockNumber, out var b1Node))
            {
                int delta = Math.Max(1, _b2Map.Count / Math.Max(_b1Map.Count, 1));
                _p = Math.Min(_p + delta, _maxEntries);

                _b1.Remove(b1Node);
                _b1Map.Remove(blockNumber);

                Replace(blockNumber, isInB2: false);

                var node = _t2.AddFirst((blockNumber, dataCopy));
                _t2Map[blockNumber] = node;
                return default;
            }

            // Case: block in B2 ghost list -- decrease p (favor frequency)
            if (_b2Map.TryGetValue(blockNumber, out var b2Node))
            {
                int delta = Math.Max(1, _b1Map.Count / Math.Max(_b2Map.Count, 1));
                _p = Math.Max(_p - delta, 0);

                _b2.Remove(b2Node);
                _b2Map.Remove(blockNumber);

                Replace(blockNumber, isInB2: true);

                var node = _t2.AddFirst((blockNumber, dataCopy));
                _t2Map[blockNumber] = node;
                return default;
            }

            // Case: new block not in any list
            int totalT1 = _t1Map.Count + _b1Map.Count;
            int totalT2 = _t2Map.Count + _b2Map.Count;

            if (totalT1 == _maxEntries)
            {
                if (_t1Map.Count < _maxEntries)
                {
                    // B1 is full, remove LRU from B1
                    EvictFromGhost(_b1, _b1Map);
                    Replace(blockNumber, isInB2: false);
                }
                else
                {
                    // T1 is full, remove LRU from T1 (no ghost)
                    EvictLruFromT1(addToGhost: false);
                }
            }
            else if (totalT1 + totalT2 >= _maxEntries)
            {
                if (totalT1 + totalT2 >= 2 * _maxEntries)
                {
                    // Remove LRU from B2
                    if (_b2Map.Count > 0)
                        EvictFromGhost(_b2, _b2Map);
                }
                Replace(blockNumber, isInB2: false);
            }

            // Insert into T1
            var newNode = _t1.AddFirst((blockNumber, dataCopy));
            _t1Map[blockNumber] = newNode;
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        return default;
    }

    /// <inheritdoc />
    public void Evict(long blockNumber)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_t1Map.TryGetValue(blockNumber, out var t1Node))
            {
                _t1.Remove(t1Node);
                _t1Map.Remove(blockNumber);
                Interlocked.Increment(ref _evictions);
            }
            else if (_t2Map.TryGetValue(blockNumber, out var t2Node))
            {
                _t2.Remove(t2Node);
                _t2Map.Remove(blockNumber);
                Interlocked.Increment(ref _evictions);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        _lock.EnterWriteLock();
        try
        {
            _t1.Clear();
            _t1Map.Clear();
            _t2.Clear();
            _t2Map.Clear();
            _b1.Clear();
            _b1Map.Clear();
            _b2.Clear();
            _b2Map.Clear();
            _p = 0;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public ArcCacheStats GetStats()
    {
        _lock.EnterReadLock();
        try
        {
            return new ArcCacheStats
            {
                Hits = Interlocked.Read(ref _hits),
                Misses = Interlocked.Read(ref _misses),
                Evictions = Interlocked.Read(ref _evictions),
                T1Size = _t1Map.Count,
                T2Size = _t2Map.Count,
                B1Size = _b1Map.Count,
                B2Size = _b2Map.Count,
            };
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Replace: evict from T1 or T2 depending on p and the context of the request.
    /// </summary>
    private void Replace(long blockNumber, bool isInB2)
    {
        int t1Count = _t1Map.Count;

        if (t1Count > 0 && (t1Count > _p || (isInB2 && t1Count == _p)))
        {
            // Evict LRU from T1, add to B1 ghost
            EvictLruFromT1(addToGhost: true);
        }
        else if (_t2Map.Count > 0)
        {
            // Evict LRU from T2, add to B2 ghost
            EvictLruFromT2(addToGhost: true);
        }
        else if (t1Count > 0)
        {
            // Fallback: evict from T1 if T2 is empty
            EvictLruFromT1(addToGhost: true);
        }
    }

    private void EvictLruFromT1(bool addToGhost)
    {
        var lruNode = _t1.Last;
        if (lruNode == null) return;

        long evictedBlock = lruNode.Value.BlockNumber;
        _t1.RemoveLast();
        _t1Map.Remove(evictedBlock);
        Interlocked.Increment(ref _evictions);

        if (addToGhost)
        {
            TrimGhostList(_b1, _b1Map, _maxEntries);
            var ghostNode = _b1.AddFirst(evictedBlock);
            _b1Map[evictedBlock] = ghostNode;
        }
    }

    private void EvictLruFromT2(bool addToGhost)
    {
        var lruNode = _t2.Last;
        if (lruNode == null) return;

        long evictedBlock = lruNode.Value.BlockNumber;
        _t2.RemoveLast();
        _t2Map.Remove(evictedBlock);
        Interlocked.Increment(ref _evictions);

        if (addToGhost)
        {
            TrimGhostList(_b2, _b2Map, _maxEntries);
            var ghostNode = _b2.AddFirst(evictedBlock);
            _b2Map[evictedBlock] = ghostNode;
        }
    }

    private static void EvictFromGhost(LinkedList<long> ghostList, Dictionary<long, LinkedListNode<long>> ghostMap)
    {
        var lru = ghostList.Last;
        if (lru == null) return;
        ghostMap.Remove(lru.Value);
        ghostList.RemoveLast();
    }

    private static void TrimGhostList(LinkedList<long> ghostList, Dictionary<long, LinkedListNode<long>> ghostMap, int maxSize)
    {
        while (ghostMap.Count >= maxSize)
        {
            EvictFromGhost(ghostList, ghostMap);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _lock.EnterWriteLock();
        try
        {
            _t1.Clear();
            _t1Map.Clear();
            _t2.Clear();
            _t2Map.Clear();
            _b1.Clear();
            _b1Map.Clear();
            _b2.Clear();
            _b2Map.Clear();
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        _lock.Dispose();
    }
}
