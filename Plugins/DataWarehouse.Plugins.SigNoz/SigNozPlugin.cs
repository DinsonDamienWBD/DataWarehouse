using System.Collections.Concurrent;
using System.Diagnostics;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.SigNoz
{
    /// <summary>
    /// Production-ready SigNoz observability plugin implementing OTLP (OpenTelemetry Protocol).
    /// Provides comprehensive observability with metrics, logs, and distributed tracing.
    ///
    /// Features:
    /// - OTLP metrics export (counters, gauges, histograms)
    /// - OTLP logs export with severity levels
    /// - OTLP distributed traces with spans
    /// - Batching and buffering for performance
    /// - Automatic resource detection (service, host, process)
    /// - Configurable export intervals
    /// - Custom headers for authentication
    ///
    /// Message Commands:
    /// - signoz.metric: Record a metric (counter/gauge/histogram)
    /// - signoz.log: Send a log message
    /// - signoz.trace.start: Start a new trace span
    /// - signoz.trace.end: End a trace span
    /// - signoz.flush: Force flush all buffered data
    /// - signoz.status: Get plugin status
    /// </summary>
    public sealed class SigNozPlugin : FeaturePluginBase, IMetricsProvider
    {
        private readonly SigNozConfiguration _config;
        private readonly OtlpClient _client;
        private readonly MetricBuffer _metricBuffer;
        private readonly LogBuffer _logBuffer;
        private readonly TraceBuffer _traceBuffer;
        private CancellationTokenSource? _cts;
        private Task? _exportTask;
        private readonly object _lock = new();
        private bool _isRunning;
        private DateTime _startTime;

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.telemetry.signoz";

        /// <inheritdoc/>
        public override string Name => "SigNoz Observability Plugin";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <inheritdoc/>
        public override PluginCategory Category => PluginCategory.MetricsProvider;

        /// <summary>
        /// Gets the plugin configuration.
        /// </summary>
        public SigNozConfiguration Configuration => _config;

        /// <summary>
        /// Gets whether the plugin is running.
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Initializes a new instance of the <see cref="SigNozPlugin"/> class.
        /// </summary>
        /// <param name="config">Optional configuration. Defaults will be used if null.</param>
        public SigNozPlugin(SigNozConfiguration? config = null)
        {
            _config = config ?? new SigNozConfiguration();
            _client = new OtlpClient(_config);
            _metricBuffer = new MetricBuffer(_config.MaxBatchSize);
            _logBuffer = new LogBuffer(_config.MaxBatchSize);
            _traceBuffer = new TraceBuffer(_config.MaxBatchSize);
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
            _exportTask = ExportLoopAsync(_cts.Token);

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

            _cts?.Cancel();

            if (_exportTask != null)
            {
                try
                {
                    await _exportTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
            }

            // Final flush
            await FlushAllAsync(CancellationToken.None);

            _cts?.Dispose();
            _cts = null;
            _exportTask = null;
        }

        #endregion

        #region IMetricsProvider Implementation

        /// <summary>
        /// Increments a counter metric.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        public void IncrementCounter(string metric)
        {
            IncrementCounter(metric, 1, null);
        }

        /// <summary>
        /// Increments a counter metric with attributes.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="amount">The amount to increment.</param>
        /// <param name="attributes">Optional attributes.</param>
        public void IncrementCounter(string metric, double amount = 1, Dictionary<string, object>? attributes = null)
        {
            _metricBuffer.AddCounter(metric, amount, attributes);
        }

        /// <summary>
        /// Records a gauge metric value.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="value">The value to record.</param>
        public void RecordMetric(string metric, double value)
        {
            RecordGauge(metric, value, null);
        }

        /// <summary>
        /// Records a gauge metric with attributes.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="value">The value to record.</param>
        /// <param name="attributes">Optional attributes.</param>
        public void RecordGauge(string metric, double value, Dictionary<string, object>? attributes = null)
        {
            _metricBuffer.AddGauge(metric, value, attributes);
        }

        /// <summary>
        /// Observes a histogram value.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <param name="value">The observed value.</param>
        /// <param name="attributes">Optional attributes.</param>
        public void ObserveHistogram(string metric, double value, Dictionary<string, object>? attributes = null)
        {
            _metricBuffer.AddHistogram(metric, value, attributes);
        }

        /// <summary>
        /// Tracks the duration of an operation.
        /// </summary>
        /// <param name="metric">The metric name.</param>
        /// <returns>A disposable that records the duration when disposed.</returns>
        public IDisposable TrackDuration(string metric)
        {
            return new DurationTracker(this, metric);
        }

        #endregion

        #region Logging

        /// <summary>
        /// Logs a message with the specified severity.
        /// </summary>
        /// <param name="message">The log message.</param>
        /// <param name="severity">The severity level (1-24, default 9=INFO).</param>
        /// <param name="attributes">Optional attributes.</param>
        public void Log(string message, int severity = 9, Dictionary<string, object>? attributes = null)
        {
            _logBuffer.AddLog(message, severity, attributes);
        }

        /// <summary>
        /// Logs a debug message.
        /// </summary>
        /// <param name="message">The log message.</param>
        /// <param name="attributes">Optional attributes.</param>
        public void LogDebug(string message, Dictionary<string, object>? attributes = null)
        {
            Log(message, 5, attributes); // DEBUG
        }

        /// <summary>
        /// Logs an info message.
        /// </summary>
        /// <param name="message">The log message.</param>
        /// <param name="attributes">Optional attributes.</param>
        public void LogInfo(string message, Dictionary<string, object>? attributes = null)
        {
            Log(message, 9, attributes); // INFO
        }

        /// <summary>
        /// Logs a warning message.
        /// </summary>
        /// <param name="message">The log message.</param>
        /// <param name="attributes">Optional attributes.</param>
        public void LogWarning(string message, Dictionary<string, object>? attributes = null)
        {
            Log(message, 13, attributes); // WARN
        }

        /// <summary>
        /// Logs an error message.
        /// </summary>
        /// <param name="message">The log message.</param>
        /// <param name="attributes">Optional attributes.</param>
        public void LogError(string message, Dictionary<string, object>? attributes = null)
        {
            Log(message, 17, attributes); // ERROR
        }

        #endregion

        #region Tracing

        /// <summary>
        /// Starts a new trace span.
        /// </summary>
        /// <param name="name">The span name.</param>
        /// <param name="attributes">Optional attributes.</param>
        /// <returns>A span context for tracking.</returns>
        public SpanContext StartSpan(string name, Dictionary<string, object>? attributes = null)
        {
            return _traceBuffer.StartSpan(name, attributes);
        }

        /// <summary>
        /// Ends a trace span.
        /// </summary>
        /// <param name="context">The span context.</param>
        /// <param name="status">The span status (1=OK, 2=ERROR).</param>
        /// <param name="statusMessage">Optional status message.</param>
        public void EndSpan(SpanContext context, int status = 1, string? statusMessage = null)
        {
            _traceBuffer.EndSpan(context, status, statusMessage);
        }

        #endregion

        #region Message Handling

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "signoz.metric":
                    HandleMetric(message);
                    break;
                case "signoz.log":
                    HandleLog(message);
                    break;
                case "signoz.trace.start":
                    HandleTraceStart(message);
                    break;
                case "signoz.trace.end":
                    HandleTraceEnd(message);
                    break;
                case "signoz.flush":
                    await HandleFlushAsync(message);
                    break;
                case "signoz.status":
                    HandleStatus(message);
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }

        private void HandleMetric(PluginMessage message)
        {
            var name = GetString(message.Payload, "name");
            if (string.IsNullOrEmpty(name))
            {
                message.Payload["error"] = "metric name required";
                return;
            }

            var type = GetString(message.Payload, "type") ?? "gauge";
            var value = GetDouble(message.Payload, "value") ?? 0;
            var attributes = GetAttributes(message.Payload);

            switch (type.ToLowerInvariant())
            {
                case "counter":
                    IncrementCounter(name, value, attributes);
                    break;
                case "gauge":
                    RecordGauge(name, value, attributes);
                    break;
                case "histogram":
                    ObserveHistogram(name, value, attributes);
                    break;
                default:
                    message.Payload["error"] = $"unknown metric type: {type}";
                    return;
            }

            message.Payload["success"] = true;
        }

        private void HandleLog(PluginMessage message)
        {
            var msg = GetString(message.Payload, "message");
            if (string.IsNullOrEmpty(msg))
            {
                message.Payload["error"] = "log message required";
                return;
            }

            var severity = (int)(GetDouble(message.Payload, "severity") ?? 9);
            var attributes = GetAttributes(message.Payload);

            Log(msg, severity, attributes);
            message.Payload["success"] = true;
        }

        private void HandleTraceStart(PluginMessage message)
        {
            var name = GetString(message.Payload, "name");
            if (string.IsNullOrEmpty(name))
            {
                message.Payload["error"] = "span name required";
                return;
            }

            var attributes = GetAttributes(message.Payload);
            var context = StartSpan(name, attributes);

            message.Payload["traceId"] = context.TraceId;
            message.Payload["spanId"] = context.SpanId;
            message.Payload["success"] = true;
        }

        private void HandleTraceEnd(PluginMessage message)
        {
            var traceId = GetString(message.Payload, "traceId");
            var spanId = GetString(message.Payload, "spanId");

            if (string.IsNullOrEmpty(traceId) || string.IsNullOrEmpty(spanId))
            {
                message.Payload["error"] = "traceId and spanId required";
                return;
            }

            var status = (int)(GetDouble(message.Payload, "status") ?? 1);
            var statusMessage = GetString(message.Payload, "statusMessage");

            var context = new SpanContext
            {
                TraceId = traceId,
                SpanId = spanId,
                StartTime = DateTime.UtcNow // Will be ignored
            };

            EndSpan(context, status, statusMessage);
            message.Payload["success"] = true;
        }

        private async Task HandleFlushAsync(PluginMessage message)
        {
            await FlushAllAsync(CancellationToken.None);
            message.Payload["success"] = true;
        }

        private void HandleStatus(PluginMessage message)
        {
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["isRunning"] = _isRunning,
                ["sigNozUrl"] = _config.SigNozUrl,
                ["serviceName"] = _config.ServiceName,
                ["exportedMetrics"] = _client.ExportedMetrics,
                ["exportedLogs"] = _client.ExportedLogs,
                ["exportedTraces"] = _client.ExportedTraces,
                ["exportErrors"] = _client.ExportErrors,
                ["bufferedMetrics"] = _metricBuffer.Count,
                ["bufferedLogs"] = _logBuffer.Count,
                ["bufferedTraces"] = _traceBuffer.Count,
                ["uptimeSeconds"] = (DateTime.UtcNow - _startTime).TotalSeconds
            };
        }

        #endregion

        #region Export Loop

        private async Task ExportLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_config.ExportInterval, ct);
                    await FlushAllAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[SigNoz] Error in export loop: {ex.Message}");
                }
            }
        }

        private async Task FlushAllAsync(CancellationToken ct)
        {
            var tasks = new List<Task>();

            if (_config.EnableMetrics)
            {
                tasks.Add(FlushMetricsAsync(ct));
            }

            if (_config.EnableLogs)
            {
                tasks.Add(FlushLogsAsync(ct));
            }

            if (_config.EnableTraces)
            {
                tasks.Add(FlushTracesAsync(ct));
            }

            await Task.WhenAll(tasks);
        }

        private async Task FlushMetricsAsync(CancellationToken ct)
        {
            var metrics = _metricBuffer.Flush();
            if (metrics.Count == 0)
            {
                return;
            }

            var request = new OtlpMetricsExportRequest
            {
                ResourceMetrics = new List<OtlpResourceMetrics>
                {
                    new OtlpResourceMetrics
                    {
                        Resource = _client.CreateResource(),
                        ScopeMetrics = new List<OtlpScopeMetrics>
                        {
                            new OtlpScopeMetrics
                            {
                                Scope = _client.CreateScope(),
                                Metrics = metrics
                            }
                        }
                    }
                }
            };

            await _client.ExportMetricsAsync(request, ct);
        }

        private async Task FlushLogsAsync(CancellationToken ct)
        {
            var logs = _logBuffer.Flush();
            if (logs.Count == 0)
            {
                return;
            }

            var request = new OtlpLogsExportRequest
            {
                ResourceLogs = new List<OtlpResourceLogs>
                {
                    new OtlpResourceLogs
                    {
                        Resource = _client.CreateResource(),
                        ScopeLogs = new List<OtlpScopeLogs>
                        {
                            new OtlpScopeLogs
                            {
                                Scope = _client.CreateScope(),
                                LogRecords = logs
                            }
                        }
                    }
                }
            };

            await _client.ExportLogsAsync(request, ct);
        }

        private async Task FlushTracesAsync(CancellationToken ct)
        {
            var spans = _traceBuffer.Flush();
            if (spans.Count == 0)
            {
                return;
            }

            var request = new OtlpTracesExportRequest
            {
                ResourceSpans = new List<OtlpResourceSpans>
                {
                    new OtlpResourceSpans
                    {
                        Resource = _client.CreateResource(),
                        ScopeSpans = new List<OtlpScopeSpans>
                        {
                            new OtlpScopeSpans
                            {
                                Scope = _client.CreateScope(),
                                Spans = spans
                            }
                        }
                    }
                }
            };

            await _client.ExportTracesAsync(request, ct);
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
            }
            return null;
        }

        private static Dictionary<string, object>? GetAttributes(Dictionary<string, object> payload)
        {
            if (!payload.TryGetValue("attributes", out var obj))
            {
                return null;
            }

            if (obj is Dictionary<string, object> dict)
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
                new() { Name = "signoz.metric", DisplayName = "Record Metric", Description = "Record a metric (counter/gauge/histogram)" },
                new() { Name = "signoz.log", DisplayName = "Send Log", Description = "Send a log message" },
                new() { Name = "signoz.trace.start", DisplayName = "Start Trace", Description = "Start a new trace span" },
                new() { Name = "signoz.trace.end", DisplayName = "End Trace", Description = "End a trace span" },
                new() { Name = "signoz.flush", DisplayName = "Flush Data", Description = "Force flush all buffered data" },
                new() { Name = "signoz.status", DisplayName = "Get Status", Description = "Get plugin status" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "Telemetry";
            metadata["SigNozUrl"] = _config.SigNozUrl;
            metadata["ServiceName"] = _config.ServiceName;
            metadata["SupportsMetrics"] = _config.EnableMetrics;
            metadata["SupportsLogs"] = _config.EnableLogs;
            metadata["SupportsTraces"] = _config.EnableTraces;
            metadata["Protocol"] = "OTLP/HTTP";
            metadata["MaxBatchSize"] = _config.MaxBatchSize;
            metadata["ExportInterval"] = _config.ExportInterval.TotalSeconds;
            return metadata;
        }

        #endregion

        #region Buffers

        private sealed class MetricBuffer
        {
            private readonly ConcurrentBag<OtlpMetric> _metrics = new();
            private readonly int _maxSize;

            public int Count => _metrics.Count;

            public MetricBuffer(int maxSize)
            {
                _maxSize = maxSize;
            }

            public void AddCounter(string name, double value, Dictionary<string, object>? attributes)
            {
                var metric = new OtlpMetric
                {
                    Name = name,
                    Sum = new OtlpSum
                    {
                        DataPoints = new List<OtlpNumberDataPoint>
                        {
                            new OtlpNumberDataPoint
                            {
                                TimeUnixNano = OtlpClient.ToUnixNano(DateTime.UtcNow),
                                AsDouble = value,
                                Attributes = OtlpClient.CreateAttributes(attributes)
                            }
                        }
                    }
                };

                _metrics.Add(metric);
            }

            public void AddGauge(string name, double value, Dictionary<string, object>? attributes)
            {
                var metric = new OtlpMetric
                {
                    Name = name,
                    Gauge = new OtlpGauge
                    {
                        DataPoints = new List<OtlpNumberDataPoint>
                        {
                            new OtlpNumberDataPoint
                            {
                                TimeUnixNano = OtlpClient.ToUnixNano(DateTime.UtcNow),
                                AsDouble = value,
                                Attributes = OtlpClient.CreateAttributes(attributes)
                            }
                        }
                    }
                };

                _metrics.Add(metric);
            }

            public void AddHistogram(string name, double value, Dictionary<string, object>? attributes)
            {
                // Simple histogram implementation (could be enhanced with actual bucketing)
                var metric = new OtlpMetric
                {
                    Name = name,
                    Histogram = new OtlpHistogram
                    {
                        DataPoints = new List<OtlpHistogramDataPoint>
                        {
                            new OtlpHistogramDataPoint
                            {
                                TimeUnixNano = OtlpClient.ToUnixNano(DateTime.UtcNow),
                                Count = 1,
                                Sum = value,
                                Min = value,
                                Max = value,
                                Attributes = OtlpClient.CreateAttributes(attributes),
                                ExplicitBounds = new List<double>(),
                                BucketCounts = new List<ulong> { 1 }
                            }
                        }
                    }
                };

                _metrics.Add(metric);
            }

            public List<OtlpMetric> Flush()
            {
                var result = new List<OtlpMetric>();
                while (_metrics.TryTake(out var metric) && result.Count < _maxSize)
                {
                    result.Add(metric);
                }
                return result;
            }
        }

        private sealed class LogBuffer
        {
            private readonly ConcurrentBag<OtlpLogRecord> _logs = new();
            private readonly int _maxSize;

            public int Count => _logs.Count;

            public LogBuffer(int maxSize)
            {
                _maxSize = maxSize;
            }

            public void AddLog(string message, int severity, Dictionary<string, object>? attributes)
            {
                var log = new OtlpLogRecord
                {
                    TimeUnixNano = OtlpClient.ToUnixNano(DateTime.UtcNow),
                    SeverityNumber = severity,
                    SeverityText = GetSeverityText(severity),
                    Body = new OtlpAnyValue { StringValue = message },
                    Attributes = OtlpClient.CreateAttributes(attributes)
                };

                _logs.Add(log);
            }

            public List<OtlpLogRecord> Flush()
            {
                var result = new List<OtlpLogRecord>();
                while (_logs.TryTake(out var log) && result.Count < _maxSize)
                {
                    result.Add(log);
                }
                return result;
            }

            private static string GetSeverityText(int severity)
            {
                return severity switch
                {
                    <= 4 => "TRACE",
                    <= 8 => "DEBUG",
                    <= 12 => "INFO",
                    <= 16 => "WARN",
                    <= 20 => "ERROR",
                    _ => "FATAL"
                };
            }
        }

        private sealed class TraceBuffer
        {
            private readonly ConcurrentDictionary<string, OtlpSpan> _activeSpans = new();
            private readonly ConcurrentBag<OtlpSpan> _completedSpans = new();
            private readonly int _maxSize;

            public int Count => _completedSpans.Count;

            public TraceBuffer(int maxSize)
            {
                _maxSize = maxSize;
            }

            public SpanContext StartSpan(string name, Dictionary<string, object>? attributes)
            {
                var traceId = GenerateId(16);
                var spanId = GenerateId(8);
                var startTime = DateTime.UtcNow;

                var span = new OtlpSpan
                {
                    TraceId = traceId,
                    SpanId = spanId,
                    Name = name,
                    StartTimeUnixNano = OtlpClient.ToUnixNano(startTime),
                    Attributes = OtlpClient.CreateAttributes(attributes)
                };

                _activeSpans[spanId] = span;

                return new SpanContext
                {
                    TraceId = traceId,
                    SpanId = spanId,
                    StartTime = startTime
                };
            }

            public void EndSpan(SpanContext context, int status, string? statusMessage)
            {
                if (_activeSpans.TryRemove(context.SpanId, out var span))
                {
                    span.EndTimeUnixNano = OtlpClient.ToUnixNano(DateTime.UtcNow);
                    span.Status = new OtlpStatus
                    {
                        Code = status,
                        Message = statusMessage
                    };

                    _completedSpans.Add(span);
                }
            }

            public List<OtlpSpan> Flush()
            {
                var result = new List<OtlpSpan>();
                while (_completedSpans.TryTake(out var span) && result.Count < _maxSize)
                {
                    result.Add(span);
                }
                return result;
            }

            private static string GenerateId(int byteCount)
            {
                var bytes = new byte[byteCount];
                Random.Shared.NextBytes(bytes);
                return Convert.ToHexString(bytes).ToLowerInvariant();
            }
        }

        #endregion

        #region Duration Tracker

        private sealed class DurationTracker : IDisposable
        {
            private readonly SigNozPlugin _plugin;
            private readonly string _metric;
            private readonly Stopwatch _stopwatch;
            private bool _disposed;

            public DurationTracker(SigNozPlugin plugin, string metric)
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
    }

    /// <summary>
    /// Represents a trace span context.
    /// </summary>
    public sealed class SpanContext
    {
        /// <summary>
        /// Gets or sets the trace ID.
        /// </summary>
        public string TraceId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the span ID.
        /// </summary>
        public string SpanId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the span start time.
        /// </summary>
        public DateTime StartTime { get; set; }
    }
}
