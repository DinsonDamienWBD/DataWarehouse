using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DataWarehouse.Dashboard.Services;
using DataWarehouse.Dashboard.Security;
using DataWarehouse.Dashboard.Models;

namespace DataWarehouse.Dashboard.Controllers;

/// <summary>
/// API controller for audit logging.
/// Audit logs are sensitive and require elevated permissions.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize(Policy = AuthorizationPolicies.OperatorOrAdmin)]
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
    /// Gets recent audit log entries with pagination.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PaginatedResponse<AuditLogEntry>), StatusCodes.Status200OK)]
    public ActionResult<PaginatedResponse<AuditLogEntry>> GetRecentLogs([FromQuery] PaginationQuery pagination)
    {
        var allLogs = _auditService.GetRecentLogs(pagination.PageSize * pagination.Page + 100);

        // Apply filtering if search query provided
        if (!string.IsNullOrWhiteSpace(pagination.Query))
        {
            allLogs = allLogs.Where(l =>
                l.Message.Contains(pagination.Query, StringComparison.OrdinalIgnoreCase) ||
                (l.Category?.Contains(pagination.Query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (l.Action?.Contains(pagination.Query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var response = allLogs.ToPaginated(pagination);
        Response.AddPaginationHeaders(response);
        return Ok(response);
    }

    /// <summary>
    /// Queries audit logs with filters and pagination.
    /// </summary>
    [HttpGet("query")]
    [ProducesResponseType(typeof(PaginatedResponse<AuditLogEntry>), StatusCodes.Status200OK)]
    public ActionResult<PaginatedResponse<AuditLogEntry>> QueryLogs(
        [FromQuery] AuditLogQueryRequest request,
        [FromQuery] PaginationQuery pagination)
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
            SearchText = request.SearchText ?? pagination.Query,
            Skip = 0,
            Take = int.MaxValue // Get all matching, then paginate
        };

        var allLogs = _auditService.QueryLogs(query).ToList();
        var response = allLogs.ToPaginated(pagination);

        Response.AddPaginationHeaders(response);
        return Ok(response);
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
    /// Gets entries by category with pagination.
    /// </summary>
    [HttpGet("categories/{category}")]
    [ProducesResponseType(typeof(PaginatedResponse<AuditLogEntry>), StatusCodes.Status200OK)]
    public ActionResult<PaginatedResponse<AuditLogEntry>> GetByCategory(string category, [FromQuery] PaginationQuery pagination)
    {
        var query = new AuditLogQuery { Category = category, Take = int.MaxValue };
        var logs = _auditService.QueryLogs(query);
        var response = logs.ToPaginated(pagination);
        Response.AddPaginationHeaders(response);
        return Ok(response);
    }

    /// <summary>
    /// Gets entries by user with pagination.
    /// </summary>
    [HttpGet("users/{userId}")]
    [ProducesResponseType(typeof(PaginatedResponse<AuditLogEntry>), StatusCodes.Status200OK)]
    public ActionResult<PaginatedResponse<AuditLogEntry>> GetByUser(string userId, [FromQuery] PaginationQuery pagination)
    {
        var query = new AuditLogQuery { UserId = userId, Take = int.MaxValue };
        var logs = _auditService.QueryLogs(query);
        var response = logs.ToPaginated(pagination);
        Response.AddPaginationHeaders(response);
        return Ok(response);
    }

    /// <summary>
    /// Gets security-related entries with pagination.
    /// </summary>
    [HttpGet("security")]
    [ProducesResponseType(typeof(PaginatedResponse<AuditLogEntry>), StatusCodes.Status200OK)]
    public ActionResult<PaginatedResponse<AuditLogEntry>> GetSecurityLogs([FromQuery] PaginationQuery pagination)
    {
        var query = new AuditLogQuery { MinSeverity = AuditSeverity.Security, Take = int.MaxValue };
        var logs = _auditService.QueryLogs(query);
        var response = logs.ToPaginated(pagination);
        Response.AddPaginationHeaders(response);
        return Ok(response);
    }

    /// <summary>
    /// Gets failed operations with pagination.
    /// </summary>
    [HttpGet("failures")]
    [ProducesResponseType(typeof(PaginatedResponse<AuditLogEntry>), StatusCodes.Status200OK)]
    public ActionResult<PaginatedResponse<AuditLogEntry>> GetFailures([FromQuery] PaginationQuery pagination)
    {
        var query = new AuditLogQuery { SuccessOnly = false, Take = int.MaxValue };
        var logs = _auditService.QueryLogs(query).Where(e => !e.Success);
        var response = logs.ToPaginated(pagination);
        Response.AddPaginationHeaders(response);
        return Ok(response);
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
