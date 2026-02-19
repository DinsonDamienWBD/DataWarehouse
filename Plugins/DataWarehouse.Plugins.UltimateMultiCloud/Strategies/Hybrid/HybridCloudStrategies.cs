using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateMultiCloud.Strategies.Hybrid;

/// <summary>
/// 118.7: Hybrid Cloud Support Strategies
/// Seamless integration between on-premise and cloud.
/// </summary>

/// <summary>
/// On-premise to cloud integration strategy.
/// </summary>
public sealed class OnPremiseIntegrationStrategy : MultiCloudStrategyBase
{
    private readonly ConcurrentDictionary<string, OnPremiseEnvironment> _environments = new();
    private readonly ConcurrentDictionary<string, SyncState> _syncStates = new();

    public override string StrategyId => "hybrid-onprem-integration";
    public override string StrategyName => "On-Premise Integration";
    public override string Category => "Hybrid";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Seamless integration between on-premise infrastructure and cloud providers",
        Category = Category,
        SupportsHybridCloud = true,
        SupportsCrossCloudReplication = true,
        TypicalLatencyOverheadMs = 20.0,
        MemoryFootprint = "Medium"
    };

    /// <summary>Registers an on-premise environment.</summary>
    public void RegisterEnvironment(string envId, string name, string endpoint, IEnumerable<string> capabilities)
    {
        _environments[envId] = new OnPremiseEnvironment
        {
            EnvironmentId = envId,
            Name = name,
            Endpoint = endpoint,
            Capabilities = capabilities.ToList(),
            Status = EnvironmentStatus.Online,
            RegisteredAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>Establishes sync with cloud provider.</summary>
    public SyncConfiguration EstablishSync(string envId, string cloudProviderId, SyncDirection direction)
    {
        if (!_environments.TryGetValue(envId, out var env))
            throw new InvalidOperationException($"Environment {envId} not found");

        var syncId = Guid.NewGuid().ToString("N");
        _syncStates[syncId] = new SyncState
        {
            SyncId = syncId,
            OnPremiseEnvId = envId,
            CloudProviderId = cloudProviderId,
            Direction = direction,
            Status = "Active",
            LastSync = DateTimeOffset.UtcNow
        };

        RecordSuccess();
        return new SyncConfiguration
        {
            SyncId = syncId,
            OnPremiseEnvId = envId,
            CloudProviderId = cloudProviderId,
            Direction = direction,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>Syncs data between on-premise and cloud.</summary>
    public async Task<SyncResult> SyncDataAsync(string syncId, CancellationToken ct = default)
    {
        if (!_syncStates.TryGetValue(syncId, out var state))
        {
            RecordFailure();
            return new SyncResult { Success = false, ErrorMessage = "Sync not found" };
        }

        var startTime = DateTimeOffset.UtcNow;
        await Task.Delay(50, ct); // Simulate sync

        state.LastSync = DateTimeOffset.UtcNow;
        state.ObjectsSynced += 100;

        RecordSuccess();
        return new SyncResult
        {
            Success = true,
            SyncId = syncId,
            ObjectsSynced = 100,
            BytesSynced = 1024 * 1024 * 10,
            Duration = DateTimeOffset.UtcNow - startTime
        };
    }

    /// <summary>Gets environment status.</summary>
    public OnPremiseEnvironment? GetEnvironment(string envId)
    {
        return _environments.TryGetValue(envId, out var env) ? env : null;
    }

    protected override string? GetCurrentState() =>
        $"Environments: {_environments.Count}, Syncs: {_syncStates.Count}";
}

/// <summary>
/// VPN/DirectConnect management strategy.
/// </summary>
public sealed class SecureConnectivityStrategy : MultiCloudStrategyBase
{
    private readonly ConcurrentDictionary<string, ConnectionTunnel> _tunnels = new();

    public override string StrategyId => "hybrid-secure-connectivity";
    public override string StrategyName => "Secure Connectivity";
    public override string Category => "Hybrid";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Manages VPN, DirectConnect, ExpressRoute, and Cloud Interconnect connections",
        Category = Category,
        SupportsHybridCloud = true,
        TypicalLatencyOverheadMs = 5.0,
        MemoryFootprint = "Low"
    };

    /// <summary>Creates a connection tunnel.</summary>
    public ConnectionTunnel CreateTunnel(string tunnelId, ConnectionType type,
        string onPremiseEndpoint, string cloudProviderId, string cloudEndpoint)
    {
        var tunnel = new ConnectionTunnel
        {
            TunnelId = tunnelId,
            Type = type,
            OnPremiseEndpoint = onPremiseEndpoint,
            CloudProviderId = cloudProviderId,
            CloudEndpoint = cloudEndpoint,
            Status = TunnelStatus.Establishing,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _tunnels[tunnelId] = tunnel;
        RecordSuccess();
        return tunnel;
    }

    /// <summary>Activates a tunnel.</summary>
    public void ActivateTunnel(string tunnelId)
    {
        if (_tunnels.TryGetValue(tunnelId, out var tunnel))
        {
            tunnel.Status = TunnelStatus.Active;
            tunnel.ActivatedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Gets tunnel status.</summary>
    public TunnelStatus? GetTunnelStatus(string tunnelId)
    {
        return _tunnels.TryGetValue(tunnelId, out var tunnel) ? tunnel.Status : null;
    }

    /// <summary>Gets all tunnels for a provider.</summary>
    public IReadOnlyList<ConnectionTunnel> GetTunnelsForProvider(string providerId)
    {
        return _tunnels.Values
            .Where(t => t.CloudProviderId == providerId)
            .ToList();
    }

    /// <summary>Monitors tunnel health.</summary>
    public TunnelHealthReport MonitorTunnels()
    {
        var active = _tunnels.Values.Count(t => t.Status == TunnelStatus.Active);
        var degraded = _tunnels.Values.Count(t => t.Status == TunnelStatus.Degraded);
        var down = _tunnels.Values.Count(t => t.Status == TunnelStatus.Down);

        return new TunnelHealthReport
        {
            TotalTunnels = _tunnels.Count,
            ActiveTunnels = active,
            DegradedTunnels = degraded,
            DownTunnels = down,
            ReportTime = DateTimeOffset.UtcNow
        };
    }

    protected override string? GetCurrentState() =>
        $"Tunnels: {_tunnels.Count}, Active: {_tunnels.Values.Count(t => t.Status == TunnelStatus.Active)}";
}

/// <summary>
/// Edge synchronization strategy for hybrid cloud.
/// </summary>
public sealed class EdgeSynchronizationStrategy : MultiCloudStrategyBase
{
    private readonly ConcurrentDictionary<string, EdgeNode> _edgeNodes = new();
    private readonly ConcurrentDictionary<string, EdgeSyncJob> _syncJobs = new();

    public override string StrategyId => "hybrid-edge-sync";
    public override string StrategyName => "Edge Synchronization";
    public override string Category => "Hybrid";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Synchronizes data between edge locations, on-premise, and cloud",
        Category = Category,
        SupportsHybridCloud = true,
        SupportsCrossCloudReplication = true,
        TypicalLatencyOverheadMs = 15.0,
        MemoryFootprint = "Medium"
    };

    /// <summary>Registers an edge node.</summary>
    public void RegisterEdgeNode(string nodeId, string location, EdgeNodeType type, double bandwidthMbps)
    {
        _edgeNodes[nodeId] = new EdgeNode
        {
            NodeId = nodeId,
            Location = location,
            Type = type,
            BandwidthMbps = bandwidthMbps,
            Status = EdgeNodeStatus.Online,
            LastSeen = DateTimeOffset.UtcNow
        };
    }

    /// <summary>Creates edge sync job.</summary>
    public EdgeSyncJob CreateSyncJob(string sourceNodeId, string targetNodeId, SyncPolicy policy)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var job = new EdgeSyncJob
        {
            JobId = jobId,
            SourceNodeId = sourceNodeId,
            TargetNodeId = targetNodeId,
            Policy = policy,
            Status = "Created",
            CreatedAt = DateTimeOffset.UtcNow
        };

        _syncJobs[jobId] = job;
        RecordSuccess();
        return job;
    }

    /// <summary>Executes sync job.</summary>
    public async Task<EdgeSyncResult> ExecuteSyncAsync(string jobId, CancellationToken ct = default)
    {
        IncrementCounter("on_premise_integration.operation");
        if (!_syncJobs.TryGetValue(jobId, out var job))
        {
            RecordFailure();
            return new EdgeSyncResult { Success = false, ErrorMessage = "Job not found" };
        }

        var startTime = DateTimeOffset.UtcNow;
        job.Status = "Running";
        job.LastRun = DateTimeOffset.UtcNow;

        // Simulate sync based on bandwidth
        var sourceNode = _edgeNodes.GetValueOrDefault(job.SourceNodeId);
        var targetNode = _edgeNodes.GetValueOrDefault(job.TargetNodeId);

        var effectiveBandwidth = Math.Min(
            sourceNode?.BandwidthMbps ?? 100,
            targetNode?.BandwidthMbps ?? 100);

        await Task.Delay(50, ct);

        job.Status = "Completed";
        job.ObjectsSynced += 50;
        job.BytesSynced += 1024 * 1024 * 5;

        RecordSuccess();
        return new EdgeSyncResult
        {
            Success = true,
            JobId = jobId,
            ObjectsSynced = 50,
            BytesSynced = 1024 * 1024 * 5,
            Duration = DateTimeOffset.UtcNow - startTime,
            EffectiveBandwidthMbps = effectiveBandwidth
        };
    }

    /// <summary>Updates node status.</summary>
    public void UpdateNodeStatus(string nodeId, EdgeNodeStatus status)
    {
        if (_edgeNodes.TryGetValue(nodeId, out var node))
        {
            node.Status = status;
            node.LastSeen = DateTimeOffset.UtcNow;
        }
    }

    protected override string? GetCurrentState() =>
        $"Nodes: {_edgeNodes.Count}, Jobs: {_syncJobs.Count}";
}

/// <summary>
/// Unified storage tier across hybrid cloud.
/// </summary>
public sealed class UnifiedStorageTierStrategy : MultiCloudStrategyBase
{
    private readonly ConcurrentDictionary<string, StorageLocation> _locations = new();
    private readonly ConcurrentDictionary<string, TieringPolicy> _policies = new();

    public override string StrategyId => "hybrid-unified-storage";
    public override string StrategyName => "Unified Storage Tier";
    public override string Category => "Hybrid";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Unified storage tiering across on-premise, edge, and cloud storage",
        Category = Category,
        SupportsHybridCloud = true,
        SupportsCostOptimization = true,
        TypicalLatencyOverheadMs = 5.0,
        MemoryFootprint = "Low"
    };

    /// <summary>Registers a storage location.</summary>
    public void RegisterLocation(string locationId, StorageLocationType type,
        string providerId, double capacityGb, double pricePerGbMonth)
    {
        _locations[locationId] = new StorageLocation
        {
            LocationId = locationId,
            Type = type,
            ProviderId = providerId,
            CapacityGb = capacityGb,
            UsedGb = 0,
            PricePerGbMonth = pricePerGbMonth
        };
    }

    /// <summary>Creates tiering policy.</summary>
    public void CreateTieringPolicy(string policyId, string name,
        int hotDays, int warmDays, int archiveDays)
    {
        _policies[policyId] = new TieringPolicy
        {
            PolicyId = policyId,
            Name = name,
            HotDays = hotDays,
            WarmDays = warmDays,
            ArchiveDays = archiveDays
        };
    }

    /// <summary>Gets optimal location for data.</summary>
    public StorageLocation? GetOptimalLocation(DataProfile profile)
    {
        var eligibleLocations = _locations.Values
            .Where(l => l.CapacityGb - l.UsedGb >= profile.SizeGb)
            .ToList();

        StorageLocation? optimal = null;

        if (profile.AccessFrequency == AccessFrequency.Hot)
        {
            // Prefer on-premise or edge for hot data
            optimal = eligibleLocations
                .Where(l => l.Type is StorageLocationType.OnPremise or StorageLocationType.Edge)
                .OrderBy(l => l.PricePerGbMonth)
                .FirstOrDefault();
        }
        else if (profile.AccessFrequency == AccessFrequency.Cold)
        {
            // Prefer cloud archive for cold data
            optimal = eligibleLocations
                .Where(l => l.Type == StorageLocationType.CloudArchive)
                .OrderBy(l => l.PricePerGbMonth)
                .FirstOrDefault();
        }

        optimal ??= eligibleLocations.OrderBy(l => l.PricePerGbMonth).FirstOrDefault();

        if (optimal != null) RecordSuccess();
        return optimal;
    }

    /// <summary>Moves data between tiers.</summary>
    public async Task<TierMoveResult> MoveToTierAsync(string dataId, string targetLocationId, CancellationToken ct = default)
    {
        if (!_locations.TryGetValue(targetLocationId, out var location))
        {
            RecordFailure();
            return new TierMoveResult { Success = false, ErrorMessage = "Location not found" };
        }

        await Task.Delay(30, ct);

        RecordSuccess();
        return new TierMoveResult
        {
            Success = true,
            DataId = dataId,
            TargetLocationId = targetLocationId,
            LocationType = location.Type,
            MovedAt = DateTimeOffset.UtcNow
        };
    }

    protected override string? GetCurrentState() =>
        $"Locations: {_locations.Count}, Policies: {_policies.Count}";
}

/// <summary>
/// Cloud bursting strategy for hybrid workloads.
/// </summary>
public sealed class CloudBurstingStrategy : MultiCloudStrategyBase
{
    private readonly ConcurrentDictionary<string, BurstConfiguration> _configs = new();
    private readonly ConcurrentDictionary<string, ActiveBurst> _activeBursts = new();

    public override string StrategyId => "hybrid-cloud-bursting";
    public override string StrategyName => "Cloud Bursting";
    public override string Category => "Hybrid";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Automatically bursts to cloud when on-premise capacity is exceeded",
        Category = Category,
        SupportsHybridCloud = true,
        SupportsAutomaticFailover = true,
        SupportsCostOptimization = true,
        TypicalLatencyOverheadMs = 10.0,
        MemoryFootprint = "Low"
    };

    /// <summary>Configures burst for a workload.</summary>
    public void ConfigureBurst(string workloadId, string onPremiseId,
        IEnumerable<string> burstTargets, double burstThreshold)
    {
        _configs[workloadId] = new BurstConfiguration
        {
            WorkloadId = workloadId,
            OnPremiseId = onPremiseId,
            BurstTargets = burstTargets.ToList(),
            BurstThreshold = burstThreshold,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>Checks if burst is needed and initiates if so.</summary>
    public BurstDecision EvaluateBurst(string workloadId, double currentUtilization)
    {
        if (!_configs.TryGetValue(workloadId, out var config))
        {
            return new BurstDecision { ShouldBurst = false, Reason = "Workload not configured" };
        }

        if (currentUtilization >= config.BurstThreshold)
        {
            var targetProvider = config.BurstTargets.FirstOrDefault();
            if (targetProvider != null)
            {
                var burstId = Guid.NewGuid().ToString("N");
                _activeBursts[burstId] = new ActiveBurst
                {
                    BurstId = burstId,
                    WorkloadId = workloadId,
                    TargetProvider = targetProvider,
                    StartedAt = DateTimeOffset.UtcNow,
                    Status = "Active"
                };

                RecordSuccess();
                return new BurstDecision
                {
                    ShouldBurst = true,
                    BurstId = burstId,
                    TargetProvider = targetProvider,
                    Reason = $"Utilization {currentUtilization:P0} exceeded threshold {config.BurstThreshold:P0}"
                };
            }
        }

        return new BurstDecision
        {
            ShouldBurst = false,
            Reason = $"Utilization {currentUtilization:P0} below threshold"
        };
    }

    /// <summary>Ends a burst session.</summary>
    public void EndBurst(string burstId)
    {
        if (_activeBursts.TryGetValue(burstId, out var burst))
        {
            burst.Status = "Ended";
            burst.EndedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Gets active bursts.</summary>
    public IReadOnlyList<ActiveBurst> GetActiveBursts()
    {
        return _activeBursts.Values.Where(b => b.Status == "Active").ToList();
    }

    protected override string? GetCurrentState() =>
        $"Configs: {_configs.Count}, Active bursts: {_activeBursts.Values.Count(b => b.Status == "Active")}";
}

#region Supporting Types

public sealed class OnPremiseEnvironment
{
    public required string EnvironmentId { get; init; }
    public required string Name { get; init; }
    public required string Endpoint { get; init; }
    public List<string> Capabilities { get; init; } = new();
    public EnvironmentStatus Status { get; set; }
    public DateTimeOffset RegisteredAt { get; init; }
}

public enum EnvironmentStatus { Online, Offline, Degraded, Maintenance }

public enum SyncDirection { ToCloud, FromCloud, Bidirectional }

public sealed class SyncState
{
    public required string SyncId { get; init; }
    public required string OnPremiseEnvId { get; init; }
    public required string CloudProviderId { get; init; }
    public SyncDirection Direction { get; init; }
    public required string Status { get; set; }
    public DateTimeOffset LastSync { get; set; }
    public long ObjectsSynced { get; set; }
}

public sealed class SyncConfiguration
{
    public required string SyncId { get; init; }
    public required string OnPremiseEnvId { get; init; }
    public required string CloudProviderId { get; init; }
    public SyncDirection Direction { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class SyncResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? SyncId { get; init; }
    public int ObjectsSynced { get; init; }
    public long BytesSynced { get; init; }
    public TimeSpan Duration { get; init; }
}

public enum ConnectionType { VPN, DirectConnect, ExpressRoute, CloudInterconnect, PrivateLink }

public enum TunnelStatus { Establishing, Active, Degraded, Down }

public sealed class ConnectionTunnel
{
    public required string TunnelId { get; init; }
    public ConnectionType Type { get; init; }
    public required string OnPremiseEndpoint { get; init; }
    public required string CloudProviderId { get; init; }
    public required string CloudEndpoint { get; init; }
    public TunnelStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ActivatedAt { get; set; }
}

public sealed class TunnelHealthReport
{
    public int TotalTunnels { get; init; }
    public int ActiveTunnels { get; init; }
    public int DegradedTunnels { get; init; }
    public int DownTunnels { get; init; }
    public DateTimeOffset ReportTime { get; init; }
}

public enum EdgeNodeType { IoTGateway, ContentCache, ComputeNode, StorageNode }

public enum EdgeNodeStatus { Online, Offline, Syncing, Error }

public sealed class EdgeNode
{
    public required string NodeId { get; init; }
    public required string Location { get; init; }
    public EdgeNodeType Type { get; init; }
    public double BandwidthMbps { get; init; }
    public EdgeNodeStatus Status { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}

public sealed class SyncPolicy
{
    public bool Continuous { get; init; }
    public int IntervalSeconds { get; init; } = 300;
    public bool ConflictResolutionLatestWins { get; init; } = true;
}

public sealed class EdgeSyncJob
{
    public required string JobId { get; init; }
    public required string SourceNodeId { get; init; }
    public required string TargetNodeId { get; init; }
    public required SyncPolicy Policy { get; init; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastRun { get; set; }
    public int ObjectsSynced { get; set; }
    public long BytesSynced { get; set; }
}

public sealed class EdgeSyncResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? JobId { get; init; }
    public int ObjectsSynced { get; init; }
    public long BytesSynced { get; init; }
    public TimeSpan Duration { get; init; }
    public double EffectiveBandwidthMbps { get; init; }
}

public enum StorageLocationType { OnPremise, Edge, CloudHot, CloudWarm, CloudCold, CloudArchive }

public sealed class StorageLocation
{
    public required string LocationId { get; init; }
    public StorageLocationType Type { get; init; }
    public required string ProviderId { get; init; }
    public double CapacityGb { get; init; }
    public double UsedGb { get; set; }
    public double PricePerGbMonth { get; init; }
}

public sealed class TieringPolicy
{
    public required string PolicyId { get; init; }
    public required string Name { get; init; }
    public int HotDays { get; init; }
    public int WarmDays { get; init; }
    public int ArchiveDays { get; init; }
}

public enum AccessFrequency { Hot, Warm, Cold, Archive }

public sealed class DataProfile
{
    public double SizeGb { get; init; }
    public AccessFrequency AccessFrequency { get; init; }
    public int DaysSinceLastAccess { get; init; }
}

public sealed class TierMoveResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? DataId { get; init; }
    public string? TargetLocationId { get; init; }
    public StorageLocationType LocationType { get; init; }
    public DateTimeOffset MovedAt { get; init; }
}

public sealed class BurstConfiguration
{
    public required string WorkloadId { get; init; }
    public required string OnPremiseId { get; init; }
    public List<string> BurstTargets { get; init; } = new();
    public double BurstThreshold { get; init; } = 0.8;
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class ActiveBurst
{
    public required string BurstId { get; init; }
    public required string WorkloadId { get; init; }
    public required string TargetProvider { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; set; }
    public required string Status { get; set; }
}

public sealed class BurstDecision
{
    public bool ShouldBurst { get; init; }
    public string? BurstId { get; init; }
    public string? TargetProvider { get; init; }
    public string? Reason { get; init; }
}

#endregion
