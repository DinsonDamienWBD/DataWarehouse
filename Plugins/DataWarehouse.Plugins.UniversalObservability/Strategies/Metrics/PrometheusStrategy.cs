using System.Net.Http;
using System.Text;
using DataWarehouse.SDK.Contracts.Observability;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Metrics;

/// <summary>
/// Observability strategy for Prometheus metrics collection and exposition.
/// Provides pull-based metrics with support for counters, gauges, histograms, and summaries.
/// </summary>
/// <remarks>
/// Prometheus is an open-source monitoring system with a multi-dimensional data model,
/// flexible query language (PromQL), and efficient time series database.
/// </remarks>
public sealed class PrometheusStrategy : ObservabilityStrategyBase
{
    private readonly BoundedDictionary<string, double> _counters = new BoundedDictionary<string, double>(1000);
    private readonly BoundedDictionary<string, double> _gauges = new BoundedDictionary<string, double>(1000);
    // P2-4633: Use Queue<double> for O(1) dequeue when capping observation history.
    private readonly BoundedDictionary<string, Queue<double>> _histogramBuckets = new BoundedDictionary<string, Queue<double>>(1000);
    private readonly HttpClient _httpClient;
    private string _pushGatewayUrl = "http://localhost:9091";
    private string _jobName = "datawarehouse";
    private int _circuitBreakerFailures = 0;
    private const int CircuitBreakerThreshold = 5;
    private volatile bool _circuitOpen = false;
    private long _circuitOpenedAtTicks = DateTimeOffset.MinValue.UtcTicks;
    private readonly object _circuitLock = new();

    /// <inheritdoc/>
    public override string StrategyId => "prometheus";

    /// <inheritdoc/>
    public override string Name => "Prometheus";

    /// <summary>
    /// Initializes a new instance of the <see cref="PrometheusStrategy"/> class.
    /// </summary>
    public PrometheusStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: false,
        SupportsLogging: false,
        SupportsDistributedTracing: false,
        SupportsAlerting: true,
        SupportedExporters: new[] { "Prometheus", "OpenMetrics", "PushGateway" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the Prometheus PushGateway endpoint.
    /// </summary>
    /// <param name="url">PushGateway URL.</param>
    /// <param name="jobName">Job name for grouping metrics.</param>
    public void Configure(string url, string jobName = "datawarehouse")
    {
        _pushGatewayUrl = url;
        _jobName = jobName;
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("prometheus.metrics_sent");
        var metricsText = new StringBuilder();

        foreach (var metric in metrics)
        {
            var labelString = FormatLabels(metric.Labels);
            var metricName = SanitizeMetricName(metric.Name);

            switch (metric.Type)
            {
                case MetricType.Counter:
                    var newCounterValue = _counters.AddOrUpdate(
                        $"{metricName}{labelString}",
                        metric.Value,
                        (_, existing) => existing + metric.Value);
                    metricsText.AppendLine($"# TYPE {metricName} counter");
                    metricsText.AppendLine($"{metricName}{labelString} {newCounterValue}");
                    break;

                case MetricType.Gauge:
                    _gauges[metricName + labelString] = metric.Value;
                    metricsText.AppendLine($"# TYPE {metricName} gauge");
                    metricsText.AppendLine($"{metricName}{labelString} {metric.Value}");
                    break;

                case MetricType.Histogram:
                    RecordHistogram(metricName, labelString, metric.Value, metricsText);
                    break;

                case MetricType.Summary:
                    metricsText.AppendLine($"# TYPE {metricName} summary");
                    metricsText.AppendLine($"{metricName}{labelString} {metric.Value}");
                    break;
            }
        }

        // Push to PushGateway
        await PushMetricsAsync(metricsText.ToString(), cancellationToken);
    }

    // P2-4633: Cap at 10k observations. Queue<double> gives O(1) dequeue vs O(n) List.RemoveAt(0).
    private const int MaxHistogramObservations = 10_000;
    private static readonly double[] BucketBoundaries = { 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10 };

    private void RecordHistogram(string metricName, string labelString, double value, StringBuilder metricsText)
    {
        var bucketKey = $"{metricName}{labelString}";
        var buckets = _histogramBuckets.GetOrAdd(bucketKey, _ => new Queue<double>());

        // Snapshot under lock, compute counts outside lock to minimise contention.
        double[] snapshot;
        lock (buckets)
        {
            if (buckets.Count >= MaxHistogramObservations)
                buckets.Dequeue(); // O(1) vs O(n) RemoveAt(0)
            buckets.Enqueue(value);
            snapshot = buckets.ToArray();
        }

        // P2-4633: Single sorted pass O(n log n) instead of O(n*b) repeated LINQ per boundary.
        Array.Sort(snapshot);
        long count = snapshot.Length;
        double sum = 0;
        foreach (var v in snapshot) sum += v;

        // Compute cumulative bucket counts with a single pointer advance.
        var bucketCounts = new long[BucketBoundaries.Length];
        int ptr = 0;
        for (int b = 0; b < BucketBoundaries.Length; b++)
        {
            while (ptr < snapshot.Length && snapshot[ptr] <= BucketBoundaries[b])
                ptr++;
            bucketCounts[b] = ptr;
        }

        var labelInner = labelString.Length > 2 ? "," + labelString.Substring(1, labelString.Length - 2) : "";
        metricsText.AppendLine($"# TYPE {metricName} histogram");
        for (int b = 0; b < BucketBoundaries.Length; b++)
            metricsText.AppendLine($"{metricName}_bucket{{le=\"{BucketBoundaries[b]}\"{labelInner}}} {bucketCounts[b]}");
        metricsText.AppendLine($"{metricName}_bucket{{le=\"+Inf\"{labelInner}}} {count}");
        metricsText.AppendLine($"{metricName}_sum{labelString} {sum}");
        metricsText.AppendLine($"{metricName}_count{labelString} {count}");
    }

    private async Task PushMetricsAsync(string metricsText, CancellationToken ct)
    {
        if (_circuitOpen &&
            DateTimeOffset.UtcNow.UtcTicks - Interlocked.Read(ref _circuitOpenedAtTicks) < TimeSpan.FromMinutes(1).Ticks)
            return; // Circuit open

        await SendWithRetryAsync(async () =>
        {
            var content = new StringContent(metricsText, Encoding.UTF8, "text/plain");
            var url = $"{_pushGatewayUrl}/metrics/job/{_jobName}";
            using var response = await _httpClient.PostAsync(url, content, ct);
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
                    _circuitBreakerFailures = 0;
                    _circuitOpen = false;
                }
                return;
            }
            catch (HttpRequestException) when (attempt < maxRetries - 1)
            {
                var delay = baseDelay * Math.Pow(2, attempt);
                await Task.Delay(delay, ct);
            }
            catch (HttpRequestException)
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
                // Swallow - metrics stored locally in dictionaries
            }
        }
    }

    /// <summary>
    /// Gets the current metrics in Prometheus exposition format.
    /// </summary>
    /// <returns>Metrics in Prometheus text format.</returns>
    public string GetMetricsText()
    {
        var sb = new StringBuilder();

        foreach (var (key, value) in _counters)
        {
            sb.AppendLine($"{key} {value}");
        }

        foreach (var (key, value) in _gauges)
        {
            sb.AppendLine($"{key} {value}");
        }

        return sb.ToString();
    }

    private static string FormatLabels(IReadOnlyList<MetricLabel> labels)
    {
        if (labels == null || labels.Count == 0)
            return "";

        var labelPairs = labels.Select(l => $"{SanitizeLabelName(l.Name)}=\"{EscapeLabelValue(l.Value)}\"");
        return "{" + string.Join(",", labelPairs) + "}";
    }

    private static string SanitizeMetricName(string name)
    {
        return name.Replace("-", "_").Replace(".", "_").Replace(" ", "_").ToLowerInvariant();
    }

    private static string SanitizeLabelName(string name)
    {
        return name.Replace("-", "_").Replace(".", "_").Replace(" ", "_").ToLowerInvariant();
    }

    private static string EscapeLabelValue(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Prometheus does not support tracing");
    }

    /// <inheritdoc/>
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Prometheus does not support logging");
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{_pushGatewayUrl}/-/ready", cancellationToken);
            return new HealthCheckResult(
                IsHealthy: response.IsSuccessStatusCode,
                Description: response.IsSuccessStatusCode ? "Prometheus PushGateway is healthy" : "PushGateway unhealthy",
                Data: new Dictionary<string, object>
                {
                    ["pushGatewayUrl"] = _pushGatewayUrl,
                    ["counters"] = _counters.Count,
                    ["gauges"] = _gauges.Count
                });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: $"Prometheus health check failed: {ex.Message}",
                Data: null);
        }
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_pushGatewayUrl) || (!_pushGatewayUrl.StartsWith("http://") && !_pushGatewayUrl.StartsWith("https://")))
            throw new InvalidOperationException("PrometheusStrategy: Invalid endpoint URL configured.");
        IncrementCounter("prometheus.initialized");
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
        IncrementCounter("prometheus.shutdown");
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
