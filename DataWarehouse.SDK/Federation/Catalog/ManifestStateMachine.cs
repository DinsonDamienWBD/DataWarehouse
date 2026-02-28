using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Federation.Addressing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Federation.Catalog;

/// <summary>
/// Raft state machine for manifest operations.
/// </summary>
/// <remarks>
/// <para>
/// ManifestStateMachine implements the state machine pattern for Raft consensus. It applies
/// log entries (register, update, remove) to the in-memory manifest index and provides
/// snapshot/restore for Raft log compaction.
/// </para>
/// <para>
/// <strong>Thread Safety:</strong> Uses ReaderWriterLockSlim for concurrent reads with
/// exclusive writes. Read operations (GetLocation, QueryByTimeRange) acquire read locks;
/// write operations (Apply, RestoreSnapshot) acquire write locks.
/// </para>
/// <para>
/// <strong>Snapshot Format:</strong> Snapshots are JSON-serialized lists of all
/// ObjectLocationEntry records. This is simple but not optimized for large catalogs
/// (future work: binary serialization or incremental snapshots).
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Raft state machine for manifest")]
internal sealed class ManifestStateMachine : IDisposable
{
    private volatile bool _disposed;
    private readonly BoundedDictionary<ObjectIdentity, ObjectLocationEntry> _index;
    private readonly ReaderWriterLockSlim _lock;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManifestStateMachine"/> class.
    /// </summary>
    public ManifestStateMachine()
    {
        _index = new BoundedDictionary<ObjectIdentity, ObjectLocationEntry>(1000);
        _lock = new ReaderWriterLockSlim();
    }

    /// <summary>
    /// Captures a snapshot of the current manifest state.
    /// </summary>
    /// <returns>A JSON-serialized byte array of all manifest entries.</returns>
    public byte[] GetSnapshot()
    {
        _lock.EnterReadLock();
        try
        {
            var entries = _index.Values.ToList();
            return JsonSerializer.SerializeToUtf8Bytes(entries);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Restores the manifest state from a snapshot.
    /// </summary>
    /// <param name="snapshot">The JSON-serialized snapshot data.</param>
    public void RestoreSnapshot(byte[] snapshot)
    {
        _lock.EnterWriteLock();
        try
        {
            _index.Clear();
            var entries = JsonSerializer.Deserialize<List<ObjectLocationEntry>>(snapshot) ?? new List<ObjectLocationEntry>();
            foreach (var entry in entries)
            {
                _index[entry.ObjectId] = entry;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Applies a log entry to the state machine.
    /// </summary>
    /// <param name="logEntry">The JSON-serialized ManifestCommand.</param>
    public void Apply(byte[] logEntry)
    {
        var command = JsonSerializer.Deserialize<ManifestCommand>(logEntry);
        if (command == null) return;

        _lock.EnterWriteLock();
        try
        {
            switch (command.Action)
            {
                case "register":
                    if (command.Entry != null)
                    {
                        _index[command.Entry.ObjectId] = command.Entry;
                    }
                    break;

                case "update":
                    if (command.ObjectId.HasValue && command.NodeIds != null)
                    {
                        if (_index.TryGetValue(command.ObjectId.Value, out var existing))
                        {
                            var updated = existing with
                            {
                                NodeIds = command.NodeIds,
                                UpdatedAt = DateTimeOffset.UtcNow
                            };
                            _index[command.ObjectId.Value] = updated;
                        }
                    }
                    break;

                case "remove":
                    if (command.ObjectId.HasValue)
                    {
                        _index.TryRemove(command.ObjectId.Value, out _);
                    }
                    break;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Gets the location entry for a specific object.
    /// </summary>
    /// <param name="objectId">The object identity.</param>
    /// <returns>The location entry, or null if not found.</returns>
    public ObjectLocationEntry? GetLocation(ObjectIdentity objectId)
    {
        _lock.EnterReadLock();
        try
        {
            _index.TryGetValue(objectId, out var entry);
            return entry;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets the location entries for multiple objects.
    /// </summary>
    /// <param name="objectIds">The object identities.</param>
    /// <returns>A list of location entries for objects that were found.</returns>
    public List<ObjectLocationEntry> GetLocations(IEnumerable<ObjectIdentity> objectIds)
    {
        _lock.EnterReadLock();
        try
        {
            return objectIds
                .Select(id => _index.TryGetValue(id, out var entry) ? entry : null)
                .Where(e => e != null)
                .Cast<ObjectLocationEntry>()
                .ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Queries objects by time range using UUID v7 timestamp extraction.
    /// </summary>
    /// <param name="start">The start of the time range (inclusive).</param>
    /// <param name="end">The end of the time range (inclusive).</param>
    /// <returns>A list of location entries ordered by object ID.</returns>
    public List<ObjectLocationEntry> QueryByTimeRange(DateTimeOffset start, DateTimeOffset end)
    {
        _lock.EnterReadLock();
        try
        {
            // UUID v7 contains timestamp -- filter by UUID timestamp extraction
            return _index.Values
                .Where(e => e.ObjectId.Timestamp >= start && e.ObjectId.Timestamp <= end)
                .OrderBy(e => e.ObjectId)
                .ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets manifest statistics.
    /// </summary>
    /// <returns>Statistics about the manifest.</returns>
    public ManifestStatistics GetStatistics()
    {
        _lock.EnterReadLock();
        try
        {
            var entries = _index.Values;
            return new ManifestStatistics
            {
                TotalObjects = entries.Count(),
                TotalBytes = entries.Sum(e => e.SizeBytes),
                AverageReplication = entries.Any() ? entries.Average(e => e.ReplicationFactor) : 0,
                UniqueNodes = entries.SelectMany(e => e.NodeIds).Distinct().Count()
            };
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Disposes the ReaderWriterLockSlim used for thread-safe manifest access.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _lock.Dispose();
        }
    }
}

/// <summary>
/// Command structure for manifest state machine operations.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Manifest command for Raft state machine")]
internal sealed record ManifestCommand
{
    /// <summary>
    /// Gets the action to perform (register, update, remove).
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    /// Gets the object location entry (for register action).
    /// </summary>
    public ObjectLocationEntry? Entry { get; init; }

    /// <summary>
    /// Gets the object identity (for update and remove actions).
    /// </summary>
    public ObjectIdentity? ObjectId { get; init; }

    /// <summary>
    /// Gets the new node IDs (for update action).
    /// </summary>
    public IReadOnlyList<string>? NodeIds { get; init; }
}
