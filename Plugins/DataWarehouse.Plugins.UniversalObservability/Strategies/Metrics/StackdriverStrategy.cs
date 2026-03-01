using System.Collections.Concurrent;
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
    private readonly ConcurrentQueue<object> _metricsBatch = new();
    private readonly ConcurrentQueue<object> _logsBatch = new();
    private readonly System.Timers.Timer _flushTimer;
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private int _batchSize = 100;
    private int _flushIntervalSeconds = 10;
    private int _circuitBreakerFailures = 0;
    private const int CircuitBreakerThreshold = 5;
    private volatile bool _circuitOpen = false;
    private long _circuitOpenedAtTicks = DateTimeOffset.MinValue.UtcTicks;
    private readonly object _circuitLock = new();
    private int _exportIntervalMs = 60000; // Default 60 seconds

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
        _flushTimer = new System.Timers.Timer(_flushIntervalSeconds * 1000);
        _flushTimer.Elapsed += async (_, _) => await FlushBatchesAsync(CancellationToken.None);
        _flushTimer.AutoReset = true;
        _flushTimer.Start();
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
        // Do NOT set DefaultRequestHeaders — inject per-request to avoid thread-safety issues.
    }

    /// <summary>Adds the Bearer authorization token to the request headers (per-request, thread-safe).</summary>
    private void AddAuthToken(HttpRequestMessage request) =>
        request.Headers.Add("Authorization", $"Bearer {_accessToken}");

    /// <inheritdoc/>
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        // Validate GCP project ID
        if (string.IsNullOrWhiteSpace(_projectId))
            throw new InvalidOperationException("GCP project ID is required. Call Configure() before initialization.");

        // Validate project ID format (alphanumeric, hyphens, lowercase, 6-30 chars)
        if (_projectId.Length < 6 || _projectId.Length > 30 || !System.Text.RegularExpressions.Regex.IsMatch(_projectId, @"^[a-z][a-z0-9-]*[a-z0-9]$"))
            throw new InvalidOperationException($"Invalid GCP project ID format: '{_projectId}'. Must be 6-30 chars, lowercase alphanumeric with hyphens.");

        // Validate metric prefix
        if (string.IsNullOrWhiteSpace(_metricPrefix))
            throw new InvalidOperationException("Metric prefix cannot be empty.");

        // Validate export interval (1 second to 5 minutes)
        if (_exportIntervalMs < 1000 || _exportIntervalMs > 300000)
            throw new InvalidOperationException($"Export interval must be between 1000ms and 300000ms. Got: {_exportIntervalMs}ms");

        // If access token is provided, test API reachability
        if (!string.IsNullOrWhiteSpace(_accessToken))
        {
            try
            {
                using var validateReq = new HttpRequestMessage(HttpMethod.Get,
                    $"https://monitoring.googleapis.com/v3/projects/{_projectId}/monitoredResourceDescriptors?pageSize=1");
                AddAuthToken(validateReq);
                using var response = await _httpClient.SendAsync(validateReq, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Stackdriver API validation failed with status {response.StatusCode}");
                }
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException($"Failed to connect to Stackdriver API: {ex.Message}", ex);
            }
        }

        await base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        // Stop flush timer
        _flushTimer?.Stop();

        // Flush remaining metrics and logs with 10-second timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        await _flushLock.WaitAsync(cts.Token);
        try
        {
            if (_metricsBatch.Count > 0)
                await FlushMetricsAsync(cts.Token);
            if (_logsBatch.Count > 0)
                await FlushLogsAsync(cts.Token);
        }
        catch (OperationCanceledException ex)
        {

            // Shutdown timeout exceeded, abandon remaining data
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _flushLock.Release();
        }

        // Close HTTP connections
        _httpClient?.Dispose();

        await base.ShutdownAsyncCore(cancellationToken);
    }

    private async Task FlushBatchesAsync(CancellationToken ct)
    {
        if (_circuitOpen && DateTimeOffset.UtcNow.UtcTicks - Interlocked.Read(ref _circuitOpenedAtTicks) < TimeSpan.FromMinutes(1).Ticks)
            return; // Circuit open, skip flush

        await _flushLock.WaitAsync(ct);
        try
        {
            if (_metricsBatch.Count >= _batchSize)
                await FlushMetricsAsync(ct);
            if (_logsBatch.Count >= _batchSize)
                await FlushLogsAsync(ct);
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private async Task FlushMetricsAsync(CancellationToken ct)
    {
        var batch = new List<object>();
        while (batch.Count < _batchSize && _metricsBatch.TryDequeue(out var ts))
            batch.Add(ts);

        if (batch.Count == 0) return;

        try
        {
            await SendWithRetryAsync(async () =>
            {
                var payload = new { timeSeries = batch };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    $"https://monitoring.googleapis.com/v3/projects/{_projectId}/timeSeries") { Content = content };
                AddAuthToken(request);
                using var response = await _httpClient.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
            }, ct);

            IncrementCounter("stackdriver.metrics_sent");
        }
        catch (Exception)
        {
            IncrementCounter("stackdriver.metrics_failed");
            throw;
        }
    }

    private async Task FlushLogsAsync(CancellationToken ct)
    {
        var batch = new List<object>();
        while (batch.Count < _batchSize && _logsBatch.TryDequeue(out var entry))
            batch.Add(entry);

        if (batch.Count == 0) return;

        await SendWithRetryAsync(async () =>
        {
            var payload = new { entries = batch };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post,
                "https://logging.googleapis.com/v2/entries:write") { Content = content };
            AddAuthToken(request);
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
        }, ct);
    }

    private async Task SendWithRetryAsync(Func<Task> action, CancellationToken ct)
    {
        var maxRetries = 3;
        var baseDelay = TimeSpan.FromMilliseconds(100);

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                await action();
                lock (_circuitLock)
                {
                    _circuitBreakerFailures = 0; // Reset on success
                    _circuitOpen = false;
                }
                return;
            }
            catch (HttpRequestException ex) when (attempt < maxRetries - 1)
            {
                // Handle specific HTTP errors
                var statusCode = ex.StatusCode;
                if (statusCode == System.Net.HttpStatusCode.Unauthorized) // 401
                {
                    IncrementCounter("stackdriver.auth_failure");
                    throw; // Don't retry auth failures
                }
                else if (statusCode == (System.Net.HttpStatusCode)429) // 429 quota exceeded
                {
                    IncrementCounter("stackdriver.quota_exceeded");
                    var delay = baseDelay * Math.Pow(2, attempt + 2); // Longer backoff for quota
                    await Task.Delay(delay, ct);
                }
                else if (statusCode == System.Net.HttpStatusCode.ServiceUnavailable) // 503
                {
                    IncrementCounter("stackdriver.service_unavailable");
                    var delay = baseDelay * Math.Pow(2, attempt);
                    await Task.Delay(delay, ct);
                }
                else
                {
                    var delay = baseDelay * Math.Pow(2, attempt);
                    await Task.Delay(delay, ct);
                }
            }
            catch (Exception) when (attempt < maxRetries - 1)
            {
                var delay = baseDelay * Math.Pow(2, attempt);
                await Task.Delay(delay, ct);
            }
            catch (Exception)
            {
                lock (_circuitLock)
                {
                    _circuitBreakerFailures++;
                    if (_circuitBreakerFailures >= CircuitBreakerThreshold)
                    {
                        _circuitOpen = true;
                        Interlocked.Exchange(ref _circuitOpenedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
                    }
                }
                throw;
            }
        }
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        if (_circuitOpen && DateTimeOffset.UtcNow.UtcTicks - Interlocked.Read(ref _circuitOpenedAtTicks) < TimeSpan.FromMinutes(1).Ticks)
            return; // Circuit open, drop metrics

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

            // For CUMULATIVE metrics, startTime must precede endTime and represent the start of the
            // cumulative measurement window.  Using AddMinutes(-1) is arbitrary and wrong — use 1
            // second before the sample timestamp as the minimum valid Cloud Monitoring window.
            var counterStartTime = metric.Type == MetricType.Counter
                ? (metric.Timestamp - TimeSpan.FromSeconds(1)).ToString("o")
                : (string?)null;

            var point = new
            {
                interval = new
                {
                    endTime = metric.Timestamp.ToString("o"),
                    startTime = counterStartTime
                },
                value = new Dictionary<string, object>
                {
                    [valueType == "INT64" ? "int64Value" : "doubleValue"] = metric.Value
                }
            };

            var ts = new
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
            };

            _metricsBatch.Enqueue(ts);
        }

        // Flush immediately if batch size reached
        if (_metricsBatch.Count >= _batchSize)
            await FlushMetricsAsync(cancellationToken);
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
        using var traceReq = new HttpRequestMessage(HttpMethod.Post,
            $"https://cloudtrace.googleapis.com/v2/projects/{_projectId}/traces:batchWrite") { Content = content };
        AddAuthToken(traceReq);
        using var response = await _httpClient.SendAsync(traceReq, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        if (_circuitOpen && DateTimeOffset.UtcNow.UtcTicks - Interlocked.Read(ref _circuitOpenedAtTicks) < TimeSpan.FromMinutes(1).Ticks)
            return; // Circuit open, drop logs

        foreach (var entry in logEntries)
        {
            var logEntry = new
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
            };

            _logsBatch.Enqueue(logEntry);
        }

        // Flush immediately if batch size reached
        if (_logsBatch.Count >= _batchSize)
            await FlushLogsAsync(cancellationToken);
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        var cachedResult = await GetCachedHealthAsync(async ct =>
        {
            try
            {
                using var healthReq = new HttpRequestMessage(HttpMethod.Get,
                    $"https://monitoring.googleapis.com/v3/projects/{_projectId}/monitoredResourceDescriptors?pageSize=1");
                AddAuthToken(healthReq);
                using var response = await _httpClient.SendAsync(healthReq, ct);

                return new DataWarehouse.SDK.Contracts.StrategyHealthCheckResult(
                    IsHealthy: response.IsSuccessStatusCode,
                    Message: response.IsSuccessStatusCode ? "Stackdriver API reachable" : $"Stackdriver API returned {response.StatusCode}",
                    Details: new Dictionary<string, object>
                    {
                        ["projectId"] = _projectId,
                        ["metricPrefix"] = _metricPrefix,
                        ["circuitOpen"] = _circuitOpen,
                        ["metricsSent"] = GetCounter("stackdriver.metrics_sent"),
                        ["metricsFailed"] = GetCounter("stackdriver.metrics_failed")
                    });
            }
            catch (Exception ex)
            {
                return new DataWarehouse.SDK.Contracts.StrategyHealthCheckResult(
                    IsHealthy: false,
                    Message: $"Stackdriver health check failed: {ex.Message}",
                    Details: new Dictionary<string, object> { ["exception"] = ex.GetType().Name });
            }
        }, TimeSpan.FromSeconds(60), cancellationToken);

        return new HealthCheckResult(
            IsHealthy: cachedResult.IsHealthy,
            Description: cachedResult.Message ?? "Stackdriver health check",
            Data: cachedResult.Details != null
                ? new Dictionary<string, object>(cachedResult.Details)
                : new Dictionary<string, object>());
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _flushTimer?.Stop();
            _flushTimer?.Dispose();
            // Bounded flush: acquire lock, flush both queues, release
            if (_flushLock.Wait(TimeSpan.FromSeconds(5)))
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                    try { FlushMetricsAsync(cts.Token).GetAwaiter().GetResult(); } catch { /* Best-effort */ }
                    try { FlushLogsAsync(cts.Token).GetAwaiter().GetResult(); } catch { /* Best-effort */ }
                }
                finally
                {
                    _flushLock.Release();
                }
            }
            _flushLock.Dispose();
            _httpClient.Dispose();
        }
        base.Dispose(disposing);
    }
}
