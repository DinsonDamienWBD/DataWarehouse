using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Dashboards;

namespace DataWarehouse.Plugins.UltimateInterface.Dashboards.Strategies.EnterpriseBi;

/// <summary>
/// SAP Analytics Cloud dashboard strategy.
/// </summary>
public sealed class SapAnalyticsStrategy : DashboardStrategyBase
{
    public override string StrategyId => "sap-analytics";
    public override string StrategyName => "SAP Analytics Cloud";
    public override string VendorName => "SAP";
    public override string Category => "Enterprise BI";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true, SupportsTemplates: true, SupportsSharing: true,
        SupportsVersioning: true, SupportsEmbedding: true, SupportsAlerting: true,
        SupportedWidgetTypes: new[] { "Chart", "Table", "Metric", "Heatmap", "Gauge", "Text", "Map", "Planning" },
        RefreshIntervals: new[] { 60, 300, 900, 3600 });

    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        try
        {
            var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/v1/stories", cancellationToken: ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default)
    {
        EnsureConfigured();
        var startTime = DateTimeOffset.UtcNow;
        var payload = new { modelId = targetId, data = data.Select(r => r.ToDictionary(k => k.Key, k => k.Value)).ToArray() };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/v1/data/import", CreateJsonContent(payload), ct);
        var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
        if (response.IsSuccessStatusCode)
            return new DataPushResult { Success = true, RowsPushed = data.Count, DurationMs = duration };
        return new DataPushResult { Success = false, RowsPushed = 0, DurationMs = duration, ErrorMessage = await response.Content.ReadAsStringAsync(ct) };
    }

    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default)
    {
        return await CreateDashboardCoreAsync(dashboard, ct);
    }

    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var payload = new { name = dashboard.Title, description = dashboard.Description ?? "", type = "Story" };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/v1/stories", CreateJsonContent(payload), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return dashboard with { Id = result.GetProperty("id").GetString(), CreatedAt = DateTimeOffset.UtcNow };
    }

    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var payload = new { name = dashboard.Title, description = dashboard.Description ?? "" };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Put, $"/api/v1/stories/{dashboard.Id}", CreateJsonContent(payload), ct);
        response.EnsureSuccessStatusCode();
        return dashboard with { UpdatedAt = DateTimeOffset.UtcNow, Version = dashboard.Version + 1 };
    }

    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/api/v1/stories/{dashboardId}", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return new Dashboard(dashboardId, result.TryGetProperty("name", out var n) ? n.GetString() ?? "Story" : "Story",
            result.TryGetProperty("description", out var d) ? d.GetString() : null, DashboardLayout.Grid(),
            Array.Empty<DashboardWidget>(), TimeRange.Last24Hours());
    }

    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Delete, $"/api/v1/stories/{dashboardId}", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/v1/stories", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var dashboards = new List<Dashboard>();
        if (result.TryGetProperty("stories", out var stories))
            foreach (var s in stories.EnumerateArray())
                dashboards.Add(new Dashboard(s.GetProperty("id").GetString()!, s.TryGetProperty("name", out var n) ? n.GetString() ?? "Story" : "Story",
                    null, DashboardLayout.Grid(), Array.Empty<DashboardWidget>(), TimeRange.Last24Hours()));
        return dashboards;
    }
}

/// <summary>
/// IBM Cognos Analytics dashboard strategy.
/// </summary>
public sealed class IbmCognosStrategy : DashboardStrategyBase
{
    public override string StrategyId => "ibm-cognos";
    public override string StrategyName => "IBM Cognos Analytics";
    public override string VendorName => "IBM";
    public override string Category => "Enterprise BI";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true, SupportsTemplates: true, SupportsSharing: true,
        SupportsVersioning: true, SupportsEmbedding: true, SupportsAlerting: true,
        SupportedWidgetTypes: new[] { "Chart", "Table", "Metric", "Heatmap", "Gauge", "Text", "Map", "Report" },
        RefreshIntervals: new[] { 60, 300, 900, 1800, 3600, 7200 });

    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        try
        {
            var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/v1/session", cancellationToken: ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default)
    {
        EnsureConfigured();
        var startTime = DateTimeOffset.UtcNow;
        var payload = new { dataSourceId = targetId, records = data.Select(r => r.ToDictionary(k => k.Key, k => k.Value)).ToArray() };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/v1/datasources/upload", CreateJsonContent(payload), ct);
        var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
        return new DataPushResult { Success = response.IsSuccessStatusCode, RowsPushed = response.IsSuccessStatusCode ? data.Count : 0, DurationMs = duration };
    }

    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default)
    {
        return await CreateDashboardCoreAsync(dashboard, ct);
    }

    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var payload = new { type = "dashboard", defaultName = dashboard.Title, description = dashboard.Description ?? "" };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/v1/content", CreateJsonContent(payload), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return dashboard with { Id = result.GetProperty("id").GetString(), CreatedAt = DateTimeOffset.UtcNow };
    }

    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var payload = new { defaultName = dashboard.Title, description = dashboard.Description ?? "" };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Put, $"/api/v1/content/{dashboard.Id}", CreateJsonContent(payload), ct);
        response.EnsureSuccessStatusCode();
        return dashboard with { UpdatedAt = DateTimeOffset.UtcNow };
    }

    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/api/v1/content/{dashboardId}", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return new Dashboard(dashboardId, result.TryGetProperty("defaultName", out var n) ? n.GetString() ?? "Dashboard" : "Dashboard",
            result.TryGetProperty("description", out var d) ? d.GetString() : null, DashboardLayout.Grid(),
            Array.Empty<DashboardWidget>(), TimeRange.Last24Hours());
    }

    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Delete, $"/api/v1/content/{dashboardId}", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/v1/content?type=dashboard", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var dashboards = new List<Dashboard>();
        if (result.TryGetProperty("content", out var content))
            foreach (var c in content.EnumerateArray())
                dashboards.Add(new Dashboard(c.GetProperty("id").GetString()!, c.TryGetProperty("defaultName", out var n) ? n.GetString() ?? "Dashboard" : "Dashboard",
                    null, DashboardLayout.Grid(), Array.Empty<DashboardWidget>(), TimeRange.Last24Hours()));
        return dashboards;
    }
}

/// <summary>
/// MicroStrategy dashboard strategy.
/// </summary>
public sealed class MicroStrategyStrategy : DashboardStrategyBase
{
    public override string StrategyId => "microstrategy";
    public override string StrategyName => "MicroStrategy";
    public override string VendorName => "MicroStrategy";
    public override string Category => "Enterprise BI";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true, SupportsTemplates: true, SupportsSharing: true,
        SupportsVersioning: true, SupportsEmbedding: true, SupportsAlerting: true,
        SupportedWidgetTypes: new[] { "Chart", "Table", "Metric", "Heatmap", "Gauge", "Text", "Map", "HyperIntelligence" },
        RefreshIntervals: new[] { 60, 300, 900, 1800, 3600, 7200, 14400, 86400 });

    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        try
        {
            var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/auth/delegate", cancellationToken: ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default)
    {
        EnsureConfigured();
        var startTime = DateTimeOffset.UtcNow;
        var payload = new { cubeId = targetId, data = data.Select(r => r.ToDictionary(k => k.Key, k => k.Value)).ToArray() };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/cubes/push", CreateJsonContent(payload), ct);
        var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
        return new DataPushResult { Success = response.IsSuccessStatusCode, RowsPushed = response.IsSuccessStatusCode ? data.Count : 0, DurationMs = duration };
    }

    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default)
    {
        return await CreateDashboardCoreAsync(dashboard, ct);
    }

    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var payload = new { name = dashboard.Title, description = dashboard.Description ?? "", type = 55 }; // 55 = Dashboard
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/documents", CreateJsonContent(payload), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return dashboard with { Id = result.GetProperty("id").GetString(), CreatedAt = DateTimeOffset.UtcNow };
    }

    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var payload = new { name = dashboard.Title, description = dashboard.Description ?? "" };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Put, $"/api/documents/{dashboard.Id}", CreateJsonContent(payload), ct);
        response.EnsureSuccessStatusCode();
        return dashboard with { UpdatedAt = DateTimeOffset.UtcNow };
    }

    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/api/documents/{dashboardId}", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return new Dashboard(dashboardId, result.TryGetProperty("name", out var n) ? n.GetString() ?? "Dashboard" : "Dashboard",
            result.TryGetProperty("description", out var d) ? d.GetString() : null, DashboardLayout.Grid(),
            Array.Empty<DashboardWidget>(), TimeRange.Last24Hours());
    }

    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Delete, $"/api/documents/{dashboardId}", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/searches/results?type=55&limit=100", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var dashboards = new List<Dashboard>();
        if (result.TryGetProperty("result", out var res))
            foreach (var d in res.EnumerateArray())
                dashboards.Add(new Dashboard(d.GetProperty("id").GetString()!, d.TryGetProperty("name", out var n) ? n.GetString() ?? "Dashboard" : "Dashboard",
                    null, DashboardLayout.Grid(), Array.Empty<DashboardWidget>(), TimeRange.Last24Hours()));
        return dashboards;
    }
}

/// <summary>
/// Sisense dashboard strategy.
/// </summary>
public sealed class SisenseStrategy : DashboardStrategyBase
{
    public override string StrategyId => "sisense";
    public override string StrategyName => "Sisense";
    public override string VendorName => "Sisense";
    public override string Category => "Enterprise BI";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true, SupportsTemplates: true, SupportsSharing: true,
        SupportsVersioning: false, SupportsEmbedding: true, SupportsAlerting: true,
        SupportedWidgetTypes: new[] { "Chart", "Table", "Metric", "Heatmap", "Gauge", "Text", "Map", "BloX", "Indicator" },
        RefreshIntervals: new[] { 30, 60, 300, 600, 900, 1800, 3600 });

    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        try
        {
            var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/v1/users/loggedin", cancellationToken: ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default)
    {
        EnsureConfigured();
        var startTime = DateTimeOffset.UtcNow;
        var payload = new { elasticubeId = targetId, data = data.Select(r => r.ToDictionary(k => k.Key, k => k.Value)).ToArray() };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/v1/elasticubes/live/push", CreateJsonContent(payload), ct);
        var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
        return new DataPushResult { Success = response.IsSuccessStatusCode, RowsPushed = response.IsSuccessStatusCode ? data.Count : 0, DurationMs = duration };
    }

    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default)
    {
        return await CreateDashboardCoreAsync(dashboard, ct);
    }

    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var payload = new { title = dashboard.Title, desc = dashboard.Description ?? "" };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/v1/dashboards", CreateJsonContent(payload), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return dashboard with { Id = result.GetProperty("oid").GetString(), CreatedAt = DateTimeOffset.UtcNow };
    }

    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var payload = new { title = dashboard.Title, desc = dashboard.Description ?? "" };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Put, $"/api/v1/dashboards/{dashboard.Id}", CreateJsonContent(payload), ct);
        response.EnsureSuccessStatusCode();
        return dashboard with { UpdatedAt = DateTimeOffset.UtcNow };
    }

    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/api/v1/dashboards/{dashboardId}", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return new Dashboard(dashboardId, result.TryGetProperty("title", out var t) ? t.GetString() ?? "Dashboard" : "Dashboard",
            result.TryGetProperty("desc", out var d) ? d.GetString() : null, DashboardLayout.Grid(),
            Array.Empty<DashboardWidget>(), TimeRange.Last24Hours());
    }

    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Delete, $"/api/v1/dashboards/{dashboardId}", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/v1/dashboards", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var dashboards = new List<Dashboard>();
        foreach (var d in result.EnumerateArray())
            dashboards.Add(new Dashboard(d.GetProperty("oid").GetString()!, d.TryGetProperty("title", out var t) ? t.GetString() ?? "Dashboard" : "Dashboard",
                d.TryGetProperty("desc", out var desc) ? desc.GetString() : null, DashboardLayout.Grid(),
                Array.Empty<DashboardWidget>(), TimeRange.Last24Hours()));
        return dashboards;
    }
}
