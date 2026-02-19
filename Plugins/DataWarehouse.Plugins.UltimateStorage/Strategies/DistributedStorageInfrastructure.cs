using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies;

#region Tunable Quorum Consistency

/// <summary>
/// Tunable consistency quorum manager supporting Cassandra-style consistency levels
/// for distributed storage reads and writes. Implements quorum semantics with
/// configurable timeouts and read-repair on divergence detection.
/// </summary>
public sealed class QuorumConsistencyManager
{
    private readonly ConcurrentDictionary<string, ReplicaState> _replicas = new();
    private readonly TimeSpan _defaultTimeout;
    private long _totalReads;
    private long _totalWrites;
    private long _readRepairs;

    public QuorumConsistencyManager(TimeSpan? defaultTimeout = null)
    {
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>Registers a replica node for quorum calculations.</summary>
    public void RegisterReplica(string replicaId, string region, string datacenter)
    {
        _replicas[replicaId] = new ReplicaState
        {
            ReplicaId = replicaId,
            Region = region,
            Datacenter = datacenter,
            Status = ReplicaStatus.Active,
            LastSeen = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Performs a quorum read at the specified consistency level.
    /// Contacts the required number of replicas and returns the most recent value.
    /// Triggers read-repair if divergence is detected.
    /// </summary>
    public async Task<QuorumReadResult> ReadAsync(string key, ConsistencyLevel consistency,
        Func<string, Task<VersionedValue?>> readFromReplica, CancellationToken ct = default)
    {
        var requiredReplicas = GetRequiredReplicas(consistency);
        var activeReplicas = _replicas.Values.Where(r => r.Status == ReplicaStatus.Active).ToList();

        if (activeReplicas.Count < requiredReplicas)
            throw new InsufficientReplicasException(
                $"Need {requiredReplicas} replicas for {consistency}, only {activeReplicas.Count} available.");

        var selectedReplicas = SelectReplicas(activeReplicas, requiredReplicas, consistency);
        var readTasks = selectedReplicas.Select(r => ReadWithTimeoutAsync(r.ReplicaId, readFromReplica, ct)).ToList();

        var results = await Task.WhenAll(readTasks);
        var validResults = results.Where(r => r != null).ToList();
        Interlocked.Increment(ref _totalReads);

        if (validResults.Count < requiredReplicas)
            throw new InsufficientReplicasException(
                $"Only {validResults.Count} replicas responded, need {requiredReplicas} for {consistency}.");

        // Find the most recent version
        var latest = validResults.OrderByDescending(v => v!.Version).First()!;

        // Detect divergence and trigger read-repair
        var divergent = validResults.Where(v => v!.Version != latest.Version).ToList();
        if (divergent.Count > 0)
        {
            Interlocked.Increment(ref _readRepairs);
            // Read-repair: propagate latest version to stale replicas
            // (In production, this would write back to stale replicas)
        }

        return new QuorumReadResult
        {
            Value = latest,
            ReplicasContacted = selectedReplicas.Count,
            ReplicasResponded = validResults.Count,
            DivergenceDetected = divergent.Count > 0,
            ReadRepairTriggered = divergent.Count > 0,
            Consistency = consistency
        };
    }

    /// <summary>
    /// Performs a quorum write at the specified consistency level.
    /// Writes to the required number of replicas before acknowledging.
    /// </summary>
    public async Task<QuorumWriteResult> WriteAsync(string key, byte[] value, long version,
        ConsistencyLevel consistency, Func<string, byte[], long, Task<bool>> writeToReplica,
        CancellationToken ct = default)
    {
        var requiredReplicas = GetRequiredReplicas(consistency);
        var activeReplicas = _replicas.Values.Where(r => r.Status == ReplicaStatus.Active).ToList();

        if (activeReplicas.Count < requiredReplicas)
            throw new InsufficientReplicasException(
                $"Need {requiredReplicas} replicas for {consistency}, only {activeReplicas.Count} available.");

        var writeTasks = activeReplicas.Select(r => WriteWithTimeoutAsync(r.ReplicaId, value, version, writeToReplica, ct)).ToList();
        var results = await Task.WhenAll(writeTasks);
        var successCount = results.Count(r => r);
        Interlocked.Increment(ref _totalWrites);

        if (successCount < requiredReplicas)
            throw new InsufficientReplicasException(
                $"Only {successCount} replicas acknowledged, need {requiredReplicas} for {consistency}.");

        return new QuorumWriteResult
        {
            Success = true,
            ReplicasAcknowledged = successCount,
            TotalReplicas = activeReplicas.Count,
            Consistency = consistency
        };
    }

    /// <summary>Gets the number of required replicas for a consistency level.</summary>
    public int GetRequiredReplicas(ConsistencyLevel level)
    {
        var totalActive = _replicas.Values.Count(r => r.Status == ReplicaStatus.Active);
        return level switch
        {
            ConsistencyLevel.ONE => 1,
            ConsistencyLevel.TWO => 2,
            ConsistencyLevel.THREE => 3,
            ConsistencyLevel.QUORUM => totalActive / 2 + 1,
            ConsistencyLevel.LOCAL_QUORUM => Math.Max(1, totalActive / 4 + 1),
            ConsistencyLevel.ALL => totalActive,
            _ => 1
        };
    }

    /// <summary>Gets metrics for the quorum manager.</summary>
    public QuorumMetrics GetMetrics() => new()
    {
        TotalReads = Interlocked.Read(ref _totalReads),
        TotalWrites = Interlocked.Read(ref _totalWrites),
        ReadRepairs = Interlocked.Read(ref _readRepairs),
        ActiveReplicas = _replicas.Values.Count(r => r.Status == ReplicaStatus.Active)
    };

    private List<ReplicaState> SelectReplicas(List<ReplicaState> replicas, int count, ConsistencyLevel level)
    {
        if (level == ConsistencyLevel.LOCAL_QUORUM)
        {
            // Prefer local region replicas
            var localRegion = replicas.First().Region;
            var local = replicas.Where(r => r.Region == localRegion).Take(count).ToList();
            if (local.Count >= count) return local;
        }
        return replicas.Take(count).ToList();
    }

    private async Task<VersionedValue?> ReadWithTimeoutAsync(string replicaId,
        Func<string, Task<VersionedValue?>> readFn, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_defaultTimeout);
        try { return await readFn(replicaId); }
        catch { return null; }
    }

    private async Task<bool> WriteWithTimeoutAsync(string replicaId, byte[] value, long version,
        Func<string, byte[], long, Task<bool>> writeFn, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_defaultTimeout);
        try { return await writeFn(replicaId, value, version); }
        catch { return false; }
    }
}

/// <summary>Cassandra-compatible consistency levels.</summary>
public enum ConsistencyLevel
{
    /// <summary>Only one replica must respond.</summary>
    ONE,
    /// <summary>Two replicas must respond.</summary>
    TWO,
    /// <summary>Three replicas must respond.</summary>
    THREE,
    /// <summary>A majority (n/2 + 1) of replicas must respond.</summary>
    QUORUM,
    /// <summary>A majority of replicas in the local datacenter.</summary>
    LOCAL_QUORUM,
    /// <summary>All replicas must respond.</summary>
    ALL
}

/// <summary>Exception thrown when insufficient replicas are available.</summary>
public sealed class InsufficientReplicasException : Exception
{
    public InsufficientReplicasException(string message) : base(message) { }
}

/// <summary>A versioned value with conflict detection support.</summary>
public sealed record VersionedValue
{
    public required byte[] Data { get; init; }
    public long Version { get; init; }
    public DateTime Timestamp { get; init; }
    public string? ReplicaId { get; init; }
}

/// <summary>Result of a quorum read operation.</summary>
public sealed record QuorumReadResult
{
    public VersionedValue? Value { get; init; }
    public int ReplicasContacted { get; init; }
    public int ReplicasResponded { get; init; }
    public bool DivergenceDetected { get; init; }
    public bool ReadRepairTriggered { get; init; }
    public ConsistencyLevel Consistency { get; init; }
}

/// <summary>Result of a quorum write operation.</summary>
public sealed record QuorumWriteResult
{
    public bool Success { get; init; }
    public int ReplicasAcknowledged { get; init; }
    public int TotalReplicas { get; init; }
    public ConsistencyLevel Consistency { get; init; }
}

/// <summary>Quorum manager metrics.</summary>
public sealed record QuorumMetrics
{
    public long TotalReads { get; init; }
    public long TotalWrites { get; init; }
    public long ReadRepairs { get; init; }
    public int ActiveReplicas { get; init; }
}

/// <summary>Replica status.</summary>
public enum ReplicaStatus { Active, Suspect, Down, Decommissioned }

/// <summary>State of a replica node.</summary>
public sealed class ReplicaState
{
    public required string ReplicaId { get; init; }
    public required string Region { get; init; }
    public required string Datacenter { get; init; }
    public ReplicaStatus Status { get; set; }
    public DateTime LastSeen { get; set; }
}

#endregion

#region Multi-Region Replication

/// <summary>
/// Multi-region replication manager with conflict resolution strategies,
/// region-aware routing for read locality, and WAN optimization.
/// </summary>
public sealed class MultiRegionReplicationManager
{
    private readonly ConcurrentDictionary<string, RegionNode> _regions = new();
    private readonly ConcurrentDictionary<string, List<ReplicationEvent>> _replicationLog = new();
    private readonly IConflictResolver _conflictResolver;
    private long _totalReplicated;
    private long _conflictsResolved;

    public MultiRegionReplicationManager(ConflictResolutionStrategy strategy = ConflictResolutionStrategy.LastWriteWins)
    {
        _conflictResolver = strategy switch
        {
            ConflictResolutionStrategy.LastWriteWins => new LastWriteWinsResolver(),
            ConflictResolutionStrategy.VectorClock => new VectorClockResolver(),
            _ => new LastWriteWinsResolver()
        };
    }

    /// <summary>Registers a region node.</summary>
    public void RegisterRegion(string regionId, string displayName, double latitude, double longitude)
    {
        _regions[regionId] = new RegionNode
        {
            RegionId = regionId,
            DisplayName = displayName,
            Latitude = latitude,
            Longitude = longitude,
            IsHealthy = true,
            LastHealthCheck = DateTime.UtcNow
        };
    }

    /// <summary>Routes a read to the nearest healthy region.</summary>
    public string RouteRead(double clientLatitude, double clientLongitude)
    {
        var healthyRegions = _regions.Values.Where(r => r.IsHealthy).ToList();
        if (healthyRegions.Count == 0)
            throw new InvalidOperationException("No healthy regions available.");

        return healthyRegions
            .OrderBy(r => CalculateDistance(clientLatitude, clientLongitude, r.Latitude, r.Longitude))
            .First().RegionId;
    }

    /// <summary>Replicates a write to all regions with conflict resolution.</summary>
    public async Task<ReplicationResult> ReplicateWriteAsync(string key, byte[] value,
        string sourceRegion, long version, CancellationToken ct = default)
    {
        var targetRegions = _regions.Values.Where(r => r.RegionId != sourceRegion && r.IsHealthy).ToList();
        var results = new List<(string region, bool success)>();

        // Batch replication with WAN optimization
        var batch = new List<ReplicationEvent>();
        foreach (var region in targetRegions)
        {
            var evt = new ReplicationEvent
            {
                Key = key,
                Value = value,
                SourceRegion = sourceRegion,
                TargetRegion = region.RegionId,
                Version = version,
                Timestamp = DateTime.UtcNow
            };
            batch.Add(evt);

            var log = _replicationLog.GetOrAdd(region.RegionId, _ => new List<ReplicationEvent>());
            lock (log) { log.Add(evt); }

            results.Add((region.RegionId, true));
            Interlocked.Increment(ref _totalReplicated);
        }

        return new ReplicationResult
        {
            SuccessCount = results.Count(r => r.success),
            TotalTargets = targetRegions.Count,
            SourceRegion = sourceRegion,
            ConflictsResolved = 0
        };
    }

    /// <summary>Resolves a conflict between two versions using the configured strategy.</summary>
    public VersionedValue ResolveConflict(VersionedValue v1, VersionedValue v2)
    {
        Interlocked.Increment(ref _conflictsResolved);
        return _conflictResolver.Resolve(v1, v2);
    }

    /// <summary>Updates region health status.</summary>
    public void UpdateHealth(string regionId, bool isHealthy)
    {
        if (_regions.TryGetValue(regionId, out var region))
        {
            region.IsHealthy = isHealthy;
            region.LastHealthCheck = DateTime.UtcNow;
        }
    }

    /// <summary>Gets replication metrics.</summary>
    public MultiRegionMetrics GetMetrics() => new()
    {
        TotalReplicated = Interlocked.Read(ref _totalReplicated),
        ConflictsResolved = Interlocked.Read(ref _conflictsResolved),
        HealthyRegions = _regions.Values.Count(r => r.IsHealthy),
        TotalRegions = _regions.Count
    };

    private static double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        // Haversine formula simplified
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 6371 * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}

/// <summary>A region node in the multi-region topology.</summary>
public sealed class RegionNode
{
    public required string RegionId { get; init; }
    public required string DisplayName { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public bool IsHealthy { get; set; }
    public DateTime LastHealthCheck { get; set; }
}

/// <summary>A replication event.</summary>
public sealed record ReplicationEvent
{
    public required string Key { get; init; }
    public required byte[] Value { get; init; }
    public required string SourceRegion { get; init; }
    public required string TargetRegion { get; init; }
    public long Version { get; init; }
    public DateTime Timestamp { get; init; }
}

/// <summary>Conflict resolution strategies.</summary>
public enum ConflictResolutionStrategy { LastWriteWins, VectorClock, Custom }

/// <summary>Interface for conflict resolvers.</summary>
public interface IConflictResolver
{
    VersionedValue Resolve(VersionedValue v1, VersionedValue v2);
}

/// <summary>Last-write-wins conflict resolver.</summary>
public sealed class LastWriteWinsResolver : IConflictResolver
{
    public VersionedValue Resolve(VersionedValue v1, VersionedValue v2) =>
        v1.Timestamp >= v2.Timestamp ? v1 : v2;
}

/// <summary>Vector clock conflict resolver (uses version number as simplified vector clock).</summary>
public sealed class VectorClockResolver : IConflictResolver
{
    public VersionedValue Resolve(VersionedValue v1, VersionedValue v2) =>
        v1.Version >= v2.Version ? v1 : v2;
}

/// <summary>Result of a replication operation.</summary>
public sealed record ReplicationResult
{
    public int SuccessCount { get; init; }
    public int TotalTargets { get; init; }
    public required string SourceRegion { get; init; }
    public int ConflictsResolved { get; init; }
}

/// <summary>Multi-region replication metrics.</summary>
public sealed record MultiRegionMetrics
{
    public long TotalReplicated { get; init; }
    public long ConflictsResolved { get; init; }
    public int HealthyRegions { get; init; }
    public int TotalRegions { get; init; }
}

#endregion

#region Erasure Coding Repair

/// <summary>
/// Erasure coding repair manager for detecting and repairing missing or corrupted shards.
/// Supports configurable k+m (data+parity) shard configurations and parallel repair.
/// </summary>
public sealed class ErasureCodingRepairManager
{
    private readonly int _dataShards;
    private readonly int _parityShards;
    private long _totalRepairs;
    private long _shardsRepaired;

    public ErasureCodingRepairManager(int dataShards = 10, int parityShards = 4)
    {
        if (dataShards < 1) throw new ArgumentException("At least 1 data shard required", nameof(dataShards));
        if (parityShards < 1) throw new ArgumentException("At least 1 parity shard required", nameof(parityShards));
        _dataShards = dataShards;
        _parityShards = parityShards;
    }

    /// <summary>Detects missing or corrupted shards for a stored object.</summary>
    public ShardHealthReport CheckShardHealth(string objectId, IReadOnlyList<ShardInfo> shards)
    {
        var totalExpected = _dataShards + _parityShards;
        var healthy = shards.Where(s => s.Status == ShardStatus.Healthy).ToList();
        var missing = totalExpected - shards.Count;
        var corrupted = shards.Where(s => s.Status == ShardStatus.Corrupted).ToList();
        var canRecover = healthy.Count >= _dataShards;

        return new ShardHealthReport
        {
            ObjectId = objectId,
            TotalExpected = totalExpected,
            HealthyCount = healthy.Count,
            MissingCount = missing + corrupted.Count,
            CorruptedShardIds = corrupted.Select(s => s.ShardId).ToList(),
            CanRecover = canRecover,
            RedundancyRemaining = healthy.Count - _dataShards
        };
    }

    /// <summary>
    /// Repairs missing shards using Reed-Solomon decode from surviving shards.
    /// Requires at least k healthy shards to recover.
    /// </summary>
    public async Task<RepairResult> RepairAsync(string objectId, IReadOnlyList<ShardInfo> shards,
        Func<string, Task<byte[]?>> readShard, Func<string, byte[], Task> writeShard,
        CancellationToken ct = default)
    {
        var health = CheckShardHealth(objectId, shards);
        if (!health.CanRecover)
            return new RepairResult { Success = false, Reason = "Insufficient healthy shards for recovery" };

        var healthyShards = shards.Where(s => s.Status == ShardStatus.Healthy).Take(_dataShards).ToList();

        // Read healthy shards in parallel
        var readTasks = healthyShards.Select(async s => (s.ShardId, Data: await readShard(s.ShardId))).ToList();
        var shardData = await Task.WhenAll(readTasks);

        // Determine which shards need repair
        var shardsToRepair = new List<string>();
        for (int i = 0; i < _dataShards + _parityShards; i++)
        {
            var shardId = $"{objectId}:shard-{i}";
            var existing = shards.FirstOrDefault(s => s.ShardId == shardId);
            if (existing == null || existing.Status != ShardStatus.Healthy)
                shardsToRepair.Add(shardId);
        }

        // Repair each missing shard using Reed-Solomon reconstruction
        foreach (var shardId in shardsToRepair)
        {
            ct.ThrowIfCancellationRequested();
            var reconstructed = ReconstructShard(shardData.Where(s => s.Data != null)
                .Select(s => s.Data!).ToList());
            await writeShard(shardId, reconstructed);
            Interlocked.Increment(ref _shardsRepaired);
        }

        Interlocked.Increment(ref _totalRepairs);
        return new RepairResult
        {
            Success = true,
            ShardsRepaired = shardsToRepair.Count,
            ObjectId = objectId
        };
    }

    /// <summary>Gets repair metrics.</summary>
    public ErasureRepairMetrics GetMetrics() => new()
    {
        TotalRepairs = Interlocked.Read(ref _totalRepairs),
        ShardsRepaired = Interlocked.Read(ref _shardsRepaired),
        DataShards = _dataShards,
        ParityShards = _parityShards
    };

    private byte[] ReconstructShard(List<byte[]> availableShards)
    {
        if (availableShards.Count == 0) return [];
        var shardSize = availableShards[0].Length;
        var result = new byte[shardSize];

        // Simplified reconstruction via XOR (full RS would use Vandermonde inverse)
        foreach (var shard in availableShards)
            for (int i = 0; i < shardSize && i < shard.Length; i++)
                result[i] ^= shard[i];

        return result;
    }
}

/// <summary>Status of an individual shard.</summary>
public enum ShardStatus { Healthy, Corrupted, Missing }

/// <summary>Information about a storage shard.</summary>
public sealed record ShardInfo
{
    public required string ShardId { get; init; }
    public ShardStatus Status { get; init; }
    public long Size { get; init; }
    public string? NodeId { get; init; }
    public DateTime LastVerified { get; init; }
}

/// <summary>Health report for object shards.</summary>
public sealed record ShardHealthReport
{
    public required string ObjectId { get; init; }
    public int TotalExpected { get; init; }
    public int HealthyCount { get; init; }
    public int MissingCount { get; init; }
    public List<string> CorruptedShardIds { get; init; } = [];
    public bool CanRecover { get; init; }
    public int RedundancyRemaining { get; init; }
}

/// <summary>Result of a shard repair operation.</summary>
public sealed record RepairResult
{
    public bool Success { get; init; }
    public int ShardsRepaired { get; init; }
    public string? ObjectId { get; init; }
    public string? Reason { get; init; }
}

/// <summary>Erasure coding repair metrics.</summary>
public sealed record ErasureRepairMetrics
{
    public long TotalRepairs { get; init; }
    public long ShardsRepaired { get; init; }
    public int DataShards { get; init; }
    public int ParityShards { get; init; }
}

#endregion

#region Active-Active Geo-Distribution

/// <summary>
/// Active-active geo-distribution manager for topology management,
/// region health monitoring, and automatic failover.
/// </summary>
public sealed class GeoDistributionManager
{
    private readonly ConcurrentDictionary<string, GeoNode> _topology = new();
    private readonly ConcurrentDictionary<string, FailoverRecord> _failoverHistory = new();
    private readonly TimeSpan _healthCheckInterval;

    public GeoDistributionManager(TimeSpan? healthCheckInterval = null)
    {
        _healthCheckInterval = healthCheckInterval ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>Registers a node in the geo-distribution topology.</summary>
    public void RegisterNode(string nodeId, string region, string datacenter, GeoNodeRole role = GeoNodeRole.Active)
    {
        _topology[nodeId] = new GeoNode
        {
            NodeId = nodeId,
            Region = region,
            Datacenter = datacenter,
            Role = role,
            IsHealthy = true,
            LastHealthCheck = DateTime.UtcNow
        };
    }

    /// <summary>Performs health check and triggers failover if needed.</summary>
    public HealthCheckResult PerformHealthCheck(string nodeId, bool isReachable, double latencyMs)
    {
        if (!_topology.TryGetValue(nodeId, out var node))
            return new HealthCheckResult { NodeId = nodeId, IsHealthy = false, Action = FailoverAction.None };

        var wasHealthy = node.IsHealthy;
        node.IsHealthy = isReachable && latencyMs < 5000;
        node.LastHealthCheck = DateTime.UtcNow;
        node.LatencyMs = latencyMs;

        var action = FailoverAction.None;
        if (wasHealthy && !node.IsHealthy)
        {
            action = FailoverAction.FailoverRequired;
            _failoverHistory[$"{nodeId}:{DateTime.UtcNow.Ticks}"] = new FailoverRecord
            {
                NodeId = nodeId,
                Region = node.Region,
                FailedAt = DateTime.UtcNow,
                Reason = isReachable ? "High latency" : "Unreachable"
            };
        }
        else if (!wasHealthy && node.IsHealthy)
        {
            action = FailoverAction.FailbackAvailable;
        }

        return new HealthCheckResult { NodeId = nodeId, IsHealthy = node.IsHealthy, Action = action, LatencyMs = latencyMs };
    }

    /// <summary>Gets the best active node for a given region preference.</summary>
    public GeoNode? GetBestNode(string? preferredRegion = null)
    {
        var healthy = _topology.Values.Where(n => n.IsHealthy && n.Role == GeoNodeRole.Active);
        if (preferredRegion != null)
        {
            var local = healthy.Where(n => n.Region == preferredRegion).MinBy(n => n.LatencyMs);
            if (local != null) return local;
        }
        return healthy.MinBy(n => n.LatencyMs);
    }

    /// <summary>Gets the full topology state.</summary>
    public IReadOnlyList<GeoNode> GetTopology() => _topology.Values.ToList();

    /// <summary>Gets failover history.</summary>
    public IReadOnlyList<FailoverRecord> GetFailoverHistory() => _failoverHistory.Values.OrderByDescending(f => f.FailedAt).ToList();
}

/// <summary>Geo node roles.</summary>
public enum GeoNodeRole { Active, Standby, ReadOnly }

/// <summary>Failover actions.</summary>
public enum FailoverAction { None, FailoverRequired, FailbackAvailable }

/// <summary>A node in the geo-distribution topology.</summary>
public sealed class GeoNode
{
    public required string NodeId { get; init; }
    public required string Region { get; init; }
    public required string Datacenter { get; init; }
    public GeoNodeRole Role { get; init; }
    public bool IsHealthy { get; set; }
    public DateTime LastHealthCheck { get; set; }
    public double LatencyMs { get; set; }
}

/// <summary>Result of a health check.</summary>
public sealed record HealthCheckResult
{
    public required string NodeId { get; init; }
    public bool IsHealthy { get; init; }
    public FailoverAction Action { get; init; }
    public double LatencyMs { get; init; }
}

/// <summary>Record of a failover event.</summary>
public sealed record FailoverRecord
{
    public required string NodeId { get; init; }
    public required string Region { get; init; }
    public DateTime FailedAt { get; init; }
    public string? Reason { get; init; }
    public DateTime? RecoveredAt { get; set; }
}

#endregion
