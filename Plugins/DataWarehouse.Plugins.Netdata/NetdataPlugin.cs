using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.Netdata
{
    /// <summary>
    /// Production-ready Netdata monitoring plugin with StatsD and HTTP API support.
    /// Provides real-time metrics collection and visualization through Netdata.
    ///
    /// Features:
    /// - StatsD protocol support for metric submission
    /// - HTTP API for custom chart creation
    /// - Counter, gauge, histogram, and timing metrics
    /// - UDP batching for efficient network usage
    /// - Default process and runtime metrics
    /// - Custom chart definitions
    /// - Metric aggregation and buffering
    ///
    /// Message Commands:
    /// - netdata.increment: Increment a counter
    /// - netdata.record: Record a gauge value
    /// - netdata.timing: Record a timing value
    /// - netdata.histogram: Record a histogram value
    /// - netdata.chart.create: Create a custom chart
    /// - netdata.status: Get plugin status
    /// - netdata.clear: Clear all metrics
    /// </summary>
    public sealed class NetdataPlugin : FeaturePluginBase, IMetricsProvider
    {
        private readonly NetdataConfiguration _config;
        private readonly ConcurrentDictionary<string, MetricState> _metrics;
        private readonly ConcurrentQueue<string> _statsdBuffer;
        private readonly ConcurrentDictionary<string, ChartDefinition> _charts;
        private UdpClient? _udpClient;
        private HttpClient? _httpClient;
        private Timer? _flushTimer;
        private CancellationTokenSource? _cts;
        private bool _isRunning;
        private readonly object _lock = new();
        private DateTime _startTime;
        private long _metricsSent;
        private long _metricsErrors;
        private readonly Stopwatch _uptimeStopwatch = new();

        // Default metrics
        private string? _processCpuChart;
        private string? _processMemoryChart;
        private string? _dotnetGcChart;
        private string? _dotnetThreadPoolChart;

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.telemetry.netdata";

        /// <inheritdoc/>
        public override string Name => "Netdata Monitoring Plugin";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <inheritdoc/>
        public override PluginCategory Category => PluginCategory.MetricsProvider;

        /// <summary>
        /// Gets the plugin configuration.
        /// </summary>
        public NetdataConfiguration Configuration => _config;

        /// <summary>
        /// Gets whether the plugin is running.
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Gets the total number of metrics sent.
        /// </summary>
        public long MetricsSent => Interlocked.Read(ref _metricsSent);

        /// <summary>
        /// Gets the total number of metric errors.
        /// </summary>
        public long MetricsErrors => Interlocked.Read(ref _metricsErrors);

        /// <summary>
        /// Initializes a new instance of the <see cref="NetdataPlugin"/> class.
        /// </summary>
        /// <param name="config">Optional configuration. Defaults will be used if null.</param>
        public NetdataPlugin(NetdataConfiguration? config = null)
        {
            _config = config ?? new NetdataConfiguration();
            _metrics = new ConcurrentDictionary<string, MetricState>(StringComparer.Ordinal);
            _statsdBuffer = new ConcurrentQueue<string>();
            _charts = new ConcurrentDictionary<string, ChartDefinition>(StringComparer.Ordinal);
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

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Initialize StatsD client
            if (_config.EnableStatsd)
            {
                try
                {
                    _udpClient = new UdpClient();
                    _udpClient.Connect(_config.StatsdHost, _config.StatsdPort);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Netdata] Failed to initialize StatsD client: {ex.Message}");
                }
            }

            // Initialize HTTP client
            if (_config.EnableHttpApi)
            {
                _httpClient = new HttpClient
                {
                    BaseAddress = new Uri(_config.NetdataUrl),
                    Timeout = TimeSpan.FromSeconds(5)
                };
            }

            // Start flush timer for batched metrics
            if (_config.BatchStatsdMetrics && _udpClient != null)
            {
                _flushTimer = new Timer(
                    FlushStatsdBuffer,
                    null,
                    TimeSpan.FromMilliseconds(_config.BatchFlushIntervalMs),
                    TimeSpan.FromMilliseconds(_config.BatchFlushIntervalMs));
            }

            // Initialize default metrics
            await InitializeDefaultMetricsAsync();

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

            // Flush remaining metrics
            if (_config.BatchStatsdMetrics)
            {
                FlushStatsdBuffer(null);
            }

            // Dispose resources
            _flushTimer?.Dispose();
            _flushTimer = null;

            _udpClient?.Dispose();
            _udpClient = null;

            _httpClient?.Dispose();
            _httpClient = null;

            _cts?.Dispose();
            _cts = null;

            await Task.CompletedTask;
        }

        #endregion

        #region IMetricsProvider Implementation

        /// <inheritdoc/>
        public void IncrementCounter(string metric)
        {
            IncrementCounter(metric, 1);
        }

        /// <summary>
        /// Increments a counter metric by the specified amount.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="amount">The amount to increment.</param>
        public void IncrementCounter(string metric, double amount = 1)
        {
            var fullName = GetFullMetricName(metric);
            _metrics.AddOrUpdate(fullName,
                _ => new MetricState { Type = MetricType.Counter, Value = amount },
                (_, state) =>
                {
                    state.Value += amount;
                    state.LastUpdated = DateTime.UtcNow;
                    return state;
                });

            SendStatsdMetric(fullName, amount, "c");
        }

        /// <inheritdoc/>
        public void RecordMetric(string metric, double value)
        {
            RecordGauge(metric, value);
        }

        /// <summary>
        /// Records a gauge metric value.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="value">The value to record.</param>
        public void RecordGauge(string metric, double value)
        {
            var fullName = GetFullMetricName(metric);
            _metrics.AddOrUpdate(fullName,
                _ => new MetricState { Type = MetricType.Gauge, Value = value },
                (_, state) =>
                {
                    state.Value = value;
                    state.LastUpdated = DateTime.UtcNow;
                    return state;
                });

            SendStatsdMetric(fullName, value, "g");
        }

        /// <summary>
        /// Records a timing metric value (in milliseconds).
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="value">The timing value in milliseconds.</param>
        public void RecordTiming(string metric, double value)
        {
            var fullName = GetFullMetricName(metric);
            _metrics.AddOrUpdate(fullName,
                _ => new MetricState { Type = MetricType.Timing, Value = value },
                (_, state) =>
                {
                    state.Value = value;
                    state.LastUpdated = DateTime.UtcNow;
                    return state;
                });

            SendStatsdMetric(fullName, value, "ms");
        }

        /// <summary>
        /// Records a histogram metric value.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="value">The value to record.</param>
        public void RecordHistogram(string metric, double value)
        {
            var fullName = GetFullMetricName(metric);
            _metrics.AddOrUpdate(fullName,
                _ => new MetricState { Type = MetricType.Histogram, Value = value },
                (_, state) =>
                {
                    state.Value = value;
                    state.LastUpdated = DateTime.UtcNow;
                    return state;
                });

            SendStatsdMetric(fullName, value, "h");
        }

        /// <inheritdoc/>
        public IDisposable TrackDuration(string metric)
        {
            return new DurationTracker(this, metric);
        }

        #endregion

        #region Chart Management

        /// <summary>
        /// Creates a custom Netdata chart.
        /// </summary>
        /// <param name="chartId">The chart identifier.</param>
        /// <param name="chartName">The chart display name.</param>
        /// <param name="chartType">The chart type (line, area, stacked).</param>
        /// <param name="units">The units for the chart.</param>
        /// <param name="dimensions">The chart dimensions (metrics).</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task<bool> CreateChartAsync(
            string chartId,
            string chartName,
            string chartType,
            string units,
            params string[] dimensions)
        {
            if (!_config.EnableHttpApi || _httpClient == null)
            {
                return false;
            }

            var chart = new ChartDefinition
            {
                Id = chartId,
                Name = chartName,
                Type = chartType,
                Units = units,
                Family = _config.ChartFamily,
                UpdateEvery = _config.ChartUpdateIntervalSeconds,
                Dimensions = dimensions
            };

            _charts[chartId] = chart;

            try
            {
                // Create chart via HTTP API
                var chartDefinition = BuildChartDefinition(chart);
                var content = new StringContent(chartDefinition, Encoding.UTF8, "text/plain");
                var response = await _httpClient.PostAsync("/api/v1/charts", content);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Netdata] Failed to create chart '{chartId}': {ex.Message}");
                return false;
            }
        }

        private string BuildChartDefinition(ChartDefinition chart)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"CHART {chart.Family}.{chart.Id} '{chart.Name}' '{chart.Units}' {chart.Family} {chart.Type} {chart.UpdateEvery}");

            foreach (var dimension in chart.Dimensions)
            {
                sb.AppendLine($"DIMENSION {dimension} '{dimension}' absolute 1 1");
            }

            return sb.ToString();
        }

        #endregion

        #region StatsD Protocol

        private void SendStatsdMetric(string metric, double value, string type)
        {
            if (!_config.EnableStatsd || _udpClient == null)
            {
                return;
            }

            try
            {
                var statsdMessage = $"{metric}:{value}|{type}";

                if (_config.BatchStatsdMetrics)
                {
                    _statsdBuffer.Enqueue(statsdMessage);
                }
                else
                {
                    SendUdpPacket(statsdMessage);
                }

                Interlocked.Increment(ref _metricsSent);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _metricsErrors);
                Console.Error.WriteLine($"[Netdata] Error sending StatsD metric: {ex.Message}");
            }
        }

        private void FlushStatsdBuffer(object? state)
        {
            if (_udpClient == null || !_isRunning)
            {
                return;
            }

            var batch = new List<string>();
            var currentSize = 0;

            while (_statsdBuffer.TryDequeue(out var metric))
            {
                var metricSize = Encoding.UTF8.GetByteCount(metric) + 1; // +1 for newline

                if (currentSize + metricSize > _config.MaxUdpPacketSize && batch.Count > 0)
                {
                    // Send current batch
                    SendUdpPacket(string.Join("\n", batch));
                    batch.Clear();
                    currentSize = 0;
                }

                batch.Add(metric);
                currentSize += metricSize;
            }

            // Send remaining batch
            if (batch.Count > 0)
            {
                SendUdpPacket(string.Join("\n", batch));
            }
        }

        private void SendUdpPacket(string message)
        {
            if (_udpClient == null)
            {
                return;
            }

            try
            {
                var data = Encoding.UTF8.GetBytes(message);
                _udpClient.Send(data, data.Length);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _metricsErrors);
                Console.Error.WriteLine($"[Netdata] Error sending UDP packet: {ex.Message}");
            }
        }

        #endregion

        #region Default Metrics

        private async Task InitializeDefaultMetricsAsync()
        {
            if (_config.IncludeProcessMetrics)
            {
                _processCpuChart = $"{_config.ChartFamily}.process_cpu";
                _processMemoryChart = $"{_config.ChartFamily}.process_memory";

                if (_config.EnableHttpApi)
                {
                    await CreateChartAsync("process_cpu", "Process CPU Usage", "line", "percent", "cpu_percent");
                    await CreateChartAsync("process_memory", "Process Memory", "line", "MB", "working_set", "virtual_memory");
                }

                // Start update timer for default metrics
                _ = Task.Run(async () => await UpdateDefaultMetricsLoop(_cts?.Token ?? CancellationToken.None));
            }

            if (_config.IncludeRuntimeMetrics)
            {
                _dotnetGcChart = $"{_config.ChartFamily}.dotnet_gc";
                _dotnetThreadPoolChart = $"{_config.ChartFamily}.dotnet_threadpool";

                if (_config.EnableHttpApi)
                {
                    await CreateChartAsync("dotnet_gc", ".NET GC Collections", "line", "count", "gen0", "gen1", "gen2");
                    await CreateChartAsync("dotnet_threadpool", ".NET ThreadPool", "line", "threads", "worker_threads", "queue_length");
                }
            }
        }

        private async Task UpdateDefaultMetricsLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _isRunning)
            {
                try
                {
                    UpdateDefaultMetrics();
                    await Task.Delay(TimeSpan.FromSeconds(_config.ChartUpdateIntervalSeconds), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Netdata] Error updating default metrics: {ex.Message}");
                }
            }
        }

        private void UpdateDefaultMetrics()
        {
            try
            {
                var process = Process.GetCurrentProcess();

                if (_config.IncludeProcessMetrics)
                {
                    // CPU usage (approximation based on total processor time)
                    var cpuPercent = (process.TotalProcessorTime.TotalMilliseconds / _uptimeStopwatch.Elapsed.TotalMilliseconds) * 100.0;
                    RecordGauge("process.cpu_percent", cpuPercent);

                    // Memory usage
                    var workingSetMb = process.WorkingSet64 / (1024.0 * 1024.0);
                    var virtualMemoryMb = process.VirtualMemorySize64 / (1024.0 * 1024.0);
                    RecordGauge("process.working_set_mb", workingSetMb);
                    RecordGauge("process.virtual_memory_mb", virtualMemoryMb);
                }

                if (_config.IncludeRuntimeMetrics)
                {
                    // GC collection counts
                    for (var gen = 0; gen <= GC.MaxGeneration; gen++)
                    {
                        var count = GC.CollectionCount(gen);
                        RecordGauge($"dotnet.gc.gen{gen}_collections", count);
                    }

                    // ThreadPool metrics
                    ThreadPool.GetAvailableThreads(out var workerThreads, out var _);
                    ThreadPool.GetMaxThreads(out var maxWorkerThreads, out var _);
                    var usedThreads = maxWorkerThreads - workerThreads;
                    var queueLength = ThreadPool.PendingWorkItemCount;

                    RecordGauge("dotnet.threadpool.worker_threads", usedThreads);
                    RecordGauge("dotnet.threadpool.queue_length", queueLength);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Netdata] Error updating default metrics: {ex.Message}");
            }
        }

        #endregion

        #region Message Handling

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "netdata.increment":
                    HandleIncrement(message);
                    break;
                case "netdata.record":
                    HandleRecord(message);
                    break;
                case "netdata.timing":
                    HandleTiming(message);
                    break;
                case "netdata.histogram":
                    HandleHistogram(message);
                    break;
                case "netdata.chart.create":
                    await HandleCreateChartAsync(message);
                    break;
                case "netdata.status":
                    HandleStatus(message);
                    break;
                case "netdata.clear":
                    HandleClear(message);
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
            IncrementCounter(metric, amount);
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

            RecordGauge(metric, value.Value);
            message.Payload["success"] = true;
        }

        private void HandleTiming(PluginMessage message)
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

            RecordTiming(metric, value.Value);
            message.Payload["success"] = true;
        }

        private void HandleHistogram(PluginMessage message)
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

            RecordHistogram(metric, value.Value);
            message.Payload["success"] = true;
        }

        private async Task HandleCreateChartAsync(PluginMessage message)
        {
            var chartId = GetString(message.Payload, "chartId");
            var chartName = GetString(message.Payload, "chartName");
            var chartType = GetString(message.Payload, "chartType") ?? _config.DefaultChartType;
            var units = GetString(message.Payload, "units") ?? "value";

            if (string.IsNullOrEmpty(chartId) || string.IsNullOrEmpty(chartName))
            {
                message.Payload["error"] = "chartId and chartName required";
                return;
            }

            var dimensions = message.Payload.TryGetValue("dimensions", out var dimObj) && dimObj is string[] dims
                ? dims
                : Array.Empty<string>();

            var success = await CreateChartAsync(chartId, chartName, chartType, units, dimensions);
            message.Payload["success"] = success;
        }

        private void HandleStatus(PluginMessage message)
        {
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["isRunning"] = _isRunning,
                ["netdataUrl"] = _config.NetdataUrl,
                ["statsdHost"] = _config.StatsdHost,
                ["statsdPort"] = _config.StatsdPort,
                ["enableHttpApi"] = _config.EnableHttpApi,
                ["enableStatsd"] = _config.EnableStatsd,
                ["metricCount"] = _metrics.Count,
                ["chartCount"] = _charts.Count,
                ["metricsSent"] = MetricsSent,
                ["metricsErrors"] = MetricsErrors,
                ["uptimeSeconds"] = _uptimeStopwatch.Elapsed.TotalSeconds
            };
        }

        private void HandleClear(PluginMessage message)
        {
            _metrics.Clear();
            _charts.Clear();
            message.Payload["success"] = true;
        }

        #endregion

        #region Helpers

        private string GetFullMetricName(string name)
        {
            if (string.IsNullOrEmpty(_config.MetricPrefix))
            {
                return name;
            }

            return $"{_config.MetricPrefix}.{name}";
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

        #endregion

        #region Plugin Metadata

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "netdata.increment", DisplayName = "Increment Counter", Description = "Increment a counter metric" },
                new() { Name = "netdata.record", DisplayName = "Record Gauge", Description = "Record a gauge metric value" },
                new() { Name = "netdata.timing", DisplayName = "Record Timing", Description = "Record a timing metric" },
                new() { Name = "netdata.histogram", DisplayName = "Record Histogram", Description = "Record a histogram value" },
                new() { Name = "netdata.chart.create", DisplayName = "Create Chart", Description = "Create a custom Netdata chart" },
                new() { Name = "netdata.status", DisplayName = "Get Status", Description = "Get plugin status" },
                new() { Name = "netdata.clear", DisplayName = "Clear Metrics", Description = "Clear all metrics" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["NetdataUrl"] = _config.NetdataUrl;
            metadata["StatsdHost"] = _config.StatsdHost;
            metadata["StatsdPort"] = _config.StatsdPort;
            metadata["EnableHttpApi"] = _config.EnableHttpApi;
            metadata["EnableStatsd"] = _config.EnableStatsd;
            metadata["SupportsCounters"] = true;
            metadata["SupportsGauges"] = true;
            metadata["SupportsTimings"] = true;
            metadata["SupportsHistograms"] = true;
            metadata["SupportsCustomCharts"] = true;
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
            private readonly NetdataPlugin _plugin;
            private readonly string _metric;
            private readonly Stopwatch _stopwatch;
            private bool _disposed;

            public DurationTracker(NetdataPlugin plugin, string metric)
            {
                _plugin = plugin;
                _metric = metric;
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
                _plugin.RecordTiming(_metric, _stopwatch.Elapsed.TotalMilliseconds);
            }
        }

        #endregion

        #region Internal Types

        private enum MetricType
        {
            Counter,
            Gauge,
            Timing,
            Histogram
        }

        private sealed class MetricState
        {
            public MetricType Type { get; set; }
            public double Value { get; set; }
            public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        }

        private sealed class ChartDefinition
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
            public string Units { get; set; } = string.Empty;
            public string Family { get; set; } = string.Empty;
            public int UpdateEvery { get; set; }
            public string[] Dimensions { get; set; } = Array.Empty<string>();
        }

        #endregion
    }
}
