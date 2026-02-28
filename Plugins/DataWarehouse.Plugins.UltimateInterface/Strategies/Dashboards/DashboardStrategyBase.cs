using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Dashboards;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateInterface.Dashboards;

/// <summary>
/// Authentication type for dashboard services.
/// </summary>
public enum AuthenticationType
{
    /// <summary>No authentication required.</summary>
    None,
    /// <summary>API key authentication.</summary>
    ApiKey,
    /// <summary>Basic authentication (username/password).</summary>
    Basic,
    /// <summary>Bearer token authentication.</summary>
    Bearer,
    /// <summary>OAuth 2.0 authentication.</summary>
    OAuth2,
    /// <summary>SAML-based single sign-on.</summary>
    Saml,
    /// <summary>Custom authentication scheme.</summary>
    Custom
}

/// <summary>
/// Connection configuration for dashboard strategies.
/// </summary>
public sealed record DashboardConnectionConfig
{
    /// <summary>Base URL of the dashboard service.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>Authentication type to use.</summary>
    public AuthenticationType AuthType { get; init; } = AuthenticationType.None;

    /// <summary>API key for ApiKey authentication.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Username for Basic authentication.</summary>
    public string? Username { get; init; }

    /// <summary>Password for Basic authentication.</summary>
    public string? Password { get; init; }

    /// <summary>Bearer token for Bearer authentication.</summary>
    public string? BearerToken { get; init; }

    /// <summary>OAuth2 client ID.</summary>
    public string? OAuth2ClientId { get; init; }

    /// <summary>OAuth2 client secret.</summary>
    public string? OAuth2ClientSecret { get; init; }

    /// <summary>OAuth2 token endpoint.</summary>
    public string? OAuth2TokenEndpoint { get; init; }

    /// <summary>OAuth2 scopes.</summary>
    public string[]? OAuth2Scopes { get; init; }

    /// <summary>Connection timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>Maximum retry attempts.</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>Enable SSL/TLS verification.</summary>
    public bool VerifySsl { get; init; } = true;

    /// <summary>Custom headers to include in requests.</summary>
    public IReadOnlyDictionary<string, string>? CustomHeaders { get; init; }

    /// <summary>Organization or workspace ID.</summary>
    public string? OrganizationId { get; init; }

    /// <summary>Project or site ID.</summary>
    public string? ProjectId { get; init; }
}

/// <summary>
/// Result of a data push operation.
/// </summary>
public sealed record DataPushResult
{
    /// <summary>Whether the push was successful.</summary>
    public required bool Success { get; init; }

    /// <summary>Number of rows pushed.</summary>
    public long RowsPushed { get; init; }

    /// <summary>Time taken in milliseconds.</summary>
    public double DurationMs { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Additional metadata.</summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Dashboard provisioning options.
/// </summary>
public sealed record DashboardProvisionOptions
{
    /// <summary>Whether to overwrite existing dashboards.</summary>
    public bool Overwrite { get; init; } = false;

    /// <summary>Target folder or workspace.</summary>
    public string? TargetFolder { get; init; }

    /// <summary>Permissions to apply.</summary>
    public IReadOnlyDictionary<string, string>? Permissions { get; init; }

    /// <summary>Variables/parameters to substitute.</summary>
    public IReadOnlyDictionary<string, object>? Variables { get; init; }

    /// <summary>Enable dashboard immediately after creation.</summary>
    public bool EnableImmediately { get; init; } = true;
}

/// <summary>
/// Statistics for a dashboard strategy.
/// </summary>
public sealed class DashboardStrategyStatistics
{
    /// <summary>Total dashboards created.</summary>
    public long DashboardsCreated { get; set; }

    /// <summary>Total dashboards updated.</summary>
    public long DashboardsUpdated { get; set; }

    /// <summary>Total dashboards deleted.</summary>
    public long DashboardsDeleted { get; set; }

    /// <summary>Total data push operations.</summary>
    public long DataPushOperations { get; set; }

    /// <summary>Total rows pushed.</summary>
    public long TotalRowsPushed { get; set; }

    /// <summary>Total bytes pushed.</summary>
    public long TotalBytesPushed { get; set; }

    /// <summary>Total errors encountered.</summary>
    public long Errors { get; set; }

    /// <summary>Average operation duration in milliseconds.</summary>
    public double AverageOperationDurationMs { get; set; }
}

/// <summary>
/// Abstract base class for dashboard strategy implementations.
/// Provides common functionality for authentication, HTTP requests, connection pooling, and statistics.
/// Inherits lifecycle, dispose, and health caching from StrategyBase.
/// </summary>
public abstract class DashboardStrategyBase : StrategyBase, IDashboardStrategy
{
    private readonly BoundedDictionary<string, Dashboard> _dashboardCache = new BoundedDictionary<string, Dashboard>(1000);
    private readonly BoundedDictionary<string, HttpClient> _httpClientPool = new BoundedDictionary<string, HttpClient>(1000);
    private readonly DashboardStrategyStatistics _statistics = new();
    private readonly SemaphoreSlim _rateLimiter;
    private readonly SemaphoreSlim _tokenRefreshLock = new SemaphoreSlim(1, 1);
    private readonly object _statsLock = new();
    private string? _cachedAccessToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    protected static readonly HttpClient SharedHttpClient = new HttpClient();

    /// <summary>
    /// Gets the unique identifier for this strategy.
    /// </summary>
    public abstract override string StrategyId { get; }

    /// <summary>
    /// Gets the display name of this strategy.
    /// </summary>
    public abstract string StrategyName { get; }

    /// <inheritdoc/>
    public override string Name => StrategyName;

    /// <summary>
    /// Gets the vendor or platform name.
    /// </summary>
    public abstract string VendorName { get; }

    /// <summary>
    /// Gets the strategy category (e.g., "Enterprise BI", "Open Source").
    /// </summary>
    public abstract string Category { get; }

    /// <summary>
    /// Gets the dashboard capabilities.
    /// </summary>
    public abstract DashboardCapabilities Capabilities { get; }

    /// <summary>
    /// Gets the connection configuration.
    /// </summary>
    protected DashboardConnectionConfig? Config { get; private set; }

    /// <summary>
    /// Gets the rate limit (requests per second).
    /// </summary>
    protected virtual int RateLimitPerSecond => 10;

    /// <summary>
    /// Initializes a new instance of the dashboard strategy.
    /// </summary>
    protected DashboardStrategyBase()
    {
        _rateLimiter = new SemaphoreSlim(RateLimitPerSecond, RateLimitPerSecond);
        StartRateLimitRefresh();
    }

    /// <summary>
    /// Configures the strategy with connection settings.
    /// </summary>
    /// <param name="config">Connection configuration.</param>
    public virtual void Configure(DashboardConnectionConfig config)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _dashboardCache.Clear();
        _cachedAccessToken = null;
        _tokenExpiry = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Gets statistics for this strategy.
    /// </summary>
    public DashboardStrategyStatistics GetStatistics()
    {
        lock (_statsLock)
        {
            return new DashboardStrategyStatistics
            {
                DashboardsCreated = _statistics.DashboardsCreated,
                DashboardsUpdated = _statistics.DashboardsUpdated,
                DashboardsDeleted = _statistics.DashboardsDeleted,
                DataPushOperations = _statistics.DataPushOperations,
                TotalRowsPushed = _statistics.TotalRowsPushed,
                TotalBytesPushed = _statistics.TotalBytesPushed,
                Errors = _statistics.Errors,
                AverageOperationDurationMs = _statistics.AverageOperationDurationMs
            };
        }
    }

    /// <summary>
    /// Pushes data to a dashboard or data source.
    /// </summary>
    /// <param name="targetId">Target dashboard or dataset ID.</param>
    /// <param name="data">Data to push.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the push operation.</returns>
    public abstract Task<DataPushResult> PushDataAsync(
        string targetId,
        IReadOnlyList<IReadOnlyDictionary<string, object>> data,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Provisions a dashboard from a template or configuration.
    /// </summary>
    /// <param name="dashboard">Dashboard configuration.</param>
    /// <param name="options">Provisioning options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The provisioned dashboard.</returns>
    public abstract Task<Dashboard> ProvisionDashboardAsync(
        Dashboard dashboard,
        DashboardProvisionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tests the connection to the dashboard service.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if connection is successful.</returns>
    public abstract Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);

    #region IDashboardStrategy Implementation

    /// <inheritdoc/>
    public async Task<Dashboard> CreateDashboardAsync(Dashboard dashboard, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dashboard);
        EnsureConfigured();

        var startTime = DateTimeOffset.UtcNow;
        try
        {
            var result = await CreateDashboardCoreAsync(dashboard, cancellationToken);
            _dashboardCache[result.Id!] = result;
            UpdateStatistics(ops => ops.DashboardsCreated++, startTime);
            return result;
        }
        catch
        {
            IncrementErrors();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<Dashboard> UpdateDashboardAsync(Dashboard dashboard, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dashboard);
        if (string.IsNullOrEmpty(dashboard.Id))
            throw new ArgumentException("Dashboard ID is required for updates", nameof(dashboard));
        EnsureConfigured();

        var startTime = DateTimeOffset.UtcNow;
        try
        {
            var result = await UpdateDashboardCoreAsync(dashboard, cancellationToken);
            _dashboardCache[result.Id!] = result;
            UpdateStatistics(ops => ops.DashboardsUpdated++, startTime);
            return result;
        }
        catch
        {
            IncrementErrors();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<Dashboard> GetDashboardAsync(string dashboardId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(dashboardId))
            throw new ArgumentNullException(nameof(dashboardId));
        EnsureConfigured();

        if (_dashboardCache.TryGetValue(dashboardId, out var cached))
            return cached;

        var dashboard = await GetDashboardCoreAsync(dashboardId, cancellationToken);
        _dashboardCache[dashboardId] = dashboard;
        return dashboard;
    }

    /// <inheritdoc/>
    public async Task DeleteDashboardAsync(string dashboardId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(dashboardId))
            throw new ArgumentNullException(nameof(dashboardId));
        EnsureConfigured();

        var startTime = DateTimeOffset.UtcNow;
        try
        {
            await DeleteDashboardCoreAsync(dashboardId, cancellationToken);
            _dashboardCache.TryRemove(dashboardId, out _);
            UpdateStatistics(ops => ops.DashboardsDeleted++, startTime);
        }
        catch
        {
            IncrementErrors();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Dashboard>> ListDashboardsAsync(DashboardFilter? filter = null, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        return await ListDashboardsCoreAsync(filter, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<Dashboard> CreateFromTemplateAsync(string templateId, IReadOnlyDictionary<string, object>? parameters = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(templateId))
            throw new ArgumentNullException(nameof(templateId));

        if (!Capabilities.SupportsTemplates)
            throw new NotSupportedException($"{StrategyName} does not support templates");

        EnsureConfigured();

        var startTime = DateTimeOffset.UtcNow;
        try
        {
            var result = await CreateFromTemplateCoreAsync(templateId, parameters, cancellationToken);
            _dashboardCache[result.Id!] = result;
            UpdateStatistics(ops => ops.DashboardsCreated++, startTime);
            return result;
        }
        catch
        {
            IncrementErrors();
            throw;
        }
    }

    #endregion

    #region Abstract Core Methods

    /// <summary>
    /// Core implementation for creating a dashboard.
    /// </summary>
    protected abstract Task<Dashboard> CreateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);

    /// <summary>
    /// Core implementation for updating a dashboard.
    /// </summary>
    protected abstract Task<Dashboard> UpdateDashboardCoreAsync(Dashboard dashboard, CancellationToken cancellationToken);

    /// <summary>
    /// Core implementation for retrieving a dashboard.
    /// </summary>
    protected abstract Task<Dashboard> GetDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);

    /// <summary>
    /// Core implementation for deleting a dashboard.
    /// </summary>
    protected abstract Task DeleteDashboardCoreAsync(string dashboardId, CancellationToken cancellationToken);

    /// <summary>
    /// Core implementation for listing dashboards.
    /// </summary>
    protected abstract Task<IReadOnlyList<Dashboard>> ListDashboardsCoreAsync(DashboardFilter? filter, CancellationToken cancellationToken);

    /// <summary>
    /// Core implementation for creating from template.
    /// </summary>
    protected virtual Task<Dashboard> CreateFromTemplateCoreAsync(string templateId, IReadOnlyDictionary<string, object>? parameters, CancellationToken cancellationToken)
    {
        throw new NotSupportedException($"{StrategyName} does not support templates");
    }

    #endregion

    #region HTTP Client Management

    /// <summary>
    /// Gets or creates an HTTP client for the configured service.
    /// </summary>
    protected HttpClient GetHttpClient()
    {
        EnsureConfigured();
        var key = Config!.BaseUrl;

        return _httpClientPool.GetOrAdd(key, _ =>
        {
            var handler = new HttpClientHandler();
            if (!Config.VerifySsl)
            {
                handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            }

            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(Config.BaseUrl),
                Timeout = TimeSpan.FromSeconds(Config.TimeoutSeconds)
            };

            // Apply custom headers
            if (Config.CustomHeaders != null)
            {
                foreach (var header in Config.CustomHeaders)
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return client;
        });
    }

    /// <summary>
    /// Sends an authenticated HTTP request with rate limiting and retries.
    /// </summary>
    protected async Task<HttpResponseMessage> SendAuthenticatedRequestAsync(
        HttpMethod method,
        string endpoint,
        HttpContent? content = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        await _rateLimiter.WaitAsync(cancellationToken);

        var client = GetHttpClient();
        var retries = 0;

        while (retries <= Config!.MaxRetries)
        {
            try
            {
                var request = new HttpRequestMessage(method, endpoint);
                request.Content = content;

                // Apply authentication
                await ApplyAuthenticationAsync(request, cancellationToken);

                var response = await client.SendAsync(request, cancellationToken);

                // Handle rate limiting
                if ((int)response.StatusCode == 429)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, retries));
                    await Task.Delay(retryAfter, cancellationToken);
                    retries++;
                    continue;
                }

                return response;
            }
            catch (HttpRequestException) when (retries < Config.MaxRetries)
            {
                retries++;
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retries)), cancellationToken);
            }
        }

        throw new HttpRequestException($"Failed after {Config.MaxRetries} retries");
    }

    /// <summary>
    /// Applies authentication to an HTTP request.
    /// </summary>
    protected virtual async Task ApplyAuthenticationAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Config == null) return;

        switch (Config.AuthType)
        {
            case AuthenticationType.ApiKey:
                if (!string.IsNullOrEmpty(Config.ApiKey))
                {
                    request.Headers.TryAddWithoutValidation("X-API-Key", Config.ApiKey);
                    request.Headers.TryAddWithoutValidation("Authorization", $"Api-Key {Config.ApiKey}");
                }
                break;

            case AuthenticationType.Basic:
                if (!string.IsNullOrEmpty(Config.Username) && !string.IsNullOrEmpty(Config.Password))
                {
                    var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Config.Username}:{Config.Password}"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                }
                break;

            case AuthenticationType.Bearer:
                if (!string.IsNullOrEmpty(Config.BearerToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.BearerToken);
                }
                break;

            case AuthenticationType.OAuth2:
                var token = await GetOAuth2TokenAsync(cancellationToken);
                if (!string.IsNullOrEmpty(token))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
                break;
        }
    }

    /// <summary>
    /// Gets or refreshes an OAuth2 access token. Thread-safe via SemaphoreSlim.
    /// </summary>
    protected virtual async Task<string?> GetOAuth2TokenAsync(CancellationToken cancellationToken)
    {
        if (Config == null || Config.AuthType != AuthenticationType.OAuth2) return null;

        // Fast path: return cached token if still valid (read without lock first)
        if (!string.IsNullOrEmpty(_cachedAccessToken) && DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-5))
        {
            return _cachedAccessToken;
        }

        // Serialize token refresh to prevent concurrent refresh races
        await _tokenRefreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring lock (another thread may have refreshed already)
            if (!string.IsNullOrEmpty(_cachedAccessToken) && DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-5))
            {
                return _cachedAccessToken;
            }

            if (string.IsNullOrEmpty(Config.OAuth2TokenEndpoint) ||
                string.IsNullOrEmpty(Config.OAuth2ClientId) ||
                string.IsNullOrEmpty(Config.OAuth2ClientSecret))
            {
                return null;
            }

            var requestBody = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = Config.OAuth2ClientId,
                ["client_secret"] = Config.OAuth2ClientSecret
            };

            if (Config.OAuth2Scopes != null)
            {
                requestBody["scope"] = string.Join(" ", Config.OAuth2Scopes);
            }

            using var response = await SharedHttpClient.PostAsync(
                Config.OAuth2TokenEndpoint,
                new FormUrlEncodedContent(requestBody),
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var tokenResponse = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            _cachedAccessToken = tokenResponse.GetProperty("access_token").GetString();

            if (tokenResponse.TryGetProperty("expires_in", out var expiresIn))
            {
                _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn.GetInt32());
            }
            else
            {
                _tokenExpiry = DateTimeOffset.UtcNow.AddHours(1);
            }

            return _cachedAccessToken;
        }
        finally
        {
            _tokenRefreshLock.Release();
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Ensures the strategy has been configured.
    /// </summary>
    protected void EnsureConfigured()
    {
        EnsureNotDisposed();
        if (Config == null)
            throw new InvalidOperationException($"{StrategyName} has not been configured. Call Configure() first.");
    }

    /// <summary>
    /// Serializes an object to JSON.
    /// </summary>
    private static readonly JsonSerializerOptions s_serializeOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions s_deserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    protected static string SerializeToJson(object obj)
    {
        return System.Text.Json.JsonSerializer.Serialize(obj, s_serializeOptions);
    }

    /// <summary>
    /// Deserializes JSON to an object.
    /// </summary>
    protected static T? DeserializeFromJson<T>(string json)
    {
        return System.Text.Json.JsonSerializer.Deserialize<T>(json, s_deserializeOptions);
    }

    /// <summary>
    /// Creates JSON HTTP content.
    /// </summary>
    protected static StringContent CreateJsonContent(object obj)
    {
        return new StringContent(SerializeToJson(obj), Encoding.UTF8, "application/json");
    }

    /// <summary>
    /// Generates a unique ID for dashboards.
    /// </summary>
    protected static string GenerateId()
    {
        return Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Computes a hash for caching purposes.
    /// </summary>
    protected static string ComputeHash(string input)
    {
        // Note: Bus delegation not available in this context; using direct crypto
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Converts SDK dashboard to platform-specific format.
    /// </summary>
    protected virtual object ConvertToPlatformFormat(Dashboard dashboard)
    {
        return new
        {
            id = dashboard.Id,
            title = dashboard.Title,
            description = dashboard.Description,
            widgets = dashboard.Widgets.Select(w => new
            {
                id = w.Id,
                title = w.Title,
                type = w.Type.ToString().ToLowerInvariant(),
                position = new
                {
                    x = w.Position.X,
                    y = w.Position.Y,
                    width = w.Position.Width,
                    height = w.Position.Height
                },
                dataSource = new
                {
                    type = w.DataSource.Type,
                    query = w.DataSource.Query,
                    parameters = w.DataSource.Parameters
                },
                configuration = w.Configuration
            }).ToArray(),
            layout = new
            {
                type = dashboard.Layout.Type.ToString().ToLowerInvariant(),
                columns = dashboard.Layout.Columns,
                rowHeight = dashboard.Layout.RowHeight
            },
            timeRange = dashboard.TimeRange.IsRelative
                ? new { relative = dashboard.TimeRange.RelativeRange }
                : new { from = dashboard.TimeRange.From?.ToString("O"), to = dashboard.TimeRange.To?.ToString("O") } as object,
            refreshInterval = dashboard.RefreshInterval,
            tags = dashboard.Tags,
            version = dashboard.Version
        };
    }

    /// <summary>
    /// Converts platform response to SDK dashboard format.
    /// </summary>
    protected virtual Dashboard ConvertFromPlatformFormat(JsonElement element)
    {
        var id = element.GetProperty("id").GetString() ?? GenerateId();
        var title = element.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "Untitled" : "Untitled";
        var description = element.TryGetProperty("description", out var descProp) ? descProp.GetString() : null;

        var widgets = new List<DashboardWidget>();
        if (element.TryGetProperty("widgets", out var widgetsProp) && widgetsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in widgetsProp.EnumerateArray())
            {
                var widgetId = w.TryGetProperty("id", out var wId) ? wId.GetString() ?? GenerateId() : GenerateId();
                var widgetTitle = w.TryGetProperty("title", out var wTitle) ? wTitle.GetString() ?? "Widget" : "Widget";
                var widgetType = WidgetType.Chart;
                if (w.TryGetProperty("type", out var wType))
                {
                    Enum.TryParse<WidgetType>(wType.GetString(), true, out widgetType);
                }

                var position = WidgetPosition.Auto();
                if (w.TryGetProperty("position", out var pos))
                {
                    position = new WidgetPosition(
                        pos.TryGetProperty("x", out var x) ? x.GetInt32() : 0,
                        pos.TryGetProperty("y", out var y) ? y.GetInt32() : 0,
                        pos.TryGetProperty("width", out var width) ? width.GetInt32() : 6,
                        pos.TryGetProperty("height", out var height) ? height.GetInt32() : 3
                    );
                }

                var dataSource = DataSourceConfiguration.Metrics("*");
                if (w.TryGetProperty("dataSource", out var ds))
                {
                    var dsType = ds.TryGetProperty("type", out var dsT) ? dsT.GetString() ?? "Metrics" : "Metrics";
                    var dsQuery = ds.TryGetProperty("query", out var dsQ) ? dsQ.GetString() ?? "*" : "*";
                    dataSource = new DataSourceConfiguration(dsType, dsQuery, null);
                }

                widgets.Add(new DashboardWidget(widgetId, widgetTitle, widgetType, position, dataSource, null));
            }
        }

        var layout = DashboardLayout.Grid();
        if (element.TryGetProperty("layout", out var layoutProp))
        {
            var layoutType = LayoutType.Grid;
            if (layoutProp.TryGetProperty("type", out var lt))
            {
                Enum.TryParse<LayoutType>(lt.GetString(), true, out layoutType);
            }
            var columns = layoutProp.TryGetProperty("columns", out var cols) ? cols.GetInt32() : 12;
            var rowHeight = layoutProp.TryGetProperty("rowHeight", out var rh) ? rh.GetInt32() : 100;
            layout = new DashboardLayout(layoutType, columns, rowHeight, null);
        }

        var timeRange = TimeRange.Last24Hours();
        int? refreshInterval = null;
        if (element.TryGetProperty("refreshInterval", out var ri) && ri.ValueKind == JsonValueKind.Number)
        {
            refreshInterval = ri.GetInt32();
        }

        List<string>? tags = null;
        if (element.TryGetProperty("tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array)
        {
            tags = tagsProp.EnumerateArray().Select(t => t.GetString() ?? "").Where(t => !string.IsNullOrEmpty(t)).ToList();
        }

        var version = element.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.Number ? ver.GetInt32() : 1;

        return new Dashboard(
            Id: id,
            Title: title,
            Description: description,
            Layout: layout,
            Widgets: widgets,
            TimeRange: timeRange,
            RefreshInterval: refreshInterval,
            Tags: tags,
            Owner: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            Version: version
        );
    }

    private void UpdateStatistics(Action<DashboardStrategyStatistics> update, DateTimeOffset startTime)
    {
        var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
        lock (_statsLock)
        {
            update(_statistics);
            var totalOps = _statistics.DashboardsCreated + _statistics.DashboardsUpdated +
                          _statistics.DashboardsDeleted + _statistics.DataPushOperations;
            _statistics.AverageOperationDurationMs =
                ((_statistics.AverageOperationDurationMs * (totalOps - 1)) + duration) / totalOps;
        }
    }

    private void IncrementErrors()
    {
        lock (_statsLock)
        {
            _statistics.Errors++;
        }
    }

    private void StartRateLimitRefresh()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(1000);
                try
                {
                    var toRelease = RateLimitPerSecond - _rateLimiter.CurrentCount;
                    if (toRelease > 0)
                    {
                        _rateLimiter.Release(toRelease);
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        });
    }

    #endregion

    #region Dispose

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var client in _httpClientPool.Values)
            {
                client.Dispose();
            }
            _httpClientPool.Clear();
            _dashboardCache.Clear();
            _rateLimiter.Dispose();
        }
        base.Dispose(disposing);
    }

    #endregion
}
