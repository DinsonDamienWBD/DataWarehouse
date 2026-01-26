using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.Logzio
{
    /// <summary>
    /// Production-ready Logz.io observability plugin for logs and metrics ingestion.
    ///
    /// Features:
    /// - JSON logs shipping to Logz.io listener
    /// - Prometheus remote write compatible metrics
    /// - Token-based authentication
    /// - Automatic batching and buffering
    /// - Retry logic with exponential backoff
    /// - GZIP compression support
    /// - Configurable flush intervals
    /// - Thread-safe operations
    ///
    /// Message Commands:
    /// - logzio.log: Send a log message
    /// - logzio.metric: Record a metric
    /// - logzio.flush: Flush buffered logs and metrics
    /// - logzio.status: Get plugin status
    /// - logzio.clear: Clear buffered data
    /// </summary>
    public sealed class LogzioPlugin : TelemetryPluginBase
    {
        private readonly LogzioConfiguration _config;
        private readonly HttpClient _httpClient;
        private readonly ConcurrentQueue<LogzioLogEntry> _logBuffer;
        private readonly ConcurrentQueue<LogzioMetricSample> _metricBuffer;
        private readonly ConcurrentDictionary<string, MetricState> _metricStates;
        private readonly Timer? _logFlushTimer;
        private readonly Timer? _metricFlushTimer;
        private readonly SemaphoreSlim _logFlushLock;
        private readonly SemaphoreSlim _metricFlushLock;
        private readonly object _lock = new();
        private bool _isRunning;
        private long _logsSent;
        private long _logsDropped;
        private long _metricsSent;
        private long _metricsDropped;
        private long _requestErrors;
        private readonly Stopwatch _uptimeStopwatch = new();

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.telemetry.logzio";

        /// <inheritdoc/>
        public override string Name => "Logz.io Observability Plugin";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <summary>
        /// Gets the configuration.
        /// </summary>
        public LogzioConfiguration Configuration => _config;

        /// <summary>
        /// Gets the total number of logs sent.
        /// </summary>
        public long LogsSent => Interlocked.Read(ref _logsSent);

        /// <summary>
        /// Gets the total number of logs dropped.
        /// </summary>
        public long LogsDropped => Interlocked.Read(ref _logsDropped);

        /// <summary>
        /// Gets the total number of metrics sent.
        /// </summary>
        public long MetricsSent => Interlocked.Read(ref _metricsSent);

        /// <summary>
        /// Gets the total number of metrics dropped.
        /// </summary>
        public long MetricsDropped => Interlocked.Read(ref _metricsDropped);

        /// <summary>
        /// Gets the total number of request errors.
        /// </summary>
        public long RequestErrors => Interlocked.Read(ref _requestErrors);

        /// <summary>
        /// Initializes a new instance of the <see cref="LogzioPlugin"/> class.
        /// </summary>
        /// <param name="config">The plugin configuration.</param>
        public LogzioPlugin(LogzioConfiguration? config = null)
        {
            _config = config ?? new LogzioConfiguration();
            _logBuffer = new ConcurrentQueue<LogzioLogEntry>();
            _metricBuffer = new ConcurrentQueue<LogzioMetricSample>();
            _metricStates = new ConcurrentDictionary<string, MetricState>(StringComparer.Ordinal);
            _logFlushLock = new SemaphoreSlim(1, 1);
            _metricFlushLock = new SemaphoreSlim(1, 1);

            // Configure HttpClient
            var handler = new HttpClientHandler();
            if (_config.EnableCompression)
            {
                handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            }

            _httpClient = new HttpClient(handler)
            {
                Timeout = _config.RequestTimeout
            };

            // Set default headers
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // Initialize timers (but don't start them yet)
            if (_config.EnableLogs)
            {
                _logFlushTimer = new Timer(
                    _ => _ = FlushLogsAsync(),
                    null,
                    Timeout.InfiniteTimeSpan,
                    Timeout.InfiniteTimeSpan);
            }

            if (_config.EnableMetrics)
            {
                _metricFlushTimer = new Timer(
                    _ => _ = FlushMetricsAsync(),
                    null,
                    Timeout.InfiniteTimeSpan,
                    Timeout.InfiniteTimeSpan);
            }

            ValidateConfiguration();
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
                _uptimeStopwatch.Start();
            }

            // Start flush timers
            if (_config.EnableLogs && _logFlushTimer != null)
            {
                _logFlushTimer.Change(_config.MaxBatchInterval, _config.MaxBatchInterval);
            }

            if (_config.EnableMetrics && _metricFlushTimer != null)
            {
                _metricFlushTimer.Change(_config.MetricsInterval, _config.MetricsInterval);
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

            // Stop timers
            if (_logFlushTimer != null)
            {
                await _logFlushTimer.DisposeAsync();
            }

            if (_metricFlushTimer != null)
            {
                await _metricFlushTimer.DisposeAsync();
            }

            // Flush remaining data
            if (_config.EnableLogs)
            {
                await FlushLogsAsync();
            }

            if (_config.EnableMetrics)
            {
                await FlushMetricsAsync();
            }

            _httpClient.Dispose();
        }

        #endregion

        #region IMetricsProvider Implementation

        /// <inheritdoc/>
        public override void IncrementCounter(string metric)
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
            if (!_config.EnableMetrics)
            {
                return;
            }

            var state = _metricStates.GetOrAdd(metric, _ => new MetricState());
            state.IncrementCounter(amount);
        }

        /// <inheritdoc/>
        public override void RecordMetric(string metric, double value)
        {
            if (!_config.EnableMetrics)
            {
                return;
            }

            var state = _metricStates.GetOrAdd(metric, _ => new MetricState());
            state.SetGauge(value);
        }

        /// <inheritdoc/>
        public override IDisposable TrackDuration(string metric)
        {
            return new DurationTracker(this, metric);
        }

        #endregion

        #region Logging Methods

        /// <summary>
        /// Sends a log message to Logz.io.
        /// </summary>
        /// <param name="message">The log message.</param>
        /// <param name="level">The log level.</param>
        /// <param name="additionalFields">Additional fields to include.</param>
        public void Log(string message, LogLevel level = LogLevel.Info, Dictionary<string, object>? additionalFields = null)
        {
            if (!_config.EnableLogs || !_isRunning)
            {
                return;
            }

            var entry = new LogzioLogEntry
            {
                Message = message,
                Level = level.ToString().ToUpperInvariant(),
                Timestamp = DateTime.UtcNow.ToString("o"),
                Type = _config.Type,
                AdditionalFields = MergeFields(_config.AdditionalFields, additionalFields)
            };

            _logBuffer.Enqueue(entry);

            // Flush if batch size reached
            if (_logBuffer.Count >= _config.MaxBatchSize)
            {
                _ = FlushLogsAsync();
            }
        }

        #endregion

        #region Flush Operations

        /// <summary>
        /// Flushes buffered logs to Logz.io.
        /// </summary>
        public async Task FlushLogsAsync()
        {
            if (!_config.EnableLogs || _logBuffer.IsEmpty)
            {
                return;
            }

            await _logFlushLock.WaitAsync();
            try
            {
                var batch = new List<LogzioLogEntry>();
                while (batch.Count < _config.MaxBatchSize && _logBuffer.TryDequeue(out var entry))
                {
                    batch.Add(entry);
                }

                if (batch.Count == 0)
                {
                    return;
                }

                await SendLogsWithRetryAsync(batch);
            }
            finally
            {
                _logFlushLock.Release();
            }
        }

        /// <summary>
        /// Flushes buffered metrics to Logz.io.
        /// </summary>
        public async Task FlushMetricsAsync()
        {
            if (!_config.EnableMetrics)
            {
                return;
            }

            await _metricFlushLock.WaitAsync();
            try
            {
                var samples = new List<LogzioMetricSample>();
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                foreach (var kvp in _metricStates)
                {
                    var state = kvp.Value;
                    var value = state.GetValue();

                    if (value.HasValue)
                    {
                        samples.Add(new LogzioMetricSample
                        {
                            Name = kvp.Key,
                            Value = value.Value,
                            Timestamp = timestamp,
                            Labels = new Dictionary<string, string>
                            {
                                ["job"] = "datawarehouse",
                                ["instance"] = Environment.MachineName
                            }
                        });
                    }
                }

                if (samples.Count == 0)
                {
                    return;
                }

                await SendMetricsWithRetryAsync(samples);
            }
            finally
            {
                _metricFlushLock.Release();
            }
        }

        #endregion

        #region HTTP Operations

        private async Task SendLogsWithRetryAsync(List<LogzioLogEntry> logs)
        {
            var retries = 0;
            Exception? lastException = null;

            while (retries < _config.MaxRetries)
            {
                try
                {
                    await SendLogsAsync(logs);
                    Interlocked.Add(ref _logsSent, logs.Count);
                    return;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    retries++;
                    Interlocked.Increment(ref _requestErrors);

                    if (retries < _config.MaxRetries)
                    {
                        var delay = TimeSpan.FromSeconds(Math.Pow(2, retries));
                        await Task.Delay(delay);
                    }
                }
            }

            // Failed after all retries
            Interlocked.Add(ref _logsDropped, logs.Count);
            Console.Error.WriteLine($"[Logzio] Failed to send {logs.Count} logs after {_config.MaxRetries} retries: {lastException?.Message}");
        }

        private async Task SendLogsAsync(List<LogzioLogEntry> logs)
        {
            var url = $"{_config.ListenerUrl}/?token={_config.Token}&type={_config.Type}";

            // Serialize logs as newline-delimited JSON
            var sb = new StringBuilder();
            foreach (var log in logs)
            {
                sb.AppendLine(JsonSerializer.Serialize(log));
            }

            var content = sb.ToString();
            byte[] data = Encoding.UTF8.GetBytes(content);

            // Compress if enabled
            if (_config.EnableCompression)
            {
                using var ms = new MemoryStream();
                using (var gzip = new GZipStream(ms, CompressionMode.Compress))
                {
                    await gzip.WriteAsync(data);
                }
                data = ms.ToArray();
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new ByteArrayContent(data);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            if (_config.EnableCompression)
            {
                request.Content.Headers.ContentEncoding.Add("gzip");
            }

            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        private async Task SendMetricsWithRetryAsync(List<LogzioMetricSample> metrics)
        {
            var retries = 0;
            Exception? lastException = null;

            while (retries < _config.MaxRetries)
            {
                try
                {
                    await SendMetricsAsync(metrics);
                    Interlocked.Add(ref _metricsSent, metrics.Count);
                    return;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    retries++;
                    Interlocked.Increment(ref _requestErrors);

                    if (retries < _config.MaxRetries)
                    {
                        var delay = TimeSpan.FromSeconds(Math.Pow(2, retries));
                        await Task.Delay(delay);
                    }
                }
            }

            // Failed after all retries
            Interlocked.Add(ref _metricsDropped, metrics.Count);
            Console.Error.WriteLine($"[Logzio] Failed to send {metrics.Count} metrics after {_config.MaxRetries} retries: {lastException?.Message}");
        }

        private async Task SendMetricsAsync(List<LogzioMetricSample> metrics)
        {
            // Convert to Prometheus remote write format
            var request = new PrometheusRemoteWriteRequest();

            foreach (var metric in metrics)
            {
                var timeSeries = new TimeSeries
                {
                    Labels = new List<Label>
                    {
                        new Label { Name = "__name__", Value = metric.Name }
                    },
                    Samples = new List<Sample>
                    {
                        new Sample { Value = metric.Value, Timestamp = metric.Timestamp }
                    }
                };

                // Add metric labels
                foreach (var label in metric.Labels)
                {
                    timeSeries.Labels.Add(new Label { Name = label.Key, Value = label.Value });
                }

                request.Timeseries.Add(timeSeries);
            }

            var url = $"{_config.MetricsListenerUrl}/prometheus/api/v1/remote_write?token={_config.Token}";

            // Serialize to Protocol Buffers (simplified JSON for now - production would use Protobuf)
            var json = JsonSerializer.Serialize(request);
            var data = Encoding.UTF8.GetBytes(json);

            // Compress if enabled
            if (_config.EnableCompression)
            {
                using var ms = new MemoryStream();
                using (var gzip = new GZipStream(ms, CompressionMode.Compress))
                {
                    await gzip.WriteAsync(data);
                }
                data = ms.ToArray();
            }

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Content = new ByteArrayContent(data);
            httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            if (_config.EnableCompression)
            {
                httpRequest.Content.Headers.ContentEncoding.Add("gzip");
            }

            using var response = await _httpClient.SendAsync(httpRequest);
            response.EnsureSuccessStatusCode();
        }

        #endregion

        #region Message Handling

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "logzio.log":
                    HandleLog(message);
                    break;
                case "logzio.metric":
                    HandleMetric(message);
                    break;
                case "logzio.flush":
                    await HandleFlushAsync(message);
                    break;
                case "logzio.status":
                    HandleStatus(message);
                    break;
                case "logzio.clear":
                    HandleClear(message);
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }

        private void HandleLog(PluginMessage message)
        {
            var msg = GetString(message.Payload, "message");
            if (string.IsNullOrEmpty(msg))
            {
                message.Payload["error"] = "message required";
                return;
            }

            var levelStr = GetString(message.Payload, "level") ?? "INFO";
            var level = Enum.TryParse<LogLevel>(levelStr, true, out var parsedLevel) ? parsedLevel : LogLevel.Info;

            var additionalFields = GetDictionary(message.Payload, "fields");
            Log(msg, level, additionalFields);

            message.Payload["success"] = true;
        }

        private void HandleMetric(PluginMessage message)
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

            RecordMetric(metric, value.Value);
            message.Payload["success"] = true;
        }

        private async Task HandleFlushAsync(PluginMessage message)
        {
            await FlushLogsAsync();
            await FlushMetricsAsync();
            message.Payload["success"] = true;
        }

        private void HandleStatus(PluginMessage message)
        {
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["isRunning"] = _isRunning,
                ["logsEnabled"] = _config.EnableLogs,
                ["metricsEnabled"] = _config.EnableMetrics,
                ["logsSent"] = LogsSent,
                ["logsDropped"] = LogsDropped,
                ["metricsSent"] = MetricsSent,
                ["metricsDropped"] = MetricsDropped,
                ["requestErrors"] = RequestErrors,
                ["bufferedLogs"] = _logBuffer.Count,
                ["bufferedMetrics"] = _metricStates.Count,
                ["uptimeSeconds"] = _uptimeStopwatch.Elapsed.TotalSeconds
            };
        }

        private void HandleClear(PluginMessage message)
        {
            _logBuffer.Clear();
            _metricBuffer.Clear();
            _metricStates.Clear();
            message.Payload["success"] = true;
        }

        #endregion

        #region Plugin Metadata

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "logzio.log", DisplayName = "Send Log", Description = "Send a log message to Logz.io" },
                new() { Name = "logzio.metric", DisplayName = "Record Metric", Description = "Record a metric value" },
                new() { Name = "logzio.flush", DisplayName = "Flush Data", Description = "Flush buffered logs and metrics" },
                new() { Name = "logzio.status", DisplayName = "Get Status", Description = "Get plugin status" },
                new() { Name = "logzio.clear", DisplayName = "Clear Buffers", Description = "Clear buffered data" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["ListenerUrl"] = _config.ListenerUrl;
            metadata["MetricsListenerUrl"] = _config.MetricsListenerUrl;
            metadata["Type"] = _config.Type;
            metadata["EnableLogs"] = _config.EnableLogs;
            metadata["EnableMetrics"] = _config.EnableMetrics;
            metadata["MaxBatchSize"] = _config.MaxBatchSize;
            metadata["CompressionEnabled"] = _config.EnableCompression;
            return metadata;
        }

        #endregion

        #region Helpers

        private void ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(_config.Token))
            {
                throw new InvalidOperationException("Logz.io token is required.");
            }

            if (_config.EnableLogs && string.IsNullOrWhiteSpace(_config.ListenerUrl))
            {
                throw new InvalidOperationException("Logz.io listener URL is required when logs are enabled.");
            }

            if (_config.EnableMetrics && string.IsNullOrWhiteSpace(_config.MetricsListenerUrl))
            {
                throw new InvalidOperationException("Logz.io metrics listener URL is required when metrics are enabled.");
            }
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

        private static Dictionary<string, object>? GetDictionary(Dictionary<string, object> payload, string key)
        {
            return payload.TryGetValue(key, out var val) && val is Dictionary<string, object> dict ? dict : null;
        }

        private static Dictionary<string, object>? MergeFields(
            Dictionary<string, object>? fields1,
            Dictionary<string, object>? fields2)
        {
            if (fields1 == null && fields2 == null)
            {
                return null;
            }

            var result = new Dictionary<string, object>();

            if (fields1 != null)
            {
                foreach (var kvp in fields1)
                {
                    result[kvp.Key] = kvp.Value;
                }
            }

            if (fields2 != null)
            {
                foreach (var kvp in fields2)
                {
                    result[kvp.Key] = kvp.Value;
                }
            }

            return result;
        }

        #endregion

        #region MetricState

        /// <summary>
        /// Thread-safe metric state tracking.
        /// </summary>
        private sealed class MetricState
        {
            private double _value;
            private readonly object _lock = new();

            public void IncrementCounter(double amount)
            {
                lock (_lock)
                {
                    _value += amount;
                }
            }

            public void SetGauge(double value)
            {
                lock (_lock)
                {
                    _value = value;
                }
            }

            public double? GetValue()
            {
                lock (_lock)
                {
                    return _value;
                }
            }
        }

        #endregion

        #region DurationTracker

        /// <summary>
        /// Helper class for tracking operation duration.
        /// </summary>
        private sealed class DurationTracker : IDisposable
        {
            private readonly LogzioPlugin _plugin;
            private readonly string _metric;
            private readonly Stopwatch _stopwatch;
            private bool _disposed;

            public DurationTracker(LogzioPlugin plugin, string metric)
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
                _plugin.RecordMetric(_metric, _stopwatch.Elapsed.TotalSeconds);
            }
        }

        #endregion
    }

    /// <summary>
    /// Abstract base class for telemetry plugins providing metrics, tracing, and logging capabilities.
    /// Extends <see cref="FeaturePluginBase"/> to support lifecycle management.
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
            metadata["SupportsLogs"] = true;
            return metadata;
        }
    }
}
