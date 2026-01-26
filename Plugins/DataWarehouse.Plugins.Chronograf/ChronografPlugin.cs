using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.Chronograf
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
    /// Production-ready Chronograf/InfluxDB v2 telemetry plugin implementing Line Protocol.
    /// Provides counter, gauge, and histogram metrics with tag support.
    ///
    /// Features:
    /// - InfluxDB v2 API write support
    /// - Line Protocol formatting
    /// - Automatic batching and flushing
    /// - Tag and field support
    /// - Retry logic with exponential backoff
    /// - GZIP compression support
    /// - Default process and runtime metrics
    ///
    /// Message Commands:
    /// - chronograf.increment: Increment a counter
    /// - chronograf.record: Record a gauge value
    /// - chronograf.observe: Observe a histogram value
    /// - chronograf.flush: Force flush pending metrics
    /// - chronograf.status: Get plugin status
    /// </summary>
    public sealed class ChronografPlugin : TelemetryPluginBase
    {
        private readonly ChronografConfiguration _config;
        private readonly HttpClient _httpClient;
        private readonly LineProtocolBatch _batch;
        private readonly InfluxDbStatistics _stats;
        private readonly ConcurrentDictionary<string, double> _counters;
        private readonly ConcurrentDictionary<string, double> _gauges;
        private readonly ConcurrentDictionary<string, List<double>> _histograms;
        private CancellationTokenSource? _cts;
        private Task? _flushTask;
        private readonly object _lock = new();
        private bool _isRunning;
        private DateTime _startTime;
        private readonly Stopwatch _uptimeStopwatch = new();

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.telemetry.chronograf";

        /// <inheritdoc/>
        public override string Name => "Chronograf/InfluxDB Metrics Plugin";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <summary>
        /// Gets the plugin configuration.
        /// </summary>
        public ChronografConfiguration Configuration => _config;

        /// <summary>
        /// Gets whether the plugin is running.
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Gets the plugin statistics.
        /// </summary>
        public InfluxDbStatistics Statistics => _stats;

        /// <summary>
        /// Initializes a new instance of the <see cref="ChronografPlugin"/> class.
        /// </summary>
        /// <param name="config">Optional configuration. Defaults will be used if null.</param>
        public ChronografPlugin(ChronografConfiguration? config = null)
        {
            _config = config ?? new ChronografConfiguration();
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds)
            };
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Token", _config.Token);
            _batch = new LineProtocolBatch(_config.BatchSize);
            _stats = new InfluxDbStatistics();
            _counters = new ConcurrentDictionary<string, double>(StringComparer.Ordinal);
            _gauges = new ConcurrentDictionary<string, double>(StringComparer.Ordinal);
            _histograms = new ConcurrentDictionary<string, List<double>>(StringComparer.Ordinal);
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

            // Start background flush task
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

            // Flush any pending metrics
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
                    // Timeout waiting for flush task to stop
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
        /// <param name="tags">Optional tags.</param>
        public void IncrementCounter(string metric, double amount = 1, Dictionary<string, string>? tags = null)
        {
            var key = GetMetricKey(metric, tags);
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
        /// <param name="tags">Optional tags.</param>
        public void RecordGauge(string metric, double value, Dictionary<string, string>? tags = null)
        {
            var key = GetMetricKey(metric, tags);
            _gauges[key] = value;
        }

        /// <summary>
        /// Observes a value for a histogram metric.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="value">The observed value.</param>
        /// <param name="tags">Optional tags.</param>
        public void ObserveHistogram(string metric, double value, Dictionary<string, string>? tags = null)
        {
            var key = GetMetricKey(metric, tags);
            _histograms.AddOrUpdate(
                key,
                _ => new List<double> { value },
                (_, list) =>
                {
                    lock (list)
                    {
                        list.Add(value);
                    }
                    return list;
                });
        }

        /// <inheritdoc/>
        public override IDisposable TrackDuration(string metric)
        {
            return TrackDuration(metric, null);
        }

        /// <summary>
        /// Tracks the duration of an operation as a histogram observation.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="tags">Optional tags.</param>
        /// <returns>A disposable that records the duration when disposed.</returns>
        public IDisposable TrackDuration(string metric, Dictionary<string, string>? tags = null)
        {
            return new DurationTracker(this, metric, tags);
        }

        #endregion

        #region Message Handling

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "chronograf.increment":
                    HandleIncrement(message);
                    break;
                case "chronograf.record":
                    HandleRecord(message);
                    break;
                case "chronograf.observe":
                    HandleObserve(message);
                    break;
                case "chronograf.flush":
                    await HandleFlushAsync(message);
                    break;
                case "chronograf.status":
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
            var tags = GetTags(message.Payload);

            IncrementCounter(metric, amount, tags);
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

            var tags = GetTags(message.Payload);
            RecordGauge(metric, value.Value, tags);
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

            var tags = GetTags(message.Payload);
            ObserveHistogram(metric, value.Value, tags);
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
                ["influxDbUrl"] = _config.InfluxDbUrl,
                ["org"] = _config.Org,
                ["bucket"] = _config.Bucket,
                ["batchSize"] = _config.BatchSize,
                ["pointsWritten"] = _stats.PointsWritten,
                ["batchesFlushed"] = _stats.BatchesFlushed,
                ["writeErrors"] = _stats.WriteErrors,
                ["retries"] = _stats.Retries,
                ["uptimeSeconds"] = _uptimeStopwatch.Elapsed.TotalSeconds,
                ["pendingPoints"] = _batch.Count
            };
        }

        #endregion

        #region Flush Logic

        private async Task FlushLoopAsync(CancellationToken ct)
        {
            var interval = TimeSpan.FromSeconds(_config.FlushIntervalSeconds);

            while (!ct.IsCancellationRequested && _isRunning)
            {
                try
                {
                    await Task.Delay(interval, ct);
                    await FlushAsync();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Chronograf] Error in flush loop: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Flushes all pending metrics to InfluxDB.
        /// </summary>
        public async Task FlushAsync()
        {
            try
            {
                // Collect all metrics as Line Protocol points
                var points = CollectMetrics();

                if (points.Count == 0)
                {
                    return;
                }

                // Convert to Line Protocol format
                var lineProtocol = string.Join("\n", points.Select(p => p.ToLineProtocol()));

                // Write to InfluxDB with retry logic
                await WriteToInfluxDbAsync(lineProtocol);

                _stats.IncrementPointsWritten(points.Count);
                _stats.IncrementBatchesFlushed();
            }
            catch (Exception ex)
            {
                _stats.IncrementWriteErrors();
                Console.Error.WriteLine($"[Chronograf] Error flushing metrics: {ex.Message}");
            }
        }

        private List<LineProtocolPoint> CollectMetrics()
        {
            var points = new List<LineProtocolPoint>();
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;

            // Collect counters
            foreach (var counter in _counters)
            {
                var (metric, tags) = ParseMetricKey(counter.Key);
                var point = new LineProtocolPointBuilder()
                    .Measurement(GetFullMetricName(metric))
                    .Tags(MergeGlobalTags(tags))
                    .Field("count", counter.Value)
                    .Timestamp(timestamp)
                    .Build();
                points.Add(point);
            }

            // Collect gauges
            foreach (var gauge in _gauges)
            {
                var (metric, tags) = ParseMetricKey(gauge.Key);
                var point = new LineProtocolPointBuilder()
                    .Measurement(GetFullMetricName(metric))
                    .Tags(MergeGlobalTags(tags))
                    .Field("value", gauge.Value)
                    .Timestamp(timestamp)
                    .Build();
                points.Add(point);
            }

            // Collect histograms with statistics
            foreach (var histogram in _histograms)
            {
                var (metric, tags) = ParseMetricKey(histogram.Key);
                List<double> values;

                lock (histogram.Value)
                {
                    values = new List<double>(histogram.Value);
                    histogram.Value.Clear();
                }

                if (values.Count > 0)
                {
                    values.Sort();
                    var count = values.Count;
                    var sum = values.Sum();
                    var min = values[0];
                    var max = values[^1];
                    var p50 = values[count / 2];
                    var p95 = values[(int)(count * 0.95)];
                    var p99 = values[(int)(count * 0.99)];

                    var point = new LineProtocolPointBuilder()
                        .Measurement(GetFullMetricName(metric))
                        .Tags(MergeGlobalTags(tags))
                        .Field("count", count)
                        .Field("sum", sum)
                        .Field("min", min)
                        .Field("max", max)
                        .Field("p50", p50)
                        .Field("p95", p95)
                        .Field("p99", p99)
                        .Timestamp(timestamp)
                        .Build();
                    points.Add(point);
                }
            }

            // Add process metrics if enabled
            if (_config.IncludeProcessMetrics)
            {
                points.AddRange(CollectProcessMetrics(timestamp));
            }

            // Add runtime metrics if enabled
            if (_config.IncludeRuntimeMetrics)
            {
                points.AddRange(CollectRuntimeMetrics(timestamp));
            }

            return points;
        }

        private List<LineProtocolPoint> CollectProcessMetrics(long timestamp)
        {
            var points = new List<LineProtocolPoint>();
            var process = Process.GetCurrentProcess();
            var globalTags = MergeGlobalTags(null);

            points.Add(new LineProtocolPointBuilder()
                .Measurement("process_cpu_seconds_total")
                .Tags(globalTags)
                .Field("value", process.TotalProcessorTime.TotalSeconds)
                .Timestamp(timestamp)
                .Build());

            points.Add(new LineProtocolPointBuilder()
                .Measurement("process_resident_memory_bytes")
                .Tags(globalTags)
                .Field("value", process.WorkingSet64)
                .Timestamp(timestamp)
                .Build());

            points.Add(new LineProtocolPointBuilder()
                .Measurement("process_virtual_memory_bytes")
                .Tags(globalTags)
                .Field("value", process.VirtualMemorySize64)
                .Timestamp(timestamp)
                .Build());

            points.Add(new LineProtocolPointBuilder()
                .Measurement("process_open_handles")
                .Tags(globalTags)
                .Field("value", process.HandleCount)
                .Timestamp(timestamp)
                .Build());

            return points;
        }

        private List<LineProtocolPoint> CollectRuntimeMetrics(long timestamp)
        {
            var points = new List<LineProtocolPoint>();
            var globalTags = MergeGlobalTags(null);

            // GC metrics
            for (var gen = 0; gen <= GC.MaxGeneration; gen++)
            {
                var tags = new Dictionary<string, string>(globalTags)
                {
                    ["generation"] = gen.ToString()
                };

                points.Add(new LineProtocolPointBuilder()
                    .Measurement("dotnet_gc_collection_count_total")
                    .Tags(tags)
                    .Field("count", GC.CollectionCount(gen))
                    .Timestamp(timestamp)
                    .Build());
            }

            // ThreadPool metrics
            ThreadPool.GetAvailableThreads(out var workerThreads, out _);
            ThreadPool.GetMaxThreads(out var maxWorkerThreads, out _);

            points.Add(new LineProtocolPointBuilder()
                .Measurement("dotnet_threadpool_threads_count")
                .Tags(globalTags)
                .Field("value", maxWorkerThreads - workerThreads)
                .Timestamp(timestamp)
                .Build());

            points.Add(new LineProtocolPointBuilder()
                .Measurement("dotnet_threadpool_queue_length")
                .Tags(globalTags)
                .Field("value", ThreadPool.PendingWorkItemCount)
                .Timestamp(timestamp)
                .Build());

            return points;
        }

        private async Task WriteToInfluxDbAsync(string lineProtocol)
        {
            var url = $"{_config.InfluxDbUrl}/api/v2/write?org={Uri.EscapeDataString(_config.Org)}&bucket={Uri.EscapeDataString(_config.Bucket)}&precision=ns";

            for (var attempt = 0; attempt <= _config.MaxRetries; attempt++)
            {
                try
                {
                    HttpContent content;

                    if (_config.EnableGzipCompression)
                    {
                        var bytes = Encoding.UTF8.GetBytes(lineProtocol);
                        using var memoryStream = new MemoryStream();
                        using (var gzipStream = new GZipStream(memoryStream, CompressionMode.Compress))
                        {
                            await gzipStream.WriteAsync(bytes);
                        }
                        content = new ByteArrayContent(memoryStream.ToArray());
                        content.Headers.ContentEncoding.Add("gzip");
                    }
                    else
                    {
                        content = new StringContent(lineProtocol, Encoding.UTF8);
                    }

                    content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

                    var response = await _httpClient.PostAsync(url, content);
                    response.EnsureSuccessStatusCode();
                    return;
                }
                catch (HttpRequestException ex)
                {
                    if (attempt < _config.MaxRetries)
                    {
                        _stats.IncrementRetries();
                        var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                        await Task.Delay(delay);
                    }
                    else
                    {
                        throw new InvalidOperationException($"Failed to write to InfluxDB after {_config.MaxRetries} retries: {ex.Message}", ex);
                    }
                }
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

        private Dictionary<string, string> MergeGlobalTags(Dictionary<string, string>? tags)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);

            if (_config.GlobalTags != null)
            {
                foreach (var tag in _config.GlobalTags)
                {
                    result[tag.Key] = tag.Value;
                }
            }

            if (tags != null)
            {
                foreach (var tag in tags)
                {
                    result[tag.Key] = tag.Value;
                }
            }

            return result;
        }

        private static string GetMetricKey(string metric, Dictionary<string, string>? tags)
        {
            if (tags == null || tags.Count == 0)
            {
                return metric;
            }

            var sb = new StringBuilder(metric);
            foreach (var tag in tags.OrderBy(t => t.Key))
            {
                sb.Append('|');
                sb.Append(tag.Key);
                sb.Append('=');
                sb.Append(tag.Value);
            }
            return sb.ToString();
        }

        private static (string metric, Dictionary<string, string> tags) ParseMetricKey(string key)
        {
            var parts = key.Split('|');
            var metric = parts[0];
            var tags = new Dictionary<string, string>(StringComparer.Ordinal);

            for (var i = 1; i < parts.Length; i++)
            {
                var tagParts = parts[i].Split('=', 2);
                if (tagParts.Length == 2)
                {
                    tags[tagParts[0]] = tagParts[1];
                }
            }

            return (metric, tags);
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
                new() { Name = "chronograf.increment", DisplayName = "Increment Counter", Description = "Increment a counter metric" },
                new() { Name = "chronograf.record", DisplayName = "Record Gauge", Description = "Record a gauge metric value" },
                new() { Name = "chronograf.observe", DisplayName = "Observe Histogram", Description = "Observe a histogram value" },
                new() { Name = "chronograf.flush", DisplayName = "Flush Metrics", Description = "Force flush pending metrics" },
                new() { Name = "chronograf.status", DisplayName = "Get Status", Description = "Get plugin status" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["InfluxDbUrl"] = _config.InfluxDbUrl;
            metadata["Org"] = _config.Org;
            metadata["Bucket"] = _config.Bucket;
            metadata["BatchSize"] = _config.BatchSize;
            metadata["FlushIntervalSeconds"] = _config.FlushIntervalSeconds;
            metadata["SupportsCounters"] = true;
            metadata["SupportsGauges"] = true;
            metadata["SupportsHistograms"] = true;
            metadata["SupportsTags"] = true;
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
            private readonly ChronografPlugin _plugin;
            private readonly string _metric;
            private readonly Dictionary<string, string>? _tags;
            private readonly Stopwatch _stopwatch;
            private bool _disposed;

            public DurationTracker(ChronografPlugin plugin, string metric, Dictionary<string, string>? tags)
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
                _plugin.ObserveHistogram(_metric, _stopwatch.Elapsed.TotalSeconds, _tags);
            }
        }

        #endregion

        #region IDisposable

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _httpClient?.Dispose();
            }
            base.Dispose(disposing);
        }

        #endregion
    }
}
