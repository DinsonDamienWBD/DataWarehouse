using System.Net.Http;
using System.Text;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Metrics;

/// <summary>
/// Observability strategy for InfluxDB time series database.
/// Provides high-performance metrics storage with InfluxQL and Flux query support.
/// </summary>
/// <remarks>
/// InfluxDB is a purpose-built time series database designed for high write and query loads.
/// Supports InfluxDB 2.x API with organizations, buckets, and Flux query language.
/// </remarks>
public sealed class InfluxDbStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _url = "http://localhost:8086";
    private string _token = "";
    private string _org = "datawarehouse";
    private string _bucket = "metrics";

    /// <inheritdoc/>
    public override string StrategyId => "influxdb";

    /// <inheritdoc/>
    public override string Name => "InfluxDB";

    /// <summary>
    /// Initializes a new instance of the <see cref="InfluxDbStrategy"/> class.
    /// </summary>
    public InfluxDbStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: false,
        SupportsLogging: false,
        SupportsDistributedTracing: false,
        SupportsAlerting: true,
        SupportedExporters: new[] { "InfluxDB", "LineProtocol", "Flux" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the InfluxDB connection.
    /// </summary>
    /// <param name="url">InfluxDB server URL.</param>
    /// <param name="token">Authentication token.</param>
    /// <param name="org">Organization name.</param>
    /// <param name="bucket">Bucket name for metrics.</param>
    public void Configure(string url, string token, string org = "datawarehouse", string bucket = "metrics")
    {
        _url = url.TrimEnd('/');
        _token = token;
        _org = org;
        _bucket = bucket;
        // Do NOT set DefaultRequestHeaders — inject per-request to avoid thread-safety issues.
    }

    /// <summary>Adds the InfluxDB token to the request headers (per-request, thread-safe).</summary>
    private void AddToken(HttpRequestMessage request) =>
        request.Headers.Add("Authorization", $"Token {_token}");

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("influx_db.metrics_sent");
        var lineProtocol = new StringBuilder();

        foreach (var metric in metrics)
        {
            // Build InfluxDB Line Protocol
            // measurement,tag1=value1,tag2=value2 field1=value1,field2=value2 timestamp

            var measurement = EscapeMeasurement(metric.Name);
            var tags = new StringBuilder();

            if (metric.Labels != null && metric.Labels.Count > 0)
            {
                foreach (var label in metric.Labels)
                {
                    tags.Append(',');
                    tags.Append(EscapeTag(label.Name));
                    tags.Append('=');
                    tags.Append(EscapeTag(label.Value));
                }
            }

            // Add metric type as tag
            tags.Append(",type=");
            tags.Append(metric.Type.ToString().ToLowerInvariant());

            var fields = new StringBuilder();
            fields.Append("value=");
            fields.Append(metric.Value);

            if (!string.IsNullOrEmpty(metric.Unit))
            {
                fields.Append(",unit=\"");
                fields.Append(EscapeFieldString(metric.Unit));
                fields.Append('"');
            }

            var timestamp = metric.Timestamp.ToUnixTimeMilliseconds();

            lineProtocol.AppendLine($"{measurement}{tags} {fields} {timestamp}");
        }

        var body = new StringContent(lineProtocol.ToString(), Encoding.UTF8, "text/plain");
        var writeUrl = $"{_url}/api/v2/write?org={Uri.EscapeDataString(_org)}&bucket={Uri.EscapeDataString(_bucket)}&precision=ms";
        using var request = new HttpRequestMessage(HttpMethod.Post, writeUrl) { Content = body };
        AddToken(request);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Queries metrics using Flux query language.
    /// </summary>
    /// <param name="fluxQuery">Flux query string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Query results as CSV.</returns>
    public async Task<string> QueryAsync(string fluxQuery, CancellationToken ct = default)
    {
        var url = $"{_url}/api/v2/query?org={Uri.EscapeDataString(_org)}";
        var content = new StringContent(fluxQuery, Encoding.UTF8, "application/vnd.flux");

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        AddToken(request);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/csv"));

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Gets the last N values for a metric.
    /// </summary>
    /// <param name="metricName">Metric name.</param>
    /// <param name="count">Number of values to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Query results.</returns>
    public Task<string> GetLastValuesAsync(string metricName, int count = 10, CancellationToken ct = default)
    {
        // LOW-4651: Escape Flux string literals to prevent query injection.
        // In Flux, backslash is the escape character inside double-quoted strings.
        var escapedBucket = EscapeFluxString(_bucket);
        var escapedMetricName = EscapeFluxString(metricName);
        var query = $@"
from(bucket: ""{escapedBucket}"")
  |> range(start: -1h)
  |> filter(fn: (r) => r._measurement == ""{escapedMetricName}"")
  |> last()
  |> limit(n: {count})";

        return QueryAsync(query, ct);
    }

    /// <summary>
    /// Escapes a value for safe embedding in a Flux double-quoted string literal.
    /// Escapes backslash and double-quote characters as required by the Flux spec.
    /// </summary>
    private static string EscapeFluxString(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string EscapeMeasurement(string measurement)
    {
        return measurement
            .Replace(",", "\\,")
            .Replace(" ", "\\ ")
            .Replace("\n", "")
            .Replace("\r", "");
    }

    private static string EscapeTag(string tag)
    {
        return tag
            .Replace(",", "\\,")
            .Replace("=", "\\=")
            .Replace(" ", "\\ ")
            .Replace("\n", "")
            .Replace("\r", "");
    }

    private static string EscapeFieldString(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("InfluxDB does not support tracing");
    }

    /// <inheritdoc/>
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("InfluxDB does not support logging");
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            using var healthReq = new HttpRequestMessage(HttpMethod.Get, $"{_url}/health");
            AddToken(healthReq);
            using var response = await _httpClient.SendAsync(healthReq, cancellationToken);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            return new HealthCheckResult(
                IsHealthy: response.IsSuccessStatusCode,
                Description: response.IsSuccessStatusCode ? "InfluxDB is healthy" : $"InfluxDB unhealthy: {content}",
                Data: new Dictionary<string, object>
                {
                    ["url"] = _url,
                    ["org"] = _org,
                    ["bucket"] = _bucket
                });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: $"InfluxDB health check failed: {ex.Message}",
                Data: null);
        }
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_url) || (!_url.StartsWith("http://") && !_url.StartsWith("https://")))
            throw new InvalidOperationException("InfluxDbStrategy: Invalid endpoint URL configured.");
        IncrementCounter("influx_db.initialized");
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
        IncrementCounter("influx_db.shutdown");
        await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
                _token = string.Empty;
        if (disposing)
        {
            _httpClient.Dispose();
        }
        base.Dispose(disposing);
    }
}
