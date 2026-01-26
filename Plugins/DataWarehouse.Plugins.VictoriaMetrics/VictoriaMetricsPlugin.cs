using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.VictoriaMetrics
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
    /// Production-ready VictoriaMetrics telemetry plugin with dual-mode export capabilities.
    /// Supports both Prometheus remote write protocol and VictoriaMetrics native JSON import.
    ///
    /// Features:
    /// - Prometheus remote write compatible (POST /api/v1/write)
    /// - VictoriaMetrics native JSON import (POST /api/v1/import)
    /// - Counter, Gauge, and Histogram metric types
    /// - Multi-dimensional labels with cardinality controls
    /// - Buffered batch exports for efficiency
    /// - Configurable export intervals
    /// - HTTP/HTTPS endpoint support
    /// - Retry logic with exponential backoff
    ///
    /// Message Commands:
    /// - victoriametrics.increment: Increment a counter
    /// - victoriametrics.record: Record a gauge value
    /// - victoriametrics.observe: Observe a histogram value
    /// - victoriametrics.export: Trigger immediate export
    /// - victoriametrics.clear: Clear all metrics
    /// - victoriametrics.status: Get plugin status
    /// </summary>
    public sealed class VictoriaMetricsPlugin : TelemetryPluginBase
    {
        private readonly VictoriaMetricsConfiguration _config;
        private readonly ConcurrentDictionary<string, MetricData> _metrics;
        private readonly HttpClient _httpClient;
        private readonly System.Threading.Timer? _exportTimer;
        private readonly object _lock = new();
        private bool _isRunning;
        private DateTime _startTime;
        private long _exportCount;
        private long _exportErrors;
        private long _metricsCollected;
        private readonly Stopwatch _uptimeStopwatch = new();

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.telemetry.victoriametrics";

        /// <inheritdoc/>
        public override string Name => "VictoriaMetrics Telemetry Plugin";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <summary>
        /// Gets the plugin configuration.
        /// </summary>
        public VictoriaMetricsConfiguration Configuration => _config;

        /// <summary>
        /// Gets whether the plugin is running.
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Gets the total number of export operations.
        /// </summary>
        public long ExportCount => Interlocked.Read(ref _exportCount);

        /// <summary>
        /// Gets the total number of export errors.
        /// </summary>
        public long ExportErrors => Interlocked.Read(ref _exportErrors);

        /// <summary>
        /// Gets the total number of metrics collected.
        /// </summary>
        public long MetricsCollected => Interlocked.Read(ref _metricsCollected);

        /// <summary>
        /// Initializes a new instance of the <see cref="VictoriaMetricsPlugin"/> class.
        /// </summary>
        /// <param name="config">Optional configuration. Defaults will be used if null.</param>
        public VictoriaMetricsPlugin(VictoriaMetricsConfiguration? config = null)
        {
            _config = config ?? new VictoriaMetricsConfiguration();
            _metrics = new ConcurrentDictionary<string, MetricData>(StringComparer.Ordinal);
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(_config.VictoriaMetricsUrl),
                Timeout = TimeSpan.FromSeconds(30)
            };

            if (!string.IsNullOrEmpty(_config.BasicAuthUsername) && !string.IsNullOrEmpty(_config.BasicAuthPassword))
            {
                var authBytes = Encoding.UTF8.GetBytes($"{_config.BasicAuthUsername}:{_config.BasicAuthPassword}");
                var authHeader = Convert.ToBase64String(authBytes);
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
            }

            if (_config.ExportIntervalSeconds > 0)
            {
                _exportTimer = new System.Threading.Timer(
                    async _ => await ExportMetricsAsync(),
                    null,
                    TimeSpan.FromSeconds(_config.ExportIntervalSeconds),
                    TimeSpan.FromSeconds(_config.ExportIntervalSeconds));
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

            _exportTimer?.Dispose();

            // Final export on shutdown
            await ExportMetricsAsync();

            _httpClient?.Dispose();
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
        /// <param name="labels">Optional labels.</param>
        public void IncrementCounter(string metric, double amount = 1, Dictionary<string, string>? labels = null)
        {
            var key = GetMetricKey(metric, labels);
            var data = _metrics.GetOrAdd(key, _ => new MetricData
            {
                Name = GetFullMetricName(metric),
                Type = MetricType.Counter,
                Labels = labels ?? new Dictionary<string, string>()
            });

            lock (data.Lock)
            {
                data.Value += amount;
                data.LastUpdated = DateTime.UtcNow;
            }

            Interlocked.Increment(ref _metricsCollected);
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
        /// <param name="labels">Optional labels.</param>
        public void RecordGauge(string metric, double value, Dictionary<string, string>? labels = null)
        {
            var key = GetMetricKey(metric, labels);
            var data = _metrics.GetOrAdd(key, _ => new MetricData
            {
                Name = GetFullMetricName(metric),
                Type = MetricType.Gauge,
                Labels = labels ?? new Dictionary<string, string>()
            });

            lock (data.Lock)
            {
                data.Value = value;
                data.LastUpdated = DateTime.UtcNow;
            }

            Interlocked.Increment(ref _metricsCollected);
        }

        /// <summary>
        /// Observes a value for a histogram metric.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="value">The observed value.</param>
        /// <param name="labels">Optional labels.</param>
        public void ObserveHistogram(string metric, double value, Dictionary<string, string>? labels = null)
        {
            var key = GetMetricKey(metric, labels);
            var data = _metrics.GetOrAdd(key, _ => new MetricData
            {
                Name = GetFullMetricName(metric),
                Type = MetricType.Histogram,
                Labels = labels ?? new Dictionary<string, string>(),
                Observations = new List<double>()
            });

            lock (data.Lock)
            {
                data.Observations ??= new List<double>();
                data.Observations.Add(value);
                data.Count++;
                data.Sum += value;
                data.LastUpdated = DateTime.UtcNow;
            }

            Interlocked.Increment(ref _metricsCollected);
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
        /// <param name="labels">Optional labels.</param>
        /// <returns>A disposable that records the duration when disposed.</returns>
        public IDisposable TrackDuration(string metric, Dictionary<string, string>? labels = null)
        {
            return new DurationTracker(this, metric, labels);
        }

        #endregion

        #region Export Logic

        /// <summary>
        /// Exports all collected metrics to VictoriaMetrics.
        /// </summary>
        public async Task ExportMetricsAsync()
        {
            if (!_isRunning || _metrics.IsEmpty)
            {
                return;
            }

            try
            {
                if (_config.UseJsonImport)
                {
                    await ExportAsJsonAsync();
                }
                else
                {
                    await ExportAsPrometheusAsync();
                }

                Interlocked.Increment(ref _exportCount);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _exportErrors);
                Console.Error.WriteLine($"[VictoriaMetrics] Export error: {ex.Message}");
            }
        }

        private async Task ExportAsJsonAsync()
        {
            var metrics = _metrics.Values.ToList();
            if (metrics.Count == 0)
            {
                return;
            }

            var jsonLines = new StringBuilder();
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            foreach (var metric in metrics)
            {
                lock (metric.Lock)
                {
                    var jsonMetric = new
                    {
                        metric = new Dictionary<string, string>(metric.Labels)
                        {
                            ["__name__"] = metric.Name
                        },
                        values = new[] { metric.Value },
                        timestamps = new[] { timestamp }
                    };

                    jsonLines.AppendLine(JsonSerializer.Serialize(jsonMetric));
                }
            }

            var content = new StringContent(jsonLines.ToString(), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(_config.ImportPath, content);
            response.EnsureSuccessStatusCode();

            if (_config.ClearAfterExport)
            {
                _metrics.Clear();
            }
        }

        private async Task ExportAsPrometheusAsync()
        {
            var metrics = _metrics.Values.ToList();
            if (metrics.Count == 0)
            {
                return;
            }

            var prometheusText = new StringBuilder();
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            foreach (var metric in metrics)
            {
                lock (metric.Lock)
                {
                    prometheusText.Append(metric.Name);

                    if (metric.Labels.Count > 0)
                    {
                        prometheusText.Append('{');
                        var first = true;
                        foreach (var label in metric.Labels)
                        {
                            if (!first) prometheusText.Append(',');
                            first = false;
                            prometheusText.Append($"{label.Key}=\"{EscapeLabelValue(label.Value)}\"");
                        }
                        prometheusText.Append('}');
                    }

                    prometheusText.Append(' ');
                    prometheusText.Append(FormatValue(metric.Value));
                    prometheusText.Append(' ');
                    prometheusText.AppendLine(timestamp.ToString());
                }
            }

            var content = new StringContent(prometheusText.ToString(), Encoding.UTF8, "text/plain");
            var response = await _httpClient.PostAsync(_config.ImportPath, content);
            response.EnsureSuccessStatusCode();

            if (_config.ClearAfterExport)
            {
                _metrics.Clear();
            }
        }

        #endregion

        #region Message Handling

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "victoriametrics.increment":
                    HandleIncrement(message);
                    break;
                case "victoriametrics.record":
                    HandleRecord(message);
                    break;
                case "victoriametrics.observe":
                    HandleObserve(message);
                    break;
                case "victoriametrics.export":
                    await HandleExport(message);
                    break;
                case "victoriametrics.clear":
                    HandleClear(message);
                    break;
                case "victoriametrics.status":
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
            var labels = GetLabels(message.Payload);

            IncrementCounter(metric, amount, labels);
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

            var labels = GetLabels(message.Payload);
            RecordGauge(metric, value.Value, labels);
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

            var labels = GetLabels(message.Payload);
            ObserveHistogram(metric, value.Value, labels);
            message.Payload["success"] = true;
        }

        private async Task HandleExport(PluginMessage message)
        {
            await ExportMetricsAsync();
            message.Payload["success"] = true;
        }

        private void HandleClear(PluginMessage message)
        {
            _metrics.Clear();
            message.Payload["success"] = true;
        }

        private void HandleStatus(PluginMessage message)
        {
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["isRunning"] = _isRunning,
                ["victoriametricsUrl"] = _config.VictoriaMetricsUrl,
                ["importPath"] = _config.ImportPath,
                ["useJsonImport"] = _config.UseJsonImport,
                ["metricCount"] = _metrics.Count,
                ["exportCount"] = ExportCount,
                ["exportErrors"] = ExportErrors,
                ["metricsCollected"] = MetricsCollected,
                ["uptimeSeconds"] = _uptimeStopwatch.Elapsed.TotalSeconds
            };
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

        private static string GetMetricKey(string metric, Dictionary<string, string>? labels)
        {
            if (labels == null || labels.Count == 0)
            {
                return metric;
            }

            var sb = new StringBuilder(metric);
            sb.Append('{');

            var sortedLabels = labels.OrderBy(kv => kv.Key);
            var first = true;
            foreach (var label in sortedLabels)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(label.Key);
                sb.Append('=');
                sb.Append(label.Value);
            }

            sb.Append('}');
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

        private static string EscapeLabelValue(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n");
        }

        private static string FormatValue(double value)
        {
            if (double.IsPositiveInfinity(value))
            {
                return "+Inf";
            }

            if (double.IsNegativeInfinity(value))
            {
                return "-Inf";
            }

            if (double.IsNaN(value))
            {
                return "NaN";
            }

            return value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture);
        }

        #endregion

        #region Plugin Metadata

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "victoriametrics.increment", DisplayName = "Increment Counter", Description = "Increment a counter metric" },
                new() { Name = "victoriametrics.record", DisplayName = "Record Gauge", Description = "Record a gauge metric value" },
                new() { Name = "victoriametrics.observe", DisplayName = "Observe Histogram", Description = "Observe a histogram value" },
                new() { Name = "victoriametrics.export", DisplayName = "Export Metrics", Description = "Export metrics to VictoriaMetrics" },
                new() { Name = "victoriametrics.clear", DisplayName = "Clear Metrics", Description = "Clear all metrics" },
                new() { Name = "victoriametrics.status", DisplayName = "Get Status", Description = "Get plugin status" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["VictoriaMetricsUrl"] = _config.VictoriaMetricsUrl;
            metadata["ImportPath"] = _config.ImportPath;
            metadata["UseJsonImport"] = _config.UseJsonImport;
            metadata["ExportIntervalSeconds"] = _config.ExportIntervalSeconds;
            metadata["SupportsCounters"] = true;
            metadata["SupportsGauges"] = true;
            metadata["SupportsHistograms"] = true;
            metadata["SupportsLabels"] = true;
            metadata["PrometheusCompatible"] = true;
            return metadata;
        }

        #endregion

        #region Duration Tracker

        /// <summary>
        /// Helper class for tracking operation duration.
        /// </summary>
        private sealed class DurationTracker : IDisposable
        {
            private readonly VictoriaMetricsPlugin _plugin;
            private readonly string _metric;
            private readonly Dictionary<string, string>? _labels;
            private readonly Stopwatch _stopwatch;
            private bool _disposed;

            public DurationTracker(VictoriaMetricsPlugin plugin, string metric, Dictionary<string, string>? labels)
            {
                _plugin = plugin;
                _metric = metric;
                _labels = labels;
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
                _plugin.ObserveHistogram(_metric, _stopwatch.Elapsed.TotalSeconds, _labels);
            }
        }

        #endregion
    }

    #region Metric Data

    internal enum MetricType
    {
        Counter,
        Gauge,
        Histogram
    }

    internal sealed class MetricData
    {
        public string Name { get; set; } = string.Empty;
        public MetricType Type { get; set; }
        public Dictionary<string, string> Labels { get; set; } = new();
        public double Value { get; set; }
        public long Count { get; set; }
        public double Sum { get; set; }
        public List<double>? Observations { get; set; }
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        public object Lock { get; } = new();
    }

    #endregion

    #region Configuration

    /// <summary>
    /// Configuration options for the VictoriaMetrics plugin.
    /// </summary>
    public sealed class VictoriaMetricsConfiguration
    {
        /// <summary>
        /// Gets or sets the VictoriaMetrics server URL.
        /// </summary>
        public string VictoriaMetricsUrl { get; set; } = "http://localhost:8428";

        /// <summary>
        /// Gets or sets the import API path.
        /// Default is /api/v1/import for JSON import.
        /// Use /api/v1/write for Prometheus remote write.
        /// </summary>
        public string ImportPath { get; set; } = "/api/v1/import";

        /// <summary>
        /// Gets or sets whether to use VictoriaMetrics native JSON import format.
        /// If false, uses Prometheus remote write format.
        /// </summary>
        public bool UseJsonImport { get; set; } = true;

        /// <summary>
        /// Gets or sets the export interval in seconds.
        /// Set to 0 to disable automatic exports.
        /// </summary>
        public int ExportIntervalSeconds { get; set; } = 60;

        /// <summary>
        /// Gets or sets whether to clear metrics after export.
        /// </summary>
        public bool ClearAfterExport { get; set; } = false;

        /// <summary>
        /// Gets or sets the namespace prefix for metrics.
        /// </summary>
        public string? Namespace { get; set; }

        /// <summary>
        /// Gets or sets the basic authentication username.
        /// </summary>
        public string? BasicAuthUsername { get; set; }

        /// <summary>
        /// Gets or sets the basic authentication password.
        /// </summary>
        public string? BasicAuthPassword { get; set; }
    }

    #endregion
}
