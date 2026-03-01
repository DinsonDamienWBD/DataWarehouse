using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Metrics;

/// <summary>
/// Observability strategy for Datadog metrics, logs, and APM integration.
/// Provides comprehensive observability with unified metrics, logs, and traces.
/// </summary>
/// <remarks>
/// Datadog is a SaaS-based monitoring and analytics platform providing
/// infrastructure monitoring, APM, log management, and security monitoring.
/// </remarks>
public sealed class DatadogStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private readonly ConcurrentQueue<object> _metricsBatch = new();
    private readonly ConcurrentQueue<object> _logsBatch = new();
    private string _apiKey = "";
    private string _site = "datadoghq.com";
    private string _service = "datawarehouse";

    /// <inheritdoc/>
    public override string StrategyId => "datadog";

    /// <inheritdoc/>
    public override string Name => "Datadog";

    /// <summary>
    /// Initializes a new instance of the <see cref="DatadogStrategy"/> class.
    /// </summary>
    public DatadogStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: true,
        SupportsLogging: true,
        SupportsDistributedTracing: true,
        SupportsAlerting: true,
        SupportedExporters: new[] { "Datadog", "DogStatsD", "DatadogAPM" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the Datadog connection.
    /// </summary>
    /// <param name="apiKey">Datadog API key.</param>
    /// <param name="site">Datadog site (e.g., datadoghq.com, datadoghq.eu).</param>
    /// <param name="service">Service name for tagging.</param>
    public void Configure(string apiKey, string site = "datadoghq.com", string service = "datawarehouse")
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Datadog API key must not be empty.", nameof(apiKey));
        _apiKey = apiKey;
        _site = site;
        _service = service;
        // Do NOT set DefaultRequestHeaders — inject per-request to avoid thread-safety issues.
    }

    /// <summary>Adds the Datadog API key to the request headers (per-request, thread-safe).</summary>
    private void AddApiKey(HttpRequestMessage request) =>
        request.Headers.Add("DD-API-KEY", _apiKey);

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("datadog.metrics_sent");
        var series = new List<object>();

        foreach (var metric in metrics)
        {
            var tags = metric.Labels?.Select(l => $"{l.Name}:{l.Value}").ToList() ?? new List<string>();
            tags.Add($"service:{_service}");

            var metricType = metric.Type switch
            {
                MetricType.Counter => "count",
                MetricType.Gauge => "gauge",
                MetricType.Histogram => "histogram",
                MetricType.Summary => "distribution",
                _ => "gauge"
            };

            series.Add(new
            {
                metric = metric.Name.Replace("-", "_"),
                type = metricType,
                points = new[] { new[] { metric.Timestamp.ToUnixTimeSeconds(), metric.Value } },
                tags = tags.ToArray(),
                unit = metric.Unit ?? "unit"
            });
        }

        var payload = JsonSerializer.Serialize(new { series });
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.{_site}/api/v2/series") { Content = content };
        AddApiKey(req);
        using var response = await _httpClient.SendAsync(req, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        IncrementCounter("datadog.traces_sent");
        var traces = new List<List<object>>();
        var traceGroups = spans.GroupBy(s => s.TraceId);

        foreach (var traceGroup in traceGroups)
        {
            var traceSpans = new List<object>();

            foreach (var span in traceGroup)
            {
                traceSpans.Add(new
                {
                    trace_id = ConvertToNumericId(span.TraceId),
                    span_id = ConvertToNumericId(span.SpanId),
                    parent_id = span.ParentSpanId != null ? ConvertToNumericId(span.ParentSpanId) : 0,
                    name = span.OperationName,
                    service = _service,
                    resource = span.OperationName,
                    type = span.Kind switch
                    {
                        SpanKind.Client => "http",
                        SpanKind.Server => "web",
                        SpanKind.Producer => "queue",
                        SpanKind.Consumer => "queue",
                        _ => "custom"
                    },
                    // Compute start time in nanoseconds safely to avoid long overflow.
                    // ToUnixTimeMilliseconds() * 1_000_000 overflows for current timestamps (~1.7T ms * 1M = overflow).
                    // Correct approach: seconds * 1_000_000_000L + (sub-second ms) * 1_000_000L
                    start = span.StartTime.ToUnixTimeSeconds() * 1_000_000_000L
                          + (span.StartTime.ToUnixTimeMilliseconds() % 1000L) * 1_000_000L,
                    duration = (long)span.Duration.TotalNanoseconds,
                    error = span.Status == SpanStatus.Error ? 1 : 0,
                    meta = span.Attributes?.ToDictionary(a => a.Key, a => a.Value?.ToString() ?? "")
                });
            }

            traces.Add(traceSpans);
        }

        var payload = JsonSerializer.Serialize(traces);
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Put, $"https://trace.agent.{_site}/v0.4/traces") { Content = content };
        AddApiKey(req);
        using var response = await _httpClient.SendAsync(req, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        IncrementCounter("datadog.logs_sent");
        var logs = new List<object>();

        foreach (var entry in logEntries)
        {
            var log = new Dictionary<string, object>
            {
                ["ddsource"] = "datawarehouse",
                ["ddtags"] = $"service:{_service}",
                ["hostname"] = Environment.MachineName,
                ["message"] = entry.Message,
                ["status"] = entry.Level switch
                {
                    LogLevel.Trace => "debug",
                    LogLevel.Debug => "debug",
                    LogLevel.Information => "info",
                    LogLevel.Warning => "warn",
                    LogLevel.Error => "error",
                    LogLevel.Critical => "critical",
                    _ => "info"
                },
                ["timestamp"] = entry.Timestamp.ToUnixTimeMilliseconds()
            };

            if (entry.Properties != null)
            {
                foreach (var prop in entry.Properties)
                {
                    log[prop.Key] = prop.Value;
                }
            }

            if (entry.Exception != null)
            {
                log["error.message"] = entry.Exception.Message;
                log["error.stack"] = entry.Exception.StackTrace ?? "";
                log["error.type"] = entry.Exception.GetType().Name;
            }

            logs.Add(log);
        }

        var payload = JsonSerializer.Serialize(logs);
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://http-intake.logs.{_site}/api/v2/logs") { Content = content };
        AddApiKey(req);
        using var response = await _httpClient.SendAsync(req, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static ulong ConvertToNumericId(string id)
    {
        // Convert hex string to numeric ID for Datadog
        if (ulong.TryParse(id, System.Globalization.NumberStyles.HexNumber, null, out var result))
            return result;

        // Fallback: use XxHash64 for a deterministic, cross-process-stable hash.
        // string.GetHashCode() is non-deterministic across processes (randomized per-run),
        // which would produce inconsistent trace IDs when spans are reported by multiple processes.
        var bytes = Encoding.UTF8.GetBytes(id);
        var hash = XxHash64.HashToUInt64(bytes);
        return hash & 0x7FFFFFFFFFFFFFFF; // Clear sign bit for positive ulong interpretation
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            using var validateReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.{_site}/api/v1/validate");
            AddApiKey(validateReq);
            using var response = await _httpClient.SendAsync(validateReq, cancellationToken);

            return new HealthCheckResult(
                IsHealthy: response.IsSuccessStatusCode,
                Description: response.IsSuccessStatusCode ? "Datadog connection is healthy" : "Datadog API validation failed",
                Data: new Dictionary<string, object>
                {
                    ["site"] = _site,
                    ["service"] = _service,
                    ["hasApiKey"] = !string.IsNullOrEmpty(_apiKey)
                });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: $"Datadog health check failed: {ex.Message}",
                Data: null);
        }
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException("DatadogStrategy: API key is required. Call Configure() before initialization.");
        IncrementCounter("datadog.initialized");
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
        IncrementCounter("datadog.shutdown");
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
