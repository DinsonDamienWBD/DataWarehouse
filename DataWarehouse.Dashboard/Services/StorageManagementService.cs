using DataWarehouse.SDK.Contracts;
using System.Collections.Concurrent;

namespace DataWarehouse.Dashboard.Services;

/// <summary>
/// Service for managing storage pools and monitoring.
/// </summary>
public interface IStorageManagementService
{
    /// <summary>
    /// Gets all storage pools.
    /// </summary>
    IEnumerable<StoragePoolInfo> GetStoragePools();

    /// <summary>
    /// Gets storage pool by ID.
    /// </summary>
    StoragePoolInfo? GetStoragePool(string poolId);

    /// <summary>
    /// Gets storage usage statistics.
    /// </summary>
    StorageUsageStats GetUsageStats();

    /// <summary>
    /// Gets RAID status for a pool.
    /// </summary>
    RaidStatusInfo? GetRaidStatus(string poolId);

    /// <summary>
    /// Gets all storage instances across all plugins.
    /// </summary>
    IEnumerable<StorageInstanceInfo> GetAllInstances();

    /// <summary>
    /// Creates a new storage pool.
    /// </summary>
    Task<StoragePoolInfo?> CreatePoolAsync(CreatePoolRequest request);

    /// <summary>
    /// Deletes a storage pool.
    /// </summary>
    Task<bool> DeletePoolAsync(string poolId);

    /// <summary>
    /// Event raised when storage changes.
    /// </summary>
    event EventHandler<StorageChangedEventArgs>? StorageChanged;
}

public class StoragePoolInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Strategy { get; set; } = string.Empty; // RAID0, RAID1, RAID5, Mirrored, etc.
    public long TotalCapacityBytes { get; set; }
    public long UsedCapacityBytes { get; set; }
    public double UsagePercent => TotalCapacityBytes > 0 ? (double)UsedCapacityBytes / TotalCapacityBytes * 100 : 0;
    public int ProviderCount { get; set; }
    public int HealthyProviders { get; set; }
    public bool IsHealthy { get; set; }
    public bool IsDegraded { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastActivity { get; set; }
    public List<StorageProviderInfo> Providers { get; set; } = new();
}

public class StorageProviderInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string PluginId { get; set; } = string.Empty;
    public StorageRole Role { get; set; }
    public long CapacityBytes { get; set; }
    public long UsedBytes { get; set; }
    public bool IsHealthy { get; set; }
    public TimeSpan? LastResponseTime { get; set; }
    public string? LastError { get; set; }
}

public class StorageUsageStats
{
    public long TotalCapacityBytes { get; set; }
    public long UsedCapacityBytes { get; set; }
    public long AvailableCapacityBytes => TotalCapacityBytes - UsedCapacityBytes;
    public double UsagePercent => TotalCapacityBytes > 0 ? (double)UsedCapacityBytes / TotalCapacityBytes * 100 : 0;
    public int TotalPools { get; set; }
    public int HealthyPools { get; set; }
    public int DegradedPools { get; set; }
    public long ObjectCount { get; set; }
    public long ReadOperationsPerMinute { get; set; }
    public long WriteOperationsPerMinute { get; set; }
    public double AverageReadLatencyMs { get; set; }
    public double AverageWriteLatencyMs { get; set; }
    public Dictionary<string, long> UsageByTier { get; set; } = new();
}

public class RaidStatusInfo
{
    public string PoolId { get; set; } = string.Empty;
    public string RaidLevel { get; set; } = string.Empty;
    public RaidState State { get; set; }
    public int TotalDisks { get; set; }
    public int ActiveDisks { get; set; }
    public int SpareDisks { get; set; }
    public int FailedDisks { get; set; }
    public double RebuildProgress { get; set; }
    public TimeSpan? EstimatedRebuildTime { get; set; }
    public List<RaidDiskInfo> Disks { get; set; } = new();
}

public enum RaidState
{
    Healthy,
    Degraded,
    Rebuilding,
    Failed
}

public class RaidDiskInfo
{
    public int Position { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public RaidDiskState State { get; set; }
    public long CapacityBytes { get; set; }
}

public enum RaidDiskState
{
    Active,
    Spare,
    Rebuilding,
    Failed,
    Missing
}

public class StorageInstanceInfo
{
    public string InstanceId { get; set; } = string.Empty;
    public string PluginId { get; set; } = string.Empty;
    public string PluginName { get; set; } = string.Empty;
    public StorageRole Roles { get; set; }
    public bool IsConnected { get; set; }
    public int ActiveConnections { get; set; }
    public int PooledConnections { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastActivity { get; set; }
    public Dictionary<string, object> Config { get; set; } = new();
}

public class CreatePoolRequest
{
    public string Name { get; set; } = string.Empty;
    public string Strategy { get; set; } = "Simple";
    public List<string> ProviderIds { get; set; } = new();
    public Dictionary<string, object> Options { get; set; } = new();
}

public class StorageChangedEventArgs : EventArgs
{
    public string PoolId { get; set; } = string.Empty;
    public StorageChangeType ChangeType { get; set; }
}

public enum StorageChangeType
{
    PoolCreated,
    PoolDeleted,
    PoolUpdated,
    ProviderAdded,
    ProviderRemoved,
    HealthChanged
}

/// <summary>
/// Implementation of storage management service.
/// </summary>
public class StorageManagementService : IStorageManagementService
{
    private readonly ConcurrentDictionary<string, StoragePoolInfo> _pools = new();
    private readonly IPluginDiscoveryService _pluginService;
    private readonly ILogger<StorageManagementService> _logger;

    public event EventHandler<StorageChangedEventArgs>? StorageChanged;

    public StorageManagementService(IPluginDiscoveryService pluginService, ILogger<StorageManagementService> logger)
    {
        _pluginService = pluginService;
        _logger = logger;

        // Initialize with sample data
        InitializeSamplePools();
    }

    public IEnumerable<StoragePoolInfo> GetStoragePools() => _pools.Values.OrderBy(p => p.Name);

    public StoragePoolInfo? GetStoragePool(string poolId) =>
        _pools.TryGetValue(poolId, out var pool) ? pool : null;

    public StorageUsageStats GetUsageStats()
    {
        var pools = _pools.Values.ToList();
        return new StorageUsageStats
        {
            TotalCapacityBytes = pools.Sum(p => p.TotalCapacityBytes),
            UsedCapacityBytes = pools.Sum(p => p.UsedCapacityBytes),
            TotalPools = pools.Count,
            HealthyPools = pools.Count(p => p.IsHealthy && !p.IsDegraded),
            DegradedPools = pools.Count(p => p.IsDegraded),
            UsageByTier = new Dictionary<string, long>
            {
                ["Hot"] = pools.Sum(p => p.UsedCapacityBytes) / 3,
                ["Warm"] = pools.Sum(p => p.UsedCapacityBytes) / 3,
                ["Cold"] = pools.Sum(p => p.UsedCapacityBytes) / 3
            }
        };
    }

    public RaidStatusInfo? GetRaidStatus(string poolId)
    {
        if (!_pools.TryGetValue(poolId, out var pool))
            return null;

        return new RaidStatusInfo
        {
            PoolId = poolId,
            RaidLevel = pool.Strategy,
            State = pool.IsDegraded ? RaidState.Degraded : (pool.IsHealthy ? RaidState.Healthy : RaidState.Failed),
            TotalDisks = pool.ProviderCount,
            ActiveDisks = pool.HealthyProviders,
            FailedDisks = pool.ProviderCount - pool.HealthyProviders,
            Disks = pool.Providers.Select((p, i) => new RaidDiskInfo
            {
                Position = i,
                ProviderId = p.Id,
                State = p.IsHealthy ? RaidDiskState.Active : RaidDiskState.Failed,
                CapacityBytes = p.CapacityBytes
            }).ToList()
        };
    }

    public IEnumerable<StorageInstanceInfo> GetAllInstances()
    {
        // Get instances from active storage plugins
        var storagePlugins = _pluginService.GetPluginsByCategory(SDK.Primitives.PluginCategory.StorageProvider);

        foreach (var plugin in storagePlugins.Where(p => p.IsActive))
        {
            if (plugin.Metadata.TryGetValue("RegisteredInstances", out var countObj) && countObj is int count)
            {
                for (int i = 0; i < count; i++)
                {
                    yield return new StorageInstanceInfo
                    {
                        InstanceId = $"{plugin.Id}-{i}",
                        PluginId = plugin.Id,
                        PluginName = plugin.Name,
                        IsConnected = true,
                        CreatedAt = DateTime.UtcNow.AddHours(-i)
                    };
                }
            }
        }
    }

    public async Task<StoragePoolInfo?> CreatePoolAsync(CreatePoolRequest request)
    {
        var pool = new StoragePoolInfo
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = request.Name,
            Strategy = request.Strategy,
            CreatedAt = DateTime.UtcNow,
            IsHealthy = true
        };

        if (_pools.TryAdd(pool.Id, pool))
        {
            StorageChanged?.Invoke(this, new StorageChangedEventArgs
            {
                PoolId = pool.Id,
                ChangeType = StorageChangeType.PoolCreated
            });
            return pool;
        }

        return null;
    }

    public async Task<bool> DeletePoolAsync(string poolId)
    {
        if (_pools.TryRemove(poolId, out _))
        {
            StorageChanged?.Invoke(this, new StorageChangedEventArgs
            {
                PoolId = poolId,
                ChangeType = StorageChangeType.PoolDeleted
            });
            return true;
        }
        return false;
    }

    private void InitializeSamplePools()
    {
        // Sample primary pool
        _pools["primary"] = new StoragePoolInfo
        {
            Id = "primary",
            Name = "Primary Storage Pool",
            Strategy = "RAID5",
            TotalCapacityBytes = 10L * 1024 * 1024 * 1024 * 1024, // 10 TB
            UsedCapacityBytes = 3L * 1024 * 1024 * 1024 * 1024,   // 3 TB
            ProviderCount = 4,
            HealthyProviders = 4,
            IsHealthy = true,
            CreatedAt = DateTime.UtcNow.AddDays(-30),
            Providers = new()
            {
                new() { Id = "disk1", Name = "Disk 1", Role = StorageRole.Primary, CapacityBytes = (long)(2.5 * 1024L * 1024 * 1024 * 1024), IsHealthy = true },
                new() { Id = "disk2", Name = "Disk 2", Role = StorageRole.Primary, CapacityBytes = (long)(2.5 * 1024L * 1024 * 1024 * 1024), IsHealthy = true },
                new() { Id = "disk3", Name = "Disk 3", Role = StorageRole.Primary, CapacityBytes = (long)(2.5 * 1024L * 1024 * 1024 * 1024), IsHealthy = true },
                new() { Id = "disk4", Name = "Disk 4", Role = StorageRole.Parity, CapacityBytes = (long)(2.5 * 1024L * 1024 * 1024 * 1024), IsHealthy = true }
            }
        };

        // Sample cache pool
        _pools["cache"] = new StoragePoolInfo
        {
            Id = "cache",
            Name = "SSD Cache Pool",
            Strategy = "RAID1",
            TotalCapacityBytes = 1L * 1024 * 1024 * 1024 * 1024, // 1 TB
            UsedCapacityBytes = 512L * 1024 * 1024 * 1024,       // 512 GB
            ProviderCount = 2,
            HealthyProviders = 2,
            IsHealthy = true,
            CreatedAt = DateTime.UtcNow.AddDays(-15),
            Providers = new()
            {
                new() { Id = "ssd1", Name = "SSD 1", Role = StorageRole.Cache, CapacityBytes = 512L * 1024 * 1024 * 1024, IsHealthy = true },
                new() { Id = "ssd2", Name = "SSD 2", Role = StorageRole.Mirror, CapacityBytes = 512L * 1024 * 1024 * 1024, IsHealthy = true }
            }
        };
    }
}
