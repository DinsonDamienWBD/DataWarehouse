using System.Net.Http.Json;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Dashboards;

namespace DataWarehouse.Plugins.UltimateInterface.Dashboards.Strategies.CloudNative;

/// <summary>
/// AWS QuickSight dashboard strategy.
/// </summary>
public sealed class AwsQuickSightStrategy : DashboardStrategyBase
{
    public override string StrategyId => "aws-quicksight";
    public override string StrategyName => "AWS QuickSight";
    public override string VendorName => "Amazon Web Services";
    public override string Category => "Cloud Native";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true, SupportsTemplates: true, SupportsSharing: true,
        SupportsVersioning: true, SupportsEmbedding: true, SupportsAlerting: true,
        SupportedWidgetTypes: new[] { "Chart", "Table", "Metric", "Heatmap", "Gauge", "Text", "Map", "Pivot", "Insight", "KPI" },
        RefreshIntervals: new[] { 60, 300, 900, 1800, 3600, 7200, 14400, 86400 });

    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        try
        {
            var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/accounts/{Config!.OrganizationId}/dashboards", cancellationToken: ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default)
    {
        EnsureConfigured();
        var startTime = DateTimeOffset.UtcNow;
        var payload = new { DataSetId = targetId, RowData = data.Select(r => r.ToDictionary(k => k.Key, k => k.Value)).ToArray() };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, $"/accounts/{Config!.OrganizationId}/data-sets/{targetId}/ingestions", CreateJsonContent(payload), ct);
        var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
        return new DataPushResult { Success = response.IsSuccessStatusCode, RowsPushed = response.IsSuccessStatusCode ? data.Count : 0, DurationMs = duration };
    }

    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default)
    {
        return await CreateDashboardCoreAsync(dashboard, ct);
    }

    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var dashboardId = GenerateId();
        var payload = new { AwsAccountId = Config!.OrganizationId, DashboardId = dashboardId, Name = dashboard.Title, Permissions = new[] { new { Principal = "*", Actions = new[] { "quicksight:DescribeDashboard", "quicksight:QueryDashboard" } } } };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, $"/accounts/{Config.OrganizationId}/dashboards/{dashboardId}", CreateJsonContent(payload), ct);
        response.EnsureSuccessStatusCode();
        return dashboard with { Id = dashboardId, CreatedAt = DateTimeOffset.UtcNow };
    }

    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var payload = new { Name = dashboard.Title };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Put, $"/accounts/{Config!.OrganizationId}/dashboards/{dashboard.Id}", CreateJsonContent(payload), ct);
        response.EnsureSuccessStatusCode();
        return dashboard with { UpdatedAt = DateTimeOffset.UtcNow, Version = dashboard.Version + 1 };
    }

    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/accounts/{Config!.OrganizationId}/dashboards/{dashboardId}", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var db = result.GetProperty("Dashboard");
        return new Dashboard(dashboardId, db.TryGetProperty("Name", out var n) ? n.GetString() ?? "Dashboard" : "Dashboard",
            null, DashboardLayout.Grid(), Array.Empty<DashboardWidget>(), TimeRange.Last24Hours());
    }

    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Delete, $"/accounts/{Config!.OrganizationId}/dashboards/{dashboardId}", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/accounts/{Config!.OrganizationId}/dashboards", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var dashboards = new List<Dashboard>();
        if (result.TryGetProperty("DashboardSummaryList", out var list))
            foreach (var d in list.EnumerateArray())
                dashboards.Add(new Dashboard(d.GetProperty("DashboardId").GetString()!, d.TryGetProperty("Name", out var n) ? n.GetString() ?? "Dashboard" : "Dashboard",
                    null, DashboardLayout.Grid(), Array.Empty<DashboardWidget>(), TimeRange.Last24Hours()));
        return dashboards;
    }

    protected override async Task<Dashboard> CreateFromTemplateCoreAsync(string templateId, IReadOnlyDictionary<string, object>? parameters, CancellationToken ct)
    {
        var dashboardId = GenerateId();
        var payload = new { AwsAccountId = Config!.OrganizationId, DashboardId = dashboardId, Name = parameters?.TryGetValue("name", out var n) == true ? n.ToString() : "From Template", SourceEntity = new { SourceTemplate = new { Arn = templateId } } };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, $"/accounts/{Config.OrganizationId}/dashboards/{dashboardId}", CreateJsonContent(payload), ct);
        response.EnsureSuccessStatusCode();
        return new Dashboard(dashboardId, parameters?.TryGetValue("name", out var name) == true ? name.ToString()! : "From Template", null, DashboardLayout.Grid(), Array.Empty<DashboardWidget>(), TimeRange.Last24Hours(), CreatedAt: DateTimeOffset.UtcNow);
    }
}

/// <summary>
/// Google Looker Studio (formerly Data Studio) dashboard strategy.
/// </summary>
public sealed class GoogleDataStudioStrategy : DashboardStrategyBase
{
    public override string StrategyId => "google-datastudio";
    public override string StrategyName => "Google Looker Studio";
    public override string VendorName => "Google";
    public override string Category => "Cloud Native";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true, SupportsTemplates: true, SupportsSharing: true,
        SupportsVersioning: false, SupportsEmbedding: true, SupportsAlerting: false,
        SupportedWidgetTypes: new[] { "Chart", "Table", "Metric", "Heatmap", "Gauge", "Text", "Map", "Scorecard", "Bullet", "Treemap" },
        RefreshIntervals: new[] { 60, 300, 900, 1800, 3600, 14400, 86400 });

    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        try
        {
            var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/v1/reports", cancellationToken: ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default)
    {
        EnsureConfigured();
        var startTime = DateTimeOffset.UtcNow;
        // Looker Studio uses BigQuery or other connectors, simulate data source update
        var payload = new { dataSourceId = targetId, rows = data.Select(r => r.ToDictionary(k => k.Key, k => k.Value)).ToArray() };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, $"/v1/datasources/{targetId}/data", CreateJsonContent(payload), ct);
        var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
        return new DataPushResult { Success = response.IsSuccessStatusCode, RowsPushed = response.IsSuccessStatusCode ? data.Count : 0, DurationMs = duration };
    }

    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default)
    {
        return await CreateDashboardCoreAsync(dashboard, ct);
    }

    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var payload = new { name = dashboard.Title };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/v1/reports", CreateJsonContent(payload), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return dashboard with { Id = result.GetProperty("reportId").GetString(), CreatedAt = DateTimeOffset.UtcNow };
    }

    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var payload = new { name = dashboard.Title };
        var response = await SendAuthenticatedRequestAsync(new HttpMethod("PATCH"), $"/v1/reports/{dashboard.Id}", CreateJsonContent(payload), ct);
        response.EnsureSuccessStatusCode();
        return dashboard with { UpdatedAt = DateTimeOffset.UtcNow };
    }

    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/v1/reports/{dashboardId}", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return new Dashboard(dashboardId, result.TryGetProperty("name", out var n) ? n.GetString() ?? "Report" : "Report",
            null, DashboardLayout.Grid(), Array.Empty<DashboardWidget>(), TimeRange.Last24Hours());
    }

    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Delete, $"/v1/reports/{dashboardId}", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/v1/reports", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var dashboards = new List<Dashboard>();
        if (result.TryGetProperty("reports", out var reports))
            foreach (var r in reports.EnumerateArray())
                dashboards.Add(new Dashboard(r.GetProperty("reportId").GetString()!, r.TryGetProperty("name", out var n) ? n.GetString() ?? "Report" : "Report",
                    null, DashboardLayout.Grid(), Array.Empty<DashboardWidget>(), TimeRange.Last24Hours()));
        return dashboards;
    }

    protected override async Task<Dashboard> CreateFromTemplateCoreAsync(string templateId, IReadOnlyDictionary<string, object>? parameters, CancellationToken ct)
    {
        var payload = new { templateReportId = templateId, name = parameters?.TryGetValue("name", out var n) == true ? n.ToString() : "Copy" };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/v1/reports:copy", CreateJsonContent(payload), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return new Dashboard(result.GetProperty("reportId").GetString()!, parameters?.TryGetValue("name", out var name) == true ? name.ToString()! : "Copy", null, DashboardLayout.Grid(), Array.Empty<DashboardWidget>(), TimeRange.Last24Hours(), CreatedAt: DateTimeOffset.UtcNow);
    }
}

/// <summary>
/// Azure Power BI Embedded dashboard strategy.
/// </summary>
public sealed class AzurePowerBiEmbeddedStrategy : DashboardStrategyBase
{
    public override string StrategyId => "azure-powerbi-embedded";
    public override string StrategyName => "Azure Power BI Embedded";
    public override string VendorName => "Microsoft";
    public override string Category => "Cloud Native";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true, SupportsTemplates: true, SupportsSharing: true,
        SupportsVersioning: false, SupportsEmbedding: true, SupportsAlerting: true,
        SupportedWidgetTypes: new[] { "Chart", "Table", "Metric", "Heatmap", "Gauge", "Text", "Map", "Matrix", "Slicer", "Card" },
        RefreshIntervals: new[] { 60, 300, 900, 1800, 3600, 7200, 28800, 86400 });

    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        try
        {
            var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/v1.0/myorg/groups", cancellationToken: ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default)
    {
        EnsureConfigured();
        var startTime = DateTimeOffset.UtcNow;
        var payload = new { rows = data.Select(r => r.ToDictionary(k => k.Key, k => k.Value)).ToArray() };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, $"/v1.0/myorg/groups/{Config!.OrganizationId}/datasets/{targetId}/tables/Data/rows", CreateJsonContent(payload), ct);
        var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
        return new DataPushResult { Success = response.IsSuccessStatusCode, RowsPushed = response.IsSuccessStatusCode ? data.Count : 0, DurationMs = duration };
    }

    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default)
    {
        return await CreateDashboardCoreAsync(dashboard, ct);
    }

    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var payload = new { name = dashboard.Title };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, $"/v1.0/myorg/groups/{Config!.OrganizationId}/dashboards", CreateJsonContent(payload), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return dashboard with { Id = result.GetProperty("id").GetString(), CreatedAt = DateTimeOffset.UtcNow };
    }

    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        // Power BI Embedded doesn't support direct dashboard name updates via API
        // Return as-is with updated timestamp
        return dashboard with { UpdatedAt = DateTimeOffset.UtcNow };
    }

    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/v1.0/myorg/groups/{Config!.OrganizationId}/dashboards/{dashboardId}", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return new Dashboard(dashboardId, result.TryGetProperty("displayName", out var n) ? n.GetString() ?? "Dashboard" : "Dashboard",
            null, DashboardLayout.Grid(), Array.Empty<DashboardWidget>(), TimeRange.Last24Hours());
    }

    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Delete, $"/v1.0/myorg/groups/{Config!.OrganizationId}/dashboards/{dashboardId}", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/v1.0/myorg/groups/{Config!.OrganizationId}/dashboards", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var dashboards = new List<Dashboard>();
        if (result.TryGetProperty("value", out var value))
            foreach (var d in value.EnumerateArray())
                dashboards.Add(new Dashboard(d.GetProperty("id").GetString()!, d.TryGetProperty("displayName", out var n) ? n.GetString() ?? "Dashboard" : "Dashboard",
                    null, DashboardLayout.Grid(), Array.Empty<DashboardWidget>(), TimeRange.Last24Hours()));
        return dashboards;
    }

    /// <summary>
    /// Gets an embed token for a dashboard.
    /// </summary>
    public async Task<string> GetEmbedTokenAsync(string dashboardId, CancellationToken ct = default)
    {
        EnsureConfigured();
        var payload = new { accessLevel = "View", datasetId = (string?)null };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, $"/v1.0/myorg/groups/{Config!.OrganizationId}/dashboards/{dashboardId}/GenerateToken", CreateJsonContent(payload), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return result.GetProperty("token").GetString()!;
    }
}
