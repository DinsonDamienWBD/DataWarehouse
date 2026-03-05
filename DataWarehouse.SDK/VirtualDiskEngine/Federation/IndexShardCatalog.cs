using System;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Text;
using System.Threading;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation;

/// <summary>
/// Level 2 index shard: maps key ranges to data shard descriptors using a bloom filter for
/// 99%+ negative lookup rejection followed by binary search on a sorted data shard array.
/// </summary>
/// <remarks>
/// <para>
/// The bloom filter is checked BEFORE the binary search: if the bloom filter says NO,
/// the key is definitely not in any data shard managed by this index shard, and we return
/// immediately without touching the sorted array.
/// </para>
/// <para>
/// Thread safety is provided via <see cref="ReaderWriterLockSlim"/>: read lock for lookups,
/// write lock for mutations (add/remove/update/rebuild).
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Index Shard (VFED-12)")]
public sealed class IndexShardCatalog : IDisposable
{
    private DataShardDescriptor[] _dataShards;
    private int _count;
    private readonly int _maxDataShards;
    private readonly ShardBloomFilter _bloomFilter;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private bool _disposed;

    /// <summary>
    /// Gets the VDE instance ID of this index shard.
    /// </summary>
    public Guid IndexVdeId { get; }

    /// <summary>
    /// Gets the number of data shards managed by this index shard.
    /// </summary>
    public int DataShardCount
    {
        get
        {
            _lock.EnterReadLock();
            try { return _count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Gets the bloom filter for external serialization and digest computation.
    /// </summary>
    public ShardBloomFilter BloomFilter => _bloomFilter;

    /// <summary>
    /// Creates a new index shard catalog with the specified bloom filter and capacity.
    /// </summary>
    /// <param name="indexVdeId">The VDE instance ID of this index shard.</param>
    /// <param name="bloomFilter">The bloom filter for negative lookup rejection.</param>
    /// <param name="maxDataShards">Maximum number of data shards. Default is 4,096.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="bloomFilter"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxDataShards"/> is less than 1.</exception>
    public IndexShardCatalog(Guid indexVdeId, ShardBloomFilter bloomFilter, int maxDataShards = 4096)
    {
        ArgumentNullException.ThrowIfNull(bloomFilter);
        if (maxDataShards < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDataShards), "Must allow at least 1 data shard.");

        IndexVdeId = indexVdeId;
        _bloomFilter = bloomFilter;
        _maxDataShards = maxDataShards;
        _dataShards = new DataShardDescriptor[Math.Min(maxDataShards, 64)];
        _count = 0;
    }

    /// <summary>
    /// Looks up the data shard that should hold the given key.
    /// FIRST checks the bloom filter: if the filter says NO, returns <c>null</c> immediately
    /// (99%+ negative rejection). If the filter says MAYBE, computes the slot from the key hash
    /// and performs binary search on the sorted data shard array.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <returns>The matching <see cref="DataShardDescriptor"/>, or <c>null</c> if not found or bloom-rejected.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    public DataShardDescriptor? LookupDataShard(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        _lock.EnterReadLock();
        try
        {
            // Bloom filter fast rejection
            if (!_bloomFilter.MayContain(key))
                return null;

            // Compute slot from key hash for binary search
            int slot = ComputeSlotFromKey(key);
            return BinarySearchBySlot(slot);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Direct slot-based lookup without bloom filter check. Used for internal routing
    /// when the slot is already known (e.g., during shard split/merge operations).
    /// </summary>
    /// <param name="slot">The hash slot to look up.</param>
    /// <returns>The matching <see cref="DataShardDescriptor"/>, or <c>null</c> if no data shard contains the slot.</returns>
    public DataShardDescriptor? LookupDataShardBySlot(int slot)
    {
        _lock.EnterReadLock();
        try
        {
            return BinarySearchBySlot(slot);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Adds a data shard descriptor, maintaining sorted order by <see cref="DataShardDescriptor.StartSlot"/>.
    /// Validates that the new descriptor's slot range does not overlap with any existing descriptor.
    /// </summary>
    /// <param name="descriptor">The data shard descriptor to add.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the catalog is at capacity or the slot range overlaps with an existing descriptor.
    /// </exception>
    public void AddDataShard(DataShardDescriptor descriptor)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_count >= _maxDataShards)
                throw new InvalidOperationException(
                    $"Index shard catalog is at maximum capacity ({_maxDataShards} data shards).");

            int insertIndex = FindInsertionPoint(descriptor.StartSlot);
            ValidateNoOverlap(descriptor, insertIndex);
            EnsureCapacity(_count + 1);

            if (insertIndex < _count)
            {
                Array.Copy(_dataShards, insertIndex, _dataShards, insertIndex + 1, _count - insertIndex);
            }

            _dataShards[insertIndex] = descriptor;
            _count++;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Removes a data shard by its VDE ID, compacting the array.
    /// </summary>
    /// <param name="dataVdeId">The VDE ID of the data shard to remove.</param>
    /// <returns><c>true</c> if the data shard was found and removed; otherwise <c>false</c>.</returns>
    public bool RemoveDataShard(Guid dataVdeId)
    {
        _lock.EnterWriteLock();
        try
        {
            for (int i = 0; i < _count; i++)
            {
                if (_dataShards[i].DataVdeId == dataVdeId)
                {
                    if (i < _count - 1)
                    {
                        Array.Copy(_dataShards, i + 1, _dataShards, i, _count - i - 1);
                    }
                    _count--;
                    _dataShards[_count] = default;
                    return true;
                }
            }
            return false;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Updates an existing data shard descriptor, identified by its <see cref="DataShardDescriptor.DataVdeId"/>.
    /// </summary>
    /// <param name="updated">The updated descriptor. Must have the same DataVdeId as an existing descriptor.</param>
    /// <exception cref="KeyNotFoundException">Thrown when no descriptor with the given DataVdeId exists.</exception>
    public void UpdateDataShard(DataShardDescriptor updated)
    {
        _lock.EnterWriteLock();
        try
        {
            for (int i = 0; i < _count; i++)
            {
                if (_dataShards[i].DataVdeId == updated.DataVdeId)
                {
                    if (_dataShards[i].StartSlot != updated.StartSlot)
                    {
                        // Slot range changed -- remove and re-insert to maintain sort order
                        if (i < _count - 1)
                        {
                            Array.Copy(_dataShards, i + 1, _dataShards, i, _count - i - 1);
                        }
                        _count--;

                        int newIndex = FindInsertionPoint(updated.StartSlot);
                        ValidateNoOverlap(updated, newIndex);

                        if (newIndex < _count)
                        {
                            Array.Copy(_dataShards, newIndex, _dataShards, newIndex + 1, _count - newIndex);
                        }
                        _dataShards[newIndex] = updated;
                        _count++;
                    }
                    else
                    {
                        _dataShards[i] = updated;
                    }
                    return;
                }
            }
            throw new KeyNotFoundException(
                $"No data shard descriptor found with DataVdeId {updated.DataVdeId}.");
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Returns a snapshot copy of all data shard descriptors.
    /// </summary>
    /// <returns>A read-only list of all current data shard descriptors.</returns>
    public IReadOnlyList<DataShardDescriptor> GetAllDataShards()
    {
        _lock.EnterReadLock();
        try
        {
            var result = new DataShardDescriptor[_count];
            Array.Copy(_dataShards, result, _count);
            return result;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Rebuilds the bloom filter by iterating all keys in all data shards.
    /// Called during shard split/merge operations to ensure filter accuracy.
    /// </summary>
    /// <param name="keyEnumerator">
    /// Function that returns all keys stored in a given data shard VDE.
    /// Takes the data shard VDE ID and returns an enumerable of keys.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="keyEnumerator"/> is null.</exception>
    public void RebuildBloomFilter(Func<Guid, IEnumerable<string>> keyEnumerator)
    {
        ArgumentNullException.ThrowIfNull(keyEnumerator);

        _lock.EnterWriteLock();
        try
        {
            _bloomFilter.Clear();

            for (int i = 0; i < _count; i++)
            {
                foreach (string key in keyEnumerator(_dataShards[i].DataVdeId))
                {
                    _bloomFilter.Add(key);
                }
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Releases resources used by the <see cref="ReaderWriterLockSlim"/>.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _lock.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    /// Computes a hash slot from a key using the federation hash seed.
    /// Uses the same XxHash64 approach as PathHashRouter for consistency.
    /// </summary>
    private static int ComputeSlotFromKey(string key)
    {
        int maxBytes = Encoding.UTF8.GetMaxByteCount(key.Length);
        Span<byte> buffer = maxBytes <= 512 ? stackalloc byte[maxBytes] : new byte[maxBytes];
        int written = Encoding.UTF8.GetBytes(key, buffer);
        ulong hash = XxHash64.HashToUInt64(buffer[..written], (long)FederationConstants.HashSeed);
        return (int)(hash % (ulong)FederationConstants.DefaultDomainSlotCount);
    }

    /// <summary>
    /// Binary search for the data shard whose [StartSlot, EndSlot) range contains the given slot.
    /// </summary>
    private DataShardDescriptor? BinarySearchBySlot(int slot)
    {
        int lo = 0;
        int hi = _count - 1;

        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            ref readonly DataShardDescriptor desc = ref _dataShards[mid];

            if (slot < desc.StartSlot)
                hi = mid - 1;
            else if (slot >= desc.EndSlot)
                lo = mid + 1;
            else
                return desc;
        }

        return null;
    }

    private int FindInsertionPoint(int startSlot)
    {
        int lo = 0;
        int hi = _count;

        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (_dataShards[mid].StartSlot < startSlot)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    private void ValidateNoOverlap(DataShardDescriptor descriptor, int insertIndex)
    {
        if (insertIndex > 0)
        {
            ref readonly DataShardDescriptor prev = ref _dataShards[insertIndex - 1];
            if (prev.EndSlot > descriptor.StartSlot)
                throw new InvalidOperationException(
                    $"Slot range [{descriptor.StartSlot}, {descriptor.EndSlot}) overlaps with existing " +
                    $"[{prev.StartSlot}, {prev.EndSlot}).");
        }

        if (insertIndex < _count)
        {
            ref readonly DataShardDescriptor next = ref _dataShards[insertIndex];
            if (descriptor.EndSlot > next.StartSlot)
                throw new InvalidOperationException(
                    $"Slot range [{descriptor.StartSlot}, {descriptor.EndSlot}) overlaps with existing " +
                    $"[{next.StartSlot}, {next.EndSlot}).");
        }
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _dataShards.Length)
            return;

        int newCapacity = Math.Min(
            Math.Max(_dataShards.Length * 2, required),
            _maxDataShards);

        var newArray = new DataShardDescriptor[newCapacity];
        Array.Copy(_dataShards, newArray, _count);
        _dataShards = newArray;
    }
}
