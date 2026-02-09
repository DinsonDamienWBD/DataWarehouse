using System.Net.Http.Json;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Dashboards;

namespace DataWarehouse.Plugins.UniversalDashboards.Strategies.Analytics;

/// <summary>
/// Domo analytics and business intelligence strategy.
/// </summary>
public sealed class DomoStrategy : DashboardStrategyBase
{
    public override string StrategyId => "domo";
    public override string StrategyName => "Domo";
    public override string VendorName => "Domo, Inc.";
    public override string Category => "Analytics";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true,
        SupportsTemplates: true,
        SupportsSharing: true,
        SupportsVersioning: true,
        SupportsEmbedding: true,
        SupportsAlerting: true,
        SupportedWidgetTypes: new[] { "chart", "card", "table", "gauge", "map", "pivot", "drill" },
        MaxWidgetsPerDashboard: 100,
        MaxDashboards: 500);

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, $"/v1/datasets/{targetId}/data", CreateJsonContent(data), cancellationToken);
        return new DataPushResult { Success = response.IsSuccessStatusCode, RowsPushed = data.Count };
    }

    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default)
    {
        return await CreateDashboardCoreAsync(dashboard, cancellationToken);
    }

    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureConfigured();
            var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/v1/users/me", null, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken)
    {
        var payload = new { name = dashboard.Title, description = dashboard.Description ?? "" };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/v1/pages", CreateJsonContent(payload), cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ConvertFromPlatformFormat(result);
    }

    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken)
    {
        var payload = new { name = dashboard.Title, description = dashboard.Description ?? "" };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Put, $"/v1/pages/{dashboard.Id}", CreateJsonContent(payload), cancellationToken);
        response.EnsureSuccessStatusCode();
        return dashboard;
    }

    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/v1/pages/{dashboardId}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ConvertFromPlatformFormat(result);
    }

    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Delete, $"/v1/pages/{dashboardId}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/v1/pages?limit=500", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var dashboards = new List<Dashboard>();
        if (result.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in result.EnumerateArray())
                dashboards.Add(ConvertFromPlatformFormat(item));
        }
        return dashboards;
    }
}

/// <summary>
/// ThoughtSpot search-driven analytics strategy.
/// </summary>
public sealed class ThoughtSpotStrategy : DashboardStrategyBase
{
    public override string StrategyId => "thoughtspot";
    public override string StrategyName => "ThoughtSpot";
    public override string VendorName => "ThoughtSpot, Inc.";
    public override string Category => "Analytics";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true,
        SupportsTemplates: true,
        SupportsSharing: true,
        SupportsVersioning: true,
        SupportsEmbedding: true,
        SupportsAlerting: true,
        SupportedWidgetTypes: new[] { "answer", "liveboard", "chart", "table", "filter", "headline" },
        MaxWidgetsPerDashboard: 50,
        MaxDashboards: 200);

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var payload = new { data_rows = data, target_database = "default" };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/rest/2.0/data/load", CreateJsonContent(payload), cancellationToken);
        return new DataPushResult { Success = response.IsSuccessStatusCode, RowsPushed = data.Count };
    }

    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default)
    {
        return await CreateDashboardCoreAsync(dashboard, cancellationToken);
    }

    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureConfigured();
            var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/rest/2.0/auth/session/user", null, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken)
    {
        var payload = new { name = dashboard.Title, description = dashboard.Description ?? "" };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/rest/2.0/metadata/liveboard/create", CreateJsonContent(payload), cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ConvertFromPlatformFormat(result);
    }

    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken)
    {
        var payload = new { metadata_identifier = dashboard.Id, name = dashboard.Title, description = dashboard.Description ?? "" };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/rest/2.0/metadata/liveboard/update", CreateJsonContent(payload), cancellationToken);
        response.EnsureSuccessStatusCode();
        return dashboard;
    }

    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken)
    {
        var payload = new { metadata_identifiers = new[] { dashboardId } };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/rest/2.0/metadata/search", CreateJsonContent(payload), cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (result.ValueKind == JsonValueKind.Array && result.GetArrayLength() > 0)
            return ConvertFromPlatformFormat(result[0]);
        throw new InvalidOperationException($"Dashboard {dashboardId} not found");
    }

    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken)
    {
        var payload = new { metadata_identifiers = new[] { dashboardId } };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/rest/2.0/metadata/delete", CreateJsonContent(payload), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken)
    {
        var payload = new { metadata = new[] { new { type = "LIVEBOARD" } }, record_size = 500 };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/rest/2.0/metadata/search", CreateJsonContent(payload), cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var dashboards = new List<Dashboard>();
        if (result.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in result.EnumerateArray())
                dashboards.Add(ConvertFromPlatformFormat(item));
        }
        return dashboards;
    }
}

/// <summary>
/// Mode Analytics collaborative analytics strategy.
/// </summary>
public sealed class ModeAnalyticsStrategy : DashboardStrategyBase
{
    public override string StrategyId => "mode-analytics";
    public override string StrategyName => "Mode Analytics";
    public override string VendorName => "Mode Analytics, Inc.";
    public override string Category => "Analytics";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: false,
        SupportsTemplates: true,
        SupportsSharing: true,
        SupportsVersioning: true,
        SupportsEmbedding: true,
        SupportsAlerting: false,
        SupportedWidgetTypes: new[] { "chart", "table", "pivot", "python", "r", "notebook" },
        MaxWidgetsPerDashboard: 100,
        MaxDashboards: 1000);

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var workspace = Config?.OrganizationId ?? "default";
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, $"/api/{workspace}/reports/{targetId}/data", CreateJsonContent(new { data }), cancellationToken);
        return new DataPushResult { Success = response.IsSuccessStatusCode, RowsPushed = data.Count };
    }

    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default)
    {
        return await CreateDashboardCoreAsync(dashboard, cancellationToken);
    }

    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureConfigured();
            var workspace = Config?.OrganizationId ?? "default";
            var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/api/{workspace}/account", null, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken)
    {
        var workspace = Config?.OrganizationId ?? "default";
        var payload = new { report = new { name = dashboard.Title, description = dashboard.Description ?? "" } };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, $"/api/{workspace}/reports", CreateJsonContent(payload), cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ConvertFromPlatformFormat(result);
    }

    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken)
    {
        var workspace = Config?.OrganizationId ?? "default";
        var payload = new { report = new { name = dashboard.Title, description = dashboard.Description ?? "" } };
        var response = await SendAuthenticatedRequestAsync(new HttpMethod("PATCH"), $"/api/{workspace}/reports/{dashboard.Id}", CreateJsonContent(payload), cancellationToken);
        response.EnsureSuccessStatusCode();
        return dashboard;
    }

    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken)
    {
        var workspace = Config?.OrganizationId ?? "default";
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/api/{workspace}/reports/{dashboardId}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ConvertFromPlatformFormat(result);
    }

    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken)
    {
        var workspace = Config?.OrganizationId ?? "default";
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Delete, $"/api/{workspace}/reports/{dashboardId}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken)
    {
        var workspace = Config?.OrganizationId ?? "default";
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/api/{workspace}/reports?filter=all", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var dashboards = new List<Dashboard>();
        if (result.TryGetProperty("_embedded", out var embedded) && embedded.TryGetProperty("reports", out var reports))
        {
            foreach (var item in reports.EnumerateArray())
                dashboards.Add(ConvertFromPlatformFormat(item));
        }
        return dashboards;
    }
}

/// <summary>
/// Observable computational notebook strategy for data exploration.
/// </summary>
public sealed class ObservableHqStrategy : DashboardStrategyBase
{
    public override string StrategyId => "observable-hq";
    public override string StrategyName => "Observable";
    public override string VendorName => "Observable, Inc.";
    public override string Category => "Analytics";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true,
        SupportsTemplates: true,
        SupportsSharing: true,
        SupportsVersioning: true,
        SupportsEmbedding: true,
        SupportsAlerting: false,
        SupportedWidgetTypes: new[] { "chart", "plot", "table", "input", "text", "html", "javascript" },
        MaxWidgetsPerDashboard: 200,
        MaxDashboards: 1000);

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var payload = new { name = $"data_{DateTime.UtcNow:yyyyMMddHHmmss}.json", data = SerializeToJson(data) };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, $"/api/v1/notebooks/{targetId}/files", CreateJsonContent(payload), cancellationToken);
        return new DataPushResult { Success = response.IsSuccessStatusCode, RowsPushed = data.Count };
    }

    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default)
    {
        return await CreateDashboardCoreAsync(dashboard, cancellationToken);
    }

    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureConfigured();
            var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/v1/user", null, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken)
    {
        var payload = new { title = dashboard.Title, description = dashboard.Description ?? "", visibility = "private" };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/v1/notebooks", CreateJsonContent(payload), cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ConvertFromPlatformFormat(result);
    }

    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken)
    {
        var payload = new { title = dashboard.Title, description = dashboard.Description ?? "" };
        var response = await SendAuthenticatedRequestAsync(new HttpMethod("PATCH"), $"/api/v1/notebooks/{dashboard.Id}", CreateJsonContent(payload), cancellationToken);
        response.EnsureSuccessStatusCode();
        return dashboard;
    }

    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/api/v1/notebooks/{dashboardId}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ConvertFromPlatformFormat(result);
    }

    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Delete, $"/api/v1/notebooks/{dashboardId}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/v1/notebooks?limit=100", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var dashboards = new List<Dashboard>();
        if (result.TryGetProperty("notebooks", out var notebooks))
        {
            foreach (var item in notebooks.EnumerateArray())
                dashboards.Add(ConvertFromPlatformFormat(item));
        }
        return dashboards;
    }
}

/// <summary>
/// Hex collaborative data workspace strategy.
/// </summary>
public sealed class HexStrategy : DashboardStrategyBase
{
    public override string StrategyId => "hex";
    public override string StrategyName => "Hex";
    public override string VendorName => "Hex Technologies, Inc.";
    public override string Category => "Analytics";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true,
        SupportsTemplates: true,
        SupportsSharing: true,
        SupportsVersioning: true,
        SupportsEmbedding: true,
        SupportsAlerting: true,
        SupportedWidgetTypes: new[] { "chart", "table", "pivot", "sql", "python", "input", "text" },
        MaxWidgetsPerDashboard: 150,
        MaxDashboards: 500);

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var payload = new { data, name = $"uploaded_data_{DateTime.UtcNow:yyyyMMddHHmmss}" };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, $"/api/v1/projects/{targetId}/data", CreateJsonContent(payload), cancellationToken);
        return new DataPushResult { Success = response.IsSuccessStatusCode, RowsPushed = data.Count };
    }

    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default)
    {
        return await CreateDashboardCoreAsync(dashboard, cancellationToken);
    }

    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureConfigured();
            var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/v1/user", null, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken)
    {
        var payload = new { name = dashboard.Title, description = dashboard.Description ?? "", type = "app" };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/v1/projects", CreateJsonContent(payload), cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ConvertFromPlatformFormat(result);
    }

    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken)
    {
        var payload = new { name = dashboard.Title, description = dashboard.Description ?? "" };
        var response = await SendAuthenticatedRequestAsync(new HttpMethod("PATCH"), $"/api/v1/projects/{dashboard.Id}", CreateJsonContent(payload), cancellationToken);
        response.EnsureSuccessStatusCode();
        return dashboard;
    }

    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/api/v1/projects/{dashboardId}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ConvertFromPlatformFormat(result);
    }

    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Delete, $"/api/v1/projects/{dashboardId}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/v1/projects?type=app", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var dashboards = new List<Dashboard>();
        if (result.TryGetProperty("projects", out var projects))
        {
            foreach (var item in projects.EnumerateArray())
                dashboards.Add(ConvertFromPlatformFormat(item));
        }
        return dashboards;
    }
}

/// <summary>
/// Sigma Computing cloud-native analytics strategy.
/// </summary>
public sealed class SigmaComputingStrategy : DashboardStrategyBase
{
    public override string StrategyId => "sigma-computing";
    public override string StrategyName => "Sigma Computing";
    public override string VendorName => "Sigma Computing, Inc.";
    public override string Category => "Analytics";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true,
        SupportsTemplates: true,
        SupportsSharing: true,
        SupportsVersioning: true,
        SupportsEmbedding: true,
        SupportsAlerting: true,
        SupportedWidgetTypes: new[] { "table", "chart", "pivot", "input", "kpi", "text", "image" },
        MaxWidgetsPerDashboard: 100,
        MaxDashboards: 500);

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var payload = new { data, workbookId = targetId };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/v2/data-sources/upload", CreateJsonContent(payload), cancellationToken);
        return new DataPushResult { Success = response.IsSuccessStatusCode, RowsPushed = data.Count };
    }

    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default)
    {
        return await CreateDashboardCoreAsync(dashboard, cancellationToken);
    }

    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureConfigured();
            var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/v2/members/me", null, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken)
    {
        var payload = new { name = dashboard.Title, description = dashboard.Description ?? "" };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/v2/workbooks", CreateJsonContent(payload), cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ConvertFromPlatformFormat(result);
    }

    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken)
    {
        var payload = new { name = dashboard.Title, description = dashboard.Description ?? "" };
        var response = await SendAuthenticatedRequestAsync(new HttpMethod("PATCH"), $"/v2/workbooks/{dashboard.Id}", CreateJsonContent(payload), cancellationToken);
        response.EnsureSuccessStatusCode();
        return dashboard;
    }

    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/v2/workbooks/{dashboardId}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ConvertFromPlatformFormat(result);
    }

    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Delete, $"/v2/workbooks/{dashboardId}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/v2/workbooks", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var dashboards = new List<Dashboard>();
        if (result.TryGetProperty("entries", out var entries))
        {
            foreach (var item in entries.EnumerateArray())
                dashboards.Add(ConvertFromPlatformFormat(item));
        }
        return dashboards;
    }
}

/// <summary>
/// Lightdash open source BI strategy.
/// </summary>
public sealed class LightdashStrategy : DashboardStrategyBase
{
    public override string StrategyId => "lightdash";
    public override string StrategyName => "Lightdash";
    public override string VendorName => "Lightdash Ltd.";
    public override string Category => "Analytics";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: false, SupportsTemplates: true, SupportsSharing: true, SupportsVersioning: true,
        SupportsEmbedding: true, SupportsAlerting: true,
        SupportedWidgetTypes: new[] { "chart", "table", "big-number", "pie", "bar", "line", "scatter" },
        MaxWidgetsPerDashboard: 100, MaxDashboards: 500);

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default)
    { EnsureConfigured(); var r = await SendAuthenticatedRequestAsync(HttpMethod.Post, $"/api/v1/dashboards/{targetId}/data", CreateJsonContent(new { data }), cancellationToken); return new DataPushResult { Success = r.IsSuccessStatusCode, RowsPushed = data.Count }; }

    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default) => await CreateDashboardCoreAsync(dashboard, cancellationToken);
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) { try { EnsureConfigured(); return (await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/v1/user", null, cancellationToken)).IsSuccessStatusCode; } catch { return false; } }
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken) { var r = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/v1/dashboards", CreateJsonContent(new { name = dashboard.Title }), cancellationToken); r.EnsureSuccessStatusCode(); return ConvertFromPlatformFormat(await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)); }
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken) { await SendAuthenticatedRequestAsync(new HttpMethod("PATCH"), $"/api/v1/dashboards/{dashboard.Id}", CreateJsonContent(new { name = dashboard.Title }), cancellationToken); return dashboard; }
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken) { var r = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/api/v1/dashboards/{dashboardId}", null, cancellationToken); return ConvertFromPlatformFormat(await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)); }
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken) { await SendAuthenticatedRequestAsync(HttpMethod.Delete, $"/api/v1/dashboards/{dashboardId}", null, cancellationToken); }
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken) { var r = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/v1/dashboards", null, cancellationToken); var result = await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken); var ds = new List<Dashboard>(); if (result.TryGetProperty("results", out var res)) foreach (var i in res.EnumerateArray()) ds.Add(ConvertFromPlatformFormat(i)); return ds; }
}

/// <summary>Evidence.dev markdown-based BI strategy.</summary>
public sealed class EvidenceStrategy : DashboardStrategyBase
{
    public override string StrategyId => "evidence";
    public override string StrategyName => "Evidence";
    public override string VendorName => "Evidence, Inc.";
    public override string Category => "Analytics";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: false, SupportsTemplates: true, SupportsSharing: true, SupportsVersioning: true,
        SupportsEmbedding: true, SupportsAlerting: false,
        SupportedWidgetTypes: new[] { "line-chart", "bar-chart", "scatter-plot", "table", "big-value" },
        MaxWidgetsPerDashboard: 50, MaxDashboards: 200);

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default)
    { EnsureConfigured(); var r = await SendAuthenticatedRequestAsync(HttpMethod.Post, $"/api/pages/{targetId}/data", CreateJsonContent(new { data }), cancellationToken); return new DataPushResult { Success = r.IsSuccessStatusCode, RowsPushed = data.Count }; }

    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default) => await CreateDashboardCoreAsync(dashboard, cancellationToken);
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) { try { EnsureConfigured(); return (await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/health", null, cancellationToken)).IsSuccessStatusCode; } catch { return false; } }
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken) { var r = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/pages", CreateJsonContent(new { title = dashboard.Title }), cancellationToken); r.EnsureSuccessStatusCode(); return ConvertFromPlatformFormat(await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)); }
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken) { await SendAuthenticatedRequestAsync(HttpMethod.Put, $"/api/pages/{dashboard.Id}", CreateJsonContent(new { title = dashboard.Title }), cancellationToken); return dashboard; }
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken) { var r = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/api/pages/{dashboardId}", null, cancellationToken); return ConvertFromPlatformFormat(await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)); }
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken) { await SendAuthenticatedRequestAsync(HttpMethod.Delete, $"/api/pages/{dashboardId}", null, cancellationToken); }
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken) { var r = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/pages", null, cancellationToken); var result = await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken); var ds = new List<Dashboard>(); if (result.TryGetProperty("pages", out var pg)) foreach (var i in pg.EnumerateArray()) ds.Add(ConvertFromPlatformFormat(i)); return ds; }
}

/// <summary>Cube.js semantic layer strategy.</summary>
public sealed class CubeJsStrategy : DashboardStrategyBase
{
    public override string StrategyId => "cubejs";
    public override string StrategyName => "Cube.js";
    public override string VendorName => "Cube Dev, Inc.";
    public override string Category => "Analytics";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true, SupportsTemplates: true, SupportsSharing: true, SupportsVersioning: false,
        SupportsEmbedding: true, SupportsAlerting: true,
        SupportedWidgetTypes: new[] { "chart", "table", "number", "pivot", "cohort" },
        MaxWidgetsPerDashboard: 100, MaxDashboards: 500);

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default)
    { EnsureConfigured(); var r = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/cubejs-api/v1/load", CreateJsonContent(new { query = new { measures = new[] { "count" } } }), cancellationToken); return new DataPushResult { Success = r.IsSuccessStatusCode, RowsPushed = data.Count }; }

    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default) => await CreateDashboardCoreAsync(dashboard, cancellationToken);
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) { try { EnsureConfigured(); return (await SendAuthenticatedRequestAsync(HttpMethod.Get, "/cubejs-api/v1/meta", null, cancellationToken)).IsSuccessStatusCode; } catch { return false; } }
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken) { var r = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/dashboards", CreateJsonContent(new { name = dashboard.Title }), cancellationToken); r.EnsureSuccessStatusCode(); return ConvertFromPlatformFormat(await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)); }
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken) { await SendAuthenticatedRequestAsync(HttpMethod.Put, $"/api/dashboards/{dashboard.Id}", CreateJsonContent(new { name = dashboard.Title }), cancellationToken); return dashboard; }
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken) { var r = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/api/dashboards/{dashboardId}", null, cancellationToken); return ConvertFromPlatformFormat(await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)); }
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken) { await SendAuthenticatedRequestAsync(HttpMethod.Delete, $"/api/dashboards/{dashboardId}", null, cancellationToken); }
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken) { var r = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/dashboards", null, cancellationToken); var result = await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken); var ds = new List<Dashboard>(); if (result.TryGetProperty("dashboards", out var d)) foreach (var i in d.EnumerateArray()) ds.Add(ConvertFromPlatformFormat(i)); return ds; }
}

/// <summary>Preset.io managed Superset strategy.</summary>
public sealed class PresetIoStrategy : DashboardStrategyBase
{
    public override string StrategyId => "preset-io";
    public override string StrategyName => "Preset.io";
    public override string VendorName => "Preset, Inc.";
    public override string Category => "Analytics";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true, SupportsTemplates: true, SupportsSharing: true, SupportsVersioning: true,
        SupportsEmbedding: true, SupportsAlerting: true,
        SupportedWidgetTypes: new[] { "chart", "table", "filter-box", "big-number", "pie", "map" },
        MaxWidgetsPerDashboard: 150, MaxDashboards: 1000);

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default)
    { EnsureConfigured(); var r = await SendAuthenticatedRequestAsync(HttpMethod.Post, $"/api/v1/dashboard/{targetId}/data", CreateJsonContent(new { data }), cancellationToken); return new DataPushResult { Success = r.IsSuccessStatusCode, RowsPushed = data.Count }; }

    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default) => await CreateDashboardCoreAsync(dashboard, cancellationToken);
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) { try { EnsureConfigured(); return (await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/v1/me/", null, cancellationToken)).IsSuccessStatusCode; } catch { return false; } }
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken) { var r = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/v1/dashboard/", CreateJsonContent(new { dashboard_title = dashboard.Title }), cancellationToken); r.EnsureSuccessStatusCode(); return ConvertFromPlatformFormat(await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)); }
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken) { await SendAuthenticatedRequestAsync(HttpMethod.Put, $"/api/v1/dashboard/{dashboard.Id}", CreateJsonContent(new { dashboard_title = dashboard.Title }), cancellationToken); return dashboard; }
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken) { var r = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/api/v1/dashboard/{dashboardId}", null, cancellationToken); return ConvertFromPlatformFormat(await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)); }
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken) { await SendAuthenticatedRequestAsync(HttpMethod.Delete, $"/api/v1/dashboard/{dashboardId}", null, cancellationToken); }
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken) { var r = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/v1/dashboard/", null, cancellationToken); var result = await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken); var ds = new List<Dashboard>(); if (result.TryGetProperty("result", out var d)) foreach (var i in d.EnumerateArray()) ds.Add(ConvertFromPlatformFormat(i)); return ds; }
}

/// <summary>Retool dashboard strategy.</summary>
public sealed class RetoolStrategy : DashboardStrategyBase
{
    public override string StrategyId => "retool";
    public override string StrategyName => "Retool";
    public override string VendorName => "Retool, Inc.";
    public override string Category => "Analytics";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true, SupportsTemplates: true, SupportsSharing: true, SupportsVersioning: true,
        SupportsEmbedding: true, SupportsAlerting: false,
        SupportedWidgetTypes: new[] { "table", "chart", "form", "button", "text", "container", "modal" },
        MaxWidgetsPerDashboard: 200, MaxDashboards: 500);

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default)
    { EnsureConfigured(); var r = await SendAuthenticatedRequestAsync(HttpMethod.Post, $"/api/v1/apps/{targetId}/data", CreateJsonContent(new { data }), cancellationToken); return new DataPushResult { Success = r.IsSuccessStatusCode, RowsPushed = data.Count }; }

    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default) => await CreateDashboardCoreAsync(dashboard, cancellationToken);
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) { try { EnsureConfigured(); return (await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/v1/users/me", null, cancellationToken)).IsSuccessStatusCode; } catch { return false; } }
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken) { var r = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/v1/apps", CreateJsonContent(new { name = dashboard.Title }), cancellationToken); r.EnsureSuccessStatusCode(); return ConvertFromPlatformFormat(await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)); }
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken) { await SendAuthenticatedRequestAsync(HttpMethod.Put, $"/api/v1/apps/{dashboard.Id}", CreateJsonContent(new { name = dashboard.Title }), cancellationToken); return dashboard; }
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken) { var r = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/api/v1/apps/{dashboardId}", null, cancellationToken); return ConvertFromPlatformFormat(await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)); }
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken) { await SendAuthenticatedRequestAsync(HttpMethod.Delete, $"/api/v1/apps/{dashboardId}", null, cancellationToken); }
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken) { var r = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/v1/apps", null, cancellationToken); var result = await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken); var ds = new List<Dashboard>(); if (result.TryGetProperty("apps", out var apps)) foreach (var i in apps.EnumerateArray()) ds.Add(ConvertFromPlatformFormat(i)); return ds; }
}

/// <summary>Appsmith low-code dashboard strategy.</summary>
public sealed class AppsmithStrategy : DashboardStrategyBase
{
    public override string StrategyId => "appsmith";
    public override string StrategyName => "Appsmith";
    public override string VendorName => "Appsmith, Inc.";
    public override string Category => "Analytics";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true, SupportsTemplates: true, SupportsSharing: true, SupportsVersioning: true,
        SupportsEmbedding: true, SupportsAlerting: false,
        SupportedWidgetTypes: new[] { "table", "chart", "form", "container", "button", "input", "text" },
        MaxWidgetsPerDashboard: 150, MaxDashboards: 500);

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken cancellationToken = default)
    { EnsureConfigured(); var r = await SendAuthenticatedRequestAsync(HttpMethod.Post, $"/api/v1/applications/{targetId}/data", CreateJsonContent(new { data }), cancellationToken); return new DataPushResult { Success = r.IsSuccessStatusCode, RowsPushed = data.Count }; }

    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken cancellationToken = default) => await CreateDashboardCoreAsync(dashboard, cancellationToken);
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) { try { EnsureConfigured(); return (await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/v1/users/me", null, cancellationToken)).IsSuccessStatusCode; } catch { return false; } }
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken) { var r = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/v1/applications", CreateJsonContent(new { name = dashboard.Title }), cancellationToken); r.EnsureSuccessStatusCode(); return ConvertFromPlatformFormat(await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)); }
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken) { await SendAuthenticatedRequestAsync(HttpMethod.Put, $"/api/v1/applications/{dashboard.Id}", CreateJsonContent(new { name = dashboard.Title }), cancellationToken); return dashboard; }
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken) { var r = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/api/v1/applications/{dashboardId}", null, cancellationToken); return ConvertFromPlatformFormat(await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)); }
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken) { await SendAuthenticatedRequestAsync(HttpMethod.Delete, $"/api/v1/applications/{dashboardId}", null, cancellationToken); }
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken) { var r = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/v1/applications", null, cancellationToken); var result = await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken); var ds = new List<Dashboard>(); if (result.TryGetProperty("data", out var apps)) foreach (var i in apps.EnumerateArray()) ds.Add(ConvertFromPlatformFormat(i)); return ds; }
}
