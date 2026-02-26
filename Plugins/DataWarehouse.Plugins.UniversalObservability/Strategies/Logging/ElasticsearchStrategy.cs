using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Logging;

/// <summary>
/// Observability strategy for Elasticsearch/ELK Stack logging.
/// Provides structured logging with full-text search, aggregations, and visualization via Kibana.
/// </summary>
/// <remarks>
/// Elasticsearch is a distributed, RESTful search and analytics engine.
/// Combined with Logstash and Kibana, it forms the ELK Stack for log management.
/// </remarks>
public sealed class ElasticsearchStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _url = "http://localhost:9200";
    private string _indexPrefix = "datawarehouse-logs";
    private string _username = "";
    private string _password = "";
    private string _apiKey = "";
    private readonly ConcurrentQueue<Dictionary<string, object>> _logsBatch = new();
    private readonly System.Timers.Timer _flushTimer;
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private int _batchSize = 500; // Elasticsearch can handle larger batches
    private int _flushIntervalMs = 5000; // Default 5 seconds
    private int _circuitBreakerFailures = 0;
    private const int CircuitBreakerThreshold = 5;
    private bool _circuitOpen = false;
    private DateTimeOffset _circuitOpenedAt = DateTimeOffset.MinValue;
    private int _flushIntervalSeconds = 5;

    /// <inheritdoc/>
    public override string StrategyId => "elasticsearch";

    /// <inheritdoc/>
    public override string Name => "Elasticsearch (ELK Stack)";

    /// <summary>
    /// Initializes a new instance of the <see cref="ElasticsearchStrategy"/> class.
    /// </summary>
    public ElasticsearchStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: false,
        SupportsTracing: false,
        SupportsLogging: true,
        SupportsDistributedTracing: false,
        SupportsAlerting: true,
        SupportedExporters: new[] { "Elasticsearch", "Logstash", "Kibana" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _flushTimer = new System.Timers.Timer(_flushIntervalSeconds * 1000);
        _flushTimer.Elapsed += async (_, _) => await FlushLogsAsync(CancellationToken.None);
        _flushTimer.AutoReset = true;
        _flushTimer.Start();
    }

    /// <summary>
    /// Configures the Elasticsearch connection.
    /// </summary>
    /// <param name="url">Elasticsearch server URL.</param>
    /// <param name="indexPrefix">Index name prefix (date will be appended).</param>
    /// <param name="username">Username for basic auth.</param>
    /// <param name="password">Password for basic auth.</param>
    public void Configure(string url, string indexPrefix = "datawarehouse-logs", string username = "", string password = "")
    {
        _url = url.TrimEnd('/');
        _indexPrefix = indexPrefix;
        _username = username;
        _password = password;

        _httpClient.DefaultRequestHeaders.Clear();
        if (!string.IsNullOrEmpty(_username) && !string.IsNullOrEmpty(_password))
        {
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_username}:{_password}"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
        }
    }

    /// <summary>
    /// Configures the Elasticsearch connection with API key authentication.
    /// </summary>
    /// <param name="url">Elasticsearch server URL.</param>
    /// <param name="apiKey">API key for authentication.</param>
    /// <param name="indexPrefix">Index name prefix.</param>
    public void ConfigureWithApiKey(string url, string apiKey, string indexPrefix = "datawarehouse-logs")
    {
        _url = url.TrimEnd('/');
        _indexPrefix = indexPrefix;
        _apiKey = apiKey;

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("ApiKey", apiKey);
    }

    /// <inheritdoc/>
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        // Validate cluster URL
        if (string.IsNullOrWhiteSpace(_url))
            throw new InvalidOperationException("Elasticsearch cluster URL is required. Call Configure() or ConfigureWithApiKey() before initialization.");

        // Validate URL format
        if (!Uri.TryCreate(_url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"Invalid Elasticsearch URL: '{_url}'");

        // Warn if not HTTPS (but allow HTTP for dev/local)
        if (uri.Scheme != "https" && !uri.Host.Contains("localhost") && !uri.Host.StartsWith("127."))
        {
            // Log warning but continue (don't throw)
        }

        // Validate index pattern (alphanumeric, hyphens, underscores only)
        if (string.IsNullOrWhiteSpace(_indexPrefix))
            throw new InvalidOperationException("Index pattern cannot be empty.");

        if (!System.Text.RegularExpressions.Regex.IsMatch(_indexPrefix, @"^[a-z0-9_-]+$"))
            throw new InvalidOperationException($"Invalid index pattern: '{_indexPrefix}'. Must contain only lowercase letters, digits, hyphens, and underscores.");

        // Validate batch size (1-10000)
        if (_batchSize < 1 || _batchSize > 10000)
            throw new InvalidOperationException($"Bulk batch size must be between 1 and 10000. Got: {_batchSize}");

        // Validate flush interval (100ms-60s)
        if (_flushIntervalMs < 100 || _flushIntervalMs > 60000)
            throw new InvalidOperationException($"Flush interval must be between 100ms and 60000ms. Got: {_flushIntervalMs}ms");

        // Test cluster health endpoint
        try
        {
            using var response = await _httpClient.GetAsync($"{_url}/_cluster/health", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Elasticsearch cluster health check failed with status {response.StatusCode}");
            }
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to connect to Elasticsearch cluster: {ex.Message}", ex);
        }

        await base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        // Stop flush timer
        _flushTimer?.Stop();

        // Flush buffered logs with 10-second timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        await _flushLock.WaitAsync(cts.Token);
        try
        {
            if (_logsBatch.Count > 0)
                await FlushLogsAsync(cts.Token);
        }
        catch (OperationCanceledException ex)
        {

            // Shutdown timeout exceeded, abandon remaining logs
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _flushLock.Release();
        }

        // Close HTTP connection
        _httpClient?.Dispose();

        await base.ShutdownAsyncCore(cancellationToken);
    }

    private async Task FlushLogsAsync(CancellationToken ct)
    {
        if (_circuitOpen && DateTimeOffset.UtcNow - _circuitOpenedAt < TimeSpan.FromMinutes(1))
            return; // Circuit open, skip flush

        if (_logsBatch.IsEmpty) return;

        await _flushLock.WaitAsync(ct);
        try
        {
            var batch = new List<Dictionary<string, object>>();
            while (batch.Count < _batchSize && _logsBatch.TryDequeue(out var doc))
                batch.Add(doc);

            if (batch.Count == 0) return;

            try
            {
                await SendWithRetryAsync(async () =>
                {
                    var indexName = $"{_indexPrefix}-{DateTime.UtcNow:yyyy.MM.dd}";
                    var bulkBody = new StringBuilder();

                    foreach (var doc in batch)
                    {
                        bulkBody.AppendLine(JsonSerializer.Serialize(new { index = new { _index = indexName } }));
                        bulkBody.AppendLine(JsonSerializer.Serialize(doc));
                    }

                    var content = new StringContent(bulkBody.ToString(), Encoding.UTF8, "application/x-ndjson");
                    using var response = await _httpClient.PostAsync($"{_url}/_bulk", content, ct);
                    response.EnsureSuccessStatusCode();

                    // Check for partial failures in bulk response
                    var responseContent = await response.Content.ReadAsStringAsync(ct);
                    var bulkResult = JsonSerializer.Deserialize<JsonElement>(responseContent);
                    if (bulkResult.TryGetProperty("errors", out var errors) && errors.GetBoolean())
                    {
                        throw new HttpRequestException("Elasticsearch bulk operation had errors");
                    }
                }, ct);

                IncrementCounter("elasticsearch.docs_indexed");
                IncrementCounter("elasticsearch.bulk_requests");
            }
            catch (Exception)
            {
                IncrementCounter("elasticsearch.bulk_requests");
                throw;
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private async Task SendWithRetryAsync(Func<Task> action, CancellationToken ct)
    {
        var maxRetries = 3;
        var baseDelay = TimeSpan.FromMilliseconds(200);

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                await action();
                _circuitBreakerFailures = 0; // Reset on success
                _circuitOpen = false;
                return;
            }
            catch (HttpRequestException ex) when (attempt < maxRetries - 1)
            {
                var statusCode = ex.StatusCode;

                if (statusCode == System.Net.HttpStatusCode.Unauthorized) // 401
                {
                    IncrementCounter("elasticsearch.auth_failure");
                    throw; // Don't retry auth failures
                }
                else if (statusCode == (System.Net.HttpStatusCode)429 || statusCode == System.Net.HttpStatusCode.ServiceUnavailable) // 429/503 - index write block
                {
                    IncrementCounter("elasticsearch.write_blocked");
                    var delay = baseDelay * Math.Pow(2, attempt + 1); // Longer backoff
                    await Task.Delay(delay, ct);
                }
                else
                {
                    var delay = baseDelay * Math.Pow(2, attempt);
                    await Task.Delay(delay, ct);
                }
            }
            catch (System.Net.Sockets.SocketException) when (attempt < maxRetries - 1)
            {
                // Connection refused - cluster might be down
                IncrementCounter("elasticsearch.connection_refused");
                var delay = baseDelay * Math.Pow(2, attempt + 1);
                await Task.Delay(delay, ct);
            }
            catch (Exception) when (attempt < maxRetries - 1)
            {
                var delay = baseDelay * Math.Pow(2, attempt);
                await Task.Delay(delay, ct);
            }
            catch (Exception)
            {
                _circuitBreakerFailures++;
                if (_circuitBreakerFailures >= CircuitBreakerThreshold)
                {
                    _circuitOpen = true;
                    _circuitOpenedAt = DateTimeOffset.UtcNow;
                }
                throw;
            }
        }
    }

    /// <inheritdoc/>
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        if (_circuitOpen && DateTimeOffset.UtcNow - _circuitOpenedAt < TimeSpan.FromMinutes(1))
            return; // Circuit open, drop logs

        foreach (var entry in logEntries)
        {
            var doc = new Dictionary<string, object>
            {
                ["@timestamp"] = entry.Timestamp.ToString("o"),
                ["level"] = entry.Level.ToString(),
                ["message"] = entry.Message,
                ["host"] = Environment.MachineName,
                ["application"] = "datawarehouse"
            };

            if (entry.Properties != null)
            {
                foreach (var prop in entry.Properties)
                {
                    doc[prop.Key] = prop.Value;
                }
            }

            if (entry.Exception != null)
            {
                doc["exception"] = new
                {
                    type = entry.Exception.GetType().FullName,
                    message = entry.Exception.Message,
                    stacktrace = entry.Exception.StackTrace ?? ""
                };
            }

            _logsBatch.Enqueue(doc);
        }

        // Flush immediately if batch size reached
        if (_logsBatch.Count >= _batchSize)
            await FlushLogsAsync(cancellationToken);
    }

    /// <summary>
    /// Searches logs using Elasticsearch Query DSL.
    /// </summary>
    /// <param name="query">Elasticsearch query object.</param>
    /// <param name="size">Maximum number of results.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Search results as JSON.</returns>
    public async Task<string> SearchAsync(object query, int size = 100, CancellationToken ct = default)
    {
        var searchBody = JsonSerializer.Serialize(new
        {
            query,
            size,
            sort = new[] { new { @timestamp = new { order = "desc" } } }
        });

        var content = new StringContent(searchBody, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync($"{_url}/{_indexPrefix}-*/_search", content, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Searches logs by message text.
    /// </summary>
    /// <param name="messageText">Text to search for in messages.</param>
    /// <param name="startTime">Start time for search range.</param>
    /// <param name="endTime">End time for search range.</param>
    /// <param name="size">Maximum number of results.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Search results as JSON.</returns>
    public Task<string> SearchByMessageAsync(string messageText, DateTimeOffset? startTime = null,
        DateTimeOffset? endTime = null, int size = 100, CancellationToken ct = default)
    {
        var mustClauses = new List<object>
        {
            new { match = new { message = messageText } }
        };

        if (startTime.HasValue || endTime.HasValue)
        {
            var rangeClause = new Dictionary<string, object>();
            if (startTime.HasValue)
                rangeClause["gte"] = startTime.Value.ToString("o");
            if (endTime.HasValue)
                rangeClause["lte"] = endTime.Value.ToString("o");

            mustClauses.Add(new { range = new { @timestamp = rangeClause } });
        }

        var query = new { @bool = new { must = mustClauses } };
        return SearchAsync(query, size, ct);
    }

    /// <summary>
    /// Gets log count by level for a time period.
    /// </summary>
    /// <param name="startTime">Start time.</param>
    /// <param name="endTime">End time.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aggregation results as JSON.</returns>
    public async Task<string> GetLogCountByLevelAsync(DateTimeOffset startTime, DateTimeOffset endTime, CancellationToken ct = default)
    {
        var aggBody = JsonSerializer.Serialize(new
        {
            query = new
            {
                range = new
                {
                    @timestamp = new
                    {
                        gte = startTime.ToString("o"),
                        lte = endTime.ToString("o")
                    }
                }
            },
            size = 0,
            aggs = new
            {
                by_level = new
                {
                    terms = new { field = "level.keyword" }
                }
            }
        });

        var content = new StringContent(aggBody, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync($"{_url}/{_indexPrefix}-*/_search", content, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Creates index template for log indices.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task CreateIndexTemplateAsync(CancellationToken ct = default)
    {
        var template = new
        {
            index_patterns = new[] { $"{_indexPrefix}-*" },
            template = new
            {
                settings = new
                {
                    number_of_shards = 1,
                    number_of_replicas = 1
                },
                mappings = new
                {
                    properties = new
                    {
                        @timestamp = new { type = "date" },
                        level = new { type = "keyword" },
                        message = new { type = "text" },
                        host = new { type = "keyword" },
                        application = new { type = "keyword" },
                        exception = new
                        {
                            properties = new
                            {
                                type = new { type = "keyword" },
                                message = new { type = "text" },
                                stacktrace = new { type = "text" }
                            }
                        }
                    }
                }
            }
        };

        var content = new StringContent(JsonSerializer.Serialize(template), Encoding.UTF8, "application/json");
        using var response = await _httpClient.PutAsync($"{_url}/_index_template/{_indexPrefix}-template", content, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Elasticsearch logging strategy does not support metrics");
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Elasticsearch logging strategy does not support tracing");
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        var cachedResult = await GetCachedHealthAsync(async ct =>
        {
            try
            {
                using var response = await _httpClient.GetAsync($"{_url}/_cluster/health", ct);
                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStringAsync(ct);
                var health = JsonSerializer.Deserialize<JsonElement>(content);

                var status = health.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : "unknown";
                var isHealthy = status == "green" || status == "yellow";

                return new DataWarehouse.SDK.Contracts.StrategyHealthCheckResult(
                    IsHealthy: isHealthy,
                    Message: $"Elasticsearch cluster status: {status}",
                    Details: new Dictionary<string, object>
                    {
                        ["url"] = _url,
                        ["indexPrefix"] = _indexPrefix,
                        ["clusterStatus"] = status ?? "unknown",
                        ["circuitOpen"] = _circuitOpen,
                        ["docsIndexed"] = GetCounter("elasticsearch.docs_indexed"),
                        ["bulkRequests"] = GetCounter("elasticsearch.bulk_requests"),
                        ["authFailures"] = GetCounter("elasticsearch.auth_failure"),
                        ["writeBlocked"] = GetCounter("elasticsearch.write_blocked"),
                        ["connectionRefused"] = GetCounter("elasticsearch.connection_refused")
                    });
            }
            catch (Exception ex)
            {
                return new DataWarehouse.SDK.Contracts.StrategyHealthCheckResult(
                    IsHealthy: false,
                    Message: $"Elasticsearch health check failed: {ex.Message}",
                    Details: new Dictionary<string, object> { ["exception"] = ex.GetType().Name });
            }
        }, TimeSpan.FromSeconds(60), cancellationToken);

        return new HealthCheckResult(
            IsHealthy: cachedResult.IsHealthy,
            Description: cachedResult.Message ?? "Elasticsearch health check",
            Data: cachedResult.Details != null
                ? new Dictionary<string, object>(cachedResult.Details)
                : new Dictionary<string, object>());
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
                _apiKey = string.Empty;
                _password = string.Empty;
        if (disposing)
        {
            _flushTimer?.Stop();
            _flushTimer?.Dispose();
            _flushLock.Wait();
            try
            {
                Task.Run(() => FlushLogsAsync(CancellationToken.None)).Wait(TimeSpan.FromSeconds(5));
            }
            finally
            {
                _flushLock.Release();
            }
            _flushLock.Dispose();
            _httpClient.Dispose();
        }
        base.Dispose(disposing);
    }
}
