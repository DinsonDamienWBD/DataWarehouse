using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.APM;

/// <summary>
/// Observability strategy for Elastic APM (Application Performance Monitoring).
/// Provides distributed tracing, error tracking, and performance metrics integrated with Elastic Stack.
/// </summary>
/// <remarks>
/// Elastic APM is part of the Elastic Observability solution, providing real-time application
/// performance monitoring with deep integration into Elasticsearch, Kibana, and Elastic Stack.
/// </remarks>
public sealed class ElasticApmStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private readonly ConcurrentQueue<object> _transactionQueue = new();
    private string _serverUrl = "http://localhost:8200";
    private string _secretToken = "";
    private string _serviceName = "datawarehouse";

    /// <inheritdoc/>
    public override string StrategyId => "elastic-apm";

    /// <inheritdoc/>
    public override string Name => "Elastic APM";

    /// <summary>
    /// Initializes a new instance of the <see cref="ElasticApmStrategy"/> class.
    /// </summary>
    public ElasticApmStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: true,
        SupportsLogging: false,
        SupportsDistributedTracing: true,
        SupportsAlerting: true,
        SupportedExporters: new[] { "ElasticAPM", "Elasticsearch" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the Elastic APM server connection.
    /// </summary>
    /// <param name="serverUrl">APM server URL.</param>
    /// <param name="secretToken">Secret token for authentication.</param>
    /// <param name="serviceName">Service name for identification.</param>
    public void Configure(string serverUrl, string secretToken, string serviceName = "datawarehouse")
    {
        _serverUrl = serverUrl;
        _secretToken = secretToken;
        _serviceName = serviceName;

        if (!string.IsNullOrEmpty(secretToken))
        {
            _httpClient.DefaultRequestHeaders.Remove("Authorization");
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {secretToken}");
        }
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("elastic_apm.metrics_sent");
        var metricsets = new List<object>();

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000; // microseconds

        foreach (var metric in metrics)
        {
            var samples = new Dictionary<string, object>
            {
                [metric.Name] = new { value = metric.Value }
            };

            var metricset = new
            {
                metricset = new
                {
                    timestamp,
                    samples,
                    tags = metric.Labels?.ToDictionary(l => l.Name, l => l.Value)
                }
            };

            metricsets.Add(metricset);
        }

        await SendToApmServerAsync(metricsets, cancellationToken);
    }

    /// <inheritdoc/>
    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        IncrementCounter("elastic_apm.traces_sent");
        var events = new List<object>();

        // Group spans by trace to create transactions and spans
        var spansByTrace = spans.GroupBy(s => s.TraceId);

        foreach (var traceGroup in spansByTrace)
        {
            // Find the root transaction span (no parent) to get its ID for child span transaction_id.
            var rootSpanId = traceGroup
                .Where(s => string.IsNullOrEmpty(s.ParentSpanId))
                .Select(s => s.SpanId)
                .FirstOrDefault();

            foreach (var span in traceGroup)
            {
                var isTransaction = string.IsNullOrEmpty(span.ParentSpanId);

                if (isTransaction)
                {
                    var transaction = new
                    {
                        transaction = new
                        {
                            id = span.SpanId,
                            trace_id = span.TraceId,
                            name = span.OperationName,
                            type = span.Kind.ToString().ToLowerInvariant(),
                            duration = span.Duration.TotalMilliseconds,
                            timestamp = span.StartTime.ToUnixTimeMilliseconds() * 1000, // microseconds
                            result = span.Attributes?.ContainsKey("error") == true ? "error" : "success",
                            context = new
                            {
                                service = new { name = _serviceName },
                                tags = span.Attributes?.ToDictionary(t => t.Key, t => t.Value)
                            }
                        }
                    };

                    events.Add(transaction);
                }
                else
                {
                    // transaction_id must reference the root transaction span of this trace,
                    // not the direct parent span (which may itself be a child span).
                    var transactionId = rootSpanId ?? span.ParentSpanId;
                    var apmSpan = new
                    {
                        span = new
                        {
                            id = span.SpanId,
                            transaction_id = transactionId,
                            trace_id = span.TraceId,
                            parent_id = span.ParentSpanId,
                            name = span.OperationName,
                            type = span.Kind.ToString().ToLowerInvariant(),
                            duration = span.Duration.TotalMilliseconds,
                            timestamp = span.StartTime.ToUnixTimeMilliseconds() * 1000, // microseconds
                            context = new
                            {
                                tags = span.Attributes?.ToDictionary(t => t.Key, t => t.Value)
                            }
                        }
                    };

                    events.Add(apmSpan);
                }
            }
        }

        await SendToApmServerAsync(events, cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Elastic APM does not support direct logging - use Elasticsearch or Filebeat instead");
    }

    private async Task SendToApmServerAsync(IEnumerable<object> events, CancellationToken ct)
    {
        try
        {
            // Elastic APM uses NDJSON format
            var sb = new StringBuilder();
            foreach (var evt in events)
            {
                sb.AppendLine(JsonSerializer.Serialize(evt));
            }

            var content = new StringContent(sb.ToString(), Encoding.UTF8, "application/x-ndjson");
            var url = $"{_serverUrl}/intake/v2/events";

            using var response = await _httpClient.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException)
        {
            // APM Server unavailable - buffer data
            foreach (var evt in events)
            {
                _transactionQueue.Enqueue(evt);
            }
        }
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{_serverUrl}/", cancellationToken);

            return new HealthCheckResult(
                IsHealthy: response.IsSuccessStatusCode,
                Description: response.IsSuccessStatusCode ? "Elastic APM server is healthy" : "APM server unhealthy",
                Data: new Dictionary<string, object>
                {
                    ["serverUrl"] = _serverUrl,
                    ["serviceName"] = _serviceName,
                    ["queuedEvents"] = _transactionQueue.Count,
                    ["hasSecretToken"] = !string.IsNullOrEmpty(_secretToken)
                });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: $"Elastic APM health check failed: {ex.Message}",
                Data: null);
        }
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_serverUrl) || (!_serverUrl.StartsWith("http://") && !_serverUrl.StartsWith("https://")))
            throw new InvalidOperationException("ElasticApmStrategy: Invalid endpoint URL configured.");
        IncrementCounter("elastic_apm.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }


    /// <inheritdoc/>
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(100), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* Shutdown grace period elapsed */ }
        IncrementCounter("elastic_apm.shutdown");
        await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
                _secretToken = string.Empty;
        if (disposing)
        {
            _httpClient.Dispose();
        }
        base.Dispose(disposing);
    }
}
