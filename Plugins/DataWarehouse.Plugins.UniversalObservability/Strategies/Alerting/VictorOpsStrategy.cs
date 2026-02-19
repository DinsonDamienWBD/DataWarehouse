using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Alerting;

/// <summary>
/// Observability strategy for VictorOps (now Splunk On-Call) incident management.
/// Provides real-time incident alerting, on-call scheduling, and team collaboration.
/// </summary>
/// <remarks>
/// VictorOps/Splunk On-Call is an incident response platform that combines alerts,
/// on-call schedules, and collaboration tools for DevOps and IT operations teams.
/// </remarks>
public sealed class VictorOpsStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _apiKey = "";
    private string _routingKey = "datawarehouse";
    private string _restEndpoint = "https://alert.victorops.com/integrations/generic/20131114/alert";

    /// <inheritdoc/>
    public override string StrategyId => "victorops";

    /// <inheritdoc/>
    public override string Name => "VictorOps";

    /// <summary>
    /// Initializes a new instance of the <see cref="VictorOpsStrategy"/> class.
    /// </summary>
    public VictorOpsStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: false,
        SupportsTracing: false,
        SupportsLogging: false,
        SupportsDistributedTracing: false,
        SupportsAlerting: true,
        SupportedExporters: new[] { "VictorOps", "SplunkOnCall" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the VictorOps integration.
    /// </summary>
    /// <param name="apiKey">VictorOps API key.</param>
    /// <param name="routingKey">Routing key for alert destination.</param>
    public void Configure(string apiKey, string routingKey = "datawarehouse")
    {
        _apiKey = apiKey;
        _routingKey = routingKey;
        _restEndpoint = $"https://alert.victorops.com/integrations/generic/20131114/alert/{apiKey}/{routingKey}";
    }

    /// <inheritdoc/>
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("victor_ops.metrics_sent");
        // Convert metrics to alerts when thresholds are exceeded
        var alerts = new List<object>();

        foreach (var metric in metrics)
        {
            // Example: Alert on high values (could be configurable)
            if (metric.Value > 90)
            {
                var alert = new
                {
                    message_type = "CRITICAL",
                    entity_id = $"metric.{metric.Name}",
                    entity_display_name = metric.Name,
                    state_message = $"Metric {metric.Name} has high value: {metric.Value}",
                    monitoring_tool = "DataWarehouse",
                    metric_name = metric.Name,
                    metric_value = metric.Value,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                alerts.Add(alert);
            }
        }

        if (alerts.Any())
        {
            return SendAlertsAsync(alerts, cancellationToken);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("VictorOps does not support tracing");
    }

    /// <inheritdoc/>
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        IncrementCounter("victor_ops.logs_sent");
        // Convert error/critical logs to incidents
        var incidents = new List<object>();

        foreach (var log in logEntries)
        {
            if (log.Level == LogLevel.Error || log.Level == LogLevel.Critical)
            {
                var incident = new
                {
                    message_type = log.Level == LogLevel.Critical ? "CRITICAL" : "WARNING",
                    entity_id = $"log.{log.Properties?.GetValueOrDefault("Source")?.ToString() ?? "unknown"}",
                    entity_display_name = log.Properties?.GetValueOrDefault("Source")?.ToString() ?? "DataWarehouse",
                    state_message = log.Message,
                    monitoring_tool = "DataWarehouse",
                    log_level = log.Level.ToString(),
                    timestamp = log.Timestamp.ToUnixTimeSeconds()
                };

                incidents.Add(incident);
            }
        }

        if (incidents.Any())
        {
            return SendAlertsAsync(incidents, cancellationToken);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Sends an alert to VictorOps.
    /// </summary>
    /// <param name="messageType">Alert severity (INFO, WARNING, CRITICAL, RECOVERY).</param>
    /// <param name="entityId">Unique identifier for the alert entity.</param>
    /// <param name="stateMessage">Description of the alert.</param>
    /// <param name="additionalData">Optional additional context data.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SendAlertAsync(
        string messageType,
        string entityId,
        string stateMessage,
        Dictionary<string, object>? additionalData = null,
        CancellationToken ct = default)
    {
        var alert = new Dictionary<string, object>
        {
            ["message_type"] = messageType,
            ["entity_id"] = entityId,
            ["state_message"] = stateMessage,
            ["monitoring_tool"] = "DataWarehouse",
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        if (additionalData != null)
        {
            foreach (var (key, value) in additionalData)
            {
                alert[key] = value;
            }
        }

        await SendAlertsAsync(new[] { alert }, ct);
    }

    private async Task SendAlertsAsync(IEnumerable<object> alerts, CancellationToken ct)
    {
        try
        {
            foreach (var alert in alerts)
            {
                var json = JsonSerializer.Serialize(alert);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(_restEndpoint, content, ct);
                response.EnsureSuccessStatusCode();
            }
        }
        catch (HttpRequestException)
        {
            // VictorOps unavailable - alerts lost (could implement retry queue)
        }
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            // Send a test INFO alert
            var testAlert = new
            {
                message_type = "INFO",
                entity_id = "health_check",
                state_message = "VictorOps health check",
                monitoring_tool = "DataWarehouse"
            };

            var json = JsonSerializer.Serialize(testAlert);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(_restEndpoint, content, cancellationToken);

            return new HealthCheckResult(
                IsHealthy: response.IsSuccessStatusCode,
                Description: response.IsSuccessStatusCode ? "VictorOps is healthy" : "VictorOps unhealthy",
                Data: new Dictionary<string, object>
                {
                    ["routingKey"] = _routingKey,
                    ["hasApiKey"] = !string.IsNullOrEmpty(_apiKey)
                });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: $"VictorOps health check failed: {ex.Message}",
                Data: null);
        }
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_restEndpoint) || (!_restEndpoint.StartsWith("http://") && !_restEndpoint.StartsWith("https://")))
            throw new InvalidOperationException("VictorOpsStrategy: Invalid endpoint URL configured.");
        IncrementCounter("victor_ops.initialized");
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
        IncrementCounter("victor_ops.shutdown");
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
