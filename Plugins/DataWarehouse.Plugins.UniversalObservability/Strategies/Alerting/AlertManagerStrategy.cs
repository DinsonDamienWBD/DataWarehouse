using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Alerting;

/// <summary>
/// Observability strategy for Prometheus AlertManager.
/// Provides alert grouping, inhibition, silencing, and routing to notification channels.
/// </summary>
public sealed class AlertManagerStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _url = "http://localhost:9093";

    public override string StrategyId => "alertmanager";
    public override string Name => "Prometheus AlertManager";

    public AlertManagerStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: false, SupportsTracing: false, SupportsLogging: false,
        SupportsDistributedTracing: false, SupportsAlerting: true,
        SupportedExporters: new[] { "AlertManager", "Prometheus", "Webhook" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Configure(string url)
    {
        _url = url.TrimEnd('/');
    }

    /// <summary>
    /// Posts alerts to AlertManager.
    /// </summary>
    public async Task PostAlertsAsync(IEnumerable<Alert> alerts, CancellationToken ct = default)
    {
        var alertData = alerts.Select(a => new
        {
            labels = new Dictionary<string, string>(a.Labels)
            {
                ["alertname"] = a.AlertName,
                ["severity"] = a.Severity
            },
            annotations = a.Annotations ?? new Dictionary<string, string>(),
            startsAt = a.StartsAt?.ToString("o") ?? DateTime.UtcNow.ToString("o"),
            endsAt = a.EndsAt?.ToString("o"),
            generatorURL = a.GeneratorUrl ?? $"http://datawarehouse/alerts/{a.AlertName}"
        });

        var json = JsonSerializer.Serialize(alertData);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync($"{_url}/api/v2/alerts", content, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Gets current alerts from AlertManager.
    /// </summary>
    public async Task<string> GetAlertsAsync(bool? active = null, bool? silenced = null,
        bool? inhibited = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (active.HasValue) query.Add($"active={active.Value.ToString().ToLowerInvariant()}");
        if (silenced.HasValue) query.Add($"silenced={silenced.Value.ToString().ToLowerInvariant()}");
        if (inhibited.HasValue) query.Add($"inhibited={inhibited.Value.ToString().ToLowerInvariant()}");

        var queryString = query.Count > 0 ? "?" + string.Join("&", query) : "";
        var response = await _httpClient.GetAsync($"{_url}/api/v2/alerts{queryString}", ct);
 response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Creates a silence for alerts matching the given matchers.
    /// </summary>
    public async Task<string> CreateSilenceAsync(string createdBy, string comment,
        Dictionary<string, string> matchers, DateTime startsAt, DateTime endsAt, CancellationToken ct = default)
    {
        var silence = new
        {
            matchers = matchers.Select(m => new { name = m.Key, value = m.Value, isRegex = false }),
            startsAt = startsAt.ToString("o"),
            endsAt = endsAt.ToString("o"),
            createdBy,
            comment
        };

        var json = JsonSerializer.Serialize(silence);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync($"{_url}/api/v2/silences", content, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Gets status of AlertManager.
    /// </summary>
    public async Task<string> GetStatusAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync($"{_url}/api/v2/status", ct);
 response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken ct)
        => throw new NotSupportedException("AlertManager does not support metrics ingestion");

    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken ct)
        => throw new NotSupportedException("AlertManager does not support tracing");

    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        IncrementCounter("alert_manager.logs_sent");
        var alerts = logEntries.Where(e => e.Level >= LogLevel.Error).Select(e => new Alert
        {
            AlertName = "datawarehouse_error",
            Severity = e.Level == LogLevel.Critical ? "critical" : "error",
            Labels = new Dictionary<string, string> { ["source"] = "datawarehouse", ["level"] = e.Level.ToString() },
            Annotations = new Dictionary<string, string> { ["summary"] = e.Message, ["description"] = e.Exception?.ToString() ?? "" },
            StartsAt = e.Timestamp.UtcDateTime
        });

        if (alerts.Any()) await PostAlertsAsync(alerts, cancellationToken);
    }

    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_url}/-/healthy", ct);
            return new HealthCheckResult(response.IsSuccessStatusCode,
                response.IsSuccessStatusCode ? "AlertManager is healthy" : "AlertManager unhealthy",
                new Dictionary<string, object> { ["url"] = _url });
        }
        catch (Exception ex) { return new HealthCheckResult(false, $"AlertManager health check failed: {ex.Message}", null); }
    }


    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_url) || (!_url.StartsWith("http://") && !_url.StartsWith("https://")))
            throw new InvalidOperationException("AlertManagerStrategy: Invalid endpoint URL configured.");
        IncrementCounter("alert_manager.initialized");
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
        IncrementCounter("alert_manager.shutdown");
        await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing) { if (disposing) _httpClient.Dispose(); base.Dispose(disposing); }

    public class Alert
    {
        public string AlertName { get; set; } = "";
        public string Severity { get; set; } = "warning";
        public Dictionary<string, string> Labels { get; set; } = new();
        public Dictionary<string, string>? Annotations { get; set; }
        public DateTime? StartsAt { get; set; }
        public DateTime? EndsAt { get; set; }
        public string? GeneratorUrl { get; set; }
    }
}
