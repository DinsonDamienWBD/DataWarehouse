using System.Net.Http.Json;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Dashboards;

namespace DataWarehouse.Plugins.UltimateInterface.Dashboards.Strategies.OpenSource;

/// <summary>
/// Metabase dashboard strategy providing full integration with Metabase.
/// Supports dashboard creation, card management, collection organization, and embedding.
/// </summary>
public sealed class MetabaseStrategy : DashboardStrategyBase
{
    private string? _sessionToken;
    // P2-3300: serialize sign-in to prevent concurrent auth races on _sessionToken.
    private readonly System.Threading.SemaphoreSlim _authLock = new(1, 1);

    /// <inheritdoc/>
    public override string StrategyId => "metabase";

    /// <inheritdoc/>
    public override string StrategyName => "Metabase";

    /// <inheritdoc/>
    public override string VendorName => "Metabase";

    /// <inheritdoc/>
    public override string Category => "Open Source";

    /// <inheritdoc/>
    public override DashboardCapabilities Capabilities { get; } = new(
        SupportsRealTime: false,
        SupportsTemplates: false,
        SupportsSharing: true,
        SupportsVersioning: false,
        SupportsEmbedding: true,
        SupportsAlerting: true,
        SupportedWidgetTypes: new[] { "Chart", "Table", "Metric", "Heatmap", "Gauge", "Text", "Map", "Pivot", "Progress" },
        MaxWidgetsPerDashboard: 100,
        MaxDashboards: null,
        RefreshIntervals: new[] { 60, 300, 900, 3600, 86400 }
    );

    /// <inheritdoc/>
    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        try
        {
            await EnsureAuthenticatedAsync(cancellationToken);
            var response = await SendAuthenticatedRequestAsync(
                HttpMethod.Get,
                "/api/user/current",
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
        await EnsureAuthenticatedAsync(cancellationToken);
        var startTime = DateTimeOffset.UtcNow;

        // Metabase doesn't have direct data push API
        // Use native query endpoint for data manipulation
        var query = GenerateInsertQuery(targetId, data);

        var payload = new
        {
            database = int.TryParse(Config!.ProjectId, out var dbId) ? dbId : 1,
            native = new
            {
                query = query
            },
            type = "native"
        };

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Post,
            "/api/dataset",
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
                    ["targetId"] = targetId,
                    ["platform"] = "Metabase"
                }
            };
        }

        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        return new DataPushResult
        {
            Success = false,
            RowsPushed = 0,
            DurationMs = duration,
            ErrorMessage = $"Metabase API error: {response.StatusCode} - {error}"
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

        // Create the dashboard
        var createdDashboard = await CreateDashboardCoreAsync(dashboard, cancellationToken);

        // Add cards for each widget
        foreach (var widget in dashboard.Widgets)
        {
            if (!int.TryParse(createdDashboard.Id, out var dashboardId))
                throw new InvalidOperationException($"Metabase returned a non-integer dashboard ID: '{createdDashboard.Id}'");
            await AddCardToDashboardAsync(dashboardId, widget, cancellationToken);
        }

        return createdDashboard;
    }

    /// <inheritdoc/>
    protected override async Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var payload = new
        {
            name = dashboard.Title,
            description = dashboard.Description ?? "",
            collection_id = int.TryParse(Config!.ProjectId, out var collectionId) ? collectionId : (int?)null,
            parameters = Array.Empty<object>()
        };

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Post,
            "/api/dashboard",
            CreateJsonContent(payload),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        return dashboard with
        {
            Id = result.GetProperty("id").GetInt32().ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <inheritdoc/>
    protected override async Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var payload = new
        {
            name = dashboard.Title,
            description = dashboard.Description ?? ""
        };

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Put,
            $"/api/dashboard/{dashboard.Id}",
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
        await EnsureAuthenticatedAsync(cancellationToken);

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Get,
            $"/api/dashboard/{dashboardId}",
            cancellationToken: cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        var widgets = new List<DashboardWidget>();
        if (result.TryGetProperty("dashcards", out var dashcards))
        {
            foreach (var card in dashcards.EnumerateArray())
            {
                var cardId = card.GetProperty("id").GetInt32().ToString();
                var cardTitle = card.TryGetProperty("card", out var cardObj) &&
                               cardObj.TryGetProperty("name", out var name)
                    ? name.GetString() ?? "Card" : "Card";

                var row = card.TryGetProperty("row", out var r) ? r.GetInt32() : 0;
                var col = card.TryGetProperty("col", out var c) ? c.GetInt32() : 0;
                var sizeX = card.TryGetProperty("size_x", out var sx) ? sx.GetInt32() : 4;
                var sizeY = card.TryGetProperty("size_y", out var sy) ? sy.GetInt32() : 3;

                var widgetType = WidgetType.Chart;
                if (cardObj.ValueKind != JsonValueKind.Undefined &&
                    cardObj.TryGetProperty("display", out var display))
                {
                    widgetType = display.GetString() switch
                    {
                        "table" => WidgetType.Table,
                        "scalar" => WidgetType.Metric,
                        "gauge" => WidgetType.Gauge,
                        "map" => WidgetType.Custom,
                        "text" => WidgetType.Text,
                        "progress" => WidgetType.Gauge,
                        _ => WidgetType.Chart
                    };
                }

                widgets.Add(new DashboardWidget(
                    cardId,
                    cardTitle,
                    widgetType,
                    new WidgetPosition(col, row, sizeX, sizeY),
                    DataSourceConfiguration.Metrics("*"),
                    null
                ));
            }
        }

        return new Dashboard(
            Id: dashboardId,
            Title: result.TryGetProperty("name", out var titleProp) ? titleProp.GetString() ?? "Untitled" : "Untitled",
            Description: result.TryGetProperty("description", out var descProp) ? descProp.GetString() : null,
            Layout: DashboardLayout.Grid(),
            Widgets: widgets,
            TimeRange: TimeRange.Last24Hours(),
            RefreshInterval: result.TryGetProperty("cache_ttl", out var cache) && cache.ValueKind == JsonValueKind.Number
                ? cache.GetInt32() : null,
            Tags: null,
            Owner: result.TryGetProperty("creator_id", out var creator)
                ? creator.GetInt32().ToString()
                : null,
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
        await EnsureAuthenticatedAsync(cancellationToken);

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Delete,
            $"/api/dashboard/{dashboardId}",
            cancellationToken: cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    protected override async Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var endpoint = "/api/dashboard";
        if (!string.IsNullOrEmpty(filter?.SearchQuery))
        {
            endpoint = $"/api/search?q={Uri.EscapeDataString(filter.SearchQuery)}&models=dashboard";
        }

        var response = await SendAuthenticatedRequestAsync(
            HttpMethod.Get,
            endpoint,
            cancellationToken: cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var dashboards = new List<Dashboard>();

        var dashboardArray = endpoint.Contains("search")
            ? (result.TryGetProperty("data", out var data) ? data : result)
            : result;

        foreach (var db in dashboardArray.EnumerateArray())
        {
            var id = db.TryGetProperty("id", out var idProp)
                ? (idProp.ValueKind == JsonValueKind.Number ? idProp.GetInt32().ToString() : idProp.GetString())
                : GenerateId();

            var title = db.TryGetProperty("name", out var name) ? name.GetString() ?? "Untitled" : "Untitled";

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
                Owner: db.TryGetProperty("creator_id", out var creator)
                    ? creator.GetInt32().ToString()
                    : null,
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
    protected override Task ApplyAuthenticationAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_sessionToken))
        {
            request.Headers.TryAddWithoutValidation("X-Metabase-Session", _sessionToken);
        }
        return Task.CompletedTask;
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        // Fast-path: already authenticated.
        if (!string.IsNullOrEmpty(_sessionToken)) return;

        // P2-3300: serialize sign-in to prevent concurrent callers from racing on _sessionToken.
        await _authLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        if (!string.IsNullOrEmpty(_sessionToken)) return; // re-check inside lock

        if (Config?.AuthType == AuthenticationType.Basic &&
            !string.IsNullOrEmpty(Config.Username) &&
            !string.IsNullOrEmpty(Config.Password))
        {
            var loginPayload = new
            {
                username = Config.Username,
                password = Config.Password
            };

            var client = GetHttpClient();
            using var response = await client.PostAsync(
                "/api/session",
                CreateJsonContent(loginPayload),
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            _sessionToken = result.GetProperty("id").GetString();
        }
        else if (Config?.AuthType == AuthenticationType.ApiKey && !string.IsNullOrEmpty(Config.ApiKey))
        {
            _sessionToken = Config.ApiKey;
        }
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task AddCardToDashboardAsync(int dashboardId, DashboardWidget widget, CancellationToken cancellationToken)
    {
        // First create a question (card) if needed, then add to dashboard
        var cardPayload = new
        {
            dashboard_id = dashboardId,
            card_id = (int?)null, // null for text cards
            row = widget.Position.Y,
            col = widget.Position.X,
            size_x = widget.Position.Width,
            size_y = widget.Position.Height,
            visualization_settings = new { }
        };

        await SendAuthenticatedRequestAsync(
            HttpMethod.Post,
            "/api/dashboard/cards",
            CreateJsonContent(cardPayload),
            cancellationToken);
    }

    private static string GenerateInsertQuery(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object>> data)
    {
        if (data.Count == 0) return "";

        var columns = data[0].Keys.ToList();
        var columnList = string.Join(", ", columns.Select(c => $"\"{c}\""));

        var values = data.Select(row =>
            "(" + string.Join(", ", columns.Select(c =>
                row.TryGetValue(c, out var v) ? FormatValue(v) : "NULL")) + ")"
        );

        return $"INSERT INTO {tableName} ({columnList}) VALUES {string.Join(", ", values)}";
    }

    private static string FormatValue(object? value)
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
}
