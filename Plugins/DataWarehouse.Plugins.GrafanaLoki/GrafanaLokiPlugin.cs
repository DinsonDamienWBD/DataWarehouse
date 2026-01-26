using System.Collections.Concurrent;
using System.Diagnostics;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.GrafanaLoki
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
            metadata["SupportsLogging"] = true;
            metadata["SupportsTags"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Production-ready Grafana Loki log streaming plugin implementing the Loki Push API.
    /// Provides structured logging with label support, batching, and automatic flushing.
    ///
    /// Features:
    /// - Log streaming to Grafana Loki via Push API
    /// - Multi-dimensional labels for log organization
    /// - Automatic batching with configurable size and interval
    /// - GZIP compression support
    /// - Retry logic with exponential backoff
    /// - Multi-tenancy support via X-Scope-OrgID header
    /// - Basic authentication support
    /// - Health check and statistics tracking
    ///
    /// Message Commands:
    /// - loki.log: Send a log message
    /// - loki.flush: Force flush buffered logs
    /// - loki.stats: Get plugin statistics
    /// - loki.health: Check Loki connectivity
    /// - loki.clear: Clear buffered logs
    /// </summary>
    public sealed class GrafanaLokiPlugin : TelemetryPluginBase
    {
        private readonly LokiConfiguration _config;
        private readonly LokiClient _client;
        private readonly ConcurrentQueue<LogEntry> _logBuffer;
        private readonly Timer _flushTimer;
        private readonly Stopwatch _uptimeStopwatch = new();
        private readonly object _lock = new();
        private bool _isRunning;
        private long _totalLogsSent;
        private long _totalBatchesSent;
        private long _totalFailures;
        private long _totalRetries;
        private DateTime? _lastPushTime;
        private string? _lastError;
        private CancellationTokenSource? _cts;

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.telemetry.loki";

        /// <inheritdoc/>
        public override string Name => "Grafana Loki Plugin";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <summary>
        /// Gets the plugin configuration.
        /// </summary>
        public LokiConfiguration Configuration => _config;

        /// <summary>
        /// Gets whether the plugin is running.
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Gets the number of logs currently buffered.
        /// </summary>
        public int BufferedLogCount => _logBuffer.Count;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrafanaLokiPlugin"/> class.
        /// </summary>
        /// <param name="config">Optional configuration. Defaults will be used if null.</param>
        public GrafanaLokiPlugin(LokiConfiguration? config = null)
        {
            _config = config ?? new LokiConfiguration();
            _client = new LokiClient(_config);
            _logBuffer = new ConcurrentQueue<LogEntry>();

            // Create flush timer (will be started in StartAsync)
            _flushTimer = new Timer(
                async _ => await FlushLogsAsync(),
                null,
                Timeout.Infinite,
                Timeout.Infinite);
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

            // Start flush timer
            _flushTimer.Change(
                TimeSpan.FromMilliseconds(_config.FlushIntervalMs),
                TimeSpan.FromMilliseconds(_config.FlushIntervalMs));

            // Test connection to Loki
            var healthy = await _client.HealthCheckAsync(ct);
            if (!healthy)
            {
                Console.Error.WriteLine(
                    $"[GrafanaLoki] Warning: Unable to connect to Loki at {_config.LokiUrl}. " +
                    "Will continue buffering logs and retry on flush.");
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

            // Stop flush timer
            _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);

            // Flush any remaining logs
            await FlushLogsAsync();

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        #endregion

        #region IMetricsProvider Implementation

        /// <inheritdoc/>
        public override void IncrementCounter(string metric)
        {
            // Log counter increment as a log message
            Log($"Counter incremented: {metric}", new Dictionary<string, string>
            {
                { "metric_type", "counter" },
                { "metric_name", metric }
            });
        }

        /// <inheritdoc/>
        public override void RecordMetric(string metric, double value)
        {
            // Log metric recording as a log message
            Log($"Metric recorded: {metric} = {value}", new Dictionary<string, string>
            {
                { "metric_type", "gauge" },
                { "metric_name", metric },
                { "metric_value", value.ToString("F2") }
            });
        }

        /// <inheritdoc/>
        public override IDisposable TrackDuration(string metric)
        {
            return new DurationTracker(this, metric);
        }

        #endregion

        #region Logging API

        /// <summary>
        /// Logs a message with optional labels.
        /// </summary>
        /// <param name="message">The log message.</param>
        /// <param name="labels">Optional labels to attach to the log.</param>
        public void Log(string message, Dictionary<string, string>? labels = null)
        {
            if (!_isRunning)
            {
                return;
            }

            // Merge default labels with provided labels
            var mergedLabels = new Dictionary<string, string>(_config.DefaultLabels);
            if (labels != null)
            {
                foreach (var label in labels)
                {
                    // Sanitize label name
                    var sanitizedName = LokiLabelValidator.SanitizeLabelName(label.Key);
                    var sanitizedValue = LokiLabelValidator.ValidateLabelValue(label.Value);
                    mergedLabels[sanitizedName] = sanitizedValue;
                }
            }

            var entry = new LogEntry
            {
                Message = message,
                Timestamp = DateTime.UtcNow,
                Labels = mergedLabels
            };

            _logBuffer.Enqueue(entry);

            // Auto-flush if batch size reached
            if (_logBuffer.Count >= _config.BatchSize)
            {
                _ = Task.Run(FlushLogsAsync);
            }
        }

        /// <summary>
        /// Logs a message at a specific level with optional labels.
        /// </summary>
        /// <param name="level">The log level (info, warn, error, debug).</param>
        /// <param name="message">The log message.</param>
        /// <param name="labels">Optional additional labels.</param>
        public void LogWithLevel(string level, string message, Dictionary<string, string>? labels = null)
        {
            var mergedLabels = labels != null
                ? new Dictionary<string, string>(labels)
                : new Dictionary<string, string>();

            mergedLabels["level"] = level;

            Log(message, mergedLabels);
        }

        /// <summary>
        /// Flushes all buffered logs to Loki immediately.
        /// </summary>
        public async Task FlushLogsAsync()
        {
            if (_logBuffer.IsEmpty)
            {
                return;
            }

            // Dequeue all logs
            var batch = new List<LogEntry>();
            while (_logBuffer.TryDequeue(out var entry) && batch.Count < _config.BatchSize * 2)
            {
                batch.Add(entry);
            }

            if (batch.Count == 0)
            {
                return;
            }

            // Group logs by stream (label combination)
            var streamGroups = batch.GroupBy(e => e.GetStreamKey());

            var request = new LokiPushRequest();

            foreach (var group in streamGroups)
            {
                var stream = new LokiStream
                {
                    Stream = group.First().Labels,
                    Values = group.Select(e => new List<string>
                    {
                        // Timestamp in nanoseconds since Unix epoch
                        ((long)(e.Timestamp.Subtract(DateTime.UnixEpoch).TotalSeconds * 1_000_000_000)).ToString(),
                        e.Message
                    }).ToList()
                };

                request.Streams.Add(stream);
            }

            // Push to Loki
            try
            {
                var success = await _client.PushAsync(request, _cts?.Token ?? CancellationToken.None);

                if (success)
                {
                    Interlocked.Add(ref _totalLogsSent, batch.Count);
                    Interlocked.Increment(ref _totalBatchesSent);
                    _lastPushTime = DateTime.UtcNow;
                    _lastError = null;
                }
                else
                {
                    Interlocked.Increment(ref _totalFailures);
                    _lastError = "Push failed after retries";
                    Console.Error.WriteLine($"[GrafanaLoki] Failed to push {batch.Count} logs");
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _totalFailures);
                _lastError = ex.Message;
                Console.Error.WriteLine($"[GrafanaLoki] Error pushing logs: {ex.Message}");

                // Re-enqueue logs on failure (with limit to prevent unbounded growth)
                if (_logBuffer.Count < _config.BatchSize * 10)
                {
                    foreach (var entry in batch)
                    {
                        _logBuffer.Enqueue(entry);
                    }
                }
            }
        }

        /// <summary>
        /// Gets current plugin statistics.
        /// </summary>
        /// <returns>Statistics object.</returns>
        public LokiStatistics GetStatistics()
        {
            var totalLogs = Interlocked.Read(ref _totalLogsSent);
            var totalBatches = Interlocked.Read(ref _totalBatchesSent);

            return new LokiStatistics
            {
                TotalLogsSent = totalLogs,
                TotalBatchesSent = totalBatches,
                TotalFailures = Interlocked.Read(ref _totalFailures),
                TotalRetries = Interlocked.Read(ref _totalRetries),
                BufferedLogs = _logBuffer.Count,
                AverageBatchSize = totalBatches > 0 ? (double)totalLogs / totalBatches : 0,
                LastPushTime = _lastPushTime,
                LastError = _lastError,
                UptimeSeconds = _uptimeStopwatch.Elapsed.TotalSeconds
            };
        }

        #endregion

        #region Message Handling

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "loki.log":
                    HandleLog(message);
                    break;
                case "loki.flush":
                    await HandleFlushAsync(message);
                    break;
                case "loki.stats":
                    HandleStats(message);
                    break;
                case "loki.health":
                    await HandleHealthAsync(message);
                    break;
                case "loki.clear":
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

            var level = GetString(message.Payload, "level") ?? "info";
            var labels = GetLabels(message.Payload);

            LogWithLevel(level, msg, labels);
            message.Payload["success"] = true;
        }

        private async Task HandleFlushAsync(PluginMessage message)
        {
            await FlushLogsAsync();
            message.Payload["success"] = true;
            message.Payload["flushed"] = true;
        }

        private void HandleStats(PluginMessage message)
        {
            var stats = GetStatistics();
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["totalLogsSent"] = stats.TotalLogsSent,
                ["totalBatchesSent"] = stats.TotalBatchesSent,
                ["totalFailures"] = stats.TotalFailures,
                ["totalRetries"] = stats.TotalRetries,
                ["bufferedLogs"] = stats.BufferedLogs,
                ["averageBatchSize"] = stats.AverageBatchSize,
                ["lastPushTime"] = stats.LastPushTime?.ToString("o") ?? "never",
                ["lastError"] = stats.LastError ?? "none",
                ["uptimeSeconds"] = stats.UptimeSeconds
            };
        }

        private async Task HandleHealthAsync(PluginMessage message)
        {
            var healthy = await _client.HealthCheckAsync();
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["healthy"] = healthy,
                ["lokiUrl"] = _config.LokiUrl,
                ["isRunning"] = _isRunning
            };
        }

        private void HandleClear(PluginMessage message)
        {
            while (_logBuffer.TryDequeue(out _)) { }
            message.Payload["success"] = true;
            message.Payload["cleared"] = true;
        }

        #endregion

        #region Helpers

        private static string? GetString(Dictionary<string, object> payload, string key)
        {
            return payload.TryGetValue(key, out var val) && val is string s ? s : null;
        }

        private static Dictionary<string, string>? GetLabels(Dictionary<string, object> payload)
        {
            if (!payload.TryGetValue("labels", out var labelsObj))
            {
                return null;
            }

            if (labelsObj is Dictionary<string, object> dict)
            {
                return dict.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value?.ToString() ?? string.Empty);
            }

            if (labelsObj is Dictionary<string, string> stringDict)
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
                new() { Name = "loki.log", DisplayName = "Log Message", Description = "Send a log message to Loki" },
                new() { Name = "loki.flush", DisplayName = "Flush Logs", Description = "Force flush buffered logs" },
                new() { Name = "loki.stats", DisplayName = "Get Statistics", Description = "Get plugin statistics" },
                new() { Name = "loki.health", DisplayName = "Health Check", Description = "Check Loki connectivity" },
                new() { Name = "loki.clear", DisplayName = "Clear Buffer", Description = "Clear buffered logs" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["LokiUrl"] = _config.LokiUrl;
            metadata["BatchSize"] = _config.BatchSize;
            metadata["FlushIntervalMs"] = _config.FlushIntervalMs;
            metadata["SupportsLabels"] = true;
            metadata["SupportsMultiTenancy"] = !string.IsNullOrEmpty(_config.TenantId);
            metadata["CompressionEnabled"] = _config.EnableCompression;
            metadata["DefaultLabels"] = _config.DefaultLabels;
            return metadata;
        }

        #endregion

        #region Duration Tracker

        /// <summary>
        /// Helper class for tracking operation duration.
        /// </summary>
        private sealed class DurationTracker : IDisposable
        {
            private readonly GrafanaLokiPlugin _plugin;
            private readonly string _metric;
            private readonly Stopwatch _stopwatch;
            private bool _disposed;

            public DurationTracker(GrafanaLokiPlugin plugin, string metric)
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

                _plugin.Log(
                    $"Duration tracked: {_metric} = {_stopwatch.Elapsed.TotalMilliseconds:F2}ms",
                    new Dictionary<string, string>
                    {
                        { "metric_type", "duration" },
                        { "metric_name", _metric },
                        { "duration_ms", _stopwatch.Elapsed.TotalMilliseconds.ToString("F2") }
                    });
            }
        }

        #endregion
    }
}
