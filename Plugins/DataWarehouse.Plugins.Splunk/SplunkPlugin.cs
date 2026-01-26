using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.Splunk
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
            metadata["SupportsEvents"] = true;
            metadata["SupportsTracing"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Production-ready Splunk HTTP Event Collector (HEC) plugin for submitting events and metrics.
    ///
    /// Features:
    /// - HTTP Event Collector (HEC) integration
    /// - Event and metric submission
    /// - Automatic batching and flushing
    /// - Retry logic with exponential backoff
    /// - Statistics tracking
    /// - Configurable indexing and source fields
    /// - Custom field support
    /// - SSL certificate validation
    ///
    /// Message Commands:
    /// - splunk.send_event: Send a single event
    /// - splunk.send_metric: Send a single metric
    /// - splunk.send_batch: Send multiple events/metrics
    /// - splunk.flush: Flush pending batches
    /// - splunk.stats: Get submission statistics
    /// - splunk.status: Get plugin status
    /// - splunk.clear: Clear statistics
    /// </summary>
    public sealed class SplunkPlugin : TelemetryPluginBase
    {
        private readonly SplunkConfiguration _config;
        private readonly HttpClient _httpClient;
        private readonly ConcurrentQueue<SplunkEvent> _eventQueue;
        private readonly ConcurrentQueue<SplunkMetric> _metricQueue;
        private readonly SubmissionStatistics _stats;
        private readonly Timer? _flushTimer;
        private readonly SemaphoreSlim _flushLock;
        private readonly object _lock = new();
        private bool _isRunning;
        private CancellationTokenSource? _cts;
        private readonly string _defaultHost;

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.telemetry.splunk";

        /// <inheritdoc/>
        public override string Name => "Splunk HEC Plugin";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <summary>
        /// Gets the plugin configuration.
        /// </summary>
        public SplunkConfiguration Configuration => _config;

        /// <summary>
        /// Gets whether the plugin is running.
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Gets the submission statistics.
        /// </summary>
        public SubmissionStatistics Statistics => _stats;

        /// <summary>
        /// Gets the number of events in the queue.
        /// </summary>
        public int QueuedEventCount => _eventQueue.Count;

        /// <summary>
        /// Gets the number of metrics in the queue.
        /// </summary>
        public int QueuedMetricCount => _metricQueue.Count;

        /// <summary>
        /// Initializes a new instance of the <see cref="SplunkPlugin"/> class.
        /// </summary>
        /// <param name="config">Optional configuration. Defaults will be used if null.</param>
        public SplunkPlugin(SplunkConfiguration? config = null)
        {
            _config = config ?? new SplunkConfiguration();
            _eventQueue = new ConcurrentQueue<SplunkEvent>();
            _metricQueue = new ConcurrentQueue<SplunkMetric>();
            _stats = new SubmissionStatistics();
            _flushLock = new SemaphoreSlim(1, 1);
            _defaultHost = Environment.MachineName;

            // Configure HttpClient
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = _config.ValidateCertificates
                    ? null
                    : (_, _, _, _) => true
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds)
            };

            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Splunk", _config.HecToken);
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            // Start periodic flush timer if configured
            if (_config.FlushIntervalMs > 0)
            {
                _flushTimer = new Timer(
                    async _ => await FlushAsync().ConfigureAwait(false),
                    null,
                    TimeSpan.FromMilliseconds(_config.FlushIntervalMs),
                    TimeSpan.FromMilliseconds(_config.FlushIntervalMs));
            }
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
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Validate configuration
            if (string.IsNullOrEmpty(_config.HecToken))
            {
                throw new InvalidOperationException("Splunk HEC token is required.");
            }

            if (string.IsNullOrEmpty(_config.HecUrl))
            {
                throw new InvalidOperationException("Splunk HEC URL is required.");
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
            }

            // Flush any pending events
            await FlushAsync().ConfigureAwait(false);

            _cts?.Cancel();
            _flushTimer?.Dispose();
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
            if (!_isRunning)
            {
                return;
            }

            var splunkMetric = CreateMetric(metric, value);
            _metricQueue.Enqueue(splunkMetric);

            // Auto-flush if batch size reached
            if (_metricQueue.Count >= _config.MaxBatchSize)
            {
                _ = Task.Run(async () => await FlushAsync().ConfigureAwait(false));
            }
        }

        /// <inheritdoc/>
        public override IDisposable TrackDuration(string metric)
        {
            return new DurationTracker(this, metric);
        }

        #endregion

        #region Event Submission

        /// <summary>
        /// Sends an event to Splunk HEC.
        /// </summary>
        /// <param name="eventData">The event data (string or structured object).</param>
        /// <param name="fields">Optional custom fields.</param>
        /// <param name="timestamp">Optional timestamp (defaults to current time).</param>
        public void SendEvent(object eventData, Dictionary<string, object>? fields = null, DateTime? timestamp = null)
        {
            if (!_isRunning)
            {
                return;
            }

            var splunkEvent = CreateEvent(eventData, fields, timestamp);
            _eventQueue.Enqueue(splunkEvent);

            // Auto-flush if batch size reached
            if (_eventQueue.Count >= _config.MaxBatchSize)
            {
                _ = Task.Run(async () => await FlushAsync().ConfigureAwait(false));
            }
        }

        /// <summary>
        /// Sends a metric to Splunk HEC.
        /// </summary>
        /// <param name="metricName">The metric name.</param>
        /// <param name="value">The metric value.</param>
        /// <param name="dimensions">Optional metric dimensions.</param>
        /// <param name="timestamp">Optional timestamp (defaults to current time).</param>
        public void SendMetric(string metricName, double value, Dictionary<string, object>? dimensions = null, DateTime? timestamp = null)
        {
            if (!_isRunning)
            {
                return;
            }

            var splunkMetric = CreateMetric(metricName, value, dimensions, timestamp);
            _metricQueue.Enqueue(splunkMetric);

            // Auto-flush if batch size reached
            if (_metricQueue.Count >= _config.MaxBatchSize)
            {
                _ = Task.Run(async () => await FlushAsync().ConfigureAwait(false));
            }
        }

        /// <summary>
        /// Flushes all pending events and metrics to Splunk.
        /// </summary>
        public async Task FlushAsync()
        {
            if (!_isRunning || (_eventQueue.IsEmpty && _metricQueue.IsEmpty))
            {
                return;
            }

            await _flushLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Flush events
                if (!_eventQueue.IsEmpty)
                {
                    var batch = new List<SplunkEvent>();
                    while (batch.Count < _config.MaxBatchSize && _eventQueue.TryDequeue(out var evt))
                    {
                        batch.Add(evt);
                    }

                    if (batch.Count > 0)
                    {
                        await SubmitEventsAsync(batch).ConfigureAwait(false);
                    }
                }

                // Flush metrics
                if (!_metricQueue.IsEmpty)
                {
                    var batch = new List<SplunkMetric>();
                    while (batch.Count < _config.MaxBatchSize && _metricQueue.TryDequeue(out var metric))
                    {
                        batch.Add(metric);
                    }

                    if (batch.Count > 0)
                    {
                        await SubmitMetricsAsync(batch).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _flushLock.Release();
            }
        }

        #endregion

        #region HTTP Submission

        private async Task SubmitEventsAsync(List<SplunkEvent> events)
        {
            if (events.Count == 0)
            {
                return;
            }

            var payload = BuildPayload(events);
            var success = await SubmitToHecAsync(payload, "/services/collector/event").ConfigureAwait(false);

            if (success)
            {
                _stats.AddEvents(events.Count);
                _stats.IncrementSuccessful();
                _stats.LastSubmission = DateTime.UtcNow;
            }
            else
            {
                _stats.IncrementFailed();
            }
        }

        private async Task SubmitMetricsAsync(List<SplunkMetric> metrics)
        {
            if (metrics.Count == 0)
            {
                return;
            }

            var payload = BuildPayload(metrics);
            var success = await SubmitToHecAsync(payload, "/services/collector").ConfigureAwait(false);

            if (success)
            {
                _stats.AddMetrics(metrics.Count);
                _stats.IncrementSuccessful();
                _stats.LastSubmission = DateTime.UtcNow;
            }
            else
            {
                _stats.IncrementFailed();
            }
        }

        private async Task<bool> SubmitToHecAsync(string payload, string endpoint)
        {
            var url = _config.HecUrl.TrimEnd('/') + endpoint;
            var retries = 0;

            while (retries <= _config.MaxRetries)
            {
                try
                {
                    var content = new StringContent(payload, Encoding.UTF8, "application/json");
                    var response = await _httpClient.PostAsync(url, content, _cts?.Token ?? CancellationToken.None)
                        .ConfigureAwait(false);

                    _stats.AddBytesSent(payload.Length);

                    if (response.IsSuccessStatusCode)
                    {
                        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var splunkResponse = JsonSerializer.Deserialize<SplunkResponse>(responseBody);

                        if (splunkResponse?.IsSuccess == true)
                        {
                            return true;
                        }

                        _stats.LastError = splunkResponse?.Text ?? "Unknown error";
                        _stats.LastErrorTime = DateTime.UtcNow;
                    }
                    else
                    {
                        _stats.LastError = $"HTTP {response.StatusCode}: {response.ReasonPhrase}";
                        _stats.LastErrorTime = DateTime.UtcNow;

                        // Don't retry client errors (4xx)
                        if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                        {
                            return false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _stats.LastError = ex.Message;
                    _stats.LastErrorTime = DateTime.UtcNow;
                }

                // Exponential backoff
                if (retries < _config.MaxRetries)
                {
                    retries++;
                    _stats.IncrementRetries();
                    var delay = TimeSpan.FromMilliseconds(Math.Pow(2, retries) * 100);
                    await Task.Delay(delay, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    break;
                }
            }

            return false;
        }

        #endregion

        #region Message Handling

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "splunk.send_event":
                    HandleSendEvent(message);
                    break;
                case "splunk.send_metric":
                    HandleSendMetric(message);
                    break;
                case "splunk.send_batch":
                    HandleSendBatch(message);
                    break;
                case "splunk.flush":
                    await HandleFlushAsync(message).ConfigureAwait(false);
                    break;
                case "splunk.stats":
                    HandleStats(message);
                    break;
                case "splunk.status":
                    HandleStatus(message);
                    break;
                case "splunk.clear":
                    HandleClear(message);
                    break;
                default:
                    await base.OnMessageAsync(message).ConfigureAwait(false);
                    break;
            }
        }

        private void HandleSendEvent(PluginMessage message)
        {
            var eventData = message.Payload.TryGetValue("event", out var evt) ? evt : null;
            if (eventData == null)
            {
                message.Payload["error"] = "event data required";
                return;
            }

            var fields = message.Payload.TryGetValue("fields", out var f) && f is Dictionary<string, object> fDict
                ? fDict
                : null;

            var timestamp = message.Payload.TryGetValue("timestamp", out var ts) && ts is DateTime dt
                ? dt
                : (DateTime?)null;

            SendEvent(eventData, fields, timestamp);
            message.Payload["success"] = true;
        }

        private void HandleSendMetric(PluginMessage message)
        {
            var metricName = GetString(message.Payload, "metric");
            if (string.IsNullOrEmpty(metricName))
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

            var dimensions = message.Payload.TryGetValue("dimensions", out var d) && d is Dictionary<string, object> dDict
                ? dDict
                : null;

            var timestamp = message.Payload.TryGetValue("timestamp", out var ts) && ts is DateTime dt
                ? dt
                : (DateTime?)null;

            SendMetric(metricName, value.Value, dimensions, timestamp);
            message.Payload["success"] = true;
        }

        private void HandleSendBatch(PluginMessage message)
        {
            var events = message.Payload.TryGetValue("events", out var e) && e is List<object> eList
                ? eList
                : null;

            var metrics = message.Payload.TryGetValue("metrics", out var m) && m is List<object> mList
                ? mList
                : null;

            if (events != null)
            {
                foreach (var evt in events)
                {
                    SendEvent(evt);
                }
            }

            if (metrics != null)
            {
                foreach (var metric in metrics)
                {
                    if (metric is Dictionary<string, object> metricDict)
                    {
                        var name = GetString(metricDict, "metric");
                        var value = GetDouble(metricDict, "value");
                        if (!string.IsNullOrEmpty(name) && value.HasValue)
                        {
                            SendMetric(name, value.Value);
                        }
                    }
                }
            }

            message.Payload["success"] = true;
        }

        private async Task HandleFlushAsync(PluginMessage message)
        {
            await FlushAsync().ConfigureAwait(false);
            message.Payload["success"] = true;
        }

        private void HandleStats(PluginMessage message)
        {
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["totalEvents"] = _stats.TotalEvents,
                ["totalMetrics"] = _stats.TotalMetrics,
                ["successfulSubmissions"] = _stats.SuccessfulSubmissions,
                ["failedSubmissions"] = _stats.FailedSubmissions,
                ["totalRetries"] = _stats.TotalRetries,
                ["bytesSent"] = _stats.BytesSent,
                ["lastSubmission"] = _stats.LastSubmission?.ToString("o") ?? string.Empty,
                ["lastError"] = _stats.LastError ?? string.Empty,
                ["lastErrorTime"] = _stats.LastErrorTime?.ToString("o") ?? string.Empty,
                ["queuedEvents"] = _eventQueue.Count,
                ["queuedMetrics"] = _metricQueue.Count
            };
        }

        private void HandleStatus(PluginMessage message)
        {
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["isRunning"] = _isRunning,
                ["hecUrl"] = _config.HecUrl,
                ["index"] = _config.Index ?? "default",
                ["source"] = _config.Source,
                ["sourceType"] = _config.SourceType,
                ["maxBatchSize"] = _config.MaxBatchSize,
                ["flushIntervalMs"] = _config.FlushIntervalMs,
                ["queuedEvents"] = _eventQueue.Count,
                ["queuedMetrics"] = _metricQueue.Count
            };
        }

        private void HandleClear(PluginMessage message)
        {
            _stats.Clear();
            message.Payload["success"] = true;
        }

        #endregion

        #region Helper Methods

        private SplunkEvent CreateEvent(object eventData, Dictionary<string, object>? fields = null, DateTime? timestamp = null)
        {
            var evt = new SplunkEvent
            {
                Event = eventData,
                Fields = MergeFields(fields)
            };

            if (_config.IncludeDefaultFields)
            {
                evt.Time = (timestamp ?? DateTime.UtcNow).ToUnixTimeSeconds();
                evt.Host = _config.Host ?? _defaultHost;
                evt.Source = _config.Source;
                evt.SourceType = _config.SourceType;
                evt.Index = _config.Index;
            }

            return evt;
        }

        private SplunkMetric CreateMetric(string metricName, double value, Dictionary<string, object>? dimensions = null, DateTime? timestamp = null)
        {
            var fields = new Dictionary<string, object>
            {
                ["metric_name"] = metricName,
                [metricName] = value
            };

            if (dimensions != null)
            {
                foreach (var dim in dimensions)
                {
                    fields[dim.Key] = dim.Value;
                }
            }

            var merged = MergeFields(fields);

            var metric = new SplunkMetric
            {
                Event = "metric",
                Fields = merged
            };

            if (_config.IncludeDefaultFields)
            {
                metric.Time = (timestamp ?? DateTime.UtcNow).ToUnixTimeSeconds();
                metric.Host = _config.Host ?? _defaultHost;
                metric.Source = _config.Source;
                metric.SourceType = _config.SourceType;
                metric.Index = _config.Index;
            }

            return metric;
        }

        private Dictionary<string, object>? MergeFields(Dictionary<string, object>? fields)
        {
            if (_config.CustomFields == null && fields == null)
            {
                return null;
            }

            var merged = new Dictionary<string, object>();

            if (_config.CustomFields != null)
            {
                foreach (var kv in _config.CustomFields)
                {
                    merged[kv.Key] = kv.Value;
                }
            }

            if (fields != null)
            {
                foreach (var kv in fields)
                {
                    merged[kv.Key] = kv.Value;
                }
            }

            return merged.Count > 0 ? merged : null;
        }

        private static string BuildPayload<T>(List<T> items)
        {
            var sb = new StringBuilder();
            foreach (var item in items)
            {
                sb.AppendLine(JsonSerializer.Serialize(item, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                }));
            }
            return sb.ToString();
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
                new() { Name = "splunk.send_event", DisplayName = "Send Event", Description = "Send an event to Splunk HEC" },
                new() { Name = "splunk.send_metric", DisplayName = "Send Metric", Description = "Send a metric to Splunk HEC" },
                new() { Name = "splunk.send_batch", DisplayName = "Send Batch", Description = "Send multiple events/metrics" },
                new() { Name = "splunk.flush", DisplayName = "Flush", Description = "Flush pending batches" },
                new() { Name = "splunk.stats", DisplayName = "Get Statistics", Description = "Get submission statistics" },
                new() { Name = "splunk.status", DisplayName = "Get Status", Description = "Get plugin status" },
                new() { Name = "splunk.clear", DisplayName = "Clear Statistics", Description = "Clear submission statistics" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["HecUrl"] = _config.HecUrl;
            metadata["Index"] = _config.Index ?? "default";
            metadata["Source"] = _config.Source;
            metadata["SourceType"] = _config.SourceType;
            metadata["MaxBatchSize"] = _config.MaxBatchSize;
            metadata["FlushIntervalMs"] = _config.FlushIntervalMs;
            metadata["SupportsEvents"] = true;
            metadata["SupportsMetrics"] = true;
            metadata["SupportsBatching"] = true;
            return metadata;
        }

        #endregion

        #region Duration Tracker

        /// <summary>
        /// Helper class for tracking operation duration.
        /// </summary>
        private sealed class DurationTracker : IDisposable
        {
            private readonly SplunkPlugin _plugin;
            private readonly string _metric;
            private readonly System.Diagnostics.Stopwatch _stopwatch;
            private bool _disposed;

            public DurationTracker(SplunkPlugin plugin, string metric)
            {
                _plugin = plugin;
                _metric = metric;
                _stopwatch = System.Diagnostics.Stopwatch.StartNew();
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
    /// Extension methods for DateTime to Unix timestamp conversion.
    /// </summary>
    internal static class DateTimeExtensions
    {
        /// <summary>
        /// Converts a DateTime to Unix timestamp in seconds.
        /// </summary>
        public static double ToUnixTimeSeconds(this DateTime dateTime)
        {
            return new DateTimeOffset(dateTime.ToUniversalTime()).ToUnixTimeSeconds();
        }
    }
}
