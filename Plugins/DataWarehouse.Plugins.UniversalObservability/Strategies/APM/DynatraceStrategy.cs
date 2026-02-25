using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.APM;

/// <summary>
/// Observability strategy for Dynatrace software intelligence platform.
/// Provides AI-powered full-stack monitoring with automatic root cause analysis.
/// </summary>
public sealed class DynatraceStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _environmentUrl = "";
    private string _apiToken = "";
    private string _entityId = "";

    public override string StrategyId => "dynatrace";
    public override string Name => "Dynatrace";

    public DynatraceStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true, SupportsTracing: true, SupportsLogging: true,
        SupportsDistributedTracing: true, SupportsAlerting: true,
        SupportedExporters: new[] { "Dynatrace", "OneAgent", "ActiveGate" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Configure(string environmentUrl, string apiToken, string entityId = "")
    {
        _environmentUrl = environmentUrl.TrimEnd('/');
        _apiToken = apiToken;
        _entityId = entityId;
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Api-Token {_apiToken}");
    }

    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("dynatrace.metrics_sent");
        // Dynatrace Metrics Ingest API (MINT protocol)
        var lines = new StringBuilder();

        foreach (var metric in metrics)
        {
            var metricKey = metric.Name.Replace(" ", "_").Replace("-", "_");
            var dimensions = metric.Labels != null
                ? string.Join(",", metric.Labels.Select(l => $"{l.Name}=\"{l.Value}\""))
                : "";

            var line = string.IsNullOrEmpty(dimensions)
                ? $"{metricKey} {metric.Value} {metric.Timestamp.ToUnixTimeMilliseconds()}"
                : $"{metricKey},{dimensions} {metric.Value} {metric.Timestamp.ToUnixTimeMilliseconds()}";

            lines.AppendLine(line);
        }

        var content = new StringContent(lines.ToString(), Encoding.UTF8, "text/plain");
        var response = await _httpClient.PostAsync($"{_environmentUrl}/api/v2/metrics/ingest", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        IncrementCounter("dynatrace.traces_sent");
        // Dynatrace OpenTelemetry Protocol compatible endpoint
        var traceData = new
        {
            resourceSpans = new[]
            {
                new
                {
                    resource = new
                    {
                        attributes = new[]
                        {
                            new { key = "service.name", value = new { stringValue = "datawarehouse" } },
                            new { key = "host.name", value = new { stringValue = Environment.MachineName } }
                        }
                    },
                    scopeSpans = new[]
                    {
                        new
                        {
                            scope = new { name = "datawarehouse" },
                            spans = spans.Select(s => new
                            {
                                traceId = s.TraceId,
                                spanId = s.SpanId,
                                parentSpanId = s.ParentSpanId,
                                name = s.OperationName,
                                kind = (int)s.Kind + 1,
                                startTimeUnixNano = s.StartTime.ToUnixTimeMilliseconds() * 1_000_000,
                                endTimeUnixNano = s.StartTime.Add(s.Duration).ToUnixTimeMilliseconds() * 1_000_000,
                                attributes = s.Attributes?.Select(a => new { key = a.Key, value = new { stringValue = a.Value?.ToString() ?? "" } }).ToArray(),
                                status = new { code = s.Status == SpanStatus.Error ? 2 : 1 }
                            }).ToArray()
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(traceData);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync($"{_environmentUrl}/api/v2/otlp/v1/traces", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        IncrementCounter("dynatrace.logs_sent");
        // Dynatrace Log Ingest API
        var logs = logEntries.Select(e => new
        {
            content = e.Message,
            timestamp = e.Timestamp.ToString("o"),
            log_source = "datawarehouse",
            severity = e.Level switch
            {
                LogLevel.Trace => "DEBUG",
                LogLevel.Debug => "DEBUG",
                LogLevel.Information => "INFO",
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERROR",
                LogLevel.Critical => "SEVERE",
                _ => "NONE"
            },
            status = e.Level >= LogLevel.Error ? "ERROR" : "OK"
        });

        var json = JsonSerializer.Serialize(logs);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync($"{_environmentUrl}/api/v2/logs/ingest", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_environmentUrl}/api/v2/activeGates", ct);
            return new HealthCheckResult(response.IsSuccessStatusCode,
                response.IsSuccessStatusCode ? "Dynatrace is healthy" : "Dynatrace unhealthy",
                new Dictionary<string, object> { ["environmentUrl"] = _environmentUrl });
        }
        catch (Exception ex) { return new HealthCheckResult(false, $"Dynatrace health check failed: {ex.Message}", null); }
    }


    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_environmentUrl) || (!_environmentUrl.StartsWith("http://") && !_environmentUrl.StartsWith("https://")))
            throw new InvalidOperationException("DynatraceStrategy: Invalid endpoint URL configured.");
        IncrementCounter("dynatrace.initialized");
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
        IncrementCounter("dynatrace.shutdown");
        await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing) { if (disposing) _httpClient.Dispose(); base.Dispose(disposing); }
}
