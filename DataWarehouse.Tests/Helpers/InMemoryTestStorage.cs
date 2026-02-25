using System.Collections.Concurrent;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Tests.Helpers;

/// <summary>
/// Centralized in-memory storage for test data.
/// Thread-safe via ConcurrentDictionary. Provides save, load, delete, exists
/// operations with byte array storage. Replaces scattered inline test storage
/// classes across test files.
/// </summary>
public sealed class InMemoryTestStorage
{
    private readonly BoundedDictionary<string, byte[]> _store = new BoundedDictionary<string, byte[]>(1000);

    /// <summary>
    /// Number of items currently stored.
    /// </summary>
    public int Count => _store.Count;

    /// <summary>
    /// Total size in bytes of all stored items.
    /// </summary>
    public long TotalSizeBytes => _store.Values.Sum(v => (long)v.Length);

    /// <summary>
    /// All stored keys.
    /// </summary>
    public IReadOnlyCollection<string> Keys => _store.Keys.ToList().AsReadOnly();

    /// <summary>
    /// Save data to the store. Overwrites existing data for the same key.
    /// </summary>
    public Task SaveAsync(string key, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(data);
        ct.ThrowIfCancellationRequested();

        _store[key] = data;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Save a stream to the store.
    /// </summary>
    public async Task SaveAsync(string key, Stream data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(data);
        ct.ThrowIfCancellationRequested();

        using var ms = new MemoryStream(65536);
        await data.CopyToAsync(ms, ct);
        _store[key] = ms.ToArray();
    }

    /// <summary>
    /// Load data from the store. Throws FileNotFoundException if key not found.
    /// </summary>
    public Task<byte[]> LoadAsync(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ct.ThrowIfCancellationRequested();

        if (!_store.TryGetValue(key, out var data))
            throw new FileNotFoundException($"Key not found in test storage: {key}");

        return Task.FromResult(data);
    }

    /// <summary>
    /// Load data as a stream from the store.
    /// </summary>
    public Task<Stream> LoadStreamAsync(string key, CancellationToken ct = default)
    {
        var data = LoadAsync(key, ct).Result;
        return Task.FromResult<Stream>(new MemoryStream(data));
    }

    /// <summary>
    /// Delete data from the store. Returns true if the key was found and removed.
    /// </summary>
    public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(_store.TryRemove(key, out _));
    }

    /// <summary>
    /// Check whether a key exists in the store.
    /// </summary>
    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(_store.ContainsKey(key));
    }

    /// <summary>
    /// Clear all stored data.
    /// </summary>
    public void Clear()
    {
        _store.Clear();
    }

    /// <summary>
    /// Get all keys matching a prefix.
    /// </summary>
    public IEnumerable<string> GetKeysByPrefix(string prefix)
    {
        return _store.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
