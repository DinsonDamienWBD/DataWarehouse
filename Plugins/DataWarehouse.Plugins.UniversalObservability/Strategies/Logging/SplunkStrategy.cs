using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Logging;

/// <summary>
/// Observability strategy for Splunk logging and analytics platform.
/// Provides enterprise-grade log management with powerful search (SPL) and visualization.
/// </summary>
/// <remarks>
/// Splunk is a leading platform for searching, monitoring, and analyzing
/// machine-generated data via a web-style interface.
/// </remarks>
public sealed class SplunkStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _hecUrl = "http://localhost:8088";
    private string _hecToken = "";
    private string _index = "main";
    private string _source = "datawarehouse";
    private string _sourcetype = "_json";

    /// <inheritdoc/>
    public override string StrategyId => "splunk";

    /// <inheritdoc/>
    public override string Name => "Splunk";

    /// <summary>
    /// Initializes a new instance of the <see cref="SplunkStrategy"/> class.
    /// </summary>
    public SplunkStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: false,
        SupportsLogging: true,
        SupportsDistributedTracing: false,
        SupportsAlerting: true,
        SupportedExporters: new[] { "SplunkHEC", "SplunkForwarder", "SplunkAPI" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the Splunk HEC (HTTP Event Collector) connection.
    /// </summary>
    /// <param name="hecUrl">Splunk HEC endpoint URL.</param>
    /// <param name="hecToken">HEC authentication token.</param>
    /// <param name="index">Splunk index name.</param>
    /// <param name="source">Event source identifier.</param>
    /// <param name="sourcetype">Event sourcetype.</param>
    public void Configure(string hecUrl, string hecToken, string index = "main",
        string source = "datawarehouse", string sourcetype = "_json")
    {
        _hecUrl = hecUrl.TrimEnd('/');
        _hecToken = hecToken;
        _index = index;
        _source = source;
        _sourcetype = sourcetype;

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Splunk {_hecToken}");
    }

    /// <inheritdoc/>
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        var events = new StringBuilder();

        foreach (var entry in logEntries)
        {
            var eventObj = new
            {
                time = entry.Timestamp.ToUnixTimeSeconds() + (entry.Timestamp.Millisecond / 1000.0),
                host = Environment.MachineName,
                source = _source,
                sourcetype = _sourcetype,
                index = _index,
                @event = new Dictionary<string, object?>
                {
                    ["level"] = entry.Level.ToString(),
                    ["message"] = entry.Message,
                    ["properties"] = entry.Properties ?? new Dictionary<string, object>(),
                    ["exception"] = entry.Exception != null ? new
                    {
                        type = entry.Exception.GetType().Name,
                        message = entry.Exception.Message,
                        stacktrace = entry.Exception.StackTrace ?? ""
                    } : null
                }
            };

            events.AppendLine(JsonSerializer.Serialize(eventObj));
        }

        var content = new StringContent(events.ToString(), Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync($"{_hecUrl}/services/collector/event", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        // Splunk HEC supports metrics via the /services/collector endpoint
        var metricEvents = new StringBuilder();

        foreach (var metric in metrics)
        {
            var dimensions = metric.Labels?.ToDictionary(l => l.Name, l => l.Value)
                ?? new Dictionary<string, string>();

            var metricObj = new
            {
                time = metric.Timestamp.ToUnixTimeSeconds() + (metric.Timestamp.Millisecond / 1000.0),
                host = Environment.MachineName,
                source = _source,
                index = _index,
                @event = "metric",
                fields = new Dictionary<string, object>(dimensions.Select(d => new KeyValuePair<string, object>(d.Key, d.Value)))
                {
                    [$"metric_name:{metric.Name}"] = metric.Value,
                    ["_value"] = metric.Value
                }
            };

            metricEvents.AppendLine(JsonSerializer.Serialize(metricObj));
        }

        var content = new StringContent(metricEvents.ToString(), Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync($"{_hecUrl}/services/collector/event", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Sends raw events to Splunk HEC.
    /// </summary>
    /// <param name="rawData">Raw event data.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SendRawAsync(string rawData, CancellationToken ct = default)
    {
        var content = new StringContent(rawData, Encoding.UTF8, "text/plain");
        var response = await _httpClient.PostAsync(
            $"{_hecUrl}/services/collector/raw?index={_index}&source={_source}&sourcetype={_sourcetype}",
            content, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Gets HEC health status.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Health status as JSON.</returns>
    public async Task<string> GetHecHealthAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync($"{_hecUrl}/services/collector/health", ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Splunk does not directly support tracing - use Splunk APM");
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_hecUrl}/services/collector/health", cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            return new HealthCheckResult(
                IsHealthy: response.IsSuccessStatusCode,
                Description: response.IsSuccessStatusCode ? "Splunk HEC is healthy" : $"Splunk HEC unhealthy: {content}",
                Data: new Dictionary<string, object>
                {
                    ["hecUrl"] = _hecUrl,
                    ["index"] = _index,
                    ["source"] = _source
                });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: $"Splunk health check failed: {ex.Message}",
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
