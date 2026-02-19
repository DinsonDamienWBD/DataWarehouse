using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Alerting;

/// <summary>
/// Observability strategy for Sensu monitoring and alerting platform.
/// Provides multi-cloud monitoring, event-driven automation, and flexible alerting.
/// </summary>
/// <remarks>
/// Sensu is a comprehensive infrastructure and application monitoring solution
/// that provides flexible event processing, multi-cloud support, and automated remediation.
/// </remarks>
public sealed class SensuStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _apiUrl = "http://localhost:8080";
    private string _apiKey = "";
    private string _namespace = "default";

    /// <inheritdoc/>
    public override string StrategyId => "sensu";

    /// <inheritdoc/>
    public override string Name => "Sensu";

    /// <summary>
    /// Initializes a new instance of the <see cref="SensuStrategy"/> class.
    /// </summary>
    public SensuStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: false,
        SupportsLogging: false,
        SupportsDistributedTracing: false,
        SupportsAlerting: true,
        SupportedExporters: new[] { "Sensu", "InfluxDB", "Prometheus" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the Sensu backend connection.
    /// </summary>
    /// <param name="apiUrl">Sensu backend API URL.</param>
    /// <param name="apiKey">API key for authentication.</param>
    /// <param name="sensuNamespace">Sensu namespace (default: "default").</param>
    public void Configure(string apiUrl, string apiKey, string sensuNamespace = "default")
    {
        _apiUrl = apiUrl;
        _apiKey = apiKey;
        _namespace = sensuNamespace;

        if (!string.IsNullOrEmpty(apiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Key {apiKey}");
        }
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("sensu.metrics_sent");
        var events = new List<object>();

        foreach (var metric in metrics)
        {
            var status = DetermineStatus(metric);

            var sensuEvent = new
            {
                check = new
                {
                    metadata = new
                    {
                        name = $"metric_{SanitizeName(metric.Name)}",
                        @namespace = _namespace
                    },
                    output = $"{metric.Name}: {metric.Value}",
                    status,
                    issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    interval = 60,
                    handlers = new[] { "default" }
                },
                metrics = new
                {
                    points = new[]
                    {
                        new
                        {
                            name = metric.Name,
                            value = metric.Value,
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            tags = metric.Labels?.Select(l => new { name = l.Name, value = l.Value }).ToArray()
                        }
                    }
                },
                entity = new
                {
                    metadata = new
                    {
                        name = "datawarehouse",
                        @namespace = _namespace
                    }
                }
            };

            events.Add(sensuEvent);
        }

        await SendEventsAsync(events, cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Sensu does not support tracing");
    }

    /// <inheritdoc/>
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Sensu does not support direct logging - use checks and events instead");
    }

    /// <summary>
    /// Sends a custom check event to Sensu.
    /// </summary>
    /// <param name="checkName">Name of the check.</param>
    /// <param name="status">Check status (0=OK, 1=WARNING, 2=CRITICAL, 3=UNKNOWN).</param>
    /// <param name="output">Check output message.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SendCheckAsync(string checkName, int status, string output, CancellationToken ct = default)
    {
        var sensuEvent = new
        {
            check = new
            {
                metadata = new
                {
                    name = checkName,
                    @namespace = _namespace
                },
                output,
                status,
                issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                interval = 60,
                handlers = new[] { "default" }
            },
            entity = new
            {
                metadata = new
                {
                    name = "datawarehouse",
                    @namespace = _namespace
                }
            }
        };

        await SendEventsAsync(new[] { sensuEvent }, ct);
    }

    private async Task SendEventsAsync(IEnumerable<object> events, CancellationToken ct)
    {
        try
        {
            foreach (var evt in events)
            {
                var json = JsonSerializer.Serialize(evt);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var url = $"{_apiUrl}/api/core/v2/namespaces/{_namespace}/events";

                var response = await _httpClient.PostAsync(url, content, ct);
                response.EnsureSuccessStatusCode();
            }
        }
        catch (HttpRequestException)
        {
            // Sensu backend unavailable - events lost
        }
    }

    private static int DetermineStatus(MetricValue metric)
    {
        // Simple threshold logic (could be configurable)
        return metric.Value switch
        {
            > 90 => 2,    // CRITICAL
            > 75 => 1,    // WARNING
            _ => 0        // OK
        };
    }

    private static string SanitizeName(string name)
    {
        return name.Replace(" ", "_").Replace("-", "_").Replace(".", "_").ToLowerInvariant();
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_apiUrl}/health", cancellationToken);

            return new HealthCheckResult(
                IsHealthy: response.IsSuccessStatusCode,
                Description: response.IsSuccessStatusCode ? "Sensu backend is healthy" : "Sensu backend unhealthy",
                Data: new Dictionary<string, object>
                {
                    ["apiUrl"] = _apiUrl,
                    ["namespace"] = _namespace,
                    ["hasApiKey"] = !string.IsNullOrEmpty(_apiKey)
                });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: $"Sensu health check failed: {ex.Message}",
                Data: null);
        }
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiUrl) || (!_apiUrl.StartsWith("http://") && !_apiUrl.StartsWith("https://")))
            throw new InvalidOperationException("SensuStrategy: Invalid endpoint URL configured.");
        IncrementCounter("sensu.initialized");
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
        IncrementCounter("sensu.shutdown");
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
