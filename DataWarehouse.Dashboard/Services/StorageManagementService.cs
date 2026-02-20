using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Dashboard.Services;

/// <summary>
/// Service for managing storage pools and monitoring.
/// </summary>
public interface IStorageManagementService
{
    IEnumerable<StoragePoolInfo> GetStoragePools();
    StoragePoolInfo? GetPool(string poolId);
    Task<StoragePoolInfo> CreatePoolAsync(string name, string poolType, long capacityBytes);
    Task<bool> DeletePoolAsync(string poolId);
    IEnumerable<RaidConfiguration> GetRaidConfigurations();
    RaidConfiguration? GetRaidConfiguration(string id);
    Task<StorageInstance?> AddInstanceAsync(string poolId, string name, string pluginId, Dictionary<string, object>? config);
    Task<bool> RemoveInstanceAsync(string poolId, string instanceId);
    event EventHandler<StorageChangedEventArgs>? StorageChanged;
}

public class StoragePoolInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string PoolType { get; set; } = "Standard";
    public long CapacityBytes { get; set; }
    public long UsedBytes { get; set; }
    public PoolHealth Health { get; set; } = PoolHealth.Healthy;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<StorageInstance> Instances { get; set; } = new();
    public StoragePoolStats? Stats { get; set; }
}

public class StorageInstance
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string PluginId { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public long CapacityBytes { get; set; }
    public long UsedBytes { get; set; }
    public Dictionary<string, object> Config { get; set; } = new();
}

public class StoragePoolStats
{
    public long ReadOperations { get; set; }
    public long WriteOperations { get; set; }
    public double ReadThroughputMBps { get; set; }
    public double WriteThroughputMBps { get; set; }
    public double AverageLatencyMs { get; set; }
    public int ActiveConnections { get; set; }
}

public enum PoolHealth
{
    Healthy,
    Degraded,
    Offline
}

public class RaidConfiguration
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Level { get; set; } = "RAID5";
    public RaidStatus Status { get; set; } = RaidStatus.Optimal;
    public int DiskCount { get; set; }
    public int ActiveDisks { get; set; }
    public int SpareDisks { get; set; }
    public long TotalCapacityBytes { get; set; }
    public int StripeSizeKB { get; set; } = 64;
    public double RebuildProgress { get; set; }
}

public enum RaidStatus
{
    Optimal,
    Degraded,
    Rebuilding,
    Failed
}

public class StorageChangedEventArgs : EventArgs
{
    public string PoolId { get; set; } = string.Empty;
    public string ChangeType { get; set; } = string.Empty;
}

/// <summary>
/// Implementation of storage management service.
/// </summary>
public class StorageManagementService : IStorageManagementService
{
    private readonly BoundedDictionary<string, StoragePoolInfo> _pools = new BoundedDictionary<string, StoragePoolInfo>(1000);
    private readonly BoundedDictionary<string, RaidConfiguration> _raidConfigs = new BoundedDictionary<string, RaidConfiguration>(1000);
    private readonly IPluginDiscoveryService _pluginService;
    private readonly ILogger<StorageManagementService> _logger;

    public event EventHandler<StorageChangedEventArgs>? StorageChanged;

    public StorageManagementService(IPluginDiscoveryService pluginService, ILogger<StorageManagementService> logger)
    {
        _pluginService = pluginService;
        _logger = logger;
        InitializeSamplePools();
    }

    public IEnumerable<StoragePoolInfo> GetStoragePools() => _pools.Values.OrderBy(p => p.Name);

    public StoragePoolInfo? GetPool(string poolId) =>
        _pools.TryGetValue(poolId, out var pool) ? pool : null;

    public Task<StoragePoolInfo> CreatePoolAsync(string name, string poolType, long capacityBytes)
    {
        var pool = new StoragePoolInfo
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            PoolType = poolType,
            CapacityBytes = capacityBytes,
            UsedBytes = 0,
            Health = PoolHealth.Healthy,
            CreatedAt = DateTime.UtcNow,
            Stats = new StoragePoolStats()
        };

        _pools[pool.Id] = pool;

        StorageChanged?.Invoke(this, new StorageChangedEventArgs
        {
            PoolId = pool.Id,
            ChangeType = "Created"
        });

        _logger.LogInformation("Created storage pool {Name} ({Id})", name, pool.Id);
        return Task.FromResult(pool);
    }

    public Task<bool> DeletePoolAsync(string poolId)
    {
        if (_pools.TryRemove(poolId, out var pool))
        {
            StorageChanged?.Invoke(this, new StorageChangedEventArgs
            {
                PoolId = poolId,
                ChangeType = "Deleted"
            });
            _logger.LogInformation("Deleted storage pool {Name} ({Id})", pool.Name, poolId);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public IEnumerable<RaidConfiguration> GetRaidConfigurations() => _raidConfigs.Values;

    public RaidConfiguration? GetRaidConfiguration(string id) =>
        _raidConfigs.TryGetValue(id, out var config) ? config : null;

    public Task<StorageInstance?> AddInstanceAsync(string poolId, string name, string pluginId, Dictionary<string, object>? config)
    {
        if (!_pools.TryGetValue(poolId, out var pool))
            return Task.FromResult<StorageInstance?>(null);

        var instance = new StorageInstance
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            PluginId = pluginId,
            Status = "Active",
            Config = config ?? new()
        };

        pool.Instances.Add(instance);

        StorageChanged?.Invoke(this, new StorageChangedEventArgs
        {
            PoolId = poolId,
            ChangeType = "InstanceAdded"
        });

        return Task.FromResult<StorageInstance?>(instance);
    }

    public Task<bool> RemoveInstanceAsync(string poolId, string instanceId)
    {
        if (!_pools.TryGetValue(poolId, out var pool))
            return Task.FromResult(false);

        var instance = pool.Instances.FirstOrDefault(i => i.Id == instanceId);
        if (instance == null)
            return Task.FromResult(false);

        pool.Instances.Remove(instance);

        StorageChanged?.Invoke(this, new StorageChangedEventArgs
        {
            PoolId = poolId,
            ChangeType = "InstanceRemoved"
        });

        return Task.FromResult(true);
    }

    private void InitializeSamplePools()
    {
        // Primary storage pool
        _pools["primary"] = new StoragePoolInfo
        {
            Id = "primary",
            Name = "Primary Storage Pool",
            PoolType = "RAID5",
            CapacityBytes = 10L * 1024 * 1024 * 1024 * 1024, // 10 TB
            UsedBytes = 3L * 1024 * 1024 * 1024 * 1024,      // 3 TB
            Health = PoolHealth.Healthy,
            CreatedAt = DateTime.UtcNow.AddDays(-30),
            Instances = new()
            {
                new() { Id = "disk1", Name = "Disk 1", PluginId = "filesystem", CapacityBytes = (long)(2.5 * 1024L * 1024 * 1024 * 1024), Status = "Active" },
                new() { Id = "disk2", Name = "Disk 2", PluginId = "filesystem", CapacityBytes = (long)(2.5 * 1024L * 1024 * 1024 * 1024), Status = "Active" },
                new() { Id = "disk3", Name = "Disk 3", PluginId = "filesystem", CapacityBytes = (long)(2.5 * 1024L * 1024 * 1024 * 1024), Status = "Active" },
                new() { Id = "disk4", Name = "Disk 4", PluginId = "filesystem", CapacityBytes = (long)(2.5 * 1024L * 1024 * 1024 * 1024), Status = "Active" }
            },
            Stats = new StoragePoolStats
            {
                ReadOperations = 15420,
                WriteOperations = 8932,
                ReadThroughputMBps = 450.5,
                WriteThroughputMBps = 320.2,
                AverageLatencyMs = 2.3,
                ActiveConnections = 45
            }
        };

        // Cache pool
        _pools["cache"] = new StoragePoolInfo
        {
            Id = "cache",
            Name = "SSD Cache Pool",
            PoolType = "RAID1",
            CapacityBytes = 1L * 1024 * 1024 * 1024 * 1024, // 1 TB
            UsedBytes = 512L * 1024 * 1024 * 1024,          // 512 GB
            Health = PoolHealth.Healthy,
            CreatedAt = DateTime.UtcNow.AddDays(-15),
            Instances = new()
            {
                new() { Id = "ssd1", Name = "SSD 1", PluginId = "filesystem", CapacityBytes = 512L * 1024 * 1024 * 1024, Status = "Active" },
                new() { Id = "ssd2", Name = "SSD 2", PluginId = "filesystem", CapacityBytes = 512L * 1024 * 1024 * 1024, Status = "Active" }
            },
            Stats = new StoragePoolStats
            {
                ReadOperations = 45230,
                WriteOperations = 23100,
                ReadThroughputMBps = 1200.0,
                WriteThroughputMBps = 950.5,
                AverageLatencyMs = 0.8,
                ActiveConnections = 120
            }
        };

        // Archive pool
        _pools["archive"] = new StoragePoolInfo
        {
            Id = "archive",
            Name = "Archive Pool",
            PoolType = "Standard",
            CapacityBytes = 50L * 1024 * 1024 * 1024 * 1024, // 50 TB
            UsedBytes = 35L * 1024 * 1024 * 1024 * 1024,     // 35 TB
            Health = PoolHealth.Healthy,
            CreatedAt = DateTime.UtcNow.AddDays(-90),
            Instances = new()
            {
                new() { Id = "archive1", Name = "Archive Drive 1", PluginId = "s3", CapacityBytes = 50L * 1024 * 1024 * 1024 * 1024, Status = "Active" }
            },
            Stats = new StoragePoolStats
            {
                ReadOperations = 1250,
                WriteOperations = 890,
                ReadThroughputMBps = 100.0,
                WriteThroughputMBps = 80.5,
                AverageLatencyMs = 15.2,
                ActiveConnections = 5
            }
        };

        // RAID configurations
        _raidConfigs["raid-primary"] = new RaidConfiguration
        {
            Id = "raid-primary",
            Name = "Primary RAID5 Array",
            Level = "RAID5",
            Status = RaidStatus.Optimal,
            DiskCount = 4,
            ActiveDisks = 4,
            SpareDisks = 0,
            TotalCapacityBytes = 10L * 1024 * 1024 * 1024 * 1024,
            StripeSizeKB = 64
        };

        _raidConfigs["raid-cache"] = new RaidConfiguration
        {
            Id = "raid-cache",
            Name = "Cache RAID1 Mirror",
            Level = "RAID1",
            Status = RaidStatus.Optimal,
            DiskCount = 2,
            ActiveDisks = 2,
            SpareDisks = 0,
            TotalCapacityBytes = 1L * 1024 * 1024 * 1024 * 1024,
            StripeSizeKB = 128
        };
    }
}
