using System.Collections.Concurrent;
using System.Diagnostics;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.Kibana
{
    /// <summary>
    /// Abstract base class for telemetry plugins providing metrics, tracing, and logging capabilities.
    /// Extends <see cref="FeaturePluginBase"/> to support lifecycle management and integration.
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
            metadata["SupportsTraces"] = false;
            return metadata;
        }
    }

    /// <summary>
    /// Production-ready Kibana/Elasticsearch dashboard plugin implementing bulk API integration.
    /// Provides real-time telemetry streaming with metrics and logs to Elasticsearch for Kibana visualization.
    ///
    /// Features:
    /// - Elasticsearch Bulk API for efficient batch operations
    /// - Automatic index pattern creation (datawarehouse-{date})
    /// - Buffered document streaming with configurable flush intervals
    /// - Metric and log document types with rich metadata
    /// - Compression support for network efficiency
    /// - Connection pooling and retry logic
    /// - Health checking and error recovery
    /// - Configurable authentication (API key or basic auth)
    ///
    /// Message Commands:
    /// - kibana.metric: Send a metric to Elasticsearch
    /// - kibana.log: Send a log entry to Elasticsearch
    /// - kibana.flush: Manually flush buffered documents
    /// - kibana.stats: Get plugin statistics
    /// - kibana.health: Check Elasticsearch health
    /// - kibana.clear: Clear buffer and reset statistics
    /// </summary>
    public sealed class KibanaPlugin : TelemetryPluginBase
    {
        private readonly KibanaConfiguration _config;
        private readonly ElasticsearchClient _client;
        private readonly ConcurrentQueue<BulkOperation> _documentBuffer;
        private readonly Timer _flushTimer;
        private readonly object _lock = new();
        private CancellationTokenSource? _cts;
        private bool _isRunning;
        private DateTime _startTime;

        // Statistics
        private long _documentsSent;
        private long _documentsFailed;
        private long _bulkRequests;
        private long _bulkErrors;
        private DateTime? _lastFlush;
        private string? _lastError;
        private DateTime? _lastErrorTime;

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.telemetry.kibana";

        /// <inheritdoc/>
        public override string Name => "Kibana/Elasticsearch Dashboard Plugin";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <summary>
        /// Gets whether the plugin is running.
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Gets the plugin configuration.
        /// </summary>
        public KibanaConfiguration Configuration => _config;

        /// <summary>
        /// Gets the current statistics.
        /// </summary>
        public KibanaStatistics Statistics => new()
        {
            DocumentsSent = Interlocked.Read(ref _documentsSent),
            DocumentsFailed = Interlocked.Read(ref _documentsFailed),
            BulkRequests = Interlocked.Read(ref _bulkRequests),
            BulkErrors = Interlocked.Read(ref _bulkErrors),
            BufferedDocuments = _documentBuffer.Count,
            LastFlush = _lastFlush,
            LastError = _lastError,
            LastErrorTime = _lastErrorTime
        };

        /// <summary>
        /// Initializes a new instance of the <see cref="KibanaPlugin"/> class.
        /// </summary>
        /// <param name="config">Optional configuration. Defaults will be used if null.</param>
        public KibanaPlugin(KibanaConfiguration? config = null)
        {
            _config = config ?? new KibanaConfiguration();
            _client = new ElasticsearchClient(_config);
            _documentBuffer = new ConcurrentQueue<BulkOperation>();
            _flushTimer = new Timer(FlushCallback, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
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
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Health check
            var isHealthy = await _client.HealthCheckAsync(ct);
            if (!isHealthy)
            {
                Console.Error.WriteLine(
                    $"[Kibana] WARNING: Elasticsearch cluster at {_config.ElasticsearchUrl} is not reachable. " +
                    "Plugin will continue but telemetry may be lost.");
            }

            // Create index templates if enabled
            if (_config.AutoCreateIndexTemplates)
            {
                try
                {
                    await _client.CreateIndexTemplateAsync(
                        $"{_config.IndexPrefix}-template",
                        $"{_config.IndexPrefix}-*",
                        ct);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Kibana] Failed to create index template: {ex.Message}");
                }
            }

            // Start flush timer
            _flushTimer.Change(_config.FlushInterval, _config.FlushInterval);
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
            }

            // Stop timer
            _flushTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            // Final flush
            await FlushAsync(CancellationToken.None);

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        #endregion

        #region IMetricsProvider Implementation

        /// <inheritdoc/>
        public override void IncrementCounter(string metric)
        {
            RecordMetric(metric, 1);
        }

        /// <inheritdoc/>
        public override void RecordMetric(string metric, double value)
        {
            if (!_config.SendMetrics || !_isRunning)
            {
                return;
            }

            var doc = new MetricDocument
            {
                Timestamp = DateTime.UtcNow,
                MetricName = metric,
                MetricType = "gauge",
                Value = value
            };

            EnqueueDocument(doc, "metrics");
        }

        /// <inheritdoc/>
        public override IDisposable TrackDuration(string metric)
        {
            return new DurationTracker(this, metric);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Sends a log entry to Elasticsearch.
        /// </summary>
        /// <param name="level">The log level.</param>
        /// <param name="message">The log message.</param>
        /// <param name="logger">The logger name.</param>
        /// <param name="fields">Additional fields.</param>
        /// <param name="exception">Exception information.</param>
        public void Log(
            string level,
            string message,
            string logger = "",
            Dictionary<string, object>? fields = null,
            ExceptionInfo? exception = null)
        {
            if (!_config.SendLogs || !_isRunning)
            {
                return;
            }

            var doc = new LogDocument
            {
                Timestamp = DateTime.UtcNow,
                Level = level,
                Message = message,
                Logger = logger,
                Fields = fields ?? new Dictionary<string, object>(),
                Exception = exception
            };

            EnqueueDocument(doc, "logs");
        }

        /// <summary>
        /// Manually flushes buffered documents to Elasticsearch.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        public async Task FlushAsync(CancellationToken ct = default)
        {
            var operations = new List<BulkOperation>();

            // Drain the queue
            while (_documentBuffer.TryDequeue(out var op))
            {
                operations.Add(op);

                if (operations.Count >= _config.BulkBatchSize)
                {
                    await SendBulkAsync(operations, ct);
                    operations.Clear();
                }
            }

            // Send remaining
            if (operations.Count > 0)
            {
                await SendBulkAsync(operations, ct);
            }

            _lastFlush = DateTime.UtcNow;
        }

        /// <summary>
        /// Checks the health of the Elasticsearch cluster.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if healthy; otherwise, false.</returns>
        public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
        {
            return await _client.HealthCheckAsync(ct);
        }

        /// <summary>
        /// Clears the buffer and resets statistics.
        /// </summary>
        public void Clear()
        {
            while (_documentBuffer.TryDequeue(out _))
            {
                // Drain queue
            }

            Interlocked.Exchange(ref _documentsSent, 0);
            Interlocked.Exchange(ref _documentsFailed, 0);
            Interlocked.Exchange(ref _bulkRequests, 0);
            Interlocked.Exchange(ref _bulkErrors, 0);
            _lastFlush = null;
            _lastError = null;
            _lastErrorTime = null;
        }

        #endregion

        #region Message Handling

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "kibana.metric":
                    HandleMetric(message);
                    break;
                case "kibana.log":
                    HandleLog(message);
                    break;
                case "kibana.flush":
                    await HandleFlushAsync(message);
                    break;
                case "kibana.stats":
                    HandleStats(message);
                    break;
                case "kibana.health":
                    await HandleHealthAsync(message);
                    break;
                case "kibana.clear":
                    HandleClear(message);
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }

        private void HandleMetric(PluginMessage message)
        {
            var metric = GetString(message.Payload, "metric");
            if (string.IsNullOrEmpty(metric))
            {
                message.Payload["error"] = "metric name required";
                return;
            }

            var value = GetDouble(message.Payload, "value") ?? 1.0;
            RecordMetric(metric, value);
            message.Payload["success"] = true;
        }

        private void HandleLog(PluginMessage message)
        {
            var level = GetString(message.Payload, "level") ?? "info";
            var messageText = GetString(message.Payload, "message");
            if (string.IsNullOrEmpty(messageText))
            {
                message.Payload["error"] = "message required";
                return;
            }

            var logger = GetString(message.Payload, "logger") ?? string.Empty;
            Log(level, messageText, logger);
            message.Payload["success"] = true;
        }

        private async Task HandleFlushAsync(PluginMessage message)
        {
            try
            {
                await FlushAsync(_cts?.Token ?? CancellationToken.None);
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
            }
        }

        private void HandleStats(PluginMessage message)
        {
            var stats = Statistics;
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["isRunning"] = _isRunning,
                ["documentsSent"] = stats.DocumentsSent,
                ["documentsFailed"] = stats.DocumentsFailed,
                ["bulkRequests"] = stats.BulkRequests,
                ["bulkErrors"] = stats.BulkErrors,
                ["bufferedDocuments"] = stats.BufferedDocuments,
                ["lastFlush"] = stats.LastFlush?.ToString("O") ?? "never",
                ["lastError"] = stats.LastError ?? "none",
                ["lastErrorTime"] = stats.LastErrorTime?.ToString("O") ?? "never"
            };
        }

        private async Task HandleHealthAsync(PluginMessage message)
        {
            try
            {
                var isHealthy = await HealthCheckAsync(_cts?.Token ?? CancellationToken.None);
                message.Payload["result"] = new Dictionary<string, object>
                {
                    ["healthy"] = isHealthy,
                    ["url"] = _config.ElasticsearchUrl
                };
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
            }
        }

        private void HandleClear(PluginMessage message)
        {
            Clear();
            message.Payload["success"] = true;
        }

        #endregion

        #region Private Methods

        private void EnqueueDocument(object document, string type)
        {
            var indexName = ElasticsearchClient.GetIndexName($"{_config.IndexPrefix}-{type}");
            var operation = new BulkOperation
            {
                Action = BulkActionType.Index,
                Index = indexName,
                Document = document
            };

            _documentBuffer.Enqueue(operation);

            // Auto-flush if buffer is full
            if (_documentBuffer.Count >= _config.BulkBatchSize)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await FlushAsync(_cts?.Token ?? CancellationToken.None);
                    }
                    catch
                    {
                        // Ignore flush errors
                    }
                });
            }
        }

        private async Task SendBulkAsync(List<BulkOperation> operations, CancellationToken ct)
        {
            if (operations.Count == 0)
            {
                return;
            }

            try
            {
                Interlocked.Increment(ref _bulkRequests);

                var response = await _client.BulkAsync(operations, ct);

                if (response.Errors)
                {
                    Interlocked.Increment(ref _bulkErrors);

                    // Count failures
                    var failureCount = 0;
                    if (response.Items != null)
                    {
                        foreach (var item in response.Items)
                        {
                            var details = item.Index ?? item.Create ?? item.Update ?? item.Delete;
                            if (details?.Error != null)
                            {
                                failureCount++;
                                _lastError = $"{details.Error.Type}: {details.Error.Reason}";
                                _lastErrorTime = DateTime.UtcNow;
                            }
                        }
                    }

                    Interlocked.Add(ref _documentsFailed, failureCount);
                    Interlocked.Add(ref _documentsSent, operations.Count - failureCount);
                }
                else
                {
                    Interlocked.Add(ref _documentsSent, operations.Count);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _bulkErrors);
                Interlocked.Add(ref _documentsFailed, operations.Count);
                _lastError = ex.Message;
                _lastErrorTime = DateTime.UtcNow;

                Console.Error.WriteLine($"[Kibana] Bulk request failed: {ex.Message}");
            }
        }

        private void FlushCallback(object? state)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await FlushAsync(_cts?.Token ?? CancellationToken.None);
                }
                catch
                {
                    // Ignore timer flush errors
                }
            });
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
                new() { Name = "kibana.metric", DisplayName = "Send Metric", Description = "Send a metric to Elasticsearch" },
                new() { Name = "kibana.log", DisplayName = "Send Log", Description = "Send a log entry to Elasticsearch" },
                new() { Name = "kibana.flush", DisplayName = "Flush Buffer", Description = "Manually flush buffered documents" },
                new() { Name = "kibana.stats", DisplayName = "Get Statistics", Description = "Get plugin statistics" },
                new() { Name = "kibana.health", DisplayName = "Health Check", Description = "Check Elasticsearch health" },
                new() { Name = "kibana.clear", DisplayName = "Clear Buffer", Description = "Clear buffer and reset statistics" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["ElasticsearchUrl"] = _config.ElasticsearchUrl;
            metadata["IndexPrefix"] = _config.IndexPrefix;
            metadata["BulkBatchSize"] = _config.BulkBatchSize;
            metadata["FlushInterval"] = _config.FlushInterval.TotalSeconds;
            metadata["SendMetrics"] = _config.SendMetrics;
            metadata["SendLogs"] = _config.SendLogs;
            metadata["SendTraces"] = _config.SendTraces;
            metadata["CompressionEnabled"] = _config.EnableCompression;
            return metadata;
        }

        #endregion


        #region Duration Tracker

        /// <summary>
        /// Helper class for tracking operation duration.
        /// </summary>
        private sealed class DurationTracker : IDisposable
        {
            private readonly KibanaPlugin _plugin;
            private readonly string _metric;
            private readonly Stopwatch _stopwatch;
            private bool _disposed;

            public DurationTracker(KibanaPlugin plugin, string metric)
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
}
