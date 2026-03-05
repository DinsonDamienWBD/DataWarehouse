using System.Net.Http.Json;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Dashboards;

namespace DataWarehouse.Plugins.UltimateInterface.Dashboards.Strategies.EnterpriseBi;

/// <summary>
/// Qlik dashboard strategy supporting Qlik Sense Enterprise and Qlik Cloud.
/// Provides app management, sheet creation, visualization support, and data connection management.
/// </summary>
public sealed class QlikStrategy : DashboardStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "qlik";

    /// <inheritdoc/>
    public override string StrategyName => "Qlik Sense";

    /// <inheritdoc/>
    public override string VendorName => "Qlik";

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
        SupportedWidgetTypes: new[] { "Chart", "Table", "Metric", "Heatmap", "Gauge", "Text", "Map", "KPI", "Filter", "Treemap", "Pivot" },
        MaxWidgetsPerDashboard: null,
        MaxDashboards: null,
        RefreshIntervals: new[] { 30, 60, 300, 600, 900, 1800, 3600, 7200 }
    );

    /// <inheritdoc/>
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        try
        {
            var response = await SendAuthenticatedRequestAsync(
                HttpMethod.Get,
                "/api/v1/users/me",
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

        // Qlik uses QVD files or direct data connections
        // For push, we use the data-files API
        var payload = new
        {
            name = $"push_data_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
            connectionId = targetId,
            data = data.Select(row => row.ToDictionary(k => k.Key, k => k.Value)).ToArray()
        };

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Post,
            "/api/v1/data-files",
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
                    ["fileId"] = result.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    ["platform"] = "Qlik"
                }
            };
        }

        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        return new DataPushResult
        {
            Success = false,
            RowsPushed = 0,
            DurationMs = duration,
            ErrorMessage = $"Qlik API error: {response.StatusCode} - {error}"
        };
    }

    /// <inheritdoc/>
    public override async Task<Dashboard> ProvisionDashboardAsync(
        Dashboard dashboard,
        DashboardProvisionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        // Create Qlik app
        var appPayload = new
        {
            attributes = new
            {
                name = dashboard.Title,
                description = dashboard.Description ?? "",
                spaceId = options?.TargetFolder ?? Config!.ProjectId
            }
        };

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Post,
            "/api/v1/apps",
            CreateJsonContent(appPayload),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var appId = result.GetProperty("attributes").GetProperty("id").GetString()!;

        // Create sheets for widgets
        foreach (var widget in dashboard.Widgets)
        {
            await CreateSheetObjectAsync(appId, widget, cancellationToken);
        }

        return dashboard with
        {
            Id = appId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <inheritdoc/>
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken)
    {
        var payload = new
        {
            attributes = new
            {
                name = dashboard.Title,
                description = dashboard.Description ?? "",
                spaceId = Config!.ProjectId
            }
        };

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Post,
            "/api/v1/apps",
            CreateJsonContent(payload),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        return dashboard with
        {
            Id = result.GetProperty("attributes").GetProperty("id").GetString(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <inheritdoc/>
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken)
    {
        var payload = new
        {
            attributes = new
            {
                name = dashboard.Title,
                description = dashboard.Description ?? ""
            }
        };

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Put,
            $"/api/v1/apps/{dashboard.Id}",
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
            $"/api/v1/apps/{dashboardId}",
            cancellationToken: cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var attrs = result.GetProperty("attributes");

        // Get sheets (objects in Qlik)
        var objectsResponse = await SendAuthenticatedRequestAsync(
            HttpMethod.Get,
            $"/api/v1/apps/{dashboardId}/objects",
            cancellationToken: cancellationToken);

        var widgets = new List<DashboardWidget>();
        if (objectsResponse.IsSuccessStatusCode)
        {
            var objectsResult = await objectsResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (objectsResult.TryGetProperty("data", out var objects))
            {
                var index = 0;
                foreach (var obj in objects.EnumerateArray())
                {
                    var objAttrs = obj.GetProperty("attributes");
                    var objId = objAttrs.GetProperty("id").GetString() ?? GenerateId();
                    var objName = objAttrs.TryGetProperty("name", out var name) ? name.GetString() ?? "Object" : "Object";
                    var objType = objAttrs.TryGetProperty("qType", out var qType) ? qType.GetString() ?? "sheet" : "sheet";

                    var widgetType = objType.ToLowerInvariant() switch
                    {
                        "sheet" => WidgetType.Chart,
                        "barchart" => WidgetType.Chart,
                        "piechart" => WidgetType.Chart,
                        "linechart" => WidgetType.Chart,
                        "table" => WidgetType.Table,
                        "kpi" => WidgetType.Metric,
                        "gauge" => WidgetType.Gauge,
                        "text-image" => WidgetType.Text,
                        _ => WidgetType.Chart
                    };

                    widgets.Add(new DashboardWidget(
                        objId,
                        objName,
                        widgetType,
                        new WidgetPosition(0, index * 3, 12, 3),
                        DataSourceConfiguration.Metrics("*"),
                        new Dictionary<string, object> { ["qType"] = objType }
                    ));
                    index++;
                }
            }
        }

        return new Dashboard(
            Id: dashboardId,
            Title: attrs.TryGetProperty("name", out var titleProp) ? titleProp.GetString() ?? "Untitled" : "Untitled",
            Description: attrs.TryGetProperty("description", out var descProp) ? descProp.GetString() : null,
            Layout: DashboardLayout.Grid(),
            Widgets: widgets,
            TimeRange: TimeRange.Last24Hours(),
            RefreshInterval: null,
            Tags: null,
            Owner: attrs.TryGetProperty("ownerId", out var owner) ? owner.GetString() : null,
            CreatedAt: attrs.TryGetProperty("createdDate", out var created)
                ? DateTimeOffset.Parse(created.GetString() ?? DateTimeOffset.UtcNow.ToString("O"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)
                : null,
            UpdatedAt: attrs.TryGetProperty("modifiedDate", out var modified)
                ? DateTimeOffset.Parse(modified.GetString() ?? DateTimeOffset.UtcNow.ToString("O"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)
                : null,
            Version: 1
        );
    }

    /// <inheritdoc/>
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Delete,
            $"/api/v1/apps/{dashboardId}",
            cancellationToken: cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken)
    {
        var endpoint = "/api/v1/apps";
        var queryParams = new List<string>();

        if (!string.IsNullOrEmpty(filter?.SearchQuery))
        {
            queryParams.Add($"name={Uri.EscapeDataString(filter.SearchQuery)}");
        }

        if (!string.IsNullOrEmpty(Config?.ProjectId))
        {
            queryParams.Add($"spaceId={Config.ProjectId}");
        }

        if (queryParams.Count > 0)
        {
            endpoint += "?" + string.Join("&", queryParams);
        }

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Get,
            endpoint,
            cancellationToken: cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var dashboards = new List<Dashboard>();

        if (result.TryGetProperty("data", out var data))
        {
            foreach (var app in data.EnumerateArray())
            {
                var attrs = app.GetProperty("attributes");
                var id = attrs.GetProperty("id").GetString() ?? GenerateId();
                var title = attrs.TryGetProperty("name", out var name) ? name.GetString() ?? "Untitled" : "Untitled";

                DateTimeOffset? createdAt = null;
                if (attrs.TryGetProperty("createdDate", out var created))
                {
                    createdAt = DateTimeOffset.Parse(created.GetString() ?? DateTimeOffset.UtcNow.ToString("O"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
                }

                // Apply date filters
                if (filter?.CreatedAfter != null && createdAt < filter.CreatedAfter)
                    continue;
                if (filter?.CreatedBefore != null && createdAt > filter.CreatedBefore)
                    continue;

                dashboards.Add(new Dashboard(
                    Id: id,
                    Title: title,
                    Description: attrs.TryGetProperty("description", out var desc) ? desc.GetString() : null,
                    Layout: DashboardLayout.Grid(),
                    Widgets: Array.Empty<DashboardWidget>(),
                    TimeRange: TimeRange.Last24Hours(),
                    RefreshInterval: null,
                    Tags: null,
                    Owner: attrs.TryGetProperty("ownerId", out var owner) ? owner.GetString() : null,
                    CreatedAt: createdAt,
                    UpdatedAt: attrs.TryGetProperty("modifiedDate", out var modified)
                        ? DateTimeOffset.Parse(modified.GetString() ?? DateTimeOffset.UtcNow.ToString("O"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)
                        : null,
                    Version: 1
                ));
            }
        }

        return dashboards;
    }

    /// <inheritdoc/>
    protected override async Task<Dashboard> CreateFromTemplateCoreAsync(
        string templateId,
        IReadOnlyDictionary<string, object>? parameters,
        CancellationToken cancellationToken)
    {
        // Copy app from template
        var payload = new
        {
            attributes = new
            {
                name = parameters?.TryGetValue("name", out var nameVal) == true ? nameVal.ToString() : "New App from Template",
                spaceId = Config!.ProjectId
            }
        };

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Post,
            $"/api/v1/apps/{templateId}/copy",
            CreateJsonContent(payload),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var attrs = result.GetProperty("attributes");

        return new Dashboard(
            Id: attrs.GetProperty("id").GetString(),
            Title: attrs.TryGetProperty("name", out var name) ? name.GetString() ?? "Untitled" : "Untitled",
            Description: attrs.TryGetProperty("description", out var desc) ? desc.GetString() : null,
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

    private async Task CreateSheetObjectAsync(string appId, DashboardWidget widget, CancellationToken cancellationToken)
    {
        var qType = widget.Type switch
        {
            WidgetType.Chart => "barchart",
            WidgetType.Table => "table",
            WidgetType.Metric => "kpi",
            WidgetType.Gauge => "gauge",
            WidgetType.Text => "text-image",
            WidgetType.Heatmap => "heatmap",
            _ => "sheet"
        };

        var payload = new
        {
            attributes = new
            {
                name = widget.Title,
                qType = qType,
                qInfo = new { qType = qType }
            }
        };

        await SendAuthenticatedRequestAsync(
            HttpMethod.Post,
            $"/api/v1/apps/{appId}/objects",
            CreateJsonContent(payload),
            cancellationToken);
    }
}
