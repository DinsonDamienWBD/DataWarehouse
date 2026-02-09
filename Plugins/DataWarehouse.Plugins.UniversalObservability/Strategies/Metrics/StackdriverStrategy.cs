using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Metrics;

/// <summary>
/// Observability strategy for Google Cloud Stackdriver (Cloud Monitoring and Logging).
/// Provides comprehensive GCP-native monitoring with metrics, logs, and traces.
/// </summary>
/// <remarks>
/// Google Cloud Stackdriver provides monitoring, logging, and diagnostics for
/// applications running on Google Cloud Platform and AWS.
/// </remarks>
public sealed class StackdriverStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _projectId = "";
    private string _accessToken = "";
    private string _metricPrefix = "custom.googleapis.com/datawarehouse";

    /// <inheritdoc/>
    public override string StrategyId => "stackdriver";

    /// <inheritdoc/>
    public override string Name => "Google Cloud Stackdriver";

    /// <summary>
    /// Initializes a new instance of the <see cref="StackdriverStrategy"/> class.
    /// </summary>
    public StackdriverStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: true,
        SupportsLogging: true,
        SupportsDistributedTracing: true,
        SupportsAlerting: true,
        SupportedExporters: new[] { "CloudMonitoring", "CloudLogging", "CloudTrace" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the Stackdriver connection.
    /// </summary>
    /// <param name="projectId">GCP project ID.</param>
    /// <param name="accessToken">OAuth2 access token.</param>
    /// <param name="metricPrefix">Prefix for custom metrics.</param>
    public void Configure(string projectId, string accessToken, string metricPrefix = "custom.googleapis.com/datawarehouse")
    {
        _projectId = projectId;
        _accessToken = accessToken;
        _metricPrefix = metricPrefix;
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_accessToken}");
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        var timeSeries = new List<object>();

        foreach (var metric in metrics)
        {
            var metricType = $"{_metricPrefix}/{metric.Name.Replace(".", "/").Replace("-", "_")}";

            var labels = metric.Labels?.ToDictionary(l => l.Name.Replace(".", "_"), l => l.Value)
                ?? new Dictionary<string, string>();

            var valueType = metric.Type switch
            {
                MetricType.Counter => "INT64",
                MetricType.Gauge => "DOUBLE",
                MetricType.Histogram => "DISTRIBUTION",
                _ => "DOUBLE"
            };

            var metricKind = metric.Type switch
            {
                MetricType.Counter => "CUMULATIVE",
                MetricType.Gauge => "GAUGE",
                _ => "GAUGE"
            };

            var point = new
            {
                interval = new
                {
                    endTime = metric.Timestamp.ToString("o"),
                    startTime = metric.Type == MetricType.Counter
                        ? metric.Timestamp.AddMinutes(-1).ToString("o")
                        : null
                },
                value = new Dictionary<string, object>
                {
                    [valueType == "INT64" ? "int64Value" : "doubleValue"] = metric.Value
                }
            };

            timeSeries.Add(new
            {
                metric = new
                {
                    type = metricType,
                    labels
                },
                resource = new
                {
                    type = "global",
                    labels = new
                    {
                        project_id = _projectId
                    }
                },
                metricKind,
                valueType,
                points = new[] { point }
            });
        }

        var payload = new { timeSeries };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(
            $"https://monitoring.googleapis.com/v3/projects/{_projectId}/timeSeries",
            content,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        var traceSpans = new List<object>();

        foreach (var span in spans)
        {
            var spanName = $"projects/{_projectId}/traces/{span.TraceId}/spans/{span.SpanId}";

            var attributes = new Dictionary<string, object>();
            if (span.Attributes != null)
            {
                foreach (var attr in span.Attributes)
                {
                    attributes[attr.Key] = new { stringValue = new { value = attr.Value?.ToString() ?? "" } };
                }
            }

            traceSpans.Add(new
            {
                name = spanName,
                spanId = span.SpanId,
                parentSpanId = span.ParentSpanId ?? "",
                displayName = new { value = span.OperationName, truncatedByteCount = 0 },
                startTime = span.StartTime.ToString("o"),
                endTime = span.StartTime.Add(span.Duration).ToString("o"),
                attributes = new { attributeMap = attributes },
                spanKind = span.Kind switch
                {
                    SpanKind.Server => "SERVER",
                    SpanKind.Client => "CLIENT",
                    SpanKind.Producer => "PRODUCER",
                    SpanKind.Consumer => "CONSUMER",
                    _ => "INTERNAL"
                },
                status = new
                {
                    code = span.Status == SpanStatus.Error ? 2 : 0
                }
            });
        }

        var payload = new { spans = traceSpans };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(
            $"https://cloudtrace.googleapis.com/v2/projects/{_projectId}/traces:batchWrite",
            content,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        var entries = logEntries.Select(entry => new
        {
            logName = $"projects/{_projectId}/logs/datawarehouse",
            resource = new
            {
                type = "global",
                labels = new { project_id = _projectId }
            },
            timestamp = entry.Timestamp.ToString("o"),
            severity = entry.Level switch
            {
                LogLevel.Trace => "DEBUG",
                LogLevel.Debug => "DEBUG",
                LogLevel.Information => "INFO",
                LogLevel.Warning => "WARNING",
                LogLevel.Error => "ERROR",
                LogLevel.Critical => "CRITICAL",
                _ => "DEFAULT"
            },
            jsonPayload = new Dictionary<string, object?>
            {
                ["message"] = entry.Message,
                ["properties"] = entry.Properties ?? new Dictionary<string, object>(),
                ["exception"] = entry.Exception != null ? new
                {
                    type = entry.Exception.GetType().Name,
                    message = entry.Exception.Message,
                    stackTrace = entry.Exception.StackTrace ?? ""
                } : null
            }
        }).ToList();

        var payload = new { entries };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(
            $"https://logging.googleapis.com/v2/entries:write",
            content,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync(
                $"https://monitoring.googleapis.com/v3/projects/{_projectId}/monitoredResourceDescriptors",
                cancellationToken);

            return new HealthCheckResult(
                IsHealthy: response.IsSuccessStatusCode,
                Description: response.IsSuccessStatusCode ? "Stackdriver connection is healthy" : "Stackdriver API error",
                Data: new Dictionary<string, object>
                {
                    ["projectId"] = _projectId,
                    ["metricPrefix"] = _metricPrefix
                });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: $"Stackdriver health check failed: {ex.Message}",
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
