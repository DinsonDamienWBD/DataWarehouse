using System.Collections.Concurrent;
using System.Diagnostics;

namespace DataWarehouse.Plugins.WinFspDriver;

/// <summary>
/// Manages read and write caching for the WinFSP filesystem driver.
/// Provides adaptive prefetch, write buffering, and cache invalidation.
/// </summary>
public sealed class WinFspCacheManager : IDisposable
{
    private readonly CacheConfig _readConfig;
    private readonly CacheConfig _writeConfig;
    private readonly ConcurrentDictionary<string, CacheEntry> _readCache;
    private readonly ConcurrentDictionary<string, WriteBuffer> _writeBuffers;
    private readonly ConcurrentDictionary<string, AccessPattern> _accessPatterns;
    private readonly SemaphoreSlim _flushLock;
    private readonly Timer _flushTimer;
    private readonly Timer _evictionTimer;
    private readonly CancellationTokenSource _cts;
    private long _currentReadCacheSize;
    private long _currentWriteBufferSize;
    private long _cacheHits;
    private long _cacheMisses;
    private bool _disposed;

    /// <summary>
    /// Initializes a new cache manager instance.
    /// </summary>
    /// <param name="readConfig">Read cache configuration.</param>
    /// <param name="writeConfig">Write cache configuration.</param>
    public WinFspCacheManager(CacheConfig readConfig, CacheConfig writeConfig)
    {
        _readConfig = readConfig ?? throw new ArgumentNullException(nameof(readConfig));
        _writeConfig = writeConfig ?? throw new ArgumentNullException(nameof(writeConfig));
        _readCache = new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        _writeBuffers = new ConcurrentDictionary<string, WriteBuffer>(StringComparer.OrdinalIgnoreCase);
        _accessPatterns = new ConcurrentDictionary<string, AccessPattern>(StringComparer.OrdinalIgnoreCase);
        _flushLock = new SemaphoreSlim(1, 1);
        _cts = new CancellationTokenSource();

        // Start background flush timer for write buffers
        if (_writeConfig.Enabled && _writeConfig.FlushIntervalMs > 0)
        {
            _flushTimer = new Timer(
                async _ => await FlushAllWriteBuffersAsync(),
                null,
                _writeConfig.FlushIntervalMs,
                _writeConfig.FlushIntervalMs);
        }
        else
        {
            _flushTimer = new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
        }

        // Start background eviction timer
        _evictionTimer = new Timer(
            _ => EvictExpiredEntries(),
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));
    }

    #region Read Cache Operations

    /// <summary>
    /// Tries to get data from the read cache.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="offset">Read offset.</param>
    /// <param name="length">Requested length.</param>
    /// <param name="data">Output data if found.</param>
    /// <returns>True if cache hit.</returns>
    public bool TryGetReadCache(string path, long offset, int length, out byte[]? data)
    {
        data = null;

        if (!_readConfig.Enabled)
        {
            Interlocked.Increment(ref _cacheMisses);
            return false;
        }

        var key = GetReadCacheKey(path, offset, length);

        if (_readCache.TryGetValue(key, out var entry) && !entry.IsExpired)
        {
            entry.LastAccess = DateTime.UtcNow;
            entry.HitCount++;
            data = entry.Data;
            Interlocked.Increment(ref _cacheHits);
            UpdateAccessPattern(path, offset, length, true);
            return true;
        }

        Interlocked.Increment(ref _cacheMisses);
        UpdateAccessPattern(path, offset, length, false);
        return false;
    }

    /// <summary>
    /// Adds data to the read cache.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="offset">Read offset.</param>
    /// <param name="data">Data to cache.</param>
    public void AddToReadCache(string path, long offset, byte[] data)
    {
        if (!_readConfig.Enabled || data == null || data.Length == 0)
            return;

        // Check if we need to evict entries to make room
        while (Interlocked.Read(ref _currentReadCacheSize) + data.Length > _readConfig.MaxSizeBytes)
        {
            if (!EvictOldestReadCacheEntry())
                break;
        }

        var key = GetReadCacheKey(path, offset, data.Length);
        var entry = new CacheEntry
        {
            Path = path,
            Offset = offset,
            Data = data,
            CreatedAt = DateTime.UtcNow,
            LastAccess = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddSeconds(_readConfig.TtlSeconds)
        };

        if (_readCache.TryAdd(key, entry))
        {
            Interlocked.Add(ref _currentReadCacheSize, data.Length);
        }
    }

    /// <summary>
    /// Invalidates all cache entries for a path.
    /// </summary>
    /// <param name="path">The file path to invalidate.</param>
    public void InvalidateReadCache(string path)
    {
        var keysToRemove = _readCache.Keys
            .Where(k => k.StartsWith(path, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in keysToRemove)
        {
            if (_readCache.TryRemove(key, out var entry))
            {
                Interlocked.Add(ref _currentReadCacheSize, -entry.Data.Length);
            }
        }
    }

    /// <summary>
    /// Clears the entire read cache.
    /// </summary>
    public void ClearReadCache()
    {
        _readCache.Clear();
        Interlocked.Exchange(ref _currentReadCacheSize, 0);
    }

    #endregion

    #region Write Buffer Operations

    /// <summary>
    /// Buffers data for writing.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="offset">Write offset.</param>
    /// <param name="data">Data to write.</param>
    /// <returns>True if buffered, false if immediate write needed.</returns>
    public bool TryBufferWrite(string path, long offset, byte[] data)
    {
        if (!_writeConfig.Enabled || _writeConfig.WriteThrough)
            return false;

        var buffer = _writeBuffers.GetOrAdd(path, _ => new WriteBuffer(path, _writeConfig.WriteBufferSize));

        lock (buffer.Lock)
        {
            // Check if buffer is getting too large
            if (buffer.TotalSize + data.Length > _writeConfig.WriteBufferSize)
            {
                return false; // Trigger immediate flush and write
            }

            buffer.AddChunk(offset, data);
            Interlocked.Add(ref _currentWriteBufferSize, data.Length);
            return true;
        }
    }

    /// <summary>
    /// Flushes the write buffer for a specific path.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>List of chunks to write.</returns>
    public List<WriteChunk> FlushWriteBuffer(string path)
    {
        if (!_writeBuffers.TryRemove(path, out var buffer))
            return new List<WriteChunk>();

        lock (buffer.Lock)
        {
            var chunks = buffer.GetMergedChunks();
            Interlocked.Add(ref _currentWriteBufferSize, -buffer.TotalSize);
            return chunks;
        }
    }

    /// <summary>
    /// Flushes all write buffers asynchronously.
    /// </summary>
    public async Task FlushAllWriteBuffersAsync()
    {
        if (!await _flushLock.WaitAsync(0))
            return;

        try
        {
            var paths = _writeBuffers.Keys.ToList();
            foreach (var path in paths)
            {
                if (_cts.Token.IsCancellationRequested)
                    break;

                var chunks = FlushWriteBuffer(path);
                if (chunks.Count > 0)
                {
                    // Notify filesystem to perform actual write
                    OnWriteBufferFlushed?.Invoke(this, new WriteBufferFlushedEventArgs(path, chunks));
                }
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    /// <summary>
    /// Event raised when a write buffer is flushed.
    /// </summary>
    public event EventHandler<WriteBufferFlushedEventArgs>? OnWriteBufferFlushed;

    #endregion

    #region Prefetch Operations

    /// <summary>
    /// Gets the recommended prefetch size for a path based on access patterns.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="currentOffset">Current read offset.</param>
    /// <returns>Recommended prefetch size in bytes.</returns>
    public int GetPrefetchSize(string path, long currentOffset)
    {
        if (!_readConfig.AdaptivePrefetch)
            return _readConfig.PrefetchSize;

        if (!_accessPatterns.TryGetValue(path, out var pattern))
            return _readConfig.PrefetchSize;

        // Analyze access pattern
        if (pattern.IsSequential)
        {
            // Sequential access: increase prefetch
            return Math.Min(_readConfig.PrefetchSize * 2, 4 * 1024 * 1024);
        }
        else if (pattern.IsRandom)
        {
            // Random access: decrease prefetch
            return Math.Max(_readConfig.PrefetchSize / 4, 64 * 1024);
        }

        return _readConfig.PrefetchSize;
    }

    /// <summary>
    /// Gets the predicted next read offset for prefetching.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="currentOffset">Current read offset.</param>
    /// <param name="currentLength">Current read length.</param>
    /// <returns>Predicted next offset, or -1 if unpredictable.</returns>
    public long GetPredictedNextOffset(string path, long currentOffset, int currentLength)
    {
        if (!_readConfig.AdaptivePrefetch)
            return currentOffset + currentLength;

        if (!_accessPatterns.TryGetValue(path, out var pattern))
            return currentOffset + currentLength;

        if (pattern.IsSequential)
        {
            return currentOffset + currentLength;
        }

        if (pattern.LastOffsets.Count >= 2)
        {
            var offsets = pattern.LastOffsets.TakeLast(2).ToList();
            var delta = offsets[1] - offsets[0];
            if (Math.Abs(delta) < 1024 * 1024) // Reasonable delta
            {
                return currentOffset + delta;
            }
        }

        return -1; // Unpredictable
    }

    #endregion

    #region Statistics

    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    public CacheStatistics GetStatistics()
    {
        var hits = Interlocked.Read(ref _cacheHits);
        var misses = Interlocked.Read(ref _cacheMisses);
        var total = hits + misses;

        return new CacheStatistics
        {
            ReadCacheEntries = _readCache.Count,
            ReadCacheSize = Interlocked.Read(ref _currentReadCacheSize),
            ReadCacheHits = hits,
            ReadCacheMisses = misses,
            ReadCacheHitRate = total > 0 ? (double)hits / total : 0,
            WriteBufferCount = _writeBuffers.Count,
            WriteBufferSize = Interlocked.Read(ref _currentWriteBufferSize),
            TrackedFiles = _accessPatterns.Count
        };
    }

    /// <summary>
    /// Resets cache statistics.
    /// </summary>
    public void ResetStatistics()
    {
        Interlocked.Exchange(ref _cacheHits, 0);
        Interlocked.Exchange(ref _cacheMisses, 0);
    }

    #endregion

    #region Private Methods

    private static string GetReadCacheKey(string path, long offset, int length)
    {
        return $"{path}|{offset}|{length}";
    }

    private void UpdateAccessPattern(string path, long offset, int length, bool hit)
    {
        var pattern = _accessPatterns.GetOrAdd(path, _ => new AccessPattern());

        lock (pattern)
        {
            pattern.LastOffsets.Add(offset);
            if (pattern.LastOffsets.Count > 10)
            {
                pattern.LastOffsets.RemoveAt(0);
            }

            pattern.TotalReads++;
            if (hit) pattern.CacheHits++;

            // Analyze pattern
            if (pattern.LastOffsets.Count >= 3)
            {
                var sorted = pattern.LastOffsets.OrderBy(o => o).ToList();
                var sequential = true;
                for (int i = 1; i < sorted.Count; i++)
                {
                    if (sorted[i] - sorted[i - 1] > 1024 * 1024) // 1MB gap
                    {
                        sequential = false;
                        break;
                    }
                }
                pattern.IsSequential = sequential;
                pattern.IsRandom = !sequential && pattern.LastOffsets.Distinct().Count() == pattern.LastOffsets.Count;
            }
        }
    }

    private bool EvictOldestReadCacheEntry()
    {
        var oldest = _readCache
            .OrderBy(kv => kv.Value.LastAccess)
            .ThenBy(kv => kv.Value.HitCount)
            .FirstOrDefault();

        if (oldest.Key != null && _readCache.TryRemove(oldest.Key, out var entry))
        {
            Interlocked.Add(ref _currentReadCacheSize, -entry.Data.Length);
            return true;
        }

        return false;
    }

    private void EvictExpiredEntries()
    {
        var now = DateTime.UtcNow;
        var expiredKeys = _readCache
            .Where(kv => kv.Value.ExpiresAt < now)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            if (_readCache.TryRemove(key, out var entry))
            {
                Interlocked.Add(ref _currentReadCacheSize, -entry.Data.Length);
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

        _cts.Cancel();
        _flushTimer.Dispose();
        _evictionTimer.Dispose();

        // Flush remaining write buffers synchronously
        try
        {
            FlushAllWriteBuffersAsync().Wait(TimeSpan.FromSeconds(10));
        }
        catch { }

        _flushLock.Dispose();
        _cts.Dispose();
        _readCache.Clear();
        _writeBuffers.Clear();
        _accessPatterns.Clear();

        _disposed = true;
    }

    #endregion
}

#region Helper Classes

/// <summary>
/// A cached data entry.
/// </summary>
internal sealed class CacheEntry
{
    public required string Path { get; init; }
    public required long Offset { get; init; }
    public required byte[] Data { get; init; }
    public required DateTime CreatedAt { get; init; }
    public DateTime LastAccess { get; set; }
    public DateTime ExpiresAt { get; init; }
    public int HitCount { get; set; }

    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
}

/// <summary>
/// Write buffer for a file.
/// </summary>
internal sealed class WriteBuffer
{
    public string Path { get; }
    public object Lock { get; } = new();
    public int MaxSize { get; }
    public long TotalSize { get; private set; }
    private readonly List<WriteChunk> _chunks = new();

    public WriteBuffer(string path, int maxSize)
    {
        Path = path;
        MaxSize = maxSize;
    }

    public void AddChunk(long offset, byte[] data)
    {
        _chunks.Add(new WriteChunk { Offset = offset, Data = data });
        TotalSize += data.Length;
    }

    public List<WriteChunk> GetMergedChunks()
    {
        if (_chunks.Count == 0)
            return new List<WriteChunk>();

        // Sort and merge adjacent chunks
        var sorted = _chunks.OrderBy(c => c.Offset).ToList();
        var merged = new List<WriteChunk>();
        var current = sorted[0];

        for (int i = 1; i < sorted.Count; i++)
        {
            var next = sorted[i];
            if (current.Offset + current.Data.Length == next.Offset)
            {
                // Merge adjacent chunks
                var newData = new byte[current.Data.Length + next.Data.Length];
                Buffer.BlockCopy(current.Data, 0, newData, 0, current.Data.Length);
                Buffer.BlockCopy(next.Data, 0, newData, current.Data.Length, next.Data.Length);
                current = new WriteChunk { Offset = current.Offset, Data = newData };
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }
        merged.Add(current);

        return merged;
    }
}

/// <summary>
/// A chunk of data to write.
/// </summary>
public sealed class WriteChunk
{
    /// <summary>
    /// Write offset.
    /// </summary>
    public required long Offset { get; init; }

    /// <summary>
    /// Data to write.
    /// </summary>
    public required byte[] Data { get; init; }
}

/// <summary>
/// Access pattern tracking for a file.
/// </summary>
internal sealed class AccessPattern
{
    public List<long> LastOffsets { get; } = new();
    public int TotalReads { get; set; }
    public int CacheHits { get; set; }
    public bool IsSequential { get; set; }
    public bool IsRandom { get; set; }
}

/// <summary>
/// Event arguments for write buffer flush.
/// </summary>
public sealed class WriteBufferFlushedEventArgs : EventArgs
{
    /// <summary>
    /// The file path.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Chunks to write.
    /// </summary>
    public IReadOnlyList<WriteChunk> Chunks { get; }

    public WriteBufferFlushedEventArgs(string path, IReadOnlyList<WriteChunk> chunks)
    {
        Path = path;
        Chunks = chunks;
    }
}

/// <summary>
/// Cache statistics.
/// </summary>
public sealed class CacheStatistics
{
    /// <summary>
    /// Number of entries in the read cache.
    /// </summary>
    public int ReadCacheEntries { get; init; }

    /// <summary>
    /// Total size of read cache in bytes.
    /// </summary>
    public long ReadCacheSize { get; init; }

    /// <summary>
    /// Number of read cache hits.
    /// </summary>
    public long ReadCacheHits { get; init; }

    /// <summary>
    /// Number of read cache misses.
    /// </summary>
    public long ReadCacheMisses { get; init; }

    /// <summary>
    /// Read cache hit rate (0-1).
    /// </summary>
    public double ReadCacheHitRate { get; init; }

    /// <summary>
    /// Number of files with pending write buffers.
    /// </summary>
    public int WriteBufferCount { get; init; }

    /// <summary>
    /// Total size of write buffers in bytes.
    /// </summary>
    public long WriteBufferSize { get; init; }

    /// <summary>
    /// Number of files with tracked access patterns.
    /// </summary>
    public int TrackedFiles { get; init; }
}

#endregion
