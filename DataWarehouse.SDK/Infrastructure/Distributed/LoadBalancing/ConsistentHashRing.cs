using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Distributed;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DataWarehouse.SDK.Infrastructure.Distributed
{
    /// <summary>
    /// Consistent hash ring with virtual nodes for uniform key distribution.
    /// Places N virtual nodes per physical node on a 32-bit hash ring and walks clockwise
    /// to find the responsible node. Adding/removing a node only affects keys between
    /// the node and its predecessor.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 29: Consistent hash ring")]
    public sealed class ConsistentHashRing : IConsistentHashRing, IDisposable
    {
        private readonly SortedDictionary<uint, string> _ring = new();
        private readonly ReaderWriterLockSlim _lock = new();
        private readonly HashSet<string> _physicalNodes = new();
        private uint[]? _sortedKeys;

        /// <summary>
        /// Creates a new consistent hash ring.
        /// </summary>
        /// <param name="virtualNodeCount">Number of virtual nodes per physical node. Default: 150.</param>
        public ConsistentHashRing(int virtualNodeCount = 150)
        {
            if (virtualNodeCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(virtualNodeCount), "Must be positive.");

            VirtualNodeCount = virtualNodeCount;
        }

        /// <inheritdoc />
        public int VirtualNodeCount { get; }

        /// <inheritdoc />
        public void AddNode(string nodeId)
        {
            ArgumentNullException.ThrowIfNull(nodeId);

            _lock.EnterWriteLock();
            try
            {
                _physicalNodes.Add(nodeId);

                for (int i = 0; i < VirtualNodeCount; i++)
                {
                    uint hash = ComputeHash($"{nodeId}:{i}");
                    _ring[hash] = nodeId;
                }

                _sortedKeys = null; // Invalidate cache
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <inheritdoc />
        public void RemoveNode(string nodeId)
        {
            ArgumentNullException.ThrowIfNull(nodeId);

            _lock.EnterWriteLock();
            try
            {
                _physicalNodes.Remove(nodeId);

                for (int i = 0; i < VirtualNodeCount; i++)
                {
                    uint hash = ComputeHash($"{nodeId}:{i}");
                    _ring.Remove(hash);
                }

                _sortedKeys = null; // Invalidate cache
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <inheritdoc />
        public string GetNode(string key)
        {
            ArgumentNullException.ThrowIfNull(key);

            _lock.EnterReadLock();
            try
            {
                if (_ring.Count == 0)
                    throw new InvalidOperationException("Hash ring is empty.");

                var keys = _sortedKeys ??= _ring.Keys.ToArray();
                uint hash = ComputeHash(key);

                int idx = Array.BinarySearch(keys, hash);
                if (idx < 0) idx = ~idx;
                if (idx >= keys.Length) idx = 0; // Wrap around (ring is circular)

                return _ring[keys[idx]];
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<string> GetNodes(string key, int count)
        {
            ArgumentNullException.ThrowIfNull(key);

            if (count <= 0)
                throw new ArgumentOutOfRangeException(nameof(count), "Must be positive.");

            _lock.EnterReadLock();
            try
            {
                if (_ring.Count == 0)
                    throw new InvalidOperationException("Hash ring is empty.");

                var keys = _sortedKeys ??= _ring.Keys.ToArray();
                uint hash = ComputeHash(key);

                int idx = Array.BinarySearch(keys, hash);
                if (idx < 0) idx = ~idx;
                if (idx >= keys.Length) idx = 0;

                // Walk clockwise collecting distinct physical nodes
                var distinctNodes = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                int maxPhysical = Math.Min(count, _physicalNodes.Count);

                for (int i = 0; i < keys.Length && distinctNodes.Count < maxPhysical; i++)
                {
                    int ringIdx = (idx + i) % keys.Length;
                    string nodeId = _ring[keys[ringIdx]];

                    if (seen.Add(nodeId))
                    {
                        distinctNodes.Add(nodeId);
                    }
                }

                return distinctNodes.AsReadOnly();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Computes a 32-bit hash using XxHash32 for ring placement.
        /// </summary>
        private static uint ComputeHash(string key)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(key);
            return System.IO.Hashing.XxHash32.HashToUInt32(bytes);
        }

        /// <summary>
        /// Disposes the reader-writer lock.
        /// </summary>
        public void Dispose()
        {
            _lock.Dispose();
        }
    }
}
