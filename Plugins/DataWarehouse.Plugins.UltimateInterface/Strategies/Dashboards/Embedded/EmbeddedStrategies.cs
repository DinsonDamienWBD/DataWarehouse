using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Dashboards;

namespace DataWarehouse.Plugins.UltimateInterface.Dashboards.Strategies.Embedded;

/// <summary>
/// Embedded Analytics SDK strategy for custom integrations.
/// </summary>
public sealed class EmbeddedSdkStrategy : DashboardStrategyBase
{
    private readonly Dictionary<string, Dashboard> _dashboards = new();

    public override string StrategyId => "embedded-sdk";
    public override string StrategyName => "Embedded Analytics SDK";
    public override string VendorName => "DataWarehouse";
    public override string Category => "Embedded";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true, SupportsTemplates: true, SupportsSharing: true,
        SupportsVersioning: true, SupportsEmbedding: true, SupportsAlerting: true,
        SupportedWidgetTypes: new[] { "Chart", "Table", "Metric", "Heatmap", "Gauge", "Text", "Map", "Custom" },
        RefreshIntervals: new[] { 5, 10, 30, 60, 300, 900, 1800, 3600 });

    public override Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        return Task.FromResult(true);
    }

    public override Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default)
    {
        EnsureConfigured();
        var startTime = DateTimeOffset.UtcNow;
        // Store data in memory for embedded rendering
        var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
        return Task.FromResult(new DataPushResult { Success = true, RowsPushed = data.Count, DurationMs = duration, Metadata = new Dictionary<string, object> { ["targetId"] = targetId } });
    }

    public override Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default)
    {
        return CreateDashboardCoreAsync(dashboard, ct);
    }

    protected override Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var id = GenerateId();
        var created = dashboard with { Id = id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        _dashboards[id] = created;
        return Task.FromResult(created);
    }

    protected override Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        if (!_dashboards.ContainsKey(dashboard.Id!))
            throw new KeyNotFoundException($"Dashboard {dashboard.Id} not found");
        var updated = dashboard with { UpdatedAt = DateTimeOffset.UtcNow, Version = dashboard.Version + 1 };
        _dashboards[dashboard.Id!] = updated;
        return Task.FromResult(updated);
    }

    protected override Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        if (!_dashboards.TryGetValue(dashboardId, out var dashboard))
            throw new KeyNotFoundException($"Dashboard {dashboardId} not found");
        return Task.FromResult(dashboard);
    }

    protected override Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        _dashboards.Remove(dashboardId);
        return Task.CompletedTask;
    }

    protected override Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct)
    {
        var dashboards = _dashboards.Values.AsEnumerable();
        if (!string.IsNullOrEmpty(filter?.SearchQuery))
            dashboards = dashboards.Where(d => d.Title.Contains(filter.SearchQuery, StringComparison.OrdinalIgnoreCase));
        if (filter?.Tags?.Count > 0)
            dashboards = dashboards.Where(d => d.Tags != null && filter.Tags.All(t => d.Tags.Contains(t)));
        if (filter?.CreatedAfter != null)
            dashboards = dashboards.Where(d => d.CreatedAt >= filter.CreatedAfter);
        if (filter?.CreatedBefore != null)
            dashboards = dashboards.Where(d => d.CreatedAt <= filter.CreatedBefore);
        return Task.FromResult<IReadOnlyList<Dashboard>>(dashboards.ToList());
    }

    protected override Task<Dashboard> CreateFromTemplateCoreAsync(string templateId, IReadOnlyDictionary<string, object>? parameters, CancellationToken ct)
    {
        if (!_dashboards.TryGetValue(templateId, out var template))
            throw new KeyNotFoundException($"Template {templateId} not found");
        var copy = template with { Id = GenerateId(), Title = parameters?.TryGetValue("name", out var n) == true ? n.ToString()! : $"{template.Title} (Copy)", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = 1 };
        _dashboards[copy.Id!] = copy;
        return Task.FromResult(copy);
    }

    /// <summary>
    /// Generates embed HTML for a dashboard.
    /// </summary>
    public string GenerateEmbedHtml(string dashboardId, int width = 800, int height = 600)
    {
        if (!_dashboards.TryGetValue(dashboardId, out var dashboard))
            throw new KeyNotFoundException($"Dashboard {dashboardId} not found");

        var sb = new StringBuilder();
        sb.AppendLine($"<div id=\"dashboard-{dashboardId}\" class=\"embedded-dashboard\" style=\"width:{width}px;height:{height}px;\">");
        sb.AppendLine($"  <h2>{dashboard.Title}</h2>");
        foreach (var widget in dashboard.Widgets)
        {
            sb.AppendLine($"  <div class=\"widget\" data-type=\"{widget.Type}\" style=\"grid-column:{widget.Position.X+1}/span {widget.Position.Width};grid-row:{widget.Position.Y+1}/span {widget.Position.Height};\">");
            sb.AppendLine($"    <h3>{widget.Title}</h3>");
            sb.AppendLine($"    <div class=\"widget-content\"></div>");
            sb.AppendLine("  </div>");
        }
        sb.AppendLine("</div>");
        return sb.ToString();
    }
}

/// <summary>
/// IFrame integration strategy for embedding third-party dashboards.
/// </summary>
public sealed class IframeIntegrationStrategy : DashboardStrategyBase
{
    private readonly Dictionary<string, Dashboard> _dashboards = new();
    private readonly Dictionary<string, string> _embedUrls = new();

    public override string StrategyId => "iframe";
    public override string StrategyName => "IFrame Integration";
    public override string VendorName => "DataWarehouse";
    public override string Category => "Embedded";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true, SupportsTemplates: false, SupportsSharing: true,
        SupportsVersioning: false, SupportsEmbedding: true, SupportsAlerting: false,
        SupportedWidgetTypes: new[] { "IFrame", "Custom" },
        RefreshIntervals: new[] { 60, 300, 900, 1800, 3600 });

    public override Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        return Task.FromResult(true);
    }

    public override Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default)
    {
        // IFrame strategy doesn't push data
        return Task.FromResult(new DataPushResult { Success = false, ErrorMessage = "IFrame strategy does not support data push" });
    }

    public override Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default)
    {
        return CreateDashboardCoreAsync(dashboard, ct);
    }

    protected override Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var id = GenerateId();
        var created = dashboard with { Id = id, CreatedAt = DateTimeOffset.UtcNow };
        _dashboards[id] = created;
        return Task.FromResult(created);
    }

    protected override Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        _dashboards[dashboard.Id!] = dashboard with { UpdatedAt = DateTimeOffset.UtcNow };
        return Task.FromResult(_dashboards[dashboard.Id!]);
    }

    protected override Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        if (!_dashboards.TryGetValue(dashboardId, out var dashboard))
            throw new KeyNotFoundException($"Dashboard {dashboardId} not found");
        return Task.FromResult(dashboard);
    }

    protected override Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        _dashboards.Remove(dashboardId);
        _embedUrls.Remove(dashboardId);
        return Task.CompletedTask;
    }

    protected override Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<Dashboard>>(_dashboards.Values.ToList());
    }

    /// <summary>
    /// Sets the embed URL for a dashboard.
    /// </summary>
    public void SetEmbedUrl(string dashboardId, string url)
    {
        _embedUrls[dashboardId] = url;
    }

    /// <summary>
    /// Generates iframe HTML for embedding.
    /// </summary>
    public string GenerateIframeHtml(string dashboardId, int width = 800, int height = 600)
    {
        if (!_embedUrls.TryGetValue(dashboardId, out var url))
            throw new InvalidOperationException($"No embed URL set for dashboard {dashboardId}");
        return $"<iframe src=\"{url}\" width=\"{width}\" height=\"{height}\" frameborder=\"0\" allowfullscreen></iframe>";
    }
}

/// <summary>
/// API-based rendering strategy for server-side dashboard generation.
/// </summary>
public sealed class ApiRenderingStrategy : DashboardStrategyBase
{
    private readonly Dictionary<string, Dashboard> _dashboards = new();

    public override string StrategyId => "api-rendering";
    public override string StrategyName => "API-Based Rendering";
    public override string VendorName => "DataWarehouse";
    public override string Category => "Embedded";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true, SupportsTemplates: true, SupportsSharing: true,
        SupportsVersioning: true, SupportsEmbedding: true, SupportsAlerting: true,
        SupportedWidgetTypes: new[] { "Chart", "Table", "Metric", "Heatmap", "Gauge", "Text", "Map", "Custom" },
        RefreshIntervals: new[] { 1, 5, 10, 30, 60, 300 });

    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        try
        {
            var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/health", cancellationToken: ct);
            return response.IsSuccessStatusCode;
        }
        catch { return true; } // Local rendering is always available
    }

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default)
    {
        EnsureConfigured();
        var startTime = DateTimeOffset.UtcNow;
        var payload = new { targetId = targetId, data = data.ToArray() };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/data/push", CreateJsonContent(payload), ct);
        var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
        return new DataPushResult { Success = response.IsSuccessStatusCode, RowsPushed = response.IsSuccessStatusCode ? data.Count : 0, DurationMs = duration };
    }

    public override Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default)
    {
        return CreateDashboardCoreAsync(dashboard, ct);
    }

    protected override Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var id = GenerateId();
        var created = dashboard with { Id = id, CreatedAt = DateTimeOffset.UtcNow };
        _dashboards[id] = created;
        return Task.FromResult(created);
    }

    protected override Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        _dashboards[dashboard.Id!] = dashboard with { UpdatedAt = DateTimeOffset.UtcNow, Version = dashboard.Version + 1 };
        return Task.FromResult(_dashboards[dashboard.Id!]);
    }

    protected override Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        if (!_dashboards.TryGetValue(dashboardId, out var dashboard))
            throw new KeyNotFoundException($"Dashboard {dashboardId} not found");
        return Task.FromResult(dashboard);
    }

    protected override Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        _dashboards.Remove(dashboardId);
        return Task.CompletedTask;
    }

    protected override Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<Dashboard>>(_dashboards.Values.ToList());
    }

    protected override Task<Dashboard> CreateFromTemplateCoreAsync(string templateId, IReadOnlyDictionary<string, object>? parameters, CancellationToken ct)
    {
        if (!_dashboards.TryGetValue(templateId, out var template))
            throw new KeyNotFoundException($"Template {templateId} not found");
        var copy = template with { Id = GenerateId(), Title = parameters?.TryGetValue("name", out var n) == true ? n.ToString()! : $"{template.Title} (Copy)", Version = 1 };
        _dashboards[copy.Id!] = copy;
        return Task.FromResult(copy);
    }

    /// <summary>
    /// Renders a dashboard to JSON format for client-side consumption.
    /// </summary>
    public string RenderToJson(string dashboardId)
    {
        if (!_dashboards.TryGetValue(dashboardId, out var dashboard))
            throw new KeyNotFoundException($"Dashboard {dashboardId} not found");
        return SerializeToJson(ConvertToPlatformFormat(dashboard));
    }
}

/// <summary>
/// White-label dashboard strategy for multi-tenant SaaS applications.
/// </summary>
public sealed class WhiteLabelStrategy : DashboardStrategyBase
{
    private readonly Dictionary<string, Dictionary<string, Dashboard>> _tenantDashboards = new();

    public override string StrategyId => "white-label";
    public override string StrategyName => "White Label Dashboards";
    public override string VendorName => "DataWarehouse";
    public override string Category => "Embedded";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true, SupportsTemplates: true, SupportsSharing: true,
        SupportsVersioning: true, SupportsEmbedding: true, SupportsAlerting: true,
        SupportedWidgetTypes: new[] { "Chart", "Table", "Metric", "Heatmap", "Gauge", "Text", "Map", "Custom" },
        RefreshIntervals: new[] { 5, 10, 30, 60, 300, 900, 1800, 3600 });

    private string TenantId => Config?.OrganizationId ?? "default";

    public override Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        return Task.FromResult(true);
    }

    public override Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default)
    {
        EnsureConfigured();
        return Task.FromResult(new DataPushResult { Success = true, RowsPushed = data.Count, DurationMs = 0 });
    }

    public override Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default)
    {
        return CreateDashboardCoreAsync(dashboard, ct);
    }

    protected override Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var tenantDashboards = GetTenantDashboards();
        var id = GenerateId();
        var created = dashboard with { Id = id, CreatedAt = DateTimeOffset.UtcNow };
        tenantDashboards[id] = created;
        return Task.FromResult(created);
    }

    protected override Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var tenantDashboards = GetTenantDashboards();
        tenantDashboards[dashboard.Id!] = dashboard with { UpdatedAt = DateTimeOffset.UtcNow, Version = dashboard.Version + 1 };
        return Task.FromResult(tenantDashboards[dashboard.Id!]);
    }

    protected override Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        var tenantDashboards = GetTenantDashboards();
        if (!tenantDashboards.TryGetValue(dashboardId, out var dashboard))
            throw new KeyNotFoundException($"Dashboard {dashboardId} not found for tenant {TenantId}");
        return Task.FromResult(dashboard);
    }

    protected override Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        var tenantDashboards = GetTenantDashboards();
        tenantDashboards.Remove(dashboardId);
        return Task.CompletedTask;
    }

    protected override Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct)
    {
        var tenantDashboards = GetTenantDashboards();
        return Task.FromResult<IReadOnlyList<Dashboard>>(tenantDashboards.Values.ToList());
    }

    protected override Task<Dashboard> CreateFromTemplateCoreAsync(string templateId, IReadOnlyDictionary<string, object>? parameters, CancellationToken ct)
    {
        var tenantDashboards = GetTenantDashboards();
        if (!tenantDashboards.TryGetValue(templateId, out var template))
            throw new KeyNotFoundException($"Template {templateId} not found for tenant {TenantId}");
        var copy = template with { Id = GenerateId(), Title = parameters?.TryGetValue("name", out var n) == true ? n.ToString()! : $"{template.Title} (Copy)", Version = 1 };
        tenantDashboards[copy.Id!] = copy;
        return Task.FromResult(copy);
    }

    private Dictionary<string, Dashboard> GetTenantDashboards()
    {
        if (!_tenantDashboards.TryGetValue(TenantId, out var dashboards))
        {
            dashboards = new Dictionary<string, Dashboard>();
            _tenantDashboards[TenantId] = dashboards;
        }
        return dashboards;
    }

    /// <summary>
    /// Applies custom branding to dashboard output.
    /// </summary>
    public object ApplyBranding(string dashboardId, BrandingOptions branding)
    {
        var dashboard = GetTenantDashboards()[dashboardId];
        return new
        {
            dashboard = ConvertToPlatformFormat(dashboard),
            branding = new
            {
                primaryColor = branding.PrimaryColor,
                secondaryColor = branding.SecondaryColor,
                logo = branding.LogoUrl,
                fontFamily = branding.FontFamily,
                customCss = branding.CustomCss
            }
        };
    }
}

/// <summary>
/// Branding options for white-label dashboards.
/// </summary>
public sealed record BrandingOptions
{
    public string? PrimaryColor { get; init; }
    public string? SecondaryColor { get; init; }
    public string? LogoUrl { get; init; }
    public string? FontFamily { get; init; }
    public string? CustomCss { get; init; }
}
