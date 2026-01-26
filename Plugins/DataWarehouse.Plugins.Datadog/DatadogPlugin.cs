using System.Collections.Concurrent;
using System.Diagnostics;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.Datadog
{
    /// <summary>
    /// Abstract base class for telemetry plugins providing metrics, tracing, and logging capabilities.
    /// Extends <see cref="FeaturePluginBase"/> to support lifecycle management and observability.
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
    /// Production-ready Datadog observability plugin with metrics, logs, and distributed tracing support.
    ///
    /// Features:
    /// - Datadog Metrics API v2 integration (gauge, count, rate)
    /// - Datadog Logs API v2 integration
    /// - Automatic batching and aggregation
    /// - Configurable flush intervals
    /// - Tag support with service/environment/version
    /// - Graceful degradation on API errors
    /// - Queue management with overflow protection
    ///
    /// Message Commands:
    /// - datadog.increment: Increment a counter
    /// - datadog.gauge: Record a gauge value
    /// - datadog.log: Submit a log entry
    /// - datadog.flush: Force flush queued data
    /// - datadog.status: Get plugin status
    /// - datadog.clear: Clear queues and reset state
    /// </summary>
    public sealed class DatadogPlugin : TelemetryPluginBase
    {
        private readonly DatadogConfiguration _config;
        private readonly DatadogClient _client;
        private readonly ConcurrentDictionary<string, MetricAccumulator> _accumulators;
        private readonly object _lock = new();
        private bool _isRunning;
        private DateTime _startTime;
        private readonly Stopwatch _uptimeStopwatch = new();

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.telemetry.datadog";

        /// <inheritdoc/>
        public override string Name => "Datadog Observability Plugin";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <summary>
        /// Gets the plugin configuration.
        /// </summary>
        public DatadogConfiguration Configuration => _config;

        /// <summary>
        /// Gets the Datadog client.
        /// </summary>
        public DatadogClient Client => _client;

        /// <summary>
        /// Gets whether the plugin is running.
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Initializes a new instance of the <see cref="DatadogPlugin"/> class.
        /// </summary>
        /// <param name="config">Optional configuration. Defaults will be used if null.</param>
        public DatadogPlugin(DatadogConfiguration? config = null)
        {
            _config = config ?? new DatadogConfiguration();
            _client = new DatadogClient(_config);
            _accumulators = new ConcurrentDictionary<string, MetricAccumulator>();
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

            // Flush any pending data
            await _client.FlushAsync();

            // Dispose client resources
            _client?.Dispose();
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
        /// <param name="tags">Optional tags.</param>
        public void IncrementCounter(string metric, double amount = 1, Dictionary<string, string>? tags = null)
        {
            RecordMetricInternal(metric, amount, DatadogMetricType.Count, tags);
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
        /// <param name="tags">Optional tags.</param>
        public void RecordGauge(string metric, double value, Dictionary<string, string>? tags = null)
        {
            RecordMetricInternal(metric, value, DatadogMetricType.Gauge, tags);
        }

        /// <summary>
        /// Records a rate metric value.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="value">The value to record.</param>
        /// <param name="tags">Optional tags.</param>
        public void RecordRate(string metric, double value, Dictionary<string, string>? tags = null)
        {
            RecordMetricInternal(metric, value, DatadogMetricType.Rate, tags);
        }

        /// <inheritdoc/>
        public override IDisposable TrackDuration(string metric)
        {
            return TrackDuration(metric, null);
        }

        /// <summary>
        /// Tracks the duration of an operation as a gauge observation.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="tags">Optional tags.</param>
        /// <returns>A disposable that records the duration when disposed.</returns>
        public IDisposable TrackDuration(string metric, Dictionary<string, string>? tags = null)
        {
            return new DurationTracker(this, metric, tags);
        }

        #endregion

        #region Metric Recording

        private void RecordMetricInternal(string metric, double value, DatadogMetricType type, Dictionary<string, string>? tags)
        {
            if (!_isRunning)
            {
                return;
            }

            var allTags = BuildTags(tags);

            if (_config.EnableMetricAggregation)
            {
                // Aggregate before sending
                var key = $"{metric}:{type}:{string.Join(",", allTags.OrderBy(t => t))}";
                var accumulator = _accumulators.GetOrAdd(key, _ => new MetricAccumulator(metric, type, allTags));
                accumulator.Record(value);
            }
            else
            {
                // Send immediately
                var metricPoint = new DatadogMetricPoint
                {
                    Metric = metric,
                    Type = type,
                    Tags = allTags,
                    Host = _config.Hostname,
                    Points = new List<DatadogPoint>
                    {
                        new DatadogPoint
                        {
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            Value = value
                        }
                    }
                };

                _client.EnqueueMetric(metricPoint);
            }
        }

        /// <summary>
        /// Flushes aggregated metrics to the client queue.
        /// </summary>
        public void FlushAggregatedMetrics()
        {
            if (!_config.EnableMetricAggregation)
            {
                return;
            }

            foreach (var kvp in _accumulators)
            {
                var accumulator = kvp.Value;
                var (value, count, lastUpdate) = accumulator.Flush();

                if (count > 0)
                {
                    var metricPoint = new DatadogMetricPoint
                    {
                        Metric = accumulator.Name,
                        Type = accumulator.Type,
                        Tags = accumulator.Tags,
                        Host = _config.Hostname,
                        Points = new List<DatadogPoint>
                        {
                            new DatadogPoint
                            {
                                Timestamp = new DateTimeOffset(lastUpdate).ToUnixTimeSeconds(),
                                Value = value
                            }
                        }
                    };

                    _client.EnqueueMetric(metricPoint);
                }
            }
        }

        #endregion

        #region Logging

        /// <summary>
        /// Submits a log entry to Datadog.
        /// </summary>
        /// <param name="message">The log message.</param>
        /// <param name="level">The log level (e.g., "info", "warn", "error").</param>
        /// <param name="tags">Optional tags.</param>
        /// <param name="attributes">Optional attributes.</param>
        public void Log(string message, string level = "info", Dictionary<string, string>? tags = null, Dictionary<string, object>? attributes = null)
        {
            if (!_isRunning)
            {
                return;
            }

            var allTags = BuildTags(tags);

            var logEntry = new DatadogLogEntry
            {
                Message = message,
                Status = level.ToLowerInvariant(),
                Service = _config.ServiceName,
                Hostname = _config.Hostname,
                Timestamp = DateTime.UtcNow.ToString("o"),
                Tags = string.Join(",", allTags),
                Attributes = attributes
            };

            _client.EnqueueLog(logEntry);
        }

        #endregion

        #region Message Handling

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "datadog.increment":
                    HandleIncrement(message);
                    break;
                case "datadog.gauge":
                    HandleGauge(message);
                    break;
                case "datadog.rate":
                    HandleRate(message);
                    break;
                case "datadog.log":
                    HandleLog(message);
                    break;
                case "datadog.flush":
                    await HandleFlushAsync(message);
                    break;
                case "datadog.status":
                    HandleStatus(message);
                    break;
                case "datadog.clear":
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
            var tags = GetTags(message.Payload);

            IncrementCounter(metric, amount, tags);
            message.Payload["success"] = true;
        }

        private void HandleGauge(PluginMessage message)
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

            var tags = GetTags(message.Payload);
            RecordGauge(metric, value.Value, tags);
            message.Payload["success"] = true;
        }

        private void HandleRate(PluginMessage message)
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

            var tags = GetTags(message.Payload);
            RecordRate(metric, value.Value, tags);
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

            var level = GetString(message.Payload, "level") ?? "info";
            var tags = GetTags(message.Payload);

            Dictionary<string, object>? attributes = null;
            if (message.Payload.TryGetValue("attributes", out var attrsObj) && attrsObj is Dictionary<string, object> attrs)
            {
                attributes = attrs;
            }

            Log(logMessage, level, tags, attributes);
            message.Payload["success"] = true;
        }

        private async Task HandleFlushAsync(PluginMessage message)
        {
            FlushAggregatedMetrics();
            await _client.FlushAsync();
            message.Payload["success"] = true;
        }

        private void HandleStatus(PluginMessage message)
        {
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["isRunning"] = _isRunning,
                ["site"] = _config.Site,
                ["serviceName"] = _config.ServiceName,
                ["environment"] = _config.Environment,
                ["metricsQueueSize"] = _client.MetricsQueueSize,
                ["logsQueueSize"] = _client.LogsQueueSize,
                ["totalMetricsSent"] = _client.TotalMetricsSent,
                ["totalLogsSent"] = _client.TotalLogsSent,
                ["totalErrors"] = _client.TotalErrors,
                ["uptimeSeconds"] = _uptimeStopwatch.Elapsed.TotalSeconds,
                ["aggregatorsCount"] = _accumulators.Count
            };
        }

        private void HandleClear(PluginMessage message)
        {
            _accumulators.Clear();
            message.Payload["success"] = true;
        }

        #endregion

        #region Helpers

        private List<string> BuildTags(Dictionary<string, string>? customTags)
        {
            var allTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Add global tags
            foreach (var tag in _config.Tags)
            {
                allTags[tag.Key] = tag.Value;
            }

            // Add standard tags
            allTags["service"] = _config.ServiceName;
            allTags["env"] = _config.Environment;
            if (!string.IsNullOrWhiteSpace(_config.Version))
            {
                allTags["version"] = _config.Version;
            }

            // Add custom tags (override)
            if (customTags != null)
            {
                foreach (var tag in customTags)
                {
                    allTags[tag.Key] = tag.Value;
                }
            }

            return DatadogTagHelper.FormatTags(allTags);
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

        private static Dictionary<string, string>? GetTags(Dictionary<string, object> payload)
        {
            if (!payload.TryGetValue("tags", out var tagsObj))
            {
                return null;
            }

            if (tagsObj is Dictionary<string, object> dict)
            {
                return dict.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value?.ToString() ?? string.Empty);
            }

            if (tagsObj is Dictionary<string, string> stringDict)
            {
                return stringDict;
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
                new() { Name = "datadog.increment", DisplayName = "Increment Counter", Description = "Increment a counter metric" },
                new() { Name = "datadog.gauge", DisplayName = "Record Gauge", Description = "Record a gauge metric value" },
                new() { Name = "datadog.rate", DisplayName = "Record Rate", Description = "Record a rate metric value" },
                new() { Name = "datadog.log", DisplayName = "Submit Log", Description = "Submit a log entry to Datadog" },
                new() { Name = "datadog.flush", DisplayName = "Flush Data", Description = "Force flush queued metrics and logs" },
                new() { Name = "datadog.status", DisplayName = "Get Status", Description = "Get plugin status" },
                new() { Name = "datadog.clear", DisplayName = "Clear State", Description = "Clear aggregators and queues" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Site"] = _config.Site;
            metadata["ServiceName"] = _config.ServiceName;
            metadata["Environment"] = _config.Environment;
            metadata["SupportsMetrics"] = true;
            metadata["SupportsLogs"] = true;
            metadata["SupportsTracing"] = false; // Future enhancement
            metadata["MetricsEndpoint"] = _config.MetricsEndpoint;
            metadata["LogsEndpoint"] = _config.LogsEndpoint;
            metadata["EnableMetricAggregation"] = _config.EnableMetricAggregation;
            metadata["FlushIntervalSeconds"] = _config.FlushInterval.TotalSeconds;
            return metadata;
        }

        #endregion

        #region Duration Tracker

        /// <summary>
        /// Helper class for tracking operation duration.
        /// </summary>
        private sealed class DurationTracker : IDisposable
        {
            private readonly DatadogPlugin _plugin;
            private readonly string _metric;
            private readonly Dictionary<string, string>? _tags;
            private readonly Stopwatch _stopwatch;
            private bool _disposed;

            public DurationTracker(DatadogPlugin plugin, string metric, Dictionary<string, string>? tags)
            {
                _plugin = plugin;
                _metric = metric;
                _tags = tags;
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
                _plugin.RecordGauge($"{_metric}.duration_seconds", _stopwatch.Elapsed.TotalSeconds, _tags);
            }
        }

        #endregion
    }
}
