using System.Collections.Concurrent;

namespace DataWarehouse.SDK.Services;

/// <summary>
/// VFS Placeholder service for managing ghost files and lazy-loaded content.
/// Placeholders appear as regular files but content is fetched on-demand.
/// Supports cloud sync, offline access, and storage optimization.
/// </summary>
public sealed class VfsPlaceholderService : IDisposable
{
    private readonly ConcurrentDictionary<string, PlaceholderEntry> _placeholders = new();
    private readonly ConcurrentDictionary<string, byte[]> _localCache = new();
    private readonly SemaphoreSlim _hydrationLock = new(10, 10); // Max 10 concurrent hydrations
    private readonly long _maxCacheSize;
    private long _currentCacheSize;
    private bool _disposed;

    /// <summary>
    /// Event raised when a placeholder is hydrated.
    /// </summary>
    public event EventHandler<PlaceholderHydratedEventArgs>? PlaceholderHydrated;

    /// <summary>
    /// Event raised when content is dehydrated to save space.
    /// </summary>
    public event EventHandler<PlaceholderDehydratedEventArgs>? PlaceholderDehydrated;

    /// <summary>
    /// Creates a new VFS placeholder service.
    /// </summary>
    /// <param name="maxCacheSize">Maximum cache size in bytes for hydrated content.</param>
    public VfsPlaceholderService(long maxCacheSize = 1024 * 1024 * 1024) // 1GB default
    {
        _maxCacheSize = maxCacheSize;
    }

    /// <summary>
    /// Registers a placeholder for a file.
    /// </summary>
    public PlaceholderEntry RegisterPlaceholder(string path, PlaceholderMetadata metadata, Func<Task<byte[]>> contentProvider)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty", nameof(path));

        var entry = new PlaceholderEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Path = NormalizePath(path),
            Metadata = metadata,
            ContentProvider = contentProvider,
            State = PlaceholderState.Dehydrated,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _placeholders[entry.Path] = entry;
        return entry;
    }

    /// <summary>
    /// Gets placeholder information for a path.
    /// </summary>
    public PlaceholderEntry? GetPlaceholder(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _placeholders.TryGetValue(NormalizePath(path), out var entry) ? entry : null;
    }

    /// <summary>
    /// Checks if a path is a placeholder.
    /// </summary>
    public bool IsPlaceholder(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _placeholders.ContainsKey(NormalizePath(path));
    }

    /// <summary>
    /// Checks if a placeholder is hydrated (content available locally).
    /// </summary>
    public bool IsHydrated(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalized = NormalizePath(path);
        return _placeholders.TryGetValue(normalized, out var entry) &&
               entry.State == PlaceholderState.Hydrated &&
               _localCache.ContainsKey(normalized);
    }

    /// <summary>
    /// Hydrates a placeholder by fetching its content.
    /// </summary>
    public async Task<byte[]> HydrateAsync(string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var normalized = NormalizePath(path);
        if (!_placeholders.TryGetValue(normalized, out var entry))
            throw new FileNotFoundException("Placeholder not found", path);

        // Return cached content if already hydrated
        if (_localCache.TryGetValue(normalized, out var cached))
        {
            entry.LastAccessedAt = DateTime.UtcNow;
            return cached;
        }

        await _hydrationLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_localCache.TryGetValue(normalized, out cached))
            {
                entry.LastAccessedAt = DateTime.UtcNow;
                return cached;
            }

            entry.State = PlaceholderState.Hydrating;

            // Fetch content
            var content = await entry.ContentProvider();

            // Ensure cache space
            await EnsureCacheSpaceAsync(content.Length);

            // Store in cache
            _localCache[normalized] = content;
            Interlocked.Add(ref _currentCacheSize, content.Length);

            entry.State = PlaceholderState.Hydrated;
            entry.HydratedAt = DateTime.UtcNow;
            entry.LastAccessedAt = DateTime.UtcNow;

            PlaceholderHydrated?.Invoke(this, new PlaceholderHydratedEventArgs
            {
                Path = path,
                Entry = entry,
                ContentSize = content.Length
            });

            return content;
        }
        catch
        {
            entry.State = PlaceholderState.Failed;
            throw;
        }
        finally
        {
            _hydrationLock.Release();
        }
    }

    /// <summary>
    /// Dehydrates a placeholder to free up local storage.
    /// </summary>
    public bool Dehydrate(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var normalized = NormalizePath(path);
        if (!_placeholders.TryGetValue(normalized, out var entry))
            return false;

        if (_localCache.TryRemove(normalized, out var content))
        {
            Interlocked.Add(ref _currentCacheSize, -content.Length);
            entry.State = PlaceholderState.Dehydrated;
            entry.DehydratedAt = DateTime.UtcNow;

            PlaceholderDehydrated?.Invoke(this, new PlaceholderDehydratedEventArgs
            {
                Path = path,
                Entry = entry,
                FreedBytes = content.Length
            });

            return true;
        }

        return false;
    }

    /// <summary>
    /// Removes a placeholder completely.
    /// </summary>
    public bool RemovePlaceholder(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var normalized = NormalizePath(path);
        if (_placeholders.TryRemove(normalized, out _))
        {
            if (_localCache.TryRemove(normalized, out var content))
            {
                Interlocked.Add(ref _currentCacheSize, -content.Length);
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Lists all placeholders.
    /// </summary>
    public IEnumerable<PlaceholderEntry> ListPlaceholders()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _placeholders.Values.ToList();
    }

    /// <summary>
    /// Lists placeholders in a directory.
    /// </summary>
    public IEnumerable<PlaceholderEntry> ListPlaceholders(string directory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var normalizedDir = NormalizePath(directory);
        if (!normalizedDir.EndsWith('/'))
            normalizedDir += '/';

        return _placeholders.Values
            .Where(e => e.Path.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    public PlaceholderCacheStats GetCacheStats()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var entries = _placeholders.Values.ToList();

        return new PlaceholderCacheStats
        {
            TotalPlaceholders = entries.Count,
            HydratedCount = entries.Count(e => e.State == PlaceholderState.Hydrated),
            DehydratedCount = entries.Count(e => e.State == PlaceholderState.Dehydrated),
            CurrentCacheSize = _currentCacheSize,
            MaxCacheSize = _maxCacheSize,
            CacheUtilization = _maxCacheSize > 0 ? (double)_currentCacheSize / _maxCacheSize : 0
        };
    }

    /// <summary>
    /// Pins a placeholder to prevent automatic dehydration.
    /// </summary>
    public bool Pin(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var normalized = NormalizePath(path);
        if (_placeholders.TryGetValue(normalized, out var entry))
        {
            entry.IsPinned = true;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Unpins a placeholder.
    /// </summary>
    public bool Unpin(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var normalized = NormalizePath(path);
        if (_placeholders.TryGetValue(normalized, out var entry))
        {
            entry.IsPinned = false;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Syncs placeholder metadata from a remote source.
    /// </summary>
    public void UpdateMetadata(string path, PlaceholderMetadata metadata)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var normalized = NormalizePath(path);
        if (_placeholders.TryGetValue(normalized, out var entry))
        {
            entry.Metadata = metadata;
            entry.UpdatedAt = DateTime.UtcNow;
        }
    }

    private async Task EnsureCacheSpaceAsync(long requiredBytes)
    {
        if (_currentCacheSize + requiredBytes <= _maxCacheSize)
            return;

        // Evict least recently accessed, unpinned entries
        var candidates = _placeholders.Values
            .Where(e => e.State == PlaceholderState.Hydrated && !e.IsPinned)
            .OrderBy(e => e.LastAccessedAt)
            .ToList();

        foreach (var entry in candidates)
        {
            if (_currentCacheSize + requiredBytes <= _maxCacheSize)
                break;

            Dehydrate(entry.Path);
            await Task.Yield(); // Allow other operations
        }
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _hydrationLock.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// State of a placeholder.
/// </summary>
public enum PlaceholderState
{
    Dehydrated,
    Hydrating,
    Hydrated,
    Failed
}

/// <summary>
/// Metadata for a placeholder file.
/// </summary>
public class PlaceholderMetadata
{
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public string ContentType { get; set; } = "application/octet-stream";
    public string? Checksum { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public string? RemoteId { get; set; }
    public string? RemoteUrl { get; set; }
    public Dictionary<string, object>? CustomMetadata { get; set; }
}

/// <summary>
/// Entry for a placeholder file.
/// </summary>
public class PlaceholderEntry
{
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public PlaceholderMetadata Metadata { get; set; } = new();
    public PlaceholderState State { get; set; }
    public bool IsPinned { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? HydratedAt { get; set; }
    public DateTime? DehydratedAt { get; set; }
    public DateTime? LastAccessedAt { get; set; }
    internal Func<Task<byte[]>> ContentProvider { get; set; } = () => Task.FromResult(Array.Empty<byte>());
}

/// <summary>
/// Cache statistics for placeholders.
/// </summary>
public class PlaceholderCacheStats
{
    public int TotalPlaceholders { get; set; }
    public int HydratedCount { get; set; }
    public int DehydratedCount { get; set; }
    public long CurrentCacheSize { get; set; }
    public long MaxCacheSize { get; set; }
    public double CacheUtilization { get; set; }
}

/// <summary>
/// Event args for placeholder hydration.
/// </summary>
public class PlaceholderHydratedEventArgs : EventArgs
{
    public string Path { get; set; } = string.Empty;
    public PlaceholderEntry Entry { get; set; } = new();
    public long ContentSize { get; set; }
}

/// <summary>
/// Event args for placeholder dehydration.
/// </summary>
public class PlaceholderDehydratedEventArgs : EventArgs
{
    public string Path { get; set; } = string.Empty;
    public PlaceholderEntry Entry { get; set; } = new();
    public long FreedBytes { get; set; }
}
