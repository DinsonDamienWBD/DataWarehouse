using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Logging;

/// <summary>
/// Observability strategy for Fluentd/Fluent Bit log collection.
/// Provides unified logging layer with support for multiple inputs and outputs.
/// </summary>
/// <remarks>
/// Fluentd is an open source data collector for unified logging layer.
/// It allows you to unify data collection and consumption for better use and understanding of data.
/// </remarks>
public sealed class FluentdStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _url = "http://localhost:9880";
    private string _tag = "datawarehouse";

    /// <inheritdoc/>
    public override string StrategyId => "fluentd";

    /// <inheritdoc/>
    public override string Name => "Fluentd";

    public FluentdStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: false,
        SupportsTracing: false,
        SupportsLogging: true,
        SupportsDistributedTracing: false,
        SupportsAlerting: false,
        SupportedExporters: new[] { "Fluentd", "FluentBit", "Forward" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Configure(string url, string tag = "datawarehouse")
    {
        _url = url.TrimEnd('/');
        _tag = tag;
    }

    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        IncrementCounter("fluentd.logs_sent");
        // LOW-4618: Fluentd HTTP input expects one JSON object per request (or NDJSON), NOT a
        // JSON array — an array is treated as a single event. Post each record individually.
        foreach (var entry in logEntries)
        {
            var record = new
            {
                tag = _tag,
                time = entry.Timestamp.ToUnixTimeSeconds(),
                record = new Dictionary<string, object?>
                {
                    ["level"] = entry.Level.ToString(),
                    ["message"] = entry.Message,
                    ["host"] = Environment.MachineName,
                    ["timestamp"] = entry.Timestamp.ToString("o"),
                    ["properties"] = entry.Properties ?? new Dictionary<string, object>(),
                    ["exception"] = entry.Exception != null ? new
                    {
                        type = entry.Exception.GetType().Name,
                        message = entry.Exception.Message,
                        stacktrace = entry.Exception.StackTrace ?? ""
                    } : (object?)null
                }
            };

            var json = JsonSerializer.Serialize(record);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync($"{_url}/{_tag}", content, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }

    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
        => throw new NotSupportedException("Fluentd does not support metrics");

    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
        => throw new NotSupportedException("Fluentd does not support tracing");

    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{_url}/api/plugins.json", cancellationToken);
            return new HealthCheckResult(response.IsSuccessStatusCode,
                response.IsSuccessStatusCode ? "Fluentd is healthy" : "Fluentd unhealthy",
                new Dictionary<string, object> { ["url"] = _url, ["tag"] = _tag });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(false, $"Fluentd health check failed: {ex.Message}", null);
        }
    }


    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_url) || (!_url.StartsWith("http://") && !_url.StartsWith("https://")))
            throw new InvalidOperationException("FluentdStrategy: Invalid endpoint URL configured.");
        IncrementCounter("fluentd.initialized");
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
        IncrementCounter("fluentd.shutdown");
        await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing) { if (disposing) _httpClient.Dispose(); base.Dispose(disposing); }
}
