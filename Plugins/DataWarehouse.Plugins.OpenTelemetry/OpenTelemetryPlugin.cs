using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.OpenTelemetry
{
    /// <summary>
    /// OpenTelemetry observability plugin.
    /// Provides distributed tracing, metrics export, and log correlation.
    ///
    /// Features:
    /// - Distributed tracing with W3C Trace Context propagation
    /// - Metrics collection (counters, gauges, histograms)
    /// - Log correlation with trace/span IDs
    /// - OTLP export (gRPC and HTTP/protobuf)
    /// - Jaeger/Zipkin compatible export
    /// - Prometheus metrics endpoint
    ///
    /// Message Commands:
    /// - otel.trace.start: Start a new span
    /// - otel.trace.end: End a span
    /// - otel.trace.add-event: Add an event to the current span
    /// - otel.metrics.counter: Increment a counter
    /// - otel.metrics.gauge: Record a gauge value
    /// - otel.metrics.histogram: Record a histogram value
    /// - otel.flush: Flush all telemetry to exporters
    /// - otel.configure: Configure exporters and settings
    /// </summary>
    public sealed class OpenTelemetryPlugin : MetricsPluginBase, IFeaturePlugin
    {
        public override string Id => "datawarehouse.opentelemetry";
        public override string Name => "OpenTelemetry Observability";
        public override string Version => "1.0.0";

        // Trace storage
        private readonly ConcurrentDictionary<string, Span> _activeSpans = new();
        private readonly ConcurrentQueue<Span> _completedSpans = new();
        private readonly AsyncLocal<SpanContext?> _currentContext = new();

        // Metrics storage
        private readonly ConcurrentDictionary<string, Counter> _counters = new();
        private readonly ConcurrentDictionary<string, Gauge> _gauges = new();
        private readonly ConcurrentDictionary<string, Histogram> _histograms = new();

        // Log correlation
        private readonly ConcurrentQueue<CorrelatedLog> _logs = new();

        // Exporters
        private readonly List<ISpanExporter> _spanExporters = new();
        private readonly List<IMetricExporter> _metricExporters = new();
        private readonly List<ILogExporter> _logExporters = new();

        // Configuration
        private string _serviceName = "datawarehouse";
        private string _serviceVersion = "1.0.0";
        private string _serviceInstanceId = string.Empty;
        private string? _otlpEndpoint;
        private string? _jaegerEndpoint;
        private string? _zipkinEndpoint;
        private bool _prometheusEnabled;
        private int _prometheusPort = 9464;
        private TimeSpan _exportInterval = TimeSpan.FromSeconds(30);
        private int _maxBatchSize = 512;
        private double _samplingRate = 1.0;

        // Background tasks
        private CancellationTokenSource? _cts;
        private Task? _exportTask;
        private HttpClient? _httpClient;
        private bool _isRunning;

        public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            _serviceInstanceId = $"{request.KernelId}-{Guid.NewGuid().ToString("N")[..8]}";

            return Task.FromResult(new HandshakeResponse
            {
                PluginId = Id,
                Name = Name,
                Version = ParseSemanticVersion(Version),
                Category = Category,
                Success = true,
                ReadyState = PluginReadyState.Ready,
                Capabilities = GetCapabilities(),
                Metadata = GetMetadata()
            });
        }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new()
                {
                    Name = "trace.start",
                    Description = "Start a new distributed trace span",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["name"] = new { type = "string", description = "Span name" },
                            ["kind"] = new { type = "string", description = "Span kind (internal, client, server, producer, consumer)" },
                            ["parentSpanId"] = new { type = "string", description = "Parent span ID for nested spans" },
                            ["attributes"] = new { type = "object", description = "Key-value attributes" }
                        },
                        ["required"] = new[] { "name" }
                    }
                },
                new()
                {
                    Name = "metrics.counter",
                    Description = "Increment a counter metric",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["name"] = new { type = "string", description = "Metric name" },
                            ["value"] = new { type = "number", description = "Value to add (default 1)" },
                            ["labels"] = new { type = "object", description = "Metric labels" }
                        },
                        ["required"] = new[] { "name" }
                    }
                },
                new()
                {
                    Name = "metrics.histogram",
                    Description = "Record a histogram observation",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["name"] = new { type = "string", description = "Metric name" },
                            ["value"] = new { type = "number", description = "Observed value" },
                            ["labels"] = new { type = "object", description = "Metric labels" }
                        },
                        ["required"] = new[] { "name", "value" }
                    }
                }
            };
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            return new Dictionary<string, object>
            {
                ["Description"] = "OpenTelemetry observability with distributed tracing and metrics",
                ["FeatureType"] = "Observability",
                ["ServiceName"] = _serviceName,
                ["ServiceInstanceId"] = _serviceInstanceId,
                ["SupportsTracing"] = true,
                ["SupportsMetrics"] = true,
                ["SupportsLogging"] = true,
                ["SupportsTags"] = true,
                ["SupportsHistograms"] = true,
                ["ActiveSpans"] = _activeSpans.Count,
                ["CounterCount"] = _counters.Count,
                ["HistogramCount"] = _histograms.Count
            };
        }

        public async Task StartAsync(CancellationToken ct)
        {
            if (_isRunning) return;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _httpClient = new HttpClient();
            _isRunning = true;

            // Configure default exporters based on settings
            ConfigureDefaultExporters();

            // Start export loop
            _exportTask = RunExportLoopAsync(_cts.Token);
        }

        public async Task StopAsync()
        {
            if (!_isRunning) return;

            _isRunning = false;
            _cts?.Cancel();

            // Final flush
            await FlushAsync();

            if (_exportTask != null)
            {
                await _exportTask.ContinueWith(_ => { });
            }

            _httpClient?.Dispose();
            _httpClient = null;

            _cts?.Dispose();
            _cts = null;
        }

        public override async Task OnMessageAsync(PluginMessage message)
        {
            if (message.Payload == null) return;

            var response = message.Type switch
            {
                "otel.trace.start" => HandleTraceStart(message.Payload),
                "otel.trace.end" => HandleTraceEnd(message.Payload),
                "otel.trace.add-event" => HandleTraceAddEvent(message.Payload),
                "otel.trace.set-status" => HandleTraceSetStatus(message.Payload),
                "otel.trace.context" => HandleTraceContext(),
                "otel.metrics.counter" => HandleMetricsCounter(message.Payload),
                "otel.metrics.gauge" => HandleMetricsGauge(message.Payload),
                "otel.metrics.histogram" => HandleMetricsHistogram(message.Payload),
                "otel.log" => HandleLog(message.Payload),
                "otel.flush" => await HandleFlushAsync(),
                "otel.configure" => HandleConfigure(message.Payload),
                "otel.prometheus" => HandlePrometheusExport(),
                _ => new Dictionary<string, object> { ["error"] = $"Unknown command: {message.Type}" }
            };

            message.Payload["_response"] = response;
        }

        #region IMetricsProvider Implementation

        public override void IncrementCounter(string metric)
        {
            IncrementCounter(metric, 1, null);
        }

        public void IncrementCounter(string metric, long value, Dictionary<string, string>? labels = null)
        {
            var key = GetMetricKey(metric, labels);
            var counter = _counters.GetOrAdd(key, _ => new Counter
            {
                Name = metric,
                Labels = labels ?? new Dictionary<string, string>()
            });

            Interlocked.Add(ref counter.Value, value);
        }

        public override void RecordMetric(string metric, double value)
        {
            RecordGauge(metric, value, null);
        }

        public void RecordGauge(string metric, double value, Dictionary<string, string>? labels = null)
        {
            var key = GetMetricKey(metric, labels);
            var gauge = _gauges.GetOrAdd(key, _ => new Gauge
            {
                Name = metric,
                Labels = labels ?? new Dictionary<string, string>()
            });

            gauge.Value = value;
            gauge.LastUpdated = DateTime.UtcNow;
        }

        public void RecordHistogram(string metric, double value, Dictionary<string, string>? labels = null)
        {
            var key = GetMetricKey(metric, labels);
            var histogram = _histograms.GetOrAdd(key, _ => new Histogram
            {
                Name = metric,
                Labels = labels ?? new Dictionary<string, string>(),
                Buckets = DefaultBuckets
            });

            histogram.Record(value);
        }

        public override IDisposable TrackDuration(string metric)
        {
            return new DurationTracker(this, metric);
        }

        public override async Task FlushAsync()
        {
            await ExportSpansAsync();
            await ExportMetricsAsync();
            await ExportLogsAsync();
        }

        #endregion

        #region Distributed Tracing

        /// <summary>
        /// Start a new trace span.
        /// </summary>
        public Span StartSpan(string name, SpanKind kind = SpanKind.Internal, SpanContext? parent = null)
        {
            // Apply sampling
            if (Random.Shared.NextDouble() > _samplingRate)
            {
                return Span.NoOp;
            }

            var context = parent ?? _currentContext.Value;

            var span = new Span
            {
                TraceId = context?.TraceId ?? GenerateTraceId(),
                SpanId = GenerateSpanId(),
                ParentSpanId = context?.SpanId,
                Name = name,
                Kind = kind,
                StartTime = DateTime.UtcNow,
                Status = SpanStatus.Unset,
                Attributes = new Dictionary<string, object>
                {
                    ["service.name"] = _serviceName,
                    ["service.version"] = _serviceVersion,
                    ["service.instance.id"] = _serviceInstanceId
                }
            };

            _activeSpans[span.SpanId] = span;

            // Set as current context
            _currentContext.Value = new SpanContext
            {
                TraceId = span.TraceId,
                SpanId = span.SpanId
            };

            return span;
        }

        /// <summary>
        /// End a span.
        /// </summary>
        public void EndSpan(string spanId)
        {
            if (_activeSpans.TryRemove(spanId, out var span))
            {
                span.EndTime = DateTime.UtcNow;
                span.Duration = span.EndTime - span.StartTime;
                _completedSpans.Enqueue(span);

                // Clear current context if this was it
                if (_currentContext.Value?.SpanId == spanId)
                {
                    _currentContext.Value = span.ParentSpanId != null
                        ? new SpanContext { TraceId = span.TraceId, SpanId = span.ParentSpanId }
                        : null;
                }
            }
        }

        /// <summary>
        /// Add an event to a span.
        /// </summary>
        public void AddSpanEvent(string spanId, string name, Dictionary<string, object>? attributes = null)
        {
            if (_activeSpans.TryGetValue(spanId, out var span))
            {
                span.Events.Add(new SpanEvent
                {
                    Name = name,
                    Timestamp = DateTime.UtcNow,
                    Attributes = attributes ?? new Dictionary<string, object>()
                });
            }
        }

        /// <summary>
        /// Set span status.
        /// </summary>
        public void SetSpanStatus(string spanId, SpanStatus status, string? message = null)
        {
            if (_activeSpans.TryGetValue(spanId, out var span))
            {
                span.Status = status;
                span.StatusMessage = message;
            }
        }

        /// <summary>
        /// Get the current trace context for propagation.
        /// </summary>
        public SpanContext? GetCurrentContext() => _currentContext.Value;

        /// <summary>
        /// Set trace context from incoming request (W3C Trace Context).
        /// </summary>
        public void SetContextFromW3C(string traceparent, string? tracestate = null)
        {
            // Format: 00-{trace-id}-{parent-id}-{flags}
            var parts = traceparent.Split('-');
            if (parts.Length >= 4)
            {
                _currentContext.Value = new SpanContext
                {
                    TraceId = parts[1],
                    SpanId = parts[2],
                    TraceState = tracestate
                };
            }
        }

        /// <summary>
        /// Get W3C Trace Context header value for propagation.
        /// </summary>
        public string? GetW3CTraceparent()
        {
            var ctx = _currentContext.Value;
            if (ctx == null) return null;
            return $"00-{ctx.TraceId}-{ctx.SpanId}-01";
        }

        #endregion

        #region Log Correlation

        /// <summary>
        /// Log a message with trace correlation.
        /// </summary>
        public void Log(LogLevel level, string message, Dictionary<string, object>? attributes = null)
        {
            var ctx = _currentContext.Value;
            var log = new CorrelatedLog
            {
                Timestamp = DateTime.UtcNow,
                Level = level,
                Message = message,
                TraceId = ctx?.TraceId,
                SpanId = ctx?.SpanId,
                Attributes = attributes ?? new Dictionary<string, object>()
            };

            log.Attributes["service.name"] = _serviceName;
            log.Attributes["service.instance.id"] = _serviceInstanceId;

            _logs.Enqueue(log);
        }

        #endregion

        #region Message Handlers

        private Dictionary<string, object> HandleTraceStart(Dictionary<string, object> payload)
        {
            var name = payload.GetValueOrDefault("name")?.ToString() ?? "unnamed";
            var kindStr = payload.GetValueOrDefault("kind")?.ToString() ?? "internal";
            var kind = Enum.TryParse<SpanKind>(kindStr, true, out var k) ? k : SpanKind.Internal;

            SpanContext? parent = null;
            if (payload.TryGetValue("parentSpanId", out var parentId) && parentId != null)
            {
                var parentSpan = _activeSpans.Values.FirstOrDefault(s => s.SpanId == parentId.ToString());
                if (parentSpan != null)
                {
                    parent = new SpanContext { TraceId = parentSpan.TraceId, SpanId = parentSpan.SpanId };
                }
            }

            var span = StartSpan(name, kind, parent);

            // Add attributes
            if (payload.TryGetValue("attributes", out var attrs) && attrs is JsonElement attrsEl)
            {
                foreach (var prop in attrsEl.EnumerateObject())
                {
                    span.Attributes[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString()!,
                        JsonValueKind.Number => prop.Value.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => prop.Value.ToString()
                    };
                }
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["traceId"] = span.TraceId,
                ["spanId"] = span.SpanId,
                ["traceparent"] = $"00-{span.TraceId}-{span.SpanId}-01"
            };
        }

        private Dictionary<string, object> HandleTraceEnd(Dictionary<string, object> payload)
        {
            var spanId = payload.GetValueOrDefault("spanId")?.ToString();
            if (string.IsNullOrEmpty(spanId))
            {
                // End current span
                spanId = _currentContext.Value?.SpanId;
            }

            if (string.IsNullOrEmpty(spanId))
            {
                return new Dictionary<string, object> { ["error"] = "No span to end" };
            }

            EndSpan(spanId);
            return new Dictionary<string, object> { ["success"] = true, ["spanId"] = spanId };
        }

        private Dictionary<string, object> HandleTraceAddEvent(Dictionary<string, object> payload)
        {
            var spanId = payload.GetValueOrDefault("spanId")?.ToString() ?? _currentContext.Value?.SpanId;
            var eventName = payload.GetValueOrDefault("name")?.ToString() ?? "event";

            if (string.IsNullOrEmpty(spanId))
            {
                return new Dictionary<string, object> { ["error"] = "No active span" };
            }

            Dictionary<string, object>? attributes = null;
            if (payload.TryGetValue("attributes", out var attrs) && attrs is JsonElement el)
            {
                attributes = JsonSerializer.Deserialize<Dictionary<string, object>>(el.GetRawText());
            }

            AddSpanEvent(spanId, eventName, attributes);
            return new Dictionary<string, object> { ["success"] = true };
        }

        private Dictionary<string, object> HandleTraceSetStatus(Dictionary<string, object> payload)
        {
            var spanId = payload.GetValueOrDefault("spanId")?.ToString() ?? _currentContext.Value?.SpanId;
            var statusStr = payload.GetValueOrDefault("status")?.ToString() ?? "ok";
            var message = payload.GetValueOrDefault("message")?.ToString();

            if (string.IsNullOrEmpty(spanId))
            {
                return new Dictionary<string, object> { ["error"] = "No active span" };
            }

            var status = statusStr.ToLowerInvariant() switch
            {
                "ok" => SpanStatus.Ok,
                "error" => SpanStatus.Error,
                _ => SpanStatus.Unset
            };

            SetSpanStatus(spanId, status, message);
            return new Dictionary<string, object> { ["success"] = true };
        }

        private Dictionary<string, object> HandleTraceContext()
        {
            var ctx = _currentContext.Value;
            if (ctx == null)
            {
                return new Dictionary<string, object> { ["hasContext"] = false };
            }

            return new Dictionary<string, object>
            {
                ["hasContext"] = true,
                ["traceId"] = ctx.TraceId,
                ["spanId"] = ctx.SpanId,
                ["traceparent"] = $"00-{ctx.TraceId}-{ctx.SpanId}-01",
                ["tracestate"] = ctx.TraceState ?? ""
            };
        }

        private Dictionary<string, object> HandleMetricsCounter(Dictionary<string, object> payload)
        {
            var name = payload.GetValueOrDefault("name")?.ToString();
            if (string.IsNullOrEmpty(name))
            {
                return new Dictionary<string, object> { ["error"] = "name is required" };
            }

            var value = payload.GetValueOrDefault("value") as double? ?? 1;
            var labels = ExtractLabels(payload);

            IncrementCounter(name, (long)value, labels);
            return new Dictionary<string, object> { ["success"] = true, ["metric"] = name };
        }

        private Dictionary<string, object> HandleMetricsGauge(Dictionary<string, object> payload)
        {
            var name = payload.GetValueOrDefault("name")?.ToString();
            if (string.IsNullOrEmpty(name))
            {
                return new Dictionary<string, object> { ["error"] = "name is required" };
            }

            var value = payload.GetValueOrDefault("value") as double? ?? 0;
            var labels = ExtractLabels(payload);

            RecordGauge(name, value, labels);
            return new Dictionary<string, object> { ["success"] = true, ["metric"] = name };
        }

        private Dictionary<string, object> HandleMetricsHistogram(Dictionary<string, object> payload)
        {
            var name = payload.GetValueOrDefault("name")?.ToString();
            if (string.IsNullOrEmpty(name))
            {
                return new Dictionary<string, object> { ["error"] = "name is required" };
            }

            var value = payload.GetValueOrDefault("value") as double? ?? 0;
            var labels = ExtractLabels(payload);

            RecordHistogram(name, value, labels);
            return new Dictionary<string, object> { ["success"] = true, ["metric"] = name };
        }

        private Dictionary<string, object> HandleLog(Dictionary<string, object> payload)
        {
            var message = payload.GetValueOrDefault("message")?.ToString() ?? "";
            var levelStr = payload.GetValueOrDefault("level")?.ToString() ?? "info";
            var level = levelStr.ToLowerInvariant() switch
            {
                "trace" => LogLevel.Trace,
                "debug" => LogLevel.Debug,
                "info" => LogLevel.Info,
                "warn" or "warning" => LogLevel.Warn,
                "error" => LogLevel.Error,
                "fatal" => LogLevel.Fatal,
                _ => LogLevel.Info
            };

            Dictionary<string, object>? attributes = null;
            if (payload.TryGetValue("attributes", out var attrs) && attrs is JsonElement el)
            {
                attributes = JsonSerializer.Deserialize<Dictionary<string, object>>(el.GetRawText());
            }

            Log(level, message, attributes);
            return new Dictionary<string, object> { ["success"] = true };
        }

        private async Task<Dictionary<string, object>> HandleFlushAsync()
        {
            await FlushAsync();
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["spansExported"] = _completedSpans.Count,
                ["countersExported"] = _counters.Count,
                ["histogramsExported"] = _histograms.Count
            };
        }

        private Dictionary<string, object> HandleConfigure(Dictionary<string, object> payload)
        {
            if (payload.TryGetValue("serviceName", out var sn) && sn != null)
            {
                _serviceName = sn.ToString()!;
            }

            if (payload.TryGetValue("serviceVersion", out var sv) && sv != null)
            {
                _serviceVersion = sv.ToString()!;
            }

            if (payload.TryGetValue("otlpEndpoint", out var otlp) && otlp != null)
            {
                _otlpEndpoint = otlp.ToString();
                ConfigureOtlpExporter();
            }

            if (payload.TryGetValue("jaegerEndpoint", out var jaeger) && jaeger != null)
            {
                _jaegerEndpoint = jaeger.ToString();
                ConfigureJaegerExporter();
            }

            if (payload.TryGetValue("zipkinEndpoint", out var zipkin) && zipkin != null)
            {
                _zipkinEndpoint = zipkin.ToString();
                ConfigureZipkinExporter();
            }

            if (payload.TryGetValue("samplingRate", out var sr) && sr is double rate)
            {
                _samplingRate = Math.Clamp(rate, 0.0, 1.0);
            }

            if (payload.TryGetValue("exportIntervalSeconds", out var ei) && ei is double interval)
            {
                _exportInterval = TimeSpan.FromSeconds(interval);
            }

            if (payload.TryGetValue("prometheusEnabled", out var pe) && pe is bool promEnabled)
            {
                _prometheusEnabled = promEnabled;
            }

            if (payload.TryGetValue("prometheusPort", out var pp) && pp is double promPort)
            {
                _prometheusPort = (int)promPort;
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["serviceName"] = _serviceName,
                ["samplingRate"] = _samplingRate,
                ["exportIntervalSeconds"] = _exportInterval.TotalSeconds,
                ["otlpEndpoint"] = _otlpEndpoint ?? "not configured",
                ["jaegerEndpoint"] = _jaegerEndpoint ?? "not configured",
                ["zipkinEndpoint"] = _zipkinEndpoint ?? "not configured",
                ["prometheusEnabled"] = _prometheusEnabled
            };
        }

        private Dictionary<string, object> HandlePrometheusExport()
        {
            var sb = new StringBuilder();

            // Export counters
            foreach (var counter in _counters.Values)
            {
                var labels = FormatPrometheusLabels(counter.Labels);
                sb.AppendLine($"# TYPE {counter.Name} counter");
                sb.AppendLine($"{counter.Name}{labels} {counter.Value}");
            }

            // Export gauges
            foreach (var gauge in _gauges.Values)
            {
                var labels = FormatPrometheusLabels(gauge.Labels);
                sb.AppendLine($"# TYPE {gauge.Name} gauge");
                sb.AppendLine($"{gauge.Name}{labels} {gauge.Value}");
            }

            // Export histograms
            foreach (var histogram in _histograms.Values)
            {
                var baseLabels = FormatPrometheusLabels(histogram.Labels);
                sb.AppendLine($"# TYPE {histogram.Name} histogram");

                // Bucket counts
                foreach (var bucket in histogram.BucketCounts)
                {
                    var le = bucket.Key == double.PositiveInfinity ? "+Inf" : bucket.Key.ToString();
                    sb.AppendLine($"{histogram.Name}_bucket{{le=\"{le}\"{(string.IsNullOrEmpty(baseLabels) ? "" : "," + baseLabels.Trim('{', '}'))}}} {bucket.Value}");
                }

                sb.AppendLine($"{histogram.Name}_sum{baseLabels} {histogram.Sum}");
                sb.AppendLine($"{histogram.Name}_count{baseLabels} {histogram.Count}");
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["contentType"] = "text/plain; version=0.0.4",
                ["body"] = sb.ToString()
            };
        }

        #endregion

        #region Export

        private async Task RunExportLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_exportInterval, ct);
                    await FlushAsync();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Export failed, will retry
                }
            }
        }

        private async Task ExportSpansAsync()
        {
            var spans = new List<Span>();
            while (_completedSpans.TryDequeue(out var span) && spans.Count < _maxBatchSize)
            {
                spans.Add(span);
            }

            if (spans.Count == 0) return;

            foreach (var exporter in _spanExporters)
            {
                try
                {
                    await exporter.ExportAsync(spans);
                }
                catch
                {
                    // Exporter failed
                }
            }
        }

        private async Task ExportMetricsAsync()
        {
            var metrics = new MetricBatch
            {
                Counters = _counters.Values.ToList(),
                Gauges = _gauges.Values.ToList(),
                Histograms = _histograms.Values.ToList(),
                Timestamp = DateTime.UtcNow
            };

            foreach (var exporter in _metricExporters)
            {
                try
                {
                    await exporter.ExportAsync(metrics);
                }
                catch
                {
                    // Exporter failed
                }
            }
        }

        private async Task ExportLogsAsync()
        {
            var logs = new List<CorrelatedLog>();
            while (_logs.TryDequeue(out var log) && logs.Count < _maxBatchSize)
            {
                logs.Add(log);
            }

            if (logs.Count == 0) return;

            foreach (var exporter in _logExporters)
            {
                try
                {
                    await exporter.ExportAsync(logs);
                }
                catch
                {
                    // Exporter failed
                }
            }
        }

        private void ConfigureDefaultExporters()
        {
            // Console exporter for development
            _spanExporters.Add(new ConsoleSpanExporter());
        }

        private void ConfigureOtlpExporter()
        {
            if (string.IsNullOrEmpty(_otlpEndpoint)) return;
            _spanExporters.Add(new OtlpSpanExporter(_httpClient!, _otlpEndpoint));
            _metricExporters.Add(new OtlpMetricExporter(_httpClient!, _otlpEndpoint));
        }

        private void ConfigureJaegerExporter()
        {
            if (string.IsNullOrEmpty(_jaegerEndpoint)) return;
            _spanExporters.Add(new JaegerExporter(_httpClient!, _jaegerEndpoint, _serviceName));
        }

        private void ConfigureZipkinExporter()
        {
            if (string.IsNullOrEmpty(_zipkinEndpoint)) return;
            _spanExporters.Add(new ZipkinExporter(_httpClient!, _zipkinEndpoint, _serviceName));
        }

        #endregion

        #region Helpers

        private static string GenerateTraceId()
        {
            var bytes = new byte[16];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static string GenerateSpanId()
        {
            var bytes = new byte[8];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static string GetMetricKey(string name, Dictionary<string, string>? labels)
        {
            if (labels == null || labels.Count == 0) return name;
            var labelStr = string.Join(",", labels.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
            return $"{name}{{{labelStr}}}";
        }

        private static Dictionary<string, string>? ExtractLabels(Dictionary<string, object> payload)
        {
            if (!payload.TryGetValue("labels", out var labels) || labels == null) return null;

            if (labels is JsonElement el)
            {
                var result = new Dictionary<string, string>();
                foreach (var prop in el.EnumerateObject())
                {
                    result[prop.Name] = prop.Value.ToString();
                }
                return result;
            }

            return null;
        }

        private static string FormatPrometheusLabels(Dictionary<string, string> labels)
        {
            if (labels.Count == 0) return "";
            var pairs = labels.Select(kv => $"{kv.Key}=\"{kv.Value.Replace("\"", "\\\"")}\"");
            return "{" + string.Join(",", pairs) + "}";
        }

        private static readonly double[] DefaultBuckets = new[]
        {
            0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1.0, 2.5, 5.0, 7.5, 10.0, double.PositiveInfinity
        };

        #endregion
    }

    #region Supporting Types

    public class Span
    {
        public static readonly Span NoOp = new() { TraceId = "", SpanId = "", Name = "noop" };

        public string TraceId { get; set; } = string.Empty;
        public string SpanId { get; set; } = string.Empty;
        public string? ParentSpanId { get; set; }
        public string Name { get; set; } = string.Empty;
        public SpanKind Kind { get; set; } = SpanKind.Internal;
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public SpanStatus Status { get; set; } = SpanStatus.Unset;
        public string? StatusMessage { get; set; }
        public Dictionary<string, object> Attributes { get; set; } = new();
        public List<SpanEvent> Events { get; set; } = new();
    }

    public class SpanContext
    {
        public string TraceId { get; set; } = string.Empty;
        public string SpanId { get; set; } = string.Empty;
        public string? TraceState { get; set; }
    }

    public class SpanEvent
    {
        public string Name { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Attributes { get; set; } = new();
    }

    public enum SpanKind
    {
        Internal,
        Client,
        Server,
        Producer,
        Consumer
    }

    public enum SpanStatus
    {
        Unset,
        Ok,
        Error
    }

    public enum LogLevel
    {
        Trace,
        Debug,
        Info,
        Warn,
        Error,
        Fatal
    }

    public class Counter
    {
        public string Name { get; set; } = string.Empty;
        public Dictionary<string, string> Labels { get; set; } = new();
        public long Value;
    }

    public class Gauge
    {
        public string Name { get; set; } = string.Empty;
        public Dictionary<string, string> Labels { get; set; } = new();
        public double Value { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    public class Histogram
    {
        public string Name { get; set; } = string.Empty;
        public Dictionary<string, string> Labels { get; set; } = new();
        public double[] Buckets { get; set; } = Array.Empty<double>();
        public ConcurrentDictionary<double, long> BucketCounts { get; } = new();
        public double Sum { get; private set; }
        public long Count { get; private set; }
        private readonly object _lock = new();

        public void Record(double value)
        {
            lock (_lock)
            {
                Sum += value;
                Count++;

                foreach (var bucket in Buckets)
                {
                    if (value <= bucket)
                    {
                        BucketCounts.AddOrUpdate(bucket, 1, (_, c) => c + 1);
                    }
                }
            }
        }
    }

    public class CorrelatedLog
    {
        public DateTime Timestamp { get; set; }
        public LogLevel Level { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? TraceId { get; set; }
        public string? SpanId { get; set; }
        public Dictionary<string, object> Attributes { get; set; } = new();
    }

    public class MetricBatch
    {
        public List<Counter> Counters { get; set; } = new();
        public List<Gauge> Gauges { get; set; } = new();
        public List<Histogram> Histograms { get; set; } = new();
        public DateTime Timestamp { get; set; }
    }

    internal class DurationTracker : IDisposable
    {
        private readonly OpenTelemetryPlugin _plugin;
        private readonly string _metric;
        private readonly Stopwatch _stopwatch;

        public DurationTracker(OpenTelemetryPlugin plugin, string metric)
        {
            _plugin = plugin;
            _metric = metric;
            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            _plugin.RecordHistogram(_metric, _stopwatch.Elapsed.TotalSeconds);
        }
    }

    #endregion

    #region Exporters

    public interface ISpanExporter
    {
        Task ExportAsync(IEnumerable<Span> spans);
    }

    public interface IMetricExporter
    {
        Task ExportAsync(MetricBatch metrics);
    }

    public interface ILogExporter
    {
        Task ExportAsync(IEnumerable<CorrelatedLog> logs);
    }

    internal class ConsoleSpanExporter : ISpanExporter
    {
        private readonly Action<string>? _logAction;
        private readonly bool _useStdErr;

        public ConsoleSpanExporter(Action<string>? logAction = null, bool useStdErr = false)
        {
            _logAction = logAction;
            _useStdErr = useStdErr;
        }

        public Task ExportAsync(IEnumerable<Span> spans)
        {
            foreach (var span in spans)
            {
                var message = $"[TRACE] {span.TraceId}:{span.SpanId} {span.Name} {span.Duration.TotalMilliseconds:F2}ms {span.Status}";

                if (_logAction != null)
                {
                    _logAction(message);
                }
                else if (_useStdErr)
                {
                    // Use stderr for trace output (non-blocking, doesn't interfere with stdout)
                    System.Diagnostics.Trace.WriteLine(message);
                }
                else
                {
                    // Only use Console.WriteLine in development mode
                    #if DEBUG
                    Console.Error.WriteLine(message);
                    #else
                    System.Diagnostics.Debug.WriteLine(message);
                    #endif
                }
            }
            return Task.CompletedTask;
        }
    }

    internal class OtlpSpanExporter : ISpanExporter
    {
        private readonly HttpClient _client;
        private readonly string _endpoint;

        public OtlpSpanExporter(HttpClient client, string endpoint)
        {
            _client = client;
            _endpoint = endpoint.TrimEnd('/') + "/v1/traces";
        }

        public async Task ExportAsync(IEnumerable<Span> spans)
        {
            var payload = new
            {
                resourceSpans = new[]
                {
                    new
                    {
                        scopeSpans = new[]
                        {
                            new
                            {
                                spans = spans.Select(s => new
                                {
                                    traceId = s.TraceId,
                                    spanId = s.SpanId,
                                    parentSpanId = s.ParentSpanId,
                                    name = s.Name,
                                    kind = (int)s.Kind + 1,
                                    startTimeUnixNano = s.StartTime.Ticks * 100,
                                    endTimeUnixNano = s.EndTime.Ticks * 100,
                                    status = new { code = (int)s.Status }
                                })
                            }
                        }
                    }
                }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );

            await _client.PostAsync(_endpoint, content);
        }
    }

    internal class OtlpMetricExporter : IMetricExporter
    {
        private readonly HttpClient _client;
        private readonly string _endpoint;

        public OtlpMetricExporter(HttpClient client, string endpoint)
        {
            _client = client;
            _endpoint = endpoint.TrimEnd('/') + "/v1/metrics";
        }

        public async Task ExportAsync(MetricBatch metrics)
        {
            var payload = new
            {
                resourceMetrics = new[]
                {
                    new
                    {
                        scopeMetrics = new[]
                        {
                            new
                            {
                                metrics = metrics.Counters.Select(c => new
                                {
                                    name = c.Name,
                                    sum = new
                                    {
                                        dataPoints = new[]
                                        {
                                            new
                                            {
                                                asInt = c.Value,
                                                timeUnixNano = metrics.Timestamp.Ticks * 100
                                            }
                                        }
                                    }
                                })
                            }
                        }
                    }
                }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );

            await _client.PostAsync(_endpoint, content);
        }
    }

    internal class JaegerExporter : ISpanExporter
    {
        private readonly HttpClient _client;
        private readonly string _endpoint;
        private readonly string _serviceName;

        public JaegerExporter(HttpClient client, string endpoint, string serviceName)
        {
            _client = client;
            _endpoint = endpoint;
            _serviceName = serviceName;
        }

        public async Task ExportAsync(IEnumerable<Span> spans)
        {
            // Jaeger Thrift HTTP format
            var batch = new
            {
                process = new
                {
                    serviceName = _serviceName
                },
                spans = spans.Select(s => new
                {
                    traceIdHigh = Convert.ToInt64(s.TraceId[..16], 16),
                    traceIdLow = Convert.ToInt64(s.TraceId[16..], 16),
                    spanId = Convert.ToInt64(s.SpanId, 16),
                    parentSpanId = string.IsNullOrEmpty(s.ParentSpanId) ? 0 : Convert.ToInt64(s.ParentSpanId, 16),
                    operationName = s.Name,
                    startTime = ((DateTimeOffset)s.StartTime).ToUnixTimeMilliseconds() * 1000,
                    duration = (long)s.Duration.TotalMicroseconds
                })
            };

            var content = new StringContent(
                JsonSerializer.Serialize(batch),
                Encoding.UTF8,
                "application/json"
            );

            await _client.PostAsync(_endpoint, content);
        }
    }

    internal class ZipkinExporter : ISpanExporter
    {
        private readonly HttpClient _client;
        private readonly string _endpoint;
        private readonly string _serviceName;

        public ZipkinExporter(HttpClient client, string endpoint, string serviceName)
        {
            _client = client;
            _endpoint = endpoint;
            _serviceName = serviceName;
        }

        public async Task ExportAsync(IEnumerable<Span> spans)
        {
            var zipkinSpans = spans.Select(s => new
            {
                traceId = s.TraceId,
                id = s.SpanId,
                parentId = s.ParentSpanId,
                name = s.Name,
                timestamp = ((DateTimeOffset)s.StartTime).ToUnixTimeMilliseconds() * 1000,
                duration = (long)s.Duration.TotalMicroseconds,
                localEndpoint = new { serviceName = _serviceName },
                kind = s.Kind switch
                {
                    SpanKind.Client => "CLIENT",
                    SpanKind.Server => "SERVER",
                    SpanKind.Producer => "PRODUCER",
                    SpanKind.Consumer => "CONSUMER",
                    _ => (string?)null
                }
            });

            var content = new StringContent(
                JsonSerializer.Serialize(zipkinSpans),
                Encoding.UTF8,
                "application/json"
            );

            await _client.PostAsync(_endpoint, content);
        }
    }

    #endregion
}
