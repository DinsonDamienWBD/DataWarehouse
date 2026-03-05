using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Alerting;

/// <summary>
/// Observability strategy for Atlassian OpsGenie alerting and incident management.
/// Provides on-call scheduling, alert routing, and incident response automation.
/// </summary>
public sealed class OpsGenieStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _apiKey = "";
    private string _region = "us"; // us or eu

    public override string StrategyId => "opsgenie";
    public override string Name => "OpsGenie";

    public OpsGenieStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: false, SupportsTracing: false, SupportsLogging: false,
        SupportsDistributedTracing: false, SupportsAlerting: true,
        SupportedExporters: new[] { "OpsGenie", "AlertAPI", "IncidentAPI" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Configure(string apiKey, string region = "us")
    {
        _apiKey = apiKey;
        _region = region;
        // Do NOT set DefaultRequestHeaders — inject per-request to avoid thread-safety issues.
    }

    /// <summary>Adds the OpsGenie API key to the request headers (per-request, thread-safe).</summary>
    private void AddApiKey(HttpRequestMessage request) =>
        request.Headers.Add("Authorization", $"GenieKey {_apiKey}");

    private string GetBaseUrl() => _region.ToLowerInvariant() == "eu"
        ? "https://api.eu.opsgenie.com/v2"
        : "https://api.opsgenie.com/v2";

    /// <summary>
    /// Creates an alert in OpsGenie.
    /// </summary>
    public async Task CreateAlertAsync(string message, string priority = "P3", string? alias = null,
        string? description = null, Dictionary<string, string>? details = null,
        string[]? tags = null, CancellationToken ct = default)
    {
        var alert = new
        {
            message,
            alias = alias ?? Guid.NewGuid().ToString(),
            description,
            priority, // P1 (Critical) to P5 (Informational)
            source = "datawarehouse",
            tags = tags ?? Array.Empty<string>(),
            details = details ?? new Dictionary<string, string>()
        };

        var json = JsonSerializer.Serialize(alert);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetBaseUrl()}/alerts") { Content = content };
        AddApiKey(request);
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Acknowledges an alert.
    /// </summary>
    public async Task AcknowledgeAlertAsync(string alias, string? note = null, CancellationToken ct = default)
    {
        var payload = new { note };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetBaseUrl()}/alerts/{alias}/acknowledge?identifierType=alias") { Content = content };
        AddApiKey(request);
        await _httpClient.SendAsync(request, ct);
    }

    /// <summary>
    /// Closes an alert.
    /// </summary>
    public async Task CloseAlertAsync(string alias, string? note = null, CancellationToken ct = default)
    {
        var payload = new { note };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetBaseUrl()}/alerts/{alias}/close?identifierType=alias") { Content = content };
        AddApiKey(request);
        await _httpClient.SendAsync(request, ct);
    }

    /// <summary>
    /// Adds a note to an alert.
    /// </summary>
    public async Task AddNoteAsync(string alias, string note, CancellationToken ct = default)
    {
        var payload = new { note };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetBaseUrl()}/alerts/{alias}/notes?identifierType=alias") { Content = content };
        AddApiKey(request);
        await _httpClient.SendAsync(request, ct);
    }

    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken ct)
        => throw new NotSupportedException("OpsGenie does not support metrics");

    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken ct)
        => throw new NotSupportedException("OpsGenie does not support tracing");

    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        IncrementCounter("ops_genie.logs_sent");
        foreach (var entry in logEntries.Where(e => e.Level >= LogLevel.Error))
        {
            var priority = entry.Level == LogLevel.Critical ? "P1" : entry.Level == LogLevel.Error ? "P2" : "P3";
            await CreateAlertAsync(
                entry.Message,
                priority,
                null,
                entry.Exception?.ToString(),
                entry.Properties?.ToDictionary(p => p.Key, p => p.Value?.ToString() ?? ""),
                new[] { entry.Level.ToString().ToLowerInvariant() },
                cancellationToken);
        }
    }

    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct)
    {
        try
        {
            using var hcRequest = new HttpRequestMessage(HttpMethod.Get, $"{GetBaseUrl()}/heartbeats");
            AddApiKey(hcRequest);
            using var response = await _httpClient.SendAsync(hcRequest, ct);
            return new HealthCheckResult(response.IsSuccessStatusCode,
                response.IsSuccessStatusCode ? "OpsGenie is healthy" : "OpsGenie unhealthy",
                new Dictionary<string, object> { ["region"] = _region });
        }
        catch (Exception ex) { return new HealthCheckResult(false, $"OpsGenie health check failed: {ex.Message}", null); }
    }


    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        // Configuration validated via Configure method
        IncrementCounter("ops_genie.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }


    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        // Finding 4584: removed decorative Task.Delay(100ms) — no real in-flight queue to drain.
        IncrementCounter("ops_genie.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }

    protected override void Dispose(bool disposing) {
                _apiKey = string.Empty; if (disposing) _httpClient.Dispose(); base.Dispose(disposing); }
}
