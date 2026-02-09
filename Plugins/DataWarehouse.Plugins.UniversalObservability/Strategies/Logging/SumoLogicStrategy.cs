using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Logging;

/// <summary>
/// Observability strategy for Sumo Logic cloud-native machine data analytics.
/// Provides log management, metrics, security analytics, and real-time insights.
/// </summary>
/// <remarks>
/// Sumo Logic is a cloud-native platform for log management, infrastructure metrics,
/// and security analytics with machine learning-powered insights.
/// </remarks>
public sealed class SumoLogicStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _collectorUrl = "";
    private string _sourceName = "datawarehouse";
    private string _sourceHost = Environment.MachineName;

    /// <inheritdoc/>
    public override string StrategyId => "sumologic";

    /// <inheritdoc/>
    public override string Name => "Sumo Logic";

    /// <summary>
    /// Initializes a new instance of the <see cref="SumoLogicStrategy"/> class.
    /// </summary>
    public SumoLogicStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: false,
        SupportsLogging: true,
        SupportsDistributedTracing: false,
        SupportsAlerting: true,
        SupportedExporters: new[] { "SumoLogic", "HTTP" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the Sumo Logic HTTP source.
    /// </summary>
    /// <param name="collectorUrl">HTTP source collector URL.</param>
    /// <param name="sourceName">Source name for identification.</param>
    /// <param name="sourceHost">Source host name.</param>
    public void Configure(string collectorUrl, string sourceName = "datawarehouse", string? sourceHost = null)
    {
        _collectorUrl = collectorUrl;
        _sourceName = sourceName;
        _sourceHost = sourceHost ?? Environment.MachineName;
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        // Sumo Logic accepts metrics as log events with specific format
        var metricLogs = metrics.Select(m => new
        {
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            source = _sourceName,
            sourceHost = _sourceHost,
            sourceCategory = "metrics",
            metricName = m.Name,
            metricValue = m.Value,
            metricType = m.Type.ToString(),
            labels = m.Labels?.ToDictionary(l => l.Name, l => l.Value)
        }).ToList();

        await SendToSumoLogicAsync(metricLogs, cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Sumo Logic does not support direct tracing - use OpenTelemetry integration instead");
    }

    /// <inheritdoc/>
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        var logs = logEntries.Select(log => new
        {
            timestamp = log.Timestamp.ToUnixTimeMilliseconds(),
            source = _sourceName,
            sourceHost = _sourceHost,
            sourceCategory = log.Properties?.GetValueOrDefault("Category")?.ToString() ?? "application",
            level = log.Level.ToString(),
            message = log.Message,
            eventId = log.Properties?.GetValueOrDefault("EventId")?.ToString(),
            exception = log.Exception?.ToString(),
            logSource = log.Properties?.GetValueOrDefault("Source")?.ToString()
        }).ToList();

        await SendToSumoLogicAsync(logs, cancellationToken);
    }

    private async Task SendToSumoLogicAsync(IEnumerable<object> data, CancellationToken ct)
    {
        try
        {
            // Sumo Logic accepts newline-delimited JSON
            var sb = new StringBuilder();
            foreach (var item in data)
            {
                sb.AppendLine(JsonSerializer.Serialize(item));
            }

            var content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json");

            // Add custom headers
            content.Headers.Add("X-Sumo-Name", _sourceName);
            content.Headers.Add("X-Sumo-Host", _sourceHost);

            var response = await _httpClient.PostAsync(_collectorUrl, content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException)
        {
            // Sumo Logic unavailable - data lost
        }
    }

    /// <summary>
    /// Sends a custom event to Sumo Logic.
    /// </summary>
    /// <param name="message">Event message.</param>
    /// <param name="category">Source category.</param>
    /// <param name="additionalFields">Optional additional fields.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SendEventAsync(
        string message,
        string category = "application",
        Dictionary<string, object>? additionalFields = null,
        CancellationToken ct = default)
    {
        var logEvent = new Dictionary<string, object>
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["source"] = _sourceName,
            ["sourceHost"] = _sourceHost,
            ["sourceCategory"] = category,
            ["message"] = message
        };

        if (additionalFields != null)
        {
            foreach (var (key, value) in additionalFields)
            {
                logEvent[key] = value;
            }
        }

        await SendToSumoLogicAsync(new[] { logEvent }, ct);
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            // Send a test event
            var testEvent = new
            {
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                source = _sourceName,
                sourceHost = _sourceHost,
                sourceCategory = "health-check",
                message = "Sumo Logic health check"
            };

            var json = JsonSerializer.Serialize(testEvent);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            content.Headers.Add("X-Sumo-Name", _sourceName);
            content.Headers.Add("X-Sumo-Host", _sourceHost);

            var response = await _httpClient.PostAsync(_collectorUrl, content, cancellationToken);

            return new HealthCheckResult(
                IsHealthy: response.IsSuccessStatusCode,
                Description: response.IsSuccessStatusCode ? "Sumo Logic is healthy" : "Sumo Logic unhealthy",
                Data: new Dictionary<string, object>
                {
                    ["sourceName"] = _sourceName,
                    ["sourceHost"] = _sourceHost,
                    ["hasCollectorUrl"] = !string.IsNullOrEmpty(_collectorUrl)
                });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: $"Sumo Logic health check failed: {ex.Message}",
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
