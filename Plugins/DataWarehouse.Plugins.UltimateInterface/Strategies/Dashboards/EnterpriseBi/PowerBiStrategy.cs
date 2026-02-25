using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Dashboards;

namespace DataWarehouse.Plugins.UltimateInterface.Dashboards.Strategies.EnterpriseBi;

/// <summary>
/// Microsoft Power BI dashboard strategy providing integration with Power BI Service.
/// Supports dataset creation, report publishing, tile management, and embedded analytics.
/// </summary>
public sealed class PowerBiStrategy : DashboardStrategyBase
{
    private const string PowerBiApiBase = "https://api.powerbi.com/v1.0/myorg";

    /// <inheritdoc/>
    public override string StrategyId => "powerbi";

    /// <inheritdoc/>
    public override string StrategyName => "Power BI";

    /// <inheritdoc/>
    public override string VendorName => "Microsoft";

    /// <inheritdoc/>
    public override string Category => "Enterprise BI";

    /// <inheritdoc/>
    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: true,
        SupportsTemplates: true,
        SupportsSharing: true,
        SupportsVersioning: false,
        SupportsEmbedding: true,
        SupportsAlerting: true,
        SupportedWidgetTypes: new[] { "Chart", "Table", "Metric", "Heatmap", "Gauge", "Text", "Map", "Matrix", "Slicer", "Card" },
        MaxWidgetsPerDashboard: 60,
        MaxDashboards: null,
        RefreshIntervals: new[] { 60, 300, 900, 1800, 3600, 7200, 28800, 86400 }
    );

    /// <inheritdoc/>
    protected override int RateLimitPerSecond => 200;

    /// <inheritdoc/>
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        try
        {
            var response = await SendAuthenticatedRequestAsync(
                HttpMethod.Get,
                GetApiEndpoint("groups"),
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

        // Power BI push datasets require specific format
        var payload = new
        {
            rows = data.Select(row => row.ToDictionary(k => k.Key, k => k.Value)).ToArray()
        };

        var groupId = Config!.OrganizationId;
        var endpoint = string.IsNullOrEmpty(groupId)
            ? $"datasets/{targetId}/tables/Data/rows"
            : $"groups/{groupId}/datasets/{targetId}/tables/Data/rows";

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Post,
            GetApiEndpoint(endpoint),
            CreateJsonContent(payload),
            cancellationToken);

        var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;

        if (response.IsSuccessStatusCode)
        {
            return new DataPushResult
            {
                Success = true,
                RowsPushed = data.Count,
                DurationMs = duration,
                Metadata = new Dictionary<string, object>
                {
                    ["datasetId"] = targetId,
                    ["platform"] = "Power BI"
                }
            };
        }

        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        return new DataPushResult
        {
            Success = false,
            RowsPushed = 0,
            DurationMs = duration,
            ErrorMessage = $"Power BI API error: {response.StatusCode} - {error}"
        };
    }

    /// <inheritdoc/>
    public override async Task<Dashboard> ProvisionDashboardAsync(
        Dashboard dashboard,
        DashboardProvisionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var groupId = Config!.OrganizationId;

        // First, create the dashboard
        var dashboardPayload = new
        {
            name = dashboard.Title
        };

        var endpoint = string.IsNullOrEmpty(groupId)
            ? "dashboards"
            : $"groups/{groupId}/dashboards";

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Post,
            GetApiEndpoint(endpoint),
            CreateJsonContent(dashboardPayload),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var dashboardId = result.GetProperty("id").GetString()!;

        // Add tiles for each widget if reports exist
        foreach (var widget in dashboard.Widgets)
        {
            await AddTileAsync(dashboardId, widget, groupId, cancellationToken);
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
        var groupId = Config!.OrganizationId;

        var payload = new
        {
            name = dashboard.Title
        };

        var endpoint = string.IsNullOrEmpty(groupId)
            ? "dashboards"
            : $"groups/{groupId}/dashboards";

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Post,
            GetApiEndpoint(endpoint),
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
        // Power BI doesn't support direct dashboard updates via API
        // We need to delete and recreate tiles
        var existing = await GetDashboardCoreAsync(dashboard.Id!, cancellationToken);
        var groupId = Config!.OrganizationId;

        // Delete existing tiles
        foreach (var widget in existing.Widgets)
        {
            var endpoint = string.IsNullOrEmpty(groupId)
                ? $"dashboards/{dashboard.Id}/tiles/{widget.Id}"
                : $"groups/{groupId}/dashboards/{dashboard.Id}/tiles/{widget.Id}";

            await SendAuthenticatedRequestAsync(
                HttpMethod.Delete,
                GetApiEndpoint(endpoint),
                cancellationToken: cancellationToken);
        }

        // Add new tiles
        foreach (var widget in dashboard.Widgets)
        {
            await AddTileAsync(dashboard.Id!, widget, groupId, cancellationToken);
        }

        return dashboard with
        {
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <inheritdoc/>
    protected override async Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken)
    {
        var groupId = Config!.OrganizationId;

        var endpoint = string.IsNullOrEmpty(groupId)
            ? $"dashboards/{dashboardId}"
            : $"groups/{groupId}/dashboards/{dashboardId}";

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Get,
            GetApiEndpoint(endpoint),
            cancellationToken: cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        // Get tiles
        var tilesEndpoint = string.IsNullOrEmpty(groupId)
            ? $"dashboards/{dashboardId}/tiles"
            : $"groups/{groupId}/dashboards/{dashboardId}/tiles";

        var tilesResponse = await SendAuthenticatedRequestAsync(
            HttpMethod.Get,
            GetApiEndpoint(tilesEndpoint),
            cancellationToken: cancellationToken);

        var widgets = new List<DashboardWidget>();
        if (tilesResponse.IsSuccessStatusCode)
        {
            var tilesResult = await tilesResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (tilesResult.TryGetProperty("value", out var tiles))
            {
                foreach (var tile in tiles.EnumerateArray())
                {
                    var tileId = tile.GetProperty("id").GetString() ?? GenerateId();
                    var tileTitle = tile.TryGetProperty("title", out var title) ? title.GetString() ?? "Tile" : "Tile";

                    widgets.Add(new DashboardWidget(
                        tileId,
                        tileTitle,
                        WidgetType.Chart,
                        new WidgetPosition(
                            tile.TryGetProperty("colSpan", out var colSpan) ? colSpan.GetInt32() : 0,
                            tile.TryGetProperty("rowSpan", out var rowSpan) ? rowSpan.GetInt32() : 0,
                            6, 3
                        ),
                        DataSourceConfiguration.Metrics("*"),
                        new Dictionary<string, object>
                        {
                            ["embedUrl"] = tile.TryGetProperty("embedUrl", out var embedUrl) ? embedUrl.GetString() ?? "" : ""
                        }
                    ));
                }
            }
        }

        return new Dashboard(
            Id: dashboardId,
            Title: result.TryGetProperty("displayName", out var name) ? name.GetString() ?? "Untitled" : "Untitled",
            Description: null,
            Layout: DashboardLayout.Grid(),
            Widgets: widgets,
            TimeRange: TimeRange.Last24Hours(),
            RefreshInterval: null,
            Tags: null,
            Owner: null,
            CreatedAt: null,
            UpdatedAt: null,
            Version: 1
        );
    }

    /// <inheritdoc/>
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken)
    {
        var groupId = Config!.OrganizationId;

        var endpoint = string.IsNullOrEmpty(groupId)
            ? $"dashboards/{dashboardId}"
            : $"groups/{groupId}/dashboards/{dashboardId}";

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Delete,
            GetApiEndpoint(endpoint),
            cancellationToken: cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken)
    {
        var groupId = Config!.OrganizationId;

        var endpoint = string.IsNullOrEmpty(groupId)
            ? "dashboards"
            : $"groups/{groupId}/dashboards";

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Get,
            GetApiEndpoint(endpoint),
            cancellationToken: cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var dashboards = new List<Dashboard>();

        if (result.TryGetProperty("value", out var value))
        {
            foreach (var db in value.EnumerateArray())
            {
                var id = db.GetProperty("id").GetString() ?? GenerateId();
                var title = db.TryGetProperty("displayName", out var name) ? name.GetString() ?? "Untitled" : "Untitled";

                // Apply search filter
                if (!string.IsNullOrEmpty(filter?.SearchQuery) &&
                    !title.Contains(filter.SearchQuery, StringComparison.OrdinalIgnoreCase))
                    continue;

                dashboards.Add(new Dashboard(
                    Id: id,
                    Title: title,
                    Description: null,
                    Layout: DashboardLayout.Grid(),
                    Widgets: Array.Empty<DashboardWidget>(),
                    TimeRange: TimeRange.Last24Hours(),
                    RefreshInterval: null,
                    Tags: null,
                    Owner: null,
                    CreatedAt: null,
                    UpdatedAt: null,
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
        var groupId = Config!.OrganizationId;

        // Clone from template report
        var clonePayload = new
        {
            name = parameters?.TryGetValue("name", out var nameVal) == true ? nameVal.ToString() : $"Dashboard from template",
            targetWorkspaceId = groupId
        };

        var endpoint = string.IsNullOrEmpty(groupId)
            ? $"reports/{templateId}/Clone"
            : $"groups/{groupId}/reports/{templateId}/Clone";

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Post,
            GetApiEndpoint(endpoint),
            CreateJsonContent(clonePayload),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        return new Dashboard(
            Id: result.GetProperty("id").GetString(),
            Title: result.TryGetProperty("name", out var name) ? name.GetString() ?? "Untitled" : "Untitled",
            Description: null,
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

    private async Task AddTileAsync(string dashboardId, DashboardWidget widget, string? groupId, CancellationToken cancellationToken)
    {
        var tilePayload = new
        {
            title = widget.Title,
            rowSpan = widget.Position.Height,
            colSpan = widget.Position.Width
        };

        var endpoint = string.IsNullOrEmpty(groupId)
            ? $"dashboards/{dashboardId}/tiles"
            : $"groups/{groupId}/dashboards/{dashboardId}/tiles";

        await SendAuthenticatedRequestAsync(
            HttpMethod.Post,
            GetApiEndpoint(endpoint),
            CreateJsonContent(tilePayload),
            cancellationToken);
    }

    private string GetApiEndpoint(string path)
    {
        if (!string.IsNullOrEmpty(Config?.BaseUrl))
        {
            return path;
        }
        return $"{PowerBiApiBase}/{path}";
    }
}
