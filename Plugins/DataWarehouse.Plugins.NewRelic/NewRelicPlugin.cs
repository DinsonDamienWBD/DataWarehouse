using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.NewRelic
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
            metadata["SupportsLogs"] = true;
            metadata["SupportsTags"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Production-ready New Relic telemetry plugin implementing the New Relic Metric API and Log API.
    /// Provides metrics and logs with batching, retry logic, and background flushing.
    ///
    /// Features:
    /// - New Relic Metric API (count, gauge, summary types)
    /// - New Relic Log API
    /// - Automatic batching with configurable batch sizes
    /// - Background flushing with configurable intervals
    /// - Retry logic with exponential backoff
    /// - Common attributes for all metrics and logs
    /// - Regional endpoint support (US/EU)
    ///
    /// Message Commands:
    /// - newrelic.increment: Increment a counter
    /// - newrelic.record: Record a gauge value
    /// - newrelic.observe: Observe a summary value
    /// - newrelic.log: Send a log entry
    /// - newrelic.flush: Manually flush pending data
    /// - newrelic.status: Get plugin status
    /// </summary>
    public sealed class NewRelicPlugin : TelemetryPluginBase
    {
        private readonly NewRelicConfiguration _config;
        private readonly HttpClient _httpClient;
        private readonly ConcurrentQueue<NewRelicMetric> _metricQueue;
        private readonly ConcurrentQueue<NewRelicLogEntry> _logQueue;
        private readonly ConcurrentDictionary<string, double> _counters;
        private readonly ConcurrentDictionary<string, double> _gauges;
        private readonly ConcurrentDictionary<string, SummaryAggregator> _summaries;
        private CancellationTokenSource? _cts;
        private Task? _flushTask;
        private readonly object _lock = new();
        private bool _isRunning;
        private long _metricsSent;
        private long _logsSent;
        private long _metricsDropped;
        private long _logsDropped;
        private long _apiErrors;
        private readonly Stopwatch _uptimeStopwatch = new();

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.telemetry.newrelic";

        /// <inheritdoc/>
        public override string Name => "New Relic Telemetry Plugin";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <summary>
        /// Gets the plugin configuration.
        /// </summary>
        public NewRelicConfiguration Configuration => _config;

        /// <summary>
        /// Gets whether the plugin is running.
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Gets the total number of metrics sent.
        /// </summary>
        public long MetricsSent => Interlocked.Read(ref _metricsSent);

        /// <summary>
        /// Gets the total number of logs sent.
        /// </summary>
        public long LogsSent => Interlocked.Read(ref _logsSent);

        /// <summary>
        /// Gets the total number of metrics dropped.
        /// </summary>
        public long MetricsDropped => Interlocked.Read(ref _metricsDropped);

        /// <summary>
        /// Gets the total number of logs dropped.
        /// </summary>
        public long LogsDropped => Interlocked.Read(ref _logsDropped);

        /// <summary>
        /// Gets the total number of API errors.
        /// </summary>
        public long ApiErrors => Interlocked.Read(ref _apiErrors);

        /// <summary>
        /// Initializes a new instance of the <see cref="NewRelicPlugin"/> class.
        /// </summary>
        /// <param name="config">The plugin configuration.</param>
        /// <exception cref="ArgumentNullException">Thrown when config is null.</exception>
        /// <exception cref="ArgumentException">Thrown when ApiKey is empty.</exception>
        public NewRelicPlugin(NewRelicConfiguration? config = null)
        {
            _config = config ?? new NewRelicConfiguration();

            if (string.IsNullOrWhiteSpace(_config.ApiKey))
            {
                throw new ArgumentException("New Relic API Key is required.", nameof(config));
            }

            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(_config.HttpTimeoutSeconds)
            };
            _httpClient.DefaultRequestHeaders.Add("Api-Key", _config.ApiKey);
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            _metricQueue = new ConcurrentQueue<NewRelicMetric>();
            _logQueue = new ConcurrentQueue<NewRelicLogEntry>();
            _counters = new ConcurrentDictionary<string, double>(StringComparer.Ordinal);
            _gauges = new ConcurrentDictionary<string, double>(StringComparer.Ordinal);
            _summaries = new ConcurrentDictionary<string, SummaryAggregator>(StringComparer.Ordinal);
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

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _flushTask = FlushLoopAsync(_cts.Token);

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

            // Final flush
            await FlushAsync();

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
                    // Timeout waiting for flush
                }
            }

            _cts?.Dispose();
            _cts = null;
            _flushTask = null;
        }

        #endregion

        #region IMetricsProvider Implementation

        /// <inheritdoc/>
        public override void IncrementCounter(string metric)
        {
            IncrementCounter(metric, 1, null);
        }

        /// <summary>
        /// Increments a counter metric by the specified amount.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="amount">The amount to increment.</param>
        /// <param name="attributes">Optional attributes.</param>
        public void IncrementCounter(string metric, double amount = 1, Dictionary<string, object>? attributes = null)
        {
            if (!_config.EnableMetrics || !_isRunning)
            {
                return;
            }

            var key = GetMetricKey(metric, attributes);
            _counters.AddOrUpdate(key, amount, (_, current) => current + amount);
        }

        /// <inheritdoc/>
        public override void RecordMetric(string metric, double value)
        {
            RecordGauge(metric, value, null);
        }

        /// <summary>
        /// Records a gauge metric value.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="value">The value to record.</param>
        /// <param name="attributes">Optional attributes.</param>
        public void RecordGauge(string metric, double value, Dictionary<string, object>? attributes = null)
        {
            if (!_config.EnableMetrics || !_isRunning)
            {
                return;
            }

            var key = GetMetricKey(metric, attributes);
            _gauges.AddOrUpdate(key, value, (_, _) => value);
        }

        /// <summary>
        /// Observes a value for a summary metric.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="value">The observed value.</param>
        /// <param name="attributes">Optional attributes.</param>
        public void ObserveSummary(string metric, double value, Dictionary<string, object>? attributes = null)
        {
            if (!_config.EnableMetrics || !_isRunning)
            {
                return;
            }

            var key = GetMetricKey(metric, attributes);
            var aggregator = _summaries.GetOrAdd(key, _ => new SummaryAggregator());
            aggregator.Observe(value);
        }

        /// <inheritdoc/>
        public override IDisposable TrackDuration(string metric)
        {
            return TrackDuration(metric, null);
        }

        /// <summary>
        /// Tracks the duration of an operation as a summary observation.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="attributes">Optional attributes.</param>
        /// <returns>A disposable that records the duration when disposed.</returns>
        public IDisposable TrackDuration(string metric, Dictionary<string, object>? attributes = null)
        {
            return new DurationTracker(this, metric, attributes);
        }

        #endregion

        #region Log Methods

        /// <summary>
        /// Sends a log entry to New Relic.
        /// </summary>
        /// <param name="message">The log message.</param>
        /// <param name="level">The log level.</param>
        /// <param name="attributes">Optional attributes.</param>
        public void Log(string message, string? level = null, Dictionary<string, object>? attributes = null)
        {
            if (!_config.EnableLogs || !_isRunning)
            {
                return;
            }

            var logEntry = new NewRelicLogEntry
            {
                Message = message,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Level = level,
                Attributes = MergeAttributes(attributes)
            };

            _logQueue.Enqueue(logEntry);

            // Check queue size
            if (_logQueue.Count > _config.LogBatchSize * 10)
            {
                // Drop oldest entries
                while (_logQueue.Count > _config.LogBatchSize * 5)
                {
                    if (_logQueue.TryDequeue(out _))
                    {
                        Interlocked.Increment(ref _logsDropped);
                    }
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
                case "newrelic.increment":
                    HandleIncrement(message);
                    break;
                case "newrelic.record":
                    HandleRecord(message);
                    break;
                case "newrelic.observe":
                    HandleObserve(message);
                    break;
                case "newrelic.log":
                    HandleLog(message);
                    break;
                case "newrelic.flush":
                    await HandleFlushAsync(message);
                    break;
                case "newrelic.status":
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
            var attributes = GetAttributes(message.Payload);

            IncrementCounter(metric, amount, attributes);
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

            var attributes = GetAttributes(message.Payload);
            RecordGauge(metric, value.Value, attributes);
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

            var attributes = GetAttributes(message.Payload);
            ObserveSummary(metric, value.Value, attributes);
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

            var level = GetString(message.Payload, "level");
            var attributes = GetAttributes(message.Payload);

            Log(logMessage, level, attributes);
            message.Payload["success"] = true;
        }

        private async Task HandleFlushAsync(PluginMessage message)
        {
            await FlushAsync();
            message.Payload["success"] = true;
        }

        private void HandleStatus(PluginMessage message)
        {
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["isRunning"] = _isRunning,
                ["region"] = _config.Region.ToString(),
                ["metricsSent"] = MetricsSent,
                ["logsSent"] = LogsSent,
                ["metricsDropped"] = MetricsDropped,
                ["logsDropped"] = LogsDropped,
                ["apiErrors"] = ApiErrors,
                ["metricQueueSize"] = _metricQueue.Count,
                ["logQueueSize"] = _logQueue.Count,
                ["uptimeSeconds"] = _uptimeStopwatch.Elapsed.TotalSeconds
            };
        }

        #endregion

        #region Flush Logic

        private async Task FlushLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_config.FlushIntervalSeconds), ct);
                    await FlushAsync();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[NewRelic] Error in flush loop: {ex.Message}");
                }
            }
        }

        private async Task FlushAsync()
        {
            // Collect pending metrics
            await FlushMetricsAsync();

            // Flush log queue
            await FlushLogsAsync();
        }

        private async Task FlushMetricsAsync()
        {
            if (!_config.EnableMetrics)
            {
                return;
            }

            var metrics = new List<NewRelicMetric>();
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Collect counters
            foreach (var kvp in _counters)
            {
                if (_counters.TryRemove(kvp.Key, out var value))
                {
                    var (name, attrs) = ParseMetricKey(kvp.Key);
                    metrics.Add(new NewRelicMetric
                    {
                        Name = name,
                        Type = NewRelicMetricType.Count,
                        Value = value,
                        Timestamp = timestamp,
                        Attributes = attrs
                    });
                }
            }

            // Collect gauges
            foreach (var kvp in _gauges)
            {
                if (_gauges.TryRemove(kvp.Key, out var value))
                {
                    var (name, attrs) = ParseMetricKey(kvp.Key);
                    metrics.Add(new NewRelicMetric
                    {
                        Name = name,
                        Type = NewRelicMetricType.Gauge,
                        Value = value,
                        Timestamp = timestamp,
                        Attributes = attrs
                    });
                }
            }

            // Collect summaries
            foreach (var kvp in _summaries)
            {
                if (_summaries.TryRemove(kvp.Key, out var aggregator) && aggregator.HasData())
                {
                    var (count, sum, min, max) = aggregator.GetAndReset();
                    var (name, attrs) = ParseMetricKey(kvp.Key);
                    metrics.Add(new NewRelicMetric
                    {
                        Name = name,
                        Type = NewRelicMetricType.Summary,
                        Timestamp = timestamp,
                        IntervalMs = _config.FlushIntervalSeconds * 1000,
                        Count = count,
                        Sum = sum,
                        Min = min,
                        Max = max,
                        Attributes = attrs
                    });
                }
            }

            if (metrics.Count == 0)
            {
                return;
            }

            // Send in batches
            for (var i = 0; i < metrics.Count; i += _config.MetricBatchSize)
            {
                var batch = metrics.Skip(i).Take(_config.MetricBatchSize).ToList();
                await SendMetricBatchAsync(batch);
            }
        }

        private async Task FlushLogsAsync()
        {
            if (!_config.EnableLogs)
            {
                return;
            }

            var logs = new List<NewRelicLogEntry>();

            while (_logQueue.TryDequeue(out var log) && logs.Count < _config.LogBatchSize)
            {
                logs.Add(log);
            }

            if (logs.Count == 0)
            {
                return;
            }

            await SendLogBatchAsync(logs);
        }

        #endregion

        #region HTTP Requests

        private async Task SendMetricBatchAsync(List<NewRelicMetric> metrics)
        {
            var payload = new MetricPayload
            {
                Metrics = new List<MetricBatch>
                {
                    new MetricBatch
                    {
                        Metrics = metrics,
                        Common = _config.CommonAttributes != null ? new CommonAttributes
                        {
                            Attributes = _config.CommonAttributes
                        } : null
                    }
                }
            };

            await SendWithRetryAsync(_config.MetricApiEndpoint, payload, metrics.Count, true);
        }

        private async Task SendLogBatchAsync(List<NewRelicLogEntry> logs)
        {
            var payload = new LogPayload
            {
                Logs = logs,
                Common = _config.CommonAttributes != null ? new LogCommonAttributes
                {
                    Attributes = _config.CommonAttributes
                } : null
            };

            await SendWithRetryAsync(_config.LogApiEndpoint, payload, logs.Count, false);
        }

        private async Task SendWithRetryAsync<T>(string endpoint, T payload, int itemCount, bool isMetric)
        {
            var attempts = 0;
            var delay = TimeSpan.FromMilliseconds(100);

            while (attempts < _config.MaxRetries)
            {
                try
                {
                    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    });
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var response = await _httpClient.PostAsync(endpoint, content);

                    if (response.IsSuccessStatusCode)
                    {
                        if (isMetric)
                        {
                            Interlocked.Add(ref _metricsSent, itemCount);
                        }
                        else
                        {
                            Interlocked.Add(ref _logsSent, itemCount);
                        }
                        return;
                    }

                    var responseBody = await response.Content.ReadAsStringAsync();
                    Console.Error.WriteLine($"[NewRelic] HTTP {response.StatusCode}: {responseBody}");

                    // Retry on 5xx errors
                    if ((int)response.StatusCode >= 500)
                    {
                        attempts++;
                        if (attempts < _config.MaxRetries)
                        {
                            await Task.Delay(delay);
                            delay *= 2; // Exponential backoff
                            continue;
                        }
                    }

                    // Don't retry on client errors
                    Interlocked.Increment(ref _apiErrors);
                    if (isMetric)
                    {
                        Interlocked.Add(ref _metricsDropped, itemCount);
                    }
                    else
                    {
                        Interlocked.Add(ref _logsDropped, itemCount);
                    }
                    return;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[NewRelic] Request error: {ex.Message}");
                    attempts++;

                    if (attempts < _config.MaxRetries)
                    {
                        await Task.Delay(delay);
                        delay *= 2;
                    }
                    else
                    {
                        Interlocked.Increment(ref _apiErrors);
                        if (isMetric)
                        {
                            Interlocked.Add(ref _metricsDropped, itemCount);
                        }
                        else
                        {
                            Interlocked.Add(ref _logsDropped, itemCount);
                        }
                    }
                }
            }
        }

        #endregion

        #region Helpers

        private string GetMetricKey(string metric, Dictionary<string, object>? attributes)
        {
            if (attributes == null || attributes.Count == 0)
            {
                return metric;
            }

            var key = new StringBuilder(metric);
            foreach (var attr in attributes.OrderBy(a => a.Key))
            {
                key.Append('|');
                key.Append(attr.Key);
                key.Append('=');
                key.Append(attr.Value);
            }
            return key.ToString();
        }

        private (string name, Dictionary<string, object>? attributes) ParseMetricKey(string key)
        {
            var parts = key.Split('|');
            if (parts.Length == 1)
            {
                return (key, null);
            }

            var name = parts[0];
            var attributes = new Dictionary<string, object>();
            for (var i = 1; i < parts.Length; i++)
            {
                var attrParts = parts[i].Split('=', 2);
                if (attrParts.Length == 2)
                {
                    attributes[attrParts[0]] = attrParts[1];
                }
            }
            return (name, attributes.Count > 0 ? attributes : null);
        }

        private Dictionary<string, object>? MergeAttributes(Dictionary<string, object>? attributes)
        {
            if (_config.CommonAttributes == null && attributes == null)
            {
                return null;
            }

            var merged = new Dictionary<string, object>();

            if (_config.CommonAttributes != null)
            {
                foreach (var kvp in _config.CommonAttributes)
                {
                    merged[kvp.Key] = kvp.Value;
                }
            }

            if (attributes != null)
            {
                foreach (var kvp in attributes)
                {
                    merged[kvp.Key] = kvp.Value;
                }
            }

            return merged.Count > 0 ? merged : null;
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

        private static Dictionary<string, object>? GetAttributes(Dictionary<string, object> payload)
        {
            if (!payload.TryGetValue("attributes", out var attributesObj))
            {
                return null;
            }

            if (attributesObj is Dictionary<string, object> dict)
            {
                return dict;
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
                new() { Name = "newrelic.increment", DisplayName = "Increment Counter", Description = "Increment a counter metric" },
                new() { Name = "newrelic.record", DisplayName = "Record Gauge", Description = "Record a gauge metric value" },
                new() { Name = "newrelic.observe", DisplayName = "Observe Summary", Description = "Observe a summary value" },
                new() { Name = "newrelic.log", DisplayName = "Send Log", Description = "Send a log entry" },
                new() { Name = "newrelic.flush", DisplayName = "Flush", Description = "Manually flush pending data" },
                new() { Name = "newrelic.status", DisplayName = "Get Status", Description = "Get plugin status" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Region"] = _config.Region.ToString();
            metadata["AccountId"] = _config.AccountId;
            metadata["EnableMetrics"] = _config.EnableMetrics;
            metadata["EnableLogs"] = _config.EnableLogs;
            metadata["MetricBatchSize"] = _config.MetricBatchSize;
            metadata["LogBatchSize"] = _config.LogBatchSize;
            metadata["FlushIntervalSeconds"] = _config.FlushIntervalSeconds;
            metadata["SupportsCounters"] = true;
            metadata["SupportsGauges"] = true;
            metadata["SupportsSummaries"] = true;
            metadata["SupportsLogs"] = true;
            return metadata;
        }

        #endregion

        #region Duration Tracker

        /// <summary>
        /// Helper class for tracking operation duration.
        /// </summary>
        private sealed class DurationTracker : IDisposable
        {
            private readonly NewRelicPlugin _plugin;
            private readonly string _metric;
            private readonly Dictionary<string, object>? _attributes;
            private readonly Stopwatch _stopwatch;
            private bool _disposed;

            public DurationTracker(NewRelicPlugin plugin, string metric, Dictionary<string, object>? attributes)
            {
                _plugin = plugin;
                _metric = metric;
                _attributes = attributes;
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
                _plugin.ObserveSummary(_metric, _stopwatch.Elapsed.TotalSeconds, _attributes);
            }
        }

        #endregion
    }
}
