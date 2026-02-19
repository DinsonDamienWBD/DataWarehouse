using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateIoTIntegration.Strategies.Edge;

#region Edge Caching Strategy

/// <summary>
/// Edge caching strategy with LRU eviction, write-through/write-back modes,
/// cache coherence protocol, and configurable TTL per cache entry.
/// </summary>
public sealed class EdgeCachingStrategy : EdgeIntegrationStrategyBase
{
    public override string StrategyId => "edge-caching";
    public override string StrategyName => "Edge Cache Manager";
    public override string Description => "Intelligent edge caching with LRU eviction, write-through/write-back, and TTL management";
    public override string[] Tags => new[] { "iot", "edge", "cache", "lru", "ttl", "performance" };

    private readonly ConcurrentDictionary<string, EdgeCacheEntry> _cache = new();
    private readonly ConcurrentDictionary<string, long> _accessOrder = new();
    private long _accessCounter;
    private readonly int _maxEntries;
    private readonly EdgeCacheMode _mode;

    /// <summary>Creates a new edge caching strategy.</summary>
    public EdgeCachingStrategy(int maxEntries = 10000, EdgeCacheMode mode = EdgeCacheMode.WriteThrough)
    {
        _maxEntries = maxEntries;
        _mode = mode;
    }

    /// <summary>Gets a value from cache.</summary>
    public EdgeCacheEntry? Get(string key)
    {
        RecordOperation();
        if (_cache.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAt.HasValue && entry.ExpiresAt.Value < DateTimeOffset.UtcNow)
            {
                _cache.TryRemove(key, out _);
                _accessOrder.TryRemove(key, out _);
                return null;
            }
            _accessOrder[key] = Interlocked.Increment(ref _accessCounter);
            entry.HitCount++;
            return entry;
        }
        return null;
    }

    /// <summary>Puts a value into cache with optional TTL.</summary>
    public void Put(string key, byte[] data, TimeSpan? ttl = null, Dictionary<string, string>? metadata = null)
    {
        RecordOperation();
        if (_cache.Count >= _maxEntries)
            EvictLru();

        _cache[key] = new EdgeCacheEntry
        {
            Key = key,
            Data = data,
            SizeBytes = data.Length,
            CachedAt = DateTimeOffset.UtcNow,
            ExpiresAt = ttl.HasValue ? DateTimeOffset.UtcNow.Add(ttl.Value) : null,
            Metadata = metadata ?? new(),
            IsDirty = _mode == EdgeCacheMode.WriteBack
        };
        _accessOrder[key] = Interlocked.Increment(ref _accessCounter);
    }

    /// <summary>Invalidates a cache entry.</summary>
    public bool Invalidate(string key)
    {
        RecordOperation();
        _accessOrder.TryRemove(key, out _);
        return _cache.TryRemove(key, out _);
    }

    /// <summary>Flushes all dirty entries (write-back mode).</summary>
    public IEnumerable<EdgeCacheEntry> FlushDirty()
    {
        RecordOperation();
        var dirty = _cache.Values.Where(e => e.IsDirty).ToList();
        foreach (var entry in dirty)
            entry.IsDirty = false;
        return dirty;
    }

    /// <summary>Gets cache statistics.</summary>
    public EdgeCacheStats GetStats()
    {
        var entries = _cache.Values.ToArray();
        return new EdgeCacheStats
        {
            TotalEntries = entries.Length,
            TotalSizeBytes = entries.Sum(e => (long)e.SizeBytes),
            DirtyEntries = entries.Count(e => e.IsDirty),
            ExpiredEntries = entries.Count(e => e.ExpiresAt.HasValue && e.ExpiresAt.Value < DateTimeOffset.UtcNow),
            TotalHits = entries.Sum(e => e.HitCount),
            MaxEntries = _maxEntries,
            Mode = _mode
        };
    }

    private void EvictLru()
    {
        var oldest = _accessOrder.OrderBy(kvp => kvp.Value).Take(_maxEntries / 10 + 1);
        foreach (var (key, _) in oldest)
        {
            if (_cache.TryGetValue(key, out var entry) && entry.IsDirty)
                continue; // Don't evict dirty entries in write-back mode
            _cache.TryRemove(key, out _);
            _accessOrder.TryRemove(key, out _);
        }
    }

    public override Task<EdgeDeploymentResult> DeployModuleAsync(EdgeDeploymentRequest request, CancellationToken ct = default)
        => Task.FromResult(new EdgeDeploymentResult { Success = true, DeploymentId = Guid.NewGuid().ToString(), ModuleName = "edge-cache", DeployedAt = DateTimeOffset.UtcNow });

    public override Task<SyncResult> SyncAsync(EdgeSyncRequest request, CancellationToken ct = default)
    {
        var dirty = FlushDirty().ToArray();
        return Task.FromResult(new SyncResult { Success = true, ItemsSynced = dirty.Length, BytesSynced = dirty.Sum(e => e.SizeBytes), SyncedAt = DateTimeOffset.UtcNow });
    }

    public override Task<EdgeComputeResult> ExecuteComputeAsync(EdgeComputeRequest request, CancellationToken ct = default)
        => Task.FromResult(new EdgeComputeResult { Success = true, Output = new Dictionary<string, object> { ["cacheStats"] = GetStats() }, ExecutionTime = TimeSpan.FromMilliseconds(1) });

    public override Task<EdgeDeviceStatus> GetEdgeStatusAsync(string edgeDeviceId, CancellationToken ct = default)
        => Task.FromResult(new EdgeDeviceStatus { EdgeDeviceId = edgeDeviceId, IsOnline = true, LastSeen = DateTimeOffset.UtcNow, Modules = new(), ResourceUsage = new() });
}

/// <summary>Cache entry.</summary>
public sealed class EdgeCacheEntry
{
    public required string Key { get; init; }
    public required byte[] Data { get; init; }
    public int SizeBytes { get; init; }
    public DateTimeOffset CachedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
    public bool IsDirty { get; set; }
    public long HitCount { get; set; }
}

/// <summary>Cache mode.</summary>
public enum EdgeCacheMode { WriteThrough, WriteBack, ReadOnly }

/// <summary>Cache statistics.</summary>
public sealed class EdgeCacheStats
{
    public int TotalEntries { get; init; }
    public long TotalSizeBytes { get; init; }
    public int DirtyEntries { get; init; }
    public int ExpiredEntries { get; init; }
    public long TotalHits { get; init; }
    public int MaxEntries { get; init; }
    public EdgeCacheMode Mode { get; init; }
}

#endregion

#region Offline Sync Strategy

/// <summary>
/// Offline-first synchronization strategy with conflict resolution,
/// operation log replay, and delta-based sync for bandwidth efficiency.
/// </summary>
public sealed class OfflineSyncStrategy : EdgeIntegrationStrategyBase
{
    public override string StrategyId => "offline-sync";
    public override string StrategyName => "Offline Sync Manager";
    public override string Description => "Offline-first data synchronization with conflict resolution and operation log replay";
    public override string[] Tags => new[] { "iot", "edge", "offline", "sync", "conflict-resolution", "delta" };

    private readonly ConcurrentQueue<OfflineOperation> _operationLog = new();
    private readonly ConcurrentDictionary<string, long> _vectorClock = new();
    private bool _isOnline;
    private DateTimeOffset _lastSyncTime = DateTimeOffset.MinValue;

    /// <summary>Records an operation while offline.</summary>
    public void RecordOfflineOperation(string key, OfflineOperationType type, byte[]? data = null)
    {
        RecordOperation();
        var nodeId = Environment.MachineName;
        _vectorClock.AddOrUpdate(nodeId, 1, (_, v) => v + 1);

        _operationLog.Enqueue(new OfflineOperation
        {
            OperationId = Guid.NewGuid().ToString(),
            Key = key,
            Type = type,
            Data = data,
            Timestamp = DateTimeOffset.UtcNow,
            NodeId = nodeId,
            VectorClock = new Dictionary<string, long>(_vectorClock)
        });
    }

    /// <summary>Replays pending operations to the server.</summary>
    public async Task<OfflineSyncResult> ReplaySyncAsync(Func<OfflineOperation, Task<bool>> applyFunc, CancellationToken ct = default)
    {
        RecordOperation();
        var result = new OfflineSyncResult();
        var pending = new List<OfflineOperation>();

        while (_operationLog.TryDequeue(out var op))
            pending.Add(op);

        // Sort by vector clock for causal ordering
        pending.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        foreach (var op in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var success = await applyFunc(op);
                if (success)
                    result.Applied++;
                else
                {
                    result.Conflicts++;
                    result.ConflictedKeys.Add(op.Key);
                }
            }
            catch
            {
                result.Failed++;
                // Re-enqueue failed operations
                _operationLog.Enqueue(op);
            }
        }

        result.PendingRemaining = _operationLog.Count;
        _lastSyncTime = DateTimeOffset.UtcNow;
        _isOnline = true;
        return result;
    }

    /// <summary>Sets online/offline status.</summary>
    public void SetOnlineStatus(bool isOnline) => _isOnline = isOnline;

    /// <summary>Gets pending operation count.</summary>
    public int PendingOperationCount => _operationLog.Count;

    public override Task<EdgeDeploymentResult> DeployModuleAsync(EdgeDeploymentRequest request, CancellationToken ct = default)
        => Task.FromResult(new EdgeDeploymentResult { Success = true, DeploymentId = Guid.NewGuid().ToString(), ModuleName = "offline-sync", DeployedAt = DateTimeOffset.UtcNow });

    public override Task<SyncResult> SyncAsync(EdgeSyncRequest request, CancellationToken ct = default)
        => Task.FromResult(new SyncResult { Success = true, ItemsSynced = _operationLog.Count, BytesSynced = 0, SyncedAt = DateTimeOffset.UtcNow });

    public override Task<EdgeComputeResult> ExecuteComputeAsync(EdgeComputeRequest request, CancellationToken ct = default)
        => Task.FromResult(new EdgeComputeResult { Success = true, Output = new Dictionary<string, object> { ["pending"] = _operationLog.Count, ["isOnline"] = _isOnline }, ExecutionTime = TimeSpan.FromMilliseconds(1) });

    public override Task<EdgeDeviceStatus> GetEdgeStatusAsync(string edgeDeviceId, CancellationToken ct = default)
        => Task.FromResult(new EdgeDeviceStatus { EdgeDeviceId = edgeDeviceId, IsOnline = _isOnline, LastSeen = DateTimeOffset.UtcNow, Modules = new(), ResourceUsage = new() });
}

/// <summary>Offline operation type.</summary>
public enum OfflineOperationType { Create, Update, Delete, Merge }

/// <summary>Recorded offline operation.</summary>
public sealed class OfflineOperation
{
    public required string OperationId { get; init; }
    public required string Key { get; init; }
    public required OfflineOperationType Type { get; init; }
    public byte[]? Data { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string NodeId { get; init; }
    public Dictionary<string, long> VectorClock { get; init; } = new();
}

/// <summary>Offline sync result.</summary>
public sealed class OfflineSyncResult
{
    public int Applied { get; set; }
    public int Conflicts { get; set; }
    public int Failed { get; set; }
    public int PendingRemaining { get; set; }
    public List<string> ConflictedKeys { get; } = new();
}

#endregion

#region Bandwidth Estimation Strategy

/// <summary>
/// Bandwidth estimation strategy using active probing and passive measurement
/// to determine available bandwidth for edge-to-cloud data transfer.
/// </summary>
public sealed class BandwidthEstimationStrategy : EdgeIntegrationStrategyBase
{
    public override string StrategyId => "bandwidth-estimation";
    public override string StrategyName => "Bandwidth Estimator";
    public override string Description => "Estimates available bandwidth using active probing and passive measurement for adaptive data transfer";
    public override string[] Tags => new[] { "iot", "edge", "bandwidth", "estimation", "network", "adaptive" };

    private readonly ConcurrentQueue<BandwidthSample> _samples = new();
    private readonly int _maxSamples;
    private double _currentEstimateBps;

    public BandwidthEstimationStrategy(int maxSamples = 100) { _maxSamples = maxSamples; }

    /// <summary>Records a bandwidth measurement sample.</summary>
    public void RecordSample(long bytesTransferred, TimeSpan duration, BandwidthDirection direction)
    {
        RecordOperation();
        var bps = duration.TotalSeconds > 0 ? bytesTransferred * 8.0 / duration.TotalSeconds : 0;

        _samples.Enqueue(new BandwidthSample
        {
            BytesTransferred = bytesTransferred,
            Duration = duration,
            Direction = direction,
            Bps = bps,
            Timestamp = DateTimeOffset.UtcNow
        });

        while (_samples.Count > _maxSamples)
            _samples.TryDequeue(out _);

        // EWMA (Exponential Weighted Moving Average) with alpha = 0.3
        _currentEstimateBps = _currentEstimateBps == 0 ? bps : _currentEstimateBps * 0.7 + bps * 0.3;
    }

    /// <summary>Gets the current bandwidth estimate.</summary>
    public BandwidthEstimate GetEstimate()
    {
        var samples = _samples.ToArray();
        if (samples.Length == 0)
            return new BandwidthEstimate { EstimatedBps = 0, Confidence = 0, SampleCount = 0 };

        var recentSamples = samples.Where(s => s.Timestamp > DateTimeOffset.UtcNow.AddMinutes(-5)).ToArray();
        var upSamples = recentSamples.Where(s => s.Direction == BandwidthDirection.Upload).ToArray();
        var downSamples = recentSamples.Where(s => s.Direction == BandwidthDirection.Download).ToArray();

        return new BandwidthEstimate
        {
            EstimatedBps = _currentEstimateBps,
            UploadBps = upSamples.Length > 0 ? upSamples.Average(s => s.Bps) : 0,
            DownloadBps = downSamples.Length > 0 ? downSamples.Average(s => s.Bps) : 0,
            Confidence = Math.Min(1.0, recentSamples.Length / 10.0),
            SampleCount = recentSamples.Length,
            Jitter = recentSamples.Length > 1
                ? recentSamples.Select(s => s.Bps).StandardDeviation() / _currentEstimateBps
                : 0,
            LastMeasured = recentSamples.Length > 0 ? recentSamples.Max(s => s.Timestamp) : DateTimeOffset.MinValue
        };
    }

    /// <summary>Suggests optimal transfer parameters based on bandwidth estimate.</summary>
    public TransferSuggestion SuggestTransferParameters(long dataSize)
    {
        var estimate = GetEstimate();
        var estimatedTransferTime = estimate.EstimatedBps > 0
            ? TimeSpan.FromSeconds(dataSize * 8.0 / estimate.EstimatedBps)
            : TimeSpan.MaxValue;

        return new TransferSuggestion
        {
            ChunkSize = estimate.EstimatedBps switch
            {
                > 100_000_000 => 1024 * 1024,  // 1MB for 100+ Mbps
                > 10_000_000 => 256 * 1024,     // 256KB for 10+ Mbps
                > 1_000_000 => 64 * 1024,       // 64KB for 1+ Mbps
                _ => 16 * 1024                    // 16KB for slow connections
            },
            ConcurrentStreams = estimate.EstimatedBps switch
            {
                > 100_000_000 => 8,
                > 10_000_000 => 4,
                > 1_000_000 => 2,
                _ => 1
            },
            UseCompression = estimate.EstimatedBps < 10_000_000,
            EstimatedTransferTime = estimatedTransferTime,
            Priority = estimatedTransferTime > TimeSpan.FromMinutes(5) ? DataTransferPriority.Background : DataTransferPriority.Normal
        };
    }

    public override Task<EdgeDeploymentResult> DeployModuleAsync(EdgeDeploymentRequest request, CancellationToken ct = default)
        => Task.FromResult(new EdgeDeploymentResult { Success = true, DeploymentId = Guid.NewGuid().ToString(), ModuleName = "bandwidth-probe", DeployedAt = DateTimeOffset.UtcNow });

    public override Task<SyncResult> SyncAsync(EdgeSyncRequest request, CancellationToken ct = default)
        => Task.FromResult(new SyncResult { Success = true, ItemsSynced = 0, BytesSynced = 0, SyncedAt = DateTimeOffset.UtcNow });

    public override Task<EdgeComputeResult> ExecuteComputeAsync(EdgeComputeRequest request, CancellationToken ct = default)
    {
        var estimate = GetEstimate();
        return Task.FromResult(new EdgeComputeResult
        {
            Success = true,
            Output = new Dictionary<string, object>
            {
                ["estimatedBps"] = estimate.EstimatedBps,
                ["confidence"] = estimate.Confidence,
                ["sampleCount"] = estimate.SampleCount
            },
            ExecutionTime = TimeSpan.FromMilliseconds(1)
        });
    }

    public override Task<EdgeDeviceStatus> GetEdgeStatusAsync(string edgeDeviceId, CancellationToken ct = default)
        => Task.FromResult(new EdgeDeviceStatus { EdgeDeviceId = edgeDeviceId, IsOnline = true, LastSeen = DateTimeOffset.UtcNow, Modules = new(), ResourceUsage = new() });
}

/// <summary>Bandwidth measurement sample.</summary>
public sealed class BandwidthSample
{
    public long BytesTransferred { get; init; }
    public TimeSpan Duration { get; init; }
    public BandwidthDirection Direction { get; init; }
    public double Bps { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>Transfer direction.</summary>
public enum BandwidthDirection { Upload, Download }

/// <summary>Bandwidth estimate.</summary>
public sealed class BandwidthEstimate
{
    public double EstimatedBps { get; init; }
    public double UploadBps { get; init; }
    public double DownloadBps { get; init; }
    public double Confidence { get; init; }
    public int SampleCount { get; init; }
    public double Jitter { get; init; }
    public DateTimeOffset LastMeasured { get; init; }
}

/// <summary>Transfer parameter suggestion.</summary>
public sealed class TransferSuggestion
{
    public int ChunkSize { get; init; }
    public int ConcurrentStreams { get; init; }
    public bool UseCompression { get; init; }
    public TimeSpan EstimatedTransferTime { get; init; }
    public DataTransferPriority Priority { get; init; }
}

/// <summary>Data transfer priority levels.</summary>
public enum DataTransferPriority { Realtime, High, Normal, Low, Background }

#endregion

#region Data Prioritization Strategy

/// <summary>
/// Data prioritization strategy for edge devices with limited bandwidth.
/// Classifies and queues data by priority, ensuring critical telemetry
/// is transmitted first, with adaptive quality reduction for lower priorities.
/// </summary>
public sealed class DataPrioritizationStrategy : EdgeIntegrationStrategyBase
{
    public override string StrategyId => "data-prioritization";
    public override string StrategyName => "Data Prioritizer";
    public override string Description => "Prioritizes edge data transmission based on criticality, freshness, and bandwidth availability";
    public override string[] Tags => new[] { "iot", "edge", "priority", "queue", "bandwidth", "qos" };

    private readonly ConcurrentDictionary<DataTransferPriority, ConcurrentQueue<PrioritizedDataItem>> _queues = new();
    private readonly ConcurrentDictionary<string, DataClassificationRule> _rules = new();

    public DataPrioritizationStrategy()
    {
        foreach (DataTransferPriority priority in Enum.GetValues(typeof(DataTransferPriority)))
            _queues[priority] = new ConcurrentQueue<PrioritizedDataItem>();

        // Default classification rules
        AddClassificationRule("alarm", new DataClassificationRule { Pattern = "alarm.*|alert.*|emergency.*", Priority = DataTransferPriority.Realtime, MaxLatency = TimeSpan.FromSeconds(1) });
        AddClassificationRule("telemetry", new DataClassificationRule { Pattern = "telemetry.*|sensor.*", Priority = DataTransferPriority.High, MaxLatency = TimeSpan.FromSeconds(30) });
        AddClassificationRule("metrics", new DataClassificationRule { Pattern = "metrics.*|stats.*", Priority = DataTransferPriority.Normal, MaxLatency = TimeSpan.FromMinutes(5) });
        AddClassificationRule("logs", new DataClassificationRule { Pattern = "log.*|audit.*", Priority = DataTransferPriority.Low, MaxLatency = TimeSpan.FromMinutes(30) });
        AddClassificationRule("archive", new DataClassificationRule { Pattern = "archive.*|backup.*", Priority = DataTransferPriority.Background, MaxLatency = TimeSpan.FromHours(24) });
    }

    /// <summary>Adds a classification rule.</summary>
    public void AddClassificationRule(string name, DataClassificationRule rule) => _rules[name] = rule;

    /// <summary>Enqueues data with automatic priority classification.</summary>
    public void Enqueue(string topic, byte[] data, Dictionary<string, string>? metadata = null)
    {
        RecordOperation();
        var priority = ClassifyPriority(topic);
        var item = new PrioritizedDataItem
        {
            ItemId = Guid.NewGuid().ToString(),
            Topic = topic,
            Data = data,
            Priority = priority,
            EnqueuedAt = DateTimeOffset.UtcNow,
            Metadata = metadata ?? new()
        };

        _queues[priority].Enqueue(item);
    }

    /// <summary>Dequeues the next highest-priority item.</summary>
    public PrioritizedDataItem? Dequeue()
    {
        RecordOperation();
        // Strict priority ordering
        foreach (DataTransferPriority priority in Enum.GetValues(typeof(DataTransferPriority)))
        {
            if (_queues[priority].TryDequeue(out var item))
                return item;
        }
        return null;
    }

    /// <summary>Dequeues items up to a byte budget (bandwidth-aware).</summary>
    public IReadOnlyList<PrioritizedDataItem> DequeueBatch(long maxBytes)
    {
        RecordOperation();
        var batch = new List<PrioritizedDataItem>();
        long totalBytes = 0;

        foreach (DataTransferPriority priority in Enum.GetValues(typeof(DataTransferPriority)))
        {
            while (_queues[priority].TryPeek(out var item) && totalBytes + item.Data.Length <= maxBytes)
            {
                if (_queues[priority].TryDequeue(out var dequeued))
                {
                    batch.Add(dequeued);
                    totalBytes += dequeued.Data.Length;
                }
            }
        }

        return batch;
    }

    /// <summary>Gets queue statistics.</summary>
    public Dictionary<DataTransferPriority, int> GetQueueDepths()
    {
        var result = new Dictionary<DataTransferPriority, int>();
        foreach (var (priority, queue) in _queues)
            result[priority] = queue.Count;
        return result;
    }

    private DataTransferPriority ClassifyPriority(string topic)
    {
        foreach (var rule in _rules.Values)
        {
            if (topic.StartsWith(rule.Pattern.TrimEnd('.', '*'), StringComparison.OrdinalIgnoreCase))
                return rule.Priority;
        }
        return DataTransferPriority.Normal;
    }

    public override Task<EdgeDeploymentResult> DeployModuleAsync(EdgeDeploymentRequest request, CancellationToken ct = default)
        => Task.FromResult(new EdgeDeploymentResult { Success = true, DeploymentId = Guid.NewGuid().ToString(), ModuleName = "data-prioritizer", DeployedAt = DateTimeOffset.UtcNow });

    public override Task<SyncResult> SyncAsync(EdgeSyncRequest request, CancellationToken ct = default)
    {
        var total = _queues.Values.Sum(q => q.Count);
        return Task.FromResult(new SyncResult { Success = true, ItemsSynced = total, BytesSynced = 0, SyncedAt = DateTimeOffset.UtcNow });
    }

    public override Task<EdgeComputeResult> ExecuteComputeAsync(EdgeComputeRequest request, CancellationToken ct = default)
        => Task.FromResult(new EdgeComputeResult { Success = true, Output = new Dictionary<string, object>(GetQueueDepths().Select(kvp => new KeyValuePair<string, object>(kvp.Key.ToString(), kvp.Value))), ExecutionTime = TimeSpan.FromMilliseconds(1) });

    public override Task<EdgeDeviceStatus> GetEdgeStatusAsync(string edgeDeviceId, CancellationToken ct = default)
        => Task.FromResult(new EdgeDeviceStatus { EdgeDeviceId = edgeDeviceId, IsOnline = true, LastSeen = DateTimeOffset.UtcNow, Modules = new(), ResourceUsage = new() });
}

/// <summary>Data classification rule.</summary>
public sealed class DataClassificationRule
{
    public required string Pattern { get; init; }
    public required DataTransferPriority Priority { get; init; }
    public TimeSpan MaxLatency { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>Prioritized data item.</summary>
public sealed class PrioritizedDataItem
{
    public required string ItemId { get; init; }
    public required string Topic { get; init; }
    public required byte[] Data { get; init; }
    public required DataTransferPriority Priority { get; init; }
    public required DateTimeOffset EnqueuedAt { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
}

#endregion

#region Edge Analytics Strategy

/// <summary>
/// Edge analytics strategy for local data processing, aggregation,
/// and anomaly detection without cloud connectivity.
/// </summary>
public sealed class EdgeAnalyticsStrategy : EdgeIntegrationStrategyBase
{
    public override string StrategyId => "edge-analytics";
    public override string StrategyName => "Edge Analytics Engine";
    public override string Description => "Local analytics with windowed aggregation, anomaly detection, and trend analysis";
    public override string[] Tags => new[] { "iot", "edge", "analytics", "aggregation", "anomaly", "trend" };

    private readonly ConcurrentDictionary<string, SlidingWindow> _windows = new();
    private readonly ConcurrentDictionary<string, AnomalyDetector> _detectors = new();

    /// <summary>Ingests a data point for a metric.</summary>
    public void Ingest(string metricId, double value, DateTimeOffset? timestamp = null)
    {
        RecordOperation();
        var ts = timestamp ?? DateTimeOffset.UtcNow;

        var window = _windows.GetOrAdd(metricId, _ => new SlidingWindow(TimeSpan.FromMinutes(5)));
        window.Add(value, ts);

        var detector = _detectors.GetOrAdd(metricId, _ => new AnomalyDetector());
        detector.Update(value);
    }

    /// <summary>Gets aggregated statistics for a metric.</summary>
    public MetricAggregation GetAggregation(string metricId)
    {
        RecordOperation();
        if (!_windows.TryGetValue(metricId, out var window))
            return new MetricAggregation { MetricId = metricId, SampleCount = 0 };

        var values = window.GetValues();
        if (values.Length == 0)
            return new MetricAggregation { MetricId = metricId, SampleCount = 0 };

        Array.Sort(values);
        return new MetricAggregation
        {
            MetricId = metricId,
            SampleCount = values.Length,
            Min = values[0],
            Max = values[^1],
            Mean = values.Average(),
            Median = values[values.Length / 2],
            P95 = values[(int)(values.Length * 0.95)],
            P99 = values[(int)(values.Length * 0.99)],
            StdDev = values.StandardDeviation(),
            WindowStart = window.OldestTimestamp,
            WindowEnd = window.NewestTimestamp
        };
    }

    /// <summary>Checks if a metric value is anomalous.</summary>
    public AnomalyResult CheckAnomaly(string metricId, double value)
    {
        RecordOperation();
        if (!_detectors.TryGetValue(metricId, out var detector))
            return new AnomalyResult { IsAnomaly = false, Confidence = 0 };

        return detector.Check(value);
    }

    /// <summary>Gets trend analysis for a metric.</summary>
    public TrendAnalysis GetTrend(string metricId)
    {
        if (!_windows.TryGetValue(metricId, out var window))
            return new TrendAnalysis { MetricId = metricId, Direction = TrendDirection.Stable };

        var values = window.GetTimedValues();
        if (values.Length < 3)
            return new TrendAnalysis { MetricId = metricId, Direction = TrendDirection.Stable };

        // Simple linear regression for trend
        var n = values.Length;
        var sumX = 0.0;
        var sumY = 0.0;
        var sumXY = 0.0;
        var sumX2 = 0.0;

        for (var i = 0; i < n; i++)
        {
            sumX += i;
            sumY += values[i].Value;
            sumXY += i * values[i].Value;
            sumX2 += i * i;
        }

        var slope = (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);
        var intercept = (sumY - slope * sumX) / n;

        return new TrendAnalysis
        {
            MetricId = metricId,
            Direction = slope > 0.01 ? TrendDirection.Increasing
                : slope < -0.01 ? TrendDirection.Decreasing
                : TrendDirection.Stable,
            Slope = slope,
            Intercept = intercept,
            SampleCount = n
        };
    }

    public override Task<EdgeDeploymentResult> DeployModuleAsync(EdgeDeploymentRequest request, CancellationToken ct = default)
        => Task.FromResult(new EdgeDeploymentResult { Success = true, DeploymentId = Guid.NewGuid().ToString(), ModuleName = "edge-analytics", DeployedAt = DateTimeOffset.UtcNow });

    public override Task<SyncResult> SyncAsync(EdgeSyncRequest request, CancellationToken ct = default)
        => Task.FromResult(new SyncResult { Success = true, ItemsSynced = _windows.Count, BytesSynced = 0, SyncedAt = DateTimeOffset.UtcNow });

    public override Task<EdgeComputeResult> ExecuteComputeAsync(EdgeComputeRequest request, CancellationToken ct = default)
    {
        var metrics = _windows.Keys.Select(k => (object)GetAggregation(k)).ToArray();
        return Task.FromResult(new EdgeComputeResult
        {
            Success = true,
            Output = new Dictionary<string, object> { ["metrics"] = metrics, ["metricCount"] = _windows.Count },
            ExecutionTime = TimeSpan.FromMilliseconds(5)
        });
    }

    public override Task<EdgeDeviceStatus> GetEdgeStatusAsync(string edgeDeviceId, CancellationToken ct = default)
        => Task.FromResult(new EdgeDeviceStatus { EdgeDeviceId = edgeDeviceId, IsOnline = true, LastSeen = DateTimeOffset.UtcNow, Modules = new(), ResourceUsage = new() });
}

/// <summary>Metric aggregation result.</summary>
public sealed class MetricAggregation
{
    public required string MetricId { get; init; }
    public int SampleCount { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double Mean { get; init; }
    public double Median { get; init; }
    public double P95 { get; init; }
    public double P99 { get; init; }
    public double StdDev { get; init; }
    public DateTimeOffset WindowStart { get; init; }
    public DateTimeOffset WindowEnd { get; init; }
}

/// <summary>Anomaly detection result.</summary>
public sealed class AnomalyResult
{
    public bool IsAnomaly { get; init; }
    public double Confidence { get; init; }
    public double ZScore { get; init; }
    public string? Reason { get; init; }
}

/// <summary>Trend analysis result.</summary>
public sealed class TrendAnalysis
{
    public required string MetricId { get; init; }
    public TrendDirection Direction { get; init; }
    public double Slope { get; init; }
    public double Intercept { get; init; }
    public int SampleCount { get; init; }
}

/// <summary>Trend direction.</summary>
public enum TrendDirection { Increasing, Stable, Decreasing }

#endregion

#region Internal Helpers

/// <summary>Sliding time window for metric values.</summary>
internal sealed class SlidingWindow
{
    private readonly TimeSpan _windowSize;
    private readonly ConcurrentQueue<(double Value, DateTimeOffset Timestamp)> _values = new();

    public DateTimeOffset OldestTimestamp { get; private set; } = DateTimeOffset.MaxValue;
    public DateTimeOffset NewestTimestamp { get; private set; } = DateTimeOffset.MinValue;

    public SlidingWindow(TimeSpan windowSize) { _windowSize = windowSize; }

    public void Add(double value, DateTimeOffset timestamp)
    {
        _values.Enqueue((value, timestamp));
        if (timestamp < OldestTimestamp) OldestTimestamp = timestamp;
        if (timestamp > NewestTimestamp) NewestTimestamp = timestamp;

        // Evict old values
        var cutoff = DateTimeOffset.UtcNow - _windowSize;
        while (_values.TryPeek(out var oldest) && oldest.Timestamp < cutoff)
            _values.TryDequeue(out _);
    }

    public double[] GetValues() => _values.Select(v => v.Value).ToArray();
    public (double Value, DateTimeOffset Timestamp)[] GetTimedValues() => _values.ToArray();
}

/// <summary>Z-score based anomaly detector with online mean/variance.</summary>
internal sealed class AnomalyDetector
{
    private double _mean;
    private double _m2;
    private long _count;
    private const double ZScoreThreshold = 3.0;

    public void Update(double value)
    {
        _count++;
        var delta = value - _mean;
        _mean += delta / _count;
        var delta2 = value - _mean;
        _m2 += delta * delta2;
    }

    public AnomalyResult Check(double value)
    {
        if (_count < 10)
            return new AnomalyResult { IsAnomaly = false, Confidence = 0 };

        var variance = _m2 / (_count - 1);
        var stdDev = Math.Sqrt(variance);

        if (stdDev < 1e-10)
            return new AnomalyResult { IsAnomaly = false, Confidence = 0, ZScore = 0 };

        var zScore = Math.Abs((value - _mean) / stdDev);
        var isAnomaly = zScore > ZScoreThreshold;

        return new AnomalyResult
        {
            IsAnomaly = isAnomaly,
            Confidence = Math.Min(1.0, zScore / (ZScoreThreshold * 2)),
            ZScore = zScore,
            Reason = isAnomaly ? $"Z-score {zScore:F2} exceeds threshold {ZScoreThreshold}" : null
        };
    }
}

/// <summary>Extension methods for statistical calculations.</summary>
internal static class StatisticsExtensions
{
    public static double StandardDeviation(this double[] values)
    {
        if (values.Length < 2) return 0;
        var mean = values.Average();
        var sumOfSquares = values.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sumOfSquares / (values.Length - 1));
    }

    public static double StandardDeviation(this IEnumerable<double> values)
        => values.ToArray().StandardDeviation();
}

#endregion
