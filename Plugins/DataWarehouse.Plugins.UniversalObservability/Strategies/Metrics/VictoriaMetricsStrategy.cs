using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Metrics;

/// <summary>
/// Observability strategy for Victoria Metrics time series database.
/// Provides Prometheus-compatible metrics ingestion with high performance and low resource usage.
/// </summary>
/// <remarks>
/// Victoria Metrics is a fast, cost-effective, and scalable time series database
/// that is compatible with Prometheus ecosystem (PromQL, remote write, scraping).
/// </remarks>
public sealed class VictoriaMetricsStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _url = "http://localhost:8428";
    private string _tenant = "0:0"; // Cluster mode tenant

    /// <inheritdoc/>
    public override string StrategyId => "victoria-metrics";

    /// <inheritdoc/>
    public override string Name => "Victoria Metrics";

    /// <summary>
    /// Initializes a new instance of the <see cref="VictoriaMetricsStrategy"/> class.
    /// </summary>
    public VictoriaMetricsStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: false,
        SupportsLogging: false,
        SupportsDistributedTracing: false,
        SupportsAlerting: true,
        SupportedExporters: new[] { "VictoriaMetrics", "Prometheus", "InfluxDB", "Graphite", "OpenTSDB" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the Victoria Metrics connection.
    /// </summary>
    /// <param name="url">Victoria Metrics server URL.</param>
    /// <param name="tenant">Tenant ID for cluster mode (format: "accountID:projectID").</param>
    public void Configure(string url, string tenant = "0:0")
    {
        _url = url.TrimEnd('/');
        _tenant = tenant;
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("victoria_metrics.metrics_sent");
        // Victoria Metrics supports multiple import formats
        // Using Prometheus exposition format for compatibility
        await ImportPrometheusFormatAsync(metrics, cancellationToken);
    }

    private async Task ImportPrometheusFormatAsync(IEnumerable<MetricValue> metrics, CancellationToken ct)
    {
        var sb = new StringBuilder();

        foreach (var metric in metrics)
        {
            var metricName = SanitizeMetricName(metric.Name);
            var labels = FormatLabels(metric.Labels);

            // Add TYPE comment
            var typeComment = metric.Type switch
            {
                MetricType.Counter => "counter",
                MetricType.Gauge => "gauge",
                MetricType.Histogram => "histogram",
                MetricType.Summary => "summary",
                _ => "gauge"
            };
            sb.AppendLine($"# TYPE {metricName} {typeComment}");

            // Add metric line with timestamp
            var timestamp = metric.Timestamp.ToUnixTimeMilliseconds();
            sb.AppendLine($"{metricName}{labels} {metric.Value} {timestamp}");
        }

        var content = new StringContent(sb.ToString(), Encoding.UTF8, "text/plain");
        var response = await _httpClient.PostAsync($"{_url}/api/v1/import/prometheus", content, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Imports metrics using InfluxDB line protocol (alternative format).
    /// </summary>
    /// <param name="metrics">Metrics to import.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ImportInfluxLineProtocolAsync(IEnumerable<MetricValue> metrics, CancellationToken ct = default)
    {
        var sb = new StringBuilder();

        foreach (var metric in metrics)
        {
            var measurement = metric.Name.Replace(" ", "_").Replace("-", "_");
            var tags = metric.Labels != null && metric.Labels.Count > 0
                ? "," + string.Join(",", metric.Labels.Select(l => $"{l.Name}={l.Value}"))
                : "";
            var timestamp = metric.Timestamp.ToUnixTimeMilliseconds() * 1_000_000;

            sb.AppendLine($"{measurement}{tags} value={metric.Value} {timestamp}");
        }

        var content = new StringContent(sb.ToString(), Encoding.UTF8, "text/plain");
        var response = await _httpClient.PostAsync($"{_url}/write", content, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Queries metrics using PromQL.
    /// </summary>
    /// <param name="query">PromQL query string.</param>
    /// <param name="time">Optional evaluation timestamp.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Query result as JSON.</returns>
    public async Task<string> QueryAsync(string query, DateTimeOffset? time = null, CancellationToken ct = default)
    {
        var queryParams = $"query={Uri.EscapeDataString(query)}";
        if (time.HasValue)
        {
            queryParams += $"&time={time.Value.ToUnixTimeSeconds()}";
        }

        var response = await _httpClient.GetAsync($"{_url}/api/v1/query?{queryParams}", ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Queries metrics over a time range using PromQL.
    /// </summary>
    /// <param name="query">PromQL query string.</param>
    /// <param name="start">Start time.</param>
    /// <param name="end">End time.</param>
    /// <param name="step">Query resolution step.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Query result as JSON.</returns>
    public async Task<string> QueryRangeAsync(string query, DateTimeOffset start, DateTimeOffset end,
        TimeSpan step, CancellationToken ct = default)
    {
        var queryParams = $"query={Uri.EscapeDataString(query)}" +
                         $"&start={start.ToUnixTimeSeconds()}" +
                         $"&end={end.ToUnixTimeSeconds()}" +
                         $"&step={step.TotalSeconds}s";

        var response = await _httpClient.GetAsync($"{_url}/api/v1/query_range?{queryParams}", ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Gets the list of available metric names.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of metric names.</returns>
    public async Task<string[]> GetMetricNamesAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync($"{_url}/api/v1/label/__name__/values", ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<JsonElement>(json);

        if (result.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            return data.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
        }

        return Array.Empty<string>();
    }

    private static string SanitizeMetricName(string name)
    {
        return name.Replace("-", "_").Replace(".", "_").Replace(" ", "_").ToLowerInvariant();
    }

    private static string FormatLabels(IReadOnlyList<MetricLabel>? labels)
    {
        if (labels == null || labels.Count == 0)
            return "";

        var labelPairs = labels.Select(l =>
            $"{SanitizeMetricName(l.Name)}=\"{EscapeLabelValue(l.Value)}\"");

        return "{" + string.Join(",", labelPairs) + "}";
    }

    private static string EscapeLabelValue(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Victoria Metrics does not support tracing");
    }

    /// <inheritdoc/>
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Victoria Metrics does not support logging");
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_url}/health", cancellationToken);

            return new HealthCheckResult(
                IsHealthy: response.IsSuccessStatusCode,
                Description: response.IsSuccessStatusCode ? "Victoria Metrics is healthy" : "Victoria Metrics unhealthy",
                Data: new Dictionary<string, object>
                {
                    ["url"] = _url,
                    ["tenant"] = _tenant
                });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: $"Victoria Metrics health check failed: {ex.Message}",
                Data: null);
        }
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_url) || (!_url.StartsWith("http://") && !_url.StartsWith("https://")))
            throw new InvalidOperationException("VictoriaMetricsStrategy: Invalid endpoint URL configured.");
        IncrementCounter("victoria_metrics.initialized");
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
        IncrementCounter("victoria_metrics.shutdown");
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
