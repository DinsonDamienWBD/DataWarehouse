using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation;

/// <summary>
/// Level 1 domain catalog: maps key-range slots within a single domain to index shard VDE addresses.
/// Each domain has its own catalog instance that is LRU-cached via <see cref="DomainCatalogCache"/>.
/// </summary>
/// <remarks>
/// <para>
/// Uses the same sorted-array binary-search pattern as <see cref="RootCatalog"/> for O(log n) lookups.
/// Domain catalogs are typically smaller than the root catalog (default max 65,536 entries vs 1M).
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Domain Catalog (VFED-09)")]
public sealed class DomainCatalog
{
    private CatalogEntry[] _entries;
    private int _count;
    private readonly int _maxEntries;
    private readonly object _syncRoot = new();

    /// <summary>
    /// Gets the VDE ID of the domain this catalog serves.
    /// </summary>
    public Guid DomainVdeId { get; }

    /// <summary>
    /// Initializes a new domain catalog for the specified domain.
    /// </summary>
    /// <param name="domainVdeId">The VDE ID of the domain this catalog serves.</param>
    /// <param name="maxEntries">Maximum number of index shard entries. Default is 65,536.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxEntries"/> is less than 1.</exception>
    public DomainCatalog(Guid domainVdeId, int maxEntries = 65536)
    {
        if (maxEntries < 1)
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "Max entries must be at least 1.");

        DomainVdeId = domainVdeId;
        _maxEntries = maxEntries;
        _entries = new CatalogEntry[Math.Min(maxEntries, 256)];
        _count = 0;
    }

    /// <summary>
    /// Gets the current number of entries in this domain catalog.
    /// </summary>
    public int EntryCount
    {
        get
        {
            lock (_syncRoot) { return _count; }
        }
    }

    /// <summary>
    /// Looks up the index shard entry whose slot range contains the given index slot.
    /// Uses O(log n) binary search.
    /// </summary>
    /// <param name="indexSlot">The index hash slot to look up.</param>
    /// <returns>The matching <see cref="CatalogEntry"/>, or <c>null</c> if no entry contains the slot.</returns>
    public CatalogEntry? LookupIndexShard(int indexSlot)
    {
        lock (_syncRoot)
        {
            int index = BinarySearchContaining(indexSlot);
            if (index >= 0)
                return _entries[index];
            return null;
        }
    }

    /// <summary>
    /// Adds a new entry, maintaining sorted order by <see cref="CatalogEntry.StartSlot"/>.
    /// </summary>
    /// <param name="entry">The catalog entry to add.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the catalog is at capacity or the entry overlaps an existing range.
    /// </exception>
    public void AddEntry(CatalogEntry entry)
    {
        lock (_syncRoot)
        {
            if (_count >= _maxEntries)
                throw new InvalidOperationException(
                    $"Domain catalog is at maximum capacity ({_maxEntries} entries).");

            int insertIndex = FindInsertionPoint(entry.StartSlot);
            ValidateNoOverlap(entry, insertIndex);
            EnsureCapacity(_count + 1);

            if (insertIndex < _count)
            {
                Array.Copy(_entries, insertIndex, _entries, insertIndex + 1, _count - insertIndex);
            }

            _entries[insertIndex] = entry;
            _count++;
        }
    }

    /// <summary>
    /// Removes an entry by its VDE ID, compacting the array.
    /// </summary>
    /// <param name="shardVdeId">The VDE ID of the entry to remove.</param>
    /// <returns><c>true</c> if the entry was found and removed; otherwise <c>false</c>.</returns>
    public bool RemoveEntry(Guid shardVdeId)
    {
        lock (_syncRoot)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_entries[i].ShardVdeId == shardVdeId)
                {
                    if (i < _count - 1)
                    {
                        Array.Copy(_entries, i + 1, _entries, i, _count - i - 1);
                    }
                    _count--;
                    _entries[_count] = default;
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Updates an existing entry, identified by its <see cref="CatalogEntry.ShardVdeId"/>.
    /// </summary>
    /// <param name="updated">The updated entry.</param>
    /// <exception cref="KeyNotFoundException">Thrown when no entry with the given VDE ID exists.</exception>
    public void UpdateEntry(CatalogEntry updated)
    {
        lock (_syncRoot)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_entries[i].ShardVdeId == updated.ShardVdeId)
                {
                    if (_entries[i].StartSlot != updated.StartSlot)
                    {
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
    }

    /// <summary>
    /// Returns a snapshot copy of all entries.
    /// </summary>
    /// <returns>A read-only list of all current entries.</returns>
    public IReadOnlyList<CatalogEntry> GetAllEntries()
    {
        lock (_syncRoot)
        {
            var result = new CatalogEntry[_count];
            Array.Copy(_entries, result, _count);
            return result;
        }
    }

    /// <summary>
    /// Serializes this domain catalog to a stream.
    /// Writes domain VDE ID (16 bytes), entry count (int32 LE), then each entry (64 bytes).
    /// </summary>
    /// <param name="stream">The destination stream.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
    public void SerializeTo(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        lock (_syncRoot)
        {
            // Write domain VDE ID
            Span<byte> guidBuffer = stackalloc byte[16];
            DomainVdeId.TryWriteBytes(guidBuffer);
            stream.Write(guidBuffer);

            // Write count
            Span<byte> countBuffer = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(countBuffer, _count);
            stream.Write(countBuffer);

            // Write entries
            Span<byte> entryBuffer = stackalloc byte[CatalogEntry.SerializedSize];
            for (int i = 0; i < _count; i++)
            {
                _entries[i].WriteTo(entryBuffer);
                stream.Write(entryBuffer);
            }
        }
    }

    /// <summary>
    /// Deserializes a domain catalog from a stream.
    /// </summary>
    /// <param name="stream">The source stream.</param>
    /// <param name="domainVdeId">The expected domain VDE ID (verified against stream contents).</param>
    /// <returns>A new <see cref="DomainCatalog"/> with the deserialized entries.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
    /// <exception cref="InvalidDataException">Thrown when the stream contains invalid data or VDE ID mismatch.</exception>
    public static DomainCatalog DeserializeFrom(Stream stream, Guid domainVdeId)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // Read domain VDE ID
        Span<byte> guidBuffer = stackalloc byte[16];
        if (stream.Read(guidBuffer) < 16)
            throw new InvalidDataException("Stream too short to read domain VDE ID.");

        var streamDomainId = new Guid(guidBuffer);
        if (streamDomainId != domainVdeId)
            throw new InvalidDataException(
                $"Domain VDE ID mismatch: expected {domainVdeId}, got {streamDomainId}.");

        // Read count
        Span<byte> countBuffer = stackalloc byte[4];
        if (stream.Read(countBuffer) < 4)
            throw new InvalidDataException("Stream too short to read entry count.");

        int count = BinaryPrimitives.ReadInt32LittleEndian(countBuffer);
        if (count < 0)
            throw new InvalidDataException($"Invalid entry count: {count}.");

        var catalog = new DomainCatalog(domainVdeId, Math.Max(count, 1));
        Span<byte> entryBuffer = stackalloc byte[CatalogEntry.SerializedSize];

        for (int i = 0; i < count; i++)
        {
            if (stream.Read(entryBuffer) < CatalogEntry.SerializedSize)
                throw new InvalidDataException($"Stream too short at entry {i}.");

            var entry = CatalogEntry.ReadFrom(entryBuffer);
            catalog.EnsureCapacity(catalog._count + 1);
            catalog._entries[catalog._count] = entry;
            catalog._count++;
        }

        return catalog;
    }

    private int BinarySearchContaining(int slot)
    {
        int lo = 0;
        int hi = _count - 1;

        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            ref readonly CatalogEntry entry = ref _entries[mid];

            if (slot < entry.StartSlot)
                hi = mid - 1;
            else if (slot >= entry.EndSlot)
                lo = mid + 1;
            else
                return mid;
        }

        return -1;
    }

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

    private void ValidateNoOverlap(CatalogEntry entry, int insertIndex)
    {
        if (insertIndex > 0)
        {
            ref readonly CatalogEntry prev = ref _entries[insertIndex - 1];
            if (prev.EndSlot > entry.StartSlot)
                throw new InvalidOperationException(
                    $"Slot range [{entry.StartSlot}, {entry.EndSlot}) overlaps with existing entry " +
                    $"[{prev.StartSlot}, {prev.EndSlot}).");
        }

        if (insertIndex < _count)
        {
            ref readonly CatalogEntry next = ref _entries[insertIndex];
            if (entry.EndSlot > next.StartSlot)
                throw new InvalidOperationException(
                    $"Slot range [{entry.StartSlot}, {entry.EndSlot}) overlaps with existing entry " +
                    $"[{next.StartSlot}, {next.EndSlot}).");
        }
    }

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

/// <summary>
/// LRU cache for <see cref="DomainCatalog"/> instances. Evicts least-recently-used domain catalogs
/// when the cache reaches its configured capacity.
/// </summary>
/// <remarks>
/// <para>
/// Uses a doubly-linked list + dictionary for O(1) access, O(1) insertion, and O(1) eviction.
/// Thread safety is provided via a simple lock (domain cache is not on the hot path).
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Domain Catalog Cache (VFED-09)")]
public sealed class DomainCatalogCache
{
    private readonly int _maxCachedDomains;
    private readonly LinkedList<(Guid DomainVdeId, DomainCatalog Catalog)> _lruList = new();
    private readonly Dictionary<Guid, LinkedListNode<(Guid DomainVdeId, DomainCatalog Catalog)>> _dict = new();
    private readonly object _syncRoot = new();
    private long _hits;
    private long _misses;
    private long _evictions;

    /// <summary>
    /// Initializes a new domain catalog cache with the specified capacity.
    /// </summary>
    /// <param name="maxCachedDomains">Maximum number of domain catalogs to cache. Default is 1,024.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxCachedDomains"/> is less than 1.</exception>
    public DomainCatalogCache(int maxCachedDomains = 1024)
    {
        if (maxCachedDomains < 1)
            throw new ArgumentOutOfRangeException(nameof(maxCachedDomains), "Must cache at least 1 domain.");

        _maxCachedDomains = maxCachedDomains;
    }

    /// <summary>
    /// Gets the number of domain catalogs currently in the cache.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_syncRoot) { return _dict.Count; }
        }
    }

    /// <summary>
    /// Retrieves a cached domain catalog, moving it to the front of the LRU list.
    /// Returns <c>null</c> on cache miss.
    /// </summary>
    /// <param name="domainVdeId">The domain VDE ID to look up.</param>
    /// <returns>The cached <see cref="DomainCatalog"/>, or <c>null</c> if not cached.</returns>
    public DomainCatalog? Get(Guid domainVdeId)
    {
        lock (_syncRoot)
        {
            if (_dict.TryGetValue(domainVdeId, out var node))
            {
                // Move to front (most recently used)
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                Interlocked.Increment(ref _hits);
                return node.Value.Catalog;
            }

            Interlocked.Increment(ref _misses);
            return null;
        }
    }

    /// <summary>
    /// Adds or updates a domain catalog in the cache. If the cache is at capacity,
    /// the least-recently-used entry is evicted.
    /// </summary>
    /// <param name="domainVdeId">The domain VDE ID.</param>
    /// <param name="catalog">The domain catalog to cache.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="catalog"/> is null.</exception>
    public void Put(Guid domainVdeId, DomainCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        lock (_syncRoot)
        {
            // If already present, update and move to front
            if (_dict.TryGetValue(domainVdeId, out var existingNode))
            {
                _lruList.Remove(existingNode);
                _dict.Remove(domainVdeId);
            }

            // Evict LRU tail if at capacity
            while (_dict.Count >= _maxCachedDomains && _lruList.Last != null)
            {
                var tail = _lruList.Last!;
                _dict.Remove(tail.Value.DomainVdeId);
                _lruList.RemoveLast();
                Interlocked.Increment(ref _evictions);
            }

            // Add to front
            var node = _lruList.AddFirst((domainVdeId, catalog));
            _dict[domainVdeId] = node;
        }
    }

    /// <summary>
    /// Explicitly evicts a specific domain catalog from the cache.
    /// Useful during shard migration to force fresh resolution.
    /// </summary>
    /// <param name="domainVdeId">The domain VDE ID to evict.</param>
    /// <returns><c>true</c> if the domain was cached and evicted; otherwise <c>false</c>.</returns>
    public bool Evict(Guid domainVdeId)
    {
        lock (_syncRoot)
        {
            if (_dict.TryGetValue(domainVdeId, out var node))
            {
                _lruList.Remove(node);
                _dict.Remove(domainVdeId);
                Interlocked.Increment(ref _evictions);
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Clears all entries from the cache.
    /// </summary>
    public void Clear()
    {
        lock (_syncRoot)
        {
            _lruList.Clear();
            _dict.Clear();
        }
    }

    /// <summary>
    /// Returns current cache performance statistics.
    /// </summary>
    /// <returns>A snapshot of hits, misses, and eviction counts.</returns>
    public DomainCatalogCacheStats GetStats()
    {
        return new DomainCatalogCacheStats(
            Interlocked.Read(ref _hits),
            Interlocked.Read(ref _misses),
            Interlocked.Read(ref _evictions));
    }

    /// <summary>
    /// Performance statistics for the domain catalog cache.
    /// </summary>
    /// <param name="Hits">Number of successful cache lookups.</param>
    /// <param name="Misses">Number of cache misses.</param>
    /// <param name="Evictions">Number of entries evicted (both LRU and explicit).</param>
    [SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Domain Catalog Cache (VFED-09)")]
    public readonly record struct DomainCatalogCacheStats(long Hits, long Misses, long Evictions);
}
