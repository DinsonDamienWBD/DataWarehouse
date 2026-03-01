using System;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Text;
using System.Threading;

namespace DataWarehouse.Plugins.UltimateConsensus;

/// <summary>
/// Consistent hash ring for routing keys to Raft groups.
/// Uses jump hash (Lamping &amp; Veach, 2014) for even distribution
/// with minimal rebalancing when groups are added/removed.
/// </summary>
/// <remarks>
/// Thread-safe: concurrent Route() calls use a reader lock; AddNode/RemoveNode use a writer lock.
/// P2-2208: Replaced exclusive lock with ReaderWriterLockSlim so read-heavy routing
/// does not block other routing threads.
/// </remarks>
public sealed class ConsistentHash : IDisposable
{
    // P2-2208: ReaderWriterLockSlim allows concurrent Route() reads.
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly HashSet<int> _activeNodes = new();
    private int _bucketCount;

    /// <summary>
    /// Creates a consistent hash ring with the specified number of initial buckets.
    /// </summary>
    /// <param name="initialBuckets">Number of initial hash buckets (Raft groups).</param>
    public ConsistentHash(int initialBuckets)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initialBuckets, 0);
        _bucketCount = initialBuckets;
        for (int i = 0; i < initialBuckets; i++)
        {
            _activeNodes.Add(i);
        }
    }

    /// <summary>
    /// Gets the bucket (Raft group ID) for the given key using jump hash.
    /// </summary>
    /// <param name="key">The key to route.</param>
    /// <param name="numBuckets">Number of buckets to hash into.</param>
    /// <returns>Bucket index in [0, numBuckets).</returns>
    public static int GetBucket(string key, int numBuckets)
    {
        // LOW-2211: Throw instead of silently returning 0 for invalid numBuckets.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(numBuckets, 0);
        var hash = XxHash32.HashToUInt32(Encoding.UTF8.GetBytes(key));
        return JumpHash(hash, numBuckets);
    }

    /// <summary>
    /// Routes a key to one of the active Raft group IDs.
    /// If the jump hash lands on a removed node, probes forward to find
    /// the nearest active node (wrapping around).
    /// </summary>
    /// <param name="key">The key to route.</param>
    /// <returns>Raft group ID.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no active nodes exist.</exception>
    public int Route(string key)
    {
        _lock.EnterReadLock();
        try
        {
            if (_activeNodes.Count == 0)
                throw new InvalidOperationException("No active nodes in the hash ring.");

            var bucket = GetBucket(key, _bucketCount);
            return ResolveActiveBucket(bucket);
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Routes raw binary data to a Raft group using XxHash32.
    /// If the jump hash lands on a removed node, probes forward to find
    /// the nearest active node (wrapping around).
    /// </summary>
    /// <param name="data">Binary data to hash.</param>
    /// <returns>Raft group ID.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no active nodes exist.</exception>
    public int Route(byte[] data)
    {
        _lock.EnterReadLock();
        try
        {
            if (_activeNodes.Count == 0)
                throw new InvalidOperationException("No active nodes in the hash ring.");

            if (_bucketCount <= 0) return 0;
            var hash = XxHash32.HashToUInt32(data);
            var bucket = JumpHash(hash, _bucketCount);
            return ResolveActiveBucket(bucket);
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Given a bucket from jump hash, finds the nearest active node.
    /// Probes forward with wrap-around. Caller must hold _lock and ensure _activeNodes is non-empty.
    /// </summary>
    private int ResolveActiveBucket(int bucket)
    {
        // Fast path: bucket is active
        if (_activeNodes.Contains(bucket))
            return bucket;

        // Probe forward with wrap-around
        for (int i = 1; i < _bucketCount; i++)
        {
            var candidate = (bucket + i) % _bucketCount;
            if (_activeNodes.Contains(candidate))
                return candidate;
        }

        // Fallback (should not reach here if _activeNodes is non-empty)
        return _activeNodes.First();
    }

    /// <summary>
    /// Adds a node (Raft group) to the hash ring.
    /// </summary>
    /// <param name="nodeId">Node/group ID to add.</param>
    public void AddNode(int nodeId)
    {
        _lock.EnterWriteLock();
        try
        {
            _activeNodes.Add(nodeId);
            _bucketCount = Math.Max(_bucketCount, nodeId + 1);
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Removes a node (Raft group) from the hash ring.
    /// Traffic for the removed node is redistributed to remaining active nodes
    /// via nearest-active-node probing in <see cref="Route(string)"/>.
    /// </summary>
    /// <param name="nodeId">Node/group ID to remove.</param>
    public void RemoveNode(int nodeId)
    {
        _lock.EnterWriteLock();
        try
        {
            _activeNodes.Remove(nodeId);
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Gets the current number of buckets.
    /// </summary>
    public int BucketCount
    {
        get
        {
            _lock.EnterReadLock();
            try { return _bucketCount; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _lock.Dispose();

    /// <summary>
    /// Jump consistent hash algorithm.
    /// O(ln(n)) time, zero memory, perfectly even distribution.
    /// </summary>
    private static int JumpHash(uint key, int numBuckets)
    {
        long b = -1;
        long j = 0;
        ulong k = key;

        while (j < numBuckets)
        {
            b = j;
            k = k * 2862933555777941757UL + 1;
            j = (long)((b + 1) * ((double)(1L << 31) / ((k >> 33) + 1)));
        }

        return (int)b;
    }
}
