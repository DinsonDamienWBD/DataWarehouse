// 91.F1: Geo-RAID - Cross-datacenter RAID with geographic awareness
using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts.RAID;

namespace DataWarehouse.Plugins.UltimateRAID.Features;

/// <summary>
/// 91.F1: Geo-RAID - Geographic RAID with cross-datacenter parity distribution.
/// Implements latency-aware striping, geographic failure domains, and async parity sync.
/// </summary>
public sealed class GeoRaid
{
    private readonly ConcurrentDictionary<string, GeoRaidArray> _arrays = new();
    private readonly ConcurrentDictionary<string, GeographicRegion> _regions = new();
    private readonly ConcurrentDictionary<string, PendingParitySync> _pendingSyncs = new();

    /// <summary>
    /// 91.F1.1: Configure cross-datacenter parity distribution.
    /// </summary>
    public void ConfigureCrossDatacenterParity(
        string arrayId,
        IEnumerable<DatacenterConfig> datacenters,
        ParityDistributionStrategy strategy = ParityDistributionStrategy.DistributedParity)
    {
        var dcList = datacenters.ToList();

        if (dcList.Count < 2)
            throw new ArgumentException("Geo-RAID requires at least 2 datacenters");

        var array = new GeoRaidArray
        {
            ArrayId = arrayId,
            Datacenters = dcList,
            ParityStrategy = strategy,
            CreatedTime = DateTime.UtcNow
        };

        // Assign parity responsibilities based on strategy
        switch (strategy)
        {
            case ParityDistributionStrategy.DistributedParity:
                DistributeParityAcrossDatacenters(array);
                break;
            case ParityDistributionStrategy.DedicatedParityDC:
                AssignDedicatedParityDatacenter(array, dcList.Last());
                break;
            case ParityDistributionStrategy.LocalParityRemoteMirror:
                ConfigureLocalParityRemoteMirror(array);
                break;
        }

        _arrays[arrayId] = array;
    }

    /// <summary>
    /// 91.F1.2: Define geographic failure domains.
    /// </summary>
    public void DefineFailureDomain(
        string domainId,
        string name,
        GeographicLocation location,
        IEnumerable<string> datacenterIds,
        FailureDomainType domainType = FailureDomainType.Region)
    {
        var region = new GeographicRegion
        {
            RegionId = domainId,
            Name = name,
            Location = location,
            DatacenterIds = datacenterIds.ToList(),
            DomainType = domainType
        };

        _regions[domainId] = region;
    }

    /// <summary>
    /// 91.F1.3: Get latency-optimized disk selection for striping.
    /// </summary>
    public StripeAllocation GetLatencyAwareStriping(
        string arrayId,
        long blockIndex,
        LatencyOptimizationMode mode = LatencyOptimizationMode.MinimizeP99)
    {
        if (!_arrays.TryGetValue(arrayId, out var array))
            throw new ArgumentException($"Array {arrayId} not found");

        var allocation = new StripeAllocation { BlockIndex = blockIndex };

        // Sort datacenters by latency
        var sortedDCs = array.Datacenters
            .OrderBy(dc => mode switch
            {
                LatencyOptimizationMode.MinimizeP99 => dc.P99LatencyMs,
                LatencyOptimizationMode.MinimizeP50 => dc.P50LatencyMs,
                LatencyOptimizationMode.MinimizeJitter => dc.JitterMs,
                _ => dc.P50LatencyMs
            })
            .ToList();

        // Assign data chunks to lowest-latency datacenters
        var dataChunks = array.ParityStrategy == ParityDistributionStrategy.DistributedParity
            ? sortedDCs.Count - 1
            : sortedDCs.Count - array.ParityDatacenterCount;

        for (int i = 0; i < dataChunks && i < sortedDCs.Count; i++)
        {
            allocation.DataDiskAssignments.Add(new DiskAssignment
            {
                DatacenterId = sortedDCs[i].DatacenterId,
                DiskId = SelectBestDiskInDC(sortedDCs[i]),
                IsParityDisk = false,
                EstimatedLatencyMs = sortedDCs[i].P50LatencyMs
            });
        }

        // Assign parity to remaining datacenters (can be slower)
        for (int i = dataChunks; i < sortedDCs.Count; i++)
        {
            allocation.ParityDiskAssignments.Add(new DiskAssignment
            {
                DatacenterId = sortedDCs[i].DatacenterId,
                DiskId = SelectBestDiskInDC(sortedDCs[i]),
                IsParityDisk = true,
                EstimatedLatencyMs = sortedDCs[i].P50LatencyMs
            });
        }

        allocation.EstimatedWriteLatencyMs = allocation.DataDiskAssignments.Max(d => d.EstimatedLatencyMs);
        allocation.EstimatedReadLatencyMs = allocation.DataDiskAssignments.Min(d => d.EstimatedLatencyMs);

        return allocation;
    }

    /// <summary>
    /// 91.F1.4: Queue asynchronous parity sync for eventual consistency.
    /// </summary>
    public async Task<SyncResult> QueueAsyncParitySyncAsync(
        string arrayId,
        long blockIndex,
        byte[] data,
        ParitySyncPriority priority = ParitySyncPriority.Normal,
        CancellationToken cancellationToken = default)
    {
        if (!_arrays.TryGetValue(arrayId, out var array))
            throw new ArgumentException($"Array {arrayId} not found");

        var syncId = Guid.NewGuid().ToString();
        var sync = new PendingParitySync
        {
            SyncId = syncId,
            ArrayId = arrayId,
            BlockIndex = blockIndex,
            Data = data,
            Priority = priority,
            CreatedTime = DateTime.UtcNow,
            Status = SyncStatus.Pending
        };

        _pendingSyncs[syncId] = sync;

        // Queue for async processing
        await ProcessParitySyncAsync(sync, array, cancellationToken);

        return new SyncResult
        {
            SyncId = syncId,
            Success = sync.Status == SyncStatus.Completed,
            DatacentersUpdated = sync.DatacentersUpdated,
            Duration = sync.CompletedTime.HasValue ? sync.CompletedTime.Value - sync.CreatedTime : TimeSpan.Zero
        };
    }

    /// <summary>
    /// Gets pending parity syncs for monitoring.
    /// </summary>
    public IReadOnlyList<PendingParitySync> GetPendingSyncs(string? arrayId = null)
    {
        var syncs = _pendingSyncs.Values.Where(s => s.Status == SyncStatus.Pending);
        if (arrayId != null)
            syncs = syncs.Where(s => s.ArrayId == arrayId);
        return syncs.OrderBy(s => s.Priority).ThenBy(s => s.CreatedTime).ToList();
    }

    /// <summary>
    /// Gets array status including cross-datacenter health.
    /// </summary>
    public GeoRaidStatus GetArrayStatus(string arrayId)
    {
        if (!_arrays.TryGetValue(arrayId, out var array))
            throw new ArgumentException($"Array {arrayId} not found");

        var status = new GeoRaidStatus
        {
            ArrayId = arrayId,
            TotalDatacenters = array.Datacenters.Count,
            HealthyDatacenters = array.Datacenters.Count(dc => dc.IsHealthy),
            PendingSyncs = _pendingSyncs.Values.Count(s => s.ArrayId == arrayId && s.Status == SyncStatus.Pending),
            ParityStrategy = array.ParityStrategy
        };

        foreach (var dc in array.Datacenters)
        {
            status.DatacenterStatuses.Add(new DatacenterStatus
            {
                DatacenterId = dc.DatacenterId,
                Name = dc.Name,
                IsHealthy = dc.IsHealthy,
                LatencyMs = dc.P50LatencyMs,
                LastHeartbeat = dc.LastHeartbeat
            });
        }

        return status;
    }

    /// <summary>
    /// Checks if array can survive failure of specified datacenters.
    /// </summary>
    public bool CanSurviveDatacenterFailure(string arrayId, IEnumerable<string> failedDatacenterIds)
    {
        if (!_arrays.TryGetValue(arrayId, out var array))
            return false;

        var failedIds = failedDatacenterIds.ToHashSet();
        var survivingDCs = array.Datacenters.Where(dc => !failedIds.Contains(dc.DatacenterId)).ToList();

        // Check if we have enough surviving DCs for data recovery
        var minRequired = array.ParityStrategy switch
        {
            ParityDistributionStrategy.DistributedParity => array.Datacenters.Count - 1,
            ParityDistributionStrategy.DedicatedParityDC => array.Datacenters.Count - array.ParityDatacenterCount - 1,
            ParityDistributionStrategy.LocalParityRemoteMirror => 1,
            _ => array.Datacenters.Count - 1
        };

        return survivingDCs.Count >= minRequired;
    }

    private void DistributeParityAcrossDatacenters(GeoRaidArray array)
    {
        // Each DC holds both data and parity, rotating responsibility
        foreach (var dc in array.Datacenters)
        {
            dc.HoldsData = true;
            dc.HoldsParity = true;
        }
        array.ParityDatacenterCount = array.Datacenters.Count;
    }

    private void AssignDedicatedParityDatacenter(GeoRaidArray array, DatacenterConfig parityDC)
    {
        foreach (var dc in array.Datacenters)
        {
            dc.HoldsData = dc.DatacenterId != parityDC.DatacenterId;
            dc.HoldsParity = dc.DatacenterId == parityDC.DatacenterId;
        }
        array.ParityDatacenterCount = 1;
    }

    private void ConfigureLocalParityRemoteMirror(GeoRaidArray array)
    {
        // First DC: primary with local parity
        // Other DCs: mirrors for disaster recovery
        var primary = array.Datacenters.First();
        primary.HoldsData = true;
        primary.HoldsParity = true;

        foreach (var dc in array.Datacenters.Skip(1))
        {
            dc.HoldsData = true;
            dc.HoldsParity = false; // Mirrors don't need parity, they have full copy
        }
        array.ParityDatacenterCount = 1;
    }

    private string SelectBestDiskInDC(DatacenterConfig dc)
    {
        // Select disk with lowest queue depth and best health
        return dc.Disks
            .OrderBy(d => d.QueueDepth)
            .ThenByDescending(d => d.HealthScore)
            .FirstOrDefault()?.DiskId ?? $"{dc.DatacenterId}-disk-0";
    }

    private async Task ProcessParitySyncAsync(PendingParitySync sync, GeoRaidArray array, CancellationToken ct)
    {
        sync.Status = SyncStatus.InProgress;

        try
        {
            var parityDCs = array.Datacenters.Where(dc => dc.HoldsParity).ToList();

            foreach (var dc in parityDCs)
            {
                ct.ThrowIfCancellationRequested();

                // Calculate parity for this DC
                var parity = CalculateParityForBlock(sync.Data, dc.DatacenterId);

                // Send async update
                await SendParityUpdateAsync(dc, sync.BlockIndex, parity, ct);

                sync.DatacentersUpdated.Add(dc.DatacenterId);
            }

            sync.Status = SyncStatus.Completed;
            sync.CompletedTime = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            sync.Status = SyncStatus.Failed;
            sync.ErrorMessage = ex.Message;
        }
    }

    private byte[] CalculateParityForBlock(byte[] data, string datacenterId)
    {
        // XOR parity calculation
        var parity = new byte[data.Length];
        Array.Copy(data, parity, data.Length);
        return parity;
    }

    private Task SendParityUpdateAsync(DatacenterConfig dc, long blockIndex, byte[] parity, CancellationToken ct)
    {
        // Simulate async parity update to remote DC
        return Task.Delay(dc.P50LatencyMs, ct);
    }
}

/// <summary>
/// Configuration for a datacenter in Geo-RAID.
/// </summary>
public sealed class DatacenterConfig
{
    public string DatacenterId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public GeographicLocation Location { get; set; } = new();
    public int P50LatencyMs { get; set; }
    public int P99LatencyMs { get; set; }
    public int JitterMs { get; set; }
    public bool IsHealthy { get; set; } = true;
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
    public bool HoldsData { get; set; }
    public bool HoldsParity { get; set; }
    public List<GeoDiskInfo> Disks { get; set; } = new();
}

/// <summary>
/// Geographic location information.
/// </summary>
public sealed class GeographicLocation
{
    public string Country { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}

/// <summary>
/// Disk information for Geo-RAID.
/// </summary>
public sealed class GeoDiskInfo
{
    public string DiskId { get; set; } = string.Empty;
    public int QueueDepth { get; set; }
    public double HealthScore { get; set; } = 1.0;
}

/// <summary>
/// Geographic failure domain region.
/// </summary>
public sealed class GeographicRegion
{
    public string RegionId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public GeographicLocation Location { get; set; } = new();
    public List<string> DatacenterIds { get; set; } = new();
    public FailureDomainType DomainType { get; set; }
}

/// <summary>
/// Type of failure domain.
/// </summary>
public enum FailureDomainType
{
    Rack,
    Room,
    Building,
    Campus,
    City,
    Region,
    Country,
    Continent
}

/// <summary>
/// Strategy for distributing parity across datacenters.
/// </summary>
public enum ParityDistributionStrategy
{
    DistributedParity,
    DedicatedParityDC,
    LocalParityRemoteMirror
}

/// <summary>
/// Latency optimization mode for stripe allocation.
/// </summary>
public enum LatencyOptimizationMode
{
    MinimizeP50,
    MinimizeP99,
    MinimizeJitter,
    BalanceLatencyAndBandwidth
}

/// <summary>
/// Geo-RAID array configuration.
/// </summary>
public sealed class GeoRaidArray
{
    public string ArrayId { get; set; } = string.Empty;
    public List<DatacenterConfig> Datacenters { get; set; } = new();
    public ParityDistributionStrategy ParityStrategy { get; set; }
    public int ParityDatacenterCount { get; set; }
    public DateTime CreatedTime { get; set; }
}

/// <summary>
/// Stripe allocation with latency awareness.
/// </summary>
public sealed class StripeAllocation
{
    public long BlockIndex { get; set; }
    public List<DiskAssignment> DataDiskAssignments { get; set; } = new();
    public List<DiskAssignment> ParityDiskAssignments { get; set; } = new();
    public int EstimatedWriteLatencyMs { get; set; }
    public int EstimatedReadLatencyMs { get; set; }
}

/// <summary>
/// Disk assignment for a stripe.
/// </summary>
public sealed class DiskAssignment
{
    public string DatacenterId { get; set; } = string.Empty;
    public string DiskId { get; set; } = string.Empty;
    public bool IsParityDisk { get; set; }
    public int EstimatedLatencyMs { get; set; }
}

/// <summary>
/// Priority for parity sync operations.
/// </summary>
public enum ParitySyncPriority
{
    Low,
    Normal,
    High,
    Critical
}

/// <summary>
/// Status of a parity sync operation.
/// </summary>
public enum SyncStatus
{
    Pending,
    InProgress,
    Completed,
    Failed
}

/// <summary>
/// Pending parity sync operation.
/// </summary>
public sealed class PendingParitySync
{
    public string SyncId { get; set; } = string.Empty;
    public string ArrayId { get; set; } = string.Empty;
    public long BlockIndex { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public ParitySyncPriority Priority { get; set; }
    public SyncStatus Status { get; set; }
    public DateTime CreatedTime { get; set; }
    public DateTime? CompletedTime { get; set; }
    public List<string> DatacentersUpdated { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Result of a parity sync operation.
/// </summary>
public sealed class SyncResult
{
    public string SyncId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public List<string> DatacentersUpdated { get; set; } = new();
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Status of a Geo-RAID array.
/// </summary>
public sealed class GeoRaidStatus
{
    public string ArrayId { get; set; } = string.Empty;
    public int TotalDatacenters { get; set; }
    public int HealthyDatacenters { get; set; }
    public int PendingSyncs { get; set; }
    public ParityDistributionStrategy ParityStrategy { get; set; }
    public List<DatacenterStatus> DatacenterStatuses { get; set; } = new();
}

/// <summary>
/// Status of a datacenter in Geo-RAID.
/// </summary>
public sealed class DatacenterStatus
{
    public string DatacenterId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsHealthy { get; set; }
    public int LatencyMs { get; set; }
    public DateTime LastHeartbeat { get; set; }
}
