using System.Net.Http.Json;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Dashboards;

namespace DataWarehouse.Plugins.UniversalDashboards.Strategies.EnterpriseBi;

/// <summary>
/// Looker dashboard strategy providing integration with Google Looker (formerly Looker).
/// Supports LookML modeling, dashboard creation, tile management, and embedded analytics.
/// </summary>
public sealed class LookerStrategy : DashboardStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "looker";

    /// <inheritdoc/>
    public override string StrategyName => "Looker";

    /// <inheritdoc/>
    public override string VendorName => "Google";

    /// <inheritdoc/>
    public override string Category => "Enterprise BI";

    /// <inheritdoc/>
    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true,
        SupportsTemplates: true,
        SupportsSharing: true,
        SupportsVersioning: true,
        SupportsEmbedding: true,
        SupportsAlerting: true,
        SupportedWidgetTypes: new[] { "Chart", "Table", "Metric", "Heatmap", "Gauge", "Text", "Map", "Look", "Filter" },
        MaxWidgetsPerDashboard: 100,
        MaxDashboards: null,
        RefreshIntervals: new[] { 60, 300, 600, 900, 1800, 3600, 7200, 14400, 28800, 86400 }
    );

    /// <inheritdoc/>
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        try
        {
            var response = await SendAuthenticatedRequestAsync(
                HttpMethod.Get,
                "/api/4.0/user",
                cancellationToken: cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public override async Task<DataPushResult> PushDataAsync(
        string targetId,
        IReadOnlyList<IReadOnlyDictionary<string, object>> data,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var startTime = DateTimeOffset.UtcNow;

        // Looker uses SQL Runner for data operations
        // For push, we create a derived table or use the SQL Runner API
        var payload = new
        {
            model = Config!.ProjectId ?? "default",
            sql = GenerateInsertSql(targetId, data)
        };

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Post,
            "/api/4.0/sql_queries",
            CreateJsonContent(payload),
            cancellationToken);

        var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;

        if (response.IsSuccessStatusCode)
        {
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return new DataPushResult
            {
                Success = true,
                RowsPushed = data.Count,
                DurationMs = duration,
                Metadata = new Dictionary<string, object>
                {
                    ["queryId"] = result.TryGetProperty("slug", out var slug) ? slug.GetString() ?? "" : "",
                    ["platform"] = "Looker"
                }
            };
        }

        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        return new DataPushResult
        {
            Success = false,
            RowsPushed = 0,
            DurationMs = duration,
            ErrorMessage = $"Looker API error: {response.StatusCode} - {error}"
        };
    }

    /// <inheritdoc/>
    public override async Task<Dashboard> ProvisionDashboardAsync(
        Dashboard dashboard,
        DashboardProvisionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        // Create the dashboard
        var dashboardPayload = new
        {
            title = dashboard.Title,
            description = dashboard.Description ?? "",
            folder_id = options?.TargetFolder ?? Config!.ProjectId,
            refresh_interval = dashboard.RefreshInterval?.ToString() ?? "1 hour"
        };

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Post,
            "/api/4.0/dashboards",
            CreateJsonContent(dashboardPayload),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var dashboardId = result.GetProperty("id").GetString()!;

        // Add dashboard elements (tiles)
        foreach (var widget in dashboard.Widgets)
        {
            await CreateDashboardElementAsync(dashboardId, widget, cancellationToken);
        }

        return dashboard with
        {
            Id = dashboardId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <inheritdoc/>
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken)
    {
        var payload = new
        {
            title = dashboard.Title,
            description = dashboard.Description ?? "",
            folder_id = Config!.ProjectId,
            refresh_interval = dashboard.RefreshInterval?.ToString() ?? "1 hour"
        };

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Post,
            "/api/4.0/dashboards",
            CreateJsonContent(payload),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        return dashboard with
        {
            Id = result.GetProperty("id").GetString(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <inheritdoc/>
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken)
    {
        var payload = new
        {
            title = dashboard.Title,
            description = dashboard.Description ?? "",
            refresh_interval = dashboard.RefreshInterval?.ToString() ?? "1 hour"
        };

        var response = await SendAuthenticatedRequestAsync(
            new HttpMethod("PATCH"),
            $"/api/4.0/dashboards/{dashboard.Id}",
            CreateJsonContent(payload),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        return dashboard with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Version = dashboard.Version + 1
        };
    }

    /// <inheritdoc/>
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Get,
            $"/api/4.0/dashboards/{dashboardId}",
            cancellationToken: cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        var widgets = new List<DashboardWidget>();
        if (result.TryGetProperty("dashboard_elements", out var elements))
        {
            foreach (var element in elements.EnumerateArray())
            {
                var elementId = element.GetProperty("id").GetString() ?? GenerateId();
                var elementTitle = element.TryGetProperty("title", out var title) ? title.GetString() ?? "Element" : "Element";
                var elementType = element.TryGetProperty("type", out var type) ? type.GetString() ?? "vis" : "vis";

                var widgetType = elementType switch
                {
                    "vis" => WidgetType.Chart,
                    "look" => WidgetType.Chart,
                    "text" => WidgetType.Text,
                    "filter" => WidgetType.Custom,
                    _ => WidgetType.Chart
                };

                var row = element.TryGetProperty("row", out var r) ? r.GetInt32() : 0;
                var col = element.TryGetProperty("col", out var c) ? c.GetInt32() : 0;
                var width = element.TryGetProperty("width", out var w) ? w.GetInt32() : 6;
                var height = element.TryGetProperty("height", out var h) ? h.GetInt32() : 3;

                widgets.Add(new DashboardWidget(
                    elementId,
                    elementTitle,
                    widgetType,
                    new WidgetPosition(col, row, width, height),
                    DataSourceConfiguration.Metrics(element.TryGetProperty("query_id", out var qid) ? qid.GetString() ?? "*" : "*"),
                    null
                ));
            }
        }

        return new Dashboard(
            Id: dashboardId,
            Title: result.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "Untitled" : "Untitled",
            Description: result.TryGetProperty("description", out var descProp) ? descProp.GetString() : null,
            Layout: DashboardLayout.Grid(),
            Widgets: widgets,
            TimeRange: TimeRange.Last24Hours(),
            RefreshInterval: result.TryGetProperty("refresh_interval", out var refresh) && refresh.ValueKind != JsonValueKind.Null
                ? ParseRefreshInterval(refresh.GetString())
                : null,
            Tags: null,
            Owner: result.TryGetProperty("user_id", out var userId) ? userId.GetString() : null,
            CreatedAt: result.TryGetProperty("created_at", out var created)
                ? DateTimeOffset.Parse(created.GetString() ?? DateTimeOffset.UtcNow.ToString("O"))
                : null,
            UpdatedAt: result.TryGetProperty("updated_at", out var updated)
                ? DateTimeOffset.Parse(updated.GetString() ?? DateTimeOffset.UtcNow.ToString("O"))
                : null,
            Version: 1
        );
    }

    /// <inheritdoc/>
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Delete,
            $"/api/4.0/dashboards/{dashboardId}",
            cancellationToken: cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken)
    {
        var endpoint = "/api/4.0/dashboards";
        var queryParams = new List<string> { "fields=id,title,description,created_at,updated_at,user_id,folder_id" };

        if (!string.IsNullOrEmpty(filter?.SearchQuery))
        {
            queryParams.Add($"title={Uri.EscapeDataString(filter.SearchQuery)}");
        }

        if (!string.IsNullOrEmpty(Config?.ProjectId))
        {
            queryParams.Add($"folder_id={Config.ProjectId}");
        }

        endpoint += "?" + string.Join("&", queryParams);

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Get,
            endpoint,
            cancellationToken: cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var dashboards = new List<Dashboard>();

        foreach (var db in result.EnumerateArray())
        {
            var id = db.GetProperty("id").GetString() ?? GenerateId();
            var title = db.TryGetProperty("title", out var name) ? name.GetString() ?? "Untitled" : "Untitled";

            DateTimeOffset? createdAt = null;
            if (db.TryGetProperty("created_at", out var created) && created.ValueKind != JsonValueKind.Null)
            {
                createdAt = DateTimeOffset.Parse(created.GetString() ?? DateTimeOffset.UtcNow.ToString("O"));
            }

            // Apply filters
            if (filter?.CreatedAfter != null && createdAt < filter.CreatedAfter)
                continue;
            if (filter?.CreatedBefore != null && createdAt > filter.CreatedBefore)
                continue;

            dashboards.Add(new Dashboard(
                Id: id,
                Title: title,
                Description: db.TryGetProperty("description", out var desc) ? desc.GetString() : null,
                Layout: DashboardLayout.Grid(),
                Widgets: Array.Empty<DashboardWidget>(),
                TimeRange: TimeRange.Last24Hours(),
                RefreshInterval: null,
                Tags: null,
                Owner: db.TryGetProperty("user_id", out var userId) ? userId.GetString() : null,
                CreatedAt: createdAt,
                UpdatedAt: db.TryGetProperty("updated_at", out var updated) && updated.ValueKind != JsonValueKind.Null
                    ? DateTimeOffset.Parse(updated.GetString() ?? DateTimeOffset.UtcNow.ToString("O"))
                    : null,
                Version: 1
            ));
        }

        return dashboards;
    }

    /// <inheritdoc/>
    protected override async Task<Dashboard> CreateFromTemplateCoreAsync(
        string templateId,
        IReadOnlyDictionary<string, object>? parameters,
        CancellationToken cancellationToken)
    {
        // Copy dashboard
        var payload = new
        {
            dashboard_id = templateId,
            folder_id = Config!.ProjectId,
            name = parameters?.TryGetValue("name", out var nameVal) == true ? nameVal.ToString() : "Dashboard Copy"
        };

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Post,
            "/api/4.0/dashboards",
            CreateJsonContent(payload),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        return new Dashboard(
            Id: result.GetProperty("id").GetString(),
            Title: result.TryGetProperty("title", out var name) ? name.GetString() ?? "Untitled" : "Untitled",
            Description: result.TryGetProperty("description", out var desc) ? desc.GetString() : null,
            Layout: DashboardLayout.Grid(),
            Widgets: Array.Empty<DashboardWidget>(),
            TimeRange: TimeRange.Last24Hours(),
            RefreshInterval: null,
            Tags: null,
            Owner: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            Version: 1
        );
    }

    private async Task CreateDashboardElementAsync(string dashboardId, DashboardWidget widget, CancellationToken cancellationToken)
    {
        var elementType = widget.Type switch
        {
            WidgetType.Text => "text",
            WidgetType.Custom => "filter",
            _ => "vis"
        };

        var payload = new
        {
            dashboard_id = dashboardId,
            title = widget.Title,
            type = elementType,
            row = widget.Position.Y,
            col = widget.Position.X,
            width = widget.Position.Width,
            height = widget.Position.Height
        };

        await SendAuthenticatedRequestAsync(
            HttpMethod.Post,
            "/api/4.0/dashboard_elements",
            CreateJsonContent(payload),
            cancellationToken);
    }

    private static string GenerateInsertSql(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object>> data)
    {
        if (data.Count == 0) return "";

        var columns = data[0].Keys.ToList();
        var columnList = string.Join(", ", columns.Select(c => $"\"{c}\""));

        var values = data.Select(row =>
            "(" + string.Join(", ", columns.Select(c =>
                row.TryGetValue(c, out var v) ? FormatSqlValue(v) : "NULL")) + ")"
        );

        return $"INSERT INTO {tableName} ({columnList}) VALUES {string.Join(", ", values)}";
    }

    private static string FormatSqlValue(object? value)
    {
        return value switch
        {
            null => "NULL",
            string s => $"'{s.Replace("'", "''")}'",
            bool b => b ? "TRUE" : "FALSE",
            DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
            DateTimeOffset dto => $"'{dto:yyyy-MM-dd HH:mm:ss}'",
            _ => value.ToString() ?? "NULL"
        };
    }

    private static int? ParseRefreshInterval(string? interval)
    {
        if (string.IsNullOrEmpty(interval)) return null;

        var lower = interval.ToLowerInvariant();
        if (lower.Contains("minute"))
        {
            var num = int.TryParse(new string(lower.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 1;
            return num * 60;
        }
        if (lower.Contains("hour"))
        {
            var num = int.TryParse(new string(lower.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 1;
            return num * 3600;
        }
        if (lower.Contains("day"))
        {
            var num = int.TryParse(new string(lower.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 1;
            return num * 86400;
        }

        return int.TryParse(interval, out var seconds) ? seconds : null;
    }
}
