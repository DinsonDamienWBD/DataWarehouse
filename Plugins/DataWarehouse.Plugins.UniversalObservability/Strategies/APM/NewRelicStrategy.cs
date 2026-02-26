using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.APM;

/// <summary>
/// Observability strategy for New Relic APM and telemetry platform.
/// Provides full-stack observability with metrics, traces, logs, and AI-powered insights.
/// </summary>
public sealed class NewRelicStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _licenseKey = "";
    private string _accountId = "";
    private string _region = "US"; // US or EU
    private string _serviceName = "datawarehouse";

    public override string StrategyId => "newrelic";
    public override string Name => "New Relic";

    public NewRelicStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true, SupportsTracing: true, SupportsLogging: true,
        SupportsDistributedTracing: true, SupportsAlerting: true,
        SupportedExporters: new[] { "NewRelic", "OTLP", "NewRelicMetrics" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Configure(string licenseKey, string accountId, string region = "US", string serviceName = "datawarehouse")
    {
        _licenseKey = licenseKey;
        _accountId = accountId;
        _region = region;
        _serviceName = serviceName;
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Remove("Api-Key");
        _httpClient.DefaultRequestHeaders.Add("Api-Key", _licenseKey);
    }

    private string GetEndpoint(string type) => _region.ToUpperInvariant() == "EU"
        ? $"https://{type}-api.eu.newrelic.com"
        : $"https://{type}-api.newrelic.com";

    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("new_relic.metrics_sent");
        var metricPayload = new[]
        {
            new
            {
                metrics = metrics.Select(m => new
                {
                    name = m.Name,
                    type = m.Type switch { MetricType.Counter => "count", MetricType.Gauge => "gauge", _ => "gauge" },
                    value = m.Value,
                    timestamp = m.Timestamp.ToUnixTimeMilliseconds(),
                    attributes = m.Labels?.ToDictionary(l => l.Name, l => (object)l.Value) ?? new Dictionary<string, object>()
                }).ToArray()
            }
        };

        var json = JsonSerializer.Serialize(metricPayload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync($"{GetEndpoint("metric")}/metric/v1", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        IncrementCounter("new_relic.traces_sent");
        var tracePayload = new[]
        {
            new
            {
                common = new { attributes = new { service = new { name = _serviceName } } },
                spans = spans.Select(s => new
                {
                    trace_id = s.TraceId,
                    id = s.SpanId,
                    attributes = new Dictionary<string, object>(s.Attributes ?? new Dictionary<string, object>())
                    {
                        ["name"] = s.OperationName,
                        ["parent.id"] = s.ParentSpanId ?? "",
                        ["duration.ms"] = s.Duration.TotalMilliseconds,
                        ["service.name"] = _serviceName
                    },
                    timestamp = s.StartTime.ToUnixTimeMilliseconds()
                }).ToArray()
            }
        };

        var json = JsonSerializer.Serialize(tracePayload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync($"{GetEndpoint("trace")}/trace/v1", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        IncrementCounter("new_relic.logs_sent");
        var logPayload = new[]
        {
            new
            {
                common = new { attributes = new { service = _serviceName, hostname = Environment.MachineName } },
                logs = logEntries.Select(e => new
                {
                    timestamp = e.Timestamp.ToUnixTimeMilliseconds(),
                    message = e.Message,
                    attributes = new Dictionary<string, object>(e.Properties ?? new Dictionary<string, object>())
                    {
                        ["level"] = e.Level.ToString(),
                        ["exception.type"] = e.Exception?.GetType().Name ?? "",
                        ["exception.message"] = e.Exception?.Message ?? ""
                    }
                }).ToArray()
            }
        };

        var json = JsonSerializer.Serialize(logPayload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync($"{GetEndpoint("log")}/log/v1", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct)
    {
        try
        {
            var testMetric = MetricValue.Gauge("newrelic.health_check", 1);
            await MetricsAsyncCore(new[] { testMetric }, ct);
            return new HealthCheckResult(true, "New Relic connection is healthy",
                new Dictionary<string, object> { ["region"] = _region, ["accountId"] = _accountId });
        }
        catch (Exception ex) { return new HealthCheckResult(false, $"New Relic health check failed: {ex.Message}", null); }
    }


    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        // Configuration validated via Configure method
        IncrementCounter("new_relic.initialized");
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
        IncrementCounter("new_relic.shutdown");
        await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing) { if (disposing) _httpClient.Dispose(); base.Dispose(disposing); }
}
