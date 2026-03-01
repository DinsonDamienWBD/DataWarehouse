using System.Net.Http.Json;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Dashboards;

namespace DataWarehouse.Plugins.UltimateInterface.Dashboards.Strategies.OpenSource;

/// <summary>
/// Redash dashboard strategy.
/// </summary>
public sealed class RedashStrategy : DashboardStrategyBase
{
    public override string StrategyId => "redash";
    public override string StrategyName => "Redash";
    public override string VendorName => "Redash";
    public override string Category => "Open Source";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: false, SupportsTemplates: false, SupportsSharing: true,
        SupportsVersioning: false, SupportsEmbedding: true, SupportsAlerting: true,
        SupportedWidgetTypes: new[] { "Chart", "Table", "Metric", "Heatmap", "Text", "Map", "Counter" },
        RefreshIntervals: new[] { 60, 300, 600, 1800, 3600, 43200, 86400 });

    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        try
        {
            var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/session", cancellationToken: ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default)
    {
        EnsureConfigured();
        var startTime = DateTimeOffset.UtcNow;
        // Redash uses queries to fetch data, not push
        // P2-3297: Quote identifier to prevent SQL injection via caller-supplied targetId.
        var safeTable = "\"" + targetId.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        var query = $"INSERT INTO {safeTable} VALUES " + string.Join(", ", data.Select(r =>
            "(" + string.Join(", ", r.Values.Select(v => v is string s ? $"'{s.Replace("'", "''", StringComparison.Ordinal)}'" : v?.ToString() ?? "NULL")) + ")"));
        var payload = new { query = query, data_source_id = int.TryParse(Config!.ProjectId, out var id) ? id : 1 };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/queries", CreateJsonContent(payload), ct);
        var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
        return new DataPushResult { Success = response.IsSuccessStatusCode, RowsPushed = response.IsSuccessStatusCode ? data.Count : 0, DurationMs = duration };
    }

    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default)
    {
        return await CreateDashboardCoreAsync(dashboard, ct);
    }

    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var payload = new { name = dashboard.Title, slug = dashboard.Title.ToLowerInvariant().Replace(" ", "-") };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/dashboards", CreateJsonContent(payload), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return dashboard with { Id = result.GetProperty("id").GetInt32().ToString(), CreatedAt = DateTimeOffset.UtcNow };
    }

    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var payload = new { name = dashboard.Title };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, $"/api/dashboards/{dashboard.Id}", CreateJsonContent(payload), ct);
        response.EnsureSuccessStatusCode();
        return dashboard with { UpdatedAt = DateTimeOffset.UtcNow };
    }

    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/api/dashboards/{dashboardId}", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return new Dashboard(dashboardId, result.TryGetProperty("name", out var n) ? n.GetString() ?? "Dashboard" : "Dashboard",
            null, DashboardLayout.Grid(), Array.Empty<DashboardWidget>(), TimeRange.Last24Hours());
    }

    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Delete, $"/api/dashboards/{dashboardId}", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/dashboards", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var dashboards = new List<Dashboard>();
        if (result.TryGetProperty("results", out var results))
            foreach (var d in results.EnumerateArray())
                dashboards.Add(new Dashboard(d.GetProperty("id").GetInt32().ToString(), d.TryGetProperty("name", out var n) ? n.GetString() ?? "Dashboard" : "Dashboard",
                    null, DashboardLayout.Grid(), Array.Empty<DashboardWidget>(), TimeRange.Last24Hours()));
        return dashboards;
    }
}

/// <summary>
/// Apache Superset dashboard strategy.
/// </summary>
public sealed class ApacheSupersetStrategy : DashboardStrategyBase
{
    private string? _accessToken;
    // P2-3300: serialize token fetch to prevent concurrent auth races on _accessToken.
    private readonly System.Threading.SemaphoreSlim _authLock = new(1, 1);

    public override string StrategyId => "apache-superset";
    public override string StrategyName => "Apache Superset";
    public override string VendorName => "Apache Software Foundation";
    public override string Category => "Open Source";

    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true, SupportsTemplates: true, SupportsSharing: true,
        SupportsVersioning: false, SupportsEmbedding: true, SupportsAlerting: true,
        SupportedWidgetTypes: new[] { "Chart", "Table", "Metric", "Heatmap", "Gauge", "Text", "Map", "Big Number", "Pivot", "Filter" },
        RefreshIntervals: new[] { 30, 60, 300, 600, 900, 1800, 3600 });

    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        try
        {
            await EnsureAuthenticatedAsync(ct);
            var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/v1/me/", cancellationToken: ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default)
    {
        EnsureConfigured();
        await EnsureAuthenticatedAsync(ct);
        var startTime = DateTimeOffset.UtcNow;
        // P2-3298: Quote identifier to prevent SQL injection via caller-supplied targetId.
        var safeTable = "\"" + targetId.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        var sql = $"INSERT INTO {safeTable} VALUES " + string.Join(", ", data.Select(r =>
            "(" + string.Join(", ", r.Values.Select(v => v is string s ? $"'{s.Replace("'", "''", StringComparison.Ordinal)}'" : v?.ToString() ?? "NULL")) + ")"));
        var payload = new { database_id = int.TryParse(Config!.ProjectId, out var id) ? id : 1, sql = sql };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/v1/sqllab/execute/", CreateJsonContent(payload), ct);
        var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
        return new DataPushResult { Success = response.IsSuccessStatusCode, RowsPushed = response.IsSuccessStatusCode ? data.Count : 0, DurationMs = duration };
    }

    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);
        return await CreateDashboardCoreAsync(dashboard, ct);
    }

    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        await EnsureAuthenticatedAsync(ct);
        var payload = new { dashboard_title = dashboard.Title, slug = dashboard.Title.ToLowerInvariant().Replace(" ", "-") };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/v1/dashboard/", CreateJsonContent(payload), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return dashboard with { Id = result.GetProperty("id").GetInt32().ToString(), CreatedAt = DateTimeOffset.UtcNow };
    }

    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        await EnsureAuthenticatedAsync(ct);
        var payload = new { dashboard_title = dashboard.Title };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Put, $"/api/v1/dashboard/{dashboard.Id}", CreateJsonContent(payload), ct);
        response.EnsureSuccessStatusCode();
        return dashboard with { UpdatedAt = DateTimeOffset.UtcNow };
    }

    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        await EnsureAuthenticatedAsync(ct);
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/api/v1/dashboard/{dashboardId}", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var res = result.GetProperty("result");
        return new Dashboard(dashboardId, res.TryGetProperty("dashboard_title", out var t) ? t.GetString() ?? "Dashboard" : "Dashboard",
            null, DashboardLayout.Grid(), Array.Empty<DashboardWidget>(), TimeRange.Last24Hours());
    }

    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        await EnsureAuthenticatedAsync(ct);
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Delete, $"/api/v1/dashboard/{dashboardId}", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct)
    {
        await EnsureAuthenticatedAsync(ct);
        var endpoint = "/api/v1/dashboard/";
        if (!string.IsNullOrEmpty(filter?.SearchQuery))
            endpoint += $"?q=(filters:!((col:dashboard_title,opr:ct,value:'{Uri.EscapeDataString(filter.SearchQuery)}')))";
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, endpoint, cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var dashboards = new List<Dashboard>();
        if (result.TryGetProperty("result", out var res))
            foreach (var d in res.EnumerateArray())
                dashboards.Add(new Dashboard(d.GetProperty("id").GetInt32().ToString(), d.TryGetProperty("dashboard_title", out var t) ? t.GetString() ?? "Dashboard" : "Dashboard",
                    null, DashboardLayout.Grid(), Array.Empty<DashboardWidget>(), TimeRange.Last24Hours()));
        return dashboards;
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken ct)
    {
        // Fast-path: already authenticated.
        if (!string.IsNullOrEmpty(_accessToken)) return;

        // P2-3300: serialize login to prevent concurrent callers from racing on _accessToken.
        await _authLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(_accessToken)) return; // re-check inside lock
            if (Config?.AuthType != AuthenticationType.Basic || string.IsNullOrEmpty(Config.Username)) return;

            var client = GetHttpClient();
            var loginPayload = new { username = Config.Username, password = Config.Password, provider = "db" };
            using var response = await client.PostAsync("/api/v1/security/login", CreateJsonContent(loginPayload), ct);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            _accessToken = result.GetProperty("access_token").GetString();
        }
        finally
        {
            _authLock.Release();
        }
    }

    protected override Task ApplyAuthenticationAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_accessToken))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Grafana Dashboards strategy.
/// </summary>
public sealed class GrafanaDashboardsStrategy : DashboardStrategyBase
{
    public override string StrategyId => "grafana";
    public override string StrategyName => "Grafana";
    public override string VendorName => "Grafana Labs";
    public override string Category => "Open Source";

    public override DashboardCapabilities Capabilities { get; } = DashboardCapabilities.Full(
        "Chart", "Table", "Metric", "Heatmap", "Gauge", "Text", "Log", "Trace", "Alert", "State Timeline", "Stat", "Bar Gauge");

    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        try
        {
            var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/health", cancellationToken: ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default)
    {
        EnsureConfigured();
        var startTime = DateTimeOffset.UtcNow;
        // Grafana typically uses external data sources, but we can push to a local data source
        var payload = new { datasourceUid = targetId, data = data.ToArray() };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/ds/query", CreateJsonContent(payload), ct);
        var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
        return new DataPushResult { Success = response.IsSuccessStatusCode, RowsPushed = response.IsSuccessStatusCode ? data.Count : 0, DurationMs = duration };
    }

    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default)
    {
        return await CreateDashboardCoreAsync(dashboard, ct);
    }

    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var grafanaDashboard = ConvertToGrafanaFormat(dashboard);
        var payload = new { dashboard = grafanaDashboard, folderUid = Config!.ProjectId, overwrite = false };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/dashboards/db", CreateJsonContent(payload), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return dashboard with { Id = result.GetProperty("uid").GetString(), CreatedAt = DateTimeOffset.UtcNow };
    }

    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var grafanaDashboard = ConvertToGrafanaFormat(dashboard);
        var payload = new { dashboard = grafanaDashboard, folderUid = Config!.ProjectId, overwrite = true };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/dashboards/db", CreateJsonContent(payload), ct);
        response.EnsureSuccessStatusCode();
        return dashboard with { UpdatedAt = DateTimeOffset.UtcNow, Version = dashboard.Version + 1 };
    }

    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/api/dashboards/uid/{dashboardId}", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var db = result.GetProperty("dashboard");
        var widgets = new List<DashboardWidget>();
        if (db.TryGetProperty("panels", out var panels))
            foreach (var p in panels.EnumerateArray())
            {
                var pType = p.TryGetProperty("type", out var t) ? t.GetString() ?? "graph" : "graph";
                widgets.Add(new DashboardWidget(
                    p.TryGetProperty("id", out var id) ? id.GetInt32().ToString() : GenerateId(),
                    p.TryGetProperty("title", out var title) ? title.GetString() ?? "Panel" : "Panel",
                    pType switch { "table" => WidgetType.Table, "stat" => WidgetType.Metric, "gauge" => WidgetType.Gauge, "text" => WidgetType.Text, _ => WidgetType.Chart },
                    new WidgetPosition(
                        p.TryGetProperty("gridPos", out var gp) && gp.TryGetProperty("x", out var x) ? x.GetInt32() : 0,
                        gp.ValueKind != JsonValueKind.Undefined && gp.TryGetProperty("y", out var y) ? y.GetInt32() : 0,
                        gp.ValueKind != JsonValueKind.Undefined && gp.TryGetProperty("w", out var w) ? w.GetInt32() : 6,
                        gp.ValueKind != JsonValueKind.Undefined && gp.TryGetProperty("h", out var h) ? h.GetInt32() : 3),
                    DataSourceConfiguration.Metrics("*"), null));
            }
        return new Dashboard(dashboardId, db.TryGetProperty("title", out var n) ? n.GetString() ?? "Dashboard" : "Dashboard",
            db.TryGetProperty("description", out var d) ? d.GetString() : null, DashboardLayout.Grid(), widgets, TimeRange.Last24Hours());
    }

    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Delete, $"/api/dashboards/uid/{dashboardId}", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct)
    {
        var endpoint = "/api/search?type=dash-db";
        if (!string.IsNullOrEmpty(filter?.SearchQuery))
            endpoint += $"&query={Uri.EscapeDataString(filter.SearchQuery)}";
        if (filter?.Tags != null && filter.Tags.Count > 0)
            endpoint += "&tag=" + string.Join("&tag=", filter.Tags.Select(Uri.EscapeDataString));
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, endpoint, cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var dashboards = new List<Dashboard>();
        foreach (var d in result.EnumerateArray())
            dashboards.Add(new Dashboard(d.GetProperty("uid").GetString()!, d.TryGetProperty("title", out var t) ? t.GetString() ?? "Dashboard" : "Dashboard",
                null, DashboardLayout.Grid(), Array.Empty<DashboardWidget>(), TimeRange.Last24Hours(),
                Tags: d.TryGetProperty("tags", out var tags) ? tags.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : null));
        return dashboards;
    }

    private object ConvertToGrafanaFormat(Dashboard dashboard)
    {
        return new
        {
            uid = dashboard.Id,
            title = dashboard.Title,
            description = dashboard.Description ?? "",
            tags = dashboard.Tags ?? Array.Empty<string>(),
            refresh = dashboard.RefreshInterval != null ? $"{dashboard.RefreshInterval}s" : "",
            panels = dashboard.Widgets.Select((w, i) => new
            {
                id = i + 1,
                title = w.Title,
                type = w.Type switch { WidgetType.Table => "table", WidgetType.Metric => "stat", WidgetType.Gauge => "gauge", WidgetType.Text => "text", _ => "graph" },
                gridPos = new { x = w.Position.X, y = w.Position.Y, w = w.Position.Width, h = w.Position.Height }
            }).ToArray()
        };
    }
}

/// <summary>
/// Kibana Dashboards strategy.
/// </summary>
public sealed class KibanaDashboardsStrategy : DashboardStrategyBase
{
    public override string StrategyId => "kibana";
    public override string StrategyName => "Kibana";
    public override string VendorName => "Elastic";
    public override string Category => "Open Source";

    public override DashboardCapabilities Capabilities { get; } = DashboardCapabilities.Full(
        "Chart", "Table", "Metric", "Heatmap", "Gauge", "Text", "Log", "Map", "TSVB", "Lens", "Vega");

    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        try
        {
            var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/api/status", cancellationToken: ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public override async Task<DataPushResult> PushDataAsync(string targetId, IReadOnlyList<IReadOnlyDictionary<string, object>> data, CancellationToken ct = default)
    {
        EnsureConfigured();
        var startTime = DateTimeOffset.UtcNow;
        // Kibana pushes to Elasticsearch
        var bulkPayload = string.Join("\n", data.SelectMany(r => new[] {
            SerializeToJson(new { index = new { _index = targetId } }),
            SerializeToJson(r.ToDictionary(k => k.Key, k => k.Value))
        })) + "\n";
        var content = new StringContent(bulkPayload, System.Text.Encoding.UTF8, "application/x-ndjson");
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/_bulk", content, ct);
        var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
        return new DataPushResult { Success = response.IsSuccessStatusCode, RowsPushed = response.IsSuccessStatusCode ? data.Count : 0, DurationMs = duration };
    }

    public override async Task<Dashboard> ProvisionDashboardAsync(Dashboard dashboard, DashboardProvisionOptions? options = null, CancellationToken ct = default)
    {
        return await CreateDashboardCoreAsync(dashboard, ct);
    }

    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var kibanaDashboard = new
        {
            attributes = new { title = dashboard.Title, description = dashboard.Description ?? "", panelsJSON = "[]", optionsJSON = "{}", timeRestore = false }
        };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/api/saved_objects/dashboard", CreateJsonContent(kibanaDashboard), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return dashboard with { Id = result.GetProperty("id").GetString(), CreatedAt = DateTimeOffset.UtcNow };
    }

    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken ct)
    {
        var kibanaDashboard = new { attributes = new { title = dashboard.Title, description = dashboard.Description ?? "" } };
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Put, $"/api/saved_objects/dashboard/{dashboard.Id}", CreateJsonContent(kibanaDashboard), ct);
        response.EnsureSuccessStatusCode();
        return dashboard with { UpdatedAt = DateTimeOffset.UtcNow };
    }

    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, $"/api/saved_objects/dashboard/{dashboardId}", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var attrs = result.GetProperty("attributes");
        return new Dashboard(dashboardId, attrs.TryGetProperty("title", out var t) ? t.GetString() ?? "Dashboard" : "Dashboard",
            attrs.TryGetProperty("description", out var d) ? d.GetString() : null, DashboardLayout.Grid(),
            Array.Empty<DashboardWidget>(), TimeRange.Last24Hours());
    }

    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken ct)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Delete, $"/api/saved_objects/dashboard/{dashboardId}", cancellationToken: ct);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken ct)
    {
        var endpoint = "/api/saved_objects/_find?type=dashboard&per_page=100";
        if (!string.IsNullOrEmpty(filter?.SearchQuery))
            endpoint += $"&search={Uri.EscapeDataString(filter.SearchQuery)}&search_fields=title";
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, endpoint, cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var dashboards = new List<Dashboard>();
        if (result.TryGetProperty("saved_objects", out var objects))
            foreach (var d in objects.EnumerateArray())
            {
                var attrs = d.GetProperty("attributes");
                dashboards.Add(new Dashboard(d.GetProperty("id").GetString()!, attrs.TryGetProperty("title", out var t) ? t.GetString() ?? "Dashboard" : "Dashboard",
                    attrs.TryGetProperty("description", out var desc) ? desc.GetString() : null, DashboardLayout.Grid(),
                    Array.Empty<DashboardWidget>(), TimeRange.Last24Hours()));
            }
        return dashboards;
    }
}
