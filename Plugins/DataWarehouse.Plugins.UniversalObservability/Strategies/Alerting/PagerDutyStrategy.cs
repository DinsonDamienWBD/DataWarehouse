using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Alerting;

/// <summary>
/// Observability strategy for PagerDuty incident management and alerting.
/// Provides on-call management, incident response, and event-driven alerting.
/// </summary>
public sealed class PagerDutyStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _routingKey = "";
    private string _apiToken = "";

    public override string StrategyId => "pagerduty";
    public override string Name => "PagerDuty";

    public PagerDutyStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: false, SupportsTracing: false, SupportsLogging: false,
        SupportsDistributedTracing: false, SupportsAlerting: true,
        SupportedExporters: new[] { "PagerDuty", "EventsAPI", "ChangeEvents" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Configure(string routingKey, string apiToken = "")
    {
        _routingKey = routingKey;
        _apiToken = apiToken;
        if (!string.IsNullOrEmpty(_apiToken))
        {
            _httpClient.DefaultRequestHeaders.Remove("Authorization");
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Token token={_apiToken}");
        }
    }

    /// <summary>
    /// Triggers an alert/incident in PagerDuty.
    /// </summary>
    public async Task TriggerAlertAsync(string summary, string severity, string source,
        Dictionary<string, object>? customDetails = null, string? dedupKey = null, CancellationToken ct = default)
    {
        var payload = new
        {
            routing_key = _routingKey,
            event_action = "trigger",
            dedup_key = dedupKey ?? Guid.NewGuid().ToString(),
            payload = new
            {
                summary,
                source,
                severity = severity.ToLowerInvariant(), // critical, error, warning, info
                timestamp = DateTime.UtcNow.ToString("o"),
                custom_details = customDetails ?? new Dictionary<string, object>()
            }
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync("https://events.pagerduty.com/v2/enqueue", content, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Acknowledges an existing alert/incident.
    /// </summary>
    public async Task AcknowledgeAlertAsync(string dedupKey, CancellationToken ct = default)
    {
        var payload = new { routing_key = _routingKey, event_action = "acknowledge", dedup_key = dedupKey };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        await _httpClient.PostAsync("https://events.pagerduty.com/v2/enqueue", content, ct);
    }

    /// <summary>
    /// Resolves an existing alert/incident.
    /// </summary>
    public async Task ResolveAlertAsync(string dedupKey, CancellationToken ct = default)
    {
        var payload = new { routing_key = _routingKey, event_action = "resolve", dedup_key = dedupKey };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        await _httpClient.PostAsync("https://events.pagerduty.com/v2/enqueue", content, ct);
    }

    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken ct)
        => throw new NotSupportedException("PagerDuty does not support metrics - use alerting methods");

    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken ct)
        => throw new NotSupportedException("PagerDuty does not support tracing");

    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        IncrementCounter("pager_duty.logs_sent");
        // Convert error/critical logs to alerts
        foreach (var entry in logEntries.Where(e => e.Level >= LogLevel.Error))
        {
            var severity = entry.Level == LogLevel.Critical ? "critical" : "error";
            await TriggerAlertAsync(
                entry.Message,
                severity,
                "datawarehouse",
                entry.Properties?.ToDictionary(p => p.Key, p => p.Value),
                null,
                cancellationToken);
        }
    }

    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct)
    {
        try
        {
            // Test with a change event (doesn't create incidents)
            var payload = new
            {
                routing_key = _routingKey,
                payload = new
                {
                    summary = "Health check test",
                    source = "datawarehouse-health-check",
                    timestamp = DateTime.UtcNow.ToString("o")
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync("https://events.pagerduty.com/v2/change/enqueue", content, ct);

            return new HealthCheckResult(response.IsSuccessStatusCode,
                response.IsSuccessStatusCode ? "PagerDuty is healthy" : "PagerDuty unhealthy",
                new Dictionary<string, object> { ["hasRoutingKey"] = !string.IsNullOrEmpty(_routingKey) });
        }
        catch (Exception ex) { return new HealthCheckResult(false, $"PagerDuty health check failed: {ex.Message}", null); }
    }


    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        // Configuration validated via Configure method
        IncrementCounter("pager_duty.initialized");
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
        IncrementCounter("pager_duty.shutdown");
        await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing) { if (disposing) _httpClient.Dispose(); base.Dispose(disposing); }
}
