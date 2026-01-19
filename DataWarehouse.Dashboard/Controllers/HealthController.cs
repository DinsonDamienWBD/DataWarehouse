using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DataWarehouse.Dashboard.Services;
using DataWarehouse.Dashboard.Security;
using DataWarehouse.SDK.Infrastructure;

namespace DataWarehouse.Dashboard.Controllers;

/// <summary>
/// API controller for system health and metrics.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize(Policy = AuthorizationPolicies.Authenticated)]
public class HealthController : ControllerBase
{
    private readonly ISystemHealthService _healthService;
    private readonly ILogger<HealthController> _logger;

    public HealthController(ISystemHealthService healthService, ILogger<HealthController> logger)
    {
        _healthService = healthService;
        _logger = logger;
    }

    /// <summary>
    /// Gets overall system health status.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(SystemHealthStatus), StatusCodes.Status200OK)]
    public async Task<ActionResult<SystemHealthStatus>> GetHealth()
    {
        var health = await _healthService.GetSystemHealthAsync();
        return Ok(health);
    }

    /// <summary>
    /// Gets simple health check for load balancers.
    /// </summary>
    [HttpGet("ping")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> Ping()
    {
        var health = await _healthService.GetSystemHealthAsync();
        if (health.OverallStatus == HealthStatus.Critical)
            return StatusCode(503, new { status = "unhealthy" });

        return Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
    }

    /// <summary>
    /// Gets detailed component health.
    /// </summary>
    [HttpGet("components")]
    [ProducesResponseType(typeof(IEnumerable<ComponentHealth>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<ComponentHealth>>> GetComponentHealth()
    {
        var health = await _healthService.GetSystemHealthAsync();
        return Ok(health.Components);
    }

    /// <summary>
    /// Gets current system metrics.
    /// </summary>
    [HttpGet("metrics")]
    [ProducesResponseType(typeof(SystemMetrics), StatusCodes.Status200OK)]
    public ActionResult<SystemMetrics> GetMetrics()
    {
        var metrics = _healthService.GetCurrentMetrics();
        return Ok(metrics);
    }

    /// <summary>
    /// Gets historical metrics.
    /// </summary>
    [HttpGet("metrics/history")]
    [ProducesResponseType(typeof(IEnumerable<SystemMetrics>), StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<SystemMetrics>> GetMetricsHistory(
        [FromQuery] int minutes = 60,
        [FromQuery] int resolution = 60)
    {
        var history = _healthService.GetMetricsHistory(TimeSpan.FromMinutes(minutes), TimeSpan.FromSeconds(resolution));
        return Ok(history);
    }

    /// <summary>
    /// Gets system alerts.
    /// </summary>
    [HttpGet("alerts")]
    [ProducesResponseType(typeof(IEnumerable<SystemAlert>), StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<SystemAlert>> GetAlerts([FromQuery] bool activeOnly = true)
    {
        var alerts = _healthService.GetAlerts(activeOnly);
        return Ok(alerts);
    }

    /// <summary>
    /// Acknowledges an alert.
    /// </summary>
    [HttpPost("alerts/{id}/acknowledge")]
    [Authorize(Policy = AuthorizationPolicies.OperatorOrAdmin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> AcknowledgeAlert(string id, [FromBody] AcknowledgeRequest request)
    {
        var success = await _healthService.AcknowledgeAlertAsync(id, request.AcknowledgedBy);
        if (!success)
            return NotFound(new { error = $"Alert '{id}' not found" });

        _logger.LogInformation("Alert {AlertId} acknowledged by {User}", id, request.AcknowledgedBy);
        return Ok(new { message = "Alert acknowledged" });
    }

    /// <summary>
    /// Clears an alert.
    /// </summary>
    [HttpPost("alerts/{id}/clear")]
    [Authorize(Policy = AuthorizationPolicies.OperatorOrAdmin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> ClearAlert(string id)
    {
        var success = await _healthService.ClearAlertAsync(id);
        if (!success)
            return NotFound(new { error = $"Alert '{id}' not found" });

        _logger.LogInformation("Alert {AlertId} cleared", id);
        return Ok(new { message = "Alert cleared" });
    }

    /// <summary>
    /// Gets system uptime information.
    /// </summary>
    [HttpGet("uptime")]
    [ProducesResponseType(typeof(UptimeInfo), StatusCodes.Status200OK)]
    public ActionResult<UptimeInfo> GetUptime()
    {
        var metrics = _healthService.GetCurrentMetrics();
        return Ok(new UptimeInfo
        {
            StartTime = DateTime.UtcNow.AddSeconds(-metrics.UptimeSeconds),
            UptimeSeconds = metrics.UptimeSeconds,
            UptimeFormatted = FormatUptime(TimeSpan.FromSeconds(metrics.UptimeSeconds))
        });
    }

    /// <summary>
    /// Gets resource usage statistics.
    /// </summary>
    [HttpGet("resources")]
    [ProducesResponseType(typeof(ResourceUsage), StatusCodes.Status200OK)]
    public ActionResult<ResourceUsage> GetResourceUsage()
    {
        var metrics = _healthService.GetCurrentMetrics();
        return Ok(new ResourceUsage
        {
            CpuPercent = metrics.CpuUsagePercent,
            MemoryUsedBytes = metrics.MemoryUsedBytes,
            MemoryTotalBytes = metrics.MemoryTotalBytes,
            MemoryPercent = metrics.MemoryTotalBytes > 0
                ? (double)metrics.MemoryUsedBytes / metrics.MemoryTotalBytes * 100
                : 0,
            DiskUsedBytes = metrics.DiskUsedBytes,
            DiskTotalBytes = metrics.DiskTotalBytes,
            DiskPercent = metrics.DiskTotalBytes > 0
                ? (double)metrics.DiskUsedBytes / metrics.DiskTotalBytes * 100
                : 0,
            ActiveConnections = metrics.ActiveConnections,
            ThreadCount = metrics.ThreadCount
        });
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
            return $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
        if (uptime.TotalHours >= 1)
            return $"{(int)uptime.TotalHours}h {uptime.Minutes}m";
        return $"{uptime.Minutes}m {uptime.Seconds}s";
    }
}

public class AcknowledgeRequest
{
    public string AcknowledgedBy { get; set; } = "admin";
}

public class UptimeInfo
{
    public DateTime StartTime { get; set; }
    public long UptimeSeconds { get; set; }
    public string UptimeFormatted { get; set; } = string.Empty;
}

public class ResourceUsage
{
    public double CpuPercent { get; set; }
    public long MemoryUsedBytes { get; set; }
    public long MemoryTotalBytes { get; set; }
    public double MemoryPercent { get; set; }
    public long DiskUsedBytes { get; set; }
    public long DiskTotalBytes { get; set; }
    public double DiskPercent { get; set; }
    public int ActiveConnections { get; set; }
    public int ThreadCount { get; set; }
}
