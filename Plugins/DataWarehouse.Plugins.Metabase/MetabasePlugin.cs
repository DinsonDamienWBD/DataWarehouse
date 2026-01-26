using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.Metabase
{
    /// <summary>
    /// Abstract base class for BI integration plugins providing analytics and reporting capabilities.
    /// Extends <see cref="FeaturePluginBase"/> to support lifecycle management and HTTP endpoints.
    /// </summary>
    public abstract class BIPluginBase : FeaturePluginBase
    {
        /// <summary>
        /// Gets the category for BI plugins.
        /// </summary>
        public override PluginCategory Category => PluginCategory.MetricsProvider;

        /// <summary>
        /// Executes a query or question.
        /// </summary>
        /// <param name="queryId">The query identifier.</param>
        /// <param name="parameters">Optional query parameters.</param>
        /// <returns>The query result.</returns>
        public abstract Task<object> ExecuteQueryAsync(string queryId, Dictionary<string, object>? parameters = null);

        /// <summary>
        /// Lists available queries or questions.
        /// </summary>
        /// <returns>The list of available queries.</returns>
        public abstract Task<IEnumerable<object>> ListQueriesAsync();

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "BI";
            metadata["SupportsQueries"] = true;
            metadata["SupportsDashboards"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Production-ready Metabase BI integration plugin with REST API support.
    /// Provides question execution, dashboard management, and database connection capabilities.
    ///
    /// Features:
    /// - Session-based authentication with automatic refresh
    /// - Question (saved query) execution with parameters
    /// - Dashboard listing and retrieval
    /// - Database connection management
    /// - Collection browsing
    /// - Automatic retry with exponential backoff
    /// - Connection pooling and keep-alive
    /// - SSL certificate verification control
    ///
    /// Message Commands:
    /// - metabase.execute_question: Execute a saved question
    /// - metabase.list_questions: List all questions
    /// - metabase.get_question: Get question details
    /// - metabase.list_dashboards: List all dashboards
    /// - metabase.get_dashboard: Get dashboard details
    /// - metabase.list_databases: List database connections
    /// - metabase.list_collections: List collections
    /// - metabase.status: Get plugin status
    /// </summary>
    public sealed class MetabasePlugin : BIPluginBase
    {
        private readonly MetabaseConfiguration _config;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _authLock;
        private readonly ConcurrentDictionary<string, QueryResult> _queryCache;
        private string? _sessionToken;
        private DateTime _sessionExpiresAt;
        private readonly Timer _sessionRefreshTimer;
        private bool _isRunning;
        private readonly object _stateLock = new();
        private long _requestCount;
        private long _errorCount;
        private long _cacheHits;

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.telemetry.metabase";

        /// <inheritdoc/>
        public override string Name => "Metabase BI Plugin";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <summary>
        /// Gets the plugin configuration.
        /// </summary>
        public MetabaseConfiguration Configuration => _config;

        /// <summary>
        /// Gets whether the plugin is running and authenticated.
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Gets the total number of requests.
        /// </summary>
        public long RequestCount => Interlocked.Read(ref _requestCount);

        /// <summary>
        /// Gets the total number of errors.
        /// </summary>
        public long ErrorCount => Interlocked.Read(ref _errorCount);

        /// <summary>
        /// Gets the total number of cache hits.
        /// </summary>
        public long CacheHits => Interlocked.Read(ref _cacheHits);

        /// <summary>
        /// Initializes a new instance of the <see cref="MetabasePlugin"/> class.
        /// </summary>
        /// <param name="config">Optional configuration. Defaults will be used if null.</param>
        public MetabasePlugin(MetabaseConfiguration? config = null)
        {
            _config = config ?? new MetabaseConfiguration();
            _authLock = new SemaphoreSlim(1, 1);
            _queryCache = new ConcurrentDictionary<string, QueryResult>(StringComparer.Ordinal);

            // Configure HttpClient
            var handler = new HttpClientHandler();
            if (!_config.VerifySsl)
            {
                handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            }

            _httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(_config.MetabaseUrl),
                Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds)
            };

            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "DataWarehouse-Metabase-Plugin/1.0");

            _sessionExpiresAt = DateTime.MinValue;

            // Session refresh timer
            _sessionRefreshTimer = new Timer(
                _ => _ = RefreshSessionAsync(),
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
        }

        #region Lifecycle Management

        /// <inheritdoc/>
        public override async Task StartAsync(CancellationToken ct)
        {
            lock (_stateLock)
            {
                if (_isRunning)
                {
                    return;
                }

                _isRunning = true;
            }

            // Authenticate
            await AuthenticateAsync(ct);

            // Start session refresh timer
            var refreshInterval = TimeSpan.FromMinutes(_config.SessionRefreshIntervalMinutes);
            _sessionRefreshTimer.Change(refreshInterval, refreshInterval);
        }

        /// <inheritdoc/>
        public override async Task StopAsync()
        {
            lock (_stateLock)
            {
                if (!_isRunning)
                {
                    return;
                }

                _isRunning = false;
            }

            // Stop refresh timer
            await _sessionRefreshTimer.ChangeAsync(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            // Clear session token
            _sessionToken = null;
            _sessionExpiresAt = DateTime.MinValue;

            // Clear cache
            _queryCache.Clear();
        }

        #endregion

        #region Authentication

        private async Task AuthenticateAsync(CancellationToken ct = default)
        {
            await _authLock.WaitAsync(ct);
            try
            {
                // Check if session is still valid
                if (!string.IsNullOrEmpty(_sessionToken) && DateTime.UtcNow < _sessionExpiresAt)
                {
                    return;
                }

                var loginRequest = new LoginRequest
                {
                    Username = _config.Username,
                    Password = _config.Password
                };

                var response = await _httpClient.PostAsJsonAsync("/api/session", loginRequest, ct);
                response.EnsureSuccessStatusCode();

                var loginResponse = await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: ct);
                if (loginResponse?.Id == null)
                {
                    throw new InvalidOperationException("Failed to obtain session token from Metabase");
                }

                _sessionToken = loginResponse.Id;
                // Metabase sessions typically last 14 days, but we refresh more frequently
                _sessionExpiresAt = DateTime.UtcNow.AddMinutes(_config.SessionRefreshIntervalMinutes * 5);

                // Update HttpClient headers
                _httpClient.DefaultRequestHeaders.Remove("X-Metabase-Session");
                _httpClient.DefaultRequestHeaders.Add("X-Metabase-Session", _sessionToken);
            }
            finally
            {
                _authLock.Release();
            }
        }

        private async Task RefreshSessionAsync()
        {
            if (!_isRunning)
            {
                return;
            }

            try
            {
                await AuthenticateAsync();
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errorCount);
                Console.Error.WriteLine($"[Metabase] Session refresh failed: {ex.Message}");
            }
        }

        #endregion

        #region BIPluginBase Implementation

        /// <inheritdoc/>
        public override async Task<object> ExecuteQueryAsync(string queryId, Dictionary<string, object>? parameters = null)
        {
            return await ExecuteQuestionAsync(int.Parse(queryId), parameters);
        }

        /// <inheritdoc/>
        public override async Task<IEnumerable<object>> ListQueriesAsync()
        {
            return await ListQuestionsAsync();
        }

        #endregion

        #region Questions

        /// <summary>
        /// Lists all questions accessible to the authenticated user.
        /// </summary>
        /// <returns>The list of questions.</returns>
        public async Task<List<MetabaseQuestion>> ListQuestionsAsync()
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                Interlocked.Increment(ref _requestCount);

                var response = await _httpClient.GetAsync("/api/card");
                await EnsureSuccessAsync(response);

                var questions = await response.Content.ReadFromJsonAsync<List<MetabaseQuestion>>();
                return questions ?? new List<MetabaseQuestion>();
            });
        }

        /// <summary>
        /// Gets a question by ID.
        /// </summary>
        /// <param name="questionId">The question ID.</param>
        /// <returns>The question details.</returns>
        public async Task<MetabaseQuestion?> GetQuestionAsync(int questionId)
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                Interlocked.Increment(ref _requestCount);

                var response = await _httpClient.GetAsync($"/api/card/{questionId}");
                await EnsureSuccessAsync(response);

                return await response.Content.ReadFromJsonAsync<MetabaseQuestion>();
            });
        }

        /// <summary>
        /// Executes a question and returns the results.
        /// </summary>
        /// <param name="questionId">The question ID.</param>
        /// <param name="parameters">Optional query parameters.</param>
        /// <returns>The query result.</returns>
        public async Task<QueryResult?> ExecuteQuestionAsync(int questionId, Dictionary<string, object>? parameters = null)
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                Interlocked.Increment(ref _requestCount);

                var url = $"/api/card/{questionId}/query";

                HttpResponseMessage response;
                if (parameters != null && parameters.Count > 0)
                {
                    var queryParams = new QueryRequest
                    {
                        Parameters = parameters.Select(p => new QueryParameter
                        {
                            Type = "category",
                            Value = p.Value
                        }).ToList()
                    };
                    response = await _httpClient.PostAsJsonAsync(url, queryParams);
                }
                else
                {
                    response = await _httpClient.PostAsync(url, null);
                }

                await EnsureSuccessAsync(response);

                return await response.Content.ReadFromJsonAsync<QueryResult>();
            });
        }

        #endregion

        #region Dashboards

        /// <summary>
        /// Lists all dashboards accessible to the authenticated user.
        /// </summary>
        /// <returns>The list of dashboards.</returns>
        public async Task<List<MetabaseDashboard>> ListDashboardsAsync()
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                Interlocked.Increment(ref _requestCount);

                var response = await _httpClient.GetAsync("/api/dashboard");
                await EnsureSuccessAsync(response);

                var dashboards = await response.Content.ReadFromJsonAsync<List<MetabaseDashboard>>();
                return dashboards ?? new List<MetabaseDashboard>();
            });
        }

        /// <summary>
        /// Gets a dashboard by ID with all its cards.
        /// </summary>
        /// <param name="dashboardId">The dashboard ID.</param>
        /// <returns>The dashboard details.</returns>
        public async Task<MetabaseDashboard?> GetDashboardAsync(int dashboardId)
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                Interlocked.Increment(ref _requestCount);

                var response = await _httpClient.GetAsync($"/api/dashboard/{dashboardId}");
                await EnsureSuccessAsync(response);

                return await response.Content.ReadFromJsonAsync<MetabaseDashboard>();
            });
        }

        #endregion

        #region Databases

        /// <summary>
        /// Lists all database connections.
        /// </summary>
        /// <returns>The list of databases.</returns>
        public async Task<List<MetabaseDatabase>> ListDatabasesAsync()
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                Interlocked.Increment(ref _requestCount);

                var response = await _httpClient.GetAsync("/api/database");
                await EnsureSuccessAsync(response);

                var databases = await response.Content.ReadFromJsonAsync<List<MetabaseDatabase>>();
                return databases ?? new List<MetabaseDatabase>();
            });
        }

        /// <summary>
        /// Gets a database by ID.
        /// </summary>
        /// <param name="databaseId">The database ID.</param>
        /// <returns>The database details.</returns>
        public async Task<MetabaseDatabase?> GetDatabaseAsync(int databaseId)
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                Interlocked.Increment(ref _requestCount);

                var response = await _httpClient.GetAsync($"/api/database/{databaseId}");
                await EnsureSuccessAsync(response);

                return await response.Content.ReadFromJsonAsync<MetabaseDatabase>();
            });
        }

        #endregion

        #region Collections

        /// <summary>
        /// Lists all collections.
        /// </summary>
        /// <returns>The list of collections.</returns>
        public async Task<List<MetabaseCollection>> ListCollectionsAsync()
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                Interlocked.Increment(ref _requestCount);

                var response = await _httpClient.GetAsync("/api/collection");
                await EnsureSuccessAsync(response);

                var collections = await response.Content.ReadFromJsonAsync<List<MetabaseCollection>>();
                return collections ?? new List<MetabaseCollection>();
            });
        }

        #endregion

        #region Message Handling

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "metabase.execute_question":
                    await HandleExecuteQuestionAsync(message);
                    break;
                case "metabase.list_questions":
                    await HandleListQuestionsAsync(message);
                    break;
                case "metabase.get_question":
                    await HandleGetQuestionAsync(message);
                    break;
                case "metabase.list_dashboards":
                    await HandleListDashboardsAsync(message);
                    break;
                case "metabase.get_dashboard":
                    await HandleGetDashboardAsync(message);
                    break;
                case "metabase.list_databases":
                    await HandleListDatabasesAsync(message);
                    break;
                case "metabase.list_collections":
                    await HandleListCollectionsAsync(message);
                    break;
                case "metabase.status":
                    HandleStatus(message);
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }

        private async Task HandleExecuteQuestionAsync(PluginMessage message)
        {
            try
            {
                var questionId = GetInt(message.Payload, "questionId");
                if (!questionId.HasValue)
                {
                    message.Payload["error"] = "questionId required";
                    return;
                }

                var parameters = GetDictionary(message.Payload, "parameters");
                var result = await ExecuteQuestionAsync(questionId.Value, parameters);

                message.Payload["result"] = result;
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errorCount);
                message.Payload["error"] = ex.Message;
            }
        }

        private async Task HandleListQuestionsAsync(PluginMessage message)
        {
            try
            {
                var questions = await ListQuestionsAsync();
                message.Payload["result"] = questions;
                message.Payload["count"] = questions.Count;
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errorCount);
                message.Payload["error"] = ex.Message;
            }
        }

        private async Task HandleGetQuestionAsync(PluginMessage message)
        {
            try
            {
                var questionId = GetInt(message.Payload, "questionId");
                if (!questionId.HasValue)
                {
                    message.Payload["error"] = "questionId required";
                    return;
                }

                var question = await GetQuestionAsync(questionId.Value);
                message.Payload["result"] = question;
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errorCount);
                message.Payload["error"] = ex.Message;
            }
        }

        private async Task HandleListDashboardsAsync(PluginMessage message)
        {
            try
            {
                var dashboards = await ListDashboardsAsync();
                message.Payload["result"] = dashboards;
                message.Payload["count"] = dashboards.Count;
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errorCount);
                message.Payload["error"] = ex.Message;
            }
        }

        private async Task HandleGetDashboardAsync(PluginMessage message)
        {
            try
            {
                var dashboardId = GetInt(message.Payload, "dashboardId");
                if (!dashboardId.HasValue)
                {
                    message.Payload["error"] = "dashboardId required";
                    return;
                }

                var dashboard = await GetDashboardAsync(dashboardId.Value);
                message.Payload["result"] = dashboard;
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errorCount);
                message.Payload["error"] = ex.Message;
            }
        }

        private async Task HandleListDatabasesAsync(PluginMessage message)
        {
            try
            {
                var databases = await ListDatabasesAsync();
                message.Payload["result"] = databases;
                message.Payload["count"] = databases.Count;
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errorCount);
                message.Payload["error"] = ex.Message;
            }
        }

        private async Task HandleListCollectionsAsync(PluginMessage message)
        {
            try
            {
                var collections = await ListCollectionsAsync();
                message.Payload["result"] = collections;
                message.Payload["count"] = collections.Count;
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errorCount);
                message.Payload["error"] = ex.Message;
            }
        }

        private void HandleStatus(PluginMessage message)
        {
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["isRunning"] = _isRunning,
                ["metabaseUrl"] = _config.MetabaseUrl,
                ["authenticated"] = !string.IsNullOrEmpty(_sessionToken),
                ["sessionExpiresAt"] = _sessionExpiresAt,
                ["requestCount"] = RequestCount,
                ["errorCount"] = ErrorCount,
                ["cacheHits"] = CacheHits,
                ["cacheSize"] = _queryCache.Count
            };
        }

        #endregion

        #region Helpers

        private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action)
        {
            var attempts = 0;
            Exception? lastException = null;

            while (attempts < _config.MaxRetryAttempts)
            {
                try
                {
                    // Ensure authenticated before each request
                    await AuthenticateAsync();
                    return await action();
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // Force re-authentication
                    _sessionToken = null;
                    _sessionExpiresAt = DateTime.MinValue;
                    lastException = ex;
                    attempts++;

                    if (attempts < _config.MaxRetryAttempts)
                    {
                        await Task.Delay(_config.RetryDelayMs * attempts);
                    }
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    attempts++;

                    if (attempts < _config.MaxRetryAttempts)
                    {
                        await Task.Delay(_config.RetryDelayMs * attempts);
                    }
                }
            }

            throw new InvalidOperationException(
                $"Failed after {_config.MaxRetryAttempts} attempts", lastException);
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var content = await response.Content.ReadAsStringAsync();
            ErrorResponse? error = null;

            try
            {
                error = JsonSerializer.Deserialize<ErrorResponse>(content);
            }
            catch
            {
                // Ignore deserialization errors
            }

            var errorMessage = error?.Message ?? content;
            throw new HttpRequestException(
                $"Metabase API error: {errorMessage}",
                null,
                response.StatusCode);
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

        private static Dictionary<string, object>? GetDictionary(Dictionary<string, object> payload, string key)
        {
            if (payload.TryGetValue(key, out var val) && val is Dictionary<string, object> dict)
            {
                return dict;
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
                new() { Name = "metabase.execute_question", DisplayName = "Execute Question", Description = "Execute a saved question with optional parameters" },
                new() { Name = "metabase.list_questions", DisplayName = "List Questions", Description = "List all accessible questions" },
                new() { Name = "metabase.get_question", DisplayName = "Get Question", Description = "Get question details by ID" },
                new() { Name = "metabase.list_dashboards", DisplayName = "List Dashboards", Description = "List all accessible dashboards" },
                new() { Name = "metabase.get_dashboard", DisplayName = "Get Dashboard", Description = "Get dashboard details by ID" },
                new() { Name = "metabase.list_databases", DisplayName = "List Databases", Description = "List all database connections" },
                new() { Name = "metabase.list_collections", DisplayName = "List Collections", Description = "List all collections" },
                new() { Name = "metabase.status", DisplayName = "Get Status", Description = "Get plugin status" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["MetabaseUrl"] = _config.MetabaseUrl;
            metadata["TimeoutSeconds"] = _config.TimeoutSeconds;
            metadata["SupportsQuestions"] = true;
            metadata["SupportsDashboards"] = true;
            metadata["SupportsDatabases"] = true;
            metadata["SupportsCollections"] = true;
            metadata["SupportsParameters"] = true;
            metadata["SessionRefreshMinutes"] = _config.SessionRefreshIntervalMinutes;
            return metadata;
        }

        #endregion

        #region Disposal

        /// <summary>
        /// Disposes resources used by the plugin.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _sessionRefreshTimer?.Dispose();
                _httpClient?.Dispose();
                _authLock?.Dispose();
            }
            base.Dispose(disposing);
        }

        #endregion
    }
}
