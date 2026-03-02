using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation;

/// <summary>
/// Level 0 root catalog: a fully in-memory sorted structure holding up to 1M domain entries.
/// Designed for Raft replication across 5 replicas with O(log n) binary search for domain slot lookup.
/// </summary>
/// <remarks>
/// <para>
/// The root catalog stores <see cref="CatalogEntry"/> records in a contiguous sorted array
/// indexed by <see cref="CatalogEntry.StartSlot"/>. This layout maximizes CPU cache utilization
/// during binary search and keeps memory overhead minimal (1M entries = ~64MB + overhead &lt; 100MB).
/// </para>
/// <para>
/// Thread safety is provided via <see cref="ReaderWriterLockSlim"/>: read lock for lookups,
/// write lock for mutations (add/remove/update).
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Root Catalog (VFED-08)")]
public sealed class RootCatalog : IDisposable
{
    private CatalogEntry[] _entries;
    private int _count;
    private readonly int _maxEntries;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly CatalogReplicationConfig _replicationConfig;
    private bool _disposed;

    /// <summary>
    /// Initializes a new root catalog with the specified replication configuration and capacity.
    /// </summary>
    /// <param name="replicationConfig">Raft replication parameters for this catalog.</param>
    /// <param name="maxEntries">Maximum number of domain entries. Default is 1,048,576 (1M).</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="replicationConfig"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxEntries"/> is less than 1.</exception>
    public RootCatalog(CatalogReplicationConfig replicationConfig, int maxEntries = 1_048_576)
    {
        ArgumentNullException.ThrowIfNull(replicationConfig);
        if (maxEntries < 1)
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "Max entries must be at least 1.");

        _replicationConfig = replicationConfig;
        _maxEntries = maxEntries;
        _entries = new CatalogEntry[Math.Min(maxEntries, 1024)]; // Start small, grow as needed
        _count = 0;
    }

    /// <summary>
    /// Gets the current number of entries in the catalog.
    /// </summary>
    public int EntryCount
    {
        get
        {
            _lock.EnterReadLock();
            try { return _count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Gets the replication configuration for this catalog.
    /// </summary>
    public CatalogReplicationConfig ReplicationConfig => _replicationConfig;

    /// <summary>
    /// Estimates the memory consumption of this catalog in bytes for monitoring against the 100MB limit.
    /// </summary>
    public long EstimatedMemoryBytes
    {
        get
        {
            _lock.EnterReadLock();
            try { return (long)_count * CatalogEntry.SerializedSize + 64; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Looks up the domain catalog entry whose slot range contains the given domain slot.
    /// Uses O(log n) binary search on the sorted entry array.
    /// </summary>
    /// <param name="domainSlot">The domain hash slot to look up.</param>
    /// <returns>The matching <see cref="CatalogEntry"/>, or <c>null</c> if no entry contains the slot.</returns>
    public CatalogEntry? LookupDomain(int domainSlot)
    {
        _lock.EnterReadLock();
        try
        {
            int index = BinarySearchContaining(domainSlot);
            if (index >= 0)
                return _entries[index];
            return null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Adds a new entry to the catalog, maintaining sorted order by <see cref="CatalogEntry.StartSlot"/>.
    /// </summary>
    /// <param name="entry">The catalog entry to add.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the catalog is at maximum capacity or the entry's slot range overlaps with an existing entry.
    /// </exception>
    public void AddEntry(CatalogEntry entry)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_count >= _maxEntries)
                throw new InvalidOperationException(
                    $"Root catalog is at maximum capacity ({_maxEntries} entries).");

            // Find insertion point via binary search on StartSlot
            int insertIndex = FindInsertionPoint(entry.StartSlot);

            // Validate no overlap with neighbors
            ValidateNoOverlap(entry, insertIndex);

            // Grow array if needed
            EnsureCapacity(_count + 1);

            // Shift entries right to make room
            if (insertIndex < _count)
            {
                Array.Copy(_entries, insertIndex, _entries, insertIndex + 1, _count - insertIndex);
            }

            _entries[insertIndex] = entry;
            _count++;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Removes an entry by its VDE ID, compacting the array.
    /// </summary>
    /// <param name="shardVdeId">The VDE ID of the entry to remove.</param>
    /// <returns><c>true</c> if the entry was found and removed; otherwise <c>false</c>.</returns>
    public bool RemoveEntry(Guid shardVdeId)
    {
        _lock.EnterWriteLock();
        try
        {
            for (int i = 0; i < _count; i++)
            {
                if (_entries[i].ShardVdeId == shardVdeId)
                {
                    // Shift entries left to fill the gap
                    if (i < _count - 1)
                    {
                        Array.Copy(_entries, i + 1, _entries, i, _count - i - 1);
                    }
                    _count--;
                    _entries[_count] = default; // Clear the last slot
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
    /// Updates an existing entry, identified by its <see cref="CatalogEntry.ShardVdeId"/>.
    /// </summary>
    /// <param name="updated">The updated entry. Must have the same VDE ID as an existing entry.</param>
    /// <exception cref="KeyNotFoundException">Thrown when no entry with the given VDE ID exists.</exception>
    public void UpdateEntry(CatalogEntry updated)
    {
        _lock.EnterWriteLock();
        try
        {
            for (int i = 0; i < _count; i++)
            {
                if (_entries[i].ShardVdeId == updated.ShardVdeId)
                {
                    // If StartSlot changed, we need to re-sort
                    if (_entries[i].StartSlot != updated.StartSlot)
                    {
                        // Remove and re-add to maintain sorted order
                        if (i < _count - 1)
                        {
                            Array.Copy(_entries, i + 1, _entries, i, _count - i - 1);
                        }
                        _count--;

                        int newIndex = FindInsertionPoint(updated.StartSlot);
                        ValidateNoOverlap(updated, newIndex);

                        if (newIndex < _count)
                        {
                            Array.Copy(_entries, newIndex, _entries, newIndex + 1, _count - newIndex);
                        }
                        _entries[newIndex] = updated;
                        _count++;
                    }
                    else
                    {
                        _entries[i] = updated;
                    }
                    return;
                }
            }
            throw new KeyNotFoundException(
                $"No catalog entry found with ShardVdeId {updated.ShardVdeId}.");
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Returns a snapshot copy of all entries for administrative viewing.
    /// </summary>
    /// <returns>A read-only list of all current entries.</returns>
    public IReadOnlyList<CatalogEntry> GetAllEntries()
    {
        _lock.EnterReadLock();
        try
        {
            var result = new CatalogEntry[_count];
            Array.Copy(_entries, result, _count);
            return result;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Serializes the entire catalog to a stream for Raft log replication.
    /// Writes the entry count (int32 LE) followed by each entry (64 bytes each).
    /// </summary>
    /// <param name="stream">The destination stream.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
    public void SerializeTo(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        _lock.EnterReadLock();
        try
        {
            Span<byte> countBuffer = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(countBuffer, _count);
            stream.Write(countBuffer);

            Span<byte> entryBuffer = stackalloc byte[CatalogEntry.SerializedSize];
            for (int i = 0; i < _count; i++)
            {
                _entries[i].WriteTo(entryBuffer);
                stream.Write(entryBuffer);
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Deserializes a root catalog from a stream.
    /// </summary>
    /// <param name="stream">The source stream.</param>
    /// <param name="config">The replication configuration for the deserialized catalog.</param>
    /// <returns>A new <see cref="RootCatalog"/> with the deserialized entries.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> or <paramref name="config"/> is null.</exception>
    /// <exception cref="InvalidDataException">Thrown when the stream contains invalid data.</exception>
    public static RootCatalog DeserializeFrom(Stream stream, CatalogReplicationConfig config)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(config);

        Span<byte> countBuffer = stackalloc byte[4];
        if (stream.Read(countBuffer) < 4)
            throw new InvalidDataException("Stream too short to read entry count.");

        int count = BinaryPrimitives.ReadInt32LittleEndian(countBuffer);
        if (count < 0)
            throw new InvalidDataException($"Invalid entry count: {count}.");

        var catalog = new RootCatalog(config, Math.Max(count, 1));
        Span<byte> entryBuffer = stackalloc byte[CatalogEntry.SerializedSize];

        for (int i = 0; i < count; i++)
        {
            if (stream.Read(entryBuffer) < CatalogEntry.SerializedSize)
                throw new InvalidDataException($"Stream too short at entry {i}.");

            var entry = CatalogEntry.ReadFrom(entryBuffer);
            // Direct insert without overlap validation since data is trusted from replication
            catalog.EnsureCapacity(catalog._count + 1);
            catalog._entries[catalog._count] = entry;
            catalog._count++;
        }

        return catalog;
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
    /// Binary search for the entry whose [StartSlot, EndSlot) range contains the given slot.
    /// </summary>
    private int BinarySearchContaining(int slot)
    {
        int lo = 0;
        int hi = _count - 1;

        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            ref readonly CatalogEntry entry = ref _entries[mid];

            if (slot < entry.StartSlot)
            {
                hi = mid - 1;
            }
            else if (slot >= entry.EndSlot)
            {
                lo = mid + 1;
            }
            else
            {
                // slot >= StartSlot && slot < EndSlot
                return mid;
            }
        }

        return -1; // Not found
    }

    /// <summary>
    /// Finds the insertion point for a new entry with the given start slot, maintaining sorted order.
    /// </summary>
    private int FindInsertionPoint(int startSlot)
    {
        int lo = 0;
        int hi = _count;

        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (_entries[mid].StartSlot < startSlot)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    /// <summary>
    /// Validates that a new entry does not overlap with neighboring entries at the insertion point.
    /// </summary>
    private void ValidateNoOverlap(CatalogEntry entry, int insertIndex)
    {
        // Check overlap with the entry before the insertion point
        if (insertIndex > 0)
        {
            ref readonly CatalogEntry prev = ref _entries[insertIndex - 1];
            if (prev.EndSlot > entry.StartSlot)
                throw new InvalidOperationException(
                    $"Slot range [{entry.StartSlot}, {entry.EndSlot}) overlaps with existing entry " +
                    $"[{prev.StartSlot}, {prev.EndSlot}).");
        }

        // Check overlap with the entry at the insertion point
        if (insertIndex < _count)
        {
            ref readonly CatalogEntry next = ref _entries[insertIndex];
            if (entry.EndSlot > next.StartSlot)
                throw new InvalidOperationException(
                    $"Slot range [{entry.StartSlot}, {entry.EndSlot}) overlaps with existing entry " +
                    $"[{next.StartSlot}, {next.EndSlot}).");
        }
    }

    /// <summary>
    /// Ensures the internal array has capacity for at least the specified number of entries.
    /// </summary>
    private void EnsureCapacity(int required)
    {
        if (required <= _entries.Length)
            return;

        int newCapacity = Math.Min(
            Math.Max(_entries.Length * 2, required),
            _maxEntries);

        var newArray = new CatalogEntry[newCapacity];
        Array.Copy(_entries, newArray, _count);
        _entries = newArray;
    }
}
