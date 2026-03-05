using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Tracing;

/// <summary>
/// Observability strategy for Zipkin distributed tracing.
/// Provides distributed tracing with simple span collection and dependency analysis.
/// </summary>
public sealed class ZipkinStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _url = "http://localhost:9411";
    private string _serviceName = "datawarehouse";

    public override string StrategyId => "zipkin";
    public override string Name => "Zipkin";

    public ZipkinStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: false, SupportsTracing: true, SupportsLogging: false,
        SupportsDistributedTracing: true, SupportsAlerting: false,
        SupportedExporters: new[] { "Zipkin", "ZipkinV2", "B3" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Configure(string url, string serviceName = "datawarehouse")
    {
        _url = url.TrimEnd('/');
        _serviceName = serviceName;
    }

    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        IncrementCounter("zipkin.traces_sent");
        var zipkinSpans = spans.Select(span => new
        {
            traceId = span.TraceId.Length > 16 ? span.TraceId : span.TraceId.PadLeft(32, '0'),
            id = span.SpanId.PadLeft(16, '0'),
            parentId = span.ParentSpanId?.PadLeft(16, '0'),
            name = span.OperationName,
            timestamp = span.StartTime.ToUnixTimeMicroseconds(),
            duration = (long)span.Duration.TotalMicroseconds,
            kind = span.Kind switch
            {
                SpanKind.Server => "SERVER",
                SpanKind.Client => "CLIENT",
                SpanKind.Producer => "PRODUCER",
                SpanKind.Consumer => "CONSUMER",
                _ => (string?)null
            },
            localEndpoint = new { serviceName = _serviceName, ipv4 = "127.0.0.1" },
            tags = span.Attributes?.ToDictionary(a => a.Key, a => a.Value?.ToString() ?? "")
                ?? new Dictionary<string, string>(),
            annotations = span.Events?.Select(e => new
            {
                timestamp = e.Timestamp.ToUnixTimeMicroseconds(),
                value = e.Name
            }).ToArray()
        }).ToArray();

        var json = JsonSerializer.Serialize(zipkinSpans);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync($"{_url}/api/v2/spans", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string> GetServicesAsync(CancellationToken ct = default)
    {
        using var response = await _httpClient.GetAsync($"{_url}/api/v2/services", ct);
 response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<string> GetTraceAsync(string traceId, CancellationToken ct = default)
    {
        using var response = await _httpClient.GetAsync($"{_url}/api/v2/trace/{traceId}", ct);
 response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken ct)
        => throw new NotSupportedException("Zipkin does not support metrics");

    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken ct)
        => throw new NotSupportedException("Zipkin does not support logging");

    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{_url}/health", ct);
            return new HealthCheckResult(response.IsSuccessStatusCode,
                response.IsSuccessStatusCode ? "Zipkin is healthy" : "Zipkin unhealthy",
                new Dictionary<string, object> { ["url"] = _url, ["serviceName"] = _serviceName });
        }
        catch (Exception ex) { return new HealthCheckResult(false, $"Zipkin health check failed: {ex.Message}", null); }
    }


    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_url) || (!_url.StartsWith("http://") && !_url.StartsWith("https://")))
            throw new InvalidOperationException("ZipkinStrategy: Invalid endpoint URL configured.");
        IncrementCounter("zipkin.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }


    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        // Finding 4584: removed decorative Task.Delay(100ms) — no real in-flight queue to drain.
        IncrementCounter("zipkin.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }

    protected override void Dispose(bool disposing) { if (disposing) _httpClient.Dispose(); base.Dispose(disposing); }
}
