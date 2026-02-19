using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts.Dashboards;

namespace DataWarehouse.Plugins.UniversalDashboards.Strategies;

/// <summary>
/// Pre-built dashboard templates for common use cases: Storage Overview, Security Audit,
/// Replication Status, System Health, and custom template creation.
/// </summary>
public sealed class DashboardTemplateService
{
    private readonly ConcurrentDictionary<string, DashboardTemplate> _templates = new();

    public DashboardTemplateService()
    {
        RegisterBuiltInTemplates();
    }

    /// <summary>
    /// Gets a template by ID.
    /// </summary>
    public DashboardTemplate? GetTemplate(string templateId) =>
        _templates.TryGetValue(templateId, out var template) ? template : null;

    /// <summary>
    /// Lists all available templates.
    /// </summary>
    public IReadOnlyList<DashboardTemplate> ListTemplates(string? category = null)
    {
        var query = _templates.Values.AsEnumerable();
        if (category != null)
            query = query.Where(t => t.Category == category);
        return query.OrderBy(t => t.Name).ToList().AsReadOnly();
    }

    /// <summary>
    /// Creates a dashboard instance from a template with variable substitution.
    /// </summary>
    public Dashboard InstantiateTemplate(string templateId, Dictionary<string, object>? variables = null)
    {
        if (!_templates.TryGetValue(templateId, out var template))
            throw new KeyNotFoundException($"Template '{templateId}' not found");

        var title = template.Name;
        if (variables?.TryGetValue("title", out var customTitle) == true)
            title = customTitle?.ToString() ?? title;

        var widgets = template.WidgetDefinitions.Select((wd, idx) =>
        {
            var query = SubstituteVariables(wd.Query, variables);
            var position = new WidgetPosition(wd.X, wd.Y, wd.Width, wd.Height);
            var dataSource = new DataSourceConfiguration(wd.DataSourceType, query, wd.Parameters);

            return new DashboardWidget(
                Id: Guid.NewGuid().ToString(),
                Title: wd.Title,
                Type: wd.WidgetType,
                Position: position,
                DataSource: dataSource,
                Configuration: wd.Configuration);
        }).ToList();

        return new Dashboard(
            Id: null,
            Title: title,
            Description: template.Description,
            Layout: DashboardLayout.Grid(template.Columns, template.RowHeight),
            Widgets: widgets,
            TimeRange: template.DefaultTimeRange ?? TimeRange.Last24Hours(),
            RefreshInterval: template.DefaultRefreshInterval,
            Tags: template.Tags?.ToList(),
            Owner: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Registers a custom template.
    /// </summary>
    public void RegisterTemplate(DashboardTemplate template)
    {
        _templates[template.TemplateId] = template;
    }

    private void RegisterBuiltInTemplates()
    {
        _templates["storage-overview"] = new DashboardTemplate
        {
            TemplateId = "storage-overview",
            Name = "Storage Overview",
            Description = "Comprehensive view of storage capacity, throughput, IOPS, and health across all storage backends.",
            Category = "Infrastructure",
            Tags = new[] { "storage", "infrastructure", "capacity" },
            Columns = 12,
            RowHeight = 100,
            DefaultRefreshInterval = 30,
            DefaultTimeRange = TimeRange.Last24Hours(),
            WidgetDefinitions = new[]
            {
                new TemplateWidgetDefinition { Title = "Total Capacity", WidgetType = WidgetType.Metric, DataSourceType = "Metrics", Query = "storage.total_capacity_bytes", X = 0, Y = 0, Width = 3, Height = 2 },
                new TemplateWidgetDefinition { Title = "Used Space", WidgetType = WidgetType.Gauge, DataSourceType = "Metrics", Query = "storage.used_percentage", X = 3, Y = 0, Width = 3, Height = 2 },
                new TemplateWidgetDefinition { Title = "IOPS", WidgetType = WidgetType.Chart, DataSourceType = "Metrics", Query = "storage.iops_total", X = 6, Y = 0, Width = 3, Height = 2 },
                new TemplateWidgetDefinition { Title = "Throughput", WidgetType = WidgetType.Chart, DataSourceType = "Metrics", Query = "storage.throughput_mbps", X = 9, Y = 0, Width = 3, Height = 2 },
                new TemplateWidgetDefinition { Title = "Storage Health", WidgetType = WidgetType.Table, DataSourceType = "Metrics", Query = "storage.backend_health", X = 0, Y = 2, Width = 12, Height = 3 }
            }
        };

        _templates["security-audit"] = new DashboardTemplate
        {
            TemplateId = "security-audit",
            Name = "Security Audit",
            Description = "Security posture dashboard with access control events, threat detection, and compliance status.",
            Category = "Security",
            Tags = new[] { "security", "audit", "compliance" },
            Columns = 12,
            RowHeight = 100,
            DefaultRefreshInterval = 60,
            DefaultTimeRange = TimeRange.LastDays(7),
            WidgetDefinitions = new[]
            {
                new TemplateWidgetDefinition { Title = "Failed Auth Attempts", WidgetType = WidgetType.Metric, DataSourceType = "Metrics", Query = "security.failed_auth_count", X = 0, Y = 0, Width = 3, Height = 2 },
                new TemplateWidgetDefinition { Title = "Active Threats", WidgetType = WidgetType.Alert, DataSourceType = "Metrics", Query = "security.active_threats", X = 3, Y = 0, Width = 3, Height = 2 },
                new TemplateWidgetDefinition { Title = "Access Events", WidgetType = WidgetType.Chart, DataSourceType = "Metrics", Query = "security.access_events_by_hour", X = 6, Y = 0, Width = 6, Height = 2 },
                new TemplateWidgetDefinition { Title = "Audit Log", WidgetType = WidgetType.Log, DataSourceType = "Logs", Query = "security.audit_log | severity >= warning", X = 0, Y = 2, Width = 12, Height = 4 }
            }
        };

        _templates["replication-status"] = new DashboardTemplate
        {
            TemplateId = "replication-status",
            Name = "Replication Status",
            Description = "Replication health, lag, throughput, and conflict resolution metrics across all replication channels.",
            Category = "Infrastructure",
            Tags = new[] { "replication", "infrastructure", "sync" },
            Columns = 12,
            RowHeight = 100,
            DefaultRefreshInterval = 10,
            DefaultTimeRange = TimeRange.LastHours(6),
            WidgetDefinitions = new[]
            {
                new TemplateWidgetDefinition { Title = "Replication Lag", WidgetType = WidgetType.Gauge, DataSourceType = "Metrics", Query = "replication.max_lag_seconds", X = 0, Y = 0, Width = 4, Height = 2 },
                new TemplateWidgetDefinition { Title = "Sync Throughput", WidgetType = WidgetType.Chart, DataSourceType = "Metrics", Query = "replication.throughput_mbps", X = 4, Y = 0, Width = 4, Height = 2 },
                new TemplateWidgetDefinition { Title = "Conflicts", WidgetType = WidgetType.Metric, DataSourceType = "Metrics", Query = "replication.conflict_count", X = 8, Y = 0, Width = 4, Height = 2 },
                new TemplateWidgetDefinition { Title = "Channel Status", WidgetType = WidgetType.Table, DataSourceType = "Metrics", Query = "replication.channel_status", X = 0, Y = 2, Width = 12, Height = 3 }
            }
        };

        _templates["system-health"] = new DashboardTemplate
        {
            TemplateId = "system-health",
            Name = "System Health",
            Description = "Overall system health including CPU, memory, disk, network, and plugin status.",
            Category = "Operations",
            Tags = new[] { "health", "operations", "monitoring" },
            Columns = 12,
            RowHeight = 100,
            DefaultRefreshInterval = 5,
            DefaultTimeRange = TimeRange.LastHours(1),
            WidgetDefinitions = new[]
            {
                new TemplateWidgetDefinition { Title = "CPU Usage", WidgetType = WidgetType.Gauge, DataSourceType = "Metrics", Query = "system.cpu_usage_percent", X = 0, Y = 0, Width = 3, Height = 2 },
                new TemplateWidgetDefinition { Title = "Memory Usage", WidgetType = WidgetType.Gauge, DataSourceType = "Metrics", Query = "system.memory_usage_percent", X = 3, Y = 0, Width = 3, Height = 2 },
                new TemplateWidgetDefinition { Title = "Disk I/O", WidgetType = WidgetType.Chart, DataSourceType = "Metrics", Query = "system.disk_io_rate", X = 6, Y = 0, Width = 3, Height = 2 },
                new TemplateWidgetDefinition { Title = "Network I/O", WidgetType = WidgetType.Chart, DataSourceType = "Metrics", Query = "system.network_io_rate", X = 9, Y = 0, Width = 3, Height = 2 },
                new TemplateWidgetDefinition { Title = "Plugin Health", WidgetType = WidgetType.Table, DataSourceType = "Metrics", Query = "system.plugin_health_status", X = 0, Y = 2, Width = 6, Height = 3 },
                new TemplateWidgetDefinition { Title = "Recent Errors", WidgetType = WidgetType.Log, DataSourceType = "Logs", Query = "system.errors | last 100", X = 6, Y = 2, Width = 6, Height = 3 }
            }
        };
    }

    private static string SubstituteVariables(string query, Dictionary<string, object>? variables)
    {
        if (variables == null || variables.Count == 0) return query;

        var result = query;
        foreach (var (key, value) in variables)
        {
            result = result.Replace($"{{{{{key}}}}}", value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }
}

/// <summary>
/// Represents a reusable dashboard template.
/// </summary>
public sealed record DashboardTemplate
{
    public required string TemplateId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Category { get; init; }
    public string[]? Tags { get; init; }
    public int Columns { get; init; } = 12;
    public int RowHeight { get; init; } = 100;
    public int? DefaultRefreshInterval { get; init; }
    public TimeRange? DefaultTimeRange { get; init; }
    public TemplateWidgetDefinition[] WidgetDefinitions { get; init; } = Array.Empty<TemplateWidgetDefinition>();
}

/// <summary>
/// Widget definition within a template.
/// </summary>
public sealed record TemplateWidgetDefinition
{
    public required string Title { get; init; }
    public WidgetType WidgetType { get; init; }
    public required string DataSourceType { get; init; }
    public required string Query { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; } = 6;
    public int Height { get; init; } = 3;
    public IReadOnlyDictionary<string, object>? Parameters { get; init; }
    public IReadOnlyDictionary<string, object>? Configuration { get; init; }
}
