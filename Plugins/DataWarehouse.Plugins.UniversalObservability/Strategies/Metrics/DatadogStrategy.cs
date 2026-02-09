using System.Collections.Concurrent;
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
    private readonly int _batchSize = 100;

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
        _apiKey = apiKey;
        _site = site;
        _service = service;
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("DD-API-KEY", _apiKey);
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
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

        var response = await _httpClient.PostAsync(
            $"https://api.{_site}/api/v2/series",
            content,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
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
                    start = span.StartTime.ToUnixTimeMilliseconds() * 1_000_000,
                    duration = (long)span.Duration.TotalNanoseconds,
                    error = span.Status == SpanStatus.Error ? 1 : 0,
                    meta = span.Attributes?.ToDictionary(a => a.Key, a => a.Value?.ToString() ?? "")
                });
            }

            traces.Add(traceSpans);
        }

        var payload = JsonSerializer.Serialize(traces);
        var content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await _httpClient.PutAsync(
            $"https://trace.agent.{_site}/v0.4/traces",
            content,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
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

        var response = await _httpClient.PostAsync(
            $"https://http-intake.logs.{_site}/api/v2/logs",
            content,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    private static ulong ConvertToNumericId(string id)
    {
        // Convert hex string to numeric ID for Datadog
        if (ulong.TryParse(id, System.Globalization.NumberStyles.HexNumber, null, out var result))
            return result;

        // Fallback: hash the string
        return (ulong)id.GetHashCode() & 0x7FFFFFFFFFFFFFFF;
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync(
                $"https://api.{_site}/api/v1/validate",
                cancellationToken);

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
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _httpClient.Dispose();
        }
        base.Dispose(disposing);
    }
}
