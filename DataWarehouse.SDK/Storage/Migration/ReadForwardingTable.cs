using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Storage.Migration;

/// <summary>
/// In-memory forwarding table for transparent read redirection during data migration.
/// When an object is being migrated from NodeA to NodeB:
/// 1. Object is copied to NodeB
/// 2. Forwarding entry registered: objectKey -> NodeB
/// 3. Reads for objectKey on NodeA are forwarded to NodeB
/// 4. After migration completes, forwarding entry expires
///
/// Thread-safe via ConcurrentDictionary. Entries have TTL for automatic cleanup.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Background migration read forwarding")]
public sealed class ReadForwardingTable : IDisposable
{
    private readonly BoundedDictionary<string, ReadForwardingEntry> _entries = new BoundedDictionary<string, ReadForwardingEntry>(1000);
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _defaultTtl;
    private bool _disposed;

    public ReadForwardingTable(TimeSpan? defaultTtl = null)
    {
        _defaultTtl = defaultTtl ?? TimeSpan.FromHours(24);
        _cleanupTimer = new Timer(_ => CleanExpired(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Number of active forwarding entries.
    /// </summary>
    public int ActiveEntries => _entries.Count;

    /// <summary>
    /// Register a forwarding entry for an object being migrated.
    /// </summary>
    /// <param name="objectKey">The key of the object being migrated.</param>
    /// <param name="originalNode">The source node the object is moving from.</param>
    /// <param name="newNode">The target node the object is moving to.</param>
    /// <param name="maxHops">Maximum number of forwarding hops before the entry is considered stale.</param>
    public void RegisterForwarding(string objectKey, string originalNode, string newNode, int maxHops = 3)
    {
        var entry = new ReadForwardingEntry(
            ObjectKey: objectKey,
            OriginalNode: originalNode,
            NewNode: newNode,
            ExpiresUtc: DateTimeOffset.UtcNow.Add(_defaultTtl),
            Hops: maxHops);
        _entries.AddOrUpdate(objectKey, entry, (_, _) => entry);
    }

    /// <summary>
    /// Lookup forwarding for an object. Returns null if no forwarding is active.
    /// </summary>
    /// <param name="objectKey">The key of the object to look up.</param>
    /// <returns>The forwarding entry, or null if no active forwarding exists.</returns>
    public ReadForwardingEntry? Lookup(string objectKey)
    {
        if (_entries.TryGetValue(objectKey, out var entry))
        {
            if (entry.ExpiresUtc > DateTimeOffset.UtcNow && entry.Hops > 0)
                return entry;
            _entries.TryRemove(objectKey, out _);
        }
        return null;
    }

    /// <summary>
    /// Remove forwarding after migration is fully complete and verified.
    /// </summary>
    /// <param name="objectKey">The key of the object whose forwarding should be removed.</param>
    /// <returns>True if an entry was removed, false if no entry existed.</returns>
    public bool RemoveForwarding(string objectKey)
    {
        return _entries.TryRemove(objectKey, out _);
    }

    /// <summary>
    /// Bulk remove all forwarding entries originating from a specific node.
    /// Useful for cleaning up after a full node migration completes.
    /// </summary>
    /// <param name="nodeId">The original node ID whose forwarding entries should be removed.</param>
    /// <returns>The number of entries removed.</returns>
    public int RemoveByOriginalNode(string nodeId)
    {
        var toRemove = _entries.Where(e => e.Value.OriginalNode == nodeId).Select(e => e.Key).ToList();
        int removed = 0;
        foreach (var key in toRemove)
            if (_entries.TryRemove(key, out _)) removed++;
        return removed;
    }

    private void CleanExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = _entries.Where(e => e.Value.ExpiresUtc <= now).Select(e => e.Key).ToList();
        foreach (var key in expired)
            _entries.TryRemove(key, out _);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _cleanupTimer.Dispose();
        }
    }
}
