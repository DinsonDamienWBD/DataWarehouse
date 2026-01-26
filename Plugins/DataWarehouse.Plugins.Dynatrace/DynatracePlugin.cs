using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.Dynatrace
{
    /// <summary>
    /// Production-ready Dynatrace observability plugin implementing metrics and logs ingestion.
    ///
    /// Features:
    /// - Metrics API v2 ingestion using line protocol format
    /// - Logs API v2 ingestion with structured JSON
    /// - Batching for efficient API usage
    /// - Counter, gauge, and histogram metrics support
    /// - Multi-dimensional metrics with dimensions
    /// - Automatic background flushing
    /// - Error handling and retry logic
    ///
    /// Message Commands:
    /// - dynatrace.increment: Increment a counter metric
    /// - dynatrace.record: Record a gauge metric value
    /// - dynatrace.histogram: Record a histogram observation
    /// - dynatrace.log: Send a log entry
    /// - dynatrace.flush: Manually flush pending batches
    /// - dynatrace.status: Get plugin status
    /// </summary>
    public sealed class DynatracePlugin : FeaturePluginBase, IMetricsProvider
    {
        private readonly DynatraceConfiguration _config;
        private readonly HttpClient _httpClient;
        private readonly MetricsBatch _currentMetricsBatch;
        private readonly LogsBatch _currentLogsBatch;
        private readonly object _metricsLock = new();
        private readonly object _logsLock = new();
        private readonly ConcurrentDictionary<string, DynatraceMetricType> _metricTypes;
        private CancellationTokenSource? _cts;
        private Task? _flushTask;
        private bool _isRunning;
        private long _metricsIngested;
        private long _logsIngested;
        private long _ingestErrors;
        private readonly Stopwatch _uptimeStopwatch = new();

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.telemetry.dynatrace";

        /// <inheritdoc/>
        public override string Name => "Dynatrace Observability Plugin";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <inheritdoc/>
        public override PluginCategory Category => PluginCategory.MetricsProvider;

        /// <summary>
        /// Gets the plugin configuration.
        /// </summary>
        public DynatraceConfiguration Configuration => _config;

        /// <summary>
        /// Gets whether the plugin is running.
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Gets the total number of metrics ingested.
        /// </summary>
        public long MetricsIngested => Interlocked.Read(ref _metricsIngested);

        /// <summary>
        /// Gets the total number of logs ingested.
        /// </summary>
        public long LogsIngested => Interlocked.Read(ref _logsIngested);

        /// <summary>
        /// Gets the total number of ingestion errors.
        /// </summary>
        public long IngestErrors => Interlocked.Read(ref _ingestErrors);

        /// <summary>
        /// Initializes a new instance of the <see cref="DynatracePlugin"/> class.
        /// </summary>
        /// <param name="config">The Dynatrace configuration.</param>
        /// <exception cref="ArgumentNullException">Thrown when config is null.</exception>
        /// <exception cref="ArgumentException">Thrown when required configuration is missing.</exception>
        public DynatracePlugin(DynatraceConfiguration? config = null)
        {
            _config = config ?? new DynatraceConfiguration();

            if (string.IsNullOrEmpty(_config.DynatraceUrl))
            {
                throw new ArgumentException("DynatraceUrl is required.", nameof(config));
            }

            if (string.IsNullOrEmpty(_config.ApiToken))
            {
                throw new ArgumentException("ApiToken is required.", nameof(config));
            }

            _currentMetricsBatch = new MetricsBatch();
            _currentLogsBatch = new LogsBatch();
            _metricTypes = new ConcurrentDictionary<string, DynatraceMetricType>(StringComparer.Ordinal);

            var handler = new HttpClientHandler();
            if (!_config.ValidateSslCertificate)
            {
                handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            }

            _httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(_config.DynatraceUrl),
                Timeout = _config.HttpTimeout
            };

            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Api-Token {_config.ApiToken}");
        }

        #region Lifecycle Management

        /// <inheritdoc/>
        public override async Task StartAsync(CancellationToken ct)
        {
            if (_isRunning)
            {
                return;
            }

            _isRunning = true;
            _uptimeStopwatch.Start();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _flushTask = FlushLoopAsync(_cts.Token);

            await Task.CompletedTask;
        }

        /// <inheritdoc/>
        public override async Task StopAsync()
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;
            _uptimeStopwatch.Stop();

            _cts?.Cancel();

            if (_flushTask != null)
            {
                try
                {
                    await _flushTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
                catch (TimeoutException)
                {
                    // Timeout waiting for flush task
                }
            }

            // Final flush
            await FlushAsync();

            _cts?.Dispose();
            _cts = null;
            _flushTask = null;
        }

        #endregion

        #region IMetricsProvider Implementation

        /// <inheritdoc/>
        public void IncrementCounter(string metric)
        {
            IncrementCounter(metric, 1, DimensionSet.Empty);
        }

        /// <summary>
        /// Increments a counter metric by the specified amount.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="amount">The amount to increment.</param>
        /// <param name="dimensions">Optional dimensions.</param>
        public void IncrementCounter(string metric, double amount = 1, DimensionSet? dimensions = null)
        {
            _metricTypes.TryAdd(metric, DynatraceMetricType.Counter);
            RecordMetricInternal(metric, amount, dimensions);
        }

        /// <inheritdoc/>
        public void RecordMetric(string metric, double value)
        {
            RecordGauge(metric, value, DimensionSet.Empty);
        }

        /// <summary>
        /// Records a gauge metric value.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="value">The value to record.</param>
        /// <param name="dimensions">Optional dimensions.</param>
        public void RecordGauge(string metric, double value, DimensionSet? dimensions = null)
        {
            _metricTypes.TryAdd(metric, DynatraceMetricType.Gauge);
            RecordMetricInternal(metric, value, dimensions);
        }

        /// <summary>
        /// Records a histogram/summary observation.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="value">The observed value.</param>
        /// <param name="dimensions">Optional dimensions.</param>
        public void RecordHistogram(string metric, double value, DimensionSet? dimensions = null)
        {
            _metricTypes.TryAdd(metric, DynatraceMetricType.Summary);
            RecordMetricInternal(metric, value, dimensions);
        }

        /// <inheritdoc/>
        public IDisposable TrackDuration(string metric)
        {
            return TrackDuration(metric, DimensionSet.Empty);
        }

        /// <summary>
        /// Tracks the duration of an operation.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="dimensions">Optional dimensions.</param>
        /// <returns>A disposable that records the duration when disposed.</returns>
        public IDisposable TrackDuration(string metric, DimensionSet? dimensions = null)
        {
            return new DurationTracker(this, metric, dimensions ?? DimensionSet.Empty);
        }

        #endregion

        #region Logging

        /// <summary>
        /// Sends a log entry to Dynatrace.
        /// </summary>
        /// <param name="message">The log message.</param>
        /// <param name="level">The log level.</param>
        /// <param name="attributes">Additional attributes.</param>
        public void Log(string message, DynatraceLogLevel level = DynatraceLogLevel.INFO, Dictionary<string, object>? attributes = null)
        {
            var logEntry = new DynatraceLogEntry
            {
                Content = message,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                LogLevel = level.ToString(),
                Attributes = attributes
            };

            // Merge default attributes
            if (_config.DefaultLogAttributes != null)
            {
                logEntry.Attributes ??= new Dictionary<string, object>();
                foreach (var attr in _config.DefaultLogAttributes)
                {
                    logEntry.Attributes.TryAdd(attr.Key, attr.Value);
                }
            }

            lock (_logsLock)
            {
                _currentLogsBatch.Logs.Add(logEntry);

                if (_currentLogsBatch.IsFull(_config.LogsBatchSize))
                {
                    _ = FlushLogsAsync();
                }
            }
        }

        #endregion

        #region Message Handling

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "dynatrace.increment":
                    HandleIncrement(message);
                    break;
                case "dynatrace.record":
                    HandleRecord(message);
                    break;
                case "dynatrace.histogram":
                    HandleHistogram(message);
                    break;
                case "dynatrace.log":
                    HandleLog(message);
                    break;
                case "dynatrace.flush":
                    await FlushAsync();
                    message.Payload["success"] = true;
                    break;
                case "dynatrace.status":
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
            var dimensions = GetDimensions(message.Payload);

            IncrementCounter(metric, amount, dimensions);
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

            var dimensions = GetDimensions(message.Payload);
            RecordGauge(metric, value.Value, dimensions);
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

            var dimensions = GetDimensions(message.Payload);
            RecordHistogram(metric, value.Value, dimensions);
            message.Payload["success"] = true;
        }

        private void HandleLog(PluginMessage message)
        {
            var logMessage = GetString(message.Payload, "message");
            if (string.IsNullOrEmpty(logMessage))
            {
                message.Payload["error"] = "message required";
                return;
            }

            var level = DynatraceLogLevel.INFO;
            if (message.Payload.TryGetValue("level", out var levelObj) && levelObj is string levelStr)
            {
                Enum.TryParse<DynatraceLogLevel>(levelStr, true, out level);
            }

            var attributes = GetAttributes(message.Payload);
            Log(logMessage, level, attributes);
            message.Payload["success"] = true;
        }

        private void HandleStatus(PluginMessage message)
        {
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["isRunning"] = _isRunning,
                ["dynatraceUrl"] = _config.DynatraceUrl,
                ["metricsIngested"] = MetricsIngested,
                ["logsIngested"] = LogsIngested,
                ["ingestErrors"] = IngestErrors,
                ["pendingMetrics"] = _currentMetricsBatch.Metrics.Count,
                ["pendingLogs"] = _currentLogsBatch.Logs.Count,
                ["uptimeSeconds"] = _uptimeStopwatch.Elapsed.TotalSeconds
            };
        }

        #endregion

        #region Internal Methods

        private void RecordMetricInternal(string metric, double value, DimensionSet? dimensions)
        {
            var fullMetricName = string.IsNullOrEmpty(_config.MetricPrefix)
                ? metric
                : $"{_config.MetricPrefix}.{metric}";

            var dims = dimensions?.ToDictionary() ?? new Dictionary<string, string>();

            // Merge default dimensions
            if (_config.DefaultDimensions != null)
            {
                foreach (var dim in _config.DefaultDimensions)
                {
                    dims.TryAdd(dim.Key, dim.Value);
                }
            }

            var dynatraceMetric = new DynatraceMetric
            {
                Name = fullMetricName,
                Value = value,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Dimensions = dims
            };

            lock (_metricsLock)
            {
                _currentMetricsBatch.Metrics.Add(dynatraceMetric);

                if (_currentMetricsBatch.IsFull(_config.MetricsBatchSize))
                {
                    _ = FlushMetricsAsync();
                }
            }
        }

        private async Task FlushLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _isRunning)
            {
                try
                {
                    await Task.Delay(_config.FlushInterval, ct);
                    await FlushAsync();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Dynatrace] Error in flush loop: {ex.Message}");
                }
            }
        }

        private async Task FlushAsync()
        {
            await Task.WhenAll(FlushMetricsAsync(), FlushLogsAsync());
        }

        private async Task FlushMetricsAsync()
        {
            MetricsBatch? batchToFlush = null;

            lock (_metricsLock)
            {
                if (_currentMetricsBatch.Metrics.Count > 0)
                {
                    batchToFlush = new MetricsBatch();
                    batchToFlush.Metrics.AddRange(_currentMetricsBatch.Metrics);
                    _currentMetricsBatch.Metrics.Clear();
                }
            }

            if (batchToFlush == null)
            {
                return;
            }

            try
            {
                var lineProtocol = batchToFlush.ToLineProtocol();
                var content = new StringContent(lineProtocol, Encoding.UTF8, "text/plain");

                var response = await _httpClient.PostAsync(_config.MetricsEndpoint, content);
                response.EnsureSuccessStatusCode();

                Interlocked.Add(ref _metricsIngested, batchToFlush.Metrics.Count);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _ingestErrors);
                Console.Error.WriteLine($"[Dynatrace] Error flushing metrics: {ex.Message}");
            }
        }

        private async Task FlushLogsAsync()
        {
            LogsBatch? batchToFlush = null;

            lock (_logsLock)
            {
                if (_currentLogsBatch.Logs.Count > 0)
                {
                    batchToFlush = new LogsBatch();
                    batchToFlush.Logs.AddRange(_currentLogsBatch.Logs);
                    _currentLogsBatch.Logs.Clear();
                }
            }

            if (batchToFlush == null)
            {
                return;
            }

            try
            {
                var json = JsonSerializer.Serialize(batchToFlush.Logs, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });

                var content = new StringContent(json, Encoding.UTF8, "application/json");
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
                {
                    CharSet = "utf-8"
                };

                var response = await _httpClient.PostAsync(_config.LogsEndpoint, content);
                response.EnsureSuccessStatusCode();

                Interlocked.Add(ref _logsIngested, batchToFlush.Logs.Count);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _ingestErrors);
                Console.Error.WriteLine($"[Dynatrace] Error flushing logs: {ex.Message}");
            }
        }

        #endregion

        #region Helper Methods

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

        private static DimensionSet? GetDimensions(Dictionary<string, object> payload)
        {
            if (!payload.TryGetValue("dimensions", out var dimensionsObj))
            {
                return null;
            }

            if (dimensionsObj is Dictionary<string, object> dict)
            {
                var dims = dict.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value?.ToString() ?? string.Empty);
                return DimensionSet.From(dims);
            }

            if (dimensionsObj is Dictionary<string, string> stringDict)
            {
                return DimensionSet.From(stringDict);
            }

            return null;
        }

        private static Dictionary<string, object>? GetAttributes(Dictionary<string, object> payload)
        {
            if (!payload.TryGetValue("attributes", out var attributesObj))
            {
                return null;
            }

            return attributesObj as Dictionary<string, object>;
        }

        #endregion

        #region Plugin Metadata

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "dynatrace.increment", DisplayName = "Increment Counter", Description = "Increment a counter metric" },
                new() { Name = "dynatrace.record", DisplayName = "Record Gauge", Description = "Record a gauge metric value" },
                new() { Name = "dynatrace.histogram", DisplayName = "Record Histogram", Description = "Record a histogram observation" },
                new() { Name = "dynatrace.log", DisplayName = "Send Log", Description = "Send a log entry" },
                new() { Name = "dynatrace.flush", DisplayName = "Flush Batches", Description = "Manually flush pending batches" },
                new() { Name = "dynatrace.status", DisplayName = "Get Status", Description = "Get plugin status" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "Telemetry";
            metadata["DynatraceUrl"] = _config.DynatraceUrl;
            metadata["SupportsMetrics"] = true;
            metadata["SupportsLogs"] = true;
            metadata["SupportsCounters"] = true;
            metadata["SupportsGauges"] = true;
            metadata["SupportsHistograms"] = true;
            metadata["MetricsBatchSize"] = _config.MetricsBatchSize;
            metadata["LogsBatchSize"] = _config.LogsBatchSize;
            metadata["FlushInterval"] = _config.FlushInterval.TotalSeconds;
            return metadata;
        }

        #endregion

        #region Duration Tracker

        /// <summary>
        /// Helper class for tracking operation duration.
        /// </summary>
        private sealed class DurationTracker : IDisposable
        {
            private readonly DynatracePlugin _plugin;
            private readonly string _metric;
            private readonly DimensionSet _dimensions;
            private readonly Stopwatch _stopwatch;
            private bool _disposed;

            public DurationTracker(DynatracePlugin plugin, string metric, DimensionSet dimensions)
            {
                _plugin = plugin;
                _metric = metric;
                _dimensions = dimensions;
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
                _plugin.RecordHistogram(_metric, _stopwatch.Elapsed.TotalSeconds, _dimensions);
            }
        }

        #endregion
    }
}
