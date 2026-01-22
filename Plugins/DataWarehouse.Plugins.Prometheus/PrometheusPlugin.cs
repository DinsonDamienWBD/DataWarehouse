using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.Prometheus
{
    /// <summary>
    /// Abstract base class for telemetry plugins providing metrics, tracing, and logging capabilities.
    /// Extends <see cref="FeaturePluginBase"/> to support lifecycle management and HTTP endpoints.
    /// </summary>
    public abstract class TelemetryPluginBase : FeaturePluginBase, IMetricsProvider
    {
        /// <summary>
        /// Gets the category for telemetry plugins.
        /// </summary>
        public override PluginCategory Category => PluginCategory.MetricsProvider;

        /// <summary>
        /// Increments a counter metric by 1.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        public abstract void IncrementCounter(string metric);

        /// <summary>
        /// Records a value for a gauge or histogram metric.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="value">The value to record.</param>
        public abstract void RecordMetric(string metric, double value);

        /// <summary>
        /// Starts tracking the duration of an operation.
        /// </summary>
        /// <param name="metric">The metric name for the duration.</param>
        /// <returns>A disposable that records the duration when disposed.</returns>
        public abstract IDisposable TrackDuration(string metric);

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "Telemetry";
            metadata["SupportsMetrics"] = true;
            metadata["SupportsHistograms"] = true;
            metadata["SupportsTags"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Production-ready Prometheus metrics plugin implementing the Prometheus text exposition format.
    /// Provides counter, gauge, histogram, and summary metrics with label support.
    ///
    /// Features:
    /// - Full Prometheus text format exposition
    /// - Counter, Gauge, Histogram, and Summary metric types
    /// - Multi-dimensional labels with cardinality controls
    /// - Default process and runtime metrics
    /// - HTTP endpoint for Prometheus scraping
    /// - Metric aggregation and registry management
    /// - GZIP compression support
    ///
    /// Message Commands:
    /// - prometheus.increment: Increment a counter
    /// - prometheus.record: Record a gauge/histogram value
    /// - prometheus.observe: Observe a histogram/summary value
    /// - prometheus.metrics: Get metrics in Prometheus format
    /// - prometheus.clear: Clear all metrics
    /// - prometheus.status: Get plugin status
    /// </summary>
    public sealed class PrometheusPlugin : TelemetryPluginBase
    {
        private readonly MetricRegistry _registry;
        private readonly PrometheusConfiguration _config;
        private readonly ConcurrentDictionary<string, MetricFamily> _autoCreatedCounters;
        private readonly ConcurrentDictionary<string, MetricFamily> _autoCreatedGauges;
        private readonly ConcurrentDictionary<string, MetricFamily> _autoCreatedHistograms;
        private HttpListener? _httpListener;
        private CancellationTokenSource? _cts;
        private Task? _listenerTask;
        private readonly object _lock = new();
        private bool _isRunning;
        private DateTime _startTime;
        private long _scrapeCount;
        private long _scrapeErrors;
        private readonly Stopwatch _uptimeStopwatch = new();

        // Default metrics
        private MetricFamily? _processStartTime;
        private MetricFamily? _processCpuSecondsTotal;
        private MetricFamily? _processResidentMemoryBytes;
        private MetricFamily? _processVirtualMemoryBytes;
        private MetricFamily? _processOpenFds;
        private MetricFamily? _processMaxFds;
        private MetricFamily? _dotnetCollectionCount;
        private MetricFamily? _dotnetGcHeapSize;
        private MetricFamily? _dotnetThreadPoolThreads;
        private MetricFamily? _dotnetThreadPoolQueueLength;
        private MetricFamily? _scrapeRequestsTotal;
        private MetricFamily? _scrapeDurationSeconds;

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.telemetry.prometheus";

        /// <inheritdoc/>
        public override string Name => "Prometheus Metrics Plugin";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <summary>
        /// Gets the metric registry.
        /// </summary>
        public MetricRegistry Registry => _registry;

        /// <summary>
        /// Gets the plugin configuration.
        /// </summary>
        public PrometheusConfiguration Configuration => _config;

        /// <summary>
        /// Gets whether the HTTP endpoint is running.
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Gets the total number of scrape requests.
        /// </summary>
        public long ScrapeCount => Interlocked.Read(ref _scrapeCount);

        /// <summary>
        /// Gets the total number of scrape errors.
        /// </summary>
        public long ScrapeErrors => Interlocked.Read(ref _scrapeErrors);

        /// <summary>
        /// Initializes a new instance of the <see cref="PrometheusPlugin"/> class.
        /// </summary>
        /// <param name="config">Optional configuration. Defaults will be used if null.</param>
        public PrometheusPlugin(PrometheusConfiguration? config = null)
        {
            _config = config ?? new PrometheusConfiguration();
            _registry = new MetricRegistry(_config.MaxLabelCardinality, _config.MaxMetricFamilies);
            _autoCreatedCounters = new ConcurrentDictionary<string, MetricFamily>(StringComparer.Ordinal);
            _autoCreatedGauges = new ConcurrentDictionary<string, MetricFamily>(StringComparer.Ordinal);
            _autoCreatedHistograms = new ConcurrentDictionary<string, MetricFamily>(StringComparer.Ordinal);

            // Wire up cardinality limit warnings
            _registry.OnCardinalityLimitReached += (name, count) =>
            {
                // Log warning - could integrate with IKernelContext if available
                Console.Error.WriteLine($"[Prometheus] Label cardinality limit reached for metric '{name}': {count} labels");
            };
        }

        #region Lifecycle Management

        /// <inheritdoc/>
        public override async Task StartAsync(CancellationToken ct)
        {
            lock (_lock)
            {
                if (_isRunning)
                {
                    return;
                }

                _isRunning = true;
                _startTime = DateTime.UtcNow;
                _uptimeStopwatch.Start();
            }

            // Initialize default metrics
            InitializeDefaultMetrics();

            // Start HTTP listener
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _httpListener = new HttpListener();

            var prefix = $"http://{_config.Host}:{_config.Port}/";
            _httpListener.Prefixes.Add(prefix);

            try
            {
                _httpListener.Start();
                _listenerTask = ListenAsync(_cts.Token);
            }
            catch (HttpListenerException ex)
            {
                _isRunning = false;
                throw new InvalidOperationException(
                    $"Failed to start Prometheus HTTP listener on {prefix}: {ex.Message}", ex);
            }

            await Task.CompletedTask;
        }

        /// <inheritdoc/>
        public override async Task StopAsync()
        {
            lock (_lock)
            {
                if (!_isRunning)
                {
                    return;
                }

                _isRunning = false;
                _uptimeStopwatch.Stop();
            }

            _cts?.Cancel();

            try
            {
                _httpListener?.Stop();
                _httpListener?.Close();
            }
            catch
            {
                // Ignore errors during shutdown
            }

            if (_listenerTask != null)
            {
                try
                {
                    await _listenerTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
                catch (TimeoutException)
                {
                    // Timeout waiting for listener to stop
                }
            }

            _cts?.Dispose();
            _cts = null;
            _httpListener = null;
            _listenerTask = null;
        }

        #endregion

        #region IMetricsProvider Implementation

        /// <inheritdoc/>
        public override void IncrementCounter(string metric)
        {
            IncrementCounter(metric, 1, LabelSet.Empty);
        }

        /// <summary>
        /// Increments a counter metric by the specified amount.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="amount">The amount to increment.</param>
        /// <param name="labels">Optional labels.</param>
        public void IncrementCounter(string metric, double amount = 1, LabelSet? labels = null)
        {
            var name = GetFullMetricName(metric);
            var family = _autoCreatedCounters.GetOrAdd(name, n =>
                _registry.CreateCounter(n, $"Auto-created counter for {metric}"));

            var value = family.GetOrCreate(labels ?? LabelSet.Empty);
            if (value is CounterValue counter)
            {
                counter.Inc(amount);
            }
        }

        /// <inheritdoc/>
        public override void RecordMetric(string metric, double value)
        {
            RecordGauge(metric, value, LabelSet.Empty);
        }

        /// <summary>
        /// Records a gauge metric value.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="value">The value to record.</param>
        /// <param name="labels">Optional labels.</param>
        public void RecordGauge(string metric, double value, LabelSet? labels = null)
        {
            var name = GetFullMetricName(metric);
            var family = _autoCreatedGauges.GetOrAdd(name, n =>
                _registry.CreateGauge(n, $"Auto-created gauge for {metric}"));

            var metricValue = family.GetOrCreate(labels ?? LabelSet.Empty);
            if (metricValue is GaugeValue gauge)
            {
                gauge.Set(value);
            }
        }

        /// <summary>
        /// Observes a value for a histogram metric.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="value">The observed value.</param>
        /// <param name="labels">Optional labels.</param>
        /// <param name="buckets">Optional custom buckets.</param>
        public void ObserveHistogram(string metric, double value, LabelSet? labels = null, IEnumerable<double>? buckets = null)
        {
            var name = GetFullMetricName(metric);
            var family = _autoCreatedHistograms.GetOrAdd(name, n =>
                _registry.CreateHistogram(n, $"Auto-created histogram for {metric}",
                    buckets ?? _config.DefaultHistogramBuckets ?? DefaultBuckets.Default));

            var metricValue = family.GetOrCreate(labels ?? LabelSet.Empty);
            if (metricValue is HistogramValue histogram)
            {
                histogram.Observe(value);
            }
        }

        /// <inheritdoc/>
        public override IDisposable TrackDuration(string metric)
        {
            return TrackDuration(metric, LabelSet.Empty);
        }

        /// <summary>
        /// Tracks the duration of an operation as a histogram observation.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="labels">Optional labels.</param>
        /// <returns>A disposable that records the duration when disposed.</returns>
        public IDisposable TrackDuration(string metric, LabelSet? labels = null)
        {
            return new DurationTracker(this, metric, labels ?? LabelSet.Empty);
        }

        #endregion

        #region ITelemetryProvider Methods

        /// <summary>
        /// Creates a counter metric family.
        /// </summary>
        /// <param name="name">The metric name.</param>
        /// <param name="help">The help text.</param>
        /// <param name="labelNames">The label names.</param>
        /// <returns>The counter metric family.</returns>
        public MetricFamily CreateCounter(string name, string help, params string[] labelNames)
        {
            return _registry.CreateCounter(GetFullMetricName(name), help, labelNames);
        }

        /// <summary>
        /// Creates a gauge metric family.
        /// </summary>
        /// <param name="name">The metric name.</param>
        /// <param name="help">The help text.</param>
        /// <param name="labelNames">The label names.</param>
        /// <returns>The gauge metric family.</returns>
        public MetricFamily CreateGauge(string name, string help, params string[] labelNames)
        {
            return _registry.CreateGauge(GetFullMetricName(name), help, labelNames);
        }

        /// <summary>
        /// Creates a histogram metric family.
        /// </summary>
        /// <param name="name">The metric name.</param>
        /// <param name="help">The help text.</param>
        /// <param name="buckets">The bucket boundaries.</param>
        /// <param name="labelNames">The label names.</param>
        /// <returns>The histogram metric family.</returns>
        public MetricFamily CreateHistogram(string name, string help, IEnumerable<double>? buckets = null, params string[] labelNames)
        {
            return _registry.CreateHistogram(GetFullMetricName(name), help, buckets ?? _config.DefaultHistogramBuckets, labelNames);
        }

        /// <summary>
        /// Creates a summary metric family.
        /// </summary>
        /// <param name="name">The metric name.</param>
        /// <param name="help">The help text.</param>
        /// <param name="quantiles">The quantile definitions.</param>
        /// <param name="labelNames">The label names.</param>
        /// <returns>The summary metric family.</returns>
        public MetricFamily CreateSummary(string name, string help, IEnumerable<QuantileDefinition>? quantiles = null, params string[] labelNames)
        {
            return _registry.CreateSummary(GetFullMetricName(name), help, quantiles ?? _config.DefaultSummaryQuantiles, labelNames);
        }

        /// <summary>
        /// Exports all metrics in Prometheus text exposition format.
        /// </summary>
        /// <returns>The metrics as a string.</returns>
        public string ExportMetrics()
        {
            // Update default metrics before export
            UpdateDefaultMetrics();

            return _registry.ExportToPrometheusFormat();
        }

        #endregion

        #region Message Handling

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "prometheus.increment":
                    HandleIncrement(message);
                    break;
                case "prometheus.record":
                    HandleRecord(message);
                    break;
                case "prometheus.observe":
                    HandleObserve(message);
                    break;
                case "prometheus.metrics":
                    HandleMetrics(message);
                    break;
                case "prometheus.clear":
                    HandleClear(message);
                    break;
                case "prometheus.status":
                    HandleStatus(message);
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }

        private void HandleIncrement(PluginMessage message)
        {
            var metric = GetString(message.Payload, "metric");
            if (string.IsNullOrEmpty(metric))
            {
                message.Payload["error"] = "metric name required";
                return;
            }

            var amount = GetDouble(message.Payload, "amount") ?? 1;
            var labels = GetLabels(message.Payload);

            IncrementCounter(metric, amount, labels);
            message.Payload["success"] = true;
        }

        private void HandleRecord(PluginMessage message)
        {
            var metric = GetString(message.Payload, "metric");
            if (string.IsNullOrEmpty(metric))
            {
                message.Payload["error"] = "metric name required";
                return;
            }

            var value = GetDouble(message.Payload, "value");
            if (!value.HasValue)
            {
                message.Payload["error"] = "value required";
                return;
            }

            var labels = GetLabels(message.Payload);
            RecordGauge(metric, value.Value, labels);
            message.Payload["success"] = true;
        }

        private void HandleObserve(PluginMessage message)
        {
            var metric = GetString(message.Payload, "metric");
            if (string.IsNullOrEmpty(metric))
            {
                message.Payload["error"] = "metric name required";
                return;
            }

            var value = GetDouble(message.Payload, "value");
            if (!value.HasValue)
            {
                message.Payload["error"] = "value required";
                return;
            }

            var labels = GetLabels(message.Payload);
            ObserveHistogram(metric, value.Value, labels);
            message.Payload["success"] = true;
        }

        private void HandleMetrics(PluginMessage message)
        {
            message.Payload["result"] = ExportMetrics();
        }

        private void HandleClear(PluginMessage message)
        {
            _registry.Clear();
            _autoCreatedCounters.Clear();
            _autoCreatedGauges.Clear();
            _autoCreatedHistograms.Clear();

            // Reinitialize default metrics
            if (_config.IncludeProcessMetrics || _config.IncludeRuntimeMetrics)
            {
                InitializeDefaultMetrics();
            }

            message.Payload["success"] = true;
        }

        private void HandleStatus(PluginMessage message)
        {
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["isRunning"] = _isRunning,
                ["port"] = _config.Port,
                ["metricsPath"] = _config.MetricsPath,
                ["metricFamilies"] = _registry.FamilyCount,
                ["scrapeCount"] = ScrapeCount,
                ["scrapeErrors"] = ScrapeErrors,
                ["uptimeSeconds"] = _uptimeStopwatch.Elapsed.TotalSeconds
            };
        }

        #endregion

        #region HTTP Listener

        private async Task ListenAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _httpListener?.IsListening == true)
            {
                try
                {
                    var context = await _httpListener.GetContextAsync().WaitAsync(ct);
                    _ = ProcessRequestAsync(context);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    // Listener was stopped
                    break;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _scrapeErrors);
                    Console.Error.WriteLine($"[Prometheus] Error accepting request: {ex.Message}");
                }
            }
        }

        private async Task ProcessRequestAsync(HttpListenerContext context)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var request = context.Request;
                var response = context.Response;

                // Only handle GET requests to the metrics path
                if (request.HttpMethod != "GET" ||
                    !request.Url?.AbsolutePath.Equals(_config.MetricsPath, StringComparison.OrdinalIgnoreCase) == true)
                {
                    response.StatusCode = 404;
                    response.Close();
                    return;
                }

                Interlocked.Increment(ref _scrapeCount);

                // Generate metrics
                var metrics = ExportMetrics();
                var metricsBytes = Encoding.UTF8.GetBytes(metrics);

                response.ContentType = "text/plain; version=0.0.4; charset=utf-8";

                // Check for gzip support
                var acceptEncoding = request.Headers["Accept-Encoding"] ?? string.Empty;
                var useGzip = _config.EnableGzipCompression &&
                              acceptEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase);

                if (useGzip)
                {
                    response.AddHeader("Content-Encoding", "gzip");
                    using var gzipStream = new GZipStream(response.OutputStream, CompressionMode.Compress);
                    await gzipStream.WriteAsync(metricsBytes);
                }
                else
                {
                    response.ContentLength64 = metricsBytes.Length;
                    await response.OutputStream.WriteAsync(metricsBytes);
                }

                response.StatusCode = 200;
                response.Close();

                // Record scrape duration
                stopwatch.Stop();
                if (_scrapeDurationSeconds != null)
                {
                    var value = _scrapeDurationSeconds.GetOrCreate(LabelSet.Empty);
                    if (value is HistogramValue histogram)
                    {
                        histogram.Observe(stopwatch.Elapsed.TotalSeconds);
                    }
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _scrapeErrors);
                Console.Error.WriteLine($"[Prometheus] Error processing request: {ex.Message}");

                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch
                {
                    // Ignore errors during error response
                }
            }
        }

        #endregion

        #region Default Metrics

        private void InitializeDefaultMetrics()
        {
            if (_config.IncludeProcessMetrics)
            {
                _processStartTime = _registry.CreateGauge(
                    "process_start_time_seconds",
                    "Start time of the process since unix epoch in seconds");

                _processCpuSecondsTotal = _registry.CreateCounter(
                    "process_cpu_seconds_total",
                    "Total user and system CPU time spent in seconds");

                _processResidentMemoryBytes = _registry.CreateGauge(
                    "process_resident_memory_bytes",
                    "Resident memory size in bytes");

                _processVirtualMemoryBytes = _registry.CreateGauge(
                    "process_virtual_memory_bytes",
                    "Virtual memory size in bytes");

                _processOpenFds = _registry.CreateGauge(
                    "process_open_fds",
                    "Number of open file descriptors");

                _processMaxFds = _registry.CreateGauge(
                    "process_max_fds",
                    "Maximum number of open file descriptors");
            }

            if (_config.IncludeRuntimeMetrics)
            {
                _dotnetCollectionCount = _registry.CreateCounter(
                    "dotnet_gc_collection_count_total",
                    "GC collection count",
                    "generation");

                _dotnetGcHeapSize = _registry.CreateGauge(
                    "dotnet_gc_heap_size_bytes",
                    "GC heap size in bytes",
                    "generation");

                _dotnetThreadPoolThreads = _registry.CreateGauge(
                    "dotnet_threadpool_threads_count",
                    "ThreadPool thread count");

                _dotnetThreadPoolQueueLength = _registry.CreateGauge(
                    "dotnet_threadpool_queue_length",
                    "ThreadPool work item queue length");
            }

            // Scrape metrics (always enabled)
            _scrapeRequestsTotal = _registry.CreateCounter(
                "prometheus_scrape_requests_total",
                "Total number of Prometheus scrape requests");

            _scrapeDurationSeconds = _registry.CreateHistogram(
                "prometheus_scrape_duration_seconds",
                "Histogram of Prometheus scrape durations",
                new[] { 0.001, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1.0 });
        }

        private void UpdateDefaultMetrics()
        {
            try
            {
                var process = Process.GetCurrentProcess();

                if (_config.IncludeProcessMetrics)
                {
                    // Process start time
                    if (_processStartTime != null)
                    {
                        var value = _processStartTime.GetOrCreate(LabelSet.Empty);
                        if (value is GaugeValue gauge)
                        {
                            gauge.Set(new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeSeconds());
                        }
                    }

                    // CPU time
                    if (_processCpuSecondsTotal != null)
                    {
                        var value = _processCpuSecondsTotal.GetOrCreate(LabelSet.Empty);
                        if (value is CounterValue counter)
                        {
                            var cpuTime = process.TotalProcessorTime.TotalSeconds;
                            var currentValue = counter.Value;
                            if (cpuTime > currentValue)
                            {
                                counter.Inc(cpuTime - currentValue);
                            }
                        }
                    }

                    // Memory
                    if (_processResidentMemoryBytes != null)
                    {
                        var value = _processResidentMemoryBytes.GetOrCreate(LabelSet.Empty);
                        if (value is GaugeValue gauge)
                        {
                            gauge.Set(process.WorkingSet64);
                        }
                    }

                    if (_processVirtualMemoryBytes != null)
                    {
                        var value = _processVirtualMemoryBytes.GetOrCreate(LabelSet.Empty);
                        if (value is GaugeValue gauge)
                        {
                            gauge.Set(process.VirtualMemorySize64);
                        }
                    }

                    // File descriptors (handles on Windows)
                    if (_processOpenFds != null)
                    {
                        var value = _processOpenFds.GetOrCreate(LabelSet.Empty);
                        if (value is GaugeValue gauge)
                        {
                            gauge.Set(process.HandleCount);
                        }
                    }
                }

                if (_config.IncludeRuntimeMetrics)
                {
                    // GC collection count
                    if (_dotnetCollectionCount != null)
                    {
                        for (var gen = 0; gen <= GC.MaxGeneration; gen++)
                        {
                            var labels = LabelSet.From(("generation", gen.ToString()));
                            var value = _dotnetCollectionCount.GetOrCreate(labels);
                            if (value is CounterValue counter)
                            {
                                var count = GC.CollectionCount(gen);
                                var currentValue = (long)counter.Value;
                                if (count > currentValue)
                                {
                                    counter.Inc(count - currentValue);
                                }
                            }
                        }
                    }

                    // GC heap size
                    if (_dotnetGcHeapSize != null)
                    {
                        var gcInfo = GC.GetGCMemoryInfo();
                        for (var gen = 0; gen <= GC.MaxGeneration; gen++)
                        {
                            var labels = LabelSet.From(("generation", gen.ToString()));
                            var value = _dotnetGcHeapSize.GetOrCreate(labels);
                            if (value is GaugeValue gauge)
                            {
                                // Get generation size from GCMemoryInfo
                                var genInfo = gcInfo.GenerationInfo[gen];
                                gauge.Set(genInfo.SizeAfterBytes);
                            }
                        }
                    }

                    // ThreadPool metrics
                    if (_dotnetThreadPoolThreads != null)
                    {
                        ThreadPool.GetAvailableThreads(out var workerThreads, out var completionPortThreads);
                        ThreadPool.GetMaxThreads(out var maxWorkerThreads, out var maxCompletionPortThreads);

                        var value = _dotnetThreadPoolThreads.GetOrCreate(LabelSet.Empty);
                        if (value is GaugeValue gauge)
                        {
                            gauge.Set(maxWorkerThreads - workerThreads);
                        }
                    }

                    if (_dotnetThreadPoolQueueLength != null)
                    {
                        var value = _dotnetThreadPoolQueueLength.GetOrCreate(LabelSet.Empty);
                        if (value is GaugeValue gauge)
                        {
                            gauge.Set(ThreadPool.PendingWorkItemCount);
                        }
                    }
                }

                // Update scrape counter
                if (_scrapeRequestsTotal != null)
                {
                    var value = _scrapeRequestsTotal.GetOrCreate(LabelSet.Empty);
                    if (value is CounterValue counter)
                    {
                        var diff = ScrapeCount - (long)counter.Value;
                        if (diff > 0)
                        {
                            counter.Inc(diff);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Prometheus] Error updating default metrics: {ex.Message}");
            }
        }

        #endregion

        #region Helpers

        private string GetFullMetricName(string name)
        {
            if (string.IsNullOrEmpty(_config.Namespace))
            {
                return name;
            }

            return $"{_config.Namespace}_{name}";
        }

        private static string? GetString(Dictionary<string, object> payload, string key)
        {
            return payload.TryGetValue(key, out var val) && val is string s ? s : null;
        }

        private static double? GetDouble(Dictionary<string, object> payload, string key)
        {
            if (payload.TryGetValue(key, out var val))
            {
                if (val is double d) return d;
                if (val is int i) return i;
                if (val is long l) return l;
                if (val is float f) return f;
                if (val is string s && double.TryParse(s, out var parsed)) return parsed;
            }
            return null;
        }

        private static LabelSet? GetLabels(Dictionary<string, object> payload)
        {
            if (!payload.TryGetValue("labels", out var labelsObj))
            {
                return null;
            }

            if (labelsObj is Dictionary<string, object> dict)
            {
                var labels = dict.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value?.ToString() ?? string.Empty);
                return LabelSet.From(labels);
            }

            if (labelsObj is Dictionary<string, string> stringDict)
            {
                return LabelSet.From(stringDict);
            }

            return null;
        }

        #endregion

        #region Plugin Metadata

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "prometheus.increment", DisplayName = "Increment Counter", Description = "Increment a counter metric" },
                new() { Name = "prometheus.record", DisplayName = "Record Gauge", Description = "Record a gauge metric value" },
                new() { Name = "prometheus.observe", DisplayName = "Observe Histogram", Description = "Observe a histogram value" },
                new() { Name = "prometheus.metrics", DisplayName = "Export Metrics", Description = "Export metrics in Prometheus format" },
                new() { Name = "prometheus.clear", DisplayName = "Clear Metrics", Description = "Clear all metrics" },
                new() { Name = "prometheus.status", DisplayName = "Get Status", Description = "Get plugin status" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Port"] = _config.Port;
            metadata["MetricsPath"] = _config.MetricsPath;
            metadata["SupportsCounters"] = true;
            metadata["SupportsGauges"] = true;
            metadata["SupportsHistograms"] = true;
            metadata["SupportsSummaries"] = true;
            metadata["SupportsLabels"] = true;
            metadata["MaxLabelCardinality"] = _config.MaxLabelCardinality;
            metadata["IncludesProcessMetrics"] = _config.IncludeProcessMetrics;
            metadata["IncludesRuntimeMetrics"] = _config.IncludeRuntimeMetrics;
            return metadata;
        }

        #endregion

        #region Duration Tracker

        /// <summary>
        /// Helper class for tracking operation duration.
        /// </summary>
        private sealed class DurationTracker : IDisposable
        {
            private readonly PrometheusPlugin _plugin;
            private readonly string _metric;
            private readonly LabelSet _labels;
            private readonly Stopwatch _stopwatch;
            private bool _disposed;

            public DurationTracker(PrometheusPlugin plugin, string metric, LabelSet labels)
            {
                _plugin = plugin;
                _metric = metric;
                _labels = labels;
                _stopwatch = Stopwatch.StartNew();
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _stopwatch.Stop();
                _plugin.ObserveHistogram(_metric, _stopwatch.Elapsed.TotalSeconds, _labels);
            }
        }

        #endregion
    }
}
