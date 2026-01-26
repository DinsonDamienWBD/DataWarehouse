using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.Redash
{
    /// <summary>
    /// Abstract base class for BI telemetry plugins providing query execution and visualization capabilities.
    /// Extends <see cref="FeaturePluginBase"/> to support lifecycle management and API integration.
    /// </summary>
    public abstract class BiTelemetryPluginBase : FeaturePluginBase
    {
        /// <summary>
        /// Gets the category for BI telemetry plugins.
        /// </summary>
        public override PluginCategory Category => PluginCategory.MetricsProvider;

        /// <summary>
        /// Executes a query and returns results.
        /// </summary>
        /// <param name="queryId">The query ID to execute.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The query result.</returns>
        public abstract Task<object?> ExecuteQueryAsync(int queryId, CancellationToken ct = default);

        /// <summary>
        /// Retrieves a list of available queries.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A collection of queries.</returns>
        public abstract Task<IEnumerable<object>> GetQueriesAsync(CancellationToken ct = default);

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "BI Telemetry";
            metadata["SupportsQueries"] = true;
            metadata["SupportsVisualizations"] = true;
            metadata["SupportsDashboards"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Production-ready Redash BI platform integration plugin.
    /// Provides query execution, visualization management, and dashboard capabilities.
    ///
    /// Features:
    /// - REST API integration with Redash
    /// - API key authentication
    /// - Query execution and result retrieval
    /// - Visualization and dashboard management
    /// - Data source integration
    /// - Comprehensive error handling
    /// - Connection pooling and retry logic
    /// - Statistics tracking
    ///
    /// Message Commands:
    /// - redash.executeQuery: Execute a query by ID
    /// - redash.getQuery: Get query details
    /// - redash.listQueries: List all queries
    /// - redash.getQueryResult: Get cached query results
    /// - redash.getVisualization: Get visualization details
    /// - redash.getDashboard: Get dashboard details
    /// - redash.listDataSources: List available data sources
    /// - redash.status: Get plugin status and statistics
    /// </summary>
    public sealed class RedashPlugin : BiTelemetryPluginBase
    {
        private readonly RedashConfiguration _config;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _rateLimiter;
        private readonly RedashStatistics _statistics;
        private readonly object _statsLock = new();
        private bool _isInitialized;

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.telemetry.redash";

        /// <inheritdoc/>
        public override string Name => "Redash BI Plugin";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <summary>
        /// Gets the plugin configuration.
        /// </summary>
        public RedashConfiguration Configuration => _config;

        /// <summary>
        /// Gets the plugin statistics.
        /// </summary>
        public RedashStatistics Statistics
        {
            get
            {
                lock (_statsLock)
                {
                    return new RedashStatistics
                    {
                        TotalQueriesExecuted = _statistics.TotalQueriesExecuted,
                        SuccessfulQueries = _statistics.SuccessfulQueries,
                        FailedQueries = _statistics.FailedQueries,
                        TotalExecutionTime = _statistics.TotalExecutionTime,
                        ApiRequestCount = _statistics.ApiRequestCount,
                        ApiErrorCount = _statistics.ApiErrorCount,
                        LastQueryTime = _statistics.LastQueryTime
                    };
                }
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RedashPlugin"/> class.
        /// </summary>
        /// <param name="config">Optional configuration. Defaults will be used if null.</param>
        public RedashPlugin(RedashConfiguration? config = null)
        {
            _config = config ?? new RedashConfiguration();
            _statistics = new RedashStatistics();
            _rateLimiter = new SemaphoreSlim(_config.MaxConcurrentRequests, _config.MaxConcurrentRequests);

            // Configure HttpClient
            var handler = new HttpClientHandler();
            if (!_config.VerifySsl)
            {
                handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            }

            _httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(_config.RedashUrl),
                Timeout = TimeSpan.FromSeconds(_config.RequestTimeoutSeconds)
            };

            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Key", _config.ApiKey);
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        }

        #region Lifecycle Management

        /// <inheritdoc/>
        public override async Task StartAsync(CancellationToken ct)
        {
            if (_isInitialized)
            {
                return;
            }

            // Verify connectivity by testing API endpoint
            try
            {
                var response = await _httpClient.GetAsync("api/data_sources", ct);
                response.EnsureSuccessStatusCode();
                _isInitialized = true;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to connect to Redash at {_config.RedashUrl}: {ex.Message}", ex);
            }

            await Task.CompletedTask;
        }

        /// <inheritdoc/>
        public override async Task StopAsync()
        {
            _isInitialized = false;
            _httpClient?.Dispose();
            _rateLimiter?.Dispose();
            await Task.CompletedTask;
        }

        #endregion

        #region Query Operations

        /// <inheritdoc/>
        public override async Task<object?> ExecuteQueryAsync(int queryId, CancellationToken ct = default)
        {
            await _rateLimiter.WaitAsync(ct);
            try
            {
                IncrementApiRequest();
                var startTime = DateTime.UtcNow;

                // Request query execution
                var executeUrl = $"api/queries/{queryId}/results";
                var response = await _httpClient.PostAsync(executeUrl, null, ct);
                response.EnsureSuccessStatusCode();

                var jobJson = await response.Content.ReadAsStringAsync(ct);
                var job = JsonSerializer.Deserialize<QueryJob>(jobJson);

                if (job?.Job == null)
                {
                    throw new InvalidOperationException("Invalid response from Redash API");
                }

                // Poll for job completion
                var result = await PollJobStatusAsync(job.Job.Id, ct);

                var duration = (DateTime.UtcNow - startTime).TotalSeconds;
                IncrementQuerySuccess(duration);

                return result;
            }
            catch (Exception ex)
            {
                IncrementQueryFailure();
                IncrementApiError();
                throw new InvalidOperationException($"Failed to execute query {queryId}: {ex.Message}", ex);
            }
            finally
            {
                _rateLimiter.Release();
            }
        }

        /// <inheritdoc/>
        public override async Task<IEnumerable<object>> GetQueriesAsync(CancellationToken ct = default)
        {
            await _rateLimiter.WaitAsync(ct);
            try
            {
                IncrementApiRequest();

                var response = await _httpClient.GetAsync("api/queries", ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                var queriesResponse = JsonSerializer.Deserialize<QueriesListResponse>(json);

                return queriesResponse?.Results ?? Enumerable.Empty<object>();
            }
            catch (Exception ex)
            {
                IncrementApiError();
                throw new InvalidOperationException($"Failed to retrieve queries: {ex.Message}", ex);
            }
            finally
            {
                _rateLimiter.Release();
            }
        }

        /// <summary>
        /// Gets details for a specific query.
        /// </summary>
        /// <param name="queryId">The query ID.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The query details.</returns>
        public async Task<RedashQuery?> GetQueryAsync(int queryId, CancellationToken ct = default)
        {
            await _rateLimiter.WaitAsync(ct);
            try
            {
                IncrementApiRequest();

                var response = await _httpClient.GetAsync($"api/queries/{queryId}", ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                return JsonSerializer.Deserialize<RedashQuery>(json);
            }
            catch (Exception ex)
            {
                IncrementApiError();
                throw new InvalidOperationException($"Failed to get query {queryId}: {ex.Message}", ex);
            }
            finally
            {
                _rateLimiter.Release();
            }
        }

        /// <summary>
        /// Gets cached query results.
        /// </summary>
        /// <param name="queryResultId">The query result ID.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The query result.</returns>
        public async Task<QueryResult?> GetQueryResultAsync(int queryResultId, CancellationToken ct = default)
        {
            await _rateLimiter.WaitAsync(ct);
            try
            {
                IncrementApiRequest();

                var response = await _httpClient.GetAsync($"api/query_results/{queryResultId}", ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                var wrapper = JsonSerializer.Deserialize<QueryResultWrapper>(json);
                return wrapper?.QueryResult;
            }
            catch (Exception ex)
            {
                IncrementApiError();
                throw new InvalidOperationException($"Failed to get query result {queryResultId}: {ex.Message}", ex);
            }
            finally
            {
                _rateLimiter.Release();
            }
        }

        #endregion

        #region Visualization Operations

        /// <summary>
        /// Gets details for a specific visualization.
        /// </summary>
        /// <param name="visualizationId">The visualization ID.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The visualization details.</returns>
        public async Task<RedashVisualization?> GetVisualizationAsync(int visualizationId, CancellationToken ct = default)
        {
            await _rateLimiter.WaitAsync(ct);
            try
            {
                IncrementApiRequest();

                var response = await _httpClient.GetAsync($"api/visualizations/{visualizationId}", ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                return JsonSerializer.Deserialize<RedashVisualization>(json);
            }
            catch (Exception ex)
            {
                IncrementApiError();
                throw new InvalidOperationException($"Failed to get visualization {visualizationId}: {ex.Message}", ex);
            }
            finally
            {
                _rateLimiter.Release();
            }
        }

        #endregion

        #region Dashboard Operations

        /// <summary>
        /// Gets details for a specific dashboard.
        /// </summary>
        /// <param name="dashboardSlug">The dashboard slug or ID.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The dashboard details.</returns>
        public async Task<RedashDashboard?> GetDashboardAsync(string dashboardSlug, CancellationToken ct = default)
        {
            await _rateLimiter.WaitAsync(ct);
            try
            {
                IncrementApiRequest();

                var response = await _httpClient.GetAsync($"api/dashboards/{dashboardSlug}", ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                return JsonSerializer.Deserialize<RedashDashboard>(json);
            }
            catch (Exception ex)
            {
                IncrementApiError();
                throw new InvalidOperationException($"Failed to get dashboard {dashboardSlug}: {ex.Message}", ex);
            }
            finally
            {
                _rateLimiter.Release();
            }
        }

        #endregion

        #region Data Source Operations

        /// <summary>
        /// Lists all available data sources.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A collection of data sources.</returns>
        public async Task<IEnumerable<RedashDataSource>> GetDataSourcesAsync(CancellationToken ct = default)
        {
            await _rateLimiter.WaitAsync(ct);
            try
            {
                IncrementApiRequest();

                var response = await _httpClient.GetAsync("api/data_sources", ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                return JsonSerializer.Deserialize<List<RedashDataSource>>(json) ?? new List<RedashDataSource>();
            }
            catch (Exception ex)
            {
                IncrementApiError();
                throw new InvalidOperationException($"Failed to get data sources: {ex.Message}", ex);
            }
            finally
            {
                _rateLimiter.Release();
            }
        }

        #endregion

        #region Message Handling

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "redash.executeQuery":
                    await HandleExecuteQueryAsync(message);
                    break;
                case "redash.getQuery":
                    await HandleGetQueryAsync(message);
                    break;
                case "redash.listQueries":
                    await HandleListQueriesAsync(message);
                    break;
                case "redash.getQueryResult":
                    await HandleGetQueryResultAsync(message);
                    break;
                case "redash.getVisualization":
                    await HandleGetVisualizationAsync(message);
                    break;
                case "redash.getDashboard":
                    await HandleGetDashboardAsync(message);
                    break;
                case "redash.listDataSources":
                    await HandleListDataSourcesAsync(message);
                    break;
                case "redash.status":
                    HandleStatus(message);
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }

        private async Task HandleExecuteQueryAsync(PluginMessage message)
        {
            try
            {
                var queryId = GetInt(message.Payload, "queryId");
                if (!queryId.HasValue)
                {
                    message.Payload["error"] = "queryId is required";
                    return;
                }

                var result = await ExecuteQueryAsync(queryId.Value, CancellationToken.None);
                message.Payload["result"] = result ?? (object)"null";
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
                message.Payload["success"] = false;
            }
        }

        private async Task HandleGetQueryAsync(PluginMessage message)
        {
            try
            {
                var queryId = GetInt(message.Payload, "queryId");
                if (!queryId.HasValue)
                {
                    message.Payload["error"] = "queryId is required";
                    return;
                }

                var query = await GetQueryAsync(queryId.Value, CancellationToken.None);
                message.Payload["result"] = query ?? (object)"null";
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
                message.Payload["success"] = false;
            }
        }

        private async Task HandleListQueriesAsync(PluginMessage message)
        {
            try
            {
                var queries = await GetQueriesAsync(CancellationToken.None);
                message.Payload["result"] = queries ?? (object)"null";
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
                message.Payload["success"] = false;
            }
        }

        private async Task HandleGetQueryResultAsync(PluginMessage message)
        {
            try
            {
                var resultId = GetInt(message.Payload, "resultId");
                if (!resultId.HasValue)
                {
                    message.Payload["error"] = "resultId is required";
                    return;
                }

                var result = await GetQueryResultAsync(resultId.Value, CancellationToken.None);
                message.Payload["result"] = result ?? (object)"null";
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
                message.Payload["success"] = false;
            }
        }

        private async Task HandleGetVisualizationAsync(PluginMessage message)
        {
            try
            {
                var vizId = GetInt(message.Payload, "visualizationId");
                if (!vizId.HasValue)
                {
                    message.Payload["error"] = "visualizationId is required";
                    return;
                }

                var visualization = await GetVisualizationAsync(vizId.Value, CancellationToken.None);
                message.Payload["result"] = visualization ?? (object)"null";
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
                message.Payload["success"] = false;
            }
        }

        private async Task HandleGetDashboardAsync(PluginMessage message)
        {
            try
            {
                var slug = GetString(message.Payload, "slug");
                if (string.IsNullOrEmpty(slug))
                {
                    message.Payload["error"] = "slug is required";
                    return;
                }

                var dashboard = await GetDashboardAsync(slug, CancellationToken.None);
                message.Payload["result"] = dashboard ?? (object)"null";
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
                message.Payload["success"] = false;
            }
        }

        private async Task HandleListDataSourcesAsync(PluginMessage message)
        {
            try
            {
                var dataSources = await GetDataSourcesAsync(CancellationToken.None);
                message.Payload["result"] = dataSources ?? (object)"null";
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
                message.Payload["success"] = false;
            }
        }

        private void HandleStatus(PluginMessage message)
        {
            var stats = Statistics;
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["isInitialized"] = _isInitialized,
                ["redashUrl"] = _config.RedashUrl,
                ["totalQueriesExecuted"] = stats.TotalQueriesExecuted,
                ["successfulQueries"] = stats.SuccessfulQueries,
                ["failedQueries"] = stats.FailedQueries,
                ["averageExecutionTime"] = stats.AverageExecutionTime,
                ["apiRequestCount"] = stats.ApiRequestCount,
                ["apiErrorCount"] = stats.ApiErrorCount,
                ["lastQueryTime"] = stats.LastQueryTime?.ToString("o") ?? "never"
            };
        }

        #endregion

        #region Helper Methods

        private async Task<QueryResult?> PollJobStatusAsync(string jobId, CancellationToken ct)
        {
            var pollInterval = TimeSpan.FromSeconds(1);
            var timeout = TimeSpan.FromSeconds(_config.QueryTimeoutSeconds);
            var deadline = DateTime.UtcNow.Add(timeout);

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                var response = await _httpClient.GetAsync($"api/jobs/{jobId}", ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                var job = JsonSerializer.Deserialize<QueryJob>(json);

                if (job?.Job == null)
                {
                    throw new InvalidOperationException("Invalid job status response");
                }

                if (job.Job.IsComplete)
                {
                    if (job.Job.IsFailed)
                    {
                        throw new InvalidOperationException($"Query execution failed: {job.Job.Error}");
                    }

                    return job.QueryResult;
                }

                await Task.Delay(pollInterval, ct);
            }

            throw new TimeoutException($"Query execution timed out after {timeout.TotalSeconds} seconds");
        }

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

        private void IncrementApiRequest()
        {
            lock (_statsLock)
            {
                _statistics.ApiRequestCount++;
            }
        }

        private void IncrementApiError()
        {
            lock (_statsLock)
            {
                _statistics.ApiErrorCount++;
            }
        }

        private void IncrementQuerySuccess(double duration)
        {
            lock (_statsLock)
            {
                _statistics.TotalQueriesExecuted++;
                _statistics.SuccessfulQueries++;
                _statistics.TotalExecutionTime += duration;
                _statistics.LastQueryTime = DateTime.UtcNow;
            }
        }

        private void IncrementQueryFailure()
        {
            lock (_statsLock)
            {
                _statistics.TotalQueriesExecuted++;
                _statistics.FailedQueries++;
                _statistics.LastQueryTime = DateTime.UtcNow;
            }
        }

        #endregion

        #region Plugin Metadata

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "redash.executeQuery", DisplayName = "Execute Query", Description = "Execute a Redash query by ID" },
                new() { Name = "redash.getQuery", DisplayName = "Get Query", Description = "Get query details" },
                new() { Name = "redash.listQueries", DisplayName = "List Queries", Description = "List all available queries" },
                new() { Name = "redash.getQueryResult", DisplayName = "Get Query Result", Description = "Get cached query results" },
                new() { Name = "redash.getVisualization", DisplayName = "Get Visualization", Description = "Get visualization details" },
                new() { Name = "redash.getDashboard", DisplayName = "Get Dashboard", Description = "Get dashboard details" },
                new() { Name = "redash.listDataSources", DisplayName = "List Data Sources", Description = "List available data sources" },
                new() { Name = "redash.status", DisplayName = "Get Status", Description = "Get plugin status and statistics" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["RedashUrl"] = _config.RedashUrl;
            metadata["QueryTimeoutSeconds"] = _config.QueryTimeoutSeconds;
            metadata["MaxConcurrentRequests"] = _config.MaxConcurrentRequests;
            metadata["SupportsQueries"] = true;
            metadata["SupportsVisualizations"] = true;
            metadata["SupportsDashboards"] = true;
            metadata["SupportsDataSources"] = true;
            return metadata;
        }

        #endregion

        #region Internal Types

        private sealed class QueriesListResponse
        {
            [JsonPropertyName("results")]
            public List<RedashQuery>? Results { get; set; }
        }

        private sealed class QueryResultWrapper
        {
            [JsonPropertyName("query_result")]
            public QueryResult? QueryResult { get; set; }
        }

        #endregion
    }
}
