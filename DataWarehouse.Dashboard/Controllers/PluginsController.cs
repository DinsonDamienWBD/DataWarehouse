using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DataWarehouse.Dashboard.Services;
using DataWarehouse.Dashboard.Security;

namespace DataWarehouse.Dashboard.Controllers;

/// <summary>
/// API controller for plugin management.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize(Policy = AuthorizationPolicies.Authenticated)]
public class PluginsController : ControllerBase
{
    private readonly IPluginDiscoveryService _pluginService;
    private readonly ILogger<PluginsController> _logger;

    public PluginsController(IPluginDiscoveryService pluginService, ILogger<PluginsController> logger)
    {
        _pluginService = pluginService;
        _logger = logger;
    }

    /// <summary>
    /// Gets all discovered plugins.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<PluginInfo>), StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<PluginInfo>> GetPlugins()
    {
        var plugins = _pluginService.GetDiscoveredPlugins();
        return Ok(plugins);
    }

    /// <summary>
    /// Gets active plugins only.
    /// </summary>
    [HttpGet("active")]
    [ProducesResponseType(typeof(IEnumerable<PluginInfo>), StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<PluginInfo>> GetActivePlugins()
    {
        var plugins = _pluginService.GetActivePlugins();
        return Ok(plugins);
    }

    /// <summary>
    /// Gets a specific plugin by ID.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(PluginInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<PluginInfo> GetPlugin(string id)
    {
        var plugin = _pluginService.GetPlugin(id);
        if (plugin == null)
            return NotFound(new { error = $"Plugin '{id}' not found" });

        return Ok(plugin);
    }

    /// <summary>
    /// Gets plugin capabilities.
    /// </summary>
    [HttpGet("{id}/capabilities")]
    [ProducesResponseType(typeof(PluginCapabilities), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<PluginCapabilities> GetPluginCapabilities(string id)
    {
        var plugin = _pluginService.GetPlugin(id);
        if (plugin == null)
            return NotFound(new { error = $"Plugin '{id}' not found" });

        return Ok(plugin.Capabilities);
    }

    /// <summary>
    /// Enables a plugin.
    /// </summary>
    [HttpPost("{id}/enable")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> EnablePlugin(string id)
    {
        var plugin = _pluginService.GetPlugin(id);
        if (plugin == null)
            return NotFound(new { error = $"Plugin '{id}' not found" });

        var success = await _pluginService.EnablePluginAsync(id);
        if (!success)
            return BadRequest(new { error = $"Failed to enable plugin '{id}'" });

        _logger.LogInformation("Plugin {PluginId} enabled", id);
        return Ok(new { message = $"Plugin '{id}' enabled successfully" });
    }

    /// <summary>
    /// Disables a plugin.
    /// </summary>
    [HttpPost("{id}/disable")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> DisablePlugin(string id)
    {
        var plugin = _pluginService.GetPlugin(id);
        if (plugin == null)
            return NotFound(new { error = $"Plugin '{id}' not found" });

        var success = await _pluginService.DisablePluginAsync(id);
        if (!success)
            return BadRequest(new { error = $"Failed to disable plugin '{id}'" });

        _logger.LogInformation("Plugin {PluginId} disabled", id);
        return Ok(new { message = $"Plugin '{id}' disabled successfully" });
    }

    /// <summary>
    /// Reloads plugin list from disk.
    /// </summary>
    [HttpPost("reload")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> ReloadPlugins()
    {
        await _pluginService.RefreshPluginsAsync();
        _logger.LogInformation("Plugins reloaded");
        return Ok(new { message = "Plugins reloaded successfully" });
    }

    /// <summary>
    /// Gets plugin configuration schema.
    /// </summary>
    [HttpGet("{id}/config/schema")]
    [ProducesResponseType(typeof(PluginConfigurationSchema), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<PluginConfigurationSchema> GetPluginConfigSchema(string id)
    {
        var schema = _pluginService.GetPluginConfigurationSchema(id);
        if (schema == null)
            return NotFound(new { error = $"Plugin '{id}' not found or has no configuration" });

        return Ok(schema);
    }

    /// <summary>
    /// Updates plugin configuration.
    /// </summary>
    [HttpPut("{id}/config")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> UpdatePluginConfig(string id, [FromBody] Dictionary<string, object> config)
    {
        var plugin = _pluginService.GetPlugin(id);
        if (plugin == null)
            return NotFound(new { error = $"Plugin '{id}' not found" });

        var success = await _pluginService.UpdatePluginConfigAsync(id, config);
        if (!success)
            return BadRequest(new { error = $"Failed to update configuration for plugin '{id}'" });

        _logger.LogInformation("Plugin {PluginId} configuration updated", id);
        return Ok(new { message = $"Configuration updated for plugin '{id}'" });
    }
}
