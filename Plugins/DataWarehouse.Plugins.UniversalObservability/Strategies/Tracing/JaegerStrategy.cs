using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Tracing;

/// <summary>
/// Observability strategy for Jaeger distributed tracing.
/// Provides end-to-end distributed tracing with root cause analysis and service dependency visualization.
/// </summary>
/// <remarks>
/// Jaeger is an open-source, end-to-end distributed tracing system used for monitoring
/// and troubleshooting microservices-based distributed systems.
/// </remarks>
public sealed class JaegerStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _collectorUrl = "http://localhost:14268";
    private string _serviceName = "datawarehouse";

    public override string StrategyId => "jaeger";
    public override string Name => "Jaeger";

    public JaegerStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: false, SupportsTracing: true, SupportsLogging: false,
        SupportsDistributedTracing: true, SupportsAlerting: false,
        SupportedExporters: new[] { "Jaeger", "Thrift", "OTLP" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Configure(string collectorUrl, string serviceName = "datawarehouse")
    {
        _collectorUrl = collectorUrl.TrimEnd('/');
        _serviceName = serviceName;
    }

    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        IncrementCounter("jaeger.traces_sent");
        // Build Jaeger Thrift format batch
        var batch = new
        {
            process = new
            {
                serviceName = _serviceName,
                tags = new[]
                {
                    new { key = "hostname", vType = "STRING", vStr = Environment.MachineName },
                    new { key = "jaeger.version", vType = "STRING", vStr = "datawarehouse-client" }
                }
            },
            spans = spans.Select(span => new
            {
                traceIdLow = ConvertToLong(span.TraceId.Substring(0, Math.Min(16, span.TraceId.Length))),
                traceIdHigh = span.TraceId.Length > 16 ? ConvertToLong(span.TraceId.Substring(16)) : 0L,
                spanId = ConvertToLong(span.SpanId),
                parentSpanId = span.ParentSpanId != null ? ConvertToLong(span.ParentSpanId) : 0L,
                operationName = span.OperationName,
                references = span.ParentSpanId != null ? new[]
                {
                    new { refType = "CHILD_OF", traceIdLow = ConvertToLong(span.TraceId.Substring(0, Math.Min(16, span.TraceId.Length))),
                          traceIdHigh = span.TraceId.Length > 16 ? ConvertToLong(span.TraceId.Substring(16)) : 0L,
                          spanId = ConvertToLong(span.ParentSpanId) }
                } : Array.Empty<object>(),
                flags = 1,
                startTime = span.StartTime.ToUnixTimeMicroseconds(),
                duration = (long)span.Duration.TotalMicroseconds,
                tags = span.Attributes?.Select(a => new { key = a.Key, vType = "STRING", vStr = a.Value?.ToString() ?? "" }).ToArray()
                    ?? Array.Empty<object>(),
                logs = span.Events?.Select(e => new
                {
                    timestamp = e.Timestamp.ToUnixTimeMicroseconds(),
                    fields = new[] { new { key = "event", vType = "STRING", vStr = e.Name } }
                }).ToArray() ?? Array.Empty<object>()
            }).ToArray()
        };

        var json = JsonSerializer.Serialize(batch);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync($"{_collectorUrl}/api/traces", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static long ConvertToLong(string hexString)
    {
        if (long.TryParse(hexString, System.Globalization.NumberStyles.HexNumber, null, out var result))
            return result;
        return hexString.GetHashCode();
    }

    public async Task<string> GetServicesAsync(CancellationToken ct = default)
    {
        using var response = await _httpClient.GetAsync($"{_collectorUrl.Replace("14268", "16686")}/api/services", ct);
 response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<string> FindTracesAsync(string service, string operation = "", int limit = 20, CancellationToken ct = default)
    {
        var query = $"service={Uri.EscapeDataString(service)}&limit={limit}";
        if (!string.IsNullOrEmpty(operation)) query += $"&operation={Uri.EscapeDataString(operation)}";
        using var response = await _httpClient.GetAsync($"{_collectorUrl.Replace("14268", "16686")}/api/traces?{query}", ct);
 response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken ct)
        => throw new NotSupportedException("Jaeger does not support metrics");

    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken ct)
        => throw new NotSupportedException("Jaeger does not support logging");

    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{_collectorUrl}/", ct);
            return new HealthCheckResult(response.IsSuccessStatusCode,
                response.IsSuccessStatusCode ? "Jaeger collector is healthy" : "Jaeger unhealthy",
                new Dictionary<string, object> { ["collectorUrl"] = _collectorUrl, ["serviceName"] = _serviceName });
        }
        catch (Exception ex) { return new HealthCheckResult(false, $"Jaeger health check failed: {ex.Message}", null); }
    }


    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_collectorUrl) || (!_collectorUrl.StartsWith("http://") && !_collectorUrl.StartsWith("https://")))
            throw new InvalidOperationException("JaegerStrategy: Invalid endpoint URL configured.");
        IncrementCounter("jaeger.initialized");
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
        IncrementCounter("jaeger.shutdown");
        await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing) { if (disposing) _httpClient.Dispose(); base.Dispose(disposing); }
}

internal static class DateTimeOffsetExtensions
{
    public static long ToUnixTimeMicroseconds(this DateTimeOffset dto) => dto.ToUnixTimeMilliseconds() * 1000;
}
