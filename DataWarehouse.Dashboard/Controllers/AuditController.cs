using Microsoft.AspNetCore.Mvc;
using DataWarehouse.Dashboard.Services;

namespace DataWarehouse.Dashboard.Controllers;

/// <summary>
/// API controller for audit logging.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class AuditController : ControllerBase
{
    private readonly IAuditLogService _auditService;
    private readonly ILogger<AuditController> _logger;

    public AuditController(IAuditLogService auditService, ILogger<AuditController> logger)
    {
        _auditService = auditService;
        _logger = logger;
    }

    /// <summary>
    /// Gets recent audit log entries.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<AuditLogEntry>), StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<AuditLogEntry>> GetRecentLogs([FromQuery] int count = 100)
    {
        var logs = _auditService.GetRecentLogs(count);
        return Ok(logs);
    }

    /// <summary>
    /// Queries audit logs with filters.
    /// </summary>
    [HttpGet("query")]
    [ProducesResponseType(typeof(AuditQueryResult), StatusCodes.Status200OK)]
    public ActionResult<AuditQueryResult> QueryLogs([FromQuery] AuditLogQueryRequest request)
    {
        var query = new AuditLogQuery
        {
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            Category = request.Category,
            Action = request.Action,
            UserId = request.UserId,
            TenantId = request.TenantId,
            MinSeverity = request.MinSeverity,
            SuccessOnly = request.SuccessOnly,
            SearchText = request.SearchText,
            Skip = request.Skip,
            Take = request.Take
        };

        var logs = _auditService.QueryLogs(query).ToList();

        return Ok(new AuditQueryResult
        {
            Entries = logs,
            Count = logs.Count,
            Skip = query.Skip,
            Take = query.Take
        });
    }

    /// <summary>
    /// Creates a new audit log entry.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(AuditLogEntry), StatusCodes.Status201Created)]
    public ActionResult<AuditLogEntry> CreateLog([FromBody] CreateAuditLogRequest request)
    {
        var entry = new AuditLogEntry
        {
            Category = request.Category,
            Action = request.Action,
            UserId = request.UserId,
            UserName = request.UserName,
            TenantId = request.TenantId,
            ResourceType = request.ResourceType,
            ResourceId = request.ResourceId,
            Severity = request.Severity,
            Message = request.Message,
            Details = request.Details,
            Success = request.Success,
            ErrorMessage = request.ErrorMessage,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
        };

        _auditService.Log(entry);
        return CreatedAtAction(nameof(GetRecentLogs), entry);
    }

    /// <summary>
    /// Gets audit log statistics.
    /// </summary>
    [HttpGet("stats")]
    [ProducesResponseType(typeof(AuditLogStats), StatusCodes.Status200OK)]
    public ActionResult<AuditLogStats> GetStats([FromQuery] int hours = 24)
    {
        var stats = _auditService.GetStats(TimeSpan.FromHours(hours));
        return Ok(stats);
    }

    /// <summary>
    /// Gets entries by category.
    /// </summary>
    [HttpGet("categories/{category}")]
    [ProducesResponseType(typeof(IEnumerable<AuditLogEntry>), StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<AuditLogEntry>> GetByCategory(string category, [FromQuery] int count = 100)
    {
        var query = new AuditLogQuery { Category = category, Take = count };
        var logs = _auditService.QueryLogs(query);
        return Ok(logs);
    }

    /// <summary>
    /// Gets entries by user.
    /// </summary>
    [HttpGet("users/{userId}")]
    [ProducesResponseType(typeof(IEnumerable<AuditLogEntry>), StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<AuditLogEntry>> GetByUser(string userId, [FromQuery] int count = 100)
    {
        var query = new AuditLogQuery { UserId = userId, Take = count };
        var logs = _auditService.QueryLogs(query);
        return Ok(logs);
    }

    /// <summary>
    /// Gets security-related entries.
    /// </summary>
    [HttpGet("security")]
    [ProducesResponseType(typeof(IEnumerable<AuditLogEntry>), StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<AuditLogEntry>> GetSecurityLogs([FromQuery] int count = 100)
    {
        var query = new AuditLogQuery { MinSeverity = AuditSeverity.Security, Take = count };
        var logs = _auditService.QueryLogs(query);
        return Ok(logs);
    }

    /// <summary>
    /// Gets failed operations.
    /// </summary>
    [HttpGet("failures")]
    [ProducesResponseType(typeof(IEnumerable<AuditLogEntry>), StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<AuditLogEntry>> GetFailures([FromQuery] int count = 100)
    {
        var query = new AuditLogQuery { SuccessOnly = false, Take = count };
        var logs = _auditService.QueryLogs(query).Where(e => !e.Success);
        return Ok(logs);
    }
}

public class AuditLogQueryRequest
{
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? Category { get; set; }
    public string? Action { get; set; }
    public string? UserId { get; set; }
    public string? TenantId { get; set; }
    public AuditSeverity? MinSeverity { get; set; }
    public bool? SuccessOnly { get; set; }
    public string? SearchText { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 100;
}

public class CreateAuditLogRequest
{
    public string Category { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? TenantId { get; set; }
    public string? ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public AuditSeverity Severity { get; set; } = AuditSeverity.Info;
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, object>? Details { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }
}

public class AuditQueryResult
{
    public IEnumerable<AuditLogEntry> Entries { get; set; } = Enumerable.Empty<AuditLogEntry>();
    public int Count { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
}
