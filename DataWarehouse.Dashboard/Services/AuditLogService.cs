using System.Collections.Concurrent;

namespace DataWarehouse.Dashboard.Services;

/// <summary>
/// Service for audit logging and retrieval.
/// </summary>
public interface IAuditLogService
{
    /// <summary>
    /// Gets recent audit log entries.
    /// </summary>
    IEnumerable<AuditLogEntry> GetRecentLogs(int count = 100);

    /// <summary>
    /// Queries audit logs with filters.
    /// </summary>
    IEnumerable<AuditLogEntry> QueryLogs(AuditLogQuery query);

    /// <summary>
    /// Logs an audit event.
    /// </summary>
    void Log(AuditLogEntry entry);

    /// <summary>
    /// Gets log statistics.
    /// </summary>
    AuditLogStats GetStats(TimeSpan period);

    /// <summary>
    /// Event raised when new entry is logged.
    /// </summary>
    event EventHandler<AuditLogEntry>? EntryLogged;
}

public class AuditLogEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
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
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }
    public TimeSpan? Duration { get; set; }
}

public enum AuditSeverity
{
    Debug,
    Info,
    Warning,
    Error,
    Critical,
    Security
}

public class AuditLogQuery
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

public class AuditLogStats
{
    public int TotalEntries { get; set; }
    public int SuccessfulOperations { get; set; }
    public int FailedOperations { get; set; }
    public Dictionary<string, int> EntriesByCategory { get; set; } = new();
    public Dictionary<AuditSeverity, int> EntriesBySeverity { get; set; } = new();
    public Dictionary<string, int> TopActions { get; set; } = new();
    public Dictionary<string, int> TopUsers { get; set; } = new();
    public int SecurityEvents { get; set; }
}

/// <summary>
/// Implementation of audit log service.
/// </summary>
public class AuditLogService : IAuditLogService
{
    private readonly ConcurrentQueue<AuditLogEntry> _logs = new();
    private const int MaxLogEntries = 10000;

    public event EventHandler<AuditLogEntry>? EntryLogged;

    public AuditLogService()
    {
        // Add some sample entries
        GenerateSampleLogs();
    }

    public IEnumerable<AuditLogEntry> GetRecentLogs(int count = 100)
    {
        return _logs.OrderByDescending(l => l.Timestamp).Take(count);
    }

    public IEnumerable<AuditLogEntry> QueryLogs(AuditLogQuery query)
    {
        var result = _logs.AsEnumerable();

        if (query.StartTime.HasValue)
            result = result.Where(l => l.Timestamp >= query.StartTime.Value);

        if (query.EndTime.HasValue)
            result = result.Where(l => l.Timestamp <= query.EndTime.Value);

        if (!string.IsNullOrEmpty(query.Category))
            result = result.Where(l => l.Category.Equals(query.Category, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(query.Action))
            result = result.Where(l => l.Action.Contains(query.Action, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(query.UserId))
            result = result.Where(l => l.UserId == query.UserId);

        if (!string.IsNullOrEmpty(query.TenantId))
            result = result.Where(l => l.TenantId == query.TenantId);

        if (query.MinSeverity.HasValue)
            result = result.Where(l => l.Severity >= query.MinSeverity.Value);

        if (query.SuccessOnly.HasValue)
            result = result.Where(l => l.Success == query.SuccessOnly.Value);

        if (!string.IsNullOrEmpty(query.SearchText))
            result = result.Where(l =>
                l.Message.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase) ||
                l.Action.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase));

        return result
            .OrderByDescending(l => l.Timestamp)
            .Skip(query.Skip)
            .Take(query.Take);
    }

    public void Log(AuditLogEntry entry)
    {
        _logs.Enqueue(entry);

        // Trim old entries
        while (_logs.Count > MaxLogEntries && _logs.TryDequeue(out _)) { }

        EntryLogged?.Invoke(this, entry);
    }

    public AuditLogStats GetStats(TimeSpan period)
    {
        var cutoff = DateTime.UtcNow - period;
        var entries = _logs.Where(l => l.Timestamp >= cutoff).ToList();

        return new AuditLogStats
        {
            TotalEntries = entries.Count,
            SuccessfulOperations = entries.Count(e => e.Success),
            FailedOperations = entries.Count(e => !e.Success),
            EntriesByCategory = entries.GroupBy(e => e.Category)
                .ToDictionary(g => g.Key, g => g.Count()),
            EntriesBySeverity = entries.GroupBy(e => e.Severity)
                .ToDictionary(g => g.Key, g => g.Count()),
            TopActions = entries.GroupBy(e => e.Action)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .ToDictionary(g => g.Key, g => g.Count()),
            TopUsers = entries.Where(e => !string.IsNullOrEmpty(e.UserName))
                .GroupBy(e => e.UserName!)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .ToDictionary(g => g.Key, g => g.Count()),
            SecurityEvents = entries.Count(e => e.Severity == AuditSeverity.Security)
        };
    }

    private void GenerateSampleLogs()
    {
        var categories = new[] { "Storage", "Plugin", "Security", "Config", "User" };
        var actions = new[] { "Create", "Read", "Update", "Delete", "Login", "Logout", "Configure", "Enable", "Disable" };
        var users = new[] { "admin", "system", "user1", "user2", "service-account" };

        for (int i = 0; i < 200; i++)
        {
            var entry = new AuditLogEntry
            {
                Timestamp = DateTime.UtcNow.AddMinutes(-Random.Shared.Next(0, 1440)),
                Category = categories[Random.Shared.Next(categories.Length)],
                Action = actions[Random.Shared.Next(actions.Length)],
                UserName = users[Random.Shared.Next(users.Length)],
                UserId = $"user-{Random.Shared.Next(1, 6)}",
                Message = $"Sample audit log entry #{i + 1}",
                Severity = (AuditSeverity)Random.Shared.Next(0, 5),
                Success = Random.Shared.Next(10) > 1, // 90% success rate
                Duration = TimeSpan.FromMilliseconds(Random.Shared.Next(10, 5000))
            };

            _logs.Enqueue(entry);
        }
    }
}
