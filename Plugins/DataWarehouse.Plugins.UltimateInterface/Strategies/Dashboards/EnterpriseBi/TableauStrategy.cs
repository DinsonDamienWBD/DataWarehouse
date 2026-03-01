using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Dashboards;

namespace DataWarehouse.Plugins.UltimateInterface.Dashboards.Strategies.EnterpriseBi;

/// <summary>
/// Tableau dashboard strategy providing integration with Tableau Server and Tableau Cloud.
/// Supports workbook publishing, view management, data extracts, and embedded analytics.
/// </summary>
public sealed class TableauStrategy : DashboardStrategyBase
{
    private string? _siteId;
    private string? _authToken;
    // P2-3299: serialize sign-in to prevent concurrent callers from racing on _authToken/_siteId.
    private readonly System.Threading.SemaphoreSlim _authLock = new(1, 1);
    private string _apiVersion = "3.21";

    /// <inheritdoc/>
    public override string StrategyId => "tableau";

    /// <inheritdoc/>
    public override string StrategyName => "Tableau";

    /// <inheritdoc/>
    public override string VendorName => "Salesforce";

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
        SupportedWidgetTypes: new[] { "Chart", "Table", "Metric", "Heatmap", "Gauge", "Text", "Map", "Treemap" },
        MaxWidgetsPerDashboard: null,
        MaxDashboards: null,
        RefreshIntervals: new[] { 60, 300, 900, 1800, 3600, 7200, 14400, 28800, 43200, 86400 }
    );

    /// <inheritdoc/>
    public override void Configure(DashboardConnectionConfig config)
    {
        base.Configure(config);
        _siteId = config.ProjectId ?? "default";
    }

    /// <inheritdoc/>
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        try
        {
            await SignInAsync(cancellationToken);
            return true;
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
        await EnsureAuthenticatedAsync(cancellationToken);

        var startTime = DateTimeOffset.UtcNow;

        // Create hyper file payload for Tableau extract
        var payload = new
        {
            datasource = new
            {
                name = targetId,
                project = new { id = Config!.ProjectId ?? "default" }
            },
            fileUpload = new
            {
                data = ConvertToTableauFormat(data)
            }
        };

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Post,
            $"/api/{_apiVersion}/sites/{_siteId}/datasources",
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
                    ["datasourceId"] = targetId,
                    ["platform"] = "Tableau"
                }
            };
        }

        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        return new DataPushResult
        {
            Success = false,
            RowsPushed = 0,
            DurationMs = duration,
            ErrorMessage = $"Tableau API error: {response.StatusCode} - {error}"
        };
    }

    /// <inheritdoc/>
    public override async Task<Dashboard> ProvisionDashboardAsync(
        Dashboard dashboard,
        DashboardProvisionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        await EnsureAuthenticatedAsync(cancellationToken);

        // Convert to Tableau workbook format (TWB/TWBX)
        var workbook = ConvertToTableauWorkbook(dashboard, options);

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Post,
            $"/api/{_apiVersion}/sites/{_siteId}/workbooks",
            CreateJsonContent(workbook),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var workbookElement = result.GetProperty("workbook");

        return dashboard with
        {
            Id = workbookElement.GetProperty("id").GetString(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <inheritdoc/>
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var workbook = new
        {
            workbook = new
            {
                name = dashboard.Title,
                description = dashboard.Description ?? "",
                showTabs = true,
                project = new { id = Config!.ProjectId ?? "default" }
            }
        };

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Post,
            $"/api/{_apiVersion}/sites/{_siteId}/workbooks",
            CreateJsonContent(workbook),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var wb = result.GetProperty("workbook");

        return dashboard with
        {
            Id = wb.GetProperty("id").GetString(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <inheritdoc/>
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var update = new
        {
            workbook = new
            {
                name = dashboard.Title,
                description = dashboard.Description ?? "",
                showTabs = true
            }
        };

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Put,
            $"/api/{_apiVersion}/sites/{_siteId}/workbooks/{dashboard.Id}",
            CreateJsonContent(update),
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
        await EnsureAuthenticatedAsync(cancellationToken);

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Get,
            $"/api/{_apiVersion}/sites/{_siteId}/workbooks/{dashboardId}",
            cancellationToken: cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var wb = result.GetProperty("workbook");

        // Get views (sheets/dashboards) within workbook
        var viewsResponse = await SendAuthenticatedRequestAsync(
            HttpMethod.Get,
            $"/api/{_apiVersion}/sites/{_siteId}/workbooks/{dashboardId}/views",
            cancellationToken: cancellationToken);

        var widgets = new List<DashboardWidget>();
        if (viewsResponse.IsSuccessStatusCode)
        {
            var viewsResult = await viewsResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (viewsResult.TryGetProperty("views", out var views) &&
                views.TryGetProperty("view", out var viewArray))
            {
                var index = 0;
                foreach (var view in viewArray.EnumerateArray())
                {
                    var viewId = view.GetProperty("id").GetString() ?? GenerateId();
                    var viewName = view.TryGetProperty("name", out var name) ? name.GetString() ?? "View" : "View";

                    widgets.Add(new DashboardWidget(
                        viewId,
                        viewName,
                        WidgetType.Chart,
                        new WidgetPosition(0, index * 3, 12, 3),
                        DataSourceConfiguration.Metrics("*"),
                        new Dictionary<string, object>
                        {
                            ["contentUrl"] = view.TryGetProperty("contentUrl", out var url) ? url.GetString() ?? "" : ""
                        }
                    ));
                    index++;
                }
            }
        }

        return new Dashboard(
            Id: dashboardId,
            Title: wb.TryGetProperty("name", out var titleProp) ? titleProp.GetString() ?? "Untitled" : "Untitled",
            Description: wb.TryGetProperty("description", out var descProp) ? descProp.GetString() : null,
            Layout: DashboardLayout.Grid(),
            Widgets: widgets,
            TimeRange: TimeRange.Last24Hours(),
            RefreshInterval: null,
            Tags: wb.TryGetProperty("tags", out var tags) && tags.TryGetProperty("tag", out var tagArray)
                ? tagArray.EnumerateArray().Select(t => t.GetProperty("label").GetString() ?? "").ToList()
                : null,
            Owner: wb.TryGetProperty("owner", out var owner) && owner.TryGetProperty("name", out var ownerName)
                ? ownerName.GetString()
                : null,
            CreatedAt: wb.TryGetProperty("createdAt", out var created)
                ? DateTimeOffset.Parse(created.GetString() ?? DateTimeOffset.UtcNow.ToString("O"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)
                : DateTimeOffset.UtcNow,
            UpdatedAt: wb.TryGetProperty("updatedAt", out var updated)
                ? DateTimeOffset.Parse(updated.GetString() ?? DateTimeOffset.UtcNow.ToString("O"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)
                : DateTimeOffset.UtcNow,
            Version: 1
        );
    }

    /// <inheritdoc/>
    protected override async Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Delete,
            $"/api/{_apiVersion}/sites/{_siteId}/workbooks/{dashboardId}",
            cancellationToken: cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var endpoint = $"/api/{_apiVersion}/sites/{_siteId}/workbooks";
        var queryParams = new List<string>();

        if (!string.IsNullOrEmpty(filter?.SearchQuery))
        {
            queryParams.Add($"filter=name:matches:{Uri.EscapeDataString(filter.SearchQuery)}");
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

        if (result.TryGetProperty("workbooks", out var workbooks) &&
            workbooks.TryGetProperty("workbook", out var workbookArray))
        {
            foreach (var wb in workbookArray.EnumerateArray())
            {
                var id = wb.GetProperty("id").GetString() ?? GenerateId();
                var title = wb.TryGetProperty("name", out var name) ? name.GetString() ?? "Untitled" : "Untitled";

                List<string>? tags = null;
                if (wb.TryGetProperty("tags", out var tagsProp) && tagsProp.TryGetProperty("tag", out var tagArray))
                {
                    tags = tagArray.EnumerateArray()
                        .Select(t => t.GetProperty("label").GetString() ?? "")
                        .Where(t => !string.IsNullOrEmpty(t))
                        .ToList();
                }

                // Apply tag filter
                if (filter?.Tags != null && filter.Tags.Count > 0)
                {
                    if (tags == null || !filter.Tags.All(t => tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                        continue;
                }

                dashboards.Add(new Dashboard(
                    Id: id,
                    Title: title,
                    Description: wb.TryGetProperty("description", out var desc) ? desc.GetString() : null,
                    Layout: DashboardLayout.Grid(),
                    Widgets: Array.Empty<DashboardWidget>(),
                    TimeRange: TimeRange.Last24Hours(),
                    RefreshInterval: null,
                    Tags: tags,
                    Owner: wb.TryGetProperty("owner", out var owner) && owner.TryGetProperty("name", out var ownerName)
                        ? ownerName.GetString()
                        : null,
                    CreatedAt: wb.TryGetProperty("createdAt", out var created)
                        ? DateTimeOffset.Parse(created.GetString() ?? DateTimeOffset.UtcNow.ToString("O"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)
                        : null,
                    UpdatedAt: wb.TryGetProperty("updatedAt", out var updated)
                        ? DateTimeOffset.Parse(updated.GetString() ?? DateTimeOffset.UtcNow.ToString("O"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)
                        : null,
                    Version: 1
                ));
            }
        }

        // Apply additional filters
        if (filter?.CreatedAfter != null)
            dashboards = dashboards.Where(d => d.CreatedAt >= filter.CreatedAfter).ToList();
        if (filter?.CreatedBefore != null)
            dashboards = dashboards.Where(d => d.CreatedAt <= filter.CreatedBefore).ToList();
        if (!string.IsNullOrEmpty(filter?.Owner))
            dashboards = dashboards.Where(d => d.Owner?.Equals(filter.Owner, StringComparison.OrdinalIgnoreCase) == true).ToList();

        return dashboards;
    }

    /// <inheritdoc/>
    protected override async Task<Dashboard> CreateFromTemplateCoreAsync(
        string templateId,
        IReadOnlyDictionary<string, object>? parameters,
        CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        // Get template workbook
        var templateResponse = await SendAuthenticatedRequestAsync(
            HttpMethod.Get,
            $"/api/{_apiVersion}/sites/{_siteId}/workbooks/{templateId}",
            cancellationToken: cancellationToken);

        templateResponse.EnsureSuccessStatusCode();

        var templateResult = await templateResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var template = templateResult.GetProperty("workbook");

        // Create new workbook based on template
        var newWorkbook = new
        {
            workbook = new
            {
                name = parameters?.TryGetValue("name", out var nameVal) == true ? nameVal.ToString() : $"{template.GetProperty("name").GetString()}_copy",
                description = template.TryGetProperty("description", out var desc) ? desc.GetString() : null,
                project = new { id = Config!.ProjectId ?? "default" }
            }
        };

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Post,
            $"/api/{_apiVersion}/sites/{_siteId}/workbooks",
            CreateJsonContent(newWorkbook),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var wb = result.GetProperty("workbook");

        return new Dashboard(
            Id: wb.GetProperty("id").GetString(),
            Title: wb.TryGetProperty("name", out var name) ? name.GetString() ?? "Untitled" : "Untitled",
            Description: wb.TryGetProperty("description", out var description) ? description.GetString() : null,
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

    private async Task SignInAsync(CancellationToken cancellationToken)
    {
        var credentials = new
        {
            credentials = new
            {
                name = Config!.Username,
                password = Config.Password,
                site = new { contentUrl = _siteId }
            }
        };

        var client = GetHttpClient();
        using var response = await client.PostAsync(
            $"/api/{_apiVersion}/auth/signin",
            CreateJsonContent(credentials),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var creds = result.GetProperty("credentials");
        _authToken = creds.GetProperty("token").GetString();
        _siteId = creds.GetProperty("site").GetProperty("id").GetString();
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        // Fast-path: already authenticated.
        if (!string.IsNullOrEmpty(_authToken)) return;

        // P2-3299: serialize sign-in to prevent concurrent callers from double-signing-in
        // and losing each other's _siteId assignment.
        await _authLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.IsNullOrEmpty(_authToken))
                await SignInAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _authLock.Release();
        }
    }

    /// <inheritdoc/>
    protected override async Task ApplyAuthenticationAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_authToken))
        {
            request.Headers.TryAddWithoutValidation("X-Tableau-Auth", _authToken);
        }
        else
        {
            await base.ApplyAuthenticationAsync(request, cancellationToken);
        }
    }

    private static object ConvertToTableauFormat(IReadOnlyList<IReadOnlyDictionary<string, object>> data)
    {
        // Convert to Tableau Hyper-compatible format
        return new
        {
            rows = data.Select(row => row.ToDictionary(k => k.Key, k => k.Value)).ToArray()
        };
    }

    private object ConvertToTableauWorkbook(Dashboard dashboard, DashboardProvisionOptions? options)
    {
        return new
        {
            workbook = new
            {
                name = dashboard.Title,
                description = dashboard.Description ?? "",
                showTabs = true,
                project = new { id = options?.TargetFolder ?? Config!.ProjectId ?? "default" },
                views = dashboard.Widgets.Select((w, i) => new
                {
                    name = w.Title,
                    index = i
                }).ToArray()
            }
        };
    }
}
