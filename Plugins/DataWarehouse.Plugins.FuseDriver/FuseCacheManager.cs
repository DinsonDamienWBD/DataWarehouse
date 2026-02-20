// <copyright file="FuseCacheManager.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// Licensed under the MIT License.
// </copyright>

using System.Runtime.InteropServices;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.FuseDriver;

/// <summary>
/// Manages kernel cache integration for the FUSE filesystem.
/// Provides direct I/O options, kernel page cache management, and splice/sendfile zero-copy support.
/// </summary>
public sealed class FuseCacheManager : IDisposable
{
    private readonly FuseConfig _config;
    private readonly IKernelContext? _kernelContext;
    private readonly BoundedDictionary<string, CacheEntry> _attrCache = new BoundedDictionary<string, CacheEntry>(1000);
    private readonly BoundedDictionary<string, byte[]> _readCache = new BoundedDictionary<string, byte[]>(1000);
    private readonly BoundedDictionary<ulong, WriteBufferEntry> _writeBuffers = new BoundedDictionary<ulong, WriteBufferEntry>(1000);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Timer _cleanupTimer;
    private readonly object _statsLock = new();
    private bool _disposed;

    private long _cacheHits;
    private long _cacheMisses;
    private long _bytesReadFromCache;
    private long _bytesWrittenToCache;

    /// <summary>
    /// Gets the maximum size of the read cache in bytes.
    /// </summary>
    public long MaxReadCacheSize { get; set; } = 256 * 1024 * 1024; // 256 MB

    /// <summary>
    /// Gets the maximum size of the write buffer in bytes.
    /// </summary>
    public long MaxWriteBufferSize { get; set; } = 64 * 1024 * 1024; // 64 MB

    /// <summary>
    /// Gets the attribute cache TTL.
    /// </summary>
    public TimeSpan AttributeCacheTtl { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets the read cache TTL.
    /// </summary>
    public TimeSpan ReadCacheTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets the current read cache size in bytes.
    /// </summary>
    public long CurrentReadCacheSize => _readCache.Values.Sum(v => (long)v.Length);

    /// <summary>
    /// Gets the current write buffer size in bytes.
    /// </summary>
    public long CurrentWriteBufferSize => _writeBuffers.Values.Sum(e => (long)e.Data.Length);

    /// <summary>
    /// Initializes a new instance of the <see cref="FuseCacheManager"/> class.
    /// </summary>
    /// <param name="config">The FUSE configuration.</param>
    /// <param name="kernelContext">Optional kernel context for logging.</param>
    public FuseCacheManager(FuseConfig config, IKernelContext? kernelContext = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _kernelContext = kernelContext;

        // Start cleanup timer
        _cleanupTimer = new Timer(CleanupExpiredEntries, null,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    #region Attribute Cache

    /// <summary>
    /// Gets cached file attributes.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>The cached attributes, or null if not cached or expired.</returns>
    public FuseStat? GetCachedAttributes(string path)
    {
        if (!_config.KernelCache)
            return null;

        if (_attrCache.TryGetValue(path, out var entry))
        {
            if (DateTime.UtcNow - entry.CachedAt < AttributeCacheTtl)
            {
                Interlocked.Increment(ref _cacheHits);
                return entry.Stat;
            }

            // Entry expired, remove it
            _attrCache.TryRemove(path, out _);
        }

        Interlocked.Increment(ref _cacheMisses);
        return null;
    }

    /// <summary>
    /// Caches file attributes.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="stat">The attributes to cache.</param>
    public void CacheAttributes(string path, FuseStat stat)
    {
        if (!_config.KernelCache)
            return;

        _attrCache[path] = new CacheEntry
        {
            Stat = stat,
            CachedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Invalidates cached attributes for a path.
    /// </summary>
    /// <param name="path">The file path.</param>
    public void InvalidateAttributes(string path)
    {
        _attrCache.TryRemove(path, out _);
    }

    /// <summary>
    /// Invalidates all cached attributes for paths matching a prefix.
    /// </summary>
    /// <param name="prefix">The path prefix.</param>
    public void InvalidateAttributesPrefix(string prefix)
    {
        var keysToRemove = _attrCache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        foreach (var key in keysToRemove)
        {
            _attrCache.TryRemove(key, out _);
        }
    }

    #endregion

    #region Read Cache

    /// <summary>
    /// Gets cached read data.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="offset">The read offset.</param>
    /// <param name="length">The read length.</param>
    /// <returns>The cached data, or null if not cached.</returns>
    public byte[]? GetCachedRead(string path, long offset, int length)
    {
        if (_config.DirectIO || !_config.KernelCache)
            return null;

        var key = GetReadCacheKey(path, offset, length);

        if (_readCache.TryGetValue(key, out var data))
        {
            Interlocked.Increment(ref _cacheHits);
            Interlocked.Add(ref _bytesReadFromCache, data.Length);
            return data;
        }

        Interlocked.Increment(ref _cacheMisses);
        return null;
    }

    /// <summary>
    /// Caches read data.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="offset">The read offset.</param>
    /// <param name="data">The data to cache.</param>
    public void CacheRead(string path, long offset, byte[] data)
    {
        if (_config.DirectIO || !_config.KernelCache)
            return;

        // Check if we need to evict entries
        while (CurrentReadCacheSize + data.Length > MaxReadCacheSize && _readCache.Count > 0)
        {
            // Simple eviction: remove first entry
            var firstKey = _readCache.Keys.FirstOrDefault();
            if (firstKey != null)
            {
                _readCache.TryRemove(firstKey, out _);
            }
        }

        var key = GetReadCacheKey(path, offset, data.Length);
        _readCache[key] = data;
        Interlocked.Add(ref _bytesWrittenToCache, data.Length);
    }

    /// <summary>
    /// Invalidates cached read data for a path.
    /// </summary>
    /// <param name="path">The file path.</param>
    public void InvalidateReadCache(string path)
    {
        var keysToRemove = _readCache.Keys.Where(k => k.StartsWith(path + "|", StringComparison.Ordinal)).ToList();
        foreach (var key in keysToRemove)
        {
            _readCache.TryRemove(key, out _);
        }
    }

    #endregion

    #region Write Buffer

    /// <summary>
    /// Buffers write data for coalescing.
    /// </summary>
    /// <param name="fileHandle">The file handle.</param>
    /// <param name="path">The file path.</param>
    /// <param name="offset">The write offset.</param>
    /// <param name="data">The data to write.</param>
    /// <returns>True if the write was buffered, false if it should be written immediately.</returns>
    public async Task<bool> BufferWriteAsync(ulong fileHandle, string path, long offset, byte[] data)
    {
        if (!_config.WritebackCache)
            return false;

        await _writeLock.WaitAsync();
        try
        {
            // Check if we have too much buffered data
            if (CurrentWriteBufferSize + data.Length > MaxWriteBufferSize)
            {
                return false; // Write immediately
            }

            if (_writeBuffers.TryGetValue(fileHandle, out var entry))
            {
                // Coalesce with existing buffer
                if (offset == entry.Offset + entry.Data.Length)
                {
                    // Append
                    var newData = new byte[entry.Data.Length + data.Length];
                    Buffer.BlockCopy(entry.Data, 0, newData, 0, entry.Data.Length);
                    Buffer.BlockCopy(data, 0, newData, entry.Data.Length, data.Length);
                    entry.Data = newData;
                    entry.LastWrite = DateTime.UtcNow;
                }
                else
                {
                    // Non-contiguous write, flush existing buffer first
                    return false;
                }
            }
            else
            {
                _writeBuffers[fileHandle] = new WriteBufferEntry
                {
                    Path = path,
                    Offset = offset,
                    Data = data,
                    LastWrite = DateTime.UtcNow
                };
            }

            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Flushes buffered writes for a file handle.
    /// </summary>
    /// <param name="fileHandle">The file handle.</param>
    /// <returns>The buffered writes to flush, or null if none.</returns>
    public async Task<WriteBufferEntry?> FlushWriteBufferAsync(ulong fileHandle)
    {
        await _writeLock.WaitAsync();
        try
        {
            if (_writeBuffers.TryRemove(fileHandle, out var entry))
            {
                return entry;
            }

            return null;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Flushes all buffered writes.
    /// </summary>
    /// <returns>All buffered writes to flush.</returns>
    public async Task<IReadOnlyList<WriteBufferEntry>> FlushAllWriteBuffersAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            var entries = _writeBuffers.Values.ToList();
            _writeBuffers.Clear();
            return entries;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    #endregion

    #region Zero-Copy Support

    /// <summary>
    /// Checks if splice read is supported and enabled.
    /// </summary>
    /// <returns>True if splice read is available.</returns>
    public bool IsSpliceReadSupported()
    {
        return _config.SpliceRead && RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    }

    /// <summary>
    /// Checks if splice write is supported and enabled.
    /// </summary>
    /// <returns>True if splice write is available.</returns>
    public bool IsSpliceWriteSupported()
    {
        return _config.SpliceWrite && RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    }

    /// <summary>
    /// Checks if splice move is supported and enabled.
    /// </summary>
    /// <returns>True if splice move is available.</returns>
    public bool IsSpliceMoveSupported()
    {
        return _config.SpliceMove && RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    }

    /// <summary>
    /// Prepares a read operation for zero-copy transfer.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="offset">The read offset.</param>
    /// <param name="size">The read size.</param>
    /// <returns>A splice descriptor if zero-copy is possible, null otherwise.</returns>
    public SpliceDescriptor? PrepareZeroCopyRead(string path, long offset, long size)
    {
        if (!IsSpliceReadSupported())
            return null;

        return new SpliceDescriptor
        {
            Path = path,
            Offset = offset,
            Size = size,
            Direction = SpliceDirection.Read
        };
    }

    /// <summary>
    /// Prepares a write operation for zero-copy transfer.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="offset">The write offset.</param>
    /// <param name="size">The write size.</param>
    /// <returns>A splice descriptor if zero-copy is possible, null otherwise.</returns>
    public SpliceDescriptor? PrepareZeroCopyWrite(string path, long offset, long size)
    {
        if (!IsSpliceWriteSupported())
            return null;

        return new SpliceDescriptor
        {
            Path = path,
            Offset = offset,
            Size = size,
            Direction = SpliceDirection.Write
        };
    }

    #endregion

    #region Statistics

    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    /// <returns>The cache statistics.</returns>
    public CacheStatistics GetStatistics()
    {
        lock (_statsLock)
        {
            var hits = Interlocked.Read(ref _cacheHits);
            var misses = Interlocked.Read(ref _cacheMisses);
            var total = hits + misses;

            return new CacheStatistics
            {
                CacheHits = hits,
                CacheMisses = misses,
                HitRate = total > 0 ? (double)hits / total : 0,
                BytesReadFromCache = Interlocked.Read(ref _bytesReadFromCache),
                BytesWrittenToCache = Interlocked.Read(ref _bytesWrittenToCache),
                AttributeCacheEntries = _attrCache.Count,
                ReadCacheEntries = _readCache.Count,
                ReadCacheSizeBytes = CurrentReadCacheSize,
                WriteBufferEntries = _writeBuffers.Count,
                WriteBufferSizeBytes = CurrentWriteBufferSize
            };
        }
    }

    /// <summary>
    /// Resets cache statistics.
    /// </summary>
    public void ResetStatistics()
    {
        lock (_statsLock)
        {
            Interlocked.Exchange(ref _cacheHits, 0);
            Interlocked.Exchange(ref _cacheMisses, 0);
            Interlocked.Exchange(ref _bytesReadFromCache, 0);
            Interlocked.Exchange(ref _bytesWrittenToCache, 0);
        }
    }

    #endregion

    #region Private Methods

    private static string GetReadCacheKey(string path, long offset, int length)
    {
        return $"{path}|{offset}|{length}";
    }

    private void CleanupExpiredEntries(object? state)
    {
        if (_disposed)
            return;

        var now = DateTime.UtcNow;

        // Clean up expired attribute cache entries
        var expiredAttr = _attrCache
            .Where(kvp => now - kvp.Value.CachedAt >= AttributeCacheTtl)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredAttr)
        {
            _attrCache.TryRemove(key, out _);
        }

        // Clean up old write buffers (flush if older than 5 seconds)
        var oldWriteBuffers = _writeBuffers
            .Where(kvp => now - kvp.Value.LastWrite >= TimeSpan.FromSeconds(5))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in oldWriteBuffers)
        {
            if (_writeBuffers.TryRemove(key, out var entry))
            {
                _kernelContext?.LogWarning(
                    $"Flushing stale write buffer for {entry.Path} ({entry.Data.Length} bytes)");
            }
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the cache manager.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cleanupTimer.Dispose();
        _writeLock.Dispose();

        _attrCache.Clear();
        _readCache.Clear();
        _writeBuffers.Clear();
    }

    #endregion

    private struct CacheEntry
    {
        public FuseStat Stat;
        public DateTime CachedAt;
    }
}

/// <summary>
/// Buffered write entry.
/// </summary>
public sealed class WriteBufferEntry
{
    /// <summary>
    /// Gets or sets the file path.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the write offset.
    /// </summary>
    public long Offset { get; set; }

    /// <summary>
    /// Gets or sets the buffered data.
    /// </summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Gets or sets the time of the last write.
    /// </summary>
    public DateTime LastWrite { get; set; }
}

/// <summary>
/// Cache statistics.
/// </summary>
public sealed class CacheStatistics
{
    /// <summary>
    /// Gets or sets the number of cache hits.
    /// </summary>
    public long CacheHits { get; set; }

    /// <summary>
    /// Gets or sets the number of cache misses.
    /// </summary>
    public long CacheMisses { get; set; }

    /// <summary>
    /// Gets or sets the cache hit rate (0.0 - 1.0).
    /// </summary>
    public double HitRate { get; set; }

    /// <summary>
    /// Gets or sets the total bytes read from cache.
    /// </summary>
    public long BytesReadFromCache { get; set; }

    /// <summary>
    /// Gets or sets the total bytes written to cache.
    /// </summary>
    public long BytesWrittenToCache { get; set; }

    /// <summary>
    /// Gets or sets the number of attribute cache entries.
    /// </summary>
    public int AttributeCacheEntries { get; set; }

    /// <summary>
    /// Gets or sets the number of read cache entries.
    /// </summary>
    public int ReadCacheEntries { get; set; }

    /// <summary>
    /// Gets or sets the read cache size in bytes.
    /// </summary>
    public long ReadCacheSizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the number of write buffer entries.
    /// </summary>
    public int WriteBufferEntries { get; set; }

    /// <summary>
    /// Gets or sets the write buffer size in bytes.
    /// </summary>
    public long WriteBufferSizeBytes { get; set; }
}

/// <summary>
/// Splice (zero-copy) transfer descriptor.
/// </summary>
public sealed class SpliceDescriptor
{
    /// <summary>
    /// Gets or sets the file path.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file offset.
    /// </summary>
    public long Offset { get; set; }

    /// <summary>
    /// Gets or sets the transfer size.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the splice direction.
    /// </summary>
    public SpliceDirection Direction { get; set; }

    /// <summary>
    /// Gets or sets the pipe file descriptors for splice.
    /// </summary>
    public int[] PipeFds { get; set; } = Array.Empty<int>();
}

/// <summary>
/// Splice transfer direction.
/// </summary>
public enum SpliceDirection
{
    /// <summary>
    /// Read from file to pipe/socket.
    /// </summary>
    Read,

    /// <summary>
    /// Write from pipe/socket to file.
    /// </summary>
    Write
}
