using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Metrics;

/// <summary>
/// Observability strategy for Azure Monitor metrics and logs.
/// Provides comprehensive Azure-native monitoring with metrics, logs, and Application Insights.
/// </summary>
/// <remarks>
/// Azure Monitor provides full-stack monitoring across Azure resources including
/// metrics, logs, alerts, and Application Insights for APM capabilities.
/// </remarks>
public sealed class AzureMonitorStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _workspaceId = "";
    private string _sharedKey = "";
    private string _instrumentationKey = "";
    private string _logType = "DataWarehouse";
    private readonly string _apiVersion = "2016-04-01";

    /// <inheritdoc/>
    public override string StrategyId => "azure-monitor";

    /// <inheritdoc/>
    public override string Name => "Azure Monitor";

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureMonitorStrategy"/> class.
    /// </summary>
    public AzureMonitorStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: true,
        SupportsLogging: true,
        SupportsDistributedTracing: true,
        SupportsAlerting: true,
        SupportedExporters: new[] { "AzureMonitor", "LogAnalytics", "ApplicationInsights" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the Azure Monitor connection.
    /// </summary>
    /// <param name="workspaceId">Log Analytics workspace ID.</param>
    /// <param name="sharedKey">Log Analytics shared key.</param>
    /// <param name="instrumentationKey">Application Insights instrumentation key.</param>
    /// <param name="logType">Custom log type name.</param>
    public void Configure(string workspaceId, string sharedKey, string instrumentationKey = "", string logType = "DataWarehouse")
    {
        _workspaceId = workspaceId;
        _sharedKey = sharedKey;
        _instrumentationKey = instrumentationKey;
        _logType = logType;
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("azure_monitor.metrics_sent");
        // Send metrics to Application Insights if configured
        if (!string.IsNullOrEmpty(_instrumentationKey))
        {
            await SendToApplicationInsightsAsync(metrics, cancellationToken);
        }

        // Also send as custom logs to Log Analytics
        var logEntries = metrics.Select(m => new Dictionary<string, object>
        {
            ["MetricName"] = m.Name,
            ["MetricValue"] = m.Value,
            ["MetricType"] = m.Type.ToString(),
            ["Unit"] = m.Unit ?? "unit",
            ["Timestamp"] = m.Timestamp.ToString("o"),
            ["Labels"] = m.Labels?.ToDictionary(l => l.Name, l => l.Value) ?? new Dictionary<string, string>()
        }).ToList();

        await PostToLogAnalyticsAsync(logEntries, cancellationToken);
    }

    private async Task SendToApplicationInsightsAsync(IEnumerable<MetricValue> metrics, CancellationToken ct)
    {
        var telemetryItems = new List<object>();

        foreach (var metric in metrics)
        {
            var tags = new Dictionary<string, string>
            {
                ["ai.cloud.roleInstance"] = Environment.MachineName,
                ["ai.operation.name"] = "MetricRecording"
            };

            var properties = metric.Labels?.ToDictionary(l => l.Name, l => (object)l.Value) ?? new Dictionary<string, object>();

            telemetryItems.Add(new
            {
                name = "Microsoft.ApplicationInsights.Metric",
                time = metric.Timestamp.ToString("o"),
                iKey = _instrumentationKey,
                tags,
                data = new
                {
                    baseType = "MetricData",
                    baseData = new
                    {
                        ver = 2,
                        metrics = new[]
                        {
                            new
                            {
                                name = metric.Name,
                                value = metric.Value,
                                kind = metric.Type == MetricType.Counter ? 1 : 0
                            }
                        },
                        properties
                    }
                }
            });
        }

        var json = JsonSerializer.Serialize(telemetryItems);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        await _httpClient.PostAsync(
            "https://dc.services.visualstudio.com/v2/track",
            content,
            ct);
    }

    /// <inheritdoc/>
    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        IncrementCounter("azure_monitor.traces_sent");
        if (string.IsNullOrEmpty(_instrumentationKey))
            throw new InvalidOperationException("Application Insights instrumentation key required for tracing");

        var telemetryItems = new List<object>();

        foreach (var span in spans)
        {
            var tags = new Dictionary<string, string>
            {
                ["ai.cloud.roleInstance"] = Environment.MachineName,
                ["ai.operation.id"] = span.TraceId,
                ["ai.operation.parentId"] = span.ParentSpanId ?? ""
            };

            var properties = span.Attributes?.ToDictionary(a => a.Key, a => a.Value) ?? new Dictionary<string, object>();

            if (span.Kind == SpanKind.Server || span.Kind == SpanKind.Client)
            {
                // Request or Dependency telemetry
                var baseType = span.Kind == SpanKind.Server ? "RequestData" : "RemoteDependencyData";
                var name = span.Kind == SpanKind.Server ? "Microsoft.ApplicationInsights.Request" : "Microsoft.ApplicationInsights.RemoteDependency";

                telemetryItems.Add(new
                {
                    name,
                    time = span.StartTime.ToString("o"),
                    iKey = _instrumentationKey,
                    tags,
                    data = new
                    {
                        baseType,
                        baseData = new
                        {
                            ver = 2,
                            id = span.SpanId,
                            name = span.OperationName,
                            duration = span.Duration.ToString(@"hh\:mm\:ss\.fff"),
                            success = span.Status != SpanStatus.Error,
                            properties
                        }
                    }
                });
            }
            else
            {
                // Custom event for internal spans
                telemetryItems.Add(new
                {
                    name = "Microsoft.ApplicationInsights.Event",
                    time = span.StartTime.ToString("o"),
                    iKey = _instrumentationKey,
                    tags,
                    data = new
                    {
                        baseType = "EventData",
                        baseData = new
                        {
                            ver = 2,
                            name = span.OperationName,
                            properties
                        }
                    }
                });
            }
        }

        var json = JsonSerializer.Serialize(telemetryItems);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        await _httpClient.PostAsync(
            "https://dc.services.visualstudio.com/v2/track",
            content,
            cancellationToken);
    }

    /// <inheritdoc/>
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        IncrementCounter("azure_monitor.logs_sent");
        var logs = logEntries.Select(entry => new Dictionary<string, object>
        {
            ["TimeGenerated"] = entry.Timestamp.ToString("o"),
            ["Level"] = entry.Level.ToString(),
            ["Message"] = entry.Message,
            ["MachineName"] = Environment.MachineName,
            ["Properties"] = entry.Properties ?? new Dictionary<string, object>(),
            ["ExceptionType"] = entry.Exception?.GetType().Name ?? "",
            ["ExceptionMessage"] = entry.Exception?.Message ?? "",
            ["ExceptionStack"] = entry.Exception?.StackTrace ?? ""
        }).ToList();

        await PostToLogAnalyticsAsync(logs, cancellationToken);
    }

    private async Task PostToLogAnalyticsAsync(List<Dictionary<string, object>> logs, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(logs);
        var dateString = DateTime.UtcNow.ToString("r");
        var contentLength = Encoding.UTF8.GetByteCount(json);

        var signature = BuildSignature(dateString, contentLength, "POST", "application/json", "/api/logs");

        var url = $"https://{_workspaceId}.ods.opinsights.azure.com/api/logs?api-version={_apiVersion}";

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Authorization", signature);
        request.Headers.Add("Log-Type", _logType);
        request.Headers.Add("x-ms-date", dateString);
        request.Headers.Add("time-generated-field", "TimeGenerated");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private string BuildSignature(string dateString, int contentLength, string method, string contentType, string resource)
    {
        var xHeaders = $"x-ms-date:{dateString}";
        var stringToHash = $"{method}\n{contentLength}\n{contentType}\n{xHeaders}\n{resource}";

        var keyBytes = Convert.FromBase64String(_sharedKey);
        using var hmac = new System.Security.Cryptography.HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToHash));

        return $"SharedKey {_workspaceId}:{Convert.ToBase64String(hash)}";
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            // Test Log Analytics connection
            var testLog = new List<Dictionary<string, object>>
            {
                new()
                {
                    ["TimeGenerated"] = DateTime.UtcNow.ToString("o"),
                    ["Level"] = "Information",
                    ["Message"] = "Health check test"
                }
            };

            await PostToLogAnalyticsAsync(testLog, cancellationToken);

            return new HealthCheckResult(
                IsHealthy: true,
                Description: "Azure Monitor connection is healthy",
                Data: new Dictionary<string, object>
                {
                    ["workspaceId"] = _workspaceId,
                    ["logType"] = _logType,
                    ["hasAppInsights"] = !string.IsNullOrEmpty(_instrumentationKey)
                });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: $"Azure Monitor health check failed: {ex.Message}",
                Data: null);
        }
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        // Configuration validated via Configure method
        IncrementCounter("azure_monitor.initialized");
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
        IncrementCounter("azure_monitor.shutdown");
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
