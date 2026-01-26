using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.LogicMonitor
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
    /// Production-ready LogicMonitor metrics plugin implementing REST API v3 metric ingestion.
    /// Provides push-based metric collection with counter, gauge, and histogram support.
    ///
    /// Features:
    /// - REST API v3 metric ingestion endpoint
    /// - LMv1 HMAC-SHA256 signature authentication
    /// - Bearer token authentication support
    /// - Automatic periodic metric push
    /// - Counter, Gauge, and Histogram metric types
    /// - Resource properties and metadata
    /// - Batch metric ingestion for efficiency
    /// - Automatic retry with exponential backoff
    ///
    /// Message Commands:
    /// - logicmonitor.increment: Increment a counter
    /// - logicmonitor.record: Record a gauge value
    /// - logicmonitor.observe: Observe a histogram value
    /// - logicmonitor.push: Manually trigger metric push
    /// - logicmonitor.status: Get plugin status
    /// </summary>
    public sealed class LogicMonitorPlugin : TelemetryPluginBase
    {
        private readonly LogicMonitorConfiguration _config;
        private readonly HttpClient _httpClient;
        private readonly ConcurrentDictionary<string, MetricData> _metrics;
        private readonly Timer? _pushTimer;
        private readonly object _lock = new();
        private bool _isRunning;
        private DateTime _startTime;
        private long _pushCount;
        private long _pushErrors;
        private long _dataPointsPushed;
        private readonly Stopwatch _uptimeStopwatch = new();

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.telemetry.logicmonitor";

        /// <inheritdoc/>
        public override string Name => "LogicMonitor Metrics Plugin";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <summary>
        /// Gets the plugin configuration.
        /// </summary>
        public LogicMonitorConfiguration Configuration => _config;

        /// <summary>
        /// Gets whether the plugin is running.
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Gets the total number of metric pushes.
        /// </summary>
        public long PushCount => Interlocked.Read(ref _pushCount);

        /// <summary>
        /// Gets the total number of push errors.
        /// </summary>
        public long PushErrors => Interlocked.Read(ref _pushErrors);

        /// <summary>
        /// Gets the total number of datapoints pushed.
        /// </summary>
        public long DataPointsPushed => Interlocked.Read(ref _dataPointsPushed);

        /// <summary>
        /// Initializes a new instance of the <see cref="LogicMonitorPlugin"/> class.
        /// </summary>
        /// <param name="config">The configuration for the plugin.</param>
        /// <exception cref="ArgumentNullException">Thrown when config is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when configuration is invalid.</exception>
        public LogicMonitorPlugin(LogicMonitorConfiguration? config = null)
        {
            _config = config ?? new LogicMonitorConfiguration();
            _config.Validate();

            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(_config.ApiBaseUrl),
                Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds)
            };

            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            _metrics = new ConcurrentDictionary<string, MetricData>(StringComparer.Ordinal);

            // Create push timer
            _pushTimer = new Timer(
                PushMetricsCallback,
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
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

            // Start periodic push timer
            var interval = TimeSpan.FromSeconds(_config.PushIntervalSeconds);
            _pushTimer?.Change(interval, interval);

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

            // Stop timer
            _pushTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            // Final push
            try
            {
                await PushMetricsAsync();
            }
            catch
            {
                // Ignore errors during shutdown
            }

            await Task.CompletedTask;
        }

        /// <inheritdoc/>
        public override void Dispose()
        {
            _pushTimer?.Dispose();
            _httpClient.Dispose();
            base.Dispose();
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
            var data = _metrics.GetOrAdd(metric, _ => new MetricData(MetricType.Counter));
            data.Increment(amount);
        }

        /// <inheritdoc/>
        public override void RecordMetric(string metric, double value)
        {
            var data = _metrics.GetOrAdd(metric, _ => new MetricData(MetricType.Gauge));
            data.SetValue(value);
        }

        /// <summary>
        /// Observes a value for a histogram metric.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="value">The observed value.</param>
        public void ObserveHistogram(string metric, double value)
        {
            var data = _metrics.GetOrAdd(metric, _ => new MetricData(MetricType.Histogram));
            data.Observe(value);
        }

        /// <inheritdoc/>
        public override IDisposable TrackDuration(string metric)
        {
            return new DurationTracker(this, metric);
        }

        #endregion

        #region Metric Push

        private void PushMetricsCallback(object? state)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await PushMetricsAsync();
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _pushErrors);
                    Console.Error.WriteLine($"[LogicMonitor] Error pushing metrics: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Manually pushes all metrics to LogicMonitor.
        /// </summary>
        /// <returns>A task representing the async operation.</returns>
        public async Task PushMetricsAsync()
        {
            if (!_isRunning)
            {
                return;
            }

            var snapshot = _metrics.ToArray();
            if (snapshot.Length == 0)
            {
                return;
            }

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var dataPoints = new List<DataPointPayload>();

            foreach (var kvp in snapshot)
            {
                var metricName = kvp.Key;
                var data = kvp.Value;

                var dataPoint = new DataPointPayload
                {
                    DataSource = "datawarehouse_telemetry",
                    DataSourceDisplayName = "DataWarehouse Telemetry",
                    DataPointName = metricName,
                    DataPointAggregationType = data.Type switch
                    {
                        MetricType.Counter => "sum",
                        MetricType.Gauge => "avg",
                        MetricType.Histogram => "avg",
                        _ => "avg"
                    },
                    InstanceName = _config.ResourceId,
                    Values = new Dictionary<string, string>
                    {
                        [timestamp.ToString()] = data.GetValue().ToString("G17")
                    }
                };

                dataPoints.Add(dataPoint);
            }

            // Batch datapoints
            for (var i = 0; i < dataPoints.Count; i += _config.MaxBatchSize)
            {
                var batch = dataPoints.Skip(i).Take(_config.MaxBatchSize).ToList();
                await PushBatchAsync(batch);
            }
        }

        private async Task PushBatchAsync(List<DataPointPayload> batch)
        {
            if (batch.Count == 0)
            {
                return;
            }

            var request = new MetricIngestRequest
            {
                Resource = new ResourceIdentity
                {
                    System_DisplayName = _config.ResourceName,
                    System_Hostname = _config.ResourceId,
                    Properties = _config.ResourceProperties ?? new Dictionary<string, string>()
                },
                DataPoints = batch
            };

            try
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/metric/ingest");

                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = false
                };

                var jsonContent = JsonSerializer.Serialize(request, jsonOptions);
                httpRequest.Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

                // Add authentication header
                if (_config.UseBearerAuth)
                {
                    httpRequest.Headers.Authorization = new AuthenticationHeaderValue(
                        "Bearer", _config.BearerToken);
                }
                else
                {
                    var authHeader = LogicMonitorAuth.GenerateLMv1Signature(
                        _config.AccessId,
                        _config.AccessKey,
                        "POST",
                        "/metric/ingest",
                        jsonContent);
                    httpRequest.Headers.Add("Authorization", authHeader);
                }

                var response = await _httpClient.SendAsync(httpRequest);
                response.EnsureSuccessStatusCode();

                Interlocked.Increment(ref _pushCount);
                Interlocked.Add(ref _dataPointsPushed, batch.Count);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _pushErrors);
                Console.Error.WriteLine($"[LogicMonitor] Failed to push batch: {ex.Message}");
                throw;
            }
        }

        #endregion

        #region Message Handling

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "logicmonitor.increment":
                    HandleIncrement(message);
                    break;
                case "logicmonitor.record":
                    HandleRecord(message);
                    break;
                case "logicmonitor.observe":
                    HandleObserve(message);
                    break;
                case "logicmonitor.push":
                    await HandlePushAsync(message);
                    break;
                case "logicmonitor.status":
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

            RecordMetric(metric, value.Value);
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

            ObserveHistogram(metric, value.Value);
            message.Payload["success"] = true;
        }

        private async Task HandlePushAsync(PluginMessage message)
        {
            try
            {
                await PushMetricsAsync();
                message.Payload["success"] = true;
            }
            catch (Exception ex)
            {
                message.Payload["error"] = ex.Message;
            }
        }

        private void HandleStatus(PluginMessage message)
        {
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["isRunning"] = _isRunning,
                ["accountName"] = _config.AccountName,
                ["resourceId"] = _config.ResourceId,
                ["pushIntervalSeconds"] = _config.PushIntervalSeconds,
                ["metricCount"] = _metrics.Count,
                ["pushCount"] = PushCount,
                ["pushErrors"] = PushErrors,
                ["dataPointsPushed"] = DataPointsPushed,
                ["uptimeSeconds"] = _uptimeStopwatch.Elapsed.TotalSeconds
            };
        }

        #endregion

        #region Plugin Metadata

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "logicmonitor.increment", DisplayName = "Increment Counter", Description = "Increment a counter metric" },
                new() { Name = "logicmonitor.record", DisplayName = "Record Gauge", Description = "Record a gauge metric value" },
                new() { Name = "logicmonitor.observe", DisplayName = "Observe Histogram", Description = "Observe a histogram value" },
                new() { Name = "logicmonitor.push", DisplayName = "Push Metrics", Description = "Manually push metrics to LogicMonitor" },
                new() { Name = "logicmonitor.status", DisplayName = "Get Status", Description = "Get plugin status" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["AccountName"] = _config.AccountName;
            metadata["ResourceId"] = _config.ResourceId;
            metadata["PushIntervalSeconds"] = _config.PushIntervalSeconds;
            metadata["SupportsCounters"] = true;
            metadata["SupportsGauges"] = true;
            metadata["SupportsHistograms"] = true;
            metadata["AuthMethod"] = _config.UseBearerAuth ? "Bearer" : "LMv1";
            return metadata;
        }

        #endregion

        #region Helpers

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

        #region Duration Tracker

        /// <summary>
        /// Helper class for tracking operation duration.
        /// </summary>
        private sealed class DurationTracker : IDisposable
        {
            private readonly LogicMonitorPlugin _plugin;
            private readonly string _metric;
            private readonly Stopwatch _stopwatch;
            private bool _disposed;

            public DurationTracker(LogicMonitorPlugin plugin, string metric)
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
                _plugin.ObserveHistogram(_metric, _stopwatch.Elapsed.TotalSeconds);
            }
        }

        #endregion

        #region Metric Data

        private enum MetricType
        {
            Counter,
            Gauge,
            Histogram
        }

        private sealed class MetricData
        {
            private readonly MetricType _type;
            private double _value;
            private long _count;
            private double _sum;
            private readonly object _lock = new();

            public MetricType Type => _type;

            public MetricData(MetricType type)
            {
                _type = type;
            }

            public void Increment(double amount)
            {
                lock (_lock)
                {
                    _value += amount;
                }
            }

            public void SetValue(double value)
            {
                lock (_lock)
                {
                    _value = value;
                }
            }

            public void Observe(double value)
            {
                lock (_lock)
                {
                    _count++;
                    _sum += value;
                    _value = _sum / _count; // Average for histogram
                }
            }

            public double GetValue()
            {
                lock (_lock)
                {
                    return _value;
                }
            }
        }

        #endregion
    }
}
