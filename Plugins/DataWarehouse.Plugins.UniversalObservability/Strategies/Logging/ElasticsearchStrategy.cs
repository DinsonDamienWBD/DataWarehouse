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
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        var indexName = $"{_indexPrefix}-{DateTime.UtcNow:yyyy.MM.dd}";

        // Use bulk API for efficiency
        var bulkBody = new StringBuilder();

        foreach (var entry in logEntries)
        {
            // Index action
            bulkBody.AppendLine(JsonSerializer.Serialize(new { index = new { _index = indexName } }));

            // Document
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

            bulkBody.AppendLine(JsonSerializer.Serialize(doc));
        }

        var content = new StringContent(bulkBody.ToString(), Encoding.UTF8, "application/x-ndjson");
        var response = await _httpClient.PostAsync($"{_url}/_bulk", content, cancellationToken);
        response.EnsureSuccessStatusCode();
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
        var response = await _httpClient.PostAsync($"{_url}/{_indexPrefix}-*/_search", content, ct);
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
        var response = await _httpClient.PostAsync($"{_url}/{_indexPrefix}-*/_search", content, ct);
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
        var response = await _httpClient.PutAsync($"{_url}/_index_template/{_indexPrefix}-template", content, ct);
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
        try
        {
            var response = await _httpClient.GetAsync($"{_url}/_cluster/health", cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var health = JsonSerializer.Deserialize<JsonElement>(content);

            var status = health.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : "unknown";
            var isHealthy = status == "green" || status == "yellow";

            return new HealthCheckResult(
                IsHealthy: isHealthy,
                Description: $"Elasticsearch cluster status: {status}",
                Data: new Dictionary<string, object>
                {
                    ["url"] = _url,
                    ["indexPrefix"] = _indexPrefix,
                    ["clusterStatus"] = status ?? "unknown"
                });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: $"Elasticsearch health check failed: {ex.Message}",
                Data: null);
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _httpClient.Dispose();
        }
        base.Dispose(disposing);
    }
}
