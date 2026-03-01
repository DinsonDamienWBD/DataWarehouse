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
    // LOW-4687: Separate query URL for Jaeger UI/query API (default port 16686).
    private string _queryUrl = "http://localhost:16686";
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

    public void Configure(string collectorUrl, string serviceName = "datawarehouse", string? queryUrl = null)
    {
        _collectorUrl = collectorUrl.TrimEnd('/');
        _serviceName = serviceName;
        // LOW-4687: Accept explicit query URL to avoid fragile port substitution heuristics.
        if (!string.IsNullOrEmpty(queryUrl))
            _queryUrl = queryUrl.TrimEnd('/');
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
                // P2-4671: Pad or truncate TraceId/SpanId to expected hex widths before conversion.
                // An empty string would fall through ConvertToLong to GetHashCode() — unpredictable.
                traceIdLow = ConvertToLong(NormalizeHexId(span.TraceId, 32).Substring(0, 16)),
                traceIdHigh = ConvertToLong(NormalizeHexId(span.TraceId, 32).Substring(16)),
                spanId = ConvertToLong(NormalizeHexId(span.SpanId, 16)),
                parentSpanId = span.ParentSpanId != null ? ConvertToLong(NormalizeHexId(span.ParentSpanId, 16)) : 0L,
                operationName = span.OperationName,
                references = span.ParentSpanId != null ? new[]
                {
                    new { refType = "CHILD_OF", traceIdLow = ConvertToLong(NormalizeHexId(span.TraceId, 32).Substring(0, 16)),
                          traceIdHigh = ConvertToLong(NormalizeHexId(span.TraceId, 32).Substring(16)),
                          spanId = ConvertToLong(NormalizeHexId(span.ParentSpanId, 16)) }
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

    /// <summary>
    /// Normalizes a hex string to exactly <paramref name="width"/> characters.
    /// Pads with leading zeros if shorter; takes the last <paramref name="width"/> chars if longer.
    /// If the string is null or empty, returns a zero-padded string of the required width.
    /// </summary>
    private static string NormalizeHexId(string? id, int width)
    {
        if (string.IsNullOrEmpty(id))
            return new string('0', width);
        if (id.Length == width)
            return id;
        if (id.Length < width)
            return id.PadLeft(width, '0');
        // Take the rightmost `width` characters (most-significant bits may be zero-padded upstream).
        return id[^width..];
    }

    private static long ConvertToLong(string hexString)
    {
        if (string.IsNullOrEmpty(hexString))
            return 0L;
        if (long.TryParse(hexString, System.Globalization.NumberStyles.HexNumber, null, out var result))
            return result;
        // Non-hex content — return a stable deterministic value rather than a hash which varies per run.
        return 0L;
    }

    /// <summary>
    /// Gets the Jaeger Query UI base URL.
    /// Uses <see cref="_queryUrl"/> which is explicitly configured rather than port-substitution heuristics.
    /// </summary>
    private string QueryUrl => string.IsNullOrEmpty(_queryUrl)
        ? _collectorUrl  // fallback if query URL not separately configured
        : _queryUrl;

    public async Task<string> GetServicesAsync(CancellationToken ct = default)
    {
        // LOW-4687: Replaced brittle string.Replace("14268", "16686") with a properly
        // configured query URL that doesn't break for non-default ports.
        using var response = await _httpClient.GetAsync($"{QueryUrl}/api/services", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<string> FindTracesAsync(string service, string operation = "", int limit = 20, CancellationToken ct = default)
    {
        var query = $"service={Uri.EscapeDataString(service)}&limit={limit}";
        if (!string.IsNullOrEmpty(operation)) query += $"&operation={Uri.EscapeDataString(operation)}";
        using var response = await _httpClient.GetAsync($"{QueryUrl}/api/traces?{query}", ct);
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
