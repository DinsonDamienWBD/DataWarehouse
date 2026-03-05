using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Scale.LsmTree
{
    /// <summary>
    /// In-memory sorted write buffer for LSM-Tree.
    /// Thread-safe implementation using ReaderWriterLockSlim.
    /// </summary>
    public sealed class MemTable : IDisposable
    {
        private readonly SortedDictionary<byte[], byte[]?> _data;
        private readonly ReaderWriterLockSlim _lock;
        private long _estimatedSize;

        /// <summary>
        /// Maximum size threshold in bytes (default 4MB).
        /// </summary>
        public long MaxSize { get; }

        /// <summary>
        /// Current number of entries in the MemTable.
        /// </summary>
        public int Count
        {
            get
            {
                _lock.EnterReadLock();
                try
                {
                    return _data.Count;
                }
                finally
                {
                    _lock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Estimated size of data in bytes.
        /// </summary>
        public long EstimatedSize => Interlocked.Read(ref _estimatedSize);

        /// <summary>
        /// Returns true if the MemTable has reached its size threshold.
        /// </summary>
        public bool IsFull => EstimatedSize >= MaxSize;

        /// <summary>
        /// Initializes a new MemTable with the specified maximum size.
        /// </summary>
        /// <param name="maxSize">Maximum size in bytes (default 4MB).</param>
        public MemTable(long maxSize = 4 * 1024 * 1024)
        {
            MaxSize = maxSize;
            _data = new SortedDictionary<byte[], byte[]?>(ByteArrayComparer.Instance);
            _lock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
            _estimatedSize = 0;
        }

        /// <summary>
        /// Inserts or updates a key-value pair.
        /// </summary>
        /// <param name="key">Key to insert.</param>
        /// <param name="value">Value to store.</param>
        public void Put(byte[] key, byte[] value)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            _lock.EnterWriteLock();
            try
            {
                // Update size estimate
                if (_data.TryGetValue(key, out var oldValue))
                {
                    // Replace existing entry
                    var sizeDelta = value.Length - (oldValue?.Length ?? 0);
                    Interlocked.Add(ref _estimatedSize, sizeDelta);
                }
                else
                {
                    // New entry
                    var sizeDelta = key.Length + value.Length + 16; // overhead estimate
                    Interlocked.Add(ref _estimatedSize, sizeDelta);
                }

                _data[key] = value;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Retrieves the value for a given key.
        /// </summary>
        /// <param name="key">Key to look up.</param>
        /// <returns>Value if found, null if not found or deleted.</returns>
        public byte[]? Get(byte[] key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            _lock.EnterReadLock();
            try
            {
                if (_data.TryGetValue(key, out var value))
                {
                    return value;
                }
                return null;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Marks a key as deleted (tombstone).
        /// </summary>
        /// <param name="key">Key to delete.</param>
        public void Delete(byte[] key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            _lock.EnterWriteLock();
            try
            {
                if (_data.TryGetValue(key, out var oldValue))
                {
                    // Existing entry: subtract the old value size (tombstone itself has zero value bytes).
                    var sizeDelta = -(oldValue?.Length ?? 0);
                    Interlocked.Add(ref _estimatedSize, sizeDelta);
                }
                else
                {
                    // New tombstone for a key not previously in the MemTable.
                    // Add only the key overhead (16 bytes node overhead, no value bytes for tombstone).
                    // Do NOT add key.Length here since there was no prior entry to account for.
                    // This prevents repeated deletes of non-existent keys from inflating size
                    // and triggering premature flushes.
                    Interlocked.Add(ref _estimatedSize, 16L);
                }

                _data[key] = null; // Tombstone marker
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Scans for all entries with keys matching the given prefix.
        /// </summary>
        /// <param name="prefix">Prefix to match.</param>
        /// <returns>Enumerable of matching key-value pairs.</returns>
        public IEnumerable<KeyValuePair<byte[], byte[]?>> Scan(byte[] prefix)
        {
            if (prefix == null)
            {
                throw new ArgumentNullException(nameof(prefix));
            }

            _lock.EnterReadLock();
            try
            {
                return _data
                    .Where(kvp => StartsWithPrefix(kvp.Key, prefix))
                    .ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Gets all sorted entries for flushing to disk.
        /// Returns a snapshot copy.
        /// </summary>
        /// <returns>Sorted list of all entries.</returns>
        public List<KeyValuePair<byte[], byte[]?>> GetSortedEntries()
        {
            _lock.EnterReadLock();
            try
            {
                return _data.ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Clears all entries and resets size counters.
        /// </summary>
        public void Clear()
        {
            _lock.EnterWriteLock();
            try
            {
                _data.Clear();
                Interlocked.Exchange(ref _estimatedSize, 0);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Checks if a key starts with the given prefix.
        /// </summary>
        private static bool StartsWithPrefix(byte[] key, byte[] prefix)
        {
            if (key.Length < prefix.Length)
            {
                return false;
            }

            return key.AsSpan(0, prefix.Length).SequenceEqual(prefix);
        }

        /// <summary>
        /// Disposes resources used by the MemTable.
        /// </summary>
        public void Dispose()
        {
            _lock?.Dispose();
        }
    }
}
