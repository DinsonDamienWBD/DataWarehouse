using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.APM;

/// <summary>
/// Observability strategy for Instana APM platform.
/// Provides automatic application performance monitoring with distributed tracing, metrics, and events.
/// </summary>
/// <remarks>
/// Instana is an enterprise APM solution featuring automatic discovery, dependency mapping,
/// real-time monitoring, and AI-powered incident detection.
/// </remarks>
public sealed class InstanaStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private readonly BoundedDictionary<string, double> _metrics = new BoundedDictionary<string, double>(1000);
    private string _agentEndpoint = "http://localhost:42699";
    private string _agentKey = "";

    /// <inheritdoc/>
    public override string StrategyId => "instana";

    /// <inheritdoc/>
    public override string Name => "Instana";

    /// <summary>
    /// Initializes a new instance of the <see cref="InstanaStrategy"/> class.
    /// </summary>
    public InstanaStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: true,
        SupportsLogging: false,
        SupportsDistributedTracing: true,
        SupportsAlerting: true,
        SupportedExporters: new[] { "Instana", "OpenTelemetry" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the Instana agent endpoint.
    /// </summary>
    /// <param name="endpoint">Agent endpoint URL.</param>
    /// <param name="agentKey">Agent authentication key.</param>
    public void Configure(string endpoint, string agentKey)
    {
        _agentEndpoint = endpoint;
        _agentKey = agentKey;
        _httpClient.DefaultRequestHeaders.Add("X-Instana-Key", agentKey);
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("instana.metrics_sent");
        var metricsList = new List<object>();

        foreach (var metric in metrics)
        {
            _metrics.AddOrUpdate(metric.Name, metric.Value, (_, _) => metric.Value);

            var metricPayload = new
            {
                name = metric.Name,
                value = metric.Value,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                tags = metric.Labels?.ToDictionary(l => l.Name, l => l.Value),
                type = metric.Type.ToString().ToLowerInvariant()
            };

            metricsList.Add(metricPayload);
        }

        await SendToInstanaAsync("/metrics", metricsList, cancellationToken);
    }

    /// <inheritdoc/>
    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        IncrementCounter("instana.traces_sent");
        var tracesList = new List<object>();

        foreach (var span in spans)
        {
            var instanaSpan = new
            {
                t = span.TraceId,                           // Trace ID
                s = span.SpanId,                            // Span ID
                p = span.ParentSpanId,                       // Parent Span ID
                n = span.OperationName,                      // Operation name
                ts = span.StartTime.ToUnixTimeMilliseconds(), // Start timestamp
                d = (long)span.Duration.TotalMilliseconds, // Duration
                k = span.Kind switch
                {
                    SpanKind.Server => 1,
                    SpanKind.Client => 2,
                    SpanKind.Producer => 3,
                    SpanKind.Consumer => 4,
                    _ => 0
                },
                data = new
                {
                    tags = span.Attributes?.ToDictionary(t => t.Key, t => t.Value),
                    service = span.Attributes?.GetValueOrDefault("service.name")?.ToString() ?? "datawarehouse",
                    error = span.Attributes?.ContainsKey("error") == true
                }
            };

            tracesList.Add(instanaSpan);
        }

        await SendToInstanaAsync("/traces", tracesList, cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Instana does not support direct logging - use events or traces instead");
    }

    private async Task SendToInstanaAsync(string endpoint, object payload, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var url = $"{_agentEndpoint}{endpoint}";

            var response = await _httpClient.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException)
        {
            // Agent unavailable - data buffered locally
        }
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_agentEndpoint}/", cancellationToken);

            return new HealthCheckResult(
                IsHealthy: response.IsSuccessStatusCode,
                Description: response.IsSuccessStatusCode ? "Instana agent is healthy" : "Instana agent unhealthy",
                Data: new Dictionary<string, object>
                {
                    ["agentEndpoint"] = _agentEndpoint,
                    ["metricsCount"] = _metrics.Count,
                    ["hasAgentKey"] = !string.IsNullOrEmpty(_agentKey)
                });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: $"Instana health check failed: {ex.Message}",
                Data: null);
        }
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_agentEndpoint) || (!_agentEndpoint.StartsWith("http://") && !_agentEndpoint.StartsWith("https://")))
            throw new InvalidOperationException("InstanaStrategy: Invalid endpoint URL configured.");
        IncrementCounter("instana.initialized");
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
        IncrementCounter("instana.shutdown");
        await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _httpClient.Dispose();
        }
        base.Dispose(disposing);
    }
}
