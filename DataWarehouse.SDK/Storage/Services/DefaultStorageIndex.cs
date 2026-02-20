using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Storage.Services;

/// <summary>
/// Default in-memory storage index implementation (AD-03).
/// Provides simple key/content-type matching for storage object metadata.
/// Extracted from IndexableStoragePluginBase logic.
/// </summary>
public sealed class DefaultStorageIndex : IStorageIndex
{
    private readonly BoundedDictionary<string, StorageObjectMetadata> _index = new BoundedDictionary<string, StorageObjectMetadata>(1000);

    /// <summary>
    /// Maximum number of entries in the index. Default: 100,000.
    /// Set to 0 for unlimited (not recommended for production).
    /// </summary>
    public int MaxIndexSize { get; set; } = 100_000;

    /// <inheritdoc/>
    public Task IndexAsync(string key, StorageObjectMetadata metadata, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(metadata);

        // Enforce bounded index
        if (MaxIndexSize > 0 && !_index.ContainsKey(key) && _index.Count >= MaxIndexSize)
        {
            // Evict oldest entry
            var oldest = _index.MinBy(kv => kv.Value.Modified);
            if (oldest.Key != null)
            {
                _index.TryRemove(oldest.Key, out _);
            }
        }

        _index[key] = metadata;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<StorageObjectMetadata>> SearchAsync(string query, int maxResults = 100, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult<IReadOnlyList<StorageObjectMetadata>>(Array.Empty<StorageObjectMetadata>());

        var queryLower = query.ToLowerInvariant();

        var results = _index.Values
            .Where(m =>
                m.Key.Contains(queryLower, StringComparison.OrdinalIgnoreCase) ||
                (m.ContentType?.Contains(queryLower, StringComparison.OrdinalIgnoreCase) == true) ||
                (m.CustomMetadata?.Values.Any(v => v.Contains(queryLower, StringComparison.OrdinalIgnoreCase)) == true))
            .Take(maxResults)
            .ToList();

        return Task.FromResult<IReadOnlyList<StorageObjectMetadata>>(results);
    }

    /// <inheritdoc/>
    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _index.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<long> GetIndexCountAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult((long)_index.Count);
    }

    /// <inheritdoc/>
    public async Task RebuildAsync(IAsyncEnumerable<StorageObjectMetadata> allObjects, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(allObjects);

        _index.Clear();

        await foreach (var metadata in allObjects.WithCancellation(ct))
        {
            if (MaxIndexSize > 0 && _index.Count >= MaxIndexSize)
                break;

            _index[metadata.Key] = metadata;
        }
    }
}
