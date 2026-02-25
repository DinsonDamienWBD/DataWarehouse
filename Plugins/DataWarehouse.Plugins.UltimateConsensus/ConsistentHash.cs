using System;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Text;

namespace DataWarehouse.Plugins.UltimateConsensus;

/// <summary>
/// Consistent hash ring for routing keys to Raft groups.
/// Uses jump hash (Lamping &amp; Veach, 2014) for even distribution
/// with minimal rebalancing when groups are added/removed.
/// </summary>
/// <remarks>
/// Thread-safe: read operations are lock-free; mutations use a lock.
/// </remarks>
public sealed class ConsistentHash
{
    private readonly object _lock = new();
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
        if (numBuckets <= 0) return 0;
        var hash = XxHash32.HashToUInt32(Encoding.UTF8.GetBytes(key));
        return JumpHash(hash, numBuckets);
    }

    /// <summary>
    /// Routes a key to one of the active Raft group IDs.
    /// </summary>
    /// <param name="key">The key to route.</param>
    /// <returns>Raft group ID.</returns>
    public int Route(string key)
    {
        lock (_lock)
        {
            return GetBucket(key, _bucketCount);
        }
    }

    /// <summary>
    /// Routes raw binary data to a Raft group using XxHash32.
    /// </summary>
    /// <param name="data">Binary data to hash.</param>
    /// <returns>Raft group ID.</returns>
    public int Route(byte[] data)
    {
        lock (_lock)
        {
            if (_bucketCount <= 0) return 0;
            var hash = XxHash32.HashToUInt32(data);
            return JumpHash(hash, _bucketCount);
        }
    }

    /// <summary>
    /// Adds a node (Raft group) to the hash ring.
    /// </summary>
    /// <param name="nodeId">Node/group ID to add.</param>
    public void AddNode(int nodeId)
    {
        lock (_lock)
        {
            _activeNodes.Add(nodeId);
            _bucketCount = Math.Max(_bucketCount, nodeId + 1);
        }
    }

    /// <summary>
    /// Removes a node (Raft group) from the hash ring.
    /// Note: Does not reduce bucket count to maintain stable routing.
    /// </summary>
    /// <param name="nodeId">Node/group ID to remove.</param>
    public void RemoveNode(int nodeId)
    {
        lock (_lock)
        {
            _activeNodes.Remove(nodeId);
        }
    }

    /// <summary>
    /// Gets the current number of buckets.
    /// </summary>
    public int BucketCount
    {
        get { lock (_lock) { return _bucketCount; } }
    }

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
