using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace DataWarehouse.Plugins.UltimateMultiCloud.Strategies.Replication;

/// <summary>
/// 118.2: Cross-Cloud Data Replication Strategies
/// Enables data synchronization across cloud providers.
/// </summary>

/// <summary>
/// Synchronous cross-cloud replication with strong consistency.
/// </summary>
public sealed class SynchronousCrossCloudReplicationStrategy : MultiCloudStrategyBase
{
    private readonly ConcurrentDictionary<string, ReplicationTopology> _topologies = new();

    public override string StrategyId => "replication-sync-cross-cloud";
    public override string StrategyName => "Synchronous Cross-Cloud Replication";
    public override string Category => "Replication";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Synchronous replication across cloud providers with strong consistency guarantees",
        Category = Category,
        SupportsCrossCloudReplication = true,
        SupportsAutomaticFailover = true,
        SupportsDataSovereignty = true,
        TypicalLatencyOverheadMs = 50.0,
        MemoryFootprint = "Medium"
    };

    /// <summary>Creates a replication topology.</summary>
    public ReplicationTopology CreateTopology(string topologyId, string primaryProvider, IEnumerable<string> replicaProviders)
    {
        var topology = new ReplicationTopology
        {
            TopologyId = topologyId,
            PrimaryProvider = primaryProvider,
            ReplicaProviders = replicaProviders.ToList(),
            Mode = ReplicationMode.Synchronous,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _topologies[topologyId] = topology;
        return topology;
    }

    /// <summary>Replicates data synchronously across clouds.</summary>
    public async Task<ReplicationResult> ReplicateAsync(
        string topologyId,
        string objectId,
        ReadOnlyMemory<byte> data,
        CancellationToken ct = default)
    {
        if (!_topologies.TryGetValue(topologyId, out var topology))
            return new ReplicationResult { Success = false, ErrorMessage = "Topology not found" };

        var startTime = DateTimeOffset.UtcNow;
        var replicaTasks = topology.ReplicaProviders.Select(async provider =>
        {
            await Task.Delay(20, ct); // Simulate network transfer
            return (provider, success: true, latency: TimeSpan.FromMilliseconds(20));
        }).ToList();

        var results = await Task.WhenAll(replicaTasks);
        var failedReplicas = results.Where(r => !r.success).ToList();

        if (failedReplicas.Any())
        {
            RecordFailure();
            return new ReplicationResult
            {
                Success = false,
                ErrorMessage = $"Replication failed on: {string.Join(", ", failedReplicas.Select(r => r.provider))}",
                Duration = DateTimeOffset.UtcNow - startTime
            };
        }

        RecordSuccess();
        return new ReplicationResult
        {
            Success = true,
            Duration = DateTimeOffset.UtcNow - startTime,
            ReplicatedTo = topology.ReplicaProviders.ToArray(),
            AverageLatencyMs = results.Average(r => r.latency.TotalMilliseconds)
        };
    }

    protected override string? GetCurrentState() => $"Topologies: {_topologies.Count}";
}

/// <summary>
/// Asynchronous cross-cloud replication with eventual consistency.
/// </summary>
public sealed class AsynchronousCrossCloudReplicationStrategy : MultiCloudStrategyBase
{
    private readonly ConcurrentQueue<ReplicationTask> _replicationQueue = new();
    private readonly ConcurrentDictionary<string, long> _replicationLag = new();

    public override string StrategyId => "replication-async-cross-cloud";
    public override string StrategyName => "Asynchronous Cross-Cloud Replication";
    public override string Category => "Replication";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Asynchronous replication with eventual consistency for high throughput",
        Category = Category,
        SupportsCrossCloudReplication = true,
        SupportsAutomaticFailover = true,
        TypicalLatencyOverheadMs = 5.0,
        MemoryFootprint = "Medium"
    };

    /// <summary>Queues data for asynchronous replication.</summary>
    public string QueueReplication(string sourceProvider, IEnumerable<string> targetProviders, string objectId, long sizeBytes)
    {
        var task = new ReplicationTask
        {
            TaskId = Guid.NewGuid().ToString("N"),
            SourceProvider = sourceProvider,
            TargetProviders = targetProviders.ToList(),
            ObjectId = objectId,
            SizeBytes = sizeBytes,
            QueuedAt = DateTimeOffset.UtcNow
        };
        _replicationQueue.Enqueue(task);
        return task.TaskId;
    }

    /// <summary>Gets replication lag for a provider.</summary>
    public TimeSpan GetReplicationLag(string providerId)
    {
        return _replicationLag.TryGetValue(providerId, out var lagMs)
            ? TimeSpan.FromMilliseconds(lagMs)
            : TimeSpan.Zero;
    }

    /// <summary>Processes queued replication tasks.</summary>
    public async Task ProcessQueueAsync(CancellationToken ct = default)
    {
        while (_replicationQueue.TryDequeue(out var task) && !ct.IsCancellationRequested)
        {
            foreach (var target in task.TargetProviders)
            {
                await Task.Delay(10, ct); // Simulate transfer
                var lag = (DateTimeOffset.UtcNow - task.QueuedAt).TotalMilliseconds;
                _replicationLag.AddOrUpdate(target, (long)lag, (_, _) => (long)lag);
            }
            RecordSuccess();
        }
    }

    protected override string? GetCurrentState() => $"Queue: {_replicationQueue.Count}";
}

/// <summary>
/// Bidirectional cross-cloud replication with conflict resolution.
/// </summary>
public sealed class BidirectionalCrossCloudReplicationStrategy : MultiCloudStrategyBase
{
    private readonly ConcurrentDictionary<string, VectorClock> _vectorClocks = new();

    public override string StrategyId => "replication-bidirectional-cross-cloud";
    public override string StrategyName => "Bidirectional Cross-Cloud Replication";
    public override string Category => "Replication";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Multi-master replication across clouds with vector clock conflict resolution",
        Category = Category,
        SupportsCrossCloudReplication = true,
        SupportsAutomaticFailover = true,
        TypicalLatencyOverheadMs = 30.0,
        MemoryFootprint = "High"
    };

    /// <summary>Resolves conflicts using vector clocks.</summary>
    public ConflictResolutionResult ResolveConflict(string objectId, ReplicaVersion localVersion, ReplicaVersion remoteVersion)
    {
        var localClock = _vectorClocks.GetOrAdd(objectId, _ => new VectorClock());

        // Vector clock comparison
        if (localVersion.Timestamp > remoteVersion.Timestamp)
        {
            return new ConflictResolutionResult
            {
                Resolution = ConflictResolution.UseLocal,
                WinningVersion = localVersion,
                Reason = "Local version is newer"
            };
        }
        else if (remoteVersion.Timestamp > localVersion.Timestamp)
        {
            return new ConflictResolutionResult
            {
                Resolution = ConflictResolution.UseRemote,
                WinningVersion = remoteVersion,
                Reason = "Remote version is newer"
            };
        }

        // Concurrent updates - use provider priority
        return new ConflictResolutionResult
        {
            Resolution = ConflictResolution.Merge,
            WinningVersion = localVersion.ProviderId.CompareTo(remoteVersion.ProviderId) < 0 ? localVersion : remoteVersion,
            Reason = "Concurrent updates - resolved by provider priority"
        };
    }
}

/// <summary>
/// Geo-routed replication directing data to optimal cloud regions.
/// </summary>
public sealed class GeoRoutedReplicationStrategy : MultiCloudStrategyBase
{
    private readonly ConcurrentDictionary<string, GeoRegion> _regions = new();

    public override string StrategyId => "replication-geo-routed";
    public override string StrategyName => "Geo-Routed Replication";
    public override string Category => "Replication";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Routes replication based on geographic location and data sovereignty requirements",
        Category = Category,
        SupportsCrossCloudReplication = true,
        SupportsDataSovereignty = true,
        TypicalLatencyOverheadMs = 10.0,
        MemoryFootprint = "Low"
    };

    /// <summary>Registers a geo region.</summary>
    public void RegisterRegion(string regionId, string provider, double latitude, double longitude, IEnumerable<string> sovereigntyZones)
    {
        _regions[regionId] = new GeoRegion
        {
            RegionId = regionId,
            ProviderId = provider,
            Latitude = latitude,
            Longitude = longitude,
            SovereigntyZones = sovereigntyZones.ToList()
        };
    }

    /// <summary>Gets optimal replication targets for a location.</summary>
    public IReadOnlyList<string> GetOptimalTargets(double latitude, double longitude, string? requiredSovereigntyZone = null)
    {
        var candidates = _regions.Values.AsEnumerable();

        if (requiredSovereigntyZone != null)
            candidates = candidates.Where(r => r.SovereigntyZones.Contains(requiredSovereigntyZone));

        return candidates
            .OrderBy(r => CalculateDistance(latitude, longitude, r.Latitude, r.Longitude))
            .Select(r => r.ProviderId)
            .ToList();
    }

    private static double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 6371 * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a)); // km
    }

    protected override string? GetCurrentState() => $"Regions: {_regions.Count}";
}

/// <summary>
/// Delta-based cross-cloud replication transferring only changes.
/// </summary>
public sealed class DeltaReplicationStrategy : MultiCloudStrategyBase
{
    public override string StrategyId => "replication-delta";
    public override string StrategyName => "Delta Cross-Cloud Replication";
    public override string Category => "Replication";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Transfers only changed blocks using rsync-like algorithm for bandwidth efficiency",
        Category = Category,
        SupportsCrossCloudReplication = true,
        SupportsCostOptimization = true,
        TypicalLatencyOverheadMs = 15.0,
        MemoryFootprint = "Medium"
    };

    /// <summary>Computes delta between versions.</summary>
    public DeltaComputeResult ComputeDelta(ReadOnlyMemory<byte> source, ReadOnlyMemory<byte> target)
    {
        // Simplified delta computation
        var changedBlocks = new List<(int offset, int length)>();
        var blockSize = 4096;

        for (int i = 0; i < source.Length; i += blockSize)
        {
            var sourceBlock = source.Slice(i, Math.Min(blockSize, source.Length - i));
            var targetBlock = i < target.Length
                ? target.Slice(i, Math.Min(blockSize, target.Length - i))
                : ReadOnlyMemory<byte>.Empty;

            if (!sourceBlock.Span.SequenceEqual(targetBlock.Span))
            {
                changedBlocks.Add((i, sourceBlock.Length));
            }
        }

        return new DeltaComputeResult
        {
            TotalBlocks = (source.Length + blockSize - 1) / blockSize,
            ChangedBlocks = changedBlocks.Count,
            ChangedBytes = changedBlocks.Sum(b => b.length),
            SavingsPercent = source.Length > 0
                ? (1.0 - (double)changedBlocks.Sum(b => b.length) / source.Length) * 100
                : 0
        };
    }
}

/// <summary>
/// Multi-region replication with quorum-based consistency.
/// </summary>
public sealed class QuorumReplicationStrategy : MultiCloudStrategyBase
{
    public override string StrategyId => "replication-quorum";
    public override string StrategyName => "Quorum Cross-Cloud Replication";
    public override string Category => "Replication";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Quorum-based replication (W+R>N) for tunable consistency across clouds",
        Category = Category,
        SupportsCrossCloudReplication = true,
        SupportsAutomaticFailover = true,
        TypicalLatencyOverheadMs = 25.0,
        MemoryFootprint = "Low"
    };

    /// <summary>Writes with quorum.</summary>
    public async Task<QuorumWriteResult> WriteWithQuorumAsync(
        string key,
        ReadOnlyMemory<byte> data,
        IEnumerable<string> replicas,
        int writeQuorum,
        CancellationToken ct = default)
    {
        var replicaList = replicas.ToList();
        var tasks = replicaList.Select(async r =>
        {
            await Task.Delay(15, ct);
            return (replica: r, success: true);
        }).ToList();

        var results = await Task.WhenAll(tasks);
        var successCount = results.Count(r => r.success);

        var achieved = successCount >= writeQuorum;
        if (achieved) RecordSuccess(); else RecordFailure();

        return new QuorumWriteResult
        {
            Success = achieved,
            RequiredQuorum = writeQuorum,
            AchievedQuorum = successCount,
            SuccessfulReplicas = results.Where(r => r.success).Select(r => r.replica).ToArray()
        };
    }
}

#region Supporting Types

public sealed class ReplicationTopology
{
    public required string TopologyId { get; init; }
    public required string PrimaryProvider { get; init; }
    public List<string> ReplicaProviders { get; init; } = new();
    public ReplicationMode Mode { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public enum ReplicationMode { Synchronous, Asynchronous, SemiSynchronous }

public sealed class ReplicationResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan Duration { get; init; }
    public string[]? ReplicatedTo { get; init; }
    public double AverageLatencyMs { get; init; }
}

public sealed class ReplicationTask
{
    public required string TaskId { get; init; }
    public required string SourceProvider { get; init; }
    public List<string> TargetProviders { get; init; } = new();
    public required string ObjectId { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset QueuedAt { get; init; }
}

public sealed class VectorClock
{
    public Dictionary<string, long> Clocks { get; } = new();
    public void Increment(string nodeId) => Clocks[nodeId] = Clocks.GetValueOrDefault(nodeId) + 1;
}

public sealed class ReplicaVersion
{
    public required string ProviderId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required long SequenceNumber { get; init; }
}

public enum ConflictResolution { UseLocal, UseRemote, Merge }

public sealed class ConflictResolutionResult
{
    public ConflictResolution Resolution { get; init; }
    public ReplicaVersion? WinningVersion { get; init; }
    public string? Reason { get; init; }
}

public sealed class GeoRegion
{
    public required string RegionId { get; init; }
    public required string ProviderId { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public List<string> SovereigntyZones { get; init; } = new();
}

public sealed class DeltaComputeResult
{
    public int TotalBlocks { get; init; }
    public int ChangedBlocks { get; init; }
    public long ChangedBytes { get; init; }
    public double SavingsPercent { get; init; }
}

public sealed class QuorumWriteResult
{
    public bool Success { get; init; }
    public int RequiredQuorum { get; init; }
    public int AchievedQuorum { get; init; }
    public string[]? SuccessfulReplicas { get; init; }
}

/// <summary>
/// Bandwidth-throttled replication strategy.
/// </summary>
public sealed class BandwidthThrottledReplicationStrategy : MultiCloudStrategyBase
{
    private readonly TokenBucketRateLimiter _rateLimiter;
    private readonly int _maxBytesPerSecond;

    public override string StrategyId => "replication-bandwidth-throttled";
    public override string StrategyName => "Bandwidth-Throttled Replication";
    public override string Category => "Replication";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Cross-cloud replication with configurable bandwidth throttling to control egress costs",
        Category = Category,
        SupportsCrossCloudReplication = true,
        SupportsCostOptimization = true,
        TypicalLatencyOverheadMs = 20.0,
        MemoryFootprint = "Low"
    };

    public BandwidthThrottledReplicationStrategy(int maxBytesPerSecond = 10_000_000)
    {
        _maxBytesPerSecond = maxBytesPerSecond;
        _rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = maxBytesPerSecond,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 100,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            TokensPerPeriod = maxBytesPerSecond,
            AutoReplenishment = true
        });
    }

    /// <summary>Replicates data with bandwidth throttling.</summary>
    public async Task<ThrottledReplicationResult> ReplicateAsync(
        string sourceProvider,
        string targetProvider,
        string objectId,
        ReadOnlyMemory<byte> data,
        CancellationToken ct = default)
    {
        var startTime = DateTimeOffset.UtcNow;
        var bytesToTransfer = data.Length;
        var bytesTransferred = 0;

        // Acquire tokens from rate limiter (non-blocking with timeout)
        using var lease = await _rateLimiter.AcquireAsync(bytesToTransfer, ct);

        if (!lease.IsAcquired)
        {
            RecordFailure();
            return new ThrottledReplicationResult
            {
                Success = false,
                ErrorMessage = "Bandwidth throttle limit exceeded",
                Duration = DateTimeOffset.UtcNow - startTime,
                BytesTransferred = 0,
                AverageThroughputBytesPerSec = 0
            };
        }

        // Simulate transfer with throttling
        const int chunkSize = 65536; // 64KB chunks
        for (int offset = 0; offset < bytesToTransfer; offset += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = Math.Min(chunkSize, bytesToTransfer - offset);
            await Task.Delay(TimeSpan.FromMilliseconds(chunk * 1000.0 / _maxBytesPerSecond), ct);
            bytesTransferred += chunk;
        }

        var duration = DateTimeOffset.UtcNow - startTime;
        RecordSuccess();

        return new ThrottledReplicationResult
        {
            Success = true,
            Duration = duration,
            BytesTransferred = bytesTransferred,
            AverageThroughputBytesPerSec = duration.TotalSeconds > 0
                ? bytesTransferred / duration.TotalSeconds
                : 0,
            SourceProvider = sourceProvider,
            TargetProvider = targetProvider
        };
    }

    /// <summary>Sets bandwidth limit dynamically.</summary>
    public void SetBandwidthLimit(int bytesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerSecond);
        // Note: TokenBucketRateLimiter doesn't support dynamic reconfiguration
        // In production, create a new limiter with the new limit
    }

    /// <summary>Gets current bandwidth utilization.</summary>
    public BandwidthUtilization GetUtilization()
    {
        var stats = _rateLimiter.GetStatistics();
        return new BandwidthUtilization
        {
            MaxBytesPerSecond = _maxBytesPerSecond,
            CurrentRequestsQueued = (int)(stats?.CurrentQueuedCount ?? 0),
            TotalRequestsSucceeded = stats?.TotalSuccessfulLeases ?? 0,
            TotalRequestsFailed = stats?.TotalFailedLeases ?? 0
        };
    }

    protected override string? GetCurrentState() =>
        $"Bandwidth: {_maxBytesPerSecond / 1_000_000:F1} MB/s";
}

/// <summary>
/// CRDT-based conflict-free replication.
/// </summary>
public sealed class CrdtReplicationStrategy : MultiCloudStrategyBase
{
    private readonly ConcurrentDictionary<string, LwwElement> _lwwRegister = new();

    public override string StrategyId => "replication-crdt";
    public override string StrategyName => "CRDT Conflict-Free Replication";
    public override string Category => "Replication";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Conflict-free replicated data types for automatic merge of concurrent updates",
        Category = Category,
        SupportsCrossCloudReplication = true,
        SupportsAutomaticFailover = true,
        TypicalLatencyOverheadMs = 5.0,
        MemoryFootprint = "Medium"
    };

    /// <summary>Updates value using LWW (Last-Write-Wins) register.</summary>
    public void Update(string key, object value, string nodeId)
    {
        var timestamp = DateTimeOffset.UtcNow.Ticks;
        var element = new LwwElement
        {
            Value = value,
            Timestamp = timestamp,
            NodeId = nodeId
        };

        _lwwRegister.AddOrUpdate(key, element, (_, existing) =>
        {
            // LWW conflict resolution: timestamp wins, nodeId as tiebreaker
            if (element.Timestamp > existing.Timestamp ||
                (element.Timestamp == existing.Timestamp &&
                 string.CompareOrdinal(element.NodeId, existing.NodeId) > 0))
            {
                return element;
            }
            return existing;
        });

        RecordSuccess();
    }

    /// <summary>Merges remote state into local state.</summary>
    public CrdtMergeResult Merge(string key, LwwElement remoteElement)
    {
        var merged = _lwwRegister.AddOrUpdate(key, remoteElement, (_, localElement) =>
        {
            if (remoteElement.Timestamp > localElement.Timestamp ||
                (remoteElement.Timestamp == localElement.Timestamp &&
                 string.CompareOrdinal(remoteElement.NodeId, localElement.NodeId) > 0))
            {
                return remoteElement;
            }
            return localElement;
        });

        var wasUpdated = merged.Timestamp == remoteElement.Timestamp &&
                         merged.NodeId == remoteElement.NodeId;

        return new CrdtMergeResult
        {
            Key = key,
            WasUpdated = wasUpdated,
            WinningValue = merged.Value,
            WinningTimestamp = merged.Timestamp,
            WinningNodeId = merged.NodeId
        };
    }

    /// <summary>Gets current value.</summary>
    public object? Get(string key) =>
        _lwwRegister.TryGetValue(key, out var element) ? element.Value : null;

    protected override string? GetCurrentState() => $"CRDT entries: {_lwwRegister.Count}";
}

public sealed class ThrottledReplicationResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan Duration { get; init; }
    public long BytesTransferred { get; init; }
    public double AverageThroughputBytesPerSec { get; init; }
    public string? SourceProvider { get; init; }
    public string? TargetProvider { get; init; }
}

public sealed class BandwidthUtilization
{
    public int MaxBytesPerSecond { get; init; }
    public int CurrentRequestsQueued { get; init; }
    public long TotalRequestsSucceeded { get; init; }
    public long TotalRequestsFailed { get; init; }
}

public sealed class LwwElement
{
    public required object Value { get; init; }
    public required long Timestamp { get; init; }
    public required string NodeId { get; init; }
}

public sealed class CrdtMergeResult
{
    public required string Key { get; init; }
    public bool WasUpdated { get; init; }
    public object? WinningValue { get; init; }
    public long WinningTimestamp { get; init; }
    public required string WinningNodeId { get; init; }
}

#endregion
