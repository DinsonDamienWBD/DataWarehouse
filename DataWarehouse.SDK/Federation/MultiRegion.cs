using System.Collections.Concurrent;

namespace DataWarehouse.SDK.Federation;

// ============================================================================
// SCENARIO 5: Multi-Region Federation
// Extends: GeoDistributedConsensus, QuorumReplicator, RoutingTable
// ============================================================================

#region Region Management

/// <summary>
/// Represents a geographic region in the federation.
/// </summary>
public sealed class FederationRegion
{
    public string RegionId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Continent { get; init; } = string.Empty;
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public List<string> AvailabilityZones { get; init; } = new();
    public RegionStatus Status { get; set; } = RegionStatus.Active;
    public int Priority { get; init; } = 1;
    public bool IsPrimary { get; set; }
}

/// <summary>
/// Manages multi-region federation with latency awareness.
/// </summary>
public sealed class MultiRegionFederation : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, FederationRegion> _regions = new();
    private readonly ConcurrentDictionary<(string, string), double> _latencyMatrix = new();
    private readonly ConcurrentDictionary<string, List<string>> _regionNodes = new();
    private readonly Timer _healthCheckTimer;
    private readonly MultiRegionConfig _config;
    private string? _localRegionId;
    private volatile bool _disposed;

    public event EventHandler<RegionEventArgs>? RegionAdded;
    public event EventHandler<RegionEventArgs>? RegionRemoved;
    public event EventHandler<RegionFailoverEventArgs>? RegionFailover;

    public MultiRegionFederation(MultiRegionConfig? config = null)
    {
        _config = config ?? new MultiRegionConfig();
        _healthCheckTimer = new Timer(CheckRegionHealth, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Registers a region in the federation.
    /// </summary>
    public void RegisterRegion(FederationRegion region)
    {
        _regions[region.RegionId] = region;
        _regionNodes[region.RegionId] = new List<string>();
        RegionAdded?.Invoke(this, new RegionEventArgs { Region = region });
    }

    /// <summary>
    /// Sets the local region for this node.
    /// </summary>
    public void SetLocalRegion(string regionId)
    {
        if (_regions.ContainsKey(regionId))
            _localRegionId = regionId;
    }

    /// <summary>
    /// Registers a node in a region.
    /// </summary>
    public void RegisterNode(string nodeId, string regionId)
    {
        if (_regionNodes.TryGetValue(regionId, out var nodes))
        {
            lock (nodes)
            {
                if (!nodes.Contains(nodeId))
                    nodes.Add(nodeId);
            }
        }
    }

    /// <summary>
    /// Updates latency between two regions.
    /// </summary>
    public void UpdateLatency(string fromRegion, string toRegion, double latencyMs)
    {
        _latencyMatrix[(fromRegion, toRegion)] = latencyMs;
        _latencyMatrix[(toRegion, fromRegion)] = latencyMs; // Assume symmetric
    }

    /// <summary>
    /// Gets latency between two regions.
    /// </summary>
    public double GetLatency(string fromRegion, string toRegion)
    {
        if (fromRegion == toRegion) return 0;
        return _latencyMatrix.TryGetValue((fromRegion, toRegion), out var latency) ? latency : double.MaxValue;
    }

    /// <summary>
    /// Gets optimal region for data placement based on latency and policy.
    /// </summary>
    public RegionPlacementResult GetOptimalRegion(RegionPlacementRequest request)
    {
        var candidates = _regions.Values
            .Where(r => r.Status == RegionStatus.Active)
            .Where(r => !request.ExcludeRegions?.Contains(r.RegionId) ?? true)
            .ToList();

        if (request.Policy == PlacementPolicy.ClosestRegion && _localRegionId != null)
        {
            candidates = candidates
                .OrderBy(r => GetLatency(_localRegionId, r.RegionId))
                .ToList();
        }
        else if (request.Policy == PlacementPolicy.PreferPrimary)
        {
            candidates = candidates.OrderByDescending(r => r.IsPrimary).ThenBy(r => r.Priority).ToList();
        }
        else if (request.Policy == PlacementPolicy.LeastLoaded)
        {
            candidates = candidates.OrderBy(r => _regionNodes.GetValueOrDefault(r.RegionId)?.Count ?? 0).ToList();
        }

        var selected = candidates.FirstOrDefault();
        return new RegionPlacementResult
        {
            Success = selected != null,
            SelectedRegion = selected?.RegionId,
            LatencyMs = selected != null && _localRegionId != null ? GetLatency(_localRegionId, selected.RegionId) : 0,
            AlternateRegions = candidates.Skip(1).Take(3).Select(r => r.RegionId).ToList()
        };
    }

    /// <summary>
    /// Gets all active regions.
    /// </summary>
    public IReadOnlyList<FederationRegion> GetActiveRegions() =>
        _regions.Values.Where(r => r.Status == RegionStatus.Active).ToList();

    /// <summary>
    /// Gets region by ID.
    /// </summary>
    public FederationRegion? GetRegion(string regionId) =>
        _regions.TryGetValue(regionId, out var region) ? region : null;

    /// <summary>
    /// Initiates failover from one region to another.
    /// </summary>
    public async Task<FailoverResult> InitiateFailoverAsync(string fromRegion, string toRegion, CancellationToken ct = default)
    {
        if (!_regions.TryGetValue(fromRegion, out var source) || !_regions.TryGetValue(toRegion, out var target))
            return new FailoverResult { Success = false, Error = "Region not found" };

        source.Status = RegionStatus.Failing;
        target.IsPrimary = source.IsPrimary;
        source.IsPrimary = false;

        // Migrate data (simulated)
        await Task.Delay(100, ct);

        source.Status = RegionStatus.Inactive;

        RegionFailover?.Invoke(this, new RegionFailoverEventArgs
        {
            FromRegion = fromRegion,
            ToRegion = toRegion,
            Reason = "Manual failover"
        });

        return new FailoverResult { Success = true, NewPrimaryRegion = toRegion };
    }

    private void CheckRegionHealth(object? state)
    {
        if (_disposed) return;

        foreach (var region in _regions.Values.Where(r => r.Status == RegionStatus.Active))
        {
            var nodes = _regionNodes.GetValueOrDefault(region.RegionId);
            if (nodes?.Count == 0 && !region.IsPrimary)
            {
                region.Status = RegionStatus.Degraded;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _healthCheckTimer.Dispose();
        return ValueTask.CompletedTask;
    }
}

#endregion

#region Cross-Region Replication

/// <summary>
/// Manages asynchronous cross-region replication.
/// </summary>
public sealed class CrossRegionReplicator : IAsyncDisposable
{
    private readonly ConcurrentQueue<ReplicationTask> _replicationQueue = new();
    private readonly ConcurrentDictionary<string, ReplicationState> _replicationStates = new();
    private readonly MultiRegionFederation _federation;
    private readonly Timer _replicationTimer;
    private readonly CrossRegionReplicationConfig _config;
    private volatile bool _disposed;

    public event EventHandler<ReplicationEventArgs>? ReplicationCompleted;
    public event EventHandler<ReplicationEventArgs>? ReplicationFailed;

    public CrossRegionReplicator(MultiRegionFederation federation, CrossRegionReplicationConfig? config = null)
    {
        _federation = federation;
        _config = config ?? new CrossRegionReplicationConfig();
        _replicationTimer = new Timer(ProcessReplicationQueue, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Queues data for cross-region replication.
    /// </summary>
    public void QueueReplication(ReplicationTask task)
    {
        _replicationQueue.Enqueue(task);
    }

    /// <summary>
    /// Replicates an object to all target regions.
    /// </summary>
    public async Task<ReplicationResult> ReplicateAsync(string objectId, byte[] data, IEnumerable<string> targetRegions, CancellationToken ct = default)
    {
        var results = new List<RegionReplicationResult>();

        foreach (var region in targetRegions)
        {
            var state = _replicationStates.GetOrAdd(objectId, _ => new ReplicationState { ObjectId = objectId });

            try
            {
                // Simulate network transfer with latency
                var sourceRegion = _federation.GetActiveRegions().FirstOrDefault(r => r.IsPrimary)?.RegionId;
                var latency = sourceRegion != null ? _federation.GetLatency(sourceRegion, region) : 50;
                await Task.Delay((int)Math.Min(latency, 1000), ct);

                state.ReplicatedRegions.Add(region);
                state.LastReplicatedAt = DateTime.UtcNow;

                results.Add(new RegionReplicationResult { Region = region, Success = true });
            }
            catch (Exception ex)
            {
                results.Add(new RegionReplicationResult { Region = region, Success = false, Error = ex.Message });
            }
        }

        var overallSuccess = results.All(r => r.Success);
        var eventArgs = new ReplicationEventArgs { ObjectId = objectId, Results = results };

        if (overallSuccess)
            ReplicationCompleted?.Invoke(this, eventArgs);
        else
            ReplicationFailed?.Invoke(this, eventArgs);

        return new ReplicationResult { Success = overallSuccess, RegionResults = results };
    }

    /// <summary>
    /// Gets replication state for an object.
    /// </summary>
    public ReplicationState? GetReplicationState(string objectId) =>
        _replicationStates.TryGetValue(objectId, out var state) ? state : null;

    /// <summary>
    /// Gets replication lag for a region.
    /// </summary>
    public TimeSpan GetReplicationLag(string objectId, string region)
    {
        if (!_replicationStates.TryGetValue(objectId, out var state))
            return TimeSpan.MaxValue;

        if (state.ReplicatedRegions.Contains(region))
            return TimeSpan.Zero;

        return DateTime.UtcNow - state.LastReplicatedAt;
    }

    private void ProcessReplicationQueue(object? state)
    {
        if (_disposed) return;

        while (_replicationQueue.TryDequeue(out var task))
        {
            _ = ReplicateAsync(task.ObjectId, task.Data, task.TargetRegions, CancellationToken.None);
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _replicationTimer.Dispose();
        return ValueTask.CompletedTask;
    }
}

#endregion

#region Regional Failover Orchestration

/// <summary>
/// Orchestrates regional failover operations.
/// </summary>
public sealed class RegionalFailoverOrchestrator
{
    private readonly MultiRegionFederation _federation;
    private readonly ConcurrentDictionary<string, FailoverPlan> _failoverPlans = new();

    public RegionalFailoverOrchestrator(MultiRegionFederation federation)
    {
        _federation = federation;
    }

    /// <summary>
    /// Creates a failover plan for a region.
    /// </summary>
    public FailoverPlan CreateFailoverPlan(string sourceRegion)
    {
        var activeRegions = _federation.GetActiveRegions()
            .Where(r => r.RegionId != sourceRegion)
            .OrderBy(r => r.Priority)
            .ToList();

        var plan = new FailoverPlan
        {
            SourceRegion = sourceRegion,
            PrimaryTarget = activeRegions.FirstOrDefault()?.RegionId ?? "",
            SecondaryTargets = activeRegions.Skip(1).Take(2).Select(r => r.RegionId).ToList(),
            CreatedAt = DateTime.UtcNow,
            Status = FailoverPlanStatus.Ready
        };

        _failoverPlans[sourceRegion] = plan;
        return plan;
    }

    /// <summary>
    /// Executes a failover plan.
    /// </summary>
    public async Task<FailoverExecutionResult> ExecuteFailoverAsync(string sourceRegion, CancellationToken ct = default)
    {
        if (!_failoverPlans.TryGetValue(sourceRegion, out var plan))
        {
            plan = CreateFailoverPlan(sourceRegion);
        }

        if (string.IsNullOrEmpty(plan.PrimaryTarget))
            return new FailoverExecutionResult { Success = false, Error = "No failover target available" };

        plan.Status = FailoverPlanStatus.Executing;
        plan.ExecutionStartedAt = DateTime.UtcNow;

        try
        {
            // Step 1: Notify all nodes of impending failover
            await Task.Delay(50, ct);

            // Step 2: Drain connections from source region
            await Task.Delay(100, ct);

            // Step 3: Promote target region
            var result = await _federation.InitiateFailoverAsync(sourceRegion, plan.PrimaryTarget, ct);
            if (!result.Success)
                throw new Exception(result.Error);

            // Step 4: Update routing tables
            await Task.Delay(50, ct);

            plan.Status = FailoverPlanStatus.Completed;
            plan.ExecutionCompletedAt = DateTime.UtcNow;

            return new FailoverExecutionResult
            {
                Success = true,
                NewPrimaryRegion = plan.PrimaryTarget,
                Duration = plan.ExecutionCompletedAt.Value - plan.ExecutionStartedAt!.Value
            };
        }
        catch (Exception ex)
        {
            plan.Status = FailoverPlanStatus.Failed;
            return new FailoverExecutionResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Gets failover plan status.
    /// </summary>
    public FailoverPlan? GetFailoverPlan(string sourceRegion) =>
        _failoverPlans.TryGetValue(sourceRegion, out var plan) ? plan : null;
}

#endregion

#region Types

public sealed class MultiRegionConfig
{
    public int MaxRegions { get; set; } = 10;
    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan FailoverTimeout { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed class CrossRegionReplicationConfig
{
    public int MaxConcurrentReplications { get; set; } = 10;
    public TimeSpan ReplicationTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public bool AsyncReplication { get; set; } = true;
}

public enum RegionStatus { Active, Degraded, Failing, Inactive, Maintenance }
public enum PlacementPolicy { ClosestRegion, PreferPrimary, LeastLoaded, RoundRobin }

public sealed class RegionPlacementRequest
{
    public PlacementPolicy Policy { get; init; } = PlacementPolicy.ClosestRegion;
    public List<string>? ExcludeRegions { get; init; }
    public int MinReplicas { get; init; } = 1;
}

public sealed class RegionPlacementResult
{
    public bool Success { get; init; }
    public string? SelectedRegion { get; init; }
    public double LatencyMs { get; init; }
    public List<string> AlternateRegions { get; init; } = new();
}

public sealed class RegionEventArgs : EventArgs
{
    public FederationRegion Region { get; init; } = null!;
}

public sealed class RegionFailoverEventArgs : EventArgs
{
    public string FromRegion { get; init; } = string.Empty;
    public string ToRegion { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed class FailoverResult
{
    public bool Success { get; init; }
    public string? NewPrimaryRegion { get; init; }
    public string? Error { get; init; }
}

public sealed class ReplicationTask
{
    public string ObjectId { get; init; } = string.Empty;
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public List<string> TargetRegions { get; init; } = new();
}

public sealed class ReplicationState
{
    public string ObjectId { get; init; } = string.Empty;
    public HashSet<string> ReplicatedRegions { get; } = new();
    public DateTime LastReplicatedAt { get; set; }
}

public sealed class ReplicationResult
{
    public bool Success { get; init; }
    public List<RegionReplicationResult> RegionResults { get; init; } = new();
}

public sealed class RegionReplicationResult
{
    public string Region { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string? Error { get; init; }
}

public sealed class ReplicationEventArgs : EventArgs
{
    public string ObjectId { get; init; } = string.Empty;
    public List<RegionReplicationResult> Results { get; init; } = new();
}

public sealed class FailoverPlan
{
    public string SourceRegion { get; init; } = string.Empty;
    public string PrimaryTarget { get; init; } = string.Empty;
    public List<string> SecondaryTargets { get; init; } = new();
    public DateTime CreatedAt { get; init; }
    public DateTime? ExecutionStartedAt { get; set; }
    public DateTime? ExecutionCompletedAt { get; set; }
    public FailoverPlanStatus Status { get; set; }
}

public enum FailoverPlanStatus { Ready, Executing, Completed, Failed }

public sealed class FailoverExecutionResult
{
    public bool Success { get; init; }
    public string? NewPrimaryRegion { get; init; }
    public TimeSpan Duration { get; init; }
    public string? Error { get; init; }
}

#endregion
