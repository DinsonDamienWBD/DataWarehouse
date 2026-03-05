using DataWarehouse.SDK.Federation.Addressing;
using System.Diagnostics.CodeAnalysis;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Federation.Catalog;

/// <summary>
/// In-memory cache for manifest lookups.
/// </summary>
/// <remarks>
/// <para>
/// ManifestCache provides bounded in-memory caching for frequently accessed object locations.
/// It uses a simple LRU eviction strategy when the cache is full.
/// </para>
/// <para>
/// <strong>Eviction:</strong> When the cache reaches max size and a new entry is added,
/// the oldest entry (by UpdatedAt timestamp) is evicted. This is a simple eviction policy
/// that works well for recency-biased workloads.
/// </para>
/// <para>
/// <strong>Thread Safety:</strong> Uses ConcurrentDictionary for lock-free reads and writes.
/// </para>
/// </remarks>
internal sealed class ManifestCache
{
    private readonly BoundedDictionary<ObjectIdentity, ObjectLocationEntry> _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManifestCache"/> class.
    /// </summary>
    /// <param name="maxSize">The maximum number of entries to cache. Default: 100,000.</param>
    public ManifestCache(int maxSize = 100_000)
    {
        _cache = new BoundedDictionary<ObjectIdentity, ObjectLocationEntry>(maxSize);
    }

    /// <summary>
    /// Attempts to get an entry from the cache.
    /// </summary>
    /// <param name="objectId">The object identity.</param>
    /// <param name="entry">The cached entry, if found.</param>
    /// <returns>True if the entry was found; otherwise false.</returns>
    public bool TryGet(ObjectIdentity objectId, [NotNullWhen(true)] out ObjectLocationEntry? entry)
    {
        return _cache.TryGetValue(objectId, out entry);
    }

    /// <summary>
    /// Adds or updates an entry in the cache.
    /// </summary>
    /// <param name="entry">The entry to cache.</param>
    public void Set(ObjectLocationEntry entry)
    {
        // BoundedDictionary enforces capacity and LRU eviction atomically under its internal lock.
        // Manual eviction here was a TOCTOU race (check-evict-insert non-atomic under ConcurrentDictionary).
        _cache[entry.ObjectId] = entry;
    }

    /// <summary>
    /// Invalidates (removes) an entry from the cache.
    /// </summary>
    /// <param name="objectId">The object identity.</param>
    public void Invalidate(ObjectIdentity objectId)
    {
        _cache.TryRemove(objectId, out _);
    }

    /// <summary>
    /// Clears all entries from the cache.
    /// </summary>
    public void Clear() => _cache.Clear();
}
