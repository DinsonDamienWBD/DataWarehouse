using Microsoft.AspNetCore.Mvc;
using DataWarehouse.Dashboard.Services;

namespace DataWarehouse.Dashboard.Controllers;

/// <summary>
/// API controller for system and tenant configuration.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class ConfigurationController : ControllerBase
{
    private readonly IConfigurationService _configService;
    private readonly ILogger<ConfigurationController> _logger;

    public ConfigurationController(IConfigurationService configService, ILogger<ConfigurationController> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    /// <summary>
    /// Gets the system configuration.
    /// </summary>
    [HttpGet("system")]
    [ProducesResponseType(typeof(SystemConfiguration), StatusCodes.Status200OK)]
    public ActionResult<SystemConfiguration> GetSystemConfiguration()
    {
        var config = _configService.GetSystemConfiguration();
        return Ok(config);
    }

    /// <summary>
    /// Updates the system configuration.
    /// </summary>
    [HttpPut("system")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> UpdateSystemConfiguration([FromBody] SystemConfiguration config)
    {
        await _configService.UpdateSystemConfigurationAsync(config);
        _logger.LogInformation("System configuration updated");
        return Ok(new { message = "System configuration updated" });
    }

    /// <summary>
    /// Gets security policy settings.
    /// </summary>
    [HttpGet("security")]
    [ProducesResponseType(typeof(SecurityPolicySettings), StatusCodes.Status200OK)]
    public ActionResult<SecurityPolicySettings> GetSecurityPolicies()
    {
        var policies = _configService.GetSecurityPolicies();
        return Ok(policies);
    }

    /// <summary>
    /// Updates security policy settings.
    /// </summary>
    [HttpPut("security")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> UpdateSecurityPolicies([FromBody] SecurityPolicySettings policies)
    {
        await _configService.UpdateSecurityPoliciesAsync(policies);
        _logger.LogInformation("Security policies updated");
        return Ok(new { message = "Security policies updated" });
    }

    /// <summary>
    /// Gets all tenant configurations.
    /// </summary>
    [HttpGet("tenants")]
    [ProducesResponseType(typeof(IEnumerable<TenantConfiguration>), StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<TenantConfiguration>> GetTenants()
    {
        var tenants = _configService.GetTenants();
        return Ok(tenants);
    }

    /// <summary>
    /// Gets a specific tenant configuration.
    /// </summary>
    [HttpGet("tenants/{tenantId}")]
    [ProducesResponseType(typeof(TenantConfiguration), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<TenantConfiguration> GetTenant(string tenantId)
    {
        var tenant = _configService.GetTenant(tenantId);
        if (tenant == null)
            return NotFound(new { error = $"Tenant '{tenantId}' not found" });

        return Ok(tenant);
    }

    /// <summary>
    /// Creates a new tenant.
    /// </summary>
    [HttpPost("tenants")]
    [ProducesResponseType(typeof(TenantConfiguration), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TenantConfiguration>> CreateTenant([FromBody] TenantConfiguration tenant)
    {
        if (string.IsNullOrWhiteSpace(tenant.TenantId))
            return BadRequest(new { error = "Tenant ID is required" });

        if (_configService.GetTenant(tenant.TenantId) != null)
            return BadRequest(new { error = $"Tenant '{tenant.TenantId}' already exists" });

        tenant.CreatedAt = DateTime.UtcNow;
        await _configService.SaveTenantAsync(tenant);

        _logger.LogInformation("Tenant {TenantId} created", tenant.TenantId);
        return CreatedAtAction(nameof(GetTenant), new { tenantId = tenant.TenantId }, tenant);
    }

    /// <summary>
    /// Updates an existing tenant.
    /// </summary>
    [HttpPut("tenants/{tenantId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UpdateTenant(string tenantId, [FromBody] TenantConfiguration tenant)
    {
        var existing = _configService.GetTenant(tenantId);
        if (existing == null)
            return NotFound(new { error = $"Tenant '{tenantId}' not found" });

        tenant.TenantId = tenantId;
        tenant.CreatedAt = existing.CreatedAt;
        await _configService.SaveTenantAsync(tenant);

        _logger.LogInformation("Tenant {TenantId} updated", tenantId);
        return Ok(new { message = "Tenant updated" });
    }

    /// <summary>
    /// Deletes a tenant.
    /// </summary>
    [HttpDelete("tenants/{tenantId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteTenant(string tenantId)
    {
        var tenant = _configService.GetTenant(tenantId);
        if (tenant == null)
            return NotFound(new { error = $"Tenant '{tenantId}' not found" });

        await _configService.DeleteTenantAsync(tenantId);
        _logger.LogInformation("Tenant {TenantId} deleted", tenantId);
        return NoContent();
    }

    /// <summary>
    /// Gets plugin-specific configuration.
    /// </summary>
    [HttpGet("plugins/{pluginId}")]
    [ProducesResponseType(typeof(Dictionary<string, object>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<Dictionary<string, object>> GetPluginConfiguration(string pluginId)
    {
        var config = _configService.GetPluginConfiguration(pluginId);
        if (config == null)
            return NotFound(new { error = $"No configuration found for plugin '{pluginId}'" });

        return Ok(config);
    }

    /// <summary>
    /// Updates plugin-specific configuration.
    /// </summary>
    [HttpPut("plugins/{pluginId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> UpdatePluginConfiguration(string pluginId, [FromBody] Dictionary<string, object> config)
    {
        await _configService.UpdatePluginConfigurationAsync(pluginId, config);
        _logger.LogInformation("Plugin {PluginId} configuration updated", pluginId);
        return Ok(new { message = "Plugin configuration updated" });
    }
}
