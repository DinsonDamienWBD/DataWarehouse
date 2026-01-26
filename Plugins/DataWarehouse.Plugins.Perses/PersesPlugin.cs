using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.Perses
{
    /// <summary>
    /// Production-ready Perses cloud-native dashboarding plugin.
    /// Provides integration with Perses dashboards and datasources via REST API.
    ///
    /// Features:
    /// - Dashboard creation, update, retrieval, and deletion
    /// - Datasource management with Prometheus support
    /// - Panel and layout configuration
    /// - Variable and query support
    /// - Automatic dashboard/datasource creation
    /// - REST API integration with authentication
    ///
    /// Message Commands:
    /// - perses.dashboard.create: Create a dashboard
    /// - perses.dashboard.update: Update a dashboard
    /// - perses.dashboard.get: Get a dashboard by name
    /// - perses.dashboard.list: List all dashboards
    /// - perses.dashboard.delete: Delete a dashboard
    /// - perses.datasource.create: Create a datasource
    /// - perses.datasource.update: Update a datasource
    /// - perses.datasource.get: Get a datasource by name
    /// - perses.datasource.list: List all datasources
    /// - perses.datasource.delete: Delete a datasource
    /// - perses.status: Get plugin status
    /// </summary>
    public sealed class PersesPlugin : FeaturePluginBase
    {
        private readonly PersesConfiguration _config;
        private readonly HttpClient _httpClient;
        private readonly JsonSerializerOptions _jsonOptions;
        private bool _isRunning;
        private readonly object _lock = new();

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.telemetry.perses";

        /// <inheritdoc/>
        public override string Name => "Perses Dashboard Plugin";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <inheritdoc/>
        public override PluginCategory Category => PluginCategory.MetricsProvider;

        /// <summary>
        /// Gets the plugin configuration.
        /// </summary>
        public PersesConfiguration Configuration => _config;

        /// <summary>
        /// Gets whether the plugin is running.
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Initializes a new instance of the <see cref="PersesPlugin"/> class.
        /// </summary>
        /// <param name="config">Optional configuration. Defaults will be used if null.</param>
        public PersesPlugin(PersesConfiguration? config = null)
        {
            _config = config ?? new PersesConfiguration();

            var handler = new HttpClientHandler();
            if (!_config.ValidateSsl)
            {
                handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            }

            _httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(_config.PersesUrl),
                Timeout = _config.RequestTimeout
            };

            if (!string.IsNullOrEmpty(_config.ApiKey))
            {
                _httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", _config.ApiKey);
            }

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
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

            // Verify connectivity to Perses API
            try
            {
                var response = await _httpClient.GetAsync("/api/v1/health", ct);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        $"Failed to connect to Perses API at {_config.PersesUrl}: {response.StatusCode}");
                }
            }
            catch (HttpRequestException ex)
            {
                _isRunning = false;
                throw new InvalidOperationException(
                    $"Failed to connect to Perses API at {_config.PersesUrl}: {ex.Message}", ex);
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

            await Task.CompletedTask;
        }

        #endregion

        #region Dashboard Operations

        /// <summary>
        /// Creates a new dashboard.
        /// </summary>
        /// <param name="dashboard">The dashboard to create.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The created dashboard.</returns>
        public async Task<PersesDashboard> CreateDashboardAsync(PersesDashboard dashboard, CancellationToken ct = default)
        {
            var project = dashboard.Metadata.Project ?? _config.Project;
            var url = $"/api/v1/projects/{project}/dashboards";

            var json = JsonSerializer.Serialize(dashboard, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(url, content, ct);
            await EnsureSuccessAsync(response);

            var result = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<PersesDashboard>(result, _jsonOptions)!;
        }

        /// <summary>
        /// Updates an existing dashboard.
        /// </summary>
        /// <param name="dashboard">The dashboard to update.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The updated dashboard.</returns>
        public async Task<PersesDashboard> UpdateDashboardAsync(PersesDashboard dashboard, CancellationToken ct = default)
        {
            var project = dashboard.Metadata.Project ?? _config.Project;
            var name = dashboard.Metadata.Name;
            var url = $"/api/v1/projects/{project}/dashboards/{name}";

            var json = JsonSerializer.Serialize(dashboard, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PutAsync(url, content, ct);
            await EnsureSuccessAsync(response);

            var result = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<PersesDashboard>(result, _jsonOptions)!;
        }

        /// <summary>
        /// Gets a dashboard by name.
        /// </summary>
        /// <param name="name">The dashboard name.</param>
        /// <param name="project">The project name (optional, uses default if null).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The dashboard, or null if not found.</returns>
        public async Task<PersesDashboard?> GetDashboardAsync(string name, string? project = null, CancellationToken ct = default)
        {
            project ??= _config.Project;
            var url = $"/api/v1/projects/{project}/dashboards/{name}";

            var response = await _httpClient.GetAsync(url, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            await EnsureSuccessAsync(response);

            var result = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<PersesDashboard>(result, _jsonOptions);
        }

        /// <summary>
        /// Lists all dashboards in a project.
        /// </summary>
        /// <param name="project">The project name (optional, uses default if null).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>List of dashboards.</returns>
        public async Task<List<PersesDashboard>> ListDashboardsAsync(string? project = null, CancellationToken ct = default)
        {
            project ??= _config.Project;
            var url = $"/api/v1/projects/{project}/dashboards";

            var response = await _httpClient.GetAsync(url, ct);
            await EnsureSuccessAsync(response);

            var result = await response.Content.ReadAsStringAsync(ct);
            var list = JsonSerializer.Deserialize<DashboardList>(result, _jsonOptions);
            return list?.Items ?? new List<PersesDashboard>();
        }

        /// <summary>
        /// Deletes a dashboard by name.
        /// </summary>
        /// <param name="name">The dashboard name.</param>
        /// <param name="project">The project name (optional, uses default if null).</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task DeleteDashboardAsync(string name, string? project = null, CancellationToken ct = default)
        {
            project ??= _config.Project;
            var url = $"/api/v1/projects/{project}/dashboards/{name}";

            var response = await _httpClient.DeleteAsync(url, ct);
            await EnsureSuccessAsync(response);
        }

        #endregion

        #region Datasource Operations

        /// <summary>
        /// Creates a new datasource.
        /// </summary>
        /// <param name="datasource">The datasource to create.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The created datasource.</returns>
        public async Task<PersesDatasource> CreateDatasourceAsync(PersesDatasource datasource, CancellationToken ct = default)
        {
            var project = datasource.Metadata.Project ?? _config.Project;
            var url = $"/api/v1/projects/{project}/datasources";

            var json = JsonSerializer.Serialize(datasource, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(url, content, ct);
            await EnsureSuccessAsync(response);

            var result = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<PersesDatasource>(result, _jsonOptions)!;
        }

        /// <summary>
        /// Updates an existing datasource.
        /// </summary>
        /// <param name="datasource">The datasource to update.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The updated datasource.</returns>
        public async Task<PersesDatasource> UpdateDatasourceAsync(PersesDatasource datasource, CancellationToken ct = default)
        {
            var project = datasource.Metadata.Project ?? _config.Project;
            var name = datasource.Metadata.Name;
            var url = $"/api/v1/projects/{project}/datasources/{name}";

            var json = JsonSerializer.Serialize(datasource, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PutAsync(url, content, ct);
            await EnsureSuccessAsync(response);

            var result = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<PersesDatasource>(result, _jsonOptions)!;
        }

        /// <summary>
        /// Gets a datasource by name.
        /// </summary>
        /// <param name="name">The datasource name.</param>
        /// <param name="project">The project name (optional, uses default if null).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The datasource, or null if not found.</returns>
        public async Task<PersesDatasource?> GetDatasourceAsync(string name, string? project = null, CancellationToken ct = default)
        {
            project ??= _config.Project;
            var url = $"/api/v1/projects/{project}/datasources/{name}";

            var response = await _httpClient.GetAsync(url, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            await EnsureSuccessAsync(response);

            var result = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<PersesDatasource>(result, _jsonOptions);
        }

        /// <summary>
        /// Lists all datasources in a project.
        /// </summary>
        /// <param name="project">The project name (optional, uses default if null).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>List of datasources.</returns>
        public async Task<List<PersesDatasource>> ListDatasourcesAsync(string? project = null, CancellationToken ct = default)
        {
            project ??= _config.Project;
            var url = $"/api/v1/projects/{project}/datasources";

            var response = await _httpClient.GetAsync(url, ct);
            await EnsureSuccessAsync(response);

            var result = await response.Content.ReadAsStringAsync(ct);
            var list = JsonSerializer.Deserialize<DatasourceList>(result, _jsonOptions);
            return list?.Items ?? new List<PersesDatasource>();
        }

        /// <summary>
        /// Deletes a datasource by name.
        /// </summary>
        /// <param name="name">The datasource name.</param>
        /// <param name="project">The project name (optional, uses default if null).</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task DeleteDatasourceAsync(string name, string? project = null, CancellationToken ct = default)
        {
            project ??= _config.Project;
            var url = $"/api/v1/projects/{project}/datasources/{name}";

            var response = await _httpClient.DeleteAsync(url, ct);
            await EnsureSuccessAsync(response);
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Creates a simple time series dashboard.
        /// </summary>
        /// <param name="name">Dashboard name.</param>
        /// <param name="title">Dashboard title.</param>
        /// <param name="queries">PromQL queries to display.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The created dashboard.</returns>
        public async Task<PersesDashboard> CreateSimpleTimeSeriesDashboardAsync(
            string name,
            string title,
            IEnumerable<string> queries,
            CancellationToken ct = default)
        {
            var dashboard = new PersesDashboard
            {
                Metadata = new DashboardMetadata
                {
                    Name = name,
                    Project = _config.Project
                },
                Spec = new DashboardSpec
                {
                    Display = new DashboardDisplay
                    {
                        Name = title
                    },
                    RefreshInterval = (int)_config.RefreshInterval.TotalSeconds
                }
            };

            var panelIndex = 0;
            var yPosition = 0;

            foreach (var query in queries)
            {
                var panelName = $"panel_{panelIndex}";

                var panel = new DashboardPanel
                {
                    Name = panelName,
                    Kind = "panel",
                    Spec = new PanelSpec
                    {
                        Display = new PanelDisplay
                        {
                            Name = $"Metric {panelIndex + 1}"
                        },
                        Queries = new List<PanelQuery>
                        {
                            new PanelQuery
                            {
                                Kind = "PromQL",
                                Spec = new QuerySpec
                                {
                                    Query = query,
                                    Datasource = new DatasourceReference
                                    {
                                        Kind = "PrometheusDataSource",
                                        Name = _config.DefaultDatasource
                                    }
                                }
                            }
                        },
                        Plugin = new PanelPlugin
                        {
                            Kind = "TimeSeriesChart"
                        }
                    }
                };

                dashboard.Spec.Panels.Add(panel);

                dashboard.Spec.Layouts.Add(new DashboardLayout
                {
                    Kind = "Grid",
                    Spec = new LayoutSpec
                    {
                        Items = new List<LayoutItem>
                        {
                            new LayoutItem
                            {
                                Panel = panelName,
                                X = 0,
                                Y = yPosition,
                                Width = 24,
                                Height = 6
                            }
                        }
                    }
                });

                panelIndex++;
                yPosition += 6;
            }

            return await CreateDashboardAsync(dashboard, ct);
        }

        /// <summary>
        /// Creates a Prometheus datasource.
        /// </summary>
        /// <param name="name">Datasource name.</param>
        /// <param name="url">Prometheus URL.</param>
        /// <param name="isDefault">Whether this is the default datasource.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The created datasource.</returns>
        public async Task<PersesDatasource> CreatePrometheusDatasourceAsync(
            string name,
            string url,
            bool isDefault = false,
            CancellationToken ct = default)
        {
            var datasource = new PersesDatasource
            {
                Metadata = new DatasourceMetadata
                {
                    Name = name,
                    Project = _config.Project
                },
                Spec = new DatasourceSpec
                {
                    IsDefault = isDefault,
                    Plugin = new DatasourcePlugin
                    {
                        Kind = "PrometheusDataSource",
                        Spec = new DatasourcePluginSpec
                        {
                            Url = url
                        }
                    }
                }
            };

            return await CreateDatasourceAsync(datasource, ct);
        }

        private async Task EnsureSuccessAsync(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var content = await response.Content.ReadAsStringAsync();
            PersesErrorResponse? error = null;

            try
            {
                error = JsonSerializer.Deserialize<PersesErrorResponse>(content, _jsonOptions);
            }
            catch
            {
                // Ignore deserialization errors
            }

            var errorMessage = error?.Error ?? content;
            throw new HttpRequestException(
                $"Perses API request failed with status {response.StatusCode}: {errorMessage}");
        }

        #endregion

        #region Message Handling

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "perses.dashboard.create":
                    await HandleDashboardCreateAsync(message);
                    break;
                case "perses.dashboard.update":
                    await HandleDashboardUpdateAsync(message);
                    break;
                case "perses.dashboard.get":
                    await HandleDashboardGetAsync(message);
                    break;
                case "perses.dashboard.list":
                    await HandleDashboardListAsync(message);
                    break;
                case "perses.dashboard.delete":
                    await HandleDashboardDeleteAsync(message);
                    break;
                case "perses.datasource.create":
                    await HandleDatasourceCreateAsync(message);
                    break;
                case "perses.datasource.update":
                    await HandleDatasourceUpdateAsync(message);
                    break;
                case "perses.datasource.get":
                    await HandleDatasourceGetAsync(message);
                    break;
                case "perses.datasource.list":
                    await HandleDatasourceListAsync(message);
                    break;
                case "perses.datasource.delete":
                    await HandleDatasourceDeleteAsync(message);
                    break;
                case "perses.status":
                    HandleStatus(message);
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }

        private async Task HandleDashboardCreateAsync(PluginMessage message)
        {
            try
            {
                var dashboardJson = GetString(message.Payload, "dashboard");
                if (string.IsNullOrEmpty(dashboardJson))
                {
                    message.Payload["error"] = "dashboard JSON required";
                    return;
                }

                var dashboard = JsonSerializer.Deserialize<PersesDashboard>(dashboardJson, _jsonOptions);
                if (dashboard == null)
                {
                    message.Payload["error"] = "invalid dashboard JSON";
                    return;
                }

                var result = await CreateDashboardAsync(dashboard);
                message.Payload["result"] = JsonSerializer.Serialize(result, _jsonOptions);
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
            }
        }

        private async Task HandleDashboardUpdateAsync(PluginMessage message)
        {
            try
            {
                var dashboardJson = GetString(message.Payload, "dashboard");
                if (string.IsNullOrEmpty(dashboardJson))
                {
                    message.Payload["error"] = "dashboard JSON required";
                    return;
                }

                var dashboard = JsonSerializer.Deserialize<PersesDashboard>(dashboardJson, _jsonOptions);
                if (dashboard == null)
                {
                    message.Payload["error"] = "invalid dashboard JSON";
                    return;
                }

                var result = await UpdateDashboardAsync(dashboard);
                message.Payload["result"] = JsonSerializer.Serialize(result, _jsonOptions);
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
            }
        }

        private async Task HandleDashboardGetAsync(PluginMessage message)
        {
            try
            {
                var name = GetString(message.Payload, "name");
                if (string.IsNullOrEmpty(name))
                {
                    message.Payload["error"] = "dashboard name required";
                    return;
                }

                var project = GetString(message.Payload, "project");
                var result = await GetDashboardAsync(name, project);

                if (result == null)
                {
                    message.Payload["error"] = "dashboard not found";
                }
                else
                {
                    message.Payload["result"] = JsonSerializer.Serialize(result, _jsonOptions);
                    message.Payload["success"] = true;
                }
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
            }
        }

        private async Task HandleDashboardListAsync(PluginMessage message)
        {
            try
            {
                var project = GetString(message.Payload, "project");
                var result = await ListDashboardsAsync(project);

                message.Payload["result"] = JsonSerializer.Serialize(result, _jsonOptions);
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
            }
        }

        private async Task HandleDashboardDeleteAsync(PluginMessage message)
        {
            try
            {
                var name = GetString(message.Payload, "name");
                if (string.IsNullOrEmpty(name))
                {
                    message.Payload["error"] = "dashboard name required";
                    return;
                }

                var project = GetString(message.Payload, "project");
                await DeleteDashboardAsync(name, project);

                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
            }
        }

        private async Task HandleDatasourceCreateAsync(PluginMessage message)
        {
            try
            {
                var datasourceJson = GetString(message.Payload, "datasource");
                if (string.IsNullOrEmpty(datasourceJson))
                {
                    message.Payload["error"] = "datasource JSON required";
                    return;
                }

                var datasource = JsonSerializer.Deserialize<PersesDatasource>(datasourceJson, _jsonOptions);
                if (datasource == null)
                {
                    message.Payload["error"] = "invalid datasource JSON";
                    return;
                }

                var result = await CreateDatasourceAsync(datasource);
                message.Payload["result"] = JsonSerializer.Serialize(result, _jsonOptions);
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
            }
        }

        private async Task HandleDatasourceUpdateAsync(PluginMessage message)
        {
            try
            {
                var datasourceJson = GetString(message.Payload, "datasource");
                if (string.IsNullOrEmpty(datasourceJson))
                {
                    message.Payload["error"] = "datasource JSON required";
                    return;
                }

                var datasource = JsonSerializer.Deserialize<PersesDatasource>(datasourceJson, _jsonOptions);
                if (datasource == null)
                {
                    message.Payload["error"] = "invalid datasource JSON";
                    return;
                }

                var result = await UpdateDatasourceAsync(datasource);
                message.Payload["result"] = JsonSerializer.Serialize(result, _jsonOptions);
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
            }
        }

        private async Task HandleDatasourceGetAsync(PluginMessage message)
        {
            try
            {
                var name = GetString(message.Payload, "name");
                if (string.IsNullOrEmpty(name))
                {
                    message.Payload["error"] = "datasource name required";
                    return;
                }

                var project = GetString(message.Payload, "project");
                var result = await GetDatasourceAsync(name, project);

                if (result == null)
                {
                    message.Payload["error"] = "datasource not found";
                }
                else
                {
                    message.Payload["result"] = JsonSerializer.Serialize(result, _jsonOptions);
                    message.Payload["success"] = true;
                }
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
            }
        }

        private async Task HandleDatasourceListAsync(PluginMessage message)
        {
            try
            {
                var project = GetString(message.Payload, "project");
                var result = await ListDatasourcesAsync(project);

                message.Payload["result"] = JsonSerializer.Serialize(result, _jsonOptions);
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
            }
        }

        private async Task HandleDatasourceDeleteAsync(PluginMessage message)
        {
            try
            {
                var name = GetString(message.Payload, "name");
                if (string.IsNullOrEmpty(name))
                {
                    message.Payload["error"] = "datasource name required";
                    return;
                }

                var project = GetString(message.Payload, "project");
                await DeleteDatasourceAsync(name, project);

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
                ["persesUrl"] = _config.PersesUrl,
                ["project"] = _config.Project,
                ["defaultDatasource"] = _config.DefaultDatasource,
                ["autoCreateDashboards"] = _config.AutoCreateDashboards,
                ["autoCreateDatasources"] = _config.AutoCreateDatasources
            };
        }

        private static string? GetString(Dictionary<string, object> payload, string key)
        {
            return payload.TryGetValue(key, out var val) && val is string s ? s : null;
        }

        #endregion

        #region Plugin Metadata

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "perses.dashboard.create", DisplayName = "Create Dashboard", Description = "Create a new dashboard" },
                new() { Name = "perses.dashboard.update", DisplayName = "Update Dashboard", Description = "Update an existing dashboard" },
                new() { Name = "perses.dashboard.get", DisplayName = "Get Dashboard", Description = "Get a dashboard by name" },
                new() { Name = "perses.dashboard.list", DisplayName = "List Dashboards", Description = "List all dashboards" },
                new() { Name = "perses.dashboard.delete", DisplayName = "Delete Dashboard", Description = "Delete a dashboard" },
                new() { Name = "perses.datasource.create", DisplayName = "Create Datasource", Description = "Create a new datasource" },
                new() { Name = "perses.datasource.update", DisplayName = "Update Datasource", Description = "Update an existing datasource" },
                new() { Name = "perses.datasource.get", DisplayName = "Get Datasource", Description = "Get a datasource by name" },
                new() { Name = "perses.datasource.list", DisplayName = "List Datasources", Description = "List all datasources" },
                new() { Name = "perses.datasource.delete", DisplayName = "Delete Datasource", Description = "Delete a datasource" },
                new() { Name = "perses.status", DisplayName = "Get Status", Description = "Get plugin status" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "Telemetry";
            metadata["PersesUrl"] = _config.PersesUrl;
            metadata["Project"] = _config.Project;
            metadata["SupportsDashboards"] = true;
            metadata["SupportsDatasources"] = true;
            metadata["SupportsPrometheus"] = true;
            metadata["AutoCreateDashboards"] = _config.AutoCreateDashboards;
            metadata["AutoCreateDatasources"] = _config.AutoCreateDatasources;
            return metadata;
        }

        #endregion

        /// <summary>
        /// Disposes the plugin and releases resources.
        /// </summary>
        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
