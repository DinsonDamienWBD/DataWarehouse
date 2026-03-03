using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Storage;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation.Tests;

/// <summary>
/// In-memory implementation of <see cref="IShardVdeAccessor"/> for testing cross-shard
/// operations without real VDE instances. Each shard stores objects in a
/// <see cref="SortedDictionary{TKey, TValue}"/> for deterministic ordering.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Federation Integration Tests (VFED-30)")]
internal sealed class InMemoryShardVdeAccessor : IShardVdeAccessor
{
    private readonly Dictionary<Guid, SortedDictionary<string, StorageObjectMetadata>> _shardData = new();
    private readonly object _lock = new();

    /// <summary>
    /// Creates a new shard with a random GUID and empty data store.
    /// </summary>
    /// <returns>The shard identifier.</returns>
    public Guid AddShard()
    {
        var shardId = Guid.NewGuid();
        lock (_lock)
        {
            _shardData[shardId] = new SortedDictionary<string, StorageObjectMetadata>(StringComparer.Ordinal);
        }
        return shardId;
    }

    /// <summary>
    /// Adds a single object to the specified shard with auto-generated timestamps and ETag.
    /// </summary>
    /// <param name="shardVdeId">The shard to add the object to.</param>
    /// <param name="key">The object key.</param>
    /// <param name="size">The object size in bytes.</param>
    /// <exception cref="KeyNotFoundException">Thrown when the shard does not exist.</exception>
    public void AddObject(Guid shardVdeId, string key, long size)
    {
        var now = DateTime.UtcNow;
        var metadata = new StorageObjectMetadata
        {
            Key = key,
            Size = size,
            Created = now,
            Modified = now,
            ETag = $"\"{Guid.NewGuid():N}\""
        };

        lock (_lock)
        {
            if (!_shardData.TryGetValue(shardVdeId, out var store))
                throw new KeyNotFoundException($"Shard {shardVdeId} does not exist.");

            store[key] = metadata;
        }
    }

    /// <summary>
    /// Bulk-adds objects to the specified shard.
    /// </summary>
    /// <param name="shardVdeId">The shard to add objects to.</param>
    /// <param name="objects">Enumerable of (key, size) tuples.</param>
    public void AddObjects(Guid shardVdeId, IEnumerable<(string key, long size)> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        foreach (var (key, size) in objects)
        {
            AddObject(shardVdeId, key, size);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Guid>> GetAllShardIdsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Guid>>(_shardData.Keys.ToList().AsReadOnly());
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StorageObjectMetadata> ListShardAsync(
        Guid shardVdeId,
        string? prefix,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<StorageObjectMetadata> snapshot;
        lock (_lock)
        {
            if (!_shardData.TryGetValue(shardVdeId, out var store))
                yield break;

            snapshot = prefix is null
                ? store.Values.ToList()
                : store.Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
                       .Select(kvp => kvp.Value)
                       .ToList();
        }

        foreach (var item in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }

        await Task.CompletedTask; // Ensure async enumerable compliance
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StorageObjectMetadata> SearchShardAsync(
        Guid shardVdeId,
        string query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<StorageObjectMetadata> snapshot;
        lock (_lock)
        {
            if (!_shardData.TryGetValue(shardVdeId, out var store))
                yield break;

            snapshot = store
                .Where(kvp => kvp.Key.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(kvp => kvp.Value)
                .ToList();
        }

        foreach (var item in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }

        await Task.CompletedTask; // Ensure async enumerable compliance
    }

    /// <inheritdoc />
    public Task DeleteFromShardAsync(Guid shardVdeId, string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (_shardData.TryGetValue(shardVdeId, out var store))
            {
                store.Remove(key);
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ShardMetadataStats> GetShardStatsAsync(Guid shardVdeId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (!_shardData.TryGetValue(shardVdeId, out var store))
                return Task.FromResult(default(ShardMetadataStats));

            long objectCount = store.Count;
            long totalSize = 0;
            foreach (var kvp in store)
            {
                totalSize += kvp.Value.Size;
            }

            return Task.FromResult(new ShardMetadataStats(objectCount, totalSize, 0, 0));
        }
    }
}
