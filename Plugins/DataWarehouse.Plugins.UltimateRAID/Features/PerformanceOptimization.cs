// 91.G: Performance Optimization - Write Coalescing, Prefetch, Caching, I/O Scheduling, QoS
using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateRAID.Features;

/// <summary>
/// 91.G: Performance Optimization features for RAID arrays.
/// Implements write coalescing, read-ahead prefetch, write-back caching,
/// I/O scheduling, and QoS enforcement.
/// </summary>
public sealed class RaidPerformanceOptimizer
{
    private readonly WriteCoalescer _writeCoalescer;
    private readonly ReadAheadPrefetcher _prefetcher;
    private readonly WriteBackCache _writeCache;
    private readonly IoScheduler _scheduler;
    private readonly QosEnforcer _qosEnforcer;

    public RaidPerformanceOptimizer(PerformanceConfig? config = null)
    {
        config ??= new PerformanceConfig();

        _writeCoalescer = new WriteCoalescer(config.WriteCoalescingConfig);
        _prefetcher = new ReadAheadPrefetcher(config.PrefetchConfig);
        _writeCache = new WriteBackCache(config.WriteCacheConfig);
        _scheduler = new IoScheduler(config.SchedulerConfig);
        _qosEnforcer = new QosEnforcer(config.QosConfig);
    }

    /// <summary>
    /// 91.G3: Submit write for coalescing.
    /// </summary>
    public async Task<CoalescedWriteResult> SubmitWriteAsync(
        string arrayId,
        long offset,
        byte[] data,
        WritePriority priority = WritePriority.Normal,
        CancellationToken cancellationToken = default)
    {
        // Apply QoS limits
        await _qosEnforcer.ThrottleIfNeededAsync(arrayId, IoType.Write, data.Length, cancellationToken);

        // Add to write coalescer
        var coalesceResult = _writeCoalescer.AddWrite(arrayId, offset, data, priority);

        // If coalesced batch is ready, schedule for execution
        if (coalesceResult.BatchReady)
        {
            var ioRequest = new IoRequest
            {
                ArrayId = arrayId,
                Type = IoType.Write,
                Offset = coalesceResult.BatchStartOffset,
                Data = coalesceResult.CoalescedData,
                Priority = priority
            };

            await _scheduler.ScheduleAsync(ioRequest, cancellationToken);
        }

        return coalesceResult;
    }

    /// <summary>
    /// 91.G4: Read with prefetching.
    /// </summary>
    public async Task<PrefetchedReadResult> ReadWithPrefetchAsync(
        string arrayId,
        long offset,
        int length,
        string workloadHint = "sequential",
        CancellationToken cancellationToken = default)
    {
        // Apply QoS limits
        await _qosEnforcer.ThrottleIfNeededAsync(arrayId, IoType.Read, length, cancellationToken);

        // Check prefetch cache first
        var cachedData = _prefetcher.GetCached(arrayId, offset, length);
        if (cachedData != null)
        {
            // Trigger background prefetch for next blocks
            _ = _prefetcher.PrefetchAheadAsync(arrayId, offset + length, workloadHint, cancellationToken);

            return new PrefetchedReadResult
            {
                Data = cachedData,
                WasCached = true,
                BytesRead = cachedData.Length
            };
        }

        // Schedule read
        var ioRequest = new IoRequest
        {
            ArrayId = arrayId,
            Type = IoType.Read,
            Offset = offset,
            Length = length,
            Priority = WritePriority.Normal
        };

        var result = await _scheduler.ExecuteReadAsync(ioRequest, cancellationToken);

        // Trigger prefetch
        _ = _prefetcher.PrefetchAheadAsync(arrayId, offset + length, workloadHint, cancellationToken);

        return new PrefetchedReadResult
        {
            Data = result.Data,
            WasCached = false,
            BytesRead = result.Data?.Length ?? 0
        };
    }

    /// <summary>
    /// 91.G5: Write through write-back cache.
    /// </summary>
    public async Task<CacheWriteResult> WriteWithCacheAsync(
        string arrayId,
        long offset,
        byte[] data,
        CacheWriteMode mode = CacheWriteMode.WriteBack,
        CancellationToken cancellationToken = default)
    {
        var result = new CacheWriteResult { ArrayId = arrayId, Offset = offset, Length = data.Length };

        switch (mode)
        {
            case CacheWriteMode.WriteBack:
                // Write to cache, return immediately
                var cached = _writeCache.CacheWrite(arrayId, offset, data);
                result.CacheHit = true;
                result.Persisted = false;
                result.Latency = TimeSpan.FromMicroseconds(50); // Cache latency
                break;

            case CacheWriteMode.WriteThrough:
                // Write to cache and disk
                _writeCache.CacheWrite(arrayId, offset, data);
                await FlushToDiskAsync(arrayId, offset, data, cancellationToken);
                result.CacheHit = true;
                result.Persisted = true;
                result.Latency = TimeSpan.FromMilliseconds(5); // Disk latency
                break;

            case CacheWriteMode.WriteAround:
                // Bypass cache, write directly to disk
                await FlushToDiskAsync(arrayId, offset, data, cancellationToken);
                result.CacheHit = false;
                result.Persisted = true;
                result.Latency = TimeSpan.FromMilliseconds(5);
                break;
        }

        return result;
    }

    /// <summary>
    /// 91.G5: Flush write-back cache.
    /// </summary>
    public async Task<CacheFlushResult> FlushCacheAsync(
        string? arrayId = null,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var entries = _writeCache.GetDirtyEntries(arrayId);
        var result = new CacheFlushResult { StartTime = DateTime.UtcNow };

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await FlushToDiskAsync(entry.ArrayId, entry.Offset, entry.Data, cancellationToken);
                _writeCache.MarkClean(entry.ArrayId, entry.Offset);
                result.EntriesFlushed++;
                result.BytesFlushed += entry.Data.Length;
            }
            catch (Exception ex)
            {
                result.FlushErrors.Add($"Flush failed for {entry.ArrayId}:{entry.Offset}: {ex.Message}");
            }
        }

        result.EndTime = DateTime.UtcNow;
        result.Duration = result.EndTime - result.StartTime;

        return result;
    }

    /// <summary>
    /// 91.G6: Schedule I/O with priority.
    /// </summary>
    public async Task<ScheduleResult> ScheduleIoAsync(
        IoRequest request,
        CancellationToken cancellationToken = default)
    {
        return await _scheduler.ScheduleAsync(request, cancellationToken);
    }

    /// <summary>
    /// 91.G7: Configure QoS for workload.
    /// </summary>
    public void ConfigureQos(
        string workloadId,
        QosPolicy policy)
    {
        _qosEnforcer.SetPolicy(workloadId, policy);
    }

    /// <summary>
    /// 91.G7: Get QoS statistics.
    /// </summary>
    public QosStatistics GetQosStatistics(string workloadId)
    {
        return _qosEnforcer.GetStatistics(workloadId);
    }

    /// <summary>
    /// Gets performance statistics.
    /// </summary>
    public PerformanceStatistics GetStatistics()
    {
        return new PerformanceStatistics
        {
            WriteCoalescing = _writeCoalescer.GetStatistics(),
            Prefetch = _prefetcher.GetStatistics(),
            WriteCache = _writeCache.GetStatistics(),
            Scheduler = _scheduler.GetStatistics()
        };
    }

    private Task FlushToDiskAsync(string arrayId, long offset, byte[] data, CancellationToken ct)
    {
        // Simulated disk write
        return Task.Delay(1, ct);
    }
}

/// <summary>
/// 91.G3: Write Coalescing - Batches small writes for efficiency.
/// </summary>
public sealed class WriteCoalescer
{
    private readonly ConcurrentDictionary<string, CoalesceBatch> _batches = new();
    private readonly WriteCoalescingConfig _config;
    private long _totalWrites;
    private long _coalescedWrites;

    public WriteCoalescer(WriteCoalescingConfig? config = null)
    {
        _config = config ?? new WriteCoalescingConfig();
    }

    public CoalescedWriteResult AddWrite(string arrayId, long offset, byte[] data, WritePriority priority)
    {
        Interlocked.Increment(ref _totalWrites);

        var batchKey = $"{arrayId}:{offset / _config.BatchAlignmentBytes}";
        var batch = _batches.GetOrAdd(batchKey, _ => new CoalesceBatch
        {
            ArrayId = arrayId,
            StartOffset = (offset / _config.BatchAlignmentBytes) * _config.BatchAlignmentBytes,
            CreatedTime = DateTime.UtcNow
        });

        lock (batch)
        {
            batch.Writes.Add(new PendingWrite { Offset = offset, Data = data, Priority = priority });
            batch.TotalBytes += data.Length;

            var batchReady = batch.TotalBytes >= _config.MaxBatchSizeBytes ||
                             batch.Writes.Count >= _config.MaxWritesPerBatch ||
                             (DateTime.UtcNow - batch.CreatedTime) >= _config.MaxBatchDelay;

            if (batchReady)
            {
                var result = BuildCoalescedResult(batch);
                _batches.TryRemove(batchKey, out _);
                Interlocked.Add(ref _coalescedWrites, batch.Writes.Count);
                return result;
            }

            return new CoalescedWriteResult
            {
                BatchReady = false,
                WritesPending = batch.Writes.Count
            };
        }
    }

    public WriteCoalescingStatistics GetStatistics()
    {
        return new WriteCoalescingStatistics
        {
            TotalWrites = _totalWrites,
            CoalescedWrites = _coalescedWrites,
            CoalesceRatio = _totalWrites > 0 ? 1.0 - ((double)_batches.Count / _totalWrites) : 0,
            PendingBatches = _batches.Count
        };
    }

    private CoalescedWriteResult BuildCoalescedResult(CoalesceBatch batch)
    {
        // Sort writes by offset and merge overlapping
        var sortedWrites = batch.Writes.OrderBy(w => w.Offset).ToList();
        var coalescedData = new byte[batch.TotalBytes + _config.BatchAlignmentBytes];
        var position = 0;

        foreach (var write in sortedWrites)
        {
            var relativeOffset = (int)(write.Offset - batch.StartOffset);
            if (relativeOffset >= 0 && relativeOffset + write.Data.Length <= coalescedData.Length)
            {
                Array.Copy(write.Data, 0, coalescedData, relativeOffset, write.Data.Length);
                position = Math.Max(position, relativeOffset + write.Data.Length);
            }
        }

        return new CoalescedWriteResult
        {
            BatchReady = true,
            WritesCoalesced = batch.Writes.Count,
            BatchStartOffset = batch.StartOffset,
            CoalescedData = coalescedData.Take(position).ToArray()
        };
    }
}

/// <summary>
/// 91.G4: Read-Ahead Prefetcher.
/// </summary>
public sealed class ReadAheadPrefetcher
{
    private readonly ConcurrentDictionary<string, PrefetchCache> _caches = new();
    private readonly PrefetchConfig _config;
    private long _cacheHits;
    private long _cacheMisses;

    public ReadAheadPrefetcher(PrefetchConfig? config = null)
    {
        _config = config ?? new PrefetchConfig();
    }

    public byte[]? GetCached(string arrayId, long offset, int length)
    {
        var cache = _caches.GetOrAdd(arrayId, _ => new PrefetchCache());

        if (cache.TryGet(offset, length, out var data))
        {
            Interlocked.Increment(ref _cacheHits);
            return data;
        }

        Interlocked.Increment(ref _cacheMisses);
        return null;
    }

    public async Task PrefetchAheadAsync(
        string arrayId,
        long startOffset,
        string workloadHint,
        CancellationToken cancellationToken)
    {
        var prefetchSize = workloadHint switch
        {
            "sequential" => _config.SequentialPrefetchBytes,
            "random" => _config.RandomPrefetchBytes,
            _ => _config.DefaultPrefetchBytes
        };

        var cache = _caches.GetOrAdd(arrayId, _ => new PrefetchCache());

        // Prefetch in background
        for (int i = 0; i < _config.PrefetchDepth; i++)
        {
            var offset = startOffset + (i * prefetchSize);

            if (!cache.Contains(offset))
            {
                // Simulated read
                var data = new byte[prefetchSize];
                cache.Add(offset, data);
            }
        }

        await Task.CompletedTask;
    }

    public PrefetchStatistics GetStatistics()
    {
        return new PrefetchStatistics
        {
            CacheHits = _cacheHits,
            CacheMisses = _cacheMisses,
            HitRatio = (_cacheHits + _cacheMisses) > 0
                ? (double)_cacheHits / (_cacheHits + _cacheMisses)
                : 0,
            CachedArrays = _caches.Count
        };
    }
}

/// <summary>
/// 91.G5: Write-Back Cache.
/// </summary>
public sealed class WriteBackCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly WriteCacheConfig _config;
    private long _totalBytes;

    public WriteBackCache(WriteCacheConfig? config = null)
    {
        _config = config ?? new WriteCacheConfig();
    }

    public bool CacheWrite(string arrayId, long offset, byte[] data)
    {
        var key = $"{arrayId}:{offset}";

        // Check capacity
        if (_totalBytes + data.Length > _config.MaxCacheSizeBytes)
        {
            EvictOldest();
        }

        var entry = new CacheEntry
        {
            ArrayId = arrayId,
            Offset = offset,
            Data = data,
            IsDirty = true,
            CachedTime = DateTime.UtcNow,
            LastAccess = DateTime.UtcNow
        };

        _cache[key] = entry;
        Interlocked.Add(ref _totalBytes, data.Length);

        return true;
    }

    public IEnumerable<CacheEntry> GetDirtyEntries(string? arrayId = null)
    {
        var entries = _cache.Values.Where(e => e.IsDirty);
        if (arrayId != null)
            entries = entries.Where(e => e.ArrayId == arrayId);
        return entries.OrderBy(e => e.CachedTime);
    }

    public void MarkClean(string arrayId, long offset)
    {
        var key = $"{arrayId}:{offset}";
        if (_cache.TryGetValue(key, out var entry))
        {
            entry.IsDirty = false;
        }
    }

    public WriteCacheStatistics GetStatistics()
    {
        return new WriteCacheStatistics
        {
            TotalEntries = _cache.Count,
            DirtyEntries = _cache.Values.Count(e => e.IsDirty),
            TotalBytes = _totalBytes,
            MaxBytes = _config.MaxCacheSizeBytes,
            Utilization = _config.MaxCacheSizeBytes > 0
                ? (double)_totalBytes / _config.MaxCacheSizeBytes
                : 0
        };
    }

    private void EvictOldest()
    {
        var oldest = _cache.Values
            .Where(e => !e.IsDirty)
            .OrderBy(e => e.LastAccess)
            .FirstOrDefault();

        if (oldest != null)
        {
            var key = $"{oldest.ArrayId}:{oldest.Offset}";
            if (_cache.TryRemove(key, out var removed))
            {
                Interlocked.Add(ref _totalBytes, -removed.Data.Length);
            }
        }
    }
}

/// <summary>
/// 91.G6: I/O Scheduler with priority queues.
/// </summary>
public sealed class IoScheduler
{
    private readonly PriorityQueue<IoRequest, int> _queue = new();
    private readonly SchedulerConfig _config;
    private readonly object _queueLock = new();
    private long _totalRequests;
    private long _completedRequests;

    public IoScheduler(SchedulerConfig? config = null)
    {
        _config = config ?? new SchedulerConfig();
    }

    public async Task<ScheduleResult> ScheduleAsync(IoRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _totalRequests);

        var priority = request.Priority switch
        {
            WritePriority.High => 0,
            WritePriority.Normal => 1,
            WritePriority.Low => 2,
            WritePriority.Background => 3,
            _ => 1
        };

        lock (_queueLock)
        {
            _queue.Enqueue(request, priority);
        }

        // Process queue
        await ProcessQueueAsync(cancellationToken);

        Interlocked.Increment(ref _completedRequests);

        return new ScheduleResult
        {
            RequestId = request.RequestId,
            Scheduled = true,
            QueuePosition = 0
        };
    }

    public async Task<IoResult> ExecuteReadAsync(IoRequest request, CancellationToken cancellationToken)
    {
        // Simulated read execution
        await Task.Delay(1, cancellationToken);

        return new IoResult
        {
            Data = new byte[request.Length],
            BytesTransferred = request.Length,
            Latency = TimeSpan.FromMilliseconds(1)
        };
    }

    public SchedulerStatistics GetStatistics()
    {
        return new SchedulerStatistics
        {
            TotalRequests = _totalRequests,
            CompletedRequests = _completedRequests,
            PendingRequests = _queue.Count
        };
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        IoRequest? request;
        lock (_queueLock)
        {
            _queue.TryDequeue(out request, out _);
        }

        if (request != null)
        {
            // Execute based on type
            if (request.Type == IoType.Write && request.Data != null)
            {
                // Simulated write
                await Task.Delay(1, cancellationToken);
            }
        }
    }
}

/// <summary>
/// 91.G7: QoS Enforcer.
/// </summary>
public sealed class QosEnforcer
{
    private readonly ConcurrentDictionary<string, QosPolicy> _policies = new();
    private readonly ConcurrentDictionary<string, QosStatistics> _statistics = new();
    private readonly QosConfig _config;

    public QosEnforcer(QosConfig? config = null)
    {
        _config = config ?? new QosConfig();
    }

    public void SetPolicy(string workloadId, QosPolicy policy)
    {
        _policies[workloadId] = policy;
        _statistics.GetOrAdd(workloadId, _ => new QosStatistics { WorkloadId = workloadId });
    }

    public async Task ThrottleIfNeededAsync(
        string workloadId,
        IoType type,
        long bytes,
        CancellationToken cancellationToken)
    {
        if (!_policies.TryGetValue(workloadId, out var policy))
            return;

        var stats = _statistics.GetOrAdd(workloadId, _ => new QosStatistics { WorkloadId = workloadId });

        // Check IOPS limit
        if (policy.MaxIops.HasValue)
        {
            var currentIops = GetCurrentIops(stats);
            if (currentIops >= policy.MaxIops.Value)
            {
                stats.ThrottledOperations++;
                await Task.Delay(10, cancellationToken); // Basic throttle
            }
        }

        // Check bandwidth limit
        if (policy.MaxBandwidthMbps.HasValue)
        {
            var currentBandwidth = GetCurrentBandwidth(stats);
            if (currentBandwidth >= policy.MaxBandwidthMbps.Value * 1_000_000)
            {
                stats.ThrottledBytes += bytes;
                await Task.Delay(10, cancellationToken);
            }
        }

        // Update statistics
        if (type == IoType.Read)
        {
            stats.TotalReadOps++;
            stats.TotalReadBytes += bytes;
        }
        else
        {
            stats.TotalWriteOps++;
            stats.TotalWriteBytes += bytes;
        }
    }

    public QosStatistics GetStatistics(string workloadId)
    {
        return _statistics.GetOrAdd(workloadId, _ => new QosStatistics { WorkloadId = workloadId });
    }

    private long GetCurrentIops(QosStatistics stats)
    {
        return stats.TotalReadOps + stats.TotalWriteOps;
    }

    private long GetCurrentBandwidth(QosStatistics stats)
    {
        return stats.TotalReadBytes + stats.TotalWriteBytes;
    }
}

// Configuration classes
public sealed class PerformanceConfig
{
    public WriteCoalescingConfig WriteCoalescingConfig { get; set; } = new();
    public PrefetchConfig PrefetchConfig { get; set; } = new();
    public WriteCacheConfig WriteCacheConfig { get; set; } = new();
    public SchedulerConfig SchedulerConfig { get; set; } = new();
    public QosConfig QosConfig { get; set; } = new();
}

public sealed class WriteCoalescingConfig
{
    public int MaxBatchSizeBytes { get; set; } = 1024 * 1024; // 1MB
    public int MaxWritesPerBatch { get; set; } = 100;
    public TimeSpan MaxBatchDelay { get; set; } = TimeSpan.FromMilliseconds(10);
    public int BatchAlignmentBytes { get; set; } = 4096;
}

public sealed class PrefetchConfig
{
    public int SequentialPrefetchBytes { get; set; } = 1024 * 1024; // 1MB
    public int RandomPrefetchBytes { get; set; } = 64 * 1024; // 64KB
    public int DefaultPrefetchBytes { get; set; } = 256 * 1024; // 256KB
    public int PrefetchDepth { get; set; } = 4;
}

public sealed class WriteCacheConfig
{
    public long MaxCacheSizeBytes { get; set; } = 256 * 1024 * 1024; // 256MB
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);
    public bool BatteryBackedRequired { get; set; } = true;
}

public sealed class SchedulerConfig
{
    public int MaxConcurrentIo { get; set; } = 64;
    public SchedulingAlgorithm Algorithm { get; set; } = SchedulingAlgorithm.Deadline;
}

public sealed class QosConfig
{
    public bool Enabled { get; set; } = true;
    public int DefaultMaxIops { get; set; } = 10000;
    public int DefaultMaxBandwidthMbps { get; set; } = 1000;
}

// Enums
public enum WritePriority { High, Normal, Low, Background }
public enum IoType { Read, Write }
public enum CacheWriteMode { WriteBack, WriteThrough, WriteAround }
public enum SchedulingAlgorithm { Fifo, Deadline, Cfq, Noop }

// Data structures
public sealed class CoalesceBatch
{
    public string ArrayId { get; set; } = string.Empty;
    public long StartOffset { get; set; }
    public DateTime CreatedTime { get; set; }
    public List<PendingWrite> Writes { get; set; } = new();
    public int TotalBytes { get; set; }
}

public sealed class PendingWrite
{
    public long Offset { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public WritePriority Priority { get; set; }
}

public sealed class CoalescedWriteResult
{
    public bool BatchReady { get; set; }
    public int WritesCoalesced { get; set; }
    public int WritesPending { get; set; }
    public long BatchStartOffset { get; set; }
    public byte[] CoalescedData { get; set; } = Array.Empty<byte>();
}

public sealed class PrefetchCache
{
    private readonly ConcurrentDictionary<long, byte[]> _data = new();
    public bool TryGet(long offset, int length, out byte[]? data) => _data.TryGetValue(offset, out data);
    public bool Contains(long offset) => _data.ContainsKey(offset);
    public void Add(long offset, byte[] data) => _data[offset] = data;
}

public sealed class PrefetchedReadResult
{
    public byte[]? Data { get; set; }
    public bool WasCached { get; set; }
    public int BytesRead { get; set; }
}

public sealed class CacheEntry
{
    public string ArrayId { get; set; } = string.Empty;
    public long Offset { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public bool IsDirty { get; set; }
    public DateTime CachedTime { get; set; }
    public DateTime LastAccess { get; set; }
}

public sealed class CacheWriteResult
{
    public string ArrayId { get; set; } = string.Empty;
    public long Offset { get; set; }
    public int Length { get; set; }
    public bool CacheHit { get; set; }
    public bool Persisted { get; set; }
    public TimeSpan Latency { get; set; }
}

public sealed class CacheFlushResult
{
    public int EntriesFlushed { get; set; }
    public long BytesFlushed { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration { get; set; }
    public List<string> FlushErrors { get; set; } = new();
}

public sealed class IoRequest
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString();
    public string ArrayId { get; set; } = string.Empty;
    public IoType Type { get; set; }
    public long Offset { get; set; }
    public int Length { get; set; }
    public byte[]? Data { get; set; }
    public WritePriority Priority { get; set; }
}

public sealed class IoResult
{
    public byte[]? Data { get; set; }
    public int BytesTransferred { get; set; }
    public TimeSpan Latency { get; set; }
}

public sealed class ScheduleResult
{
    public string RequestId { get; set; } = string.Empty;
    public bool Scheduled { get; set; }
    public int QueuePosition { get; set; }
}

public sealed class QosPolicy
{
    public int? MaxIops { get; set; }
    public int? MaxBandwidthMbps { get; set; }
    public int? MinIops { get; set; }
    public int? MinBandwidthMbps { get; set; }
    public WritePriority Priority { get; set; } = WritePriority.Normal;
}

public sealed class QosStatistics
{
    public string WorkloadId { get; set; } = string.Empty;
    public long TotalReadOps { get; set; }
    public long TotalWriteOps { get; set; }
    public long TotalReadBytes { get; set; }
    public long TotalWriteBytes { get; set; }
    public long ThrottledOperations { get; set; }
    public long ThrottledBytes { get; set; }
}

// Statistics classes
public sealed class PerformanceStatistics
{
    public WriteCoalescingStatistics WriteCoalescing { get; set; } = new();
    public PrefetchStatistics Prefetch { get; set; } = new();
    public WriteCacheStatistics WriteCache { get; set; } = new();
    public SchedulerStatistics Scheduler { get; set; } = new();
}

public sealed class WriteCoalescingStatistics
{
    public long TotalWrites { get; set; }
    public long CoalescedWrites { get; set; }
    public double CoalesceRatio { get; set; }
    public int PendingBatches { get; set; }
}

public sealed class PrefetchStatistics
{
    public long CacheHits { get; set; }
    public long CacheMisses { get; set; }
    public double HitRatio { get; set; }
    public int CachedArrays { get; set; }
}

public sealed class WriteCacheStatistics
{
    public int TotalEntries { get; set; }
    public int DirtyEntries { get; set; }
    public long TotalBytes { get; set; }
    public long MaxBytes { get; set; }
    public double Utilization { get; set; }
}

public sealed class SchedulerStatistics
{
    public long TotalRequests { get; set; }
    public long CompletedRequests { get; set; }
    public int PendingRequests { get; set; }
}
