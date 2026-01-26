using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.ApacheSuperset
{
    /// <summary>
    /// Abstract base class for BI plugins providing analytics and visualization capabilities.
    /// Extends <see cref="FeaturePluginBase"/> to support lifecycle management and HTTP endpoints.
    /// </summary>
    public abstract class BIPluginBase : FeaturePluginBase
    {
        /// <summary>
        /// Gets the category for BI plugins.
        /// </summary>
        public override PluginCategory Category => PluginCategory.MetricsProvider;

        /// <summary>
        /// Registers a dataset for BI analytics.
        /// </summary>
        /// <param name="name">The dataset name.</param>
        /// <param name="schema">The dataset schema.</param>
        public abstract Task<int> RegisterDatasetAsync(string name, string? schema = null);

        /// <summary>
        /// Registers a metric for a dataset.
        /// </summary>
        /// <param name="datasetId">The dataset ID.</param>
        /// <param name="metricName">The metric name.</param>
        /// <param name="expression">The metric expression (SQL).</param>
        public abstract Task RegisterMetricAsync(int datasetId, string metricName, string expression);

        /// <summary>
        /// Creates a chart (visualization).
        /// </summary>
        /// <param name="name">The chart name.</param>
        /// <param name="datasetId">The dataset ID.</param>
        /// <param name="vizType">The visualization type.</param>
        public abstract Task<int> CreateChartAsync(string name, int datasetId, string vizType);

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "BusinessIntelligence";
            metadata["SupportsDatasets"] = true;
            metadata["SupportsMetrics"] = true;
            metadata["SupportsCharts"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Production-ready Apache Superset BI plugin for analytics and visualization.
    /// Provides dataset registration, metric management, chart creation, and dashboard integration.
    ///
    /// Features:
    /// - REST API integration with Superset
    /// - JWT authentication with auto-refresh
    /// - Dataset and metric registration
    /// - Chart and dashboard management
    /// - SQLAlchemy-based data source support
    /// - Caching and performance optimization
    /// - Comprehensive error handling
    ///
    /// Message Commands:
    /// - superset.login: Authenticate with Superset
    /// - superset.dataset.create: Create a new dataset
    /// - superset.dataset.update: Update an existing dataset
    /// - superset.metric.register: Register a metric for a dataset
    /// - superset.chart.create: Create a new chart
    /// - superset.chart.update: Update an existing chart
    /// - superset.dashboard.create: Create a new dashboard
    /// - superset.query.execute: Execute a SQL query
    /// - superset.status: Get plugin status
    /// </summary>
    public sealed class SupersetPlugin : BIPluginBase
    {
        private readonly SupersetConfiguration _config;
        private readonly HttpClient _httpClient;
        private readonly ConcurrentDictionary<string, int> _datasetCache;
        private readonly ConcurrentDictionary<string, int> _chartCache;
        private readonly object _lock = new();
        private string? _accessToken;
        private string? _refreshToken;
        private DateTime _tokenExpiry;
        private Timer? _refreshTimer;
        private bool _isRunning;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.telemetry.superset";

        /// <inheritdoc/>
        public override string Name => "Apache Superset BI Plugin";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <summary>
        /// Gets the plugin configuration.
        /// </summary>
        public SupersetConfiguration Configuration => _config;

        /// <summary>
        /// Gets whether the plugin is authenticated.
        /// </summary>
        public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken) && _tokenExpiry > DateTime.UtcNow;

        /// <summary>
        /// Gets whether the plugin is running.
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Initializes a new instance of the <see cref="SupersetPlugin"/> class.
        /// </summary>
        /// <param name="config">Optional configuration. Defaults will be used if null.</param>
        public SupersetPlugin(SupersetConfiguration? config = null)
        {
            _config = config ?? new SupersetConfiguration();
            _datasetCache = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            _chartCache = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

            var handler = new HttpClientHandler();
            if (!_config.ValidateSslCertificate)
            {
                handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            }

            _httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(_config.SupersetUrl),
                Timeout = TimeSpan.FromSeconds(_config.RequestTimeoutSeconds)
            };

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = false
            };
        }

        #region Lifecycle Management

        /// <inheritdoc/>
        public override async Task StartAsync(CancellationToken ct)
        {
            lock (_lock)
            {
                if (_isRunning)
                {
                    return;
                }

                _isRunning = true;
            }

            // Authenticate
            await LoginAsync();

            // Setup auto-refresh if enabled
            if (_config.AutoRefreshToken && !string.IsNullOrEmpty(_refreshToken))
            {
                var refreshInterval = TimeSpan.FromMinutes(_config.TokenRefreshIntervalMinutes);
                _refreshTimer = new Timer(async _ => await RefreshTokenAsync(), null, refreshInterval, refreshInterval);
            }

            await Task.CompletedTask;
        }

        /// <inheritdoc/>
        public override async Task StopAsync()
        {
            lock (_lock)
            {
                if (!_isRunning)
                {
                    return;
                }

                _isRunning = false;
            }

            _refreshTimer?.Dispose();
            _refreshTimer = null;

            _accessToken = null;
            _refreshToken = null;

            await Task.CompletedTask;
        }

        #endregion

        #region Authentication

        /// <summary>
        /// Authenticates with the Superset server.
        /// </summary>
        public async Task LoginAsync()
        {
            var loginRequest = new LoginRequest
            {
                Username = _config.Username,
                Password = _config.Password,
                Provider = _config.Provider,
                Refresh = true
            };

            var response = await PostAsync<LoginRequest, LoginResponse>("/api/v1/security/login", loginRequest);

            if (response?.AccessToken == null)
            {
                throw new InvalidOperationException("Failed to authenticate with Superset: no access token received");
            }

            _accessToken = response.AccessToken;
            _refreshToken = response.RefreshToken;

            // JWT tokens typically expire in 1 hour, we'll refresh before that
            _tokenExpiry = DateTime.UtcNow.AddMinutes(_config.TokenRefreshIntervalMinutes);
        }

        /// <summary>
        /// Refreshes the access token using the refresh token.
        /// </summary>
        public async Task RefreshTokenAsync()
        {
            if (string.IsNullOrEmpty(_refreshToken))
            {
                await LoginAsync();
                return;
            }

            try
            {
                var refreshRequest = new RefreshTokenRequest { RefreshToken = _refreshToken };
                var response = await PostAsync<RefreshTokenRequest, LoginResponse>("/api/v1/security/refresh", refreshRequest);

                if (response?.AccessToken != null)
                {
                    _accessToken = response.AccessToken;
                    _tokenExpiry = DateTime.UtcNow.AddMinutes(_config.TokenRefreshIntervalMinutes);
                }
                else
                {
                    // Refresh failed, re-login
                    await LoginAsync();
                }
            }
            catch
            {
                // Refresh failed, re-login
                await LoginAsync();
            }
        }

        #endregion

        #region BIPluginBase Implementation

        /// <inheritdoc/>
        public override async Task<int> RegisterDatasetAsync(string name, string? schema = null)
        {
            var cacheKey = $"{schema ?? "default"}.{name}";
            if (_datasetCache.TryGetValue(cacheKey, out var cachedId))
            {
                return cachedId;
            }

            var dataset = new Dataset
            {
                TableName = name,
                DatabaseId = _config.DefaultDatabaseId ?? 1,
                Schema = schema,
                CacheTimeout = _config.EnableCaching ? _config.CacheTimeoutSeconds : null
            };

            var response = await PostAsync<Dataset, SupersetResponse<Dataset>>("/api/v1/dataset/", dataset);

            if (response?.Ids != null && response.Ids.Count > 0)
            {
                var datasetId = response.Ids[0];
                _datasetCache[cacheKey] = datasetId;
                return datasetId;
            }

            throw new InvalidOperationException($"Failed to register dataset '{name}'");
        }

        /// <inheritdoc/>
        public override async Task RegisterMetricAsync(int datasetId, string metricName, string expression)
        {
            // Fetch the dataset to update it
            var dataset = await GetAsync<Dataset>($"/api/v1/dataset/{datasetId}");

            if (dataset == null)
            {
                throw new InvalidOperationException($"Dataset {datasetId} not found");
            }

            var metric = new DatasetMetric
            {
                MetricName = metricName,
                Expression = expression,
                IsActive = true
            };

            dataset.Metrics ??= new List<DatasetMetric>();
            dataset.Metrics.Add(metric);

            await PutAsync($"/api/v1/dataset/{datasetId}", dataset);
        }

        /// <inheritdoc/>
        public override async Task<int> CreateChartAsync(string name, int datasetId, string vizType)
        {
            var cacheKey = $"{datasetId}.{name}";
            if (_chartCache.TryGetValue(cacheKey, out var cachedId))
            {
                return cachedId;
            }

            var chart = new Chart
            {
                SliceName = name,
                DatasourceId = datasetId,
                DatasourceType = "table",
                VizType = vizType,
                CacheTimeout = _config.EnableCaching ? _config.CacheTimeoutSeconds : null
            };

            var response = await PostAsync<Chart, SupersetResponse<Chart>>("/api/v1/chart/", chart);

            if (response?.Ids != null && response.Ids.Count > 0)
            {
                var chartId = response.Ids[0];
                _chartCache[cacheKey] = chartId;
                return chartId;
            }

            throw new InvalidOperationException($"Failed to create chart '{name}'");
        }

        #endregion

        #region Additional Public Methods

        /// <summary>
        /// Updates an existing dataset.
        /// </summary>
        /// <param name="datasetId">The dataset ID.</param>
        /// <param name="dataset">The updated dataset.</param>
        public async Task UpdateDatasetAsync(int datasetId, Dataset dataset)
        {
            await PutAsync($"/api/v1/dataset/{datasetId}", dataset);
        }

        /// <summary>
        /// Updates an existing chart.
        /// </summary>
        /// <param name="chartId">The chart ID.</param>
        /// <param name="chart">The updated chart.</param>
        public async Task UpdateChartAsync(int chartId, Chart chart)
        {
            await PutAsync($"/api/v1/chart/{chartId}", chart);
        }

        /// <summary>
        /// Creates a dashboard.
        /// </summary>
        /// <param name="dashboard">The dashboard to create.</param>
        /// <returns>The created dashboard ID.</returns>
        public async Task<int> CreateDashboardAsync(Dashboard dashboard)
        {
            var response = await PostAsync<Dashboard, SupersetResponse<Dashboard>>("/api/v1/dashboard/", dashboard);

            if (response?.Ids != null && response.Ids.Count > 0)
            {
                return response.Ids[0];
            }

            throw new InvalidOperationException($"Failed to create dashboard '{dashboard.DashboardTitle}'");
        }

        /// <summary>
        /// Executes a SQL query.
        /// </summary>
        /// <param name="request">The SQL query request.</param>
        /// <returns>The query result.</returns>
        public async Task<string> ExecuteSqlQueryAsync(SqlQueryRequest request)
        {
            var response = await PostAsync<SqlQueryRequest, object>("/api/v1/sqllab/execute/", request);
            return JsonSerializer.Serialize(response, _jsonOptions);
        }

        #endregion

        #region Message Handling

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "superset.login":
                    await HandleLoginAsync(message);
                    break;
                case "superset.dataset.create":
                    await HandleDatasetCreateAsync(message);
                    break;
                case "superset.dataset.update":
                    await HandleDatasetUpdateAsync(message);
                    break;
                case "superset.metric.register":
                    await HandleMetricRegisterAsync(message);
                    break;
                case "superset.chart.create":
                    await HandleChartCreateAsync(message);
                    break;
                case "superset.chart.update":
                    await HandleChartUpdateAsync(message);
                    break;
                case "superset.dashboard.create":
                    await HandleDashboardCreateAsync(message);
                    break;
                case "superset.query.execute":
                    await HandleQueryExecuteAsync(message);
                    break;
                case "superset.status":
                    HandleStatus(message);
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }

        private async Task HandleLoginAsync(PluginMessage message)
        {
            try
            {
                await LoginAsync();
                message.Payload["success"] = true;
                message.Payload["authenticated"] = IsAuthenticated;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
            }
        }

        private async Task HandleDatasetCreateAsync(PluginMessage message)
        {
            var name = GetString(message.Payload, "name");
            if (string.IsNullOrEmpty(name))
            {
                message.Payload["error"] = "Dataset name required";
                return;
            }

            var schema = GetString(message.Payload, "schema");

            try
            {
                var datasetId = await RegisterDatasetAsync(name, schema);
                message.Payload["result"] = datasetId;
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
            }
        }

        private async Task HandleDatasetUpdateAsync(PluginMessage message)
        {
            var datasetId = GetInt(message.Payload, "datasetId");
            if (!datasetId.HasValue)
            {
                message.Payload["error"] = "Dataset ID required";
                return;
            }

            var datasetJson = GetString(message.Payload, "dataset");
            if (string.IsNullOrEmpty(datasetJson))
            {
                message.Payload["error"] = "Dataset data required";
                return;
            }

            try
            {
                var dataset = JsonSerializer.Deserialize<Dataset>(datasetJson, _jsonOptions);
                if (dataset == null)
                {
                    message.Payload["error"] = "Invalid dataset JSON";
                    return;
                }

                await UpdateDatasetAsync(datasetId.Value, dataset);
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
            }
        }

        private async Task HandleMetricRegisterAsync(PluginMessage message)
        {
            var datasetId = GetInt(message.Payload, "datasetId");
            if (!datasetId.HasValue)
            {
                message.Payload["error"] = "Dataset ID required";
                return;
            }

            var metricName = GetString(message.Payload, "metricName");
            var expression = GetString(message.Payload, "expression");

            if (string.IsNullOrEmpty(metricName) || string.IsNullOrEmpty(expression))
            {
                message.Payload["error"] = "Metric name and expression required";
                return;
            }

            try
            {
                await RegisterMetricAsync(datasetId.Value, metricName, expression);
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
            }
        }

        private async Task HandleChartCreateAsync(PluginMessage message)
        {
            var name = GetString(message.Payload, "name");
            var datasetId = GetInt(message.Payload, "datasetId");
            var vizType = GetString(message.Payload, "vizType") ?? "table";

            if (string.IsNullOrEmpty(name) || !datasetId.HasValue)
            {
                message.Payload["error"] = "Chart name and dataset ID required";
                return;
            }

            try
            {
                var chartId = await CreateChartAsync(name, datasetId.Value, vizType);
                message.Payload["result"] = chartId;
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
            }
        }

        private async Task HandleChartUpdateAsync(PluginMessage message)
        {
            var chartId = GetInt(message.Payload, "chartId");
            if (!chartId.HasValue)
            {
                message.Payload["error"] = "Chart ID required";
                return;
            }

            var chartJson = GetString(message.Payload, "chart");
            if (string.IsNullOrEmpty(chartJson))
            {
                message.Payload["error"] = "Chart data required";
                return;
            }

            try
            {
                var chart = JsonSerializer.Deserialize<Chart>(chartJson, _jsonOptions);
                if (chart == null)
                {
                    message.Payload["error"] = "Invalid chart JSON";
                    return;
                }

                await UpdateChartAsync(chartId.Value, chart);
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
            }
        }

        private async Task HandleDashboardCreateAsync(PluginMessage message)
        {
            var dashboardJson = GetString(message.Payload, "dashboard");
            if (string.IsNullOrEmpty(dashboardJson))
            {
                message.Payload["error"] = "Dashboard data required";
                return;
            }

            try
            {
                var dashboard = JsonSerializer.Deserialize<Dashboard>(dashboardJson, _jsonOptions);
                if (dashboard == null)
                {
                    message.Payload["error"] = "Invalid dashboard JSON";
                    return;
                }

                var dashboardId = await CreateDashboardAsync(dashboard);
                message.Payload["result"] = dashboardId;
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
            }
        }

        private async Task HandleQueryExecuteAsync(PluginMessage message)
        {
            var queryJson = GetString(message.Payload, "query");
            if (string.IsNullOrEmpty(queryJson))
            {
                message.Payload["error"] = "Query data required";
                return;
            }

            try
            {
                var query = JsonSerializer.Deserialize<SqlQueryRequest>(queryJson, _jsonOptions);
                if (query == null)
                {
                    message.Payload["error"] = "Invalid query JSON";
                    return;
                }

                var result = await ExecuteSqlQueryAsync(query);
                message.Payload["result"] = result;
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
            }
        }

        private void HandleStatus(PluginMessage message)
        {
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["isRunning"] = _isRunning,
                ["isAuthenticated"] = IsAuthenticated,
                ["supersetUrl"] = _config.SupersetUrl,
                ["datasetsRegistered"] = _datasetCache.Count,
                ["chartsCreated"] = _chartCache.Count,
                ["tokenExpiry"] = _tokenExpiry.ToString("o")
            };
        }

        #endregion

        #region HTTP Helpers

        private async Task<TResponse?> GetAsync<TResponse>(string endpoint)
        {
            await EnsureAuthenticatedAsync();

            var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

            var response = await _httpClient.SendAsync(request);
            await EnsureSuccessAsync(response);

            var content = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<TResponse>(content, _jsonOptions);
        }

        private async Task<TResponse?> PostAsync<TRequest, TResponse>(string endpoint, TRequest data)
        {
            await EnsureAuthenticatedAsync();

            var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

            var json = JsonSerializer.Serialize(data, _jsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            await EnsureSuccessAsync(response);

            var content = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<TResponse>(content, _jsonOptions);
        }

        private async Task PutAsync<TRequest>(string endpoint, TRequest data)
        {
            await EnsureAuthenticatedAsync();

            var request = new HttpRequestMessage(HttpMethod.Put, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

            var json = JsonSerializer.Serialize(data, _jsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            await EnsureSuccessAsync(response);
        }

        private async Task EnsureAuthenticatedAsync()
        {
            if (!IsAuthenticated)
            {
                if (!string.IsNullOrEmpty(_refreshToken))
                {
                    await RefreshTokenAsync();
                }
                else
                {
                    await LoginAsync();
                }
            }
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                SupersetErrorResponse? error = null;

                try
                {
                    error = JsonSerializer.Deserialize<SupersetErrorResponse>(content);
                }
                catch
                {
                    // Ignore deserialization errors
                }

                var errorMessage = error?.Message ?? $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
                throw new HttpRequestException($"Superset API error: {errorMessage}");
            }
        }

        #endregion

        #region Helpers

        private static string? GetString(Dictionary<string, object> payload, string key)
        {
            return payload.TryGetValue(key, out var val) && val is string s ? s : null;
        }

        private static int? GetInt(Dictionary<string, object> payload, string key)
        {
            if (payload.TryGetValue(key, out var val))
            {
                if (val is int i) return i;
                if (val is long l) return (int)l;
                if (val is string s && int.TryParse(s, out var parsed)) return parsed;
            }
            return null;
        }

        #endregion

        #region Plugin Metadata

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "superset.login", DisplayName = "Login", Description = "Authenticate with Superset" },
                new() { Name = "superset.dataset.create", DisplayName = "Create Dataset", Description = "Create a new dataset" },
                new() { Name = "superset.dataset.update", DisplayName = "Update Dataset", Description = "Update an existing dataset" },
                new() { Name = "superset.metric.register", DisplayName = "Register Metric", Description = "Register a metric for a dataset" },
                new() { Name = "superset.chart.create", DisplayName = "Create Chart", Description = "Create a new chart" },
                new() { Name = "superset.chart.update", DisplayName = "Update Chart", Description = "Update an existing chart" },
                new() { Name = "superset.dashboard.create", DisplayName = "Create Dashboard", Description = "Create a new dashboard" },
                new() { Name = "superset.query.execute", DisplayName = "Execute Query", Description = "Execute a SQL query" },
                new() { Name = "superset.status", DisplayName = "Get Status", Description = "Get plugin status" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SupersetUrl"] = _config.SupersetUrl;
            metadata["SupportsDatasets"] = true;
            metadata["SupportsMetrics"] = true;
            metadata["SupportsCharts"] = true;
            metadata["SupportsDashboards"] = true;
            metadata["SupportsSQLQueries"] = true;
            metadata["AuthenticationType"] = "JWT";
            metadata["CachingEnabled"] = _config.EnableCaching;
            return metadata;
        }

        #endregion

        /// <summary>
        /// Disposes resources used by the plugin.
        /// </summary>
        public override void Dispose()
        {
            _refreshTimer?.Dispose();
            _httpClient?.Dispose();
            base.Dispose();
        }
    }
}
