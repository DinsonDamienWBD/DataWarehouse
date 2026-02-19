using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Metrics;

/// <summary>
/// Observability strategy for Telegraf metrics agent.
/// Provides HTTP, Socket, and InfluxDB protocol support for metric ingestion.
/// </summary>
/// <remarks>
/// Telegraf is an agent for collecting, processing, aggregating, and writing metrics.
/// It supports a wide variety of input and output plugins.
/// </remarks>
public sealed class TelegrafStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _url = "http://localhost:8186/telegraf";
    private string _database = "telegraf";
    private OutputFormat _format = OutputFormat.InfluxLineProtocol;

    /// <summary>
    /// Output format for Telegraf.
    /// </summary>
    public enum OutputFormat
    {
        /// <summary>InfluxDB Line Protocol format.</summary>
        InfluxLineProtocol,
        /// <summary>JSON format for HTTP listener.</summary>
        Json,
        /// <summary>Graphite plaintext format.</summary>
        Graphite
    }

    /// <inheritdoc/>
    public override string StrategyId => "telegraf";

    /// <inheritdoc/>
    public override string Name => "Telegraf";

    /// <summary>
    /// Initializes a new instance of the <see cref="TelegrafStrategy"/> class.
    /// </summary>
    public TelegrafStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: false,
        SupportsLogging: false,
        SupportsDistributedTracing: false,
        SupportsAlerting: false,
        SupportedExporters: new[] { "Telegraf", "InfluxLineProtocol", "JSON" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the Telegraf connection.
    /// </summary>
    /// <param name="url">Telegraf HTTP listener URL.</param>
    /// <param name="database">Database name (used in InfluxDB format).</param>
    /// <param name="format">Output format.</param>
    public void Configure(string url, string database = "telegraf", OutputFormat format = OutputFormat.InfluxLineProtocol)
    {
        _url = url.TrimEnd('/');
        _database = database;
        _format = format;
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("telegraf.metrics_sent");
        var content = _format switch
        {
            OutputFormat.InfluxLineProtocol => FormatAsInfluxLineProtocol(metrics),
            OutputFormat.Json => FormatAsJson(metrics),
            OutputFormat.Graphite => FormatAsGraphite(metrics),
            _ => FormatAsInfluxLineProtocol(metrics)
        };

        var contentType = _format == OutputFormat.Json ? "application/json" : "text/plain";
        var httpContent = new StringContent(content, Encoding.UTF8, contentType);

        var response = await _httpClient.PostAsync(_url, httpContent, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private string FormatAsInfluxLineProtocol(IEnumerable<MetricValue> metrics)
    {
        var sb = new StringBuilder();

        foreach (var metric in metrics)
        {
            var measurement = EscapeMeasurement(metric.Name);
            var tags = BuildTags(metric.Labels);
            var fields = BuildFields(metric);
            var timestamp = metric.Timestamp.ToUnixTimeMilliseconds() * 1_000_000;

            sb.AppendLine($"{measurement}{tags} {fields} {timestamp}");
        }

        return sb.ToString();
    }

    private string FormatAsJson(IEnumerable<MetricValue> metrics)
    {
        var telegrafMetrics = metrics.Select(m => new
        {
            name = m.Name,
            timestamp = m.Timestamp.ToUnixTimeSeconds(),
            fields = new Dictionary<string, object> { ["value"] = m.Value },
            tags = m.Labels?.ToDictionary(l => l.Name, l => l.Value) ?? new Dictionary<string, string>()
        });

        return JsonSerializer.Serialize(new { metrics = telegrafMetrics });
    }

    private string FormatAsGraphite(IEnumerable<MetricValue> metrics)
    {
        var sb = new StringBuilder();

        foreach (var metric in metrics)
        {
            var path = metric.Name.Replace(" ", "_").Replace("-", "_");
            if (metric.Labels != null)
            {
                foreach (var label in metric.Labels)
                {
                    path += $".{label.Name}_{label.Value}".Replace(" ", "_");
                }
            }

            var timestamp = metric.Timestamp.ToUnixTimeSeconds();
            sb.AppendLine($"{path} {metric.Value} {timestamp}");
        }

        return sb.ToString();
    }

    private static string BuildTags(IReadOnlyList<MetricLabel>? labels)
    {
        if (labels == null || labels.Count == 0)
            return "";

        var tags = new StringBuilder();
        foreach (var label in labels)
        {
            tags.Append(',');
            tags.Append(EscapeTag(label.Name));
            tags.Append('=');
            tags.Append(EscapeTag(label.Value));
        }

        return tags.ToString();
    }

    private static string BuildFields(MetricValue metric)
    {
        var fields = new StringBuilder();
        fields.Append("value=");
        fields.Append(metric.Value);

        if (!string.IsNullOrEmpty(metric.Unit))
        {
            fields.Append(",unit=\"");
            fields.Append(EscapeFieldString(metric.Unit));
            fields.Append('"');
        }

        fields.Append(",type=\"");
        fields.Append(metric.Type.ToString().ToLowerInvariant());
        fields.Append('"');

        return fields.ToString();
    }

    private static string EscapeMeasurement(string measurement)
    {
        return measurement.Replace(",", "\\,").Replace(" ", "\\ ").Replace("\n", "");
    }

    private static string EscapeTag(string tag)
    {
        return tag.Replace(",", "\\,").Replace("=", "\\=").Replace(" ", "\\ ").Replace("\n", "");
    }

    private static string EscapeFieldString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Telegraf does not support tracing");
    }

    /// <inheritdoc/>
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Telegraf does not support logging");
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            // Send a test metric
            var testMetric = MetricValue.Gauge("telegraf.health_check", 1);
            await MetricsAsyncCore(new[] { testMetric }, cancellationToken);

            return new HealthCheckResult(
                IsHealthy: true,
                Description: "Telegraf connection is healthy",
                Data: new Dictionary<string, object>
                {
                    ["url"] = _url,
                    ["format"] = _format.ToString(),
                    ["database"] = _database
                });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: $"Telegraf health check failed: {ex.Message}",
                Data: null);
        }
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_url) || (!_url.StartsWith("http://") && !_url.StartsWith("https://")))
            throw new InvalidOperationException("TelegrafStrategy: Invalid endpoint URL configured.");
        IncrementCounter("telegraf.initialized");
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
        IncrementCounter("telegraf.shutdown");
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
