using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DataWarehouse.Dashboard.Services;
using DataWarehouse.Dashboard.Security;
using DataWarehouse.Kernel.Storage;

namespace DataWarehouse.Dashboard.Controllers;

/// <summary>
/// API controller for storage management.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize(Policy = AuthorizationPolicies.Authenticated)]
public class StorageController : ControllerBase
{
    private readonly IStorageManagementService _storageService;
    private readonly ILogger<StorageController> _logger;

    public StorageController(IStorageManagementService storageService, ILogger<StorageController> logger)
    {
        _storageService = storageService;
        _logger = logger;
    }

    /// <summary>
    /// Gets all storage pools.
    /// </summary>
    [HttpGet("pools")]
    [ProducesResponseType(typeof(IEnumerable<StoragePoolInfo>), StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<StoragePoolInfo>> GetPools()
    {
        var pools = _storageService.GetStoragePools();
        return Ok(pools);
    }

    /// <summary>
    /// Gets a specific storage pool by ID.
    /// </summary>
    [HttpGet("pools/{id}")]
    [ProducesResponseType(typeof(StoragePoolInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<StoragePoolInfo> GetPool(string id)
    {
        var pool = _storageService.GetPool(id);
        if (pool == null)
            return NotFound(new { error = $"Storage pool '{id}' not found" });

        return Ok(pool);
    }

    /// <summary>
    /// Creates a new storage pool.
    /// </summary>
    [HttpPost("pools")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [ProducesResponseType(typeof(StoragePoolInfo), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<StoragePoolInfo>> CreatePool([FromBody] CreatePoolRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Pool name is required" });

        var pool = await _storageService.CreatePoolAsync(request.Name, request.PoolType, request.CapacityBytes);
        _logger.LogInformation("Storage pool {PoolName} created", request.Name);

        return CreatedAtAction(nameof(GetPool), new { id = pool.Id }, pool);
    }

    /// <summary>
    /// Deletes a storage pool.
    /// </summary>
    [HttpDelete("pools/{id}")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeletePool(string id)
    {
        var success = await _storageService.DeletePoolAsync(id);
        if (!success)
            return NotFound(new { error = $"Storage pool '{id}' not found" });

        _logger.LogInformation("Storage pool {PoolId} deleted", id);
        return NoContent();
    }

    /// <summary>
    /// Gets pool statistics.
    /// </summary>
    [HttpGet("pools/{id}/stats")]
    [ProducesResponseType(typeof(StoragePoolStats), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<StoragePoolStats> GetPoolStats(string id)
    {
        var pool = _storageService.GetPool(id);
        if (pool == null)
            return NotFound(new { error = $"Storage pool '{id}' not found" });

        return Ok(pool.Stats);
    }

    /// <summary>
    /// Gets RAID configurations.
    /// </summary>
    [HttpGet("raid")]
    [ProducesResponseType(typeof(IEnumerable<RaidConfiguration>), StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<RaidConfiguration>> GetRaidConfigurations()
    {
        var configs = _storageService.GetRaidConfigurations();
        return Ok(configs);
    }

    /// <summary>
    /// Gets a specific RAID configuration.
    /// </summary>
    [HttpGet("raid/{id}")]
    [ProducesResponseType(typeof(RaidConfiguration), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<RaidConfiguration> GetRaidConfiguration(string id)
    {
        var config = _storageService.GetRaidConfiguration(id);
        if (config == null)
            return NotFound(new { error = $"RAID configuration '{id}' not found" });

        return Ok(config);
    }

    /// <summary>
    /// Gets storage instances by pool.
    /// </summary>
    [HttpGet("pools/{poolId}/instances")]
    [ProducesResponseType(typeof(IEnumerable<StorageInstance>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IEnumerable<StorageInstance>> GetPoolInstances(string poolId)
    {
        var pool = _storageService.GetPool(poolId);
        if (pool == null)
            return NotFound(new { error = $"Storage pool '{poolId}' not found" });

        return Ok(pool.Instances);
    }

    /// <summary>
    /// Adds a storage instance to a pool.
    /// </summary>
    [HttpPost("pools/{poolId}/instances")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [ProducesResponseType(typeof(StorageInstance), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StorageInstance>> AddInstance(string poolId, [FromBody] AddInstanceRequest request)
    {
        var pool = _storageService.GetPool(poolId);
        if (pool == null)
            return NotFound(new { error = $"Storage pool '{poolId}' not found" });

        var instance = await _storageService.AddInstanceAsync(poolId, request.Name, request.PluginId, request.Config);
        if (instance == null)
            return BadRequest(new { error = "Failed to add instance" });

        _logger.LogInformation("Instance {InstanceName} added to pool {PoolId}", request.Name, poolId);
        return CreatedAtAction(nameof(GetPoolInstances), new { poolId }, instance);
    }

    /// <summary>
    /// Removes a storage instance from a pool.
    /// </summary>
    [HttpDelete("pools/{poolId}/instances/{instanceId}")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> RemoveInstance(string poolId, string instanceId)
    {
        var success = await _storageService.RemoveInstanceAsync(poolId, instanceId);
        if (!success)
            return NotFound(new { error = "Instance or pool not found" });

        _logger.LogInformation("Instance {InstanceId} removed from pool {PoolId}", instanceId, poolId);
        return NoContent();
    }

    /// <summary>
    /// Gets overall storage summary.
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(StorageSummary), StatusCodes.Status200OK)]
    public ActionResult<StorageSummary> GetStorageSummary()
    {
        var pools = _storageService.GetStoragePools().ToList();

        var summary = new StorageSummary
        {
            TotalPools = pools.Count,
            TotalCapacityBytes = pools.Sum(p => p.CapacityBytes),
            UsedBytes = pools.Sum(p => p.UsedBytes),
            TotalInstances = pools.Sum(p => p.Instances.Count),
            HealthyPools = pools.Count(p => p.Health == PoolHealth.Healthy),
            DegradedPools = pools.Count(p => p.Health == PoolHealth.Degraded),
            OfflinePools = pools.Count(p => p.Health == PoolHealth.Offline)
        };

        return Ok(summary);
    }
}

public class CreatePoolRequest
{
    [Required(ErrorMessage = "Pool name is required.")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "Pool name must be between 1 and 100 characters.")]
    [DataWarehouse.Dashboard.Validation.ValidIdentifier(ErrorMessage = "Pool name must be a valid identifier (letters, numbers, underscores, hyphens).")]
    [DataWarehouse.Dashboard.Validation.SafeString]
    public string Name { get; set; } = string.Empty;

    [StringLength(50, ErrorMessage = "Pool type must be at most 50 characters.")]
    [DataWarehouse.Dashboard.Validation.SafeString]
    public string PoolType { get; set; } = "Standard";

    [Range(1024L * 1024, 1024L * 1024 * 1024 * 1024 * 100, ErrorMessage = "Capacity must be between 1 MB and 100 TB.")]
    public long CapacityBytes { get; set; } = 1024L * 1024 * 1024 * 10; // 10 GB default
}

public class AddInstanceRequest
{
    [Required(ErrorMessage = "Instance name is required.")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "Instance name must be between 1 and 100 characters.")]
    [DataWarehouse.Dashboard.Validation.ValidIdentifier(ErrorMessage = "Instance name must be a valid identifier.")]
    [DataWarehouse.Dashboard.Validation.SafeString]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Plugin ID is required.")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "Plugin ID must be between 1 and 100 characters.")]
    [DataWarehouse.Dashboard.Validation.SafeString]
    public string PluginId { get; set; } = string.Empty;

    [DataWarehouse.Dashboard.Validation.SafeDictionary(MaxEntries = 50)]
    public Dictionary<string, object>? Config { get; set; }
}

public class StorageSummary
{
    public int TotalPools { get; set; }
    public long TotalCapacityBytes { get; set; }
    public long UsedBytes { get; set; }
    public double UsagePercent => TotalCapacityBytes > 0 ? (double)UsedBytes / TotalCapacityBytes * 100 : 0;
    public int TotalInstances { get; set; }
    public int HealthyPools { get; set; }
    public int DegradedPools { get; set; }
    public int OfflinePools { get; set; }
}
